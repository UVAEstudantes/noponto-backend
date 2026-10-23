"""SSH stdin: bounded READ ONLY preflight. No raw configuration, credentials or GPS output."""
import datetime
import copy
import json
import os
import re
import subprocess
import urllib.request


def run(args, stdin=None, timeout=15, env=None):
    result = subprocess.run(args, input=stdin, capture_output=True, text=True, timeout=timeout, env=env)
    if result.returncode:
        raise RuntimeError("read_only_command_failed:" + args[0])
    return result.stdout


def emit(stage, value):
    print(json.dumps({"stage": stage, "value": value}), flush=True)


def safe_settings(values):
    allowed = re.compile(r"^(GpsSources__|GpsPolling__|GtfsRealtimeGps__|GpsSppoCollector__|GpsMatching__|PhysicalEta__|GPS_MATCHING_BATCH_ENABLED$|ASPNETCORE_ENVIRONMENT$|DOTNET_ENVIRONMENT$)")
    return {k: v for k, v in values.items() if allowed.match(k)
            and not re.search(r"password|secret|token|key|connection|url|endpoint", k, re.I)
            and re.fullmatch(r"[a-zA-Z0-9_.+-]{0,100}", str(v))}


try:
    api = json.loads(run(["docker", "inspect", "noponto_api"]))[0]
    labels = api["Config"]["Labels"]
    assert labels["com.docker.compose.project"] == "noponto" and labels["com.docker.compose.service"] == "api"
    environment = dict(x.split("=", 1) for x in api["Config"]["Env"] if "=" in x)
    files = labels["com.docker.compose.project.config_files"].split(",")
    directory = labels["com.docker.compose.project.working_dir"]
    assert all(os.path.isabs(x) and os.path.isfile(x) for x in files)
    env_file = labels.get("com.docker.compose.project.environment_file", directory + "/.env")
    assert os.path.isfile(env_file)
    command = ["docker", "compose", "--project-name", "noponto", "--project-directory", directory, "--env-file", env_file]
    for path in files:
        command += ["-f", path]
    rendered = json.loads(run(command + ["config", "--format", "json"], timeout=30,
                              env=dict(os.environ, GTFSRT_API_IMAGE=api["Config"]["Image"])))
    candidate = rendered["services"]["api"]
    connection_keys = [k for k in environment if re.search(r"postgres|redis|connection", k, re.I)]
    emit("connection_configuration", {"keys_only": sorted(connection_keys),
        "render_matches_running": all(candidate.get("environment", {}).get(k) == environment[k] for k in connection_keys),
        "values": "omitted"})
    override = {"services": {"api": {"environment": {
        "GpsSources__BusPrimarySource": "ITS_CURRENT", "GpsSources__BusFallbackSource": "GTFSRT_BUS"}}}}
    desired = json.loads(run(command + ["-f", "-", "config", "--format", "json"], stdin=json.dumps(override), timeout=30,
                            env=dict(os.environ, GTFSRT_API_IMAGE=api["Config"]["Image"])))
    normalized = copy.deepcopy(desired)
    for key in override["services"]["api"]["environment"]:
        if key in candidate["environment"]:
            normalized["services"]["api"]["environment"][key] = candidate["environment"][key]
        else:
            normalized["services"]["api"]["environment"].pop(key, None)
    assert normalized == rendered
    emit("proposed_render", {"only_two_source_keys_change": True, "other_services_unchanged": True,
                             "source": "ITS_CURRENT", "fallback": "GTFSRT_BUS", "current_image_preserved_for_render_only": True,
                             "applied": False})
    emit("compose", {"files": files, "project_directory": directory, "env_file": env_file,
                     "image": api["Config"]["Image"], "rendered_image_matches": candidate["image"] == api["Config"]["Image"],
                     "environment": safe_settings(environment), "rendered_environment": safe_settings(candidate.get("environment", {})),
                     "memory_bytes": api["HostConfig"]["Memory"], "nano_cpus": api["HostConfig"]["NanoCpus"],
                     "pids_limit": api["HostConfig"]["PidsLimit"], "restarts": api["RestartCount"],
                     "oom_killed": api["State"]["OOMKilled"], "started": api["State"]["StartedAt"]})
    mem = dict((s.split(":")[0], int(s.split()[1])) for s in open("/proc/meminfo") if s.startswith(("MemTotal:", "MemAvailable:", "SwapTotal:", "SwapFree:")))
    emit("host", {"utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "cpus": os.cpu_count(), "load": os.getloadavg(), "memory_kib": mem})
    stats = [json.loads(x) for x in run(["docker", "stats", "--no-stream", "--format", "{{json .}}"], timeout=20).splitlines()]
    emit("containers", [{k: row[k] for k in ("Name", "CPUPerc", "MemUsage", "MemPerc", "PIDs")} for row in stats])

    pg = json.loads(run(["docker", "inspect", "transporte_postgres"]))[0]
    pg_env = dict(x.split("=", 1) for x in pg["Config"]["Env"] if "=" in x)
    user = pg_env.get("POSTGRES_USER", "postgres")
    database = pg_env.get("POSTGRES_DB", user)
    assert environment.get("POSTGRES_DB") == database
    aliases = {pg["Name"].lstrip("/"), pg["Config"]["Hostname"]}
    for network in pg["NetworkSettings"]["Networks"].values():
        aliases.update(network.get("Aliases") or [])
        aliases.add(network["IPAddress"])
    emit("database_binding", {"database_matches_api": True,
        "host_matches_postgres_container": environment.get("POSTGRES_HOST") in aliases,
        "credentials": "never emitted", "transaction": "READ ONLY"})
    def sql(query):
        wrapped = "BEGIN READ ONLY; SET LOCAL statement_timeout='3s'; SET LOCAL lock_timeout='500ms'; SET LOCAL idle_in_transaction_session_timeout='5s'; " + query + "; COMMIT;"
        result = run(["docker", "exec", "-i", "transporte_postgres", "psql", "-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-U", user, "-d", database], stdin=wrapped)
        return [json.loads(x) for x in result.splitlines() if x.startswith("{")]
    emit("structure", sql('''SELECT json_build_object('route_ids',count(*),'distinct_route_ids',count(DISTINCT lie."ExternalId"),
        'lines',count(DISTINCT lie."LinhaId"),'agency_ids',count(*) FILTER (WHERE EXISTS (SELECT 1 FROM "LinhasIdentidadesExternas" a WHERE a."FonteEstruturalId"=lie."FonteEstruturalId" AND a."LinhaId"=lie."LinhaId" AND a."Tipo"='ROUTE_AGENCY' AND starts_with(a."ExternalId",lie."ExternalId"||':'))),
        'origin_ids',count(*) FILTER (WHERE EXISTS (SELECT 1 FROM "LinhasIdentidadesExternas" a WHERE a."FonteEstruturalId"=lie."FonteEstruturalId" AND a."LinhaId"=lie."LinhaId" AND a."Tipo"='ROUTE_ORIGIN_V1' AND starts_with(a."ExternalId",lie."ExternalId"||':'))))
        FROM "LinhasIdentidadesExternas" lie JOIN "FontesEstruturais" f ON f."Id"=lie."FonteEstruturalId" WHERE f."Codigo"='DATARIO_GTFS' AND lie."Tipo"='ROUTE_ID' '''))
    emit("schema", sql('''SELECT json_build_object('migrations',count(*),'latest',max("MigrationId")) FROM "__EFMigrationsHistory"'''))
    for line in ("838", "898", "006"):
        with urllib.request.urlopen("http://127.0.0.1:8080/veiculos/linha/" + line, timeout=5) as response:
            body = response.read(1_000_001)
        assert len(body) <= 1_000_000
        payload = json.loads(body)
        positions = payload.get("posicoes", [])
        now = datetime.datetime.now(datetime.timezone.utc)
        ages = [(now - datetime.datetime.fromisoformat(p["timestampGps"].replace("Z", "+00:00"))).total_seconds() for p in positions]
        emit("line_baseline", {"line": line, "vehicles": len(positions), "until40": sum(0 <= x <= 40 for x in ages),
                               "until60": sum(0 <= x <= 60 for x in ages), "until180": sum(0 <= x <= 180 for x in ages),
                               "future": sum(x < 0 for x in ages), "max_age": max(ages, default=None)})
except Exception as error:
    emit("blocked", {"type": type(error).__name__, "detail": "values omitted; no writes attempted"})
    raise SystemExit(1)
