"""Guards do executável compilado. Nunca abre conexão PostgreSQL."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
DLL = TOOLS / "EtaMl.Export/bin/Debug/net9.0/EtaMl.Export.dll"


class ExporterGuardsTests(unittest.TestCase):
    def run_guard(self, mutate, expected):
        self.assertTrue(DLL.exists(), "Compilar tools/EtaMl.Export primeiro")
        audit = json.loads((TOOLS / "EtaMl.Export/audit.example.json").read_text())
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


if __name__ == "__main__":
    unittest.main()
