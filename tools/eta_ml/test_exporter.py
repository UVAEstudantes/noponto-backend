"""Guards do executável compilado. Nunca abre conexão PostgreSQL."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from collection_profiles import PROFILES, HISTORICAL, CURRENT

TOOLS = Path(__file__).resolve().parents[1]
DLL = TOOLS / "EtaMl.Export/bin/Debug/net9.0/EtaMl.Export.dll"


class ExporterGuardsTests(unittest.TestCase):
    def run_guard(self, mutate, expected, profile=CURRENT):
        self.assertTrue(DLL.exists(), "Compilar tools/EtaMl.Export primeiro")
        audit = json.loads((TOOLS / "EtaMl.Export/audit.example.json").read_text())
        p = PROFILES[profile]
        audit.update(CollectionProfile=profile,CollectionStartedAtUtc=p["cutoff"],
                     BackendCommit=p["backend_commit"],Image=p["image"])
        audit.update(SnapshotReference="fixture-guard-no-database", Trips=[{
            "ViagemId": "00000000-0000-0000-0000-000000000001", "Inicio": "2026-10-08T03:00:00Z",
            "Fim": "2026-10-08T03:15:00Z", "CodigoLinha": "FIXTURE", "Qualidade": "AuditadaSemProtecao",
            "ReferenciaAuditoria": "fixture-guard-only"}])
        mutate(audit)
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "audit.json"
            path.write_text(json.dumps(audit))
            env = dict(os.environ, ETA_ML_LOCAL_CONNECTION="Host=example.invalid;Database=fixture")
            result = subprocess.run(["dotnet", str(DLL), str(path), str(TOOLS / "EtaMl.Export/options.example.json"),
                                     "unused.sql", str(Path(root) / "output")], env=env,
                                    capture_output=True, text=True, encoding="utf-8", timeout=30)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(expected, result.stderr)
            self.assertFalse((Path(root) / "output").exists())

    def test_remote_blocked_before_connection(self):
        self.run_guard(lambda a: None, "Somente snapshot PostgreSQL local")

    def test_empty_audit_rejected(self):
        self.run_guard(lambda a: a.update(Trips=[]), "auditoria inválido")

    def test_unaudited_rejected(self):
        self.run_guard(lambda a: a["Trips"][0].update(Qualidade="NaoVerificada"), "auditoria inválido")

    def test_cutoff_mismatch_rejected(self):
        self.run_guard(lambda a: a.update(CollectionStartedAtUtc="2026-10-07T14:53:59Z"), "auditoria inválido")

    def test_duplicate_trip_rejected(self):
        self.run_guard(lambda a: a["Trips"].append(a["Trips"][0].copy()), "auditoria inválido")

    def test_collection_metadata_mismatch_rejected(self):
        self.run_guard(lambda a: a.update(BackendCommit="other-release"), "Manifesto diverge da coleta oficial")

    def test_invalid_data_kind_rejected(self):
        self.run_guard(lambda a: a.update(DataKind="unknown"), "auditoria inválido")

    def test_legacy_audit_data_kind_defaults_real(self):
        self.run_guard(lambda a: a.pop("DataKind"), "Somente snapshot PostgreSQL local")

    def test_historical_profile_accepted_before_remote_guard(self):
        self.run_guard(lambda a: a.pop("CollectionProfile"), "Somente snapshot PostgreSQL local", HISTORICAL)

    def test_historical_synthetic_fixture_still_accepted(self):
        self.run_guard(lambda a: a.update(DataKind="synthetic"), "Somente snapshot PostgreSQL local", HISTORICAL)

    def test_same_declarative_catalog_is_embedded(self):
        csproj = (TOOLS / "EtaMl.Export/EtaMl.Export.csproj").read_text()
        self.assertIn('Include="../collection_profiles.json" LogicalName="EtaMl.CollectionProfiles.json"', csproj)
        self.assertEqual(json.loads((TOOLS / "collection_profiles.json").read_text()), PROFILES)

    def test_postgis_fixture_metadata_without_database(self):
        from integration_postgis import seed
        from collection_profiles import from_audit, match
        class FakeDb:
            def sql(self, text):
                self.text = text
        with tempfile.TemporaryDirectory() as root:
            audit, options, config = seed(FakeDb(), Path(root))
            self.assertEqual(match(from_audit(audit)), HISTORICAL)
            self.assertEqual(audit["DataKind"], "synthetic")
            self.assertEqual(options["Inicio"], PROFILES[HISTORICAL]["cutoff"])
            self.assertEqual(config["inicio"], options["Inicio"])

    def test_mixed_cutoff_commit_image_and_sampling(self):
        old = PROFILES[HISTORICAL]
        for mutate in (lambda a: a.update(BackendCommit=old["backend_commit"]),
                       lambda a: a.update(Image=old["image"]),
                       lambda a: a.update(CollectionStartedAtUtc=old["cutoff"]),
                       lambda a: a.update(CollectionProfile=HISTORICAL),
                       lambda a: a["Sampling"].update(line_percentage=20),
                       lambda a: a["Sampling"].update(line_percentage=10.0),
                       lambda a: a["Sampling"].update(enabled=False),
                       lambda a: a["Sampling"].update(unapproved=True)):
            self.run_guard(mutate, "Manifesto diverge da coleta oficial")

    def test_contract_and_protected_trips_rejected(self):
        self.run_guard(lambda a: a.update(DatasetVersion="other"), "auditoria inválido")
        self.run_guard(lambda a: a["Trips"][0].update(Qualidade="ProtegidaOuAmbigua"), "auditoria inválido")

    def test_local_read_only_repeatable_read_source_guard(self):
        text = (TOOLS / "EtaMl.Export/Program.cs").read_text(encoding="utf-8-sig")
        self.assertIn('builder.Host is not ("localhost" or "127.0.0.1" or "::1")', text)
        self.assertIn("IsolationLevel.RepeatableRead", text)
        self.assertIn("SET TRANSACTION READ ONLY", text)
        self.assertLess(text.index("builder.Host is not"), text.index("connection.OpenAsync()"))


if __name__ == "__main__":
    unittest.main()
