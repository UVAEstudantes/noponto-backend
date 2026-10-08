"""Conservative schema dependency policy for snapshot v2 (no database access)."""
import json
import re
from pathlib import Path

REVIEWED = json.loads(Path(__file__).with_name("snapshot_functions.reviewed.json").read_text(encoding="utf-8"))
TRIGGERS = {
    "TR_OcorrenciasPadroes_Imutavel": ("OcorrenciasParadasPadroes", "BloquearMutacaoOcorrenciaPublicada"),
    "TR_PadroesVersoes_Imutavel": ("PadroesVersoes", "BloquearMutacaoVersaoPublicada"),
}


def identity(name):
    return 'public."' + name + '"()'


def queries(tables):
    names = ",".join("'" + t + "'" for t in tables)
    selected = f"SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relname IN ({names})"
    triggers = f"""SELECT jsonb_build_object('table',c.relname,'name',t.tgname,
    'enabled',t.tgenabled,'definition',pg_get_triggerdef(t.oid,false),
    'function',format('%I.%I(%s)',n.nspname,p.proname,pg_get_function_identity_arguments(p.oid))) AS row FROM pg_trigger t
    JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_proc p ON p.oid=t.tgfoid
    JOIN pg_namespace n ON n.oid=p.pronamespace WHERE NOT t.tgisinternal AND c.oid IN ({selected})
    ORDER BY c.relname,t.tgname"""
    functions = f"""SELECT jsonb_build_object('identity',format('%I.%I(%s)',n.nspname,p.proname,pg_get_function_identity_arguments(p.oid)),
    'schema',n.nspname,'name',p.proname,'arguments',pg_get_function_identity_arguments(p.oid),
    'result',pg_get_function_result(p.oid),'language',l.lanname,'security_definer',p.prosecdef,
    'kind',p.prokind,'volatility',p.provolatile,'strict',p.proisstrict,'leakproof',p.proleakproof,
    'parallel',p.proparallel,'cost',p.procost,'rows',p.prorows,'config',p.proconfig,
    'body',p.prosrc,'definition',pg_get_functiondef(p.oid)) AS row FROM pg_proc p
    JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_language l ON l.oid=p.prolang
    WHERE p.oid IN (SELECT tgfoid FROM pg_trigger WHERE NOT tgisinternal AND tgrelid IN ({selected}))
    ORDER BY n.nspname,p.proname,pg_get_function_identity_arguments(p.oid)"""
    # Walk table-owned constraints, indexes, defaults, row types, triggers and
    # sequences. Inspect all normal external dependencies, not only two names.
    dependencies = f"""WITH RECURSIVE owned(classid,objid) AS (
    SELECT 'pg_class'::regclass::oid,oid FROM pg_class WHERE oid IN ({selected})
    UNION SELECT 'pg_proc'::regclass::oid,tgfoid FROM pg_trigger WHERE NOT tgisinternal AND tgrelid IN ({selected})
    UNION SELECT d.classid,d.objid FROM pg_depend d JOIN owned o
    ON d.refclassid=o.classid AND d.refobjid=o.objid WHERE d.deptype IN ('a','i'))
    SELECT row FROM (SELECT DISTINCT jsonb_build_object('type',i.type,'schema',i.schema,'identity',i.identity,
    'extension',(SELECT e.extname FROM pg_depend x JOIN pg_extension e ON e.oid=x.refobjid
    WHERE x.classid=d.refclassid AND x.objid=d.refobjid AND x.refclassid='pg_extension'::regclass AND x.deptype='e')) AS row
    FROM owned o JOIN pg_depend d ON d.classid=o.classid AND d.objid=o.objid
    CROSS JOIN LATERAL pg_identify_object(d.refclassid,d.refobjid,d.refobjsubid) i
    WHERE NOT EXISTS (SELECT 1 FROM owned r WHERE r.classid=d.refclassid AND r.objid=d.refobjid)
    ) evidence ORDER BY row::text"""
    return {k: " ".join(v.splitlines()) for k, v in
            (("triggers", triggers), ("functions", functions), ("dependencies", dependencies))}


def normalize(body):
    return "\n".join(line.strip() for line in body.strip().splitlines())


def validate(functions, triggers, dependencies):
    expected = {identity(n) for n in REVIEWED}
    if len(functions) != len(expected) or {f["identity"] for f in functions} != expected:
        raise ValueError("Missing/unreviewed trigger functions")
    for f in functions:
        properties = dict(schema="public", arguments="", result="trigger", language="plpgsql",
                          security_definer=False, kind="f", volatility="v", strict=False,
                          leakproof=False, parallel="u", cost=100, rows=0, config=None)
        if (f["name"] not in REVIEWED or f["identity"] != identity(f["name"])
                or any(f.get(k) != v for k, v in properties.items())
                or normalize(f["body"]) != REVIEWED[f["name"]]):
            raise ValueError("Divergent/unreviewed function properties or body")
        pattern = (r'\s*CREATE OR REPLACE FUNCTION public\."' + re.escape(f["name"])
                   + r'"\(\)\s+RETURNS trigger\s+LANGUAGE plpgsql\s+'
                   + r'(?:SECURITY INVOKER\s+)?AS (\$[A-Za-z_0-9]*\$)(.*?)\1\s*;?\s*')
        match = re.fullmatch(pattern, f["definition"], re.S)
        if not match or match[2] != f["body"]:
            raise ValueError("Function definition/identity differs from reviewed metadata")
    if len(triggers) != len(TRIGGERS) or {t["name"] for t in triggers} != set(TRIGGERS):
        raise ValueError("Missing/unreviewed triggers")
    for t in triggers:
        table, name = TRIGGERS[t["name"]]
        definition = (f'CREATE TRIGGER "{t["name"]}" BEFORE DELETE OR UPDATE ON public."{table}" '
                      f'FOR EACH ROW EXECUTE FUNCTION {identity(name)}')
        if (t["table"] != table or t["function"] != identity(name)
                or t["enabled"] != "O" or t["definition"] != definition):
            raise ValueError("Divergent trigger/function/immutability rule")
    for d in dependencies:
        if (d["schema"] == "pg_catalog" or d["extension"] == "postgis"
                or (d["type"] == "schema" and d["identity"] == "public")
                or (d["type"] == "language" and d["identity"] == "plpgsql")):
            continue
        raise ValueError("Unsupported external schema dependency: " + str(d))


def sql(functions):
    return ("\\set ON_ERROR_STOP on\n\\encoding UTF8\nBEGIN;\nSET LOCAL search_path=public,pg_catalog;\n"
            + "\n".join(f["definition"].rstrip().rstrip(";") + ";" for f in functions)
            + "\nCOMMIT;\n")
