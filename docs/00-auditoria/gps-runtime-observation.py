"""Append after helpers from gps-source-observation.py, before its polling loop.

Reads at most 50 feed identities/modal in Redis, plus one REST line/modal.
EVAL_RO has only HGET/TTL/GET; no SCAN or writes. Output contains aggregates only.
"""
import subprocess


def command(args):
    result = subprocess.run(args, capture_output=True, text=True, timeout=10)
    if result.returncode:
        raise RuntimeError('read_command_failed')
    return result.stdout


redis_script = '''local out = {}
for i=1,#KEYS,3 do
 local active=redis.call('HGET',KEYS[i],'data')
 local recent=redis.call('HGET',KEYS[i+1],'data')
 table.insert(out,cjson.encode({active=active or false,recent=recent or false,
 active_ttl=redis.call('TTL',KEYS[i]),recent_ttl=redis.call('TTL',KEYS[i+1]),
 watermark=redis.call('GET',KEYS[i+2]) or false}))
end
return out'''
output = {'utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'sampling': 'first 50 sorted feed identities per modal; convenience sample, not fleet estimate', 'modals': {}}
for modal in ('BUS', 'BRT'):
    feed = fetch('GTFSRT_'+modal)
    if 'error' in feed:
        output['modals'][modal] = feed
        continue
    identities = sorted(feed['latest'])[:50]
    keys = []
    for identity in identities:
        order = 'BRT-'+identity if modal == 'BRT' else identity
        keys += ['veiculo:'+order+suffix for suffix in (':ativo', ':recente', ':ts')]
    raw = command(['docker', 'exec', 'transporte_redis', 'redis-cli', '--raw', 'EVAL_RO', redis_script, str(len(keys)), *keys])
    counters = collections.Counter()
    ages = []; active_ttls = []; recent_ttls = []; reasons = collections.Counter(); codes = []
    for line in raw.splitlines():
        value = json.loads(line)
        active, recent = value['active'], value['recent']
        counters['sampled'] += 1
        counters['active'] += bool(active); counters['recent'] += bool(recent)
        counters['watermark'] += bool(value['watermark'])
        counters['recent_without_active'] += bool(recent and not active)
        if not (active or recent):
            continue
        position = {key.lower(): v for key, v in json.loads(active or recent).items()}
        timestamp = iso(position['timestampgps'])
        ages.append(time.time()-timestamp)
        if active:
            active_ttls.append(value['active_ttl'])
        if recent:
            recent_ttls.append(value['recent_ttl'])
        counters['physical_predecessor'] += bool(position.get('timestampanterior'))
        counters['matching_version'] += bool(position.get('padraoversaoid'))
        counters['eta_present'] += position.get('etaproximaparadasegundos') is not None
        confidence = position.get('etaconfianca')
        if confidence in ('physical_causal:estimativa_fisica', 'physical_causal:previsao_indisponivel', 'physical_causal:gps_desatualizado', None):
            reasons[str(confidence)] += 1
        else:
            reasons['other'] += 1
        code = position.get('codigolinha')
        if code:
            codes.append(code)
    counts = dict(counters)
    rest = None
    if codes:
        code = collections.Counter(codes).most_common(1)[0][0]
        try:
            with urllib.request.urlopen('http://127.0.0.1:8080/veiculos/linha/'+urllib.parse.quote(code), timeout=5) as response:
                body = response.read(1_000_001)
                if len(body) > 1_000_000:
                    raise ValueError('rest_body_limit')
                result = json.loads(body)
            positions = result.get('posicoes', [])
            rest = {'code': code, 'total': result.get('totalVeiculos'), 'active': result.get('totalAtivos'),
                    'no_signal': result.get('totalSemSinal'),
                    'gps_age_seconds': summary([time.time()-iso(p['timestampGps']) for p in positions]),
                    'mismatching_line_count': sum(p['codigoLinha'].upper()!=code.upper() for p in positions),
                    'eta_present': sum(p.get('etaProximaParadaSegundos') is not None for p in positions)}
        except Exception as error:
            rest = {'error': type(error).__name__, 'http_status': getattr(error, 'code', None)}
    output['modals'][modal] = {'redis_counts': counts, 'gps_age_seconds': summary(ages),
        'active_ttl_seconds': summary(active_ttls), 'recent_ttl_seconds': summary(recent_ttls),
        'eta_quality_counts': dict(reasons), 'rest': rest}

groups = command(['docker', 'exec', 'transporte_redis', 'redis-cli', '--json', 'XINFO', 'GROUPS', 'noponto:ml:telemetria'])
try:
    output['telemetry_stream_groups'] = json.loads(groups)
except ValueError:
    output['telemetry_stream_groups'] = 'unavailable'
print(json.dumps(output, ensure_ascii=False), flush=True)
