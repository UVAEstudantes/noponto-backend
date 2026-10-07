"""Guardas estaticas 3B.2A: nenhuma conexao ou execucao SQL.

Nao substituem parsing/execucao PostgreSQL nem aferem custo em producao.
"""
import hashlib
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SQL = ROOT / "NoPonto/ETA_ML_AUDITORIA_REAL_3B_2.sql"


def statements():
    text = SQL.read_text(encoding="utf-8")
    text = re.sub(r"--[^\n]*", "", text)
    text = re.sub(r"(?m)^\\.*$", "", text)
    text = re.sub(r"'(?:''|[^'])*'", lambda m: m[0].replace(";", "_"), text)
    return [s.strip() for s in text.split(";") if s.strip()]


class AuditSqlTests(unittest.TestCase):
    def test_read_only_envelope_and_statement_allowlist(self):
        stmts = statements()
        self.assertEqual(stmts[0], "BEGIN READ ONLY")
        self.assertEqual(stmts[-1], "COMMIT")
        self.assertEqual(stmts[1], "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ")
        for stmt in stmts[2:-1]:
            self.assertRegex(stmt, r"^(SELECT|WITH)\b")
            code = re.sub(r"'(?:''|[^'])*'", "''", stmt)
            self.assertNotRegex(code, r"(?i)\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|ANALYZE|EXPLAIN|COPY|CALL|DO|LOCK|INTO)\b")
            self.assertNotRegex(code, r"(?i)\b(set_config|pg_sleep|nextval|setval|clock_timestamp|dblink|pg_advisory\w*|ST_LineSubstring)\s*\(")

    def test_heavy_sources_only_inside_optional_block(self):
        text = SQL.read_text(encoding="utf-8")
        before, detailed = text.split("\\if :detalhado", 1)
        self.assertIn("\\set detalhado false", before)
        for table in ("TelemetriasVeiculoMl", "HistoricoPassagens", "EventosViagem", "ViagensOperacionais"):
            self.assertNotIn(f'FROM "{table}"', before)
            self.assertEqual(detailed.count(f'FROM "{table}"'), 1)
        self.assertIn("\\endif", detailed)

    def test_schema_columns_and_json_keys_from_repository(self):
        text = SQL.read_text(encoding="utf-8")
        snapshot = (ROOT / "NoPonto/Migrations/TransporteDbContextModelSnapshot.cs").read_text(encoding="utf-8-sig")
        entities = {}
        for block in re.split(r"modelBuilder.Entity\(", snapshot)[1:]:
            table = re.search(r'b.ToTable\("([^"]+)"', block)
            if table:
                entities.setdefault(table[1], set()).update(re.findall(r'b.Property<[^\n]+>\("([^"]+)"', block))
        aliases = {"t":"TelemetriasVeiculoMl", "h":"HistoricoPassagens", "e":"EventosViagem",
                   "o":"OcorrenciasParadasPadroes", "obs":"OcorrenciasParadasPadroes", "v":"PadroesVersoes",
                   "p":"PadroesOperacionais", "s":"Sentidos", "l":"Linhas", "d":"Paradas"}
        # v is also the viagens CTE, but only its unquoted derived fields are used.
        for alias, table in aliases.items():
            for column in re.findall(rf'\b{alias}\."([^\"]+)"', text):
                self.assertIn(column, entities[table], (table, column))
        event_code = (ROOT / "NoPonto/2-Application/Services/GPS/ViagemOperacional.cs").read_text(encoding="utf-8-sig")
        for key in re.findall(r'"Payload"->>\x27([^\x27]+)\x27', text):
            self.assertIn(f'JsonPropertyName("{key}")', event_code)

    def test_cutoff_and_original_adapter_preserved(self):
        text = SQL.read_text(encoding="utf-8")
        self.assertEqual(set(re.findall(r"2026-\d\d-\d\dT[^']+Z", text)), {"2026-10-07T14:53:59.225082Z"})
        self.assertIn("WHERE f.inicio>=TIMESTAMPTZ", text)
        self.assertIn("PerdaContinuidadeCircular", text)
        self.assertIn("candidate_inventory != AuditadaSemProtecao", text)
        original = ROOT / "NoPonto/ETA_ML_CANDIDATOS_3A.sql"
        self.assertEqual(hashlib.sha256(original.read_bytes()).hexdigest(),
                         "b2c5f0909d817fc57ba3942c9b2985e77dcfbf9bbaf9a1644dd3228aa77898be")

    def test_observational_occurrence_is_optional_and_version_compatible(self):
        text = SQL.read_text(encoding="utf-8")
        gps = text.split("gps AS MATERIALIZED (", 1)[1].split("journal AS MATERIALIZED", 1)[0]
        compact = re.sub(r"\s+", "", re.sub(r"--[^\n]*", "", gps))
        self.assertIn('obsONobs."Id"=t."OcorrenciaParadaPadraoId"', compact)
        self.assertIn('t."OcorrenciaParadaPadraoId"ISNULLOR(', compact)
        self.assertIn('obs."PadraoVersaoId"=v."Id"', compact)
        self.assertIn('obs."Id"<>\'00000000-0000-0000-0000-000000000000\'::uuid', compact)
        # Reject direct equality in either order, and indirect equality through
        # aliases whose Id is already joined to the operational destination.
        all_code = re.sub(r"--[^\n]*", "", text)
        target_aliases = {"o"}
        for alias in re.findall(r'\b(\w+)\."Id"\s*=\s*t\."ProximaOcorrenciaParadaPadraoId"', all_code):
            target_aliases.add(alias)
        left = r't\."OcorrenciaParadaPadraoId"'
        right = r'(?:t\."ProximaOcorrenciaParadaPadraoId"|' + '|'.join(
            re.escape(a) + r'\."Id"' for a in target_aliases) + ')'
        self.assertNotRegex(all_code, rf'(?:{left}\s*=\s*{right}|{right}\s*=\s*{left})')
        self.assertNotRegex(all_code, r'obs\."Id"\s*=\s*o\."Id"|o\."Id"\s*=\s*obs\."Id"')
        pares = text.split("pares AS MATERIALIZED (", 1)[1].split("markers_negativos", 1)[0]
        self.assertIn('h."OcorrenciaParadaPadraoId"=g."ProximaOcorrenciaParadaPadraoId"', pares)
        self.assertNotIn('g."OcorrenciaParadaPadraoId"', pares)

    def test_documented_selective_generator_without_database(self):
        doc = (ROOT / "NoPonto/docs/ETA_ML_TREINO_3B.md").read_text(encoding="utf-8")
        script = doc.split("python3 - <<'PY'\n", 1)[1].split("\nPY\n", 1)[0]
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            (base / "NoPonto").mkdir()
            (base / "NoPonto" / SQL.name).write_bytes(SQL.read_bytes())
            result = subprocess.run([sys.executable, "-c", script], cwd=base,
                                    capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 0, result.stderr)
            generated = (base / "eta-auditoria-seletiva.sql").read_text(encoding="utf-8")
        self.assertIn("\n\\if :executar\n", generated)
        self.assertNotIn("\\\\if", generated)
        self.assertIn('WHERE t."ViagemId"=:\'viagem_id\'::uuid', generated)
        self.assertNotIn('WHERE t."TimestampGps">=TIMESTAMPTZ', generated)
        self.assertIn("EXPLAIN (COSTS ON)", generated)
        self.assertNotIn("ANALYZE", generated)
        self.assertTrue(generated.startswith("BEGIN READ ONLY;"))
        self.assertTrue(generated.endswith("COMMIT;\n"))


if __name__ == "__main__":
    unittest.main()
