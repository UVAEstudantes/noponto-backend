import copy
import math
import json
import tempfile
from pathlib import Path
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parent))
import experiment as lab


class ExperimentTests(unittest.TestCase):
    def fixture(self):
        identity = dict.fromkeys(lab.IDENTITY,"synthetic-fixture")
        p = dict(identity,predictions={"physical":100.,"historical":130.,"extra_trees":110.})
        label = dict(identity,data_kind="synthetic",label_segundos=120.)
        return p,label

    def test_exact_join(self):
        p,l = self.fixture()
        self.assertEqual([(p,l)],lab.join([p],[l]))

    def test_identity_mismatches(self):
        p,l = self.fixture()
        for field in lab.IDENTITY:
            with self.subTest(field=field):
                other = dict(l,**{field:"different"})
                with self.assertRaisesRegex(ValueError,"Unmatched"):
                    lab.join([p],[other])

    def test_duplicate_prediction(self):
        p,l = self.fixture()
        with self.assertRaisesRegex(ValueError,"Duplicate prediction"):
            lab.join([p,p],[l])

    def test_duplicate_label(self):
        p,l = self.fixture()
        with self.assertRaisesRegex(ValueError,"Duplicate label"):
            lab.join([p],[l,l])

    def test_real_label_never_implicitly_certified(self):
        p,l = self.fixture()
        l["data_kind"] = "real"
        with self.assertRaisesRegex(ValueError,"cannot certify"):
            lab.join([p],[l])

    def test_no_label_no_match(self):
        p,_ = self.fixture()
        with self.assertRaises(ValueError): lab.join([p],[])

    def test_physical_formula_and_no_train_fallback(self):
        row = dict(velocidade_kmh=20.,velocidade_media_causal_kmh=36.,distancia_metros=1000.)
        self.assertEqual(100.,lab.physical(row))
        self.assertEqual(180.,lab.physical(dict(row,velocidade_media_causal_kmh=math.nan)))
        for v in (0., math.nan, math.inf, 161.):
            self.assertIsNone(lab.physical(dict(row,velocidade_kmh=v)))

    def test_metrics_and_common_sample_coverage(self):
        # Reuse actual scientific metrics when PYTHONPATH points to existing ML tools.
        import pipeline
        p,l = self.fixture()
        p2,l2 = copy.deepcopy(p),dict(l,observacao_id="second")
        p2["observacao_id"] = "second"
        p2["predictions"]["physical"] = None
        result = lab.report(lab.join([p,p2],[l,l2]),pipeline.metrics)
        self.assertEqual(2,result["individual_coverage"]["observations"])
        self.assertEqual(1,result["common_sample"]["observations"])
        self.assertEqual(.5,result["individual_coverage"]["physical"]["coverage"])
        self.assertEqual(20.,result["common_sample"]["physical"]["mae_seconds"])
        self.assertEqual(10.,result["common_sample"]["extra_trees"]["mae_seconds"])
        self.assertAlmostEqual(1/3,result["common_sample"]["physical"]["minutes"]["mae_minutes"])
        self.assertIn("prediction_disagreement_NOT_arrival_error",result)

    def test_retained_predictions_survive_restart_without_memory_state(self):
        p,l = self.fixture()
        with tempfile.TemporaryDirectory(prefix="noponto-tcc-fixture-") as temp:
            path = Path(temp)/"predictions.jsonl"
            path.write_text(json.dumps(p)+"\n",encoding="utf-8")
            restored = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
            self.assertEqual([(p,l)],lab.join(restored,[l]))


if __name__ == "__main__": unittest.main()
