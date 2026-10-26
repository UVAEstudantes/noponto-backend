import importlib.util,unittest,copy
from unittest.mock import patch
from pathlib import Path
spec=importlib.util.spec_from_file_location('window',Path(__file__).with_name('its_window.py'));w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)

def sample():
    return {'oom':False,'restarts':0,'running':True,'wire':{'regressions':0,'duplicates':0,'invalid':0},'cycles':[],'selection':[],
     'containers':{'auth_api':{'started':'same','running':True,'restarts':0,'health':None}},
     'resources':[{'Name':'noponto_api','MemPerc':'35%'}],'memory_kib':{'MemAvailable':2000000},
     'lines':{x:{'vehicles':20,'until60':15,'older180':0} for x in w.LINES}}

class Safety(unittest.TestCase):
    def test_interrupted_command_keeps_exit_and_safe_operation(self):
        import types
        result=types.SimpleNamespace(returncode=-15,stdout='',stderr='secret payload')
        with patch.object(w.subprocess,'run',return_value=result):
            with self.assertRaises(w.CommandFailure) as caught:
                w.run(['docker','logs','--since','private','noponto_api'])
        self.assertEqual({'type':'CommandFailure','reason':'command_interrupted','tool':'docker',
                          'operation':'logs','exit_code':-15},caught.exception.evidence())
        self.assertNotIn('secret',str(caught.exception))

    def test_hybrid_binary_proof_requires_both_healthy_contributions(self):
        good='2026-10-24T17:00:00Z BUS hybrid: primary_status=Sucesso, support_status=Sucesso, selected_its=100, selected_gtfs=200'
        self.assertTrue(w.hybrid_proof(good))
        for bad in ('',good.replace('selected_gtfs=200','selected_gtfs=0'),good.replace('support_status=Sucesso','support_status=Falha')):
            self.assertFalse(w.hybrid_proof(bad))
    def test_repeated_hybrid_support_failure_interrupts_window(self):
        x=sample();x['selection']=[{'stamp':str(i),'mode':'Hybrid','primary_status':'Sucesso','support_status':'Falha'} for i in range(3)]
        self.assertEqual('hybrid_support_failed',self.decide(x))
    def test_both_hybrid_sources_failed_interrupts_earlier(self):
        x=sample();x['selection']=[{'stamp':str(i),'mode':'Hybrid','primary_status':'Falha','support_status':'Falha'} for i in range(2)]
        self.assertEqual('both_sources_failed',self.decide(x))
    def decide(self,current,**kw):return w.decision(current,sample(),kw.get('seen',set()),0,0,kw.get('low'),kw.get('watch'))[0]
    def test_healthy(self):self.assertIsNone(self.decide(sample()))
    def test_wire_integrity(self):
        for key in ('duplicates','regressions','invalid'):
            with self.subTest(key=key):
                x=sample();x['wire'][key]=1;self.assertEqual('signalr_gps_integrity',self.decide(x))
    def test_slow_cycles_boundary(self):
        x=sample();x['cycles']=[{'stamp':str(i),'total_ms':24000} for i in range(3)];self.assertIsNone(self.decide(x))
        x['cycles']=[{'stamp':str(i),'total_ms':24001} for i in range(3)];self.assertEqual('slow_polling',self.decide(x))
        x['cycles']=[{'stamp':'x','total_ms':60001}];self.assertEqual('slow_polling',self.decide(x))
    def test_replayed_logs_not_recounted(self):
        x=sample();x['cycles']=[{'stamp':'same','total_ms':25000}]*6;self.assertIsNone(self.decide(x))
    def test_both_failed(self):
        x=sample();x['selection']=[{'stamp':str(i),'active_source':'GTFSRT_BUS','count':'0','status':'Falha'} for i in range(2)]
        self.assertEqual('both_sources_failed',self.decide(x))
    def test_memory_sustained(self):
        x=sample();x['memory_kib']['MemAvailable']=100
        with patch.object(w.time,'time',return_value=1061):self.assertEqual('memory_pressure',self.decide(x,low=1000))
    def test_other_service_changed(self):
        x=sample();x['containers']['auth_api']['restarts']=1;self.assertEqual('other_service_degradation',self.decide(x))
    def test_poor_freshness_sustained(self):
        x=sample();x['lines']['838']={'vehicles':8,'until60':0,'older180':8}
        self.assertIsNone(w.decision(x,sample(),set(),0,0,None)[0]);self.assertEqual('coverage_or_freshness_degradation',w.decision(x,sample(),set(),0,2,None)[0])
    def test_no_start_when_stop_or_writer_check_fails(self):
        for failed in ('stop','ps','writer'):
            with self.subTest(failed=failed):
                calls=[]
                def fake(args,**kw):
                    calls.append(args)
                    if (failed=='stop' and 'stop' in args) or (failed=='ps' and args[1]=='ps'):raise RuntimeError('fake')
                    return 'still-running' if failed=='writer' and args[1]=='ps' else ''
                with patch.object(w,'run',side_effect=fake):
                    with self.assertRaises(RuntimeError):w.switch('fake','gtfs.rollback.override.yml')
                self.assertFalse(any('up' in c for c in calls))
    def test_switch_order(self):
        with patch.object(w,'run',return_value='') as run:
            w.switch('fake','its.primary.override.yml')
            self.assertIn('stop',run.call_args_list[0].args[0]);self.assertEqual('ps',run.call_args_list[1].args[0][1]);self.assertIn('up',run.call_args_list[2].args[0])
    def test_finished_cron_has_no_side_effect(self):
        with patch.object(w,'load',return_value={'phase':'complete'}),patch.object(w,'run') as run:w.tick();run.assert_not_called()

    def test_watchdog_returns_gtfs_and_releases_lock_before_orphan_cleanup(self):
        from unittest.mock import Mock
        state={'phase':'its','deadline':0,'heartbeat':0}
        order=[];lock=Mock();lock.close.side_effect=lambda:order.append('lock_release')
        with patch.object(w,'load',return_value=state),patch.object(w.time,'time',return_value=100),\
             patch.object(w,'take_lock',return_value=lock),patch.object(w,'rollback',side_effect=lambda *_:order.append('rollback')),\
             patch.object(w,'stop_orphan_capture',side_effect=lambda *_:order.append('capture_stop')),\
             patch.object(w,'finalize_capture',side_effect=lambda *_:order.append('verify')):
            w.tick()
        self.assertEqual(['rollback','lock_release','capture_stop','verify'],order)

class HttpSafety(unittest.TestCase):
    def http(self, path, status, payload):
        import io,urllib.error,json
        return urllib.error.HTTPError('http://127.0.0.1:8080'+path,status,'ignored',{},io.BytesIO(json.dumps(payload).encode()))

    def test_real_http_failures_keep_path_and_status(self):
        from probe_http import ProbeError
        for status in (401,403,404,429,500,503):
            for path in ('/', '/veiculos/linha/898'):
                with self.subTest(status=status,path=path),patch.object(w.urllib.request,'urlopen',side_effect=self.http(path,status,{'mensagem':'other'})):
                    with self.assertRaises(ProbeError) as error:w.get(path)
                    self.assertEqual(path,error.exception.evidence()['path'])
                    self.assertEqual(status,error.exception.evidence()['http_status'])

    def test_wrong_line_empty_message_is_failure(self):
        from probe_http import ProbeError
        with patch.object(w.urllib.request,'urlopen',side_effect=self.http('/veiculos/linha/898',404,{'mensagem':'Nenhum veículo ativo para a linha 838.'})):
            with self.assertRaises(ProbeError):w.get('/veiculos/linha/898')

    def test_timeout_is_failure_with_path(self):
        from probe_http import ProbeError
        with patch.object(w.urllib.request,'urlopen',side_effect=TimeoutError()):
            with self.assertRaises(ProbeError) as error:w.get('/veiculos/linha/898')
        self.assertEqual({'type':'ProbeError','path':'/veiculos/linha/898','http_status':None,'reason':'timeout'},error.exception.evidence())

    def test_wrapped_timeout_keeps_timeout_classification(self):
        from probe_http import ProbeError
        import urllib.error
        with patch.object(w.urllib.request,'urlopen',side_effect=urllib.error.URLError(TimeoutError())):
            with self.assertRaises(ProbeError) as error:w.get('/veiculos/linha/898')
        self.assertEqual('timeout',error.exception.reason)

    def test_connection_failure_is_not_empty(self):
        from probe_http import ProbeError
        import urllib.error
        with patch.object(w.urllib.request,'urlopen',side_effect=urllib.error.URLError(ConnectionRefusedError())):
            with self.assertRaises(ProbeError) as error:w.get('/veiculos/linha/898')
        self.assertEqual('transport_failure',error.exception.reason)

    def test_empty_reference_still_triggers_sustained_rollback(self):
        x=sample();baseline=sample()
        x['lines']['898']={'vehicles':0,'until60':0,'older180':0}
        baseline['lines']['898']['until60']=0
        self.assertIsNone(w.decision(x,baseline,set(),0,0,None)[0])
        self.assertEqual('coverage_or_freshness_degradation',w.decision(x,baseline,set(),0,2,None)[0])

    def test_one_stale_line_cannot_hide_behind_other_healthy_lines(self):
        x=sample();baseline=sample()
        x['lines']['898']={'vehicles':7,'until60':0,'older180':7}
        baseline['lines']['898']={'vehicles':9,'until60':3,'older180':0}
        self.assertIsNone(w.decision(x,baseline,set(),0,0,None)[0])
        self.assertEqual('coverage_or_freshness_degradation',w.decision(x,baseline,set(),0,2,None)[0])

    def test_body_limit_invalid_json_and_contract_fail_closed(self):
        import io
        from probe_http import ProbeError
        for body,reason in ((b'x'*1000001,'body_limit'),(b'broken','invalid_json'),(b'{}','invalid_positions_contract')):
            response=io.BytesIO(body);response.code=200
            with self.subTest(reason=reason),patch.object(w.urllib.request,'urlopen',return_value=response):
                with self.assertRaises(ProbeError) as error:w.get('/veiculos/linha/898')
                self.assertEqual(reason,error.exception.reason)

    def test_successful_line_and_health_contracts(self):
        import io
        for path,body in (('/',b'healthy'),('/veiculos/linha/898',b'{"posicoes":[]}')):
            response=io.BytesIO(body);response.code=200
            with patch.object(w.urllib.request,'urlopen',return_value=response):self.assertEqual(body,w.get(path)[0])

    def test_expected_line_404_is_empty(self):
        import io,urllib.error
        error=urllib.error.HTTPError('http://127.0.0.1:8080/veiculos/linha/898',404,'Not Found',{},io.BytesIO(b'{"mensagem":"Nenhum ve\\u00edculo ativo para a linha 898."}'))
        with patch.object(w.urllib.request,'urlopen',side_effect=error):
            body,_=w.get('/veiculos/linha/898')
        self.assertEqual([],w.json.loads(body)['posicoes'])

class TraceProof(unittest.TestCase):
    def test_connection_state_is_separate_from_empty_data(self):
        for connected,connections,expected in ((False,0,'not_connected'),(True,1,'connected'),(False,1,'reconnecting')):
            with self.subTest(expected=expected),patch.dict(w.wire,connected=connected,connections=connections,lines={}):
                self.assertEqual(expected,w.wire_summary()['connection_state'])

    def test_freshness_uses_physical_time_not_recent_receipt(self):
        import datetime
        def point(epoch):return {'timestampGps':datetime.datetime.fromtimestamp(epoch,datetime.timezone.utc).isoformat(),'recebidoEmUtc':'just-now'}
        with patch.object(w.time,'time',return_value=1000),patch.object(w,'activation_epoch',900):
            old=w.fresh([point(819)]);new=w.fresh([point(980)])
        self.assertEqual(0,old['until180']);self.assertEqual(0,old['new_after_activation']);self.assertEqual(181,old['median'])
        self.assertEqual(1,new['until40']);self.assertEqual(1,new['new_after_activation'])

    def test_trace_override_is_only_additional_permitted_setting(self):
        import json
        image='ghcr.io/guilhermedesales/noponto-api@sha256:'+'a'*64
        env={'GpsSources__BusPrimarySource':'GTFSRT_BUS','GpsSources__BrtPrimarySource':'GTFSRT_BRT','GtfsRealtimeGps__BusEnabled':'true','GtfsRealtimeGps__CrosswalkValidated':'true'}
        api={'Config':{'Image':image,'Labels':{'com.docker.compose.project':'noponto'},'Env':[k+'='+v for k,v in env.items()]},'HostConfig':{'Memory':805306368,'NanoCpus':1000000000}}
        def render(args,**kw):
            result={'services':{'api':{'environment':dict(env)},'other':{'image':'preserve'}}}
            if any(x.endswith(('its.primary.override.yml','gtfs.rollback.override.yml')) for x in args):
                its=any('its.primary.override.yml' in x for x in args)
                result['services']['api']['environment'].update(GpsSources__BusPrimarySource='ITS_CURRENT' if its else 'GTFSRT_BUS',GpsSources__BusFallbackSource='GTFSRT_BUS' if its else '',GpsSources__BusSupportMode='Hybrid' if its else 'FullFallback',GpsPipelineTrace__Enabled='true' if its else 'false')
            return json.dumps(result)
        with patch.object(w,'inspect',return_value=[api]),patch.object(w,'run',side_effect=render):w.validate(image)
        def unsafe(args,**kw):
            value=json.loads(render(args,**kw))
            if any('its.primary.override.yml' in x for x in args):value['services']['other']['image']='changed'
            return json.dumps(value)
        with patch.object(w,'inspect',return_value=[api]),patch.object(w,'run',side_effect=unsafe):
            with self.assertRaises(AssertionError):w.validate(image)

    def test_http_failure_and_contract_failure_stay_distinct(self):
        from probe_http import ProbeError
        self.assertEqual('backend_http',w.failure_evidence(ProbeError('/',503,'http_failure'))['classification'])
        self.assertEqual('backend_transport',w.failure_evidence(ProbeError('/',None,'timeout'))['classification'])
        self.assertEqual('backend_contract',w.failure_evidence(ProbeError('/veiculos/linha/898',200,'invalid_positions_contract'))['classification'])

    def test_gate_898_keeps_90_second_boundary(self):
        baseline=sample();current=sample()
        for at,expected in ((1089.99,None),(1090,'gate_898_unproven')):
            with self.subTest(at=at),patch.object(w.time,'time',return_value=at):
                self.assertEqual(expected,w.decision(current,baseline,set(),0,0,None,{'activated':1000})[0])

    def test_server_generation_and_client_renewal_both_required(self):
        frame={'line':'898','source':'GTFSRT_BUS','linked_complete_new':1}
        wire={'lines':{'898':{'new_after_activation':1}}}
        self.assertEqual('proven',w.correlation_state([frame],wire))
        self.assertEqual('client_only',w.correlation_state([dict(frame,linked_complete_new=0)],wire))
        self.assertEqual('server_only',w.correlation_state([frame],{}))
        with patch.object(w.time,'time',return_value=1089.99):
            current=sample();current.update(flow=[frame],wire={**current['wire'],**wire})
            self.assertIsNone(w.decision(current,sample(),set(),0,0,None,{'activated':1000})[0])
        for late in (1090,1090.001):
            with patch.object(w.time,'time',return_value=late):
                self.assertEqual('gate_898_unproven',w.decision(current,sample(),set(),0,0,None,{'activated':1000})[0])

    def test_empty_stale_and_aging_are_not_backend_failures(self):
        self.assertEqual('valid_empty',w.data_state({'vehicles':0}))
        self.assertEqual('present_stale',w.data_state({'vehicles':4,'older180':4,'until60':0}))
        self.assertEqual('present_aging',w.data_state({'vehicles':4,'older180':0,'until60':0}))
        self.assertEqual('present_recent',w.data_state({'vehicles':4,'older180':0,'until60':1}))

    def test_timeout_preserves_safe_command_cause(self):
        import subprocess
        with patch.object(w.subprocess,'run',side_effect=subprocess.TimeoutExpired(['docker','logs','secret'],5)):
            with self.assertRaises(w.CommandFailure) as caught:w.run(['docker','logs','secret'])
        self.assertEqual('command_timeout',caught.exception.evidence()['reason'])
        self.assertNotIn('secret',str(caught.exception))

    def test_request_stop_only_signals_verified_controller_and_keeps_reason(self):
        import tempfile,os,json
        pid=os.getpid()
        # No /proc access or signal is performed by this test, on Windows or Linux.
        with tempfile.TemporaryDirectory() as tmp,patch.object(w,'ROOT',Path(tmp)),patch.object(w,'load',return_value={'phase':'its','pid':pid,'pid_start':'birth'}),patch.object(Path,'read_text',return_value=' '.join(['0']*21+['birth'])),patch.object(Path,'read_bytes',return_value=str(Path(w.__file__).resolve()).encode()),patch.object(w.os,'kill') as kill,patch.object(w.os,'killpg',create=True) as killpg:
            w.request_stop('gate_898_unproven')
            kill.assert_called_once_with(pid,w.signal.SIGTERM);killpg.assert_not_called()
        with tempfile.TemporaryDirectory() as tmp,patch.object(w,'ROOT',Path(tmp)):
            (Path(tmp)/'stop-request.json').write_text(json.dumps({'reason':'gate_898_unproven'}))
            self.assertEqual('gate_898_unproven',w.stop_reason())

    def test_wrong_pid_never_signalled(self):
        with patch.object(w,'load',return_value={'phase':'its','pid':123,'pid_start':'correct'}),patch.object(Path,'read_text',return_value=' '.join(['0']*21+['wrong'])),patch.object(w.os,'kill') as kill:
            with self.assertRaises(RuntimeError):w.request_stop('operator_rollback')
            kill.assert_not_called()

    def test_flow_parser_rejects_malformed_frames_without_inventing_evidence(self):
        self.assertEqual([],w.parse_flow_logs('GPS flow: []\nGPS flow: {broken\nGPS flow: null'))
        self.assertEqual('unproven',w.correlation_state([],{}))

class CaptureSafety(unittest.TestCase):
    def test_capture_health_rejects_dead_stale_wrong_container_and_gaps(self):
        import tempfile,json,types
        contract={'session':'a'*32,'source_sha256':'b'*64,'pid':123,'pid_start':'123456',
                  'container_id':'c'*64,'directory_device':1,'directory_inode':2,'archive_device':1,'archive_inode':3}
        good={**contract,'pid':123,'image':w.APPROVED_IMAGE,'heartbeat':1000,
              'state':'running','ready':True,'failure':None,'gap_count':0,'truncated':False,
              'container_id':'c'*64,'records':4,'bytes':500,'input_bytes':100}
        proc=types.SimpleNamespace(pid=123,poll=lambda:None)
        identity={k:contract[k] for k in ('directory_device','directory_inode','archive_device','archive_inode')}
        with tempfile.TemporaryDirectory() as tmp,patch.object(w,'ROOT',Path(tmp)),patch.object(w,'capture_process',proc),patch.object(w,'capture_contract',contract),patch.object(w.time,'time',return_value=1005),patch.object(w,'process_identity',return_value=True),patch.object(w,'capture_file_identity',return_value=identity):
            root=Path(tmp)/'capture';root.mkdir();path=root/'health.json'
            path.write_text(json.dumps(good));self.assertTrue(w.capture_health('c'*64)['healthy'])
            for change in ({'heartbeat':994},{'heartbeat':float('nan')},{'container_id':'d'*64},
                           {'gap_count':1},{'truncated':True},{'ready':False},{'session':'d'*32},
                           {'state':'closed'},{'failure':'input_budget'}):
                with self.subTest(change=change):
                    path.write_text(json.dumps({**good,**change}));self.assertFalse(w.capture_health('c'*64)['healthy'])
            path.unlink();self.assertFalse(w.capture_health()['healthy'])
            path.write_text(json.dumps(good));proc.poll=lambda:23
            self.assertFalse(w.capture_health()['healthy'])

    def test_capture_failure_interrupts_without_relaxing_gps_gate(self):
        for capture in (None,{'healthy':False,'reason':'capture_evidence_failed'},{'healthy':True,'families':{}}):
            current=sample();current['capture']=capture
            self.assertEqual('capture_evidence_failed',w.decision(current,sample(),set(),0,0,None,{'capture_required':True})[0])
        current=sample();current['capture']={'healthy':True,'families':dict.fromkeys(('http_page','normalized_page','timing','acquisition','hybrid'),1)}
        with patch.object(w.time,'time',return_value=1090):
            self.assertEqual('gate_898_unproven',w.decision(current,sample(),set(),0,0,None,{'capture_required':True,'activated':1000})[0])
        self.assertEqual('evidence_capture',w.failure_evidence(w.CaptureFailure())['classification'])

    def test_capture_requires_exact_release_and_all_five_hashes(self):
        import tempfile,hashlib,types
        names=('its_window.py','its_log_capture.py','probe_http.py','its.primary.override.yml','gtfs.rollback.override.yml')
        proc=types.SimpleNamespace(pid=123,poll=lambda:None,terminate=lambda:None)
        identity={'directory_device':1,'directory_inode':2,'archive_device':1,'archive_inode':3}
        health={'ready':True,'session':'a'*32,'pid':123,'pid_start':'birth','container_id':'c'*64}
        with tempfile.TemporaryDirectory() as tmp,patch.object(w,'ROOT',Path(tmp)),patch.object(w,'save'),patch.object(w.subprocess,'Popen',return_value=proc) as popen,patch.object(w,'capture_contract',None),patch.object(w,'capture_process',None),patch.object(w,'read_capture_health',side_effect=lambda:{**health,'session':state['capture']['session']}),patch.object(w,'capture_file_identity',return_value=identity),patch.object(w,'capture_matches'),patch.object(w,'capture_descriptors',return_value=True),patch.object(w,'archive_prefix_valid'),patch.object(w,'inspect',return_value=[{'Id':'c'*64}]):
            hashes={}
            for name in names:
                data=name.encode();(Path(tmp)/name).write_bytes(data);hashes[name]=hashlib.sha256(data).hexdigest()
            for state in ({'image':'wrong','operational_sha256':hashes},{'image':w.APPROVED_IMAGE},
                          {'image':w.APPROVED_IMAGE,'operational_sha256':{**hashes,'probe_http.py':'wrong'}}):
                with self.assertRaises(w.CaptureFailure):w.start_capture(state)
            popen.assert_not_called()
            state={'image':w.APPROVED_IMAGE,'operational_sha256':hashes};w.start_capture(state)
            command=popen.call_args.args[0];options=popen.call_args.kwargs
            self.assertIn('--seconds',command);self.assertEqual('1000',command[-1])
            self.assertTrue(options['start_new_session']);self.assertTrue(options['close_fds'])
            self.assertEqual(w.subprocess.DEVNULL,options['stdout'])
            self.assertEqual(123,state['capture']['pid'])

    def test_capture_stop_does_not_wait_or_take_deploy_lock(self):
        from unittest.mock import Mock
        proc=Mock();proc.poll.return_value=None
        with patch.object(w,'capture_process',proc),patch.object(w,'take_lock') as lock:
            w.stop_capture()
        proc.terminate.assert_called_once();proc.wait.assert_not_called();proc.join.assert_not_called();lock.assert_not_called()

    def test_new_capture_terminates_if_identity_cannot_be_sealed(self):
        import tempfile,hashlib,types
        from unittest.mock import Mock
        proc=Mock();proc.pid=123;proc.poll.return_value=None
        with tempfile.TemporaryDirectory() as tmp,patch.object(w,'ROOT',Path(tmp)),patch.object(w,'capture_process',None),\
             patch.object(w,'capture_contract',None),patch.object(w,'save'),patch.object(w.subprocess,'Popen',return_value=proc),\
             patch.object(w,'read_capture_health',side_effect=lambda:{'ready':True,'session':state['capture']['session'],'pid_start':'123','container_id':'c'*64}),\
             patch.object(w,'capture_file_identity',return_value={}),patch.object(w,'inspect',side_effect=RuntimeError('docker unavailable')):
            hashes={}
            for name in w.CAPTURE_FILES:
                data=name.encode();(Path(tmp)/name).write_bytes(data);hashes[name]=hashlib.sha256(data).hexdigest()
            state={'image':w.APPROVED_IMAGE,'operational_sha256':hashes}
            with self.assertRaises(w.CaptureFailure):w.start_capture(state)
            proc.terminate.assert_called_once()
            self.assertIn('capture',state)

    def test_supervisor_rolls_back_before_stopping_capture(self):
        from unittest.mock import Mock
        # Abort immediately after activation; no clocks, threads, sockets, Docker or /proc I/O.
        # Exercise real supervisor control flow with a baseline ending on its second clock check.
        order=[];lock=Mock();lock.close.side_effect=lambda:order.append('lock_release');thread=Mock();baseline=sample()
        baseline['capture']={'healthy':True};baseline['wire'].update(messages=7,lines={x:{} for x in w.LINES})
        def fail_activation(state,*_):state['activated']=1000;raise RuntimeError('controlled_activation_failure')
        def returned_gtfs(state,*_):state['phase']='complete';order.append('rollback')
        with patch.object(w.os,'getpgrp',create=True,return_value=123),patch.object(w.os,'getpid',return_value=123),patch.object(Path,'read_text',return_value=' '.join(['0']*22)),patch.object(w,'take_lock',return_value=lock),patch.object(w,'save'),patch.object(w,'record'),patch.object(w.threading,'Thread',return_value=thread),patch.object(w.signal,'signal'),patch.object(w,'validate'),patch.object(w,'ensure_capture'),patch.object(w,'capture_health',return_value={'healthy':True}),patch.object(w,'inspect',return_value=[{'Id':'c'*64}]),patch.object(w,'sample',return_value=baseline),patch.object(w.time,'monotonic',side_effect=[0,0,1,121]),patch.object(w.stop,'wait'),patch.object(w.stop,'is_set',return_value=False),patch.object(w,'run_activation',side_effect=fail_activation),patch.object(w,'rollback',side_effect=returned_gtfs),patch.object(w,'stop_capture',side_effect=lambda:order.append('capture_stop')),patch.object(w,'finalize_capture',side_effect=lambda *_:order.append('verify')):
            w.supervise({'image':w.APPROVED_IMAGE})
        w.stop.clear()
        self.assertEqual(['rollback','capture_stop','lock_release','verify'],order);lock.close.assert_called_once()

class CaptureHandoff(unittest.TestCase):
    """Proof process exits; the cron controller must attach to its live child."""
    def setUp(self):
        import tempfile,hashlib,json,os
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)/'window';self.root.mkdir()
        self.proc=Path(self.temp.name)/'proc';self.proc.mkdir()
        self.pid=4321;self.birth='123456';self.session='a'*32;self.container='c'*64
        self.names=('its_window.py','its_log_capture.py','probe_http.py','its.primary.override.yml','gtfs.rollback.override.yml')
        self.hashes={}
        for name in self.names:
            data=name.encode();(self.root/name).write_bytes(data);self.hashes[name]=hashlib.sha256(data).hexdigest()
        capture=self.root/'capture';capture.mkdir()
        (capture/'aggregates.jsonl').write_text(json.dumps({'kind':'begin','schema':1,'session':self.session})+'\n'+json.dumps({'kind':'stream_start','container':self.container})+'\n')
        directory_stat=capture.stat();archive_stat=(capture/'aggregates.jsonl').stat()
        self.contract={'session':self.session,'source_sha256':self.hashes['its_log_capture.py'],'pid':self.pid,
            'pid_start':self.birth,'container_id':self.container,
            'directory_device':directory_stat.st_dev,'directory_inode':directory_stat.st_ino,
            'archive_device':archive_stat.st_dev,'archive_inode':archive_stat.st_ino}
        self.state={'phase':'scheduled','image':w.APPROVED_IMAGE,'operational_sha256':self.hashes,'capture':dict(self.contract)}
        self.health={**self.contract,'image':w.APPROVED_IMAGE,'heartbeat':1000,'state':'running','ready':True,
                     'failure':None,'gap_count':0,'truncated':False,'records':2,'bytes':(capture/'aggregates.jsonl').stat().st_size,
                     'input_bytes':100,'families':{}}
        (capture/'health.json').write_text(json.dumps(self.health))
        pid_dir=self.proc/str(self.pid);pid_dir.mkdir()
        (pid_dir/'stat').write_text(' '.join(['0']*21+[self.birth,'0','0']))
        self.cmd=['python3','-B',str(self.root/'its_log_capture.py'),'--directory',str(capture),
                  '--session',self.session,'--seconds','1000']
        (pid_dir/'cmdline').write_bytes(b'\0'.join(x.encode() for x in self.cmd)+b'\0')
        self.patches=[patch.object(w,'ROOT',self.root),patch.object(w,'PROC_ROOT',self.proc,create=True),
                      patch.object(w,'capture_process',None),patch.object(w,'capture_contract',None),
                      patch.object(w.time,'time',return_value=1001),
                      patch.object(w,'capture_descriptors',return_value=True,create=True),
                      patch.object(w,'inspect',return_value=[{'Id':self.container}])]
        for item in self.patches:item.start();self.addCleanup(item.stop)

    def test_healthy_proof_capture_is_adopted_without_second_process(self):
        with patch.object(w,'start_capture') as start:
            w.ensure_capture(self.state)
            self.assertTrue(w.capture_health(self.container)['healthy'])
            w.ensure_capture(self.state)
            start.assert_not_called()
        self.assertEqual(self.pid,w.capture_process.pid)

    def test_adoption_rejects_other_session_and_reused_pid(self):
        for changed in ({'session':'b'*32},{'pid_start':'wrong'}):
            with self.subTest(changed=changed):
                state={**self.state,'capture':{**self.contract,**changed}}
                with self.assertRaises(w.CaptureFailure):w.ensure_capture(state)

    def test_adoption_rejects_bad_script_image_container_and_stale_or_gapped_health(self):
        import json
        path=self.root/'capture'/'health.json'
        for changed in ({'source_sha256':'0'*64},{'image':'wrong'},{'container_id':'d'*64},
                        {'heartbeat':989},{'gap_count':1},{'truncated':True},{'failure':'input_queue_overflow'}):
            with self.subTest(changed=changed):
                path.write_text(json.dumps({**self.health,**changed}))
                with self.assertRaises(w.CaptureFailure):w.ensure_capture(self.state)
        path.write_text(json.dumps(self.health))

    def test_existing_archive_without_live_process_is_no_go(self):
        (self.proc/str(self.pid)/'stat').unlink()
        with patch.object(w.subprocess,'Popen') as popen:
            with self.assertRaises(w.CaptureFailure):w.ensure_capture(self.state)
            popen.assert_not_called()

    def test_incomplete_contract_wrong_archive_session_and_torn_prefix_are_no_go(self):
        import json
        state={**self.state,'capture':{k:v for k,v in self.contract.items() if k!='pid_start'}}
        with self.assertRaises(w.CaptureFailure):w.ensure_capture(state)
        archive=self.root/'capture'/'aggregates.jsonl'
        before=archive.read_bytes()
        archive.write_text(json.dumps({'kind':'begin','schema':1,'session':'b'*32})+'\n'+json.dumps({'kind':'stream_start'})+'\n')
        with self.assertRaises(w.CaptureFailure):w.ensure_capture(self.state)
        archive.write_bytes(before[:-1])
        with self.assertRaises(w.CaptureFailure):w.ensure_capture(self.state)

    def test_process_disappearing_after_adoption_invalidates_health_and_cannot_be_signalled(self):
        w.ensure_capture(self.state)
        (self.proc/str(self.pid)/'stat').unlink()
        self.assertFalse(w.capture_health(self.container)['healthy'])
        with patch.object(w.os,'kill') as kill:
            w.stop_capture()
            kill.assert_not_called()

    def test_watchdog_only_signals_orphan_with_verified_birth_and_files(self):
        with patch.object(w.os,'kill') as kill:
            w.stop_orphan_capture(self.state)
            kill.assert_called_once_with(self.pid,w.signal.SIGTERM)
        (self.proc/str(self.pid)/'stat').write_text(' '.join(['0']*21+['reused','0']))
        with patch.object(w.os,'kill') as kill:
            w.stop_orphan_capture(self.state)
            kill.assert_not_called()

    def test_controlled_api_recreation_keeps_reader_but_requires_current_container_id(self):
        import json
        w.ensure_capture(self.state)
        changed='d'*64
        (self.root/'capture'/'health.json').write_text(json.dumps({**self.health,'container_id':changed}))
        self.assertTrue(w.capture_health(changed)['healthy'])
        self.assertFalse(w.capture_health(self.container)['healthy'])

    def test_archive_prefix_survives_handoff_and_file_identity_is_stable(self):
        import json
        w.ensure_capture(self.state)
        archive=self.root/'capture'/'aggregates.jsonl'
        before=archive.read_text();inode=archive.stat().st_ino
        with archive.open('a') as file:file.write(json.dumps({'kind':'aggregate','family':'http_page'})+'\n')
        self.assertTrue(w.capture_health(self.container)['healthy'])
        self.assertEqual(inode,archive.stat().st_ino)
        self.assertTrue(archive.read_text().startswith(before))
        self.assertEqual(3,len(archive.read_text().splitlines()))

    def test_ambiguous_existing_archive_cannot_be_replaced(self):
        state={k:v for k,v in self.state.items() if k!='capture'}
        with patch.object(w.subprocess,'Popen') as popen:
            with self.assertRaises(w.CaptureFailure):w.ensure_capture(state)
            popen.assert_not_called()

    def test_no_existing_capture_can_start_one_fresh_reader(self):
        state={k:v for k,v in self.state.items() if k!='capture'}
        import shutil
        shutil.rmtree(self.root/'capture')
        with patch.object(w,'start_capture') as start:
            w.ensure_capture(state)
            start.assert_called_once_with(state)

    def test_tampered_script_cmdline_archive_or_inherited_lock_is_no_go(self):
        archive=self.root/'capture'/'aggregates.jsonl'
        script=self.root/'its_log_capture.py'
        script.write_bytes(b'tampered')
        with self.assertRaises(w.CaptureFailure):w.ensure_capture(self.state)
        script.write_bytes(b'its_log_capture.py')
        cmdline=self.proc/str(self.pid)/'cmdline'
        cmdline.write_bytes(b'python3\0-B\0other-script\0')
        with self.assertRaises(w.CaptureFailure):w.ensure_capture(self.state)
        cmdline.write_bytes(b'\0'.join(x.encode() for x in self.cmd)+b'\0')
        self.state['capture']['archive_inode']+=1
        with self.assertRaises(w.CaptureFailure):w.ensure_capture(self.state)
        self.state['capture']['archive_inode']-=1
        # A separate capture with a valid identity still cannot inherit the deploy lock.
        with patch.object(w,'capture_descriptors',return_value=False):
            with self.assertRaises(w.CaptureFailure):w.ensure_capture(self.state)

    def test_precondition_abort_signals_only_adopted_pid_and_verifies_once_after_safe_state(self):
        import json
        w.ensure_capture(self.state)
        with patch.object(w.os,'kill') as kill,patch.object(w,'take_lock') as lock:
            w.stop_capture()
            kill.assert_called_once_with(self.pid,w.signal.SIGTERM)
            lock.assert_not_called()
        closed={**self.health,'state':'closed','ready':False}
        (self.root/'capture'/'health.json').write_text(json.dumps(closed))
        state={**self.state,'phase':'precondition_failed'}
        result={'integrity_valid':True,'archive_complete':True,'evidence_complete':True,'gap_count':0}
        with patch.object(w,'run',return_value=json.dumps(result)) as run,patch.object(w,'save') as save,patch.object(w,'record') as record:
            self.assertTrue(w.finalize_capture(state)['evidence_complete'])
            w.finalize_capture(state)
            run.assert_called_once();save.assert_called_once();record.assert_called_once()
        self.assertEqual('precondition_failed',state['phase'])

    def test_proof_cron_scheduled_baseline_adopts_then_aborts_before_its(self):
        from unittest.mock import Mock
        controller=9876;proc=self.proc/str(controller);proc.mkdir()
        (proc/'stat').write_text(' '.join(['0']*21+['controller-birth','0']))
        state=dict(self.state)
        state['phase']='proof'
        original_read=Path.read_text
        parents={controller:('python3',200),200:('sh',300),300:('cron',400),400:('cron',1),1:('systemd',0)}
        def fake_read(path,*args,**kwargs):
            name=str(path).replace('\\','/')
            if name.startswith('/proc/'):
                parts=name.split('/')
                pid=int(parts[-2]);kind=parts[-1]
                if pid in parents:return parents[pid][0] if kind=='comm' else 'PPid:\t'+str(parents[pid][1])
            return original_read(path,*args,**kwargs)
        with patch.object(w,'load',return_value=state),patch.object(w.os,'getpid',return_value=controller),patch.object(Path,'read_text',fake_read):
            w.tick()
        self.assertTrue(__import__('json').loads((self.root/'cron-proof.json').read_text())['independent_cron'])
        state['phase']='scheduled'
        baseline=sample();baseline['capture']={'healthy':True}
        baseline['wire'].update(messages=2,lines={x:{} for x in w.LINES})
        events=[];lock=Mock();thread=Mock();stopping=Mock()
        stopping.is_set.side_effect=[False,True,True]
        with patch.object(w,'load',return_value=state),patch.object(w,'save'),patch.object(w,'record',side_effect=lambda k,v:events.append(k)),\
             patch.object(w,'take_lock',return_value=lock),patch.object(w,'validate'),patch.object(w,'sample',return_value=baseline),\
             patch.object(w,'stop',stopping),patch.object(w.threading,'Thread',return_value=thread),patch.object(w.signal,'signal'),\
             patch.object(w.os,'getpgrp',create=True,return_value=controller),patch.object(w.os,'getpid',return_value=controller),\
             patch.object(w.time,'monotonic',side_effect=[0,0,0,121]),patch.object(w,'switch') as switch,\
             patch.object(w,'start_capture') as start,patch.object(w,'stop_capture'),patch.object(w,'finalize_capture'):
            w.tick()
        self.assertIn('baseline_sample',events,(events,state.get('failure')))
        self.assertNotIn('capture_evidence_failed',[state.get('failure'),state.get('reason')])
        self.assertEqual('precondition_failed',state['phase'])
        self.assertNotIn('its_sample',events)
        switch.assert_not_called();start.assert_not_called();lock.close.assert_called_once()

    def test_invalid_handoff_fails_before_baseline_and_never_switches_source(self):
        import json
        from unittest.mock import Mock
        health=self.root/'capture'/'health.json'
        health.write_text(json.dumps({**self.health,'gap_count':1}))
        state=dict(self.state);state['phase']='scheduled'
        events=[];lock=Mock();thread=Mock();stopping=Mock();stopping.is_set.return_value=False
        with patch.object(w,'load',return_value=state),patch.object(w,'save'),patch.object(w,'record',side_effect=lambda k,v:events.append((k,v))),\
             patch.object(w,'take_lock',return_value=lock),patch.object(w,'validate'),patch.object(w,'stop',stopping),\
             patch.object(w.threading,'Thread',return_value=thread),patch.object(w.signal,'signal'),\
             patch.object(w.os,'getpgrp',create=True,return_value=self.pid),patch.object(w.os,'getpid',return_value=self.pid),\
             patch.object(w,'sample') as sample_call,patch.object(w,'switch') as switch,patch.object(w,'stop_capture'),\
             patch.object(w,'finalize_capture'):
            w.tick()
        self.assertEqual('precondition_failed',state['phase'])
        self.assertEqual('CaptureFailure',state['failure'])
        self.assertEqual('capture_evidence_failed',events[0][1]['operational_reason'])
        sample_call.assert_not_called();switch.assert_not_called();lock.close.assert_called_once()

class StartupDeadline(unittest.TestCase):
    def test_real_supervisor_startup_does_not_delay_898_rollback_past90(self):
        from contextlib import ExitStack
        from unittest.mock import Mock
        clock={'now':0.,'alarm':None};handlers={};rollback_at=[];saved=[]
        def advance(seconds):
            end=clock['now']+seconds
            while clock['alarm'] is not None and clock['alarm']<=end:
                clock['now']=clock['alarm'];clock['alarm']=None;handlers[14](14,None)
            clock['now']=end
        def alarm(_kind,seconds,*_):clock['alarm']=clock['now']+seconds if seconds else None
        event=Mock();event.is_set.return_value=False;event.wait.side_effect=advance
        baseline=sample();baseline['capture']={'healthy':True}
        baseline['wire'].update(messages=10,lines={x:{} for x in w.LINES})
        api={'Id':'a'*64,'Config':{'Image':w.APPROVED_IMAGE,'Env':[
            'GpsSources__BusPrimarySource=ITS_CURRENT','GpsSources__BusSupportMode=Hybrid','GpsSources__BrtPrimarySource=GTFSRT_BRT']},
            'State':{'Running':True,'OOMKilled':False},'RestartCount':0}
        def rollback(state,reason):rollback_at.append((clock['now'],reason));state['phase']='complete'
        with ExitStack() as stack:
            patches=[(w.os,'getpgrp',Mock(return_value=123)),(w.os,'getpid',Mock(return_value=123)),
                (w,'process_birth',Mock(return_value='birth')),(w,'take_lock',Mock()),(w,'save',lambda s:saved.append(copy.deepcopy(s))),
                (w,'record',Mock()),(w.threading,'Thread',Mock()),(w.signal,'signal',lambda key,fn:handlers.__setitem__(key,fn)),
                (w.signal,'getsignal',Mock(return_value=None)),(w.signal,'setitimer',alarm),(w.signal,'getitimer',Mock(return_value=(0,0))),
                (w.signal,'SIGALRM',14),(w.signal,'ITIMER_REAL',0),(w,'validate',Mock()),(w,'ensure_capture',Mock()),
                (w,'capture_health',Mock(return_value={'healthy':True})),(w,'inspect',Mock(return_value=[api])),
                (w,'sample',Mock(return_value=baseline)),(w,'get',Mock(return_value=(b'',0))),
                (w,'run',Mock(return_value='BUS hybrid: primary_status=Falha support_status=Sucesso selected_its=0 selected_gtfs=100')),
                (w,'stop',event),(w,'activation_epoch',None),(w.time,'monotonic',lambda:clock['now']),(w.time,'time',lambda:1000+clock['now']),
                (w.time,'sleep',advance),(w,'switch',lambda *_:advance(35)),(w,'rollback',rollback),
                (w,'stop_capture',Mock()),(w,'finalize_capture',Mock())]
            for obj,key,value in patches:stack.enter_context(patch.object(obj,key,value,create=True))
            w.supervise({'image':w.APPROVED_IMAGE})
        activation=next(x['activated']-1000 for x in saved if 'activated' in x)
        self.assertEqual(1,len(rollback_at))
        self.assertLessEqual(rollback_at[0][0]-activation,90)
        self.assertEqual('gate_898_unproven',rollback_at[0][1])

class SimulatedTime:
    def __init__(self):self.now=0.;self.alarm=None;self.handlers={};self.actions=[]
    def schedule(self,at,action):self.actions.append((at,action));self.actions.sort(key=lambda x:x[0])
    def advance(self,seconds):
        end=self.now+seconds
        while True:
            action=self.actions[0][0] if self.actions else float('inf')
            alarm=self.alarm if self.alarm is not None else float('inf')
            if min(action,alarm)>end:break
            self.now=min(action,alarm)
            if alarm<=action:
                self.alarm=None;self.handlers[14](14,None)
            else:self.actions.pop(0)[1]()
        self.now=end
    def arm(self,_kind,seconds,*_):self.alarm=self.now+seconds if seconds else None


class ActivationLoop(unittest.TestCase):
    def setUp(self):
        from contextlib import ExitStack
        from unittest.mock import Mock
        self.stack=ExitStack();self.addCleanup(self.stack.close)
        self.clock=SimulatedTime();self.events=[];self.saved=[];self.observers=[]
        self.boot=35;self.proof_at=None;self.client=True;self.client_new=1;self.healthy_at=60
        self.state={'image':w.APPROVED_IMAGE,'phase':'baseline'};self.baseline=sample()
        self.baseline['cycles']=[]
        self.stopping=Mock();self.stopping.is_set.return_value=False;self.stopping.wait.side_effect=self.clock.advance
        def start(observer):
            self.observers.append(observer)
            for at in range(0,601,15):
                self.clock.schedule(at,lambda o=observer:None if o.finished.is_set() else o.observe_once())
        self.original_switch=w.switch
        self.switch=Mock(side_effect=lambda *_:self.clock.advance(self.boot))
        patches=[(w.time,'monotonic',lambda:self.clock.now),(w.time,'time',lambda:1000+self.clock.now),
            (w.signal,'SIGALRM',14),(w.signal,'ITIMER_REAL',0),(w.signal,'setitimer',self.clock.arm),
            (w.signal,'getitimer',lambda *_:(0.,0.)),(w.signal,'getsignal',lambda *_:None),
            (w.signal,'signal',lambda key,fn:self.clock.handlers.__setitem__(key,fn)),(w,'stop',self.stopping),(w,'activation_epoch',None),
            (w.threading,'Thread',Mock()),(w.ActivationObserver,'start',start),(w,'sample',self.observation),
            (w,'switch',self.switch),(w,'inspect',lambda:[{'Config':{'Image':w.APPROVED_IMAGE,'Env':[
                'GpsSources__BusPrimarySource=ITS_CURRENT','GpsSources__BusSupportMode=Hybrid','GpsSources__BrtPrimarySource=GTFSRT_BRT']}}]),
            (w,'save',lambda s:self.saved.append(copy.deepcopy(s))),
            (w,'record',lambda k,v:self.events.append((k,copy.deepcopy(v))))]
        for obj,key,value in patches:self.stack.enter_context(patch.object(obj,key,value,create=True))

    def observation(self):
        x=sample();now=self.clock.now;x['source']='ITS_CURRENT' if now>=self.boot else 'GTFSRT_BUS'
        healthy=now>=self.healthy_at
        x['selection']=[{'mode':'Hybrid','stamp':str(now),'primary_status':'Sucesso' if healthy else 'Falha',
                         'support_status':'Sucesso','selected_its':'100' if healthy else '0','selected_gtfs':'200'}]
        x['capture']={'healthy':True,'families':dict.fromkeys(('http_page','normalized_page','timing','acquisition','hybrid'),1)}
        x['wire'].update(messages=3,connected=self.client,lines={'898':{'new_after_activation':self.client_new}} if self.client else {})
        x['flow']=[{'line':'898','source':'GTFSRT_BUS','generation':2,'linked_complete_new':int(self.proof_at is not None and now>=self.proof_at)}]
        return x

    def run_until_abort(self,reason):
        with self.assertRaises(w.SafetyFailure) as error:w.run_activation(self.state,self.baseline,[])
        self.assertEqual(reason,error.exception.reason)
        self.assertEqual(1000,self.state['activated']);self.assertEqual(1600,self.state['deadline'])
        self.assertIsNone(self.clock.alarm)

    def test_slow_start_and_its_timeout_keep_gtfs_observed_until_exact90(self):
        self.healthy_at=999
        self.run_until_abort('gate_898_unproven')
        self.assertEqual(90,self.clock.now)
        self.assertEqual(['starting','starting','starting','degraded','degraded','degraded'],[v['hybrid_state'] for _,v in self.events])
        self.assertTrue(all(v['wire']['messages']>0 for _,v in self.events))
        self.switch.assert_called_once()

    def test_selection79_ack92_never_proves_the_gate(self):
        self.proof_at=92
        self.clock.schedule(79,lambda:self.observers[0].observe_once())
        self.run_until_abort('gate_898_unproven')
        self.assertEqual(90,self.clock.now)
        self.assertNotIn('gate898_proven_elapsed',self.state)
        self.proof_at=90;self.observers[0].safety.admit(self.observation())
        self.assertFalse(self.observers[0].safety.proven)
        self.clock.advance(2);self.observers[0].safety.admit(self.observation())
        self.assertFalse(self.observers[0].safety.proven)

    def test_complete_server_rest_and_client_proof_before90_keeps_max600(self):
        self.proof_at=75
        result=w.run_activation(self.state,self.baseline,[])
        self.assertEqual('ten_minutes_completed',result);self.assertEqual(600,self.clock.now)
        self.assertEqual(75,self.state['gate898_proven_elapsed'])
        self.assertTrue(any(v['hybrid_state']=='healthy' for _,v in self.events))

    def test_rest_and_server_without_signalr_client_do_not_pass(self):
        self.proof_at=60;self.client=False
        self.run_until_abort('gate_898_unproven');self.assertEqual(90,self.clock.now)

    def test_blocked_source_switch_is_interrupted_without_second_writer(self):
        self.boot=200
        self.run_until_abort('gate_898_unproven');self.assertEqual(90,self.clock.now)
        self.switch.assert_called_once()

    def test_observer_that_never_finishes_cannot_hold_the_deadline(self):
        with patch.object(w.ActivationObserver,'start',lambda *_:None):self.run_until_abort('gate_898_unproven')
        self.assertEqual(90,self.clock.now);self.assertEqual([],self.events)

    def test_degraded_support_with_898_proof_still_cannot_approve_hybrid_health(self):
        self.proof_at=60;self.healthy_at=999
        self.run_until_abort('hybrid_startup_unconfirmed')
        self.assertEqual(95,self.clock.now)
        self.assertTrue(all(v['hybrid_state']!='healthy' for _,v in self.events))

    def test_startup_http_failure_is_observed_and_interrupts_at30(self):
        from probe_http import ProbeError
        with patch.object(w,'sample',side_effect=ProbeError('/',503,'http_failure')):self.run_until_abort('backend_unavailable')
        self.assertEqual(30,self.clock.now)
        self.assertEqual(2,len(self.events))
        self.assertTrue(all(k=='its_observation_error' and v['http_status']==503 for k,v in self.events))

    def test_recorder_failure_is_fail_closed_without_waiting_for90(self):
        with patch.object(w,'record',side_effect=OSError('private data')):self.run_until_abort('observation_failed')
        self.assertEqual(.25,self.clock.now)

    def test_no_interruption_capability_blocks_before_activation(self):
        with patch.object(w.signal,'getitimer',return_value=(1,0)):
            with self.assertRaises(w.SafetyFailure) as error:w.run_activation(self.state,self.baseline,[])
        self.assertEqual('deadline_interrupt_unavailable',error.exception.reason)
        self.switch.assert_not_called();self.assertNotIn('activated',self.state)

    def test_timer_installation_failure_restores_handlers_before_any_source_switch(self):
        with patch.object(w.signal,'setitimer',side_effect=OSError('not available')):
            with self.assertRaises(w.SafetyFailure) as error:w.run_activation(self.state,self.baseline,[])
        self.assertEqual('deadline_interrupt_unavailable',error.exception.reason)
        self.switch.assert_not_called();self.assertNotIn('activated',self.state)
        self.assertEqual({14:None,w.signal.SIGTERM:None,w.signal.SIGINT:None},self.clock.handlers)

    def test_existing_activation_or_proof_cannot_reset_the_deadline(self):
        for extra in ({'activated':500,'deadline':1100},{'gate898_proven_elapsed':5}):
            state={**self.state,**extra};original=copy.deepcopy(state)
            with self.assertRaises(w.SafetyFailure) as error:w.run_activation(state,self.baseline,[])
            self.assertEqual('activation_state_reused',error.exception.reason);self.assertEqual(original,state)
        self.switch.assert_not_called()

    def test_proof_phase_cannot_call_the_activation_path(self):
        self.state['phase']='proof'
        with self.assertRaises(w.SafetyFailure) as error:w.run_activation(self.state,self.baseline,[])
        self.assertEqual('activation_phase_invalid',error.exception.reason)
        self.switch.assert_not_called();self.assertNotIn('activated',self.state)

    def test_beta_requires_explicit_authorization_and_strict_remains_default(self):
        self.assertFalse(w.beta_mode({}))
        self.state['experiment_mode']='beta_diagnostic'
        with self.assertRaises(w.SafetyFailure) as error:w.run_activation(self.state,self.baseline,[])
        self.assertEqual('beta_not_authorized',error.exception.reason);self.switch.assert_not_called()

    def test_beta_collects_degraded_its_and_late898_until600(self):
        self.state.update(experiment_mode='beta_diagnostic',beta_authorized=True)
        self.healthy_at=999;self.proof_at=92
        self.assertEqual('ten_minutes_completed',w.run_activation(self.state,self.baseline,[]))
        self.assertEqual(600,self.clock.now)
        self.assertTrue(any(v['activation_elapsed']>90 and v['hybrid_state']=='degraded' for _,v in self.events))
        last=self.events[-1][1]['beta_diagnostics']
        self.assertFalse(last['gate898_within90']);self.assertEqual(105,last['first_complete_elapsed'])

    def test_beta_keeps_persistent_backend_outage_critical(self):
        from probe_http import ProbeError
        self.state.update(experiment_mode='beta_diagnostic',beta_authorized=True)
        with patch.object(w,'sample',side_effect=ProbeError('/',503,'http_failure')):self.run_until_abort('backend_unavailable')
        self.assertEqual(30,self.clock.now)

    def test_operator_stop_interrupts_boot_and_keeps_operational_reason(self):
        self.clock.schedule(10,lambda:self.clock.handlers[w.signal.SIGTERM](w.signal.SIGTERM,None))
        with patch.object(w,'stop_reason',return_value='operator_rollback'):self.run_until_abort('operator_rollback')
        self.assertEqual(10,self.clock.now)

    def test_late_healthy_sample_after_stop_cannot_add_proof_or_events(self):
        self.proof_at=0
        def after_stop():
            self.observers[0].stop();return self.observation()
        with patch.object(w,'sample',side_effect=after_stop):self.run_until_abort('gate_898_unproven')
        self.assertEqual([],self.events);self.assertNotIn('gate898_proven_elapsed',self.state)

    def test_wall_clock_jump_does_not_move_monotonic_898_deadline(self):
        with patch.object(w.time,'time',side_effect=lambda:1000+self.clock.now-(10000 if self.clock.now>=45 else 0)):
            self.run_until_abort('gate_898_unproven')
        self.assertEqual(90,self.clock.now)

    def test_runtime_gate_abort_terminates_command_before_late_up(self):
        calls=[]
        def command(args,**_):calls.append(args);self.clock.advance(200)
        with patch.object(w,'switch',side_effect=self.original_switch),patch.object(w,'run',side_effect=command):
            self.run_until_abort('gate_898_unproven')
        self.assertEqual(1,len(calls));self.assertIn('stop',calls[0])
        self.assertEqual(90,self.clock.now)

    def test_898_proof_is_not_server_signalr_send_without_client_observation(self):
        self.proof_at=60;self.client=False
        self.run_until_abort('gate_898_unproven')
        self.assertTrue(any(x['flow'][0]['linked_complete_new']>0 for _,x in self.events))
        self.assertTrue(all(not x['wire']['lines'] for _,x in self.events))

    def test_healthy_rest_and_stale_client_positions_never_simulate_physical_renewal(self):
        self.proof_at=60;self.client_new=0
        self.run_until_abort('gate_898_unproven')
        self.assertEqual(90,self.clock.now)
        self.assertEqual(6,len(self.events))
        self.assertTrue(all(x['wire']['lines']['898']['new_after_activation']==0 for _,x in self.events))

    def test_missing_normalized_page_is_expected_only_while_its_is_unavailable(self):
        original=self.observation
        def without_normalization():
            x=original();x['capture']['families'].pop('normalized_page');return x
        self.healthy_at=60
        with patch.object(w,'sample',side_effect=without_normalization):self.run_until_abort('capture_evidence_failed')
        self.assertGreaterEqual(self.clock.now,60);self.assertLessEqual(self.clock.now,60.25)
        self.assertTrue(any(x['hybrid_state']=='degraded' for _,x in self.events))
        self.assertEqual('healthy',self.events[-1][1]['hybrid_state'])


class StartupRecovery(unittest.TestCase):
    def test_beta_watchdog_ignores898_but_keeps_expiration(self):
        from unittest.mock import Mock
        state={'phase':'its','activated':1000,'deadline':1600,'heartbeat':1091,'experiment_mode':'beta_diagnostic','beta_authorized':True}
        with patch.object(w,'load',return_value=state),patch.object(w.time,'time',return_value=1091),patch.object(w,'rollback') as rollback:
            w.tick();rollback.assert_not_called()
        state['heartbeat']=1606
        with patch.object(w,'load',return_value=state),patch.object(w.time,'time',return_value=1606),\
             patch.object(w,'take_lock',return_value=Mock()),patch.object(w,'rollback') as rollback,\
             patch.object(w,'stop_orphan_capture'),patch.object(w,'finalize_capture'):
            w.tick();rollback.assert_called_once_with(state,'independent_cron_watchdog')

    def test_beta_diagnostic_gates_cannot_mask_critical_failures(self):
        baseline=sample();x=sample();x['capture']={'healthy':False};x['cycles']=[{'stamp':'slow','total_ms':70000}]
        watch={'beta':True,'capture_required':True,'activated':1000,'activation_elapsed':100}
        with patch.object(w.time,'time',return_value=1100):
            self.assertIsNone(w.decision(x,baseline,set(),0,3,None,dict(watch))[0])
            for field,reason in (('oom','api_restart_or_oom'),('writer_count','multiple_gps_writers')):
                bad=copy.deepcopy(x);bad[field]=True if field=='oom' else 2
                self.assertEqual(reason,w.decision(bad,baseline,set(),0,3,None,dict(watch))[0])
            bad=copy.deepcopy(x);bad['wire']['regressions']=1
            self.assertEqual('signalr_gps_integrity',w.decision(bad,baseline,set(),0,0,None,dict(watch))[0])
            bad=copy.deepcopy(x);bad['cycles'][0]['commits_infra']=1
            self.assertEqual('cas_infrastructure_failure',w.decision(bad,baseline,set(),0,0,None,dict(watch))[0])
            x['selection']=[{'mode':'Hybrid','stamp':str(i),'primary_status':'Vazio','support_status':'Vazio','selected':'0'} for i in range(2)]
            self.assertEqual('both_sources_failed',w.decision(x,baseline,set(),0,0,None,dict(watch))[0])
            x['selection']=[{'mode':'Hybrid','stamp':str(i),'primary_status':'Sucesso','support_status':'Sucesso','selected':'10','recent180':'0'} for i in range(2)]
            self.assertEqual('both_sources_failed',w.decision(x,baseline,set(),0,0,None,dict(watch))[0])

    def test_post_rollback_probe_failure_cannot_recreate_api_or_undo_safe_state(self):
        from contextlib import ExitStack
        from unittest.mock import Mock
        from probe_http import ProbeError
        state={'image':w.APPROVED_IMAGE};baseline=sample();baseline['capture']={'healthy':True}
        baseline['wire'].update(messages=2,lines={x:{} for x in w.LINES})
        def completed_window(s,*_):s.update(activated=1000,phase='its');return 'ten_minutes_completed'
        def returned(s,*_):s['phase']='complete'
        rollback=Mock(side_effect=returned);verify=Mock();stop_capture=Mock();event=Mock();event.is_set.return_value=False
        with ExitStack() as stack:
            for obj,key,value in [(w.os,'getpgrp',Mock(return_value=123)),(w.os,'getpid',Mock(return_value=123)),
                (w,'process_birth',Mock(return_value='birth')),(w,'take_lock',Mock()),(w,'save',Mock()),(w,'record',Mock()),
                (w.threading,'Thread',Mock()),(w.signal,'signal',Mock()),(w,'validate',Mock()),(w,'ensure_capture',Mock()),
                (w,'capture_health',Mock(return_value={'healthy':True})),(w,'inspect',Mock(return_value=[{'Id':'c'*64}])),
                (w,'sample',Mock(side_effect=[baseline,ProbeError('/',503,'http_failure')])),
                (w.time,'monotonic',Mock(side_effect=[0,0,1,121])),(w,'stop',event),
                (w,'run_activation',completed_window),(w,'rollback',rollback),(w,'stop_capture',stop_capture),(w,'finalize_capture',verify)]:
                stack.enter_context(patch.object(obj,key,value,create=True))
            w.supervise(state)
        self.assertEqual('complete',state['phase']);rollback.assert_called_once()
        stop_capture.assert_called_once();verify.assert_called_once()

    def test_failed_rollback_defers_capture_cleanup_until_independent_watchdog_recovers(self):
        from contextlib import ExitStack
        from unittest.mock import Mock
        state={'image':w.APPROVED_IMAGE};order=[];lock=Mock();lock.close.side_effect=lambda:order.append('unlock')
        baseline=sample();baseline['capture']={'healthy':True};baseline['wire'].update(messages=2,lines={x:{} for x in w.LINES})
        def fail_start(s,*_):s.update(activated=1000,phase='its');raise w.SafetyFailure('gate_898_unproven')
        def fail_return(*_):order.append('rollback');raise RuntimeError('private provider text')
        event=Mock();event.is_set.return_value=False;stop_capture=Mock();verify=Mock();records=[]
        with ExitStack() as stack:
            for obj,key,value in [(w.os,'getpgrp',Mock(return_value=123)),(w.os,'getpid',Mock(return_value=123)),
                (w,'process_birth',Mock(return_value='birth')),(w,'take_lock',Mock(return_value=lock)),(w,'save',Mock()),
                (w,'record',lambda k,v:records.append((k,v))),(w.threading,'Thread',Mock()),(w.signal,'signal',Mock()),
                (w,'validate',Mock()),(w,'ensure_capture',Mock()),(w,'capture_health',Mock(return_value={'healthy':True})),
                (w,'inspect',Mock(return_value=[{'Id':'c'*64}])),(w,'sample',Mock(return_value=baseline)),
                (w.time,'monotonic',Mock(side_effect=[0,0,1,121])),(w,'stop',event),
                (w,'run_activation',fail_start),(w,'rollback',fail_return),(w,'stop_capture',stop_capture),(w,'finalize_capture',verify)]:
                stack.enter_context(patch.object(obj,key,value,create=True))
            w.supervise(state)
        self.assertEqual('rollback_failed',state['phase']);self.assertEqual(['rollback','unlock'],order)
        stop_capture.assert_not_called();verify.assert_not_called()
        failure=next(value for kind,value in records if kind=='supervisor_error')
        self.assertEqual('safety_gate',failure['classification']);self.assertEqual('gate_898_unproven',failure['operational_reason'])
        self.assertNotIn('private',str(records))
        state.pop('pid');state['heartbeat']=0;order.clear()
        def recovered(s,reason):s['phase']='complete';order.append('gtfs_confirmed')
        with patch.object(w,'load',return_value=state),patch.object(w.time,'time',return_value=2000),\
             patch.object(w,'take_lock',return_value=lock),patch.object(w,'rollback',side_effect=recovered),\
             patch.object(w,'stop_orphan_capture',side_effect=lambda *_:order.append('capture_stop')),\
             patch.object(w,'finalize_capture',side_effect=lambda *_:order.append('verify')):
            w.tick()
        self.assertEqual(['gtfs_confirmed','unlock','capture_stop','verify'],order)
        self.assertEqual('complete',state['phase'])

    def test_fresh_heartbeat_cannot_hide_expired_898_from_independent_cron(self):
        from unittest.mock import Mock
        state={'phase':'its','deadline':1600,'activated':1000,'heartbeat':1090}
        order=[];lock=Mock();lock.close.side_effect=lambda:order.append('unlock')
        with patch.object(w,'load',return_value=state),patch.object(w.time,'time',return_value=1090),\
             patch.object(w,'take_lock',return_value=lock),patch.object(w,'rollback',side_effect=lambda s,r:order.append(r)),\
             patch.object(w,'stop_orphan_capture',side_effect=lambda *_:order.append('capture_stop')),\
             patch.object(w,'finalize_capture',side_effect=lambda *_:order.append('verify')):
            w.tick()
        self.assertEqual(['gate_898_unproven','unlock','capture_stop','verify'],order)

    def test_only_timely_persisted_proof_prevents_gate_watchdog(self):
        for proof,expired in ((89.9,False),(90,True),(92,True),(-1,True),(True,True)):
            with self.subTest(proof=proof),patch.object(w,'load',return_value={'phase':'its','deadline':1600,'activated':1000,'heartbeat':1091,'gate898_proven_elapsed':proof}),\
                 patch.object(w.time,'time',return_value=1091),patch.object(w,'take_lock'),patch.object(w,'rollback') as rollback,\
                 patch.object(w,'stop_orphan_capture'),patch.object(w,'finalize_capture'):
                w.tick();self.assertEqual(expired,rollback.called)

    def test_deadline_exception_cancels_subprocess_instead_of_waiting_for_result(self):
        from unittest.mock import MagicMock
        child=MagicMock();child.communicate.side_effect=w.SafetyFailure('gate_898_unproven')
        with patch.object(w.subprocess,'Popen') as popen:
            popen.return_value.__enter__.return_value=child
            with self.assertRaises(w.SafetyFailure):w.run(['docker','logs','container'])
        child.kill.assert_called_once()

unittest.main()

unittest.main()
