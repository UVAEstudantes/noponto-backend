"""Offline wiring/compatibility guards; never connects to a database."""
import json
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[2]


class EvidenceFoundationGuards(unittest.TestCase):
    def test_integration_remains_off_without_activation(self):
        program=(ROOT/'NoPonto/Program.cs').read_text(encoding='utf-8-sig')
        self.assertIn('AddOptions<EtaDecisionCoverageOptions>()',program)
        self.assertNotIn('GetSection("EtaDecisionCoverage',program)
        self.assertNotIn('BindLocal(',program)
        coverage=(ROOT/'NoPonto/2-Application/Services/GPS/EtaDecisionCoverage.cs').read_text()
        self.assertIn('public bool Enabled { get; set; }',coverage)
        self.assertNotIn('RegisterEpochAsync',program)
        repository=(ROOT/'NoPonto/4-Data/Repositories/EtaTripEvidenceRepository.cs').read_text()
        self.assertIn('epoch.ActivatedUs is not null',repository)
        self.assertIn('epoch.WriterBarrierProven',repository)
        self.assertNotIn('NpgsqlDataSource.Create',repository)
        self.assertIn('"EtaEvidenceHeads"."Sequence"=@expected',repository)
        self.assertIn('FROM head_write',repository)

    def test_migration_additive_and_snapshot_v3_is_future_only(self):
        migration=(ROOT/'NoPonto/Migrations/20261008090000_EtaTripEvidenceFoundation.cs').read_text()
        for mutation in ('ALTER TABLE','DROP TABLE','INSERT INTO','UPDATE "ViagensOperacionais"'):
            self.assertNotIn(mutation,migration)
        self.assertEqual(migration.count('CREATE TABLE'),4)
        spec=json.loads((ROOT/'tools/eta_ml/snapshot-v3.contract.json').read_text())
        self.assertEqual(spec['status'],'prepared-not-enabled')
        self.assertIn('explicit',spec['sources']['EtaEvidenceJournal']['filter'])
        self.assertIn('PK lookup',spec['sources']['OutboxViagens']['filter'])

    def test_exclusive_local_harness(self):
        script=(ROOT/'tools/eta_ml/homologate_evidence.ps1').read_text()
        self.assertIn('127.0.0.1:',script);self.assertIn('[Guid]::NewGuid()',script)
        self.assertIn('noponto.fixture=evidence-3g3b1',script)
        self.assertNotIn('docker compose',script);self.assertNotIn('docker system prune',script)
        test=(ROOT/'NoPonto/5-Testes/EtaTripEvidenceLocalTests.cs').read_text()
        self.assertIn('migration.UpOperations',test);self.assertIn('Assert.Equal(Marker',test)
        self.assertIn('Assert.Equal("127.0.0.1"',test)


if __name__=='__main__':unittest.main()
