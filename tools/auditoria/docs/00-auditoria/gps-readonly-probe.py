"""Run over SSH stdin; bounded read-only metadata/log inspection, aggregate output only."""
import collections
import datetime
import json
import re
import statistics
import subprocess
import sys


def run(args, timeout=15):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError("read-only probe failed: " + args[0])
    return result.stdout + (result.stderr if args[:2] == ["docker", "logs"] else "")


def summary(values):
    values = sorted(values)
    return {"n": len(values), "min": min(values), "p50": statistics.median(values),
            "p95_nearest_rank": values[max(0, (95 * len(values) + 99) // 100 - 1)], "max": max(values)} if values else {"n": 0}


api = json.loads(run(["docker", "inspect", "noponto_api"]))[0]
env = dict(item.split("=", 1) for item in api["Config"]["Env"] if "=" in item)
allowed = re.compile(r"^(GpsPolling__|GtfsRealtimeGps__|GpsSppoCollector__|GpsMatching__|PositionCorrection__|PhysicalEta__|EtaV2__|HistoricalEtaShadow__|TelemetriaMlSampling__|GpsCorrelatedDiagnostics__|GPS_MATCHING_BATCH_ENABLED$|ML__ETA__Enabled$|ASPNETCORE_ENVIRONMENT$)")
config = {}
for key, value in sorted(env.items()):
    if (allowed.match(key) or key in ("GpsSources__BusPrimarySource", "GpsSources__BrtPrimarySource", "GpsSources__BusShadowSources__0", "GpsSources__BrtShadowSources__0")) and re.fullmatch(r"[A-Za-z0-9_.+-]{1,90}", value) and not re.search(r"seed|password|secret|token|key", key, re.I):
        config[key] = value
image = json.loads(run(["docker", "image", "inspect", api["Image"]]))[0]
appsettings = json.loads(run(["docker", "exec", "noponto_api", "cat", "/app/appsettings.json"]))
settings = {}
def flatten(obj, path=''):
    if isinstance(obj, dict):
        for key, value in obj.items():
            flatten(value, path+'__'+key if path else key)
    elif (allowed.match(path) and not re.search(r"seed|password|secret|token|key", path, re.I)
          and isinstance(obj, (bool, int, float, str)) and re.fullmatch(r"[A-Za-z0-9_.+-]{1,90}", str(obj))):
        settings[path] = obj
flatten(appsettings)
print(json.dumps({"utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "container": {"image_id": api["Image"], "configured_image": api["Config"]["Image"],
                  "started": api["State"]["StartedAt"], "restarts": api["RestartCount"]},
    "image": {"digests": image.get("RepoDigests"), "labels": {k: v for k, v in (image["Config"].get("Labels") or {}).items() if k in ("org.opencontainers.image.revision", "org.opencontainers.image.version", "org.opencontainers.image.created")}},
    "environment_whitelist": config, "image_appsettings_whitelist": settings}, ensure_ascii=False))

# Do not emit raw logs, identities, coordinates, URLs or exception text.
logs = run(["docker", "logs", "--since", "10m", "--tail", "12000", "noponto_api"], 20)
metrics = collections.defaultdict(list)
sources = collections.defaultdict(lambda: collections.defaultdict(list))
snapshots = collections.defaultdict(list)
operational_cycles = {}
for line in logs.splitlines():
    if "Performance GPS:" in line:
        for key, value in re.findall(r"([A-Za-z0-9_]+)=([0-9]+(?:[.][0-9]+)?)(?=[,;\s]|$)", line):
            metrics[key].append(float(value.replace(",", ".")))
    funnel = re.search(r"Funil GPS: cycle_id=(\S+).*?parte=(\d+) partes=(\d+).*?contadores=(.*)", line)
    if funnel:
        cycle = operational_cycles.setdefault(funnel[1], {'parts': {}, 'expected': int(funnel[3]), 'duplicate': False})
        part = int(funnel[2])
        if part in cycle['parts']:
            cycle['duplicate'] = True
        cycle['parts'][part] = dict((k, int(v)) for k,v in re.findall(r"(op_[A-Za-z0-9_]+)=(\d+)", funnel[4]))
    snapshot_match = re.search(r"GTFSRT source=(GTFSRT_BUS|GTFSRT_BRT).*snapshot_received=(.*?);", line)
    if snapshot_match:
        try:
            value = datetime.datetime.strptime(snapshot_match[2], '%m/%d/%Y %H:%M:%S %z')
            snapshots[snapshot_match[1]].append(value)
        except ValueError:
            pass
    match = re.search(r"GTFSRT source=(GTFSRT_BUS|GTFSRT_BRT) raw=(\d+) promoted=(\d+) unresolved_route=(\d+) invalid_direction=(\d+) invalid_speed=(\d+) parser_rejected=(\d+) duplicates=(\d+) reused=(True|False|true|false)", line)
    if match:
        for key, value in zip(("raw", "promoted", "unresolved_route", "invalid_direction", "invalid_speed", "parser_rejected", "duplicates"), match.groups()[1:8]):
            sources[match[1]][key].append(int(value))
        sources[match[1]]["reused"].append(int(match[9].lower() == "true"))
selected = ("total_ms", "start_to_start_ms", "ciclo_concluido", "entrada", "validas", "novas", "commits_aceitos", "commits_infra", "commits_rejeitados", "viagem_chamadas", "viagem_infra", "matching_batch_infrastructure_failures", "brt_consultas_http", "brt_cache_reutilizacoes", "brt_cache_idade_ms", "linhas_publicadas", "veiculos_enviados", "signalr_preparacao_ms", "signalr_broadcast_espera_ms", "delay_planejado_ms", "matching_etapa_ms", "commit_viagem_etapa_ms")
complete = [v for v in operational_cycles.values() if not v['duplicate'] and set(v['parts']) == set(range(1,v['expected']+1))]
operational = collections.Counter()
for cycle in complete:
    for values in cycle['parts'].values():
        operational.update(values)
print(json.dumps({"logs": {"window": "10m", "tail_cap": 12000, "lines_returned": len(logs.splitlines()),
    "selected_metrics": {k: dict(summary(metrics[k]), sum=sum(metrics[k])) for k in selected if metrics[k]},
    "gtfs_source_summaries": {s: {k: summary(v) for k, v in fields.items()} for s, fields in sources.items()},
    "http_200_snapshot_intervals_seconds": {s: dict(summary([(b-a).total_seconds() for a,b in zip(v,v[1:])]),
        first=v[0].isoformat(), last=v[-1].isoformat()) for s,v in snapshots.items() if len(v)>1},
    "operational_cycles_complete": len(complete), "operational_cycles_seen": len(operational_cycles),
    "operational_counter_sums": {k: v for k,v in operational.items() if k.startswith('op_nova_') and ('predecessor' in k or 'resultado' in k or k.endswith(('CommitPassagem','ComCruzamentos','ItineraryChanged','ZeroCruzamentos')))},
    "failure_tag_counts": {tag: logs.count(tag) for tag in ("crosswalk_unavailable", "acquisition_timeout", "invalid_or_unavailable_feed", "exceeded cadence", "Falha no ciclo de polling", "cache_requires_validation")}}}, ensure_ascii=False))

if sys.argv[1:] == ['--logs-only']:
    raise SystemExit(0)

# Metadata only, bounded transactions. Credentials stay inside Docker/psql environment.
pg = json.loads(run(["docker", "inspect", "transporte_postgres"]))[0]
pg_env = dict(item.split("=", 1) for item in pg["Config"]["Env"] if "=" in item)
sql = '''BEGIN READ ONLY;
SET LOCAL statement_timeout='3s'; SET LOCAL lock_timeout='500ms';
SET LOCAL idle_in_transaction_session_timeout='5s';
SELECT table_name,column_name,data_type FROM information_schema.columns
WHERE table_schema='public' AND (table_name ILIKE '%telemetria%' OR table_name ILIKE '%journal%')
ORDER BY table_name,ordinal_position LIMIT 120;
SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname='public'
AND (tablename ILIKE '%telemetria%' OR tablename ILIKE '%journal%') LIMIT 40;
COMMIT;'''
result = run(["docker", "exec", "transporte_postgres", "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", pg_env.get("POSTGRES_USER", "postgres"), "-d", pg_env.get("POSTGRES_DB", pg_env.get("POSTGRES_USER", "postgres")), "-A", "-t", "-c", sql], 10)
print(json.dumps({"schema_metadata": result.splitlines()}, ensure_ascii=False))
