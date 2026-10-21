"""Opt-in, local-only Combined SQL laboratory; never accepts a connection string.

Requires psycopg2 and a newly provisioned, labelled tmpfs Docker fixture. Private
state is outside Git. The bootstrap is documented in report 46. No application
startup, operational service, migration or existing database is used.
"""
import argparse
import collections
import csv
import hashlib
import io
import json
import math
import pathlib
import random
import re
import statistics
import subprocess
import time
import uuid
import zipfile

import psycopg2
from psycopg2.extras import execute_values

SCHEMA = "combined46"
SQL_PATH = pathlib.Path("NoPonto/4-Data/Repositories/GpsItinerarioRepository.CombinedSetBased.cs")
NS = uuid.UUID("d47b0000-0000-0000-0000-000000000046")


def ident(value):
    return str(uuid.uuid5(NS, str(value)))


def guarded_connection(state_path):
    state = json.loads(pathlib.Path(state_path).read_text(encoding="utf-8-sig"))
    if state["context"] != "desktop-linux" or not re.fullmatch(r"noponto-combined-[a-f0-9]+", state["container"]):
        raise RuntimeError("Not the disposable local fixture")
    meta = json.loads(subprocess.check_output(
        ["docker", "--context", state["context"], "inspect", state["id"]], text=True))[0]
    ports = meta["NetworkSettings"]["Ports"]["5432/tcp"]
    if (meta["Id"] != state["id"] or meta["Name"] != "/" + state["container"]
            or meta["Config"]["Labels"].get("noponto.combined.guard") != state["guard"]
            or meta["Config"]["Image"] != "postgis/postgis:16-3.4"
            or meta["Mounts"] or "/var/lib/postgresql/data" not in meta["HostConfig"]["Tmpfs"]
            or len(ports) != 1 or ports[0]["HostIp"] != "127.0.0.1"
            or int(ports[0]["HostPort"]) != state["port"]):
        raise RuntimeError("Disposable container ownership/isolation check failed")
    conn = psycopg2.connect(host="127.0.0.1", port=state["port"], dbname=state["database"],
                            user="postgres", password=state["password"], connect_timeout=5)
    conn.autocommit = True
    with conn.cursor() as cur:
        cur.execute("SELECT token FROM public.combined_fixture_guard")
        if cur.fetchall() != [(state["guard"],)]:
            conn.close()
            raise RuntimeError("Fixture ownership marker mismatch")
        cur.execute("SELECT current_database()")
        if cur.fetchone()[0] != state["database"]:
            raise RuntimeError("Database identity mismatch")
    return conn


def sql_from_source(source):
    match = re.search(r'private const string SqlMatchingCombinadoLoteSetBased = """\r?\n(.*?)\r?\n\s*""";', source, re.S)
    if not match:
        raise RuntimeError("Combined SQL not found")
    return match[1].replace("@inputs", "%(inputs)s")


def seed(conn, zip_path, output):
    with zipfile.ZipFile(zip_path) as z:
        bus_routes = [r for r in csv.DictReader(io.TextIOWrapper(z.open("routes.txt"), encoding="utf-8-sig"))
                      if r["route_type"] in ("3", "700", "200")]
    if len(bus_routes) < 100:
        raise RuntimeError("Representative BUS static catalog missing; refusing DDL")
    with conn.cursor() as cur:
        # Seed is allowed only in the brand-new database containing our guard.
        cur.execute("""SELECT n.nspname,c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
          WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema')
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid='pg_class'::regclass
            AND d.objid=c.oid AND d.deptype='e' AND d.refclassid='pg_extension'::regclass)""")
        if cur.fetchall() != [("public", "combined_fixture_guard")]:
            raise RuntimeError("Seed refuses any preexisting user tables")
        cur.execute(f'CREATE SCHEMA {SCHEMA}; SET search_path={SCHEMA},public')
        cur.execute('''
          CREATE TABLE "Linhas"("Id" uuid PRIMARY KEY,"Codigo" text NOT NULL);
          CREATE INDEX ON "Linhas"("Codigo");
          CREATE TABLE "Sentidos"("Id" uuid PRIMARY KEY,"LinhaId" uuid NOT NULL);
          CREATE INDEX ON "Sentidos"("LinhaId");
          CREATE TABLE "PadroesOperacionais"("Id" uuid PRIMARY KEY,"SentidoId" uuid NOT NULL,"VersaoAtualId" uuid);
          CREATE INDEX ON "PadroesOperacionais"("SentidoId");
          CREATE TABLE "PadroesVersoes"("Id" uuid PRIMARY KEY,"PadraoOperacionalId" uuid NOT NULL,"Geometria" geometry(LineString,4326) NOT NULL,"Topologia" text NOT NULL);
          CREATE INDEX ON "PadroesVersoes"("PadraoOperacionalId");
          CREATE INDEX ON "PadroesVersoes" USING gist("Geometria");
          CREATE TABLE "Paradas"("Id" uuid PRIMARY KEY,"Nome" text,"Localizacao" geometry(Point,4326));
          CREATE TABLE "OcorrenciasParadasPadroes"("Id" uuid PRIMARY KEY,"ParadaId" uuid,"PadraoVersaoId" uuid,"Ordem" int,"PosicaoTracado" float8,"DistanciaAcumuladaMetros" float8 NOT NULL DEFAULT 0,"DistanciaDaLinhaMetros" float8 NOT NULL DEFAULT 0);
          CREATE INDEX ON "OcorrenciasParadasPadroes"("PadraoVersaoId","PosicaoTracado");
          CREATE INDEX ON "OcorrenciasParadasPadroes"("PadraoVersaoId","Ordem");
          CREATE INDEX ON "OcorrenciasParadasPadroes"("ParadaId");
        ''')
        with zipfile.ZipFile(zip_path) as z:
            def rows(name):
                return csv.DictReader(io.TextIOWrapper(z.open(name), encoding="utf-8-sig"))
            routes = {r["route_id"]: r for r in bus_routes}
            patterns = {}
            trip_patterns = {}
            for t in rows("trips.txt"):
                if t["route_id"] not in routes or not t["shape_id"]:
                    continue
                key = (t["route_id"], t["direction_id"], t["shape_id"])
                if key not in patterns:
                    patterns[key] = t["trip_id"]
                    trip_patterns[t["trip_id"]] = key
            shapes = collections.defaultdict(list)
            for p in rows("shapes.txt"):
                shapes[p["shape_id"]].append((int(p["shape_pt_sequence"]), float(p["shape_pt_lon"]), float(p["shape_pt_lat"])))
            stops = {s["stop_id"]: (float(s["stop_lon"]), float(s["stop_lat"])) for s in rows("stops.txt")}
            occurrences = collections.defaultdict(list)
            for o in rows("stop_times.txt"):
                if o["trip_id"] in trip_patterns and o["stop_id"] in stops:
                    occurrences[trip_patterns[o["trip_id"]]].append((int(o["stop_sequence"]), o["stop_id"]))
        execute_values(cur, 'INSERT INTO "Linhas" VALUES %s', [(ident("line"+k), v["route_short_name"]) for k,v in routes.items()])
        directions = sorted({(k[0], k[1]) for k in patterns})
        execute_values(cur, 'INSERT INTO "Sentidos" VALUES %s', [(ident("dir"+str(k)), ident("line"+k[0])) for k in directions])
        valid = {}
        for key in sorted(patterns):
            coords = [(x,y) for _,x,y in sorted(shapes[key[2]])]
            if len(coords) < 2 or len(set(coords)) < 2:
                continue
            pid = ident("pattern"+str(key))
            version = ident("current"+str(key))
            old = ident("previous"+str(key))
            valid[key] = (pid, version, old)
            wkt = "LINESTRING("+",".join(f"{x} {y}" for x,y in coords)+")"
            cur.execute('INSERT INTO "PadroesOperacionais" VALUES(%s,%s,%s)', (pid,ident("dir"+str(key[:2])),version))
            topology = "CIRCULAR" if coords[0] == coords[-1] else "LINEAR"
            for vid in (version,old):
                cur.execute('INSERT INTO "PadroesVersoes" VALUES(%s,%s,ST_GeomFromText(%s,4326),%s)', (vid,pid,wkt,topology))
        execute_values(cur, 'INSERT INTO "Paradas" VALUES %s', [(ident("stop"+k),"Fixture stop",f"SRID=4326;POINT({x} {y})") for k,(x,y) in stops.items()])
        # Reconstruct fractions from real static points; not a replay of importer snapping.
        values = []
        for key, (_,version,old) in valid.items():
            for order,stop in sorted(occurrences[key]):
                for vid in (version,old):
                    values.append((ident(f"occ:{vid}:{order}"),ident("stop"+stop),vid,order))
        execute_values(cur, '''INSERT INTO "OcorrenciasParadasPadroes"("Id","ParadaId","PadraoVersaoId","Ordem","PosicaoTracado")
          SELECT x.id::uuid,x.stop::uuid,x.version::uuid,x.ord,ST_LineLocatePoint(v."Geometria",p."Localizacao")
          FROM (VALUES %s) x(id,stop,version,ord) JOIN "Paradas" p ON p."Id"=x.stop::uuid JOIN "PadroesVersoes" v ON v."Id"=x.version::uuid''', values, page_size=500)
        # Exact score tie (UUID decides), circular wrap and repeated stop on one route.
        cur.execute('''
          INSERT INTO "Linhas" VALUES('46000000-0000-0000-0000-000000000001','LAB46-TIE');
          INSERT INTO "Sentidos" VALUES('46000000-0000-0000-0000-000000000002','46000000-0000-0000-0000-000000000001');
          INSERT INTO "PadroesOperacionais" VALUES
            ('46000000-0000-0000-0000-000000000011','46000000-0000-0000-0000-000000000002','46000000-0000-0000-0000-000000000021'),
            ('46000000-0000-0000-0000-000000000012','46000000-0000-0000-0000-000000000002','46000000-0000-0000-0000-000000000022');
          INSERT INTO "PadroesVersoes" SELECT vid,pid,ST_GeomFromText('LINESTRING(0 0,.01 0,.01 .01,0 .01,0 0)',4326),'CIRCULAR'
          FROM (VALUES ('46000000-0000-0000-0000-000000000021'::uuid,'46000000-0000-0000-0000-000000000011'::uuid),
                       ('46000000-0000-0000-0000-000000000022'::uuid,'46000000-0000-0000-0000-000000000012'::uuid),
                       ('46000000-0000-0000-0000-000000000031'::uuid,'46000000-0000-0000-0000-000000000011'::uuid),
                       ('46000000-0000-0000-0000-000000000032'::uuid,'46000000-0000-0000-0000-000000000012'::uuid)) x(vid,pid);
          INSERT INTO "Paradas" VALUES('46000000-0000-0000-0000-000000000003','Repeated fixture stop',ST_SetSRID(ST_MakePoint(0,0),4326));
          INSERT INTO "OcorrenciasParadasPadroes"
          SELECT md5(v."Id"::text||f::text)::uuid,'46000000-0000-0000-0000-000000000003',v."Id",(f*10)::int,f,0,0
          FROM "PadroesVersoes" v CROSS JOIN (VALUES(0.0),(0.75)) x(f) WHERE v."PadraoOperacionalId" IN
          ('46000000-0000-0000-0000-000000000011','46000000-0000-0000-0000-000000000012');
          ANALYZE;
        ''')
        cur.execute('''SELECT (SELECT count(*) FROM "Linhas"),(SELECT count(*) FROM "PadroesOperacionais"),count(*),sum(ST_NPoints("Geometria")),percentile_cont(.5) WITHIN GROUP(ORDER BY ST_NPoints("Geometria")),max(ST_NPoints("Geometria")),count(*) FILTER(WHERE "Topologia"='CIRCULAR'),(SELECT count(*) FROM "OcorrenciasParadasPadroes") FROM "PadroesVersoes"''')
        catalog = cur.fetchone()
    output.mkdir(parents=True, exist_ok=True)
    meta = dict(zip(["lines","patterns","versions","points","median_vertices","max_vertices","circular_versions","occurrences"],catalog))
    meta["gtfs_sha256"] = hashlib.sha256(pathlib.Path(zip_path).read_bytes()).hexdigest()
    meta["positions"] = "reconstructed; not production replay"
    (output/"catalog.json").write_text(json.dumps(meta,indent=2))
    print(json.dumps(meta))


def load_inputs(conn, n, high, concentrated=False):
    with conn.cursor() as cur:
        cur.execute(f'SET search_path={SCHEMA},public')
        cur.execute('''WITH samples AS (
          SELECT l."Id" line,s."Id" direction,po."Id" pattern,pv."Id" version,l."Codigo" code,pv."Geometria" geom,
            (SELECT "Id" FROM "PadroesVersoes" WHERE "PadraoOperacionalId"=po."Id" AND "Id"<>pv."Id" LIMIT 1) old
          FROM "Linhas" l JOIN "Sentidos" s ON s."LinhaId"=l."Id" JOIN "PadroesOperacionais" po ON po."SentidoId"=s."Id" JOIN "PadroesVersoes" pv ON pv."Id"=po."VersaoAtualId"
          WHERE l."Codigo" NOT IN ('','0')
        ), pts AS (SELECT *,ST_LineInterpolatePoint(geom,CASE WHEN code='LAB46-TIE' THEN .95 ELSE .5 END) pt FROM samples),
        loc AS (SELECT *,ST_LineLocatePoint(geom,pt) f FROM pts)
        SELECT line,direction,pattern,version,old,code,ST_Y(pt),ST_X(pt),f,
          degrees(ST_Azimuth(ST_LineInterpolatePoint(geom,GREATEST(0,f-.025))::geography,ST_LineInterpolatePoint(geom,LEAST(1,f+.025))::geography))
        FROM loc ORDER BY code,pattern''')
        samples = [r for r in cur.fetchall() if r[-1] is not None]
    random.Random(46).shuffle(samples)
    if concentrated:
        samples = [next(r for r in samples if r[5] == "838")]
    result=[]
    for i in range(n):
        line,direction,pattern,version,old,code,lat,lon,f,bearing = samples[i % len(samples)]
        prev = i < 2400 if high else i < 40
        op = i < 500 if high else 40 <= i < 80
        # Low scenario rejects ~140 points spatially while preserving all three branches.
        if not high and i >= n-140:
            lat,lon = 0,0
        result.append(dict(input_id=f"synthetic-{i:05d}",codigo=code,lat=lat,lon=lon,bearing=bearing,dist_max=250.,
          usar_anterior=prev,padrao_versao_id=old if prev else str(uuid.UUID(int=0)),fracao_min=max(0,f-.12),fracao_max=min(1,f+.12),
          usar_operacional=op,padrao_operacional=old if op else str(uuid.UUID(int=0)),validar_identidade_operacional=op,
          padrao_operacional_esperado=pattern,sentido_operacional_esperado=direction,linha_operacional_esperada=line,
          posicao_operacional_anterior=max(0,f-.01),orcamento_operacional_metros=1500.))
    return result


def nodes(plan, path="root"):
    yield path,plan
    for i, child in enumerate(plan.get("Plans",[])):
        yield from nodes(child,f"{path}/{i}")


def summarize_plans(plans):
    """Node times are inclusive; CTE demand time can be charged to its first reader.

    Branches and CTEs are separate views of the same execution, never additive.
    PostgreSQL reports per-loop averages rounded at each node.
    """
    ctes = collections.defaultdict(lambda: collections.Counter())
    branches = collections.defaultdict(lambda: collections.Counter())
    scans = collections.defaultdict(lambda: collections.Counter())
    joins = collections.defaultdict(lambda: collections.Counter())
    kinds = collections.Counter()
    spatial = []
    buffers = collections.Counter()
    for chunk, plan in enumerate(plans):
        root=plan["Plan"]
        for key in ("Shared Hit Blocks","Shared Read Blocks","Temp Read Blocks","Temp Written Blocks"):
            buffers[key]+=root.get(key,0)
        for path,node in nodes(root):
            kind=node["Node Type"]; kinds[kind]+=1
            loops=node.get("Actual Loops",0)
            if "Subplan Name" in node:
                stats=ctes[node["Subplan Name"]]
                stats["inclusive_ms"]+=node.get("Actual Total Time",0)*loops
                stats["rows"]+=node.get("Actual Rows",0)*loops
                stats["loops"]+=loops
            if kind=="Append" and len(node.get("Plans",[]))==3:
                for name,child in zip(("GLOBAL","ANTERIOR","OPERACIONAL"),node["Plans"]):
                    stats=branches[name]
                    stats["inclusive_ms"]+=child.get("Actual Total Time",0)*child.get("Actual Loops",0)
                    stats["rows"]+=child.get("Actual Rows",0)*child.get("Actual Loops",0)
                    for _,inner in nodes(child):
                        if inner["Node Type"]=="Limit":
                            stats["next_stop_ms"]+=inner.get("Actual Total Time",0)*inner.get("Actual Loops",0)
                            stats["next_stop_loops"]+=inner.get("Actual Loops",0)
            if "Relation Name" in node:
                stats=scans[kind+":"+node["Relation Name"]]
                stats["loops"]+=loops
                stats["rows_approx"]+=node.get("Actual Rows",0)*loops
                stats["removed_approx"]+=node.get("Rows Removed by Filter",0)*loops
            if "Join" in kind or kind=="Nested Loop":
                stats=joins[kind]
                stats["loops"]+=loops
                stats["rows_approx"]+=node.get("Actual Rows",0)*loops
                stats["removed_approx"]+=node.get("Rows Removed by Join Filter",0)*loops
            if any("st_" in node.get(key,"").lower() for key in ("Filter","Join Filter","Index Cond")):
                spatial.append({"chunk":chunk,"path":path,"type":kind,"loops":loops,
                                "rows":node.get("Actual Rows",0),"removed":node.get("Rows Removed by Filter",0),
                                "removed_join":node.get("Rows Removed by Join Filter",0)})
    return {"planning_ms":sum(p["Planning Time"] for p in plans),
            "execution_ms":sum(p["Execution Time"] for p in plans),"buffers":dict(buffers),
            "ctes_inclusive":dict(ctes),"branches_inclusive":dict(branches),"scans":dict(scans),"joins":dict(joins),
            "node_counts":dict(kinds),"spatial_filter_nodes":spatial,
            "jit_chunks":sum("JIT" in p for p in plans)}


def execute(conn, sql, inputs, explain=False):
    started=time.perf_counter()
    with conn.cursor() as cur:
        cur.execute(("EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " if explain else "")+sql, {"inputs":json.dumps(inputs)})
        rows=cur.fetchall()
    return (time.perf_counter()-started)*1000, rows[0][0][0] if explain else rows


def profile(conn, output, source_path, scenario):
    sql=sql_from_source(pathlib.Path(source_path).read_text(encoding="utf-8-sig"))
    with conn.cursor() as cur:
        cur.execute(f'SET search_path={SCHEMA},public')
    inputs=load_inputs(conn,2800 if scenario=="low" else 3000,scenario!="low",scenario=="concentrated")
    branch_counts=collections.Counter()
    plans=[]
    wall=0
    chunks=[inputs[i:i+100] for i in range(0,len(inputs),100)]
    for i,chunk in enumerate(chunks):
        elapsed,rows=execute(conn,sql,chunk)
        wall+=elapsed
        branch_counts.update(r[1] for r in rows)
        elapsed,plan=execute(conn,sql,chunk,True)
        plans.append(plan)
    output.mkdir(parents=True,exist_ok=True)
    prefix=f"{scenario}-{pathlib.Path(source_path).stem}-{hashlib.sha256(sql.encode()).hexdigest()[:8]}"
    (output/(prefix+"-plans.json")).write_text(json.dumps(plans,indent=2))
    summary={"scenario":scenario,"inputs":len(inputs),"commands":len(chunks),"rows":dict(branch_counts),"wall_ms":wall,
      "planning_ms":sum(p["Planning Time"] for p in plans),"execution_ms":sum(p["Execution Time"] for p in plans),
      "aggregate":summarize_plans(plans),"nodes":[]}
    for path,p in nodes(plans[0]["Plan"]):
        summary["nodes"].append({k:p[k] for k in ("Node Type","Subplan Name","Relation Name","Actual Total Time","Actual Loops","Actual Rows","Rows Removed by Filter","Rows Removed by Join Filter","Shared Hit Blocks","Shared Read Blocks","Temp Read Blocks","Temp Written Blocks","Filter","Join Filter","Index Cond") if k in p}|{"path":path})
    (output/(prefix+"-summary.json")).write_text(json.dumps(summary,indent=2))
    # Isolated branch probes retain original CTEs/expressions. Dead CTEs may be
    # skipped; these plans attribute cost but must not be added to the full plan.
    final_start=sql.index("SELECT input_id,'GLOBAL' ramo")
    common=sql[:final_start]
    branches=sql[final_start:].rsplit("ORDER BY input_id DESC,ramo DESC",1)[0].split("UNION ALL")
    isolated={}
    for name,body in zip(("GLOBAL","ANTERIOR","OPERACIONAL"),branches):
        _,plan=execute(conn,common+body,chunks[0],True)
        isolated[name]=plan
    (output/(prefix+"-isolated-branches.json")).write_text(json.dumps(isolated,indent=2))
    print(json.dumps({k:v for k,v in summary.items() if k not in ("nodes","aggregate")}))


def percentile(values, p):
    # Nearest rank, documented; seven pairs are a small sample for p95.
    return sorted(values)[math.ceil(len(values)*p)-1]


def compare(conn, output, baseline_path, candidate_path, pairs, scenarios):
    if pairs < 7:
        raise ValueError("At least seven warmed alternating pairs required")
    variants={k:sql_from_source(pathlib.Path(v).read_text(encoding="utf-8-sig"))
              for k,v in (("baseline",baseline_path),("candidate",candidate_path))}
    if variants["baseline"] == variants["candidate"]:
        raise ValueError("Candidate must differ from baseline")
    output.mkdir(parents=True,exist_ok=True)
    results={"pairs":pairs,"chunk_size":100,"percentile":"nearest rank","scenarios":{}}
    for scenario in scenarios:
        inputs=load_inputs(conn,2800 if scenario=="low" else 3000,scenario!="low",scenario=="concentrated")
        chunks=[inputs[i:i+100] for i in range(0,len(inputs),100)]
        expected=[]
        rows_by_branch=collections.Counter()
        for chunk in chunks:
            _,rows=execute(conn,variants["baseline"],chunk)
            expected.append(rows)
            rows_by_branch.update(r[1] for r in rows)
        def cycle(variant):
            started=time.perf_counter()
            for i,chunk in enumerate(chunks):
                _,actual=execute(conn,variants[variant],chunk)
                # Exact field AND order equivalence, without numeric tolerance or sorting.
                if actual != expected[i]:
                    raise AssertionError(f"Full ordered SQL rows diverged: {scenario},chunk={i},variant={variant}")
            return (time.perf_counter()-started)*1000
        for variant in variants: cycle(variant)
        samples={k:[] for k in variants}
        runs=[]
        for pair in range(pairs):
            for variant in (["baseline","candidate"] if pair%2==0 else ["candidate","baseline"]):
                elapsed=cycle(variant)
                samples[variant].append(elapsed)
                runs.append({"pair":pair+1,"variant":variant,"wall_ms":elapsed})
                print(json.dumps({"scenario":scenario,**runs[-1]}),flush=True)
        results["scenarios"][scenario]={"inputs":len(inputs),"commands":len(chunks),"rows":dict(rows_by_branch),"runs":runs,
          "summary":{k:{"p50_ms":statistics.median(v),"p95_ms":percentile(v,.95)} for k,v in samples.items()},
          "input_sha256":hashlib.sha256(json.dumps(inputs,sort_keys=True).encode()).hexdigest(),"exact_ordered_equivalence":True}
        (output/"comparison.json").write_text(json.dumps(results,indent=2))
        print(json.dumps({"scenario":scenario,"summary":results["scenarios"][scenario]["summary"]}),flush=True)


def main():
    p=argparse.ArgumentParser()
    p.add_argument("--state",required=True)
    p.add_argument("--output",required=True,type=pathlib.Path)
    sub=p.add_subparsers(dest="mode",required=True)
    s=sub.add_parser("seed");s.add_argument("--gtfs",required=True)
    r=sub.add_parser("profile");r.add_argument("--source",default=str(SQL_PATH));r.add_argument("--scenario",choices=["low","high","concentrated"],required=True)
    r=sub.add_parser("compare");r.add_argument("--baseline",required=True);r.add_argument("--candidate",required=True)
    r.add_argument("--pairs",type=int,default=7);r.add_argument("--scenario",nargs="+",choices=["low","high","concentrated"],default=["low","high","concentrated"])
    a=p.parse_args()
    conn=guarded_connection(a.state)
    try:
        if a.mode=="seed": seed(conn,a.gtfs,a.output)
        elif a.mode=="compare": compare(conn,a.output,a.baseline,a.candidate,a.pairs,a.scenario)
        else: profile(conn,a.output,a.source,a.scenario)
    finally:
        conn.close()


if __name__=="__main__":
    main()
