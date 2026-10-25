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
        with patch.object(w.time,'time',return_value=1090):
            current=sample();current.update(flow=[frame],wire={**current['wire'],**wire})
            self.assertIsNone(w.decision(current,sample(),set(),0,0,None,{'activated':1000})[0])
        with patch.object(w.time,'time',return_value=1090.001):
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
        contract={'session':'a'*32,'source_sha256':'b'*64}
        good={**contract,'pid':123,'image':w.APPROVED_IMAGE,'heartbeat':1000,
              'state':'running','ready':True,'failure':None,'gap_count':0,'truncated':False,
              'container_id':'c'*64,'records':4,'bytes':500}
        proc=types.SimpleNamespace(pid=123,poll=lambda:None)
        with tempfile.TemporaryDirectory() as tmp,patch.object(w,'ROOT',Path(tmp)),patch.object(w,'capture_process',proc),patch.object(w,'capture_contract',contract),patch.object(w.time,'time',return_value=1005):
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
        with tempfile.TemporaryDirectory() as tmp,patch.object(w,'ROOT',Path(tmp)),patch.object(w,'save'),patch.object(w.subprocess,'Popen',return_value=types.SimpleNamespace(pid=123)) as popen,patch.object(w,'capture_contract'),patch.object(w,'capture_process'):
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

    def test_supervisor_rolls_back_before_stopping_capture(self):
        from unittest.mock import Mock
        # Abort immediately after activation; no clocks, threads, sockets, Docker or /proc I/O.
        # Exercise real supervisor control flow with a baseline ending on its second clock check.
        order=[];lock=Mock();thread=Mock();baseline=sample()
        baseline['capture']={'healthy':True};baseline['wire'].update(messages=7,lines={x:{} for x in w.LINES})
        with patch.object(w.os,'getpgrp',create=True,return_value=123),patch.object(w.os,'getpid',return_value=123),patch.object(Path,'read_text',return_value=' '.join(['0']*22)),patch.object(w,'take_lock',return_value=lock),patch.object(w,'save'),patch.object(w,'record'),patch.object(w.threading,'Thread',return_value=thread),patch.object(w.signal,'signal'),patch.object(w,'validate'),patch.object(w,'start_capture'),patch.object(w,'capture_health',return_value={'healthy':True}),patch.object(w,'inspect',return_value=[{'Id':'c'*64}]),patch.object(w,'sample',return_value=baseline),patch.object(w.time,'monotonic',side_effect=[0,0,1,121]),patch.object(w.stop,'wait'),patch.object(w.stop,'is_set',return_value=False),patch.object(w,'switch',side_effect=RuntimeError('controlled_activation_failure')),patch.object(w,'rollback',side_effect=lambda *_:order.append('rollback')),patch.object(w,'stop_capture',side_effect=lambda:order.append('capture_stop')):
            w.supervise({'image':w.APPROVED_IMAGE})
        w.stop.clear()
        self.assertEqual(['rollback','capture_stop'],order);lock.close.assert_called_once()

unittest.main()
