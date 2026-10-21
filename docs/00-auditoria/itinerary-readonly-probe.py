"""SSH stdin only. Bounded observation; never starts the API or changes data/config.

Two existing REST lines, <=12 identities/line, three Redis reads, 35s apart.
--geometry: one snapshot and <=6 two-version projections/modal.
--structure: one snapshot and indexed cursor/next/last-stop seeks.
SQL: lookups by indexed keys only, READ ONLY, 3s statement/500ms lock timeout.
No raw positions, vehicle/trip identities, credentials or geometries in output.
"""
import collections
import datetime as dt
import hashlib
import json
import math
import subprocess
import sys
import time
import urllib.request
import uuid


def run(args, timeout=10, stdin=None):
    r = subprocess.run(args, input=stdin, capture_output=True, text=True, timeout=timeout)
    if r.returncode:
        raise RuntimeError('readonly_command_failed:' + args[0])
    return r.stdout


def alias(value):
    return hashlib.sha256((salt + str(value)).encode()).hexdigest()[:12]


def norm(value):
    return str(value or '').replace('-', '').lower()


def iso(value):
    return dt.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()


def ticks(value):
    return int(value) / 10_000_000 - 62135596800


def sql(query):
    wrapped = ("BEGIN READ ONLY; SET LOCAL statement_timeout='3s'; "
               "SET LOCAL lock_timeout='500ms'; SET LOCAL idle_in_transaction_session_timeout='5s'; "
               + query + '; COMMIT;')
    raw = run(['docker', 'exec', '-i', 'transporte_postgres', 'psql', '-X', '-q', '-t', '-A',
               '-v', 'ON_ERROR_STOP=1', '-U', pg_user, '-d', pg_db], stdin=wrapped)
    return [json.loads(line) for line in raw.splitlines() if line.startswith('{')]


salt = uuid.uuid4().hex
geometry_mode = sys.argv[1:] == ['--geometry']
structure_mode = sys.argv[1:] == ['--structure']
api = json.loads(run(['docker', 'inspect', 'noponto_api']))[0]
env = dict(item.split('=', 1) for item in api['Config']['Env'] if '=' in item)
settings = json.loads(run(['docker', 'exec', 'noponto_api', 'cat', '/app/appsettings.json']))
print(json.dumps({'stage': 'runtime', 'utc': dt.datetime.now(dt.timezone.utc).isoformat(),
    'image': api['Image'], 'started': api['State']['StartedAt'],
    'config': {key: env.get(key, settings.get(key.split('__')[0], {}).get(key.split('__')[-1], 'absent'))
        for key in ('GpsPolling__MudancaOperacionalHabilitada', 'GpsPolling__VelocidadeMaximaKmh',
                    'GpsPolling__ToleranciaProjecaoMetros', 'GpsPolling__DistanciaMaximaRotaMetros',
                    'GpsCorrelatedDiagnostics__Enabled')}}), flush=True)
pg = json.loads(run(['docker', 'inspect', 'transporte_postgres']))[0]
pg_env = dict(item.split('=', 1) for item in pg['Config']['Env'] if '=' in item)
pg_user = pg_env.get('POSTGRES_USER', 'postgres')
pg_db = pg_env.get('POSTGRES_DB', pg_user)
metadata = sql("""SELECT json_build_object('table',tablename,'index',indexname,'definition',indexdef)
 FROM pg_indexes WHERE schemaname='public' AND tablename IN
 ('ViagensOperacionais','PadroesVersoes','PadroesOperacionais','Sentidos','Linhas','OcorrenciasParadasPadroes')
 AND indexdef LIKE '%UNIQUE%' LIMIT 24""")
print(json.dumps({'stage': 'lookup_indexes', 'rows': metadata}), flush=True)
if not any(r['table'] == 'ViagensOperacionais' and '"OrdemVeiculo"' in r['definition'] for r in metadata):
    raise RuntimeError('vehicle_primary_key_not_verified')
if structure_mode and not any(r['table'] == 'OcorrenciasParadasPadroes'
        and '("PadraoVersaoId", "Ordem")' in r['definition'] for r in metadata):
    raise RuntimeError('occurrence_order_index_not_verified')

orders = []
for code, modal in [('898', 'BUS'), ('35', 'BRT')]:
    with urllib.request.urlopen('http://127.0.0.1:8080/veiculos/linha/' + code, timeout=5) as response:
        body = response.read(1_000_001)
    if len(body) > 1_000_000:
        raise RuntimeError('rest_body_limit')
    data = json.loads(body)
    positions = sorted(data.get('posicoes', []), key=lambda p: p['ordem'])[:12]
    orders.extend((p['ordem'], modal) for p in positions)
    print(json.dumps({'stage': 'selection', 'line': code, 'modal': modal,
        'available': data.get('totalVeiculos'), 'sampled': len(positions)}), flush=True)

redis_lua = """local out={}
for i=1,#KEYS,3 do
 table.insert(out,cjson.encode({active=redis.call('HGET',KEYS[i],'data') or false,
 recent=redis.call('HGET',KEYS[i+1],'data') or false,
 operational=redis.call('HGETALL',KEYS[i+2])}))
end return out"""
keys = [f'veiculo:{order}:{suffix}' for order, _ in orders for suffix in ('ativo', 'recente', 'viagem')]
previous = {}
geometry_counts = collections.Counter()
for round_id in range(1 if geometry_mode or structure_mode else 3):
    if round_id:
        time.sleep(35)
    now = time.time()
    raw = run(['docker', 'exec', 'transporte_redis', 'redis-cli', '--raw', 'EVAL_RO',
               redis_lua, str(len(keys)), *keys]) if keys else ''
    records = []
    for (order, modal), line in zip(orders, raw.splitlines()):
        value = json.loads(line)
        payload = value['active'] or value['recent']
        op_array = value['operational']
        op = dict(zip(op_array[::2], op_array[1::2]))
        if not payload or not op:
            records.append({'vehicle': alias(order), 'modal': modal, 'position_present': bool(payload),
                'operational_present': bool(op)})
            continue
        p = {k.lower(): v for k, v in json.loads(payload).items()}
        timestamp = iso(p['timestampgps'])
        operational_ts = ticks(op['TimestampUltimaAtualizacao'])
        record = {'vehicle': alias(order), 'modal': modal, 'line': p.get('codigolinha'),
            'operational_line': op['CodigoLinha'], 'active': bool(value['active']),
            'gps': p['timestampgps'], 'gps_age_s': round(now - timestamp, 2),
            'operational_gps_utc': dt.datetime.fromtimestamp(operational_ts, dt.timezone.utc).isoformat(),
            'operational_lag_s': round(timestamp - operational_ts, 2),
            'state': op['EstadoViagem'], 'trip': alias(op['ViagemId']),
            'matching_present': bool(p.get('padraoversaoid')),
            'same_version': norm(p.get('padraoversaoid')) == norm(op['PadraoVersaoId']),
            'same_pattern': norm(p.get('padraooperacionalid')) == norm(op['PadraoOperacionalId']),
            'same_direction': norm(p.get('sentidoid')) == norm(op['SentidoId']),
            'same_line': norm(p.get('linhaid')) == norm(op['LinhaId']),
            'matching_version': alias(norm(p.get('padraoversaoid'))),
            'pinned_version': alias(norm(op['PadraoVersaoId'])),
            'matching_topology': p.get('topologiapadrao'), 'pinned_topology': op['Topologia'],
            'matching_fraction': p.get('posicaonarota'), 'pinned_fraction': float(op['PosicaoNaRotaConfirmada']),
            'cursor_order': op['UltimaParadaOrdem'], 'candidate': bool(op.get('CandidatoPadraoVersaoId')),
            'eta_present': p.get('etaproximaparadasegundos') is not None,
            'matching_target_present': bool(p.get('proximaocorrenciaparadapadraoid'))}
        old = previous.get(order)
        if old:
            record.update(gps_increased=timestamp > old[0], operational_unchanged=op == old[1])
        previous[order] = (timestamp, op)
        # One bounded PK lookup per selected vehicle, first/last rounds only.
        if round_id in (0, 2) and not structure_mode:
            literal = order.replace("'", "''")
            durable = sql('SELECT json_build_object(\'state\',"Estado",\'version\',"Versao") '
                          'FROM "ViagensOperacionais" WHERE "OrdemVeiculo"=\'' + literal + "' LIMIT 1")
            if durable:
                d = durable[0]['state']
                record.update(durable_present=True, durable_trip_same=norm(d[0]) == norm(op['ViagemId']),
                    durable_pinned_same=norm(d[2]) == norm(op['PadraoVersaoId']),
                    durable_version=durable[0]['version'], durable_cursor_order=d[7],
                    durable_gps_utc=dt.datetime.fromtimestamp(ticks(d[4]), dt.timezone.utc).isoformat())
        if geometry_mode and geometry_counts[modal] < 6 and not record['same_version'] and record['matching_present']:
            # UUIDs/numbers validated before SQL interpolation. No history-table access.
            pinned = str(uuid.UUID(op['PadraoVersaoId']))
            matched = str(uuid.UUID(p['padraoversaoid']))
            lat, lon = float(p['latitude']), float(p['longitude'])
            start = float(op['PosicaoNaRotaConfirmada'])
            max_speed = float(env.get('GpsPolling__VelocidadeMaximaKmh', 90))
            tolerance = float(env.get('GpsPolling__ToleranciaProjecaoMetros', 50))
            distance_limit = float(env.get('GpsPolling__DistanciaMaximaRotaMetros', 250))
            budget = max(0, timestamp - operational_ts) * max_speed * 2 / 3.6 + tolerance
            if not all(math.isfinite(n) for n in (lat, lon, start, budget)):
                raise RuntimeError('invalid_geometry_input')
            geometry = sql(f'''WITH versions AS MATERIALIZED (
 SELECT v.*,po."VersaoAtualId",po."Chave",po."TipoServico",po."SentidoId",s."Nome" direction_name,
 s."LinhaId",l."Codigo" line_code FROM "PadroesVersoes" v
 JOIN "PadroesOperacionais" po ON po."Id"=v."PadraoOperacionalId"
 JOIN "Sentidos" s ON s."Id"=po."SentidoId" JOIN "Linhas" l ON l."Id"=s."LinhaId"
 WHERE v."Id" IN ('{pinned}'::uuid,'{matched}'::uuid)
 ), base AS (
 SELECT a.*,b."Numero" matching_number,b."Chave" matching_key,b.direction_name matching_direction,
 b."TipoServico" matching_service,b.line_code matching_line,
 ST_Length(a."Geometria"::geography) len,
 ST_SetSRID(ST_MakePoint({lon},{lat}),4326) point FROM versions a CROSS JOIN versions b
 WHERE a."Id"='{pinned}'::uuid AND b."Id"='{matched}'::uuid
 ), bounds AS (
 SELECT *,GREATEST(0.0,{start}-({budget}/NULLIF(len,0))) lo,
 LEAST(1.0,{start}+({budget}/NULLIF(len,0))) hi FROM base
 ), projected AS (
 SELECT *,ST_LineSubstring("Geometria",lo,hi) sub FROM bounds WHERE lo<hi
 ), result AS (
 SELECT *,lo+ST_LineLocatePoint(sub,point)*(hi-lo) fraction,
 ST_Distance(sub::geography,point::geography) distance FROM projected
 ) SELECT json_build_object('pinned_number',"Numero",'matching_number',matching_number,
 'pinned_is_current',"Id"="VersaoAtualId",'pinned_key',"Chave",'matching_key',matching_key,
 'pinned_service',"TipoServico",'matching_service',matching_service,
 'pinned_direction',direction_name,'matching_direction',matching_direction,
 'pinned_line',line_code,'matching_line',matching_line,
 'length_m',len,'budget_m',{budget},'projection_fraction',fraction,
 'projection_distance_m',distance,'spatial_eligible',distance<={distance_limit},
 'nonregressive',fraction>={start},'same_line_code',line_code=matching_line,
 'pinned_geometry_simple',ST_IsSimple("Geometria"),'pinned_geometry_closed',ST_IsClosed("Geometria")) FROM result''')
            record['geometry_replay'] = geometry[0] if geometry else {'no_result': True}
            geometry_counts[modal] += 1
        if structure_mode:
            pinned = str(uuid.UUID(op['PadraoVersaoId']))
            cursor = str(uuid.UUID(op['UltimaOcorrenciaParadaPadraoId'] or '0' * 32))
            order_number = int(op['UltimaParadaOrdem'])
            structure = sql(f'''SELECT json_build_object(
 'cursor_fraction',c."PosicaoTracado",'terminal_order',t."Ordem",'terminal_fraction',t."PosicaoTracado",
 'next_order',n."Ordem",'next_fraction',n."PosicaoTracado")
 FROM (VALUES(1)) x(id)
 LEFT JOIN "OcorrenciasParadasPadroes" c ON c."Id"='{cursor}'::uuid
 LEFT JOIN LATERAL (SELECT "Ordem","PosicaoTracado" FROM "OcorrenciasParadasPadroes"
 WHERE "PadraoVersaoId"='{pinned}'::uuid ORDER BY "Ordem" DESC LIMIT 1) t ON true
 LEFT JOIN LATERAL (SELECT "Ordem","PosicaoTracado" FROM "OcorrenciasParadasPadroes"
 WHERE "PadraoVersaoId"='{pinned}'::uuid AND "Ordem">{order_number}
 ORDER BY "Ordem" LIMIT 1) n ON true''')
            record['structure'] = structure[0] if structure else {'no_result': True}
        records.append(record)
    print(json.dumps({'stage': 'snapshot', 'round': round_id,
        'utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'records': records}), flush=True)
