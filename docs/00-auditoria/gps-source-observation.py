"""Bounded public source observation. No raw positions/identities are printed or saved.

Nine rounds, 30 seconds apart, four GETs per round, 8s/socket, 4MB/body.
The wire decoder reads only fields used by the comparison; it is not the production parser.
"""
import collections
import concurrent.futures
import datetime as dt
import gzip
import io
import json
import math
import statistics
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


def summary(values):
    values = sorted(values)
    return {"n": len(values), "p50": round(statistics.median(values), 3),
            "p95": round(values[max(0, math.ceil(len(values)*.95)-1)], 3),
            "min": round(values[0], 3), "max": round(values[-1], 3)} if values else {"n": 0}


def varint(body, index):
    value = shift = 0
    while index < len(body) and shift < 70:
        byte = body[index]; index += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, index
        shift += 7
    raise ValueError("invalid_varint")


def fields(body):
    index = 0
    while index < len(body):
        tag, index = varint(body, index)
        number, wire = tag >> 3, tag & 7
        if wire == 0:
            value, index = varint(body, index)
        elif wire == 2:
            size, index = varint(body, index)
            value = body[index:index+size]; index += size
        elif wire in (1, 5):
            size = 8 if wire == 1 else 4
            value = struct.unpack('<d' if wire == 1 else '<f', body[index:index+size])[0]
            index += size
        else:
            raise ValueError("unsupported_wire")
        if index > len(body):
            raise ValueError("truncated_wire")
        yield number, value


def parse_gtfs(body):
    rows = []
    generated = None
    for number, value in fields(body):
        if number == 1:
            generated = dict(fields(value)).get(3)
        if number != 2:
            continue
        entity = dict(fields(value))
        if 4 not in entity:
            continue
        vehicle = dict(fields(entity[4]))
        descriptor = dict(fields(vehicle.get(8, b'')))
        trip = dict(fields(vehicle.get(1, b'')))
        position = dict(fields(vehicle.get(2, b'')))
        identity = descriptor.get(1, b'').decode().strip().upper().removeprefix('BRT-')
        if identity and 5 in vehicle and 1 in position and 2 in position:
            rows.append((identity, float(vehicle[5]), position[1], position[2], position.get(5), trip.get(5, b'').decode()))
    return rows, generated


def iso(value):
    return dt.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()


def fetch(name):
    now = dt.datetime.now(dt.timezone.utc)
    base = 'https://dados.mobilidade.rio/'
    path = {'GTFSRT_BUS': 'gtfs/realtime/onibus/vehicle-positions',
            'GTFSRT_BRT': 'gtfs/realtime/brt/vehicle-positions', 'BRT_RIO': 'gps/brt',
            'ZIRIX': 'sppo/zirix/gps'}[name]
    if name == 'ZIRIX':
        path += '?' + urllib.parse.urlencode({'dataInicial': (now-dt.timedelta(seconds=30)).strftime('%Y-%m-%dT%H:%M:%SZ'),
                                             'dataFinal': now.strftime('%Y-%m-%dT%H:%M:%SZ')})
    started = time.monotonic()
    try:
        req = urllib.request.Request(base+path, headers={'User-Agent': 'NoPonto-GPS-readonly-audit/1', 'Accept-Encoding': 'identity'})
        with urllib.request.urlopen(req, timeout=8) as response:
            body = response.read(4_000_001)
            if len(body) > 4_000_000:
                raise ValueError('body_limit')
            headers = {key: response.headers.get(key) for key in ('Cache-Control', 'Age', 'Content-Type')}
        if body[:2] == b'\x1f\x8b':
            with gzip.GzipFile(fileobj=io.BytesIO(body)) as compressed:
                body = compressed.read(4_000_001)
            if len(body) > 4_000_000:
                raise ValueError('decoded_body_limit')
        generated = None
        if name.startswith('GTFS'):
            rows, generated = parse_gtfs(body)
        elif name == 'BRT_RIO':
            rows = [(str(r['codigo']).strip().upper().removeprefix('BRT-'), float(r['dataHora'])/1000,
                     float(r['latitude']), float(r['longitude']), float(r['velocidade']), str(r['linha']))
                    for r in json.loads(body).get('veiculos', []) if r.get('codigo') and r.get('dataHora') and str(r.get('linha', '0')) != '0']
        else:
            rows = [(str(r['id_veiculo']).strip().upper(), iso(r['datetime']), float(r['latitude']), float(r['longitude']),
                     float(r['velocidade']), str(r['servico'])) for r in json.loads(body)
                    if r.get('id_veiculo') and r.get('datetime') and str(r.get('servico', '0')) != '0']
        latest = {}
        for row in rows:
            if row[0] not in latest or row[1] > latest[row[0]][1]:
                latest[row[0]] = row
        received = time.time()
        return {'rows': rows, 'latest': latest, 'received': received, 'generated': generated,
                'headers': headers, 'bytes': len(body), 'seconds': time.monotonic()-started}
    except Exception as error:
        return {'error': type(error).__name__, 'http_status': getattr(error, 'code', None)}


names = ('GTFSRT_BUS', 'GTFSRT_BRT', 'BRT_RIO', 'ZIRIX')
if sys.argv[1:] == ['--brt-only']:
    names = ('GTFSRT_BRT', 'BRT_RIO')
state = {name: {'previous': {}, 'deltas': [], 'ages': [], 'failures': [], 'pairs': 0,
                'same': 0, 'position_unchanged_on_new_timestamp': 0, 'counts': [], 'headers': [], 'http_seconds': []} for name in names}
paired = {modal: {'timestamp_delta_current_minus_old': [], 'equal_timestamp': 0,
                  'speed_absolute_difference_equal_timestamp': [], 'distance_m_equal_timestamp': []} for modal in ('BUS', 'BRT')}
start = time.monotonic()
for round_index in range(9):
    if round_index:
        time.sleep(max(0, start+30*round_index-time.monotonic()))
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        results = dict(zip(names, pool.map(fetch, names)))
    public = {}
    for name, result in results.items():
        item = state[name]
        if 'error' in result:
            item['failures'].append(result)
            public[name] = result
            continue
        latest = result['latest']
        item['counts'].append(len(latest)); item['headers'].append(result['headers']); item['http_seconds'].append(result['seconds'])
        ages = [result['received']-row[1] for row in latest.values()]
        item['ages'].extend(ages)
        for identity, row in latest.items():
            prior = item['previous'].get(identity)
            if prior and row[1] >= prior[1]:
                item['pairs'] += 1
                if row[1] == prior[1]:
                    item['same'] += 1
                else:
                    item['deltas'].append(row[1]-prior[1])
                    if row[2:4] == prior[2:4]:
                        item['position_unchanged_on_new_timestamp'] += 1
        item['previous'].update(latest)
        public[name] = {'vehicles': len(latest), 'rows': len(result['rows']), 'gps_age_seconds': summary(ages),
                        'headers': result['headers'], 'bytes': result['bytes'], 'http_seconds': round(result['seconds'], 3),
                        'generated_age_seconds': round(result['received']-result['generated'], 3) if result['generated'] else None}
    for modal, old in (('BUS', 'ZIRIX'), ('BRT', 'BRT_RIO')):
        if old not in results:
            continue
        current, legacy = results['GTFSRT_'+modal], results[old]
        if 'error' in current or 'error' in legacy:
            continue
        for identity in current['latest'].keys() & legacy['latest'].keys():
            a, b = current['latest'][identity], legacy['latest'][identity]
            paired[modal]['timestamp_delta_current_minus_old'].append(a[1]-b[1])
            if a[1] == b[1]:
                paired[modal]['equal_timestamp'] += 1
                if a[4] is not None and b[4] is not None:
                    paired[modal]['speed_absolute_difference_equal_timestamp'].append(abs(a[4]-b[4]))
                lat = math.radians((a[2]+b[2])/2)
                paired[modal]['distance_m_equal_timestamp'].append(111195*math.hypot(a[2]-b[2], (a[3]-b[3])*math.cos(lat)))
    print(json.dumps({'round': round_index+1, 'utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'sources': public}), flush=True)

print(json.dumps({'summary': {name: {'successful_rounds': len(item['counts']), 'failures': item['failures'],
    'vehicle_counts': summary(item['counts']), 'observed_positive_gps_delta_seconds': summary(item['deltas']),
    'positive_delta_histogram': dict(collections.Counter(item['deltas']).most_common(12)),
    'gps_age_seconds': summary(item['ages']), 'consecutive_seen_pairs': item['pairs'], 'same_timestamp_pairs': item['same'],
    'same_position_new_timestamp': item['position_unchanged_on_new_timestamp'], 'http_seconds': summary(item['http_seconds'])}
    for name, item in state.items()},
    'paired': {modal: {key: summary(value) if isinstance(value, list) else value for key, value in item.items()} for modal, item in paired.items()},
    'duration_seconds': round(time.monotonic()-start, 3),
    'limitations': ['latest-per-round, not all provider events', 'no sampling policy applied', 'no crosswalk promotion in probe',
                    'same GPS timestamp may represent a stationary vehicle; no frozen-position verdict', 'wire decoder independent of production parser']}, ensure_ascii=False), flush=True)
