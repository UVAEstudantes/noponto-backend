"""Snapshot guards: fixtures/files only, no subprocess/network/database execution."""
import copy
import csv
import json
import re
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools.eta_ml import snapshot as s

TRIP = "00000000-0000-0000-0000-000000000001"


def request():
    return {"profile": copy.deepcopy(s.PROFILE), "snapshot_end": "2026-10-08T00:00:00Z",
            "trips": [{"viagem_id": TRIP, "inicio": "2026-10-07T21:10:00Z",
                       "fim": "2026-10-07T21:30:00Z", "codigo_linha": "FIXTURE",
                       "referencia_auditoria": "synthetic-guard-only"}]}


def csv_file(path, records):
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, lineterminator="\n")
        writer.writerow(["row"])
        writer.writerows([s.canonical(r).strip()] for r in records)


def fixture(directory):
    req = s.prepare(request(), directory)
    catalog = []
    for table in s.TABLES:
        records = []
        if table == "EventosViagem":
            for kind, prefix, ts in (("ViagemIniciada", "inicio", req["trips"][0]["inicio"]),
                                     ("ViagemFinalizada", "fim", req["trips"][0]["fim"])):
                event_id = prefix + ":" + TRIP
                records.append({"EventId": event_id, "Tipo": kind, "TimestampEvento": ts,
                                "Payload": {"viagem_id": TRIP, "event_id": event_id, "tipo": kind,
                                            "schema_version": 2, "codigo_linha": "FIXTURE",
                                            "timestamp_evento": ts}})
            records.sort(key=lambda r: r["EventId"])
        elif table in s.HEAVY:
            records = [{"Id": TRIP, "ViagemId": TRIP}]
        cols = records[0].keys() if records else ["Id"]
        catalog.append({"table": table, "columns": [{"name": c, "type": "text"} for c in cols],
                        "foreign_keys": []})
        csv_file(directory / (table + ".csv"), records)
    csv_file(directory / "catalog.csv", sorted(catalog, key=lambda r: r["table"]))
    (directory / "schema.dump").write_bytes(b"PGDMP-synthetic-placeholder-not-restorable")
    csv_file(directory / "source_context.csv", [{"snapshot_at": "2026-10-08T00:01:00Z",
                                              "mvcc_snapshot": "fixture", "postgresql": "16",
                                              "postgis": "3.4"}])
    csv_file(directory / "completed.csv", ["completed-read-only-export"])
    functions = []
    for name, reviewed in s.snapshot_schema.REVIEWED.items():
        body = "\n" + reviewed + "\n"
        functions.append(dict(identity=s.snapshot_schema.identity(name), schema="public", name=name,
                              arguments="", result="trigger", language="plpgsql", security_definer=False,
                              kind="f", volatility="v", strict=False, leakproof=False, parallel="u",
                              cost=100, rows=0, config=None, body=body,
                              definition=f'CREATE OR REPLACE FUNCTION public."{name}"()\n RETURNS trigger\n LANGUAGE plpgsql\nAS $function${body}$function$\n'))
    triggers = []
    for name, (table, function) in s.snapshot_schema.TRIGGERS.items():
        triggers.append(dict(table=table, name=name, enabled="O", function=s.snapshot_schema.identity(function),
                             definition=f'CREATE TRIGGER "{name}" BEFORE DELETE OR UPDATE ON public."{table}" FOR EACH ROW EXECUTE FUNCTION {s.snapshot_schema.identity(function)}'))
    csv_file(directory / "functions.csv", functions)
    csv_file(directory / "triggers.csv", sorted(triggers, key=lambda t: (t["table"], t["name"])))
    csv_file(directory / "dependencies.csv", [])
    return req


def responder(path):
    def respond(command, **kwargs):
        stdout = ""
        if kwargs.get("capture_output"):
            statement = command[-1]
            name = "catalog"
            for key, query in s.snapshot_schema.queries(s.TABLES).items():
                if query in statement:
                    name = key
            stdout = "".join(s.canonical(r) for r in s.rows(path / (name + ".csv")))
        return subprocess.CompletedProcess(command, 0, stdout=stdout)
    return respond


class SnapshotTests(unittest.TestCase):
    def test_official_profile_and_invalid_selection(self):
        template = json.loads(Path(s.__file__).with_name("snapshot.request.example.json").read_text())
        self.assertEqual(template["profile"], s.PROFILE)
        mutations = [lambda r: r["profile"].update(cutoff="2026-10-07T14:53:59Z"),
                     lambda r: r["profile"].update(backend_commit="wrong"),
                     lambda r: r["profile"].update(dataset_contract="other"),
                     lambda r: r.update(trips=[]),
                     lambda r: r["trips"].append(r["trips"][0].copy()),
                     lambda r: r["trips"][0].update(inicio="2026-10-07T20:00:00Z"),
                     lambda r: r["trips"][0].update(referencia_auditoria=""),
                     lambda r: r["trips"][0].update(fim=r["snapshot_end"])]
        for mutate in mutations:
            req = request()
            mutate(req)
            with self.assertRaises(ValueError):
                s.validate(req)

    def test_heavy_queries_require_explicit_ids(self):
        req = s.validate(request())
        for table in s.HEAVY:
            sql = s.query(table, req)
            self.assertIn(" WHERE ", sql)
            self.assertIn(TRIP, sql)
            self.assertIn('"ViagemId" IN' if table != "EventosViagem"
                          else "->>'viagem_id' IN", sql)
            self.assertIn("to_jsonb(t)", sql)
        with self.assertRaises(ValueError):
            s.prepare({**request(), "trips": []}, "unused")
        with self.assertRaises(ValueError):
            s.query("ViagensOperacionais", req)
        for table in s.HEAVY:
            with self.assertRaises(ValueError):
                s.query(table, {**req, "trips": []})

    def test_spatial_rows_and_timestamp_precision(self):
        for table in ("Paradas", "PadroesVersoes"):
            self.assertIn("ST_AsEWKB", s.query(table, request()))
            self.assertIn("'hex'", s.query(table, request()))
        self.assertTrue(s.same_pg_time("2026-10-07T21:10:00.123457+00:00",
                                      "2026-10-07T21:10:00.1234567Z"))
        self.assertFalse(s.same_pg_time("2026-10-07T21:10:00.123459Z",
                                       "2026-10-07T21:10:00.1234567Z"))

    def test_source_read_only_and_schema_only(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            s.prepare(request(), path)
            sql = (path / "export.sql").read_text()
            self.assertIn("BEGIN READ ONLY", sql)
            self.assertIn("REPEATABLE READ", sql)
            self.assertIn("AS fk_guard", sql)
            self.assertIn(s.PROFILE["migration"], sql)
            self.assertNotRegex(sql, r"(?i)\b(INSERT|UPDATE|DELETE|ALTER|DROP|CREATE|TRUNCATE|ANALYZE)\b")
            shell = (path / "export.sh").read_text()
            self.assertIn("--schema-only", shell)
            self.assertIn("default_transaction_read_only=on", shell)
            self.assertNotIn("--host", shell)
            self.assertNotIn("PASSWORD", shell)
            plans = (path / "plans.sql").read_text()
            self.assertIn("EXPLAIN (COSTS ON)", plans)
            self.assertNotIn("ANALYZE", plans)

    def test_deterministic_bundle_and_tamper_detection(self):
        with tempfile.TemporaryDirectory() as root:
            a, b = Path(root) / "a", Path(root) / "b"
            fixture(a)
            fixture(b)
            ma, mb = s.seal(a), s.seal(b)
            self.assertEqual(ma, mb)
            self.assertEqual((a / "snapshot.manifest.json").read_bytes(),
                             (b / "snapshot.manifest.json").read_bytes())
            self.assertEqual(s.verify(a), ma)
            (a / "schema.dump").write_bytes(b"changed")
            with self.assertRaises(ValueError):
                s.verify(a)

    def test_unselected_rows_or_missing_boundaries_rejected(self):
        for table in s.HEAVY:
            with tempfile.TemporaryDirectory() as root:
                path = Path(root) / "bundle"
                fixture(path)
                records = list(s.rows(path / (table + ".csv")))
                if table == "EventosViagem":
                    records[0]["Payload"]["viagem_id"] = "other"
                else:
                    records[0]["ViagemId"] = "other"
                csv_file(path / (table + ".csv"), records)
                with self.assertRaisesRegex(ValueError, "unselected trip"):
                    s.seal(path)
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            csv_file(path / "EventosViagem.csv", [])
            with self.assertRaises(ValueError):
                s.seal(path)

    def test_fk_dependency_outside_allowlist_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            catalog = list(s.rows(path / "catalog.csv"))
            catalog[0]["foreign_keys"] = [{"parent": "Unknown", "parent_schema": "public"}]
            csv_file(path / "catalog.csv", catalog)
            with self.assertRaises(ValueError):
                s.seal(path)

    def test_postfix_precision_and_completed_export_required(self):
        req = request()
        req["profile"]["sampling"]["enabled"] = 1
        with self.assertRaises(ValueError):
            s.validate(req)
        for broken in ("completed.csv", "source_context.csv"):
            with tempfile.TemporaryDirectory() as root:
                path = Path(root) / "bundle"
                fixture(path)
                csv_file(path / broken, [])
                with self.assertRaises(ValueError):
                    s.seal(path)

    def test_restore_local_phases_constraints_and_counts(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            manifest = s.seal(path)
            sql = s.restore_sql(path, manifest)
            self.assertEqual(sql.count("AS count_guard"), len(s.TABLES))
            self.assertNotRegex(sql, r"(?i)DISABLE|session_replication_role|DROP.*CONSTRAINT")
            with patch.object(s.subprocess, "run") as run:
                run.side_effect = responder(path)
                s.restore(path, "eta_snapshot_fixture", 55439)
                for call in run.call_args_list:
                    self.assertIn("--host=127.0.0.1", call.args[0])
                commands = [call.args[0] for call in run.call_args_list]
                self.assertTrue(any("--section=pre-data" in c for c in commands))
                self.assertTrue(any("--section=post-data" in c for c in commands))
                phases = [next(i for i, c in enumerate(commands) if marker in c)
                          for marker in ("--section=pre-data", "functions.sql", "--section=post-data")]
                self.assertEqual(phases, sorted(phases))
            with patch.object(s.subprocess, "run") as run:
                for database in ("production", "host=remote", "eta_snapshot_x;DROP", "postgres"):
                    with self.assertRaises(ValueError):
                        s.restore(path, database, 55439)
                run.assert_not_called()

    def test_reviewed_bodies_match_migration(self):
        migration = Path("NoPonto/Migrations/20260925175931_EstruturaFinalEtapas1e2.cs").read_text(encoding="utf-8-sig")
        bodies = {n: s.snapshot_schema.normalize(b) for n, b in re.findall(
            r'CREATE OR REPLACE FUNCTION "([^"]+)"\(\) RETURNS trigger AS \$\$(.*?)\$\$ LANGUAGE plpgsql;', migration, re.S)}
        self.assertEqual(s.snapshot_schema.REVIEWED, bodies)
        self.assertEqual(len(bodies), 2)

    def test_missing_divergent_or_external_functions_rejected(self):
        mutations = [lambda f: f.clear(), lambda f: f[0].update(security_definer=True),
                     lambda f: f[0].update(body="BEGIN RETURN NEW; END;"),
                     lambda f: f[0].update(identity='other."External"()'),
                     lambda f: f[0].update(config=["search_path=other"]),
                     lambda f: f[0].update(definition=f[0]["definition"] + "DROP TABLE x;")]
        for mutate in mutations:
            with self.subTest(mutate=mutate), tempfile.TemporaryDirectory() as root:
                path = Path(root) / "bundle"
                fixture(path)
                records = list(s.rows(path / "functions.csv"))
                mutate(records)
                csv_file(path / "functions.csv", records)
                with self.assertRaises(ValueError):
                    s.seal(path)

    def test_unsupported_dependencies_and_changed_triggers_rejected(self):
        for filename, mutate in (
            ("dependencies", lambda r: r.append(dict(type="function", schema="other", identity="other.f()", extension=None))),
            ("dependencies", lambda r: r.append(dict(type="type", schema="public", identity="public.custom_enum", extension=None))),
            ("triggers", lambda r: r[0].update(function="other.f()")),
            ("triggers", lambda r: r[0].update(enabled="D")),
            ("triggers", lambda r: r[0].update(definition=r[0]["definition"].replace("DELETE OR UPDATE", "UPDATE"))),
            ("triggers", lambda r: r.append({**r[0], "name": "external_trigger"})),
        ):
            with self.subTest(filename=filename), tempfile.TemporaryDirectory() as root:
                path = Path(root) / "bundle"
                fixture(path)
                records = list(s.rows(path / (filename + ".csv")))
                mutate(records)
                csv_file(path / (filename + ".csv"), records)
                with self.assertRaises(ValueError):
                    s.seal(path)

    def test_all_new_evidence_is_hashed_and_sealed_bundle_is_immutable(self):
        for filename in ("functions.csv", "triggers.csv", "dependencies.csv", "functions.sql", "bundle.version.json"):
            with self.subTest(filename=filename), tempfile.TemporaryDirectory() as root:
                path = Path(root) / "bundle"
                fixture(path)
                manifest = s.seal(path)
                self.assertIn(filename, manifest["files_sha256"])
                before = {p.name: p.read_bytes() for p in path.iterdir()}
                with self.assertRaises(ValueError):
                    s.seal(path)
                self.assertEqual(before, {p.name: p.read_bytes() for p in path.iterdir()})
                with (path / filename).open("a", encoding="utf-8") as stream:
                    stream.write("corruption")
                with self.assertRaises((ValueError, csv.Error)):
                    s.verify(path)

    def test_legacy_verify_without_silent_upgrade_and_no_restore(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            req = fixture(path)
            manifest = s.manifest_for(path, req, s.LEGACY_VERSION)
            for filename in ("bundle.version.json", "functions.csv", "triggers.csv", "dependencies.csv"):
                (path / filename).unlink()
            s.write(path / "snapshot.manifest.json", s.canonical(manifest))
            before = {p.name: p.read_bytes() for p in path.iterdir()}
            self.assertEqual(s.verify(path), manifest)
            with patch.object(s.subprocess, "run") as run:
                with self.assertRaisesRegex(ValueError, "Legacy v1"):
                    s.restore(path, "eta_snapshot_fixture", 55439)
                run.assert_not_called()
            self.assertEqual(before, {p.name: p.read_bytes() for p in path.iterdir()})

    def test_restored_function_and_trigger_divergence_rejected(self):
        for target in ("functions", "triggers", "dependencies"):
            with self.subTest(target=target), tempfile.TemporaryDirectory() as root:
                path = Path(root) / "bundle"
                fixture(path)
                s.seal(path)
                normal = responder(path)
                def respond(command, **kwargs):
                    result = normal(command, **kwargs)
                    if kwargs.get("capture_output") and s.snapshot_schema.queries(s.TABLES)[target] in command[-1]:
                        result.stdout = "{}\n"
                    return result
                with patch.object(s.subprocess, "run", side_effect=respond):
                    with self.assertRaisesRegex(ValueError, "Restored " + target):
                        s.restore(path, "eta_snapshot_fixture", 55439)

    def test_failure_stops_each_restore_phase(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            s.seal(path)
            for fail_at in range(1, 11):
                calls = []
                normal = responder(path)
                def respond(command, **kwargs):
                    calls.append(command)
                    if len(calls) == fail_at:
                        raise subprocess.CalledProcessError(1, command)
                    return normal(command, **kwargs)
                with patch.object(s.subprocess, "run", side_effect=respond):
                    with self.assertRaises(subprocess.CalledProcessError):
                        s.restore(path, "eta_snapshot_fixture", 55439)
                self.assertEqual(len(calls), fail_at)

    def test_missing_evidence_and_timeout_stop_before_post_data(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            (path / "functions.csv").unlink()
            with self.assertRaises(FileNotFoundError):
                s.seal(path)
            fixture(Path(root) / "valid")
            path = Path(root) / "valid"
            s.seal(path)
            normal = responder(path)
            def respond(command, **kwargs):
                if "functions.sql" in command:
                    raise subprocess.TimeoutExpired(command, 300)
                return normal(command, **kwargs)
            with patch.object(s.subprocess, "run", side_effect=respond) as run:
                with self.assertRaises(subprocess.TimeoutExpired):
                    s.restore(path, "eta_snapshot_fixture", 55439)
                self.assertFalse(any("--section=post-data" in c.args[0] for c in run.call_args_list))

    def test_builtin_dependencies_and_catalog_discovery(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            dependencies = [dict(type="schema", schema=None, identity="public", extension=None),
                            dict(type="language", schema=None, identity="plpgsql", extension="plpgsql"),
                            dict(type="type", schema="public", identity="public.geometry", extension="postgis"),
                            dict(type="operator", schema="pg_catalog", identity="pg_catalog.=(uuid,uuid)", extension=None)]
            dependencies.sort(key=lambda d: s.canonical(d))
            csv_file(path / "dependencies.csv", dependencies)
            self.assertEqual(s.seal(path)["schema_dependencies"]["dependencies"], dependencies)
        queries = s.snapshot_schema.queries(s.TABLES)
        self.assertIn("pg_get_functiondef", queries["functions"])
        self.assertIn("NOT tgisinternal", queries["functions"])
        self.assertIn("WITH RECURSIVE", queries["dependencies"])
        self.assertIn("pg_identify_object", queries["dependencies"])
        for query in queries.values():
            for table in s.TABLES:
                self.assertIn("'" + table + "'", query)
            self.assertNotIn("BloquearMutacao", query)

    def test_metadata_context_overrides_inherited_path_without_changing_bundle(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            s.seal(path)
            before = {p.name: p.read_bytes() for p in path.iterdir()}
            normal = responder(path)
            def respond(command, **kwargs):
                result = normal(command, **kwargs)
                sql = command[-1]
                # Simulate inherited public visibility dequalifying the function.
                if s.snapshot_schema.queries(s.TABLES)["triggers"] in sql:
                    if "SET LOCAL search_path=pg_catalog;" not in sql:
                        result.stdout = result.stdout.replace('EXECUTE FUNCTION public.', 'EXECUTE FUNCTION ')
                elif s.catalog_query() in sql:
                    self.assertIn("SET LOCAL search_path=public,pg_catalog;", sql)
                return result
            for inherited in ("public", '"$user",public', "pg_catalog", "other,public"):
                with patch.dict("os.environ", {"PGOPTIONS": "-c search_path=" + inherited}), patch.object(
                        s.subprocess, "run", side_effect=respond) as run:
                    s.check_restored(path, "eta_snapshot_fixture", 55439)
                    self.assertEqual(run.call_count, 4)
                    for call in run.call_args_list:
                        command = call.args[0]
                        self.assertEqual(command[0], "psql")
                        self.assertIn("--host=127.0.0.1", command)
                        self.assertIn("BEGIN READ ONLY;", command[-1])
                        self.assertTrue(command[-1].endswith("; COMMIT;"))
                        self.assertNotRegex(command[-1], r"(?i)\b(INSERT|UPDATE|DELETE|ALTER|DROP|CREATE|TRUNCATE)\b")
            self.assertEqual(before, {p.name: p.read_bytes() for p in path.iterdir()})

    def test_trigger_functional_changes_still_fail_local_check(self):
        mutations = [lambda t: t.update(function='other."BloquearMutacaoVersaoPublicada"()'),
                     lambda t: t.update(table="OtherTable"), lambda t: t.update(enabled="D"),
                     lambda t: t.update(name="OtherTrigger"),
                     lambda t: t.update(definition=t["definition"].replace("DELETE OR UPDATE", "UPDATE")),
                     lambda t: t.update(definition=t["definition"].replace("BEFORE", "AFTER")),
                     lambda t: t.update(definition=t["definition"].replace("FOR EACH ROW", "FOR EACH STATEMENT")),
                     lambda t: t.update(definition=t["definition"].replace('EXECUTE FUNCTION public.', 'EXECUTE FUNCTION '))]
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            s.seal(path)
            normal = responder(path)
            for mutate in mutations:
                def respond(command, **kwargs):
                    result = normal(command, **kwargs)
                    if s.snapshot_schema.queries(s.TABLES)["triggers"] in command[-1]:
                        records = list(s.rows(path / "triggers.csv"))
                        mutate(records[0])
                        result.stdout = "".join(s.canonical(r) for r in records)
                    return result
                with patch.object(s.subprocess, "run", side_effect=respond):
                    with self.assertRaisesRegex(ValueError, "Restored triggers"):
                        s.check_restored(path, "eta_snapshot_fixture", 55439)

    def test_check_restored_guards_before_connection(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "bundle"
            fixture(path)
            s.seal(path)
            with patch.object(s.subprocess, "run") as run:
                with self.assertRaises(ValueError):
                    s.check_restored(path, "host=remote", 55439)
                (path / "schema.dump").write_bytes(b"corrupt")
                with self.assertRaises(ValueError):
                    s.check_restored(path, "eta_snapshot_fixture", 55439)
                run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
