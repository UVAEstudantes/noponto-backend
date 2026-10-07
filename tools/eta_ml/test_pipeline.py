import copy
import csv
import json
import math
import pickle
import tempfile
import unittest
from pathlib import Path
import numpy as np
from pipeline import Baselines, CUTOFF, FEATURES, load, matrix, metrics, sha, train, volume_report, write_json
from synthetic import generate


class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        generate(self.root / "data")
        self.csv = self.root / "data/dataset.csv"
        self.manifest = self.root / "data/dataset.manifest.json"
        self.config_path = Path(__file__).with_name("config.synthetic.json")
        self.config = json.loads(self.config_path.read_text())

    def tearDown(self):
        self.tmp.cleanup()

    def mutate(self, callback):
        with self.csv.open(newline="") as stream:
            reader = csv.DictReader(stream)
            fields, rows = reader.fieldnames, list(reader)
        callback(rows)
        with self.csv.open("w", newline="") as stream:
            writer = csv.DictWriter(stream, fields)
            writer.writeheader()
            writer.writerows(rows)
        m = json.loads(self.manifest.read_text())
        m["dataset_sha256"] = sha(self.csv)
        write_json(self.manifest, m)

    def test_cutoff_and_boundary_purge_whole_trip(self):
        m = json.loads(self.manifest.read_text())
        m["trips"][0]["inicio"] = "2026-10-07T14:53:59.225081Z"
        m["trips"][1]["fim"] = self.config["fim_treino"]
        write_json(self.manifest, m)
        rows, _, drops = load(self.csv, self.manifest, self.config)
        self.assertEqual(drops, {"trip_outside_window": 8, "trip_crosses_split": 8})
        self.assertFalse(any(r["viagem_id"] in {m["trips"][i]["viagem_id"] for i in (0, 1)} for r in rows))

    def test_split_overlap_rejected(self):
        self.mutate(lambda rows: rows[0].update(split="TEST"))
        with self.assertRaisesRegex(ValueError, "Split"):
            load(self.csv, self.manifest, self.config)

    def test_duplicate_rejected(self):
        self.mutate(lambda rows: rows.append(rows[0].copy()))
        with self.assertRaisesRegex(ValueError, "duplicado"):
            load(self.csv, self.manifest, self.config)

    def test_label_and_provenance_never_features(self):
        rows, _, _ = load(self.csv, self.manifest, self.config)
        before = matrix(rows)
        for r in rows:
            for k in ("label_segundos", "timestamp_passagem", "observacao_id", "viagem_id", "veiculo", "provedor"):
                r[k] = "poison"
        after = matrix(rows)
        np.testing.assert_equal(before[:, :9], after[:, :9])
        np.testing.assert_allclose(before[:, 9:].astype(float), after[:, 9:].astype(float), equal_nan=True)
        self.assertFalse(set(FEATURES) & {"label_segundos", "timestamp_passagem", "viagem_id"})

    def test_baseline_train_only_unseen_and_invalid_speed(self):
        rows, _, _ = load(self.csv, self.manifest, self.config)
        b = Baselines().fit([r for r in rows if r["split"] == "TRAIN"])
        row = dict(rows[-1], codigo_linha="UNKNOWN", velocidade_kmh=0, velocidade_media_causal_kmh=float("nan"))
        self.assertEqual(b.predict([row], "historical")[0], b.global_eta)
        self.assertGreater(b.predict([row], "physical")[0], 0)
        original = b.predict([row], "historical")[0]
        row["label_segundos"] = 999999
        self.assertEqual(original, b.predict([row], "historical")[0])

    def test_metrics_known_and_coverage(self):
        m = metrics([10, 20, 30], [12, 16, float("nan")])
        self.assertEqual(m["mae_seconds"], 3)
        self.assertAlmostEqual(m["rmse_seconds"], math.sqrt(10))
        self.assertEqual(m["coverage"], 2/3)
        self.assertEqual(m["median_absolute_error_seconds"], 3)
        self.assertAlmostEqual(m["p90_absolute_error_seconds"], 3.8)

    def test_invalid_label_and_hash(self):
        self.mutate(lambda rows: rows[0].update(label_segundos="nan"))
        with self.assertRaisesRegex(ValueError, "Label"):
            load(self.csv, self.manifest, self.config)
        self.csv.write_text("corrupted")
        with self.assertRaisesRegex(ValueError, "hash"):
            load(self.csv, self.manifest, self.config)

    def test_fit_reproducibility_reload_unseen_category(self):
        self.mutate(lambda rows: [r.update(codigo_linha="VALIDATION_ONLY") for r in rows if r["split"] == "VALIDATION"])
        a = train(self.csv, self.manifest, self.config_path, self.root / "a")
        b = train(self.csv, self.manifest, self.config_path, self.root / "b")
        self.assertEqual(a["metrics"], b["metrics"])
        self.assertEqual(a["artifact_sha256"], b["artifact_sha256"])
        bundle = pickle.loads((self.root / "a/model.pkl").read_bytes())
        rows, _, _ = load(self.csv, self.manifest, self.config)
        row = dict(rows[-1], codigo_linha="NEVER_SEEN", versao_id="NEW_VERSION")
        self.assertTrue(np.isfinite(bundle["model"].predict(matrix([row]))[0]))
        encoder = bundle["model"][0].named_transformers_["categories"]
        self.assertNotIn("NEVER_SEEN", encoder.categories_[2])
        self.assertNotIn("VALIDATION_ONLY", encoder.categories_[2])

    def test_real_volume_gate(self):
        rows, _, _ = load(self.csv, self.manifest, self.config)
        report = volume_report(rows)
        self.assertFalse(report["ready"])
        self.assertEqual(report["trips"], {"TRAIN": 144, "VALIDATION": 48, "TEST": 48})
        m = json.loads(self.manifest.read_text())
        m["data_kind"] = "real"
        write_json(self.manifest, m)
        with self.assertRaisesRegex(ValueError, "Volume real insuficiente"):
            train(self.csv, self.manifest, self.config_path, self.root / "forbidden")
        self.assertFalse((self.root / "forbidden").exists())


if __name__ == "__main__":
    unittest.main()
