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

unittest.main()
