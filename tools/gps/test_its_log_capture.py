import datetime as dt
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import its_log_capture as c

CID = 'a' * 64
NEW = 'b' * 64
FINAL = 'c' * 64
ACQ = 'd' * 32
SESSION = 'e' * 32
BASE = dt.datetime(2026, 10, 24, 21, 0, tzinfo=dt.timezone.utc)


def line(family, values, tick=0):
    prefix = next(p for p, name in c.PREFIXES.items() if name == family)
    text = json.dumps(values, separators=(',', ':')) if family == 'flow' else ' '.join(
        key + '=' + (json.dumps(v, separators=(',', ':')) if isinstance(v, dict) else '(null)' if v is None else str(v))
        for key, v in values.items())
    stamp = (BASE + dt.timedelta(seconds=tick)).isoformat().replace('+00:00', 'Z')
    return (stamp + '      ' + prefix + text + '\n').encode()


def http(acquisition=ACQ, **change):
    return dict(acquisition=acquisition, minute_utc='10/24/2026 21:00:00 +00:00', cursor_present='False',
                http_status=200, completed='True', phase='parse', headers_ms=7000,
                body_ms=500, parse_ms=100, bytes=3000, **change)


def timing():
    return dict(acquisition=ACQ, status='Sucesso', gate_ms=0, interval_wait_ms=0, pending_wait_ms=0,
                normalize_ms=2400, rejected_reasons={'physical_stale': 15}, age_at_start_p50=14,
                age_at_finish_p50=62, expired_at_finish=1)


def summary():
    return dict(status='Sucesso', reason=None, pages=6, windows=3, records=18000, rejected=15,
                duration_ms=48000, vehicles=2400, recent40=0, recent60=900, age_p50=62, age_p90=78,
                zirix_available=1500, conecta_available=1700, zirix_selected=1000,
                conecta_selected=1400, zirix_exclusive=100, conecta_exclusive=400)


def hybrid():
    values = {k: 0 for k in c.SCHEMAS['hybrid']}
    values.update(primary_status='Sucesso', support_status='Sucesso', status='Sucesso', reason=None,
                  its_input=2400, gtfs_input=1400, selected=2500, selected_its=1100, selected_gtfs=1400, duration_ms=48700)
    return values


def flow(stage, generation=9):
    values = {k: 0 for k in c.SCHEMAS['flow']}
    values.update(session=SESSION, generation=generation, stage=stage, line='898', source='GTFSRT_BUS',
                  utc=BASE.isoformat(), count=12, physical_since_start=12, linked_complete_new=1 if stage == 'ack' else 0)
    return values


class Clock:
    def __init__(self): self.value = BASE.timestamp()
    def __call__(self): return self.value
    def advance(self, seconds): self.value += seconds


class FakeStream:
    def __init__(self, lines=(), done=False, code=0, error=None):
        self.lines = list(lines); self.done = done; self.code = code; self.error = error
        self.bytes = 0; self.closed = False
    def drain(self):
        lines, self.lines = self.lines, []
        self.bytes += sum(map(len, lines))
        return lines, self.error, self.done, self.code, self.bytes
    def close(self): self.closed = True


class FakeDocker:
    def __init__(self, container=CID):
        self.container = container; self.running = True; self.plans = {}; self.calls = []
    def discover(self):
        self.calls.append(('inspect',))
        return {'id': self.container, 'running': self.running} if self.container else None
    def open(self, container, since, limits, remaining):
        self.calls.append(('logs', container, since))
        return self.plans[container].pop(0)


class CaptureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.directory = pathlib.Path(self.temp.name) / 'capture'
        self.clock = Clock()
    def archive(self, limits=c.Limits()):
        value = c.Archive(self.directory, SESSION, limits, self.clock)
        self.addCleanup(lambda: value.close(False) if not value.closed else None)
        return value
    def records(self):
        return [json.loads(x) for x in (self.directory / 'aggregates.jsonl').read_text(encoding='utf-8').splitlines()]

    def test_pages_windows_48_seconds_preserve_phase_metrics_and_generation(self):
        archive = self.archive()
        for i in range(6):
            minute = (BASE - dt.timedelta(minutes=i // 2)).isoformat()
            h = http(); h.update(minute_utc=minute, cursor_present='True' if i % 2 else 'False')
            archive.ingest(CID, line('http_page', h, i * 8))
            archive.ingest(CID, line('normalized_page', dict(acquisition=ACQ, window=i // 2,
                minute_utc=minute, page=i + 1, records=3000, providers={'ZIRIX': 1000, 'CONECTA': 2000},
                rejections={'physical_stale': i}, normalize_ms=400), i * 8 + 1))
        archive.ingest(CID, line('timing', timing(), 48))
        archive.ingest(CID, line('acquisition', summary(), 49))
        archive.ingest(CID, line('hybrid', hybrid(), 50))
        for i, stage in enumerate(('selected', 'cas_accepted', 'rest_returned', 'signalr_completed', 'ack')):
            archive.ingest(CID, line('flow', flow(stage), 51 + i))
        archive.close()
        events = [x for x in self.records() if x['kind'] == 'aggregate']
        pages = [x['fields'] for x in events if x['family'] == 'http_page']
        normalized = [x['fields'] for x in events if x['family'] == 'normalized_page']
        self.assertEqual(6, len(pages)); self.assertEqual({0, 1, 2}, {x['window'] for x in normalized})
        self.assertEqual(48000, sum(x['headers_ms'] + x['body_ms'] + x['parse_ms'] for x in pages)
                         + sum(x['normalize_ms'] for x in normalized))
        self.assertEqual(15, sum(x['rejections']['physical_stale'] for x in normalized))
        self.assertEqual(9, events[-1]['fields']['generation'])
        self.assertEqual(ACQ, events[-1]['acquisition'])
        self.assertEqual('same_container_serial_order', events[-1]['link_basis'])
        self.assertTrue(c.verify(self.directory)['evidence_complete'])
        archive.status({'state':'closed'})
        status=json.loads((self.directory/'health.json').read_text())
        self.assertEqual(6,status['families']['http_page']);self.assertEqual(5,status['families']['flow'])

    def test_pending_wait_interval_and_gate_are_separate_from_http(self):
        archive = self.archive(); values = timing(); values.update(gate_ms=10, interval_wait_ms=800, pending_wait_ms=2000)
        archive.ingest(CID, line('provider_wait', dict(acquisition=ACQ, window=0, minute_utc=BASE.isoformat(), attempt=1, discarded_pending_rows=10)))
        archive.ingest(CID, line('timing', values, 2)); archive.close()
        values = self.records()[-2]['fields']
        self.assertEqual((10, 800, 2000, 2400), tuple(values[k] for k in ('gate_ms', 'interval_wait_ms', 'pending_wait_ms', 'normalize_ms')))

    def test_secret_fields_and_unrelated_payloads_never_reach_disk(self):
        archive = self.archive()
        values = http(); values.update(cursor='SECRET_CURSOR', token='SECRET_TOKEN', url='https://provider.invalid/?token=SECRET_URL')
        archive.ingest(CID, line('http_page', values))
        values = flow('selected'); values.update(vehicle='SECRET_ID', latitude=-22.987654, payload={'token': 'SECRET_PAYLOAD'})
        archive.ingest(CID, line('flow', values, 1))
        archive.ingest(CID, b'2026-10-24T21:00:02Z unrelated SECRET_RAW\n')
        bad = timing(); bad['rejected_reasons'] = {'SECRET_KEY': 1}
        archive.ingest(CID, line('timing', bad, 3)); archive.close()
        text = (self.directory / 'aggregates.jsonl').read_text(encoding='utf-8')
        self.assertNotIn('SECRET', text); self.assertNotIn('provider.invalid', text); self.assertNotIn('-22.987654', text)
        self.assertFalse(c.verify(self.directory)['archive_complete'])

    def test_overlap_deduplicates_without_corrupting_later_generation_binding(self):
        archive = self.archive(); one = line('timing', timing())
        archive.ingest(CID, one); archive.ingest(CID, line('hybrid', hybrid(), 1))
        archive.ingest(CID, line('flow', flow('selected'), 2)); archive.ingest(CID, one)
        archive.ingest(CID, line('flow', flow('ack'), 3)); archive.close()
        self.assertEqual(1, archive.duplicates)
        self.assertEqual(ACQ, self.records()[-2]['acquisition'])
        self.assertEqual(4, len([x for x in self.records() if x['kind'] == 'aggregate']))

    def test_recreation_keeps_old_stream_records_after_container_disappears(self):
        archive = self.archive(); docker = FakeDocker()
        old = FakeStream([line('http_page', http())]); new = FakeStream([line('timing', timing(), 2)])
        docker.plans = {CID: [old], NEW: [new]}; engine = c.Engine(archive, docker, self.clock)
        engine.step(); engine.step()
        old.lines = [line('normalized_page', dict(acquisition=ACQ, window=0, minute_utc=BASE.isoformat(), page=1,
            records=1, providers={'ZIRIX': 1}, rejections={}, normalize_ms=2), 1)]
        old.done = True; docker.container = NEW
        engine.step(); engine.step(); engine.close(); archive.close()
        events = [x for x in self.records() if x['kind'] == 'aggregate']
        self.assertEqual([CID, CID, NEW], [x['container'] for x in events])
        self.assertTrue(old.closed); self.assertTrue(c.verify(self.directory)['archive_complete'])
        self.assertFalse(any(call[0] not in ('inspect', 'logs') for call in docker.calls))

    def test_temporary_loss_reconnects_with_overlap_but_never_claims_complete(self):
        archive = self.archive(); docker = FakeDocker(); raw = line('http_page', http())
        first = FakeStream([raw], done=True, code=1)
        second = FakeStream([raw, line('timing', timing(), 3)])
        docker.plans = {CID: [first, second]}; engine = c.Engine(archive, docker, self.clock)
        engine.step(); engine.step(); engine.step()
        self.assertEqual(2, len([x for x in docker.calls if x[0] == 'logs']))
        self.assertEqual(1, archive.duplicates)
        self.assertEqual('log_stream_ended', engine.failure)
        self.assertNotEqual(engine.since, [x for x in docker.calls if x[0] == 'logs'][-1][2])
        engine.close(); archive.close()
        self.assertFalse(c.verify(self.directory)['archive_complete'])
        self.assertEqual(2, len([x for x in self.records() if x['kind'] == 'aggregate']))

    def test_abrupt_collector_exit_retains_fsynced_prefix_and_reports_unclean_close(self):
        module = str(pathlib.Path(c.__file__).parent)
        code = 'import sys,os;sys.path.insert(0,' + repr(module) + ');import its_log_capture as c;a=c.Archive(sys.argv[1],'+repr(SESSION)+');a.ingest('+repr(CID)+','+repr(line('http_page', http()))+');os._exit(23)'
        result = subprocess.run([sys.executable, '-B', '-c', code, str(self.directory)], capture_output=True, timeout=5)
        self.assertEqual(23, result.returncode)
        result = c.verify(self.directory)
        self.assertFalse(result['archive_complete']); self.assertEqual('unclean_close', result['reason'])
        self.assertEqual(2, result['durable_prefix_records'])

    def test_budget_reserves_footer_and_never_claims_truncated_evidence_complete(self):
        archive = self.archive(c.Limits(max_lines=4))
        for tick in range(10): archive.ingest(CID, line('http_page', http(), tick))
        archive.close(); result = c.verify(self.directory)
        self.assertTrue(result['integrity_valid']); self.assertTrue(result['truncated']); self.assertFalse(result['archive_complete'])
        self.assertEqual(4, len(self.records()))
        self.assertLessEqual((self.directory / 'aggregates.jsonl').stat().st_size, archive.limits.max_bytes)

    def test_byte_budget_is_independent_of_line_budget(self):
        archive = self.archive(c.Limits(max_bytes=4096))
        for tick in range(50): archive.ingest(CID, line('http_page', http(), tick))
        archive.close(); self.assertTrue(archive.truncated)
        self.assertLessEqual((self.directory / 'aggregates.jsonl').stat().st_size, 4096)
        self.assertTrue(c.verify(self.directory)['integrity_valid'])

    def test_rollback_during_acquisition_preserves_partial_without_waiting_for_timing(self):
        archive = self.archive(); docker = FakeDocker()
        active = FakeStream([line('http_page', http())]); rollback = FakeStream()
        docker.plans = {CID: [active], FINAL: [rollback]}; engine = c.Engine(archive, docker, self.clock)
        engine.step(); engine.step()
        # The API is already replaced before capture observes retirement. No control call or lock.
        docker.container = FINAL; active.done = True
        engine.step(); engine.step(); engine.close(); archive.close()
        result = c.verify(self.directory)
        self.assertTrue(result['archive_complete']); self.assertFalse(result['evidence_complete'])
        self.assertEqual(1, result['acquisitions_without_timing'])
        self.assertTrue(any(x.get('family') == 'http_page' for x in self.records()))
        self.assertTrue(all(x[0] in ('inspect', 'logs') for x in docker.calls))

    def test_private_exclusive_archive_cannot_overwrite_previous_run(self):
        archive = self.archive(); archive.close()
        with self.assertRaises(FileExistsError): c.Archive(self.directory, SESSION)
        with patch.object(c.os, 'open', wraps=c.os.open) as opened:
            archive.status({'state': 'closed'})
            self.assertTrue(any(call.args[-1] == 0o600 for call in opened.call_args_list))
        if os.name == 'posix':
            self.assertEqual(0o700, self.directory.stat().st_mode & 0o777)
            self.assertEqual(0o600, (self.directory/'aggregates.jsonl').stat().st_mode & 0o777)

    def test_real_log_child_exit_is_observable_and_flushable(self):
        command = [sys.executable, '-B', '-c', 'import sys;sys.stdout.buffer.write('+repr(line('timing', timing()))+');sys.stdout.flush();sys.exit(7)']
        stream = c.DockerStream(command, 16384, 100000)
        try:
            self.assertTrue(stream.done.wait(3))
            lines, error, done, code, size = stream.drain()
            self.assertEqual(1, len(lines)); self.assertIsNone(error); self.assertTrue(done); self.assertEqual(7, code)
            self.assertEqual(len(lines[0]), size)
        finally: stream.close()

    def test_reader_limits_reject_oversized_input_without_archiving_payload(self):
        for cap,budget,data,expected in ((1024,5000,b'x'*1025+b'\n','input_line_limit'),
                                         (16384,1024,b'x'*1200+b'\n','input_budget')):
            with self.subTest(expected=expected):
                stream=c.DockerStream([sys.executable,'-B','-c','import sys;sys.stdout.buffer.write('+repr(data)+')'],cap,budget)
                try:
                    self.assertTrue(stream.done.wait(3))
                    lines,error,done,_,_=stream.drain()
                    self.assertEqual([],lines);self.assertEqual(expected,error);self.assertTrue(done)
                finally:stream.close()

    def test_input_budget_is_shared_across_streams_and_reconnections(self):
        budget=c.InputBudget(1024)
        command=[sys.executable,'-B','-c',"import sys;sys.stdout.buffer.write(b'x'*799+b'\\n')"]
        first=c.DockerStream(command,16384,5000,budget)
        try:
            self.assertTrue(first.done.wait(3));self.assertIsNone(first.drain()[1])
        finally:first.close()
        second=c.DockerStream(command,16384,5000,budget)
        try:
            self.assertTrue(second.done.wait(3));lines,error,_,_,_=second.drain()
            self.assertEqual([],lines);self.assertEqual('input_budget',error);self.assertEqual(1600,budget.used)
        finally:second.close()

    def test_transport_only_inspects_metadata_and_follows_immutable_id(self):
        from types import SimpleNamespace
        metadata={'id':CID,'image':c.IMAGE,'running':True,'project':'noponto','service':'api'}
        with patch.object(c.subprocess,'run',return_value=SimpleNamespace(returncode=0,stdout=json.dumps(metadata).encode())) as run,patch.object(c,'DockerStream') as stream:
            transport=c.DockerTransport();self.assertEqual(metadata,transport.discover())
            transport.open(CID,'2026-10-24T21:00:00Z',c.Limits(),1024)
            args=run.call_args.args[0];self.assertEqual(['docker','inspect','--format'],args[:3]);self.assertNotIn('.Env',args[3])
            command=stream.call_args.args[0];self.assertEqual(['docker','logs','--follow','--timestamps'],command[:4]);self.assertEqual(CID,command[-1]);self.assertNotIn('--tail',command)
            metadata['image']='wrong'
            run.return_value.stdout=json.dumps(metadata).encode()
            with self.assertRaises(RuntimeError):transport.discover()

    def test_malformed_recognized_schema_marks_gap_without_persisting_raw_text(self):
        archive=self.archive()
        bad=flow('selected');bad['utc']=None
        archive.ingest(CID,line('flow',bad));archive.close()
        self.assertEqual({'schema_rejected':1},archive.gaps)
        self.assertFalse(c.verify(self.directory)['archive_complete'])
        self.assertEqual([], [x for x in self.records() if x['kind']=='aggregate'])

    def test_missing_page_is_detected_by_success_summary_reconciliation(self):
        archive=self.archive()
        archive.ingest(CID,line('http_page',http()))
        archive.ingest(CID,line('timing',timing(),48))
        archive.ingest(CID,line('acquisition',summary(),49))
        archive.close()
        self.assertEqual(1,archive.gaps['acquisition_log_mismatch'])
        self.assertFalse(c.verify(self.directory)['evidence_complete'])

    def test_transport_failure_reason_is_retained_as_a_closed_enum(self):
        archive=self.archive();value=summary();value.update(status='Falha',reason='http_unavailable')
        archive.ingest(CID,line('acquisition',value));archive.close()
        record=next(x for x in self.records() if x['kind']=='aggregate')
        self.assertEqual('http_unavailable',record['fields']['reason'])
        self.assertIsNone(record['acquisition']);self.assertEqual('unproven',record['link_basis'])

    def test_integrity_verifier_rejects_modified_prefix(self):
        archive=self.archive();archive.ingest(CID,line('timing',timing()));archive.close()
        path=self.directory/'aggregates.jsonl';data=path.read_bytes()
        path.write_bytes(data.replace(b'gate_ms":0.0',b'gate_ms":1.0'))
        self.assertNotEqual(data,path.read_bytes())
        result=c.verify(self.directory)
        self.assertFalse(result['integrity_valid']);self.assertFalse(result['archive_complete']);self.assertFalse(result['evidence_complete'])


if __name__ == '__main__': unittest.main()
