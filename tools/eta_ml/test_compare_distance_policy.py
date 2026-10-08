import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch
from tools.eta_ml import compare_distance_policy as c


class ComparisonTests(unittest.TestCase):
    def test_exporter_policy_matches_python_guard(self):
        from tools.eta_ml.validation_policies import CURRENT
        root=Path(__file__).resolve().parents[2]
        source=(root/'NoPonto/2-Application/Services/GPS/EtaDataset.cs').read_text(encoding='utf-8-sig')
        self.assertIn(f'public const string PoliticaValidacao = "{CURRENT}";',source)
        exporter=(root/'tools/EtaMl.Export/Program.cs').read_text(encoding='utf-8-sig')
        self.assertIn('validation_policy=EtaDataset.PoliticaValidacao',exporter)

    def test_existing_output_and_untrusted_previous_evidence_blocked(self):
        with tempfile.TemporaryDirectory() as root:
            p=Path(root)
            with self.assertRaisesRegex(ValueError,'New output'):
                c.compare(p/'bundle',p/'csv',p/'report',p)
            previous={'sources':{},'candidate_file_sha256':'bad'}
            with patch.object(c.json,'loads',return_value=previous),patch.object(Path,'read_text',return_value='{}'):
                with self.assertRaisesRegex(ValueError,'Previous evidence'):
                    c.compare(p/'bundle',p/'csv',p/'report',p/'new')
