"""Passive, bounded aggregate archive for a manually authorized GPS window.

Only docker inspect/logs are used. No provider requests, container control or deploy lock.
The stream is archived while its container exists, not fetched after rollback.
"""
import argparse
import datetime as dt
import hashlib
import json
import math
import os
import pathlib
import queue
import re
import signal
import subprocess
import threading
import time
import uuid
from dataclasses import dataclass

IMAGE = 'ghcr.io/guilhermedesales/noponto-api@sha256:5bea032825319c0cd8e243806f8a49afd26741fc643882aa04d958b5663e463b'
STAMP = r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,9})?Z'
GUID = re.compile(r'[a-f0-9]{32}\Z')
CONTAINER = re.compile(r'[a-f0-9]{64}\Z')
REASONS = {'scope', 'service', 'physical_invalid', 'physical_future', 'physical_stale', 'payload', 'provider', 'identity', 'speed'}
STAGES = {'selected', 'polling', 'age_valid', 'timestamp_new', 'matched', 'cas_accepted',
          'cas_rejected_timestamp', 'cas_infrastructure_failure', 'signalr_completed', 'rest_returned', 'ack'}
FAILURES = {'no_valid_records', 'previous_io_pending', 'retry_after', 'acquisition_limit', 'invalid_response',
            'provider_incomplete', 'record_limit', 'invalid_cursor', 'acquisition_budget', 'request_timeout',
            'response_limit', 'read_error', 'http_error', 'http_unavailable', 'hybrid_primary_unavailable',
            'hybrid_support_unavailable', 'hybrid_no_valid_observations', 'primary_in_recovery'}
PREFIXES = {'ITS HTTP page ': 'http_page', 'ITS normalized page ': 'normalized_page',
            'ITS timing ': 'timing', 'ITS provider wait ': 'provider_wait',
            'ITS acquisition ': 'acquisition', 'BUS hybrid: ': 'hybrid', 'GPS flow: ': 'flow'}
LOG_ENVELOPE = re.compile(('^(' + STAMP + r')\s+').encode())
AGGREGATE_PREFIXES = tuple(prefix.encode() for prefix in PREFIXES)
TERMINAL_STREAM_FAILURES = {'input_queue_overflow', 'input_line_limit',
                            'log_reader_stalled', 'log_reader_failure'}


class InvalidRecord(ValueError):
    """No source text is included in this exception."""


def number(value):
    if isinstance(value, bool):
        raise InvalidRecord('numeric_schema')
    value = float(value)
    if not math.isfinite(value) or abs(value) > 1e12:
        raise InvalidRecord('numeric_schema')
    return value


def integer(value):
    result = number(value)
    if result < 0 or result != int(result):
        raise InvalidRecord('integer_schema')
    return int(result)


def optional_number(value):
    return None if value in (None, '(null)') else number(value)


def enum(*allowed):
    def check(value):
        if value not in allowed:
            raise InvalidRecord('enum_schema')
        return value
    return check


def guid(value):
    if not isinstance(value, str) or not GUID.fullmatch(value):
        raise InvalidRecord('guid_schema')
    return value


def utc(value):
    if not isinstance(value,str):raise InvalidRecord('utc_schema')
    try:
        value = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    except ValueError:
        value = dt.datetime.strptime(value, '%m/%d/%Y %H:%M:%S %z')
    if value.tzinfo is None:
        raise InvalidRecord('timezone_missing')
    return value.astimezone(dt.timezone.utc).isoformat()


def reason(value):
    if value in (None, '(null)'):
        return None
    if value not in FAILURES and not re.fullmatch(r'http_[1-5]\d\d', value):
        raise InvalidRecord('reason_schema')
    return value


def counts(keys):
    def check(value):
        obj = json.loads(value) if isinstance(value, str) else value
        if not isinstance(obj, dict) or not set(obj) <= keys:
            raise InvalidRecord('counter_schema')
        return {key: integer(count) for key, count in obj.items()}
    return check


STATUS = enum('Sucesso', 'Vazio', 'Parcial', 'Falha')
SCHEMAS = {
    'http_page': dict(acquisition=guid, minute_utc=utc, cursor_present=enum('True', 'False', 'true', 'false'),
                     http_status=optional_number, completed=enum('True', 'False', 'true', 'false'),
                     phase=enum('headers', 'body', 'parse'), headers_ms=number, body_ms=number, parse_ms=number, bytes=integer),
    'normalized_page': dict(acquisition=guid, window=integer, minute_utc=utc, page=integer, records=integer,
                            providers=counts({'ZIRIX', 'CONECTA', 'OTHER'}), rejections=counts(REASONS), normalize_ms=number),
    'provider_wait': dict(acquisition=guid, window=integer, minute_utc=utc, attempt=integer, discarded_pending_rows=integer),
    'timing': dict(acquisition=guid, status=STATUS, gate_ms=number, interval_wait_ms=number, pending_wait_ms=number,
                   normalize_ms=number, rejected_reasons=counts(REASONS), age_at_start_p50=optional_number,
                   age_at_finish_p50=optional_number, expired_at_finish=integer),
    'acquisition': dict(status=STATUS, reason=reason, pages=integer, windows=integer, records=integer, rejected=integer,
                        duration_ms=number, vehicles=integer, recent40=integer, recent60=integer,
                        age_p50=optional_number, age_p90=optional_number, zirix_available=integer, conecta_available=integer,
                        zirix_selected=integer, conecta_selected=integer, zirix_exclusive=integer, conecta_exclusive=integer),
    'hybrid': dict(primary_status=STATUS, support_status=STATUS, status=STATUS, reason=reason,
                   **{key: integer for key in ('its_input', 'gtfs_input', 'selected', 'selected_its', 'selected_gtfs',
                       'shared', 'ties', 'coordinate_conflicts', 'line_conflicts', 'direction_conflicts',
                       'invalid', 'recent40', 'recent60', 'recent180')}, duration_ms=number),
    'flow': dict(session=guid, generation=integer, stage=enum(*STAGES),
                 line=enum('838', '898', '006', '457', '483', '355', '805'),
                 source=enum('ITS_CURRENT', 'GTFSRT_BUS', 'OTHER'), utc=utc, count=integer,
                 physical_since_start=integer, linked_complete_new=integer,
                 age_p50=optional_number, age_p90=number, recent40=integer, recent60=integer, recent180=integer,
                 age_at_acquisition_start_p50=optional_number, since_body_received_p50=optional_number, since_ready_ms=number)
}


def sanitize_line(raw):
    """Closed schemas: never persist a raw line or unknown key/value, even on failure."""
    try:
        text = raw.decode('utf-8') if isinstance(raw, bytes) else raw
        match = re.fullmatch('(' + STAMP + r')\s+([^\r\n]*)\r?\n?', text)
        if not match:
            return None
        stamp, message = match.groups()
        for prefix, family in PREFIXES.items():
            if not message.startswith(prefix):
                continue
            utc(stamp)  # Validate the Docker timestamp, retaining its original nanoseconds.
            body = message[len(prefix):]
            if family == 'flow':
                values = json.loads(body)
                if not isinstance(values, dict):
                    raise InvalidRecord('flow_schema')
            else:
                keys = list(re.finditer(r'(?:^|\s)([a-zA-Z_][a-zA-Z_0-9]*)=', body))
                values = {}
                for i, item in enumerate(keys):
                    key = item.group(1)
                    if key in values:
                        raise InvalidRecord('duplicate_field')
                    values[key] = body[item.end():keys[i + 1].start() if i + 1 < len(keys) else len(body)].strip().rstrip(',;')
            schema = SCHEMAS[family]
            if not set(schema) <= set(values):
                raise InvalidRecord('missing_field')
            return {'timestamp': stamp, 'family': family,
                    'fields': {key: convert(values[key]) for key, convert in schema.items()}}
        return None
    except (ValueError, TypeError, KeyError, OverflowError, UnicodeError):
        raise InvalidRecord('aggregate_schema') from None


@dataclass(frozen=True)
class Limits:
    max_bytes: int = 8 * 1024 * 1024
    max_lines: int = 12000
    max_input_bytes: int = 64 * 1024 * 1024
    max_line_bytes: int = 16384
    max_containers: int = 4

    def __post_init__(self):
        if not (4096 <= self.max_bytes <= 16 * 1024 * 1024 and 4 <= self.max_lines <= 24000
                and 1024 <= self.max_input_bytes <= 128 * 1024 * 1024
                and 1024 <= self.max_line_bytes <= 16384 and 1 <= self.max_containers <= 4):
            raise ValueError('invalid_capture_limits')


class Archive:
    FOOTER_RESERVE = 1024

    def __init__(self, directory, session, limits=Limits(), now=time.time):
        self.root = pathlib.Path(directory)
        self.root.mkdir(mode=0o700)  # New exclusive directory: never overwrite prior evidence.
        os.chmod(self.root, 0o700)
        self.session, self.limits, self.now = guid(session), limits, now
        fd = os.open(self.root / 'aggregates.jsonl', os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        self.file = os.fdopen(fd, 'wb', buffering=0)
        self.digest = hashlib.sha256()
        self.lines = self.bytes = self.duplicates = self.input_bytes = 0
        self.gaps = {}
        self.families = {}
        self.seen = set()
        self.contexts = {}
        self.closed = self.truncated = False
        self._write({'kind': 'begin', 'schema': 1, 'session': session, 'at': now()})

    def _write(self, value):
        data = (json.dumps(value, separators=(',', ':'), allow_nan=False) + '\n').encode()
        if self.file.write(data)!=len(data):raise OSError('archive_short_write')
        os.fsync(self.file.fileno())
        self.digest.update(data)
        self.bytes += len(data)
        self.lines += 1

    def write(self, value):
        size = len((json.dumps(value, separators=(',', ':'), allow_nan=False) + '\n').encode())
        if self.truncated or self.lines >= self.limits.max_lines - 1 or self.bytes + size > self.limits.max_bytes - self.FOOTER_RESERVE:
            self.truncated = True
            return False
        self._write(value)
        return True

    def gap(self, code, container=None):
        # Codes originate only in this module, never provider/Docker exception messages.
        if code not in {'schema_rejected', 'discovery_failed', 'input_budget', 'input_line_limit',
                        'input_queue_overflow', 'log_reader_stalled', 'log_reader_failure',
                        'log_stream_ended', 'container_budget', 'stream_budget', 'stream_start_failed',
                        'retired_stream_unclosed', 'capture_duration_or_budget', 'acquisition_log_mismatch'}:
            code = 'unknown_capture_failure'
        self.gaps[code] = self.gaps.get(code, 0) + 1
        for context in self.contexts.values():
            context['uncertain'] = True
        self.write({'kind': 'gap', 'at': self.now(), 'reason': code, 'container': container})

    def ingest(self, container, raw):
        if not CONTAINER.fullmatch(container):
            raise ValueError('invalid_container_id')
        try:
            event = sanitize_line(raw)
        except InvalidRecord:
            self.gap('schema_rejected', container)
            return
        if event is None:
            return
        canonical = json.dumps(event, sort_keys=True, separators=(',', ':')).encode()
        key = (container, event['timestamp'], hashlib.sha256(canonical).digest())
        if key in self.seen:
            self.duplicates += 1
            return
        if self.truncated:
            return
        c = self.contexts.setdefault(container, {'uncertain': bool(self.gaps), 'timing': None, 'composition': None, 'generations': {}, 'stats': {}, 'unassociated': 0})
        fields, family = event['fields'], event['family']
        association = None
        if 'acquisition' in fields:
            association = fields['acquisition']
            # Keep the bounded archive's acquisition counters until close: HTTP tasks can
            # finish logging after timing/summary, especially on cancellation/timeouts.
            stats=c['stats'].setdefault(association,{'http':0,'pending':0,'records':0,'rejected':0,'pages':set(),'windows':set(),'timing':False,'summary':None})
            if family=='http_page' and fields['completed'].lower()=='true':stats['http']+=1
            if family=='provider_wait':stats['pending']+=1
            if family=='normalized_page':
                stats['records']+=fields['records'];stats['rejected']+=sum(fields['rejections'].values())
                stats['pages'].add(fields['page']);stats['windows'].add(fields['window'])
            if family == 'timing':
                c['timing'] = association
                stats['timing'] = True
        elif family == 'acquisition':
            association = c['timing'] if not c['uncertain'] else None
            stats=c['stats'].get(association)
            if stats is None:
                # No ID in this log contract: never guess an association from nearby pages.
                c['unassociated']+=1
            elif stats['summary'] is not None and stats['summary']!=fields:
                self.gap('acquisition_log_mismatch',container)
            else:stats['summary']=fields
        elif family == 'hybrid':
            association = c['timing'] if not c['uncertain'] else None
            c['composition'] = association
            c['timing'] = None
        elif family == 'flow':
            g = (fields['session'], fields['generation'])
            if fields['stage'] == 'selected' and g not in c['generations']:
                if len(c['generations']) >= 16:
                    del c['generations'][next(iter(c['generations']))]
                c['generations'][g] = c['composition'] if not c['uncertain'] else None
            association = c['generations'].get(g)
        explicit = 'acquisition' in fields
        record = {'kind': 'aggregate', 'container': container, **event, 'acquisition': association,
                  'link_basis': 'backend_explicit' if explicit else 'same_container_serial_order' if association else 'unproven'}
        if self.write(record):
            self.seen.add(key)
            self.families[family] = self.families.get(family, 0) + 1

    def status(self, value):
        payload = {**value, 'session': self.session, 'heartbeat': self.now(), 'records': self.lines,
                   'bytes': self.bytes, 'duplicates': self.duplicates, 'input_bytes': self.input_bytes,
                   'gap_count': sum(self.gaps.values()), 'truncated': self.truncated, 'families': self.families}
        data = json.dumps(payload, allow_nan=False).encode()
        if len(data) > 4096:
            raise ValueError('status_limit')
        tmp = self.root / 'health.tmp'
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, 'wb') as f:
            f.write(data); f.flush(); os.fsync(f.fileno())
        os.replace(tmp, self.root / 'health.json')

    def close(self, clean=True):
        if self.closed:
            return
        missing_timing=missing_summary=opened=unassociated=0
        for container,c in self.contexts.items():
            unassociated+=c['unassociated']
            for stats in c['stats'].values():
                summary=stats['summary']
                missing_timing+=not stats['timing'];missing_summary+=summary is None
                opened+=not stats['timing'] or summary is None
                if summary is not None and summary['status'] in ('Sucesso','Vazio'):
                    if (stats['http']!=summary['pages'] or len(stats['pages'])+stats['pending']!=summary['pages']
                            or stats['records']!=summary['records'] or stats['rejected']!=summary['rejected']
                            or len(stats['windows'])!=summary['windows']):
                        self.gap('acquisition_log_mismatch',container)
        opened+=unassociated
        footer = {'kind': 'end', 'at': self.now(), 'session': self.session,
                  'content_sha256': self.digest.hexdigest(), 'records_before_footer': self.lines,
                  'archive_complete': clean and not self.gaps and not self.truncated,
                  'evidence_complete': clean and not self.gaps and not self.truncated and opened == 0,
                  'truncated': self.truncated, 'gap_count': sum(self.gaps.values()), 'acquisitions_without_timing': opened,
                  'missing_timing':missing_timing,'missing_summary':missing_summary,'unassociated_summaries':unassociated}
        self._write(footer)
        self.file.close()
        self.closed = True


def verify(directory):
    """Read-only post-run verification. No footer means an unclean/abrupt termination."""
    path = pathlib.Path(directory) / 'aggregates.jsonl'
    digest = hashlib.sha256()
    count = 0
    session = None
    try:
        if path.stat().st_size > 16 * 1024 * 1024:
            return {'archive_complete': False, 'reason': 'archive_limit'}
        with path.open('rb') as f:
            for line in f:
                if not line.endswith(b'\n') or len(line) > 16384:
                    return {'archive_complete': False, 'reason': 'incomplete_or_oversized_line'}
                value = json.loads(line)
                if not isinstance(value,dict):raise ValueError('archive_schema')
                if count==0:
                    if value.get('kind')!='begin' or value.get('schema')!=1:raise ValueError('archive_header')
                    session=guid(value.get('session'))
                if value.get('kind') == 'end':
                    valid = value.get('session') == session and value.get('content_sha256') == digest.hexdigest() and value.get('records_before_footer') == count and not f.read(1)
                    return {**value, 'archive_complete': valid and bool(value.get('archive_complete')),
                            'evidence_complete': valid and bool(value.get('evidence_complete')),
                            'integrity_valid': valid, 'file_sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
                digest.update(line); count += 1
    except (OSError, ValueError):
        return {'archive_complete': False, 'reason': 'archive_unreadable'}
    return {'archive_complete': False, 'reason': 'unclean_close', 'durable_prefix_records': count}


class InputBudget:
    """Shared across streams/reconnections; overshoot is at most one read per live reader."""
    def __init__(self, limit):
        self.limit, self.used = limit, 0
        self.lock = threading.Lock()

    def take(self, size):
        with self.lock:
            self.used += size
            return self.used <= self.limit


class DockerStream:
    def __init__(self, command, max_line, input_limit, budget=None):
        self.process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                        stdin=subprocess.DEVNULL)
        self.queue = queue.Queue(maxsize=256)
        self.closed_tail = []
        self.bytes = 0
        self.error = None
        self.cursor = None
        self.done = threading.Event()
        self.code = None
        self.thread = threading.Thread(target=self._read, args=(max_line, input_limit, budget), daemon=True)
        self.thread.start()

    def _read(self, cap, limit, budget):
        try:
            while True:
                raw = self.process.stdout.readline(cap + 1)
                if not raw:
                    break
                self.bytes += len(raw)
                if (budget is not None and not budget.take(len(raw))) or self.bytes > limit:
                    self.error = 'input_budget'; break
                if len(raw) > cap:
                    self.error = 'input_line_limit'; break
                envelope = LOG_ENVELOPE.match(raw)
                if envelope:
                    stamp = envelope[1].decode('ascii')
                    try: utc(stamp)
                    except ValueError: pass
                    else:
                        if self.cursor is None or stamp > self.cursor:
                            self.cursor = stamp
                if not envelope or not any(raw.startswith(prefix, envelope.end())
                                           for prefix in AGGREGATE_PREFIXES):
                    continue
                try:
                    # Preserve matching aggregates during a short burst. The queue stays
                    # bounded; a stalled consumer still fails explicitly after six seconds.
                    self.queue.put(raw, timeout=6)
                except queue.Full:
                    self.error = 'input_queue_overflow'; break
            self.code = self.process.wait(timeout=.2)
        except subprocess.TimeoutExpired:
            self.error = self.error or 'log_reader_stalled'
        except (OSError, ValueError):
            self.error = 'log_reader_failure'
        finally:
            self.done.set()

    def drain(self):
        values, self.closed_tail = self.closed_tail, []
        for _ in range(256):
            try: values.append(self.queue.get_nowait())
            except queue.Empty: break
        return values, self.error, self.done.is_set() and self.queue.empty(), self.code, self.bytes

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
            try: self.process.wait(timeout=.2)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=.2)
        # A producer may be waiting on a full queue when rollback stops this
        # reader. Drain into a bounded tail so the caller can archive every line.
        deadline = time.monotonic() + 7
        while self.thread.is_alive() and time.monotonic() < deadline:
            for _ in range(256):
                try: self.closed_tail.append(self.queue.get_nowait())
                except queue.Empty: break
            self.thread.join(.05)
        if self.thread.is_alive():
            self.error = 'log_reader_stalled'
        for _ in range(256):
            try: self.closed_tail.append(self.queue.get_nowait())
            except queue.Empty: break
        if self.process.stdout:
            self.process.stdout.close()


class DockerTransport:
    def __init__(self):
        self.budget = None

    def discover(self):
        template = '{"id":{{json .Id}},"image":{{json .Config.Image}},"running":{{json .State.Running}},"project":{{json (index .Config.Labels "com.docker.compose.project")}},"service":{{json (index .Config.Labels "com.docker.compose.service")}}}'
        r = subprocess.run(['docker', 'inspect', '--format', template, 'noponto_api'],
                           capture_output=True, timeout=3)
        if r.returncode:
            if b'No such object' in r.stderr or b'No such container' in r.stderr:
                return None
            raise RuntimeError('discovery_failed')
        if len(r.stdout) > 4096:
            raise RuntimeError('discovery_limit')
        value = json.loads(r.stdout)
        if value.get('image') != IMAGE or value.get('project') != 'noponto' or value.get('service') != 'api' or not CONTAINER.fullmatch(value.get('id', '')):
            raise RuntimeError('container_contract')
        return value

    def open(self, container, since, limits, remaining):
        if self.budget is None:
            self.budget = InputBudget(limits.max_input_bytes)
        return DockerStream(['docker', 'logs', '--follow', '--timestamps', '--since', since, container],
                            limits.max_line_bytes, remaining, self.budget)


class Engine:
    def __init__(self, archive, transport, now=time.time):
        self.archive, self.transport, self.now = archive, transport, now
        self.since = dt.datetime.fromtimestamp(now(), dt.timezone.utc).isoformat()
        self.streams, self.cursors, self.read_bytes = {}, {}, {}
        self.containers = set()
        self.current = None
        self.failure = None
        self.opens = 0
        self.retiring = {}

    def step(self):
        try:
            found = self.transport.discover()
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired):
            self.archive.gap('discovery_failed', self.current)
            self.failure = 'discovery_failed'; found = None
        active = found['id'] if found and found['running'] else None
        self.current = active
        # Drain old streams even when their container has already disappeared.
        for container, stream in list(self.streams.items()):
            lines, error, done, code, read_bytes = stream.drain()
            self.archive.input_bytes += read_bytes - self.read_bytes.get(container, 0)
            self.read_bytes[container] = read_bytes
            for raw in lines:
                self.archive.ingest(container, raw)
                match = re.match(('^(' + STAMP + ')').encode(), raw)
                if match:
                    stamp = match[1].decode('ascii')
                    try:utc(stamp)
                    except ValueError:continue
                    if stamp > self.cursors.get(container, ''):
                        self.cursors[container] = stamp
            # Unrelated logs are never queued, but still advance the reconnect cursor.
            cursor = getattr(stream, 'cursor', None)
            if cursor and cursor > self.cursors.get(container, ''):
                self.cursors[container] = cursor
            if error:
                self.archive.gap(error, container); self.failure = error
            if container != active and not done:
                self.retiring.setdefault(container, self.now())
                if self.now() - self.retiring[container] > 2:
                    self.archive.gap('retired_stream_unclosed', container)
                    self.failure = 'retired_stream_unclosed'
                    error = error or 'retired_stream_unclosed'
            if done or error:
                if not error and (container == active or code != 0):
                    self.archive.gap('log_stream_ended', container); self.failure = 'log_stream_ended'
                stream.close()
                # Termination can release a final buffered line. Archive it before retirement.
                tail, tail_error, _, _, final_bytes = stream.drain()
                if tail_error and not error:
                    self.archive.gap(tail_error, container); self.failure = tail_error
                self.archive.input_bytes += final_bytes - read_bytes
                for raw in tail:
                    self.archive.ingest(container, raw)
                self.archive.write({'kind': 'stream_end', 'container': container, 'at': self.now(), 'retired': container != active, 'exit_code': code})
                del self.streams[container]
                self.read_bytes.pop(container, None)
        if self.archive.input_bytes >= self.archive.limits.max_input_bytes:
            self.failure = 'input_budget'; self.archive.gap('input_budget', active)
        if active and active not in self.streams and not self.archive.truncated \
                and self.failure not in TERMINAL_STREAM_FAILURES | {'input_budget'}:
            if self.opens >= 32:
                self.archive.gap('stream_budget', active); self.failure = 'stream_budget'
            elif len(self.streams) >= 2 or len(self.containers | {active}) > self.archive.limits.max_containers:
                self.archive.gap('container_budget', active); self.failure = 'container_budget'
            else:
                self.containers.add(active)
                since = self.since
                if active in self.cursors:
                    point = dt.datetime.fromisoformat(self.cursors[active].replace('Z', '+00:00')) - dt.timedelta(seconds=2)
                    since = point.isoformat()
                try:
                    self.opens += 1
                    self.streams[active] = self.transport.open(active, since, self.archive.limits,
                        self.archive.limits.max_input_bytes - self.archive.input_bytes)
                    self.archive.write({'kind': 'stream_start', 'container': active, 'at': self.now(), 'since': since})
                except (OSError, RuntimeError):
                    self.archive.gap('stream_start_failed', active); self.failure = 'stream_start_failed'
        if self.archive.truncated:
            self.failure = 'output_budget'
        return {'ready': active in self.streams and not self.failure, 'container_id': active,
                'failure': self.failure, 'state': 'degraded' if self.failure else 'running'}

    def close(self):
        # Only our Docker log CLI children are stopped; never an API/container.
        for container, stream in list(self.streams.items()):
            stream.close()
            lines, error, _, _, read_bytes = stream.drain()
            self.archive.input_bytes += read_bytes - self.read_bytes.get(container, 0)
            for raw in lines:
                self.archive.ingest(container, raw)
            if error:
                self.archive.gap(error, container)
        self.streams.clear()


def run_capture(directory, session, seconds=1000):
    if os.name != 'posix' or not 1 <= seconds <= 1200:
        raise ValueError('capture_requires_linux_and_bounded_duration')
    stop = threading.Event()
    signal.signal(signal.SIGTERM, lambda *_: stop.set())
    signal.signal(signal.SIGINT, lambda *_: stop.set())
    archive = Archive(directory, session)
    engine = Engine(archive, DockerTransport())
    meta = {'pid': os.getpid(), 'pid_start': pathlib.Path('/proc/self/stat').read_text().split()[21],
            'image': IMAGE, 'source_sha256': hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest()}
    clean = False
    try:
        deadline = time.monotonic() + seconds
        while not stop.is_set() and time.monotonic() < deadline:
            status = engine.step()
            archive.status({**meta, **status})
            if archive.truncated or engine.failure in TERMINAL_STREAM_FAILURES | {'input_budget', 'container_budget', 'stream_budget'}:
                break
            stop.wait(1)
        clean = stop.is_set()
        if not clean:
            archive.gap('capture_duration_or_budget', engine.current)
    finally:
        engine.close()
        archive.close(clean)
        archive.status({**meta, 'state': 'closed', 'ready': False, 'container_id': engine.current,
                        'failure': engine.failure, 'archive_complete': verify(directory).get('archive_complete', False),
                        'evidence_complete': verify(directory).get('evidence_complete', False)})


if __name__ == '__main__':
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', required=True)
    parser.add_argument('--session')
    parser.add_argument('--seconds', type=int, default=1000)
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    if args.verify:
        print(json.dumps(verify(args.directory)))
    else:
        try:
            run_capture(args.directory, args.session or uuid.uuid4().hex, args.seconds)
        except Exception as error:
            raise SystemExit('capture_failed:' + type(error).__name__) from None
