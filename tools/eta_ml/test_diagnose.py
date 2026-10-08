"""Exploration tests are offline; no subprocesses or connection calls."""
import copy
import json
import re
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from tools.eta_ml import diagnose as d


def sample():
    uid = '00000000-0000-0000-0000-000000000001'
    g = dict(ViagemId=uid, SentidoId=uid, LinhaId=uid, PadraoVersaoId=uid,
             ProximaOcorrenciaParadaPadraoId=uid, OcorrenciaParadaPadraoId=uid,
             Volta=0, OrigemPosicao='REAL', Modal='ONIBUS', TimestampGps='2026-10-08T00:01:00Z',
             ObservacaoId='fixture', OrdemVeiculo='fixture', CodigoLinha='fixture', Provedor='fixture',
             PosicaoNaRota=0.1, ComprimentoRotaMetros=1000, DistanciaProximaParadaMetros=100)
    h = dict(ViagemId=uid, SentidoId=uid, PadraoVersaoId=uid, OcorrenciaParadaPadraoId=uid,
             Volta=0, Ordem='fixture', CodigoLinha='fixture', ParadaId=uid,
             TimestampPassagem='2026-10-08T00:02:00Z', TimestampGps='2026-10-08T00:02:10Z', PosicaoNaRota=0.2)
    target = dict(ocorrencia_id=uid, parada_id=uid, padrao_id=uid, linha_id=uid, sentido_id=uid,
                  versao_id=uid, codigo_linha='fixture', topologia='LINEAR', posicao_destino=0.2)
    j = dict(tipo='PassagemParada', schema_version=2, event_id=f'passagem:{uid}:{uid}:0',
             viagem_id=uid, ordem_veiculo='fixture', codigo_linha='fixture', sentido_id=uid,
             padrao_versao_id=uid, ocorrencia_parada_padrao_id=uid, parada_id=uid, volta=0,
             posicao_linha=0.2, timestamp_passagem=h['TimestampPassagem'], timestamp_gps=h['TimestampGps'],
             linha_id=uid, padrao_operacional_id=uid)
    trip = dict(viagem_id=uid, inicio='2026-10-08T00:00:00Z', fim='2026-10-08T00:03:00Z')
    return g, h, j, target, trip


class DiagnoseTests(unittest.TestCase):
    def test_copy_lexer_preserves_literals_identifiers_and_parameters(self):
        sql = "SELECT 'a--b  c', 'it''s @codigo', \"x--y\", @codigo -- tail\n/* a /* nested */ b */ FROM t;"
        actual = d.copy_query(sql, {'codigo': "'linha--  com espaço'"})
        self.assertNotIn('\n', actual)
        for token in ("'a--b  c'", "'it''s @codigo'", '"x--y"', "'linha--  com espaço'"):
            self.assertIn(token, actual)
        self.assertNotIn('tail', actual)
        self.assertNotIn('nested', actual)
        self.assertTrue(actual.endswith('FROM t'))

    def test_copy_lexer_rejects_unsafe_or_unterminated_constructs(self):
        for sql in ("SELECT 'a\nb'", "SELECT 'a'\n'b'", "SELECT $$a\nb$$",
                    "SELECT E'a\\nb'", "SELECT 'a\\b'", "SELECT 'oops", "SELECT /* oops", "SELECT @unknown"):
            with self.subTest(sql=sql), self.assertRaises(ValueError):
                d.copy_query(sql, {})
        with self.assertRaises(ValueError):
            d.copy_query('SELECT @codigo', {'codigo': "'a\nb'"})

    def test_pinned_3a_query_tokens_unchanged(self):
        values = {'codigo': "'fixture'", 'inicio': "'2026-10-08T00:00:00Z'::timestamptz",
                  'fim': "'2026-10-08T01:00:00Z'::timestamptz",
                  'cursor_ts': "'2026-10-08T00:00:00Z'::timestamptz",
                  'cursor_id': "'00000000-0000-0000-0000-000000000000'::uuid", 'tamanho': '1000'}
        source = d.SQL.read_text(encoding='utf-8-sig')
        # Known pinned source has no comment markers/whitespace in its literals.
        original = re.sub(r'--[^\n]*', '', source).strip().rstrip(';')
        original = re.sub(r'@(codigo|inicio|fim|cursor_ts|cursor_id|tamanho)\b', lambda m: values[m[1]], original)
        self.assertEqual(' '.join(d.copy_query(source, values).split()), ' '.join(original.split()))

    def test_source_pins_fail_closed(self):
        pins = json.loads(Path(d.__file__).with_name('diagnose.sources.json').read_text())
        self.assertEqual(pins, dict(sql_3a=d.source_hash(d.SQL), validator_3a=d.source_hash(d.VALIDATOR)))
        with tempfile.TemporaryDirectory() as root, patch.object(d, 'source_hash', return_value='changed'):
            with self.assertRaisesRegex(ValueError, '3A source changed'):
                d.diagnose(Path(root)/'bundle', Path(root)/'output')

    def test_quality_never_granted_and_distance_pending(self):
        args = sample()
        self.assertEqual(d.reason(*args, 100, False), 'PendenteGeographySQL3A')
        self.assertEqual(d.reason(*args, 100, True), 'TecnicamenteElegivelPreliminar')
        self.assertNotIn('Qualidade', args[4])

    def test_rejections_and_no_label_invention(self):
        cases = [('label', 'LabelAusente'), ('journal', 'PassagemSemJournalConferido'),
                 ('identity', 'IdentidadeEstruturalIncompativel'), ('time', 'TempoInvalido'),
                 ('geometry', 'DestinoNaoAdianteOuDistanciaInvalida'),
                 ('distance', 'TecnicamenteElegivelPreliminar')]
        for kind, expected in cases:
            g, h, j, target, trip = copy.deepcopy(sample())
            if kind == 'label': h = None
            if kind == 'journal': j['schema_version'] = 1
            if kind == 'identity': g['OcorrenciaParadaPadraoId'] = None
            if kind == 'time': g['TimestampGps'] = h['TimestampPassagem']
            if kind == 'geometry': g['PosicaoNaRota'] = 0.5
            if kind == 'distance': g['DistanciaProximaParadaMetros'] = 200
            self.assertEqual(d.reason(g, h, j, target, trip, 100, True), expected)

    def test_destination_distance_and_identity_guards(self):
        for distance in (None, float('nan'), 0, -1, 2000):
            self.assertEqual(d.reason(*sample(), distance, True), 'DistanciaNaoConferida')
        g = sample()[0]
        g['ProximaOcorrenciaParadaPadraoId'] = None
        self.assertFalse(d.identity(g))

    def test_output_cannot_write_bundle_and_database_guard(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            for output in (path, path/'nested'):
                with self.assertRaises(ValueError): d.diagnose(path, output)
            with self.assertRaises(ValueError): d.diagnose(path/'bundle', path/'out', database='production')

    def test_report_sql_and_manual_geography_completion(self):
        g, h, j, target, trip = sample()
        uid = g['ViagemId']; g['Id'] = uid; trip['codigo_linha'] = 'fixture'
        manifest = dict(snapshot_version=d.s.VERSION, cutoff='2026-10-07T21:00:57.997530Z',
                        snapshot_end='2026-10-08T01:00:00Z', trips=[trip])
        records = {'TelemetriasVeiculoMl.csv': [g], 'HistoricoPassagens.csv': [h],
                   'EventosViagem.csv': [dict(EventId=j['event_id'], Payload=j)],
                   'OcorrenciasParadasPadroes.csv': [dict(Id=uid, PadraoVersaoId=uid, ParadaId=uid, PosicaoTracado=0.2)],
                   'PadroesVersoes.csv': [dict(Id=uid, PadraoOperacionalId=uid, Topologia='LINEAR')],
                   'PadroesOperacionais.csv': [dict(Id=uid, SentidoId=uid)],
                   'Sentidos.csv': [dict(Id=uid, LinhaId=uid)], 'Linhas.csv': [dict(Id=uid, Codigo='fixture')],
                   'candidates.csv': [dict(gps=g, passagem=h, journal=j, **target, distancia_rota_conferida_metros=100)]}
        with tempfile.TemporaryDirectory() as root:
            path = Path(root); bundle = path/'bundle'; bundle.mkdir()
            (bundle/'snapshot.manifest.json').write_text('{}')
            candidates = path/'candidates.csv'; candidates.write_text('synthetic-fixture-only')
            with patch.object(d.s, 'verify', return_value=manifest), patch.object(
                    d.s, 'rows', side_effect=lambda p: iter(records[p.name])):
                pending = d.diagnose(bundle, path/'pending')
                self.assertEqual(pending['total']['PendenteGeographySQL3A'], 1)
                complete = d.diagnose(bundle, path/'complete', candidates)
                self.assertEqual(complete['total']['TecnicamenteElegivelPreliminar'], 1)
                self.assertEqual(complete['trips'][0]['qualidade'], 'NaoVerificada')
                self.assertFalse(complete['training_dataset'])
                sql = (path/'complete/diagnostic.sql').read_text()
                self.assertIn('REPEATABLE READ', sql)
                self.assertIn('BEGIN READ ONLY', sql)
                self.assertIn('ST_LineSubstring', sql)
                copies = [line for line in sql.splitlines() if line.startswith('\\copy ')]
                self.assertEqual(len(copies), 1)
                self.assertTrue(copies[0].endswith("WITH (FORMAT csv, HEADER true, ENCODING 'UTF8')"))
                self.assertIn('WITH gps AS (', copies[0])
                self.assertIn('::geography', copies[0])
                self.assertIn('LIMIT 1000', copies[0])
                self.assertNotRegex(sql, r'(?i)\b(INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|TRUNCATE|ANALYZE)\b')
                records['candidates.csv'][0]['gps'] = {**g, 'OrdemVeiculo': 'tampered'}
                with self.assertRaisesRegex(ValueError, 'differs from sealed GPS'):
                    d.diagnose(bundle, path/'tampered', candidates)


if __name__ == '__main__':
    unittest.main()
