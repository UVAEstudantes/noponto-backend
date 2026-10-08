import copy
import csv
import json
import math
import pickle
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
import numpy as np
from pipeline import Baselines, CUTOFF, FEATURES, load, matrix, metrics, sha, train, volume_report, write_json
from synthetic import generate
from collection_profiles import PROFILES, HISTORICAL, CURRENT, SYNTHETIC, dataset_profile


def audited_manifest(manifest, profile_name):
    p = PROFILES[profile_name]
    manifest.update(data_kind="real", cutoff_utc=p["cutoff"], collection_profile=profile_name)
    manifest["collection"] = {
        "DatasetVersion": p["dataset_contract"], "CollectionStartedAtUtc": p["cutoff"],
        "BackendCommit": p["backend_commit"], "Image": p["image"], "Migration": p["migration"],
        "Sampling": copy.deepcopy(p["sampling"]), "DataKind": "real",
        "Trips": [{"ViagemId": t["viagem_id"], "Inicio": t["inicio"], "Fim": t["fim"],
                   "Qualidade": "AuditadaSemProtecao", "ReferenciaAuditoria": "synthetic-test-only"}
                  for t in manifest["trips"]]}
    return manifest


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
        command = [sys.executable, str(Path(__file__).with_name("evaluate.py")),
                   "--dataset", str(self.csv), "--manifest", str(self.manifest),
                   "--config", str(self.config_path), "--model", str(self.root / "a/model.pkl"),
                   "--model-manifest", str(self.root / "a/model.manifest.json"),
                   "--output", str(self.root / "reevaluation.json")]
        result = subprocess.run(command, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads((self.root / "reevaluation.json").read_text())["metrics"], a["metrics"])
        metadata = json.loads((self.root / "a/model.manifest.json").read_text())
        metadata["collection_profile"] = CURRENT
        write_json(self.root / "a/model.manifest.json", metadata)
        result = subprocess.run(command, capture_output=True, text=True, timeout=30)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("divergem do perfil", result.stderr)

    def test_real_volume_gate(self):
        rows, _, _ = load(self.csv, self.manifest, self.config)
        report = volume_report(rows)
        self.assertFalse(report["ready"])
        self.assertEqual(report["trips"], {"TRAIN": 144, "VALIDATION": 48, "TEST": 48})
        m = json.loads(self.manifest.read_text())
        audited_manifest(m, HISTORICAL)
        write_json(self.manifest, m)
        with self.assertRaisesRegex(ValueError, "Volume real insuficiente"):
            train(self.csv, self.manifest, self.config_path, self.root / "forbidden")
        self.assertFalse((self.root / "forbidden").exists())

    def test_official_profiles_and_legacy_fixture(self):
        fixture = json.loads(self.manifest.read_text())
        self.assertEqual(dataset_profile(fixture, self.config)[0], SYNTHETIC)
        for name in (HISTORICAL, CURRENT):
            m = audited_manifest(copy.deepcopy(fixture), name)
            config = dict(self.config, collection_profile=name, inicio=PROFILES[name]["cutoff"])
            write_json(self.manifest, m)
            self.assertEqual(len(load(self.csv, self.manifest, config)[0]), 1920)
            if name == HISTORICAL:
                m.pop("collection_profile")
                m["collection"]["CollectionProfile"] = None
                write_json(self.manifest, m)
                self.assertEqual(len(load(self.csv, self.manifest, self.config)[0]), 1920)

    def test_mixed_release_and_config_fail(self):
        original = audited_manifest(json.loads(self.manifest.read_text()), CURRENT)
        config = dict(self.config, collection_profile=CURRENT, inicio=PROFILES[CURRENT]["cutoff"])
        old = PROFILES[HISTORICAL]
        mutations = [lambda m: m["collection"].update(BackendCommit=old["backend_commit"]),
                     lambda m: m["collection"].update(Image=old["image"]),
                     lambda m: m["collection"].update(CollectionStartedAtUtc=old["cutoff"]),
                     lambda m: m.update(cutoff_utc=old["cutoff"]),
                     lambda m: m["collection"].update(DatasetVersion="other"),
                     lambda m: m["collection"].update(Migration="other"),
                     lambda m: m["collection"]["Sampling"].update(seed="other"),
                     lambda m: m["collection"]["Sampling"].update(enabled=1),
                     lambda m: m["collection"]["Sampling"].update(line_percentage=10.0),
                     lambda m: m["collection"]["Trips"][0].update(Qualidade="NaoVerificada")]
        for mutate in mutations:
            m = copy.deepcopy(original)
            mutate(m)
            write_json(self.manifest, m)
            with self.assertRaises(ValueError):
                load(self.csv, self.manifest, config)
        write_json(self.manifest, original)
        for wrong in (self.config, dict(config, collection_profile=HISTORICAL),
                      dict(config, inicio=old["cutoff"])):
            with self.assertRaises(ValueError):
                load(self.csv, self.manifest, wrong)

    def test_synthetic_cannot_be_relabelled_real(self):
        m = json.loads(self.manifest.read_text())
        m["data_kind"] = "real"
        write_json(self.manifest, m)
        with self.assertRaises(ValueError):
            load(self.csv, self.manifest, self.config)

    def test_current_real_cli_volume_and_train_validate_same_profile(self):
        m = audited_manifest(json.loads(self.manifest.read_text()), CURRENT)
        write_json(self.manifest, m)
        config = dict(self.config, collection_profile=CURRENT, inicio=PROFILES[CURRENT]["cutoff"])
        config_path = self.root / "current-config.json"
        write_json(config_path, config)
        for cli in ("check_volume.py", "train.py"):
            command = [sys.executable, str(Path(__file__).with_name(cli)),
                       "--dataset", str(self.csv), "--manifest", str(self.manifest),
                       "--config", str(config_path), "--output", str(self.root / (cli + ".output"))]
            result = subprocess.run(command, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 2 if cli == "check_volume.py" else 1)
            self.assertIn('"ready": false' if cli == "check_volume.py" else "Volume real insuficiente",
                          result.stdout if cli == "check_volume.py" else result.stderr)
            write_json(config_path, dict(config, collection_profile=HISTORICAL))
            result = subprocess.run(command, capture_output=True, text=True, timeout=30)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("perfis diferentes", result.stderr)
            write_json(config_path, config)


if __name__ == "__main__":
    unittest.main()
