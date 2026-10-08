"""Review pins cannot prove runtime coverage; changes demand an explicit renewed review."""
import hashlib
import json
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[2]


class CoverageGuards(unittest.TestCase):
    def test_reviewed_sources_have_not_silently_changed(self):
        inventory=json.loads((ROOT/'tools/eta_ml/coverage.sources.json').read_text())
        self.assertFalse(inventory['certification_enabled'])
        self.assertFalse(inventory['coverage_complete'])
        for item in inventory['files']:
            text=(ROOT/item['path']).read_text(encoding='utf-8-sig')
            self.assertEqual(item['sha256_normalized_utf8'],hashlib.sha256(text.encode()).hexdigest(),item['path'])

    def test_off_and_uninstrumented_ingress_are_not_certifiable(self):
        program=(ROOT/'NoPonto/Program.cs').read_text(encoding='utf-8-sig')
        self.assertNotIn('BindLocal(',program)
        self.assertNotIn('GetSection("EtaDecisionCoverage',program)
        polling=(ROOT/'NoPonto/2-Application/Services/GPS/GpsPollingService.cs').read_text()
        self.assertIn('ingress-frontier-not-attested',polling)
        self.assertIn('_coverage?.Enabled == true ?',polling)
        producer=(ROOT/'NoPonto/4-Data/Repositories/EtaEvidenceBoundaryWriter.cs').read_text()
        self.assertIn('eta_evidence_3g3b1_',producer)
        self.assertIn('c.Host != "127.0.0.1"',producer)
        self.assertIn('Prospective Begin is missing',producer)
        self.assertIn('EtaTripEvidence.MissingDecision',producer)
        self.assertNotIn('RegisterEpochAsync',producer)

    def test_v1_fixture_is_not_replaced_by_v2(self):
        first=json.loads((ROOT/'tools/eta_ml/fixtures/trip-evidence-v1.json').read_text())
        second=json.loads((ROOT/'tools/eta_ml/fixtures/trip-evidence-v2.json').read_text())
        self.assertEqual(first['bundle']['evidence_contract'],'eta-trip-evidence-v1')
        self.assertEqual(second['evidence_contract'],'eta-trip-evidence-v2')
        self.assertTrue(second['fixture_only'])
        self.assertFalse(second['certification_enabled'])

    def test_ingress_precedes_filtering_and_finally_resolves_unclaimed(self):
        polling=(ROOT/'NoPonto/2-Application/Services/GPS/GpsPollingService.cs').read_text()
        admission=polling.index('_ingress?.Begin(posicoes)')
        self.assertLess(admission,polling.index('var maisRecentes',admission))
        self.assertIn('ingressBatch?.Dispose()',polling)
        ingress=(ROOT/'NoPonto/2-Application/Services/GPS/EtaGpsIngressCoverage.cs').read_text()
        self.assertIn('if(!coverage.Enabled)return null',ingress)
        self.assertIn('conflicting-ingress-payload',ingress)
        self.assertIn('SameRawObservation',ingress)

    def test_recovery_is_read_only_explicit_and_never_certifies(self):
        recovery=(ROOT/'NoPonto/4-Data/Repositories/EtaEvidenceRecoveryRepository.cs').read_text()
        self.assertIn('SET TRANSACTION READ ONLY',recovery)
        self.assertIn('trips.Count>200',recovery)
        self.assertIn('"NaoVerificada"',recovery)
        for forbidden in ('INSERT INTO','UPDATE ','DELETE FROM','AuditadaSemProtecao','TelemetriasVeiculoMl'):
            self.assertNotIn(forbidden,recovery)


if __name__=='__main__': unittest.main()
