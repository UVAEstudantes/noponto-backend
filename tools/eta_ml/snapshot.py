"""3B.2B: prepare/seal offline; restore explicitly local. No source connection code."""
import argparse
import csv
import hashlib
import json
import re
import subprocess
from decimal import Decimal
from datetime import datetime, timezone
from pathlib import Path
from uuid import UUID
if __package__:
    from .collection_profiles import PROFILES, CURRENT
    from . import snapshot_schema
else:
    from collection_profiles import PROFILES, CURRENT
    import snapshot_schema

PROFILE = PROFILES[CURRENT]
STRUCTURE = ("Modais", "FontesEstruturais", "Linhas", "Sentidos", "Paradas",
             "PadroesOperacionais", "PadroesVersoes", "OcorrenciasParadasPadroes")
HEAVY = ("TelemetriasVeiculoMl", "HistoricoPassagens", "EventosViagem")
TABLES = STRUCTURE + HEAVY
VERSION = "noponto-snapshot-3b2b-v2"
LEGACY_VERSION = "noponto-snapshot-3b2b-v1"
# v2 catalog was exported with public visible; trigger/function evidence with
# only pg_catalog visible. Preserve BOTH representations of already sealed v2.
CATALOG_SEARCH_PATH = "public,pg_catalog"
SCHEMA_SEARCH_PATH = "pg_catalog"
# Versioned geometries can exceed csv's 128KiB default; retain a finite per-row cap.
csv.field_size_limit(64 * 1024 * 1024)


def utc(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None or result.utcoffset().total_seconds() != 0:
        raise ValueError("Expected UTC timestamp")
    return result.astimezone(timezone.utc)


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True,
                      separators=(",", ":"), allow_nan=False) + "\n"


def write(path, value):
    Path(path).write_text(value, encoding="utf-8", newline="\n")


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def validate(request):
    if (set(request) != {"profile", "snapshot_end", "trips"}
            or canonical(request["profile"]) != canonical(PROFILE)):
        raise ValueError("Official release/cutoff/contract mismatch")
    end = utc(request["snapshot_end"])
    if end <= utc(PROFILE["cutoff"]) or not request["trips"]:
        raise ValueError("Snapshot needs an end and explicit audited trips")
    ids = set()
    for trip in request["trips"]:
        if set(trip) != {"viagem_id", "inicio", "fim", "codigo_linha", "referencia_auditoria"}:
            raise ValueError("Invalid trip fields")
        uid = str(UUID(trip["viagem_id"]))
        if uid != trip["viagem_id"] or UUID(uid).int == 0 or uid in ids:
            raise ValueError("Invalid/duplicate trip UUID")
        ids.add(uid)
        if not utc(PROFILE["cutoff"]) <= utc(trip["inicio"]) < utc(trip["fim"]) < end:
            raise ValueError("Trip is partial/pre-cutoff/open/outside snapshot")
        if not trip["codigo_linha"].strip() or not trip["referencia_auditoria"].strip():
            raise ValueError("Missing line/audit evidence")
    return {**request, "trips": sorted(request["trips"], key=lambda t: t["viagem_id"])}


def literal(value):
    return "'" + value.replace("'", "''") + "'"


def query(table, request):
    if table not in TABLES:
        raise ValueError("Table outside allowlist")
    if table in HEAVY and not request["trips"]:
        raise ValueError("Heavy source requires explicit trip selection")
    # Explicit EWKB avoids PostGIS geometry->JSON/GeoJSON casts: geometry input
    # accepts hex EWKB and preserves SRID, dimensions and exact coordinates.
    geometry = {"Paradas": "Localizacao", "PadroesVersoes": "Geometria"}.get(table)
    expression = "to_jsonb(t)"
    if geometry:
        expression += f" || jsonb_build_object('{geometry}',encode(ST_AsEWKB(t.\"{geometry}\"),'hex'))"
    base = f'SELECT {expression} AS row FROM public."{table}" t'
    if table in HEAVY:
        ids = ",".join(literal(t["viagem_id"]) for t in request["trips"])
        if table == "EventosViagem":
            # Explicit IDs plus indexed event-time windows; no global GPS scan.
            windows = " OR ".join(
                f'(t."Payload"->>\'viagem_id\'={literal(t["viagem_id"])} '
                f'AND t."TimestampEvento">={literal(t["inicio"])}::timestamptz '
                f'AND t."TimestampEvento"<={literal(t["fim"])}::timestamptz)'
                for t in request["trips"])
            base += f" WHERE t.\"Payload\"->>'viagem_id' IN ({ids}) AND ({windows})"
        else:
            base += f' WHERE t."ViagemId" IN ({ids})'
    key = "EventId" if table == "EventosViagem" else "Id"
    return base + f' ORDER BY t."{key}"'


def catalog_query():
    names = ",".join(literal(t) for t in TABLES)
    return f"""SELECT jsonb_build_object('table',c.relname,'columns',
 (SELECT jsonb_agg(jsonb_build_object('name',a.attname,'type',format_type(a.atttypid,a.atttypmod)) ORDER BY a.attnum)
  FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped),
 'foreign_keys',COALESCE((SELECT jsonb_agg(jsonb_build_object('name',k.conname,'parent',p.relname,
 'parent_schema',pn.nspname,'definition',pg_get_constraintdef(k.oid)) ORDER BY k.conname)
 FROM pg_constraint k JOIN pg_class p ON p.oid=k.confrelid JOIN pg_namespace pn ON pn.oid=p.relnamespace
 WHERE k.conrelid=c.oid AND k.contype='f'),'[]'::jsonb)) AS row
 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname='public' AND c.relname IN ({names}) ORDER BY c.relname""".replace("\n", " ")


def prepare(request, directory):
    request = validate(request)
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=False)
    write(directory / "request.json", canonical(request))
    write(directory / "bundle.version.json", canonical(VERSION))
    # Relative paths: psql must run with this directory as cwd. Never interpolate paths.
    names = ",".join(literal(t) for t in TABLES)
    sql = ["\\set ON_ERROR_STOP on", "BEGIN READ ONLY;",
           "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;",
           "SET LOCAL statement_timeout='60s';", "SET LOCAL lock_timeout='2s';",
           "SET LOCAL work_mem='8MB';", "SET LOCAL timezone='UTC';",
           "SET LOCAL extra_float_digits=3;",
           "SELECT 1 / CASE WHEN CURRENT_TIMESTAMP>=" + literal(request["snapshot_end"])
           + "::timestamptz THEN 1 ELSE 0 END AS snapshot_end_guard;",
           "SELECT 1 / CASE WHEN (SELECT max(\"MigrationId\") FROM public.\"__EFMigrationsHistory\")="
           + literal(PROFILE["migration"]) + " THEN 1 ELSE 0 END AS migration_guard;",
           # Unknown external parents/generated columns abort before copying data.
           f"SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_class p ON p.oid=k.confrelid JOIN pg_namespace pn ON pn.oid=p.relnamespace WHERE k.contype='f' AND n.nspname='public' AND c.relname IN ({names}) AND (pn.nspname<>'public' OR p.relname NOT IN ({names}))) THEN 1 ELSE 0 END AS fk_guard;",
           f"SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relname IN ({names}) AND (a.attgenerated<>'' OR a.attidentity<>'')) THEN 1 ELSE 0 END AS generated_guard;",
           f"SET LOCAL search_path={CATALOG_SEARCH_PATH};",
           f"\\copy ({catalog_query()}) TO 'catalog.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')",
           "\\copy (SELECT jsonb_build_object('snapshot_at',CURRENT_TIMESTAMP,'mvcc_snapshot',pg_current_snapshot()::text,'postgresql',current_setting('server_version'),'postgis',postgis_lib_version()) AS row) TO 'source_context.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')"]
    # Stable deparser context; definitions themselves are preserved verbatim.
    sql.append(f"SET LOCAL search_path={SCHEMA_SEARCH_PATH};")
    for name, statement in snapshot_schema.queries(TABLES).items():
        sql.append(f"\\copy ({statement}) TO '{name}.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')")
    sql.append("SET LOCAL search_path=public,pg_catalog;")
    plans = ["\\set ON_ERROR_STOP on", "BEGIN READ ONLY;", "SET LOCAL statement_timeout='10s';",
             "SET LOCAL lock_timeout='2s';", "SET LOCAL work_mem='8MB';"]
    for table in TABLES:
        sql.append(f"\\copy ({query(table, request)}) TO '{table}.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')")
        if table in HEAVY:
            plans.append("EXPLAIN (COSTS ON) " + query(table, request) + ";")
    sql.append("COMMIT;")
    sql.append("\\copy (SELECT to_jsonb('completed-read-only-export'::text) AS row) TO 'completed.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')")
    plans.append("COMMIT;")
    write(directory / "export.sql", "\n".join(sql) + "\n")
    write(directory / "plans.sql", "\n".join(plans) + "\n")
    # Shell template uses libpq service, never host/password or remote address.
    tables = " ".join("--table=" + "'public.\"" + t + "\"'" for t in TABLES)
    write(directory / "export.sh", "#!/bin/sh\nset -eu\n: \"${PGSERVICE:?Set operator-configured read-only libpq service}\"\n"
          "test ! -e schema.dump\n"
          "export PGOPTIONS='-c default_transaction_read_only=on -c statement_timeout=60000 -c lock_timeout=2000'\n"
          f"pg_dump --no-password --format=custom --schema-only --no-owner --no-privileges {tables} --file=schema.dump\n"
          "psql -X --no-password -v ON_ERROR_STOP=1 -f export.sql\n")
    return request


def rows(path):
    with Path(path).open(encoding="utf-8", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames != ["row"]:
            raise ValueError("Unexpected CSV header")
        for row in reader:
            if set(row) != {"row"} or row["row"] is None:
                raise ValueError("Malformed CSV")
            yield json.loads(row["row"])


def inventory(directory, request):
    directory = Path(directory)
    catalog = list(rows(directory / "catalog.csv"))
    by_table = {r["table"]: r for r in catalog}
    if len(catalog) != len(TABLES) or set(by_table) != set(TABLES):
        raise ValueError("Incomplete source schema")
    for r in catalog:
        if any(fk["parent"] not in TABLES or fk["parent_schema"] != "public"
               for fk in r["foreign_keys"]):
            raise ValueError("Unclosed FK dependency")
    trips = {t["viagem_id"]: t for t in request["trips"]}
    counts, bounds = {}, {}
    for table in TABLES:
        counts[table] = 0
        columns = {c["name"] for c in by_table[table]["columns"]}
        geometry = {"Paradas": "Localizacao", "PadroesVersoes": "Geometria"}.get(table)
        if any(c["type"].startswith(("geometry", "geography")) and c["name"] != geometry
               for c in by_table[table]["columns"]):
            raise ValueError("Unreviewed spatial column")
        for row in rows(directory / (table + ".csv")):
            counts[table] += 1
            if set(row) != columns:
                raise ValueError("Full source row/columns mismatch")
            if table in HEAVY:
                payload = row["Payload"] if table == "EventosViagem" else None
                trip_id = payload.get("viagem_id") if payload is not None else row.get("ViagemId")
                if trip_id not in trips:
                    raise ValueError("Heavy source contains unselected trip")
                trip = trips[trip_id]
                if table == "EventosViagem" and row["Tipo"] in ("ViagemIniciada", "ViagemFinalizada"):
                    kind = row["Tipo"]
                    expected = trip["inicio"] if kind == "ViagemIniciada" else trip["fim"]
                    event_id = ("inicio:" if kind == "ViagemIniciada" else "fim:") + trip_id
                    if (row["EventId"] != event_id or payload.get("event_id") != event_id
                            or payload.get("tipo") != kind or payload.get("schema_version") != 2
                            or payload.get("codigo_linha") != trip["codigo_linha"]
                            or not same_pg_time(row["TimestampEvento"], expected)
                            or utc(payload["timestamp_evento"]) != utc(expected)
                            or (trip_id, kind) in bounds):
                        raise ValueError("Journal boundary incompatible with audit")
                    bounds[trip_id, kind] = True
    if len(bounds) != 2 * len(trips):
        raise ValueError("Missing closed journal boundaries")
    return counts


def same_pg_time(actual, expected):
    # JSON carries 100ns ticks; PostgreSQL timestamptz stores microseconds.
    def ticks(value):
        utc(value)
        value = value.replace("Z", "+00:00")
        match = re.fullmatch(r"(.{19})(?:\.(\d{1,7}))?\+00:00", value)
        if not match:
            raise ValueError("Expected ISO UTC timestamp")
        whole = datetime.fromisoformat(match[1] + "+00:00")
        seconds = (whole - datetime(1970, 1, 1, tzinfo=timezone.utc)).total_seconds()
        return Decimal(int(seconds)) + Decimal("0." + (match[2] or "0"))
    return abs(ticks(actual) - ticks(expected)) <= Decimal("0.0000009")


def files(directory, version=VERSION):
    names = ["request.json", "export.sql", "plans.sql", "export.sh", "schema.dump", "catalog.csv",
             "source_context.csv", "completed.csv"]
    names += [t + ".csv" for t in TABLES]
    if version == VERSION:
        names += ["bundle.version.json", "functions.csv", "triggers.csv", "dependencies.csv", "functions.sql"]
    return {name: sha(Path(directory) / name) for name in sorted(names)}


def seal(directory):
    directory = Path(directory)
    if (directory / "snapshot.manifest.json").exists():
        raise ValueError("Manifest already exists; use verify")
    if json.loads((directory / "bundle.version.json").read_text(encoding="utf-8")) != VERSION:
        raise ValueError("New exports must use snapshot v2; do not upgrade sealed bundles")
    request = validate(json.loads((directory / "request.json").read_text(encoding="utf-8")))
    evidence = schema_evidence(directory)
    write(directory / "functions.sql", snapshot_schema.sql(evidence["functions"]))
    manifest = manifest_for(directory, request)
    write(directory / "snapshot.manifest.json", canonical(manifest))
    return manifest


def verify(directory):
    directory = Path(directory)
    manifest = json.loads((directory / "snapshot.manifest.json").read_text(encoding="utf-8"))
    request = validate(json.loads((directory / "request.json").read_text(encoding="utf-8")))
    expected = manifest_for(directory, request, manifest.get("snapshot_version"))
    if manifest != expected:
        raise ValueError("Snapshot manifest/count/hash mismatch")
    return manifest


def schema_evidence(directory):
    evidence = {name: list(rows(directory / (name + ".csv")))
                for name in ("functions", "triggers", "dependencies")}
    snapshot_schema.validate(**evidence)
    return evidence


def manifest_for(directory, request, version=VERSION):
    if version not in (VERSION, LEGACY_VERSION):
        raise ValueError("Unsupported snapshot version")
    extra = {}
    if version == VERSION:
        if json.loads((directory / "bundle.version.json").read_text(encoding="utf-8")) != VERSION:
            raise ValueError("Snapshot version mismatch")
        evidence = schema_evidence(directory)
        if (directory / "functions.sql").read_text(encoding="utf-8") != snapshot_schema.sql(evidence["functions"]):
            raise ValueError("Function SQL differs from source evidence")
        extra["schema_dependencies"] = evidence
    context = list(rows(directory / "source_context.csv"))
    if (list(rows(directory / "completed.csv")) != ["completed-read-only-export"]
            or len(context) != 1 or utc(context[0]["snapshot_at"]) < utc(request["snapshot_end"])):
        raise ValueError("Incomplete export/invalid MVCC snapshot context")
    with (directory / "schema.dump").open("rb") as stream:
        if stream.read(5) != b"PGDMP":
            raise ValueError("Expected custom schema-only pg_dump archive")
    if version == VERSION and (not context[0]["postgresql"].split(".")[0].split()[0] == "16"
                              or not re.match(r"^3\.4(?:\.|$)", context[0]["postgis"])):
        raise ValueError("Snapshot requires PostgreSQL 16/PostGIS 3.4")
    return {"snapshot_version": version, **request["profile"], **extra,
            "snapshot_end": request["snapshot_end"], "source_context": context[0],
            "trips": request["trips"], "viagem_ids": [t["viagem_id"] for t in request["trips"]],
            "counts": inventory(directory, request), "files_sha256": files(directory, version)}


def restore_sql(directory, manifest):
    catalog = {r["table"]: r for r in rows(Path(directory) / "catalog.csv")}
    sql = ["\\set ON_ERROR_STOP on", "\\encoding UTF8", "BEGIN;",
           "CREATE TEMP TABLE snapshot_row (row jsonb) ON COMMIT DROP;"]
    for table in TABLES:
        cols = ",".join('"' + c["name"].replace('"', '""') + '"' for c in catalog[table]["columns"])
        # Temporary staging only; target rows are never changed/dropped to bypass FKs.
        sql += ["TRUNCATE snapshot_row;",
                f"\\copy snapshot_row FROM '{table}.csv' WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')",
                f'INSERT INTO public."{table}" ({cols}) SELECT {cols} FROM snapshot_row s CROSS JOIN LATERAL jsonb_populate_record(NULL::public."{table}",s.row) r;',
                f'SELECT 1 / CASE WHEN (SELECT count(*) FROM public."{table}")={manifest["counts"][table]} THEN 1 ELSE 0 END AS count_guard;']
    sql.append("COMMIT;")
    return "\n".join(sql) + "\n"


def local_args(database, port):
    # Plain DB identifier only. No connection string/service/host input can override loopback.
    if not re.fullmatch(r"eta_snapshot_[a-z0-9_]+", database) or not 1 <= port <= 65535:
        raise ValueError("Use a dedicated eta_snapshot_* database and valid local port")
    return ["--host=127.0.0.1", f"--port={port}", f"--dbname={database}", "--no-password"]


def metadata_sql(statement, search_path):
    if search_path not in (CATALOG_SEARCH_PATH, SCHEMA_SEARCH_PATH):
        raise ValueError("Unsupported metadata context")
    return ("BEGIN READ ONLY; SET LOCAL statement_timeout='60s'; SET LOCAL lock_timeout='2s'; "
            f"SET LOCAL search_path={search_path}; " + statement + "; COMMIT;")


def compare_restored(directory, manifest, args):
    queries = {"catalog": (catalog_query(), CATALOG_SEARCH_PATH),
               **{name: (query, SCHEMA_SEARCH_PATH)
                  for name, query in snapshot_schema.queries(TABLES).items()}}
    for name, (statement, context) in queries.items():
        result = subprocess.run(["psql", "-X", *args, "-v", "ON_ERROR_STOP=1", "-qAt", "-c",
                                 metadata_sql(statement, context)], cwd=directory, check=True,
                                timeout=60, capture_output=True, text=True, encoding="utf-8")
        actual = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
        expected = (list(rows(directory / "catalog.csv")) if name == "catalog"
                    else manifest["schema_dependencies"][name])
        if actual != expected:
            raise ValueError("Restored " + name + " differ from source evidence")


def check_restored(directory, database, port):
    """Compare metadata in an existing LOCAL restored database; never restore/write."""
    args = local_args(database, port)
    directory = Path(directory).resolve()
    manifest = verify(directory)
    if manifest["snapshot_version"] != VERSION:
        raise ValueError("check-restored requires snapshot v2")
    compare_restored(directory, manifest, args)


def restore(directory, database, port):
    args = local_args(database, port)
    directory = Path(directory).resolve()
    manifest = verify(directory)
    if manifest["snapshot_version"] != VERSION:
        raise ValueError("Legacy v1 is verifiable only; prepare a NEW v2 snapshot with function evidence")
    def run(command, **kwargs):
        subprocess.run(command, cwd=directory, check=True, timeout=300, **kwargs)
    # Dedicated NEW empty database; extension must be preinstalled by operator locally.
    run(["psql", "-X", *args, "-v", "ON_ERROR_STOP=1", "-c",
         "SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p') AND NOT EXISTS (SELECT 1 FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid WHERE d.classid='pg_class'::regclass AND d.objid=c.oid AND d.deptype='e')) AND NOT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.deptype='e')) THEN 1 ELSE 0 END AS empty_guard; SELECT 1 / CASE WHEN current_setting('server_version_num')::int BETWEEN 160000 AND 169999 AND (postgis_lib_version()='3.4' OR postgis_lib_version() LIKE '3.4.%') THEN 1 ELSE 0 END AS versions_guard;"])
    run(["pg_restore", *args, "--exit-on-error", "--no-owner", "--no-privileges", "--section=pre-data", "schema.dump"])
    run(["psql", "-X", *args, "-v", "ON_ERROR_STOP=1"],
        input=restore_sql(directory, manifest), text=True, encoding="utf-8")
    run(["psql", "-X", *args, "-v", "ON_ERROR_STOP=1", "-f", "functions.sql"])
    # FKs/unique/check constraints from source are created AND validated, never disabled.
    run(["pg_restore", *args, "--exit-on-error", "--no-owner", "--no-privileges", "--section=post-data", "schema.dump"])
    run(["psql", "-X", *args, "-v", "ON_ERROR_STOP=1", "-c",
         "SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND NOT k.convalidated) THEN 1 ELSE 0 END AS constraints_guard;"])
    compare_restored(directory, manifest, args)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("prepare")
    p.add_argument("request")
    p.add_argument("directory")
    for name in ("seal", "verify", "restore", "check-restored"):
        p = sub.add_parser(name)
        p.add_argument("directory")
        if name in ("restore", "check-restored"):
            p.add_argument("--database", required=True)
            p.add_argument("--port", type=int, required=True)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(json.loads(Path(args.request).read_text(encoding="utf-8-sig")), args.directory)
    elif args.command == "restore":
        restore(args.directory, args.database, args.port)
    elif args.command == "check-restored":
        check_restored(args.directory, args.database, args.port)
        print("Restored metadata match sealed v2; geometry/content validation remains separate.")
    else:
        print(canonical(globals()[args.command](args.directory)), end="")


if __name__ == "__main__":
    main()
