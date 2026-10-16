"""Synthetic fixtures only; no model, database, audit approval or scientific packages."""
import copy
import csv
import hashlib
import json
import re
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import UUID

import evaluate_shadow as subject


class ShadowArrivalFixtureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="noponto-shadow-4d-fixture-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.csv = self.root/"dataset.csv"
        self.manifest = self.root/"dataset.manifest.json"
        self.audit = self.root/"audit.json"
        self.predictions = self.root/"predictions.jsonl"
        self.time = datetime(2026, 10, 11, 12, tzinfo=timezone.utc)
        self.trip = str(UUID(int=1))
        self.audit_value = {"DatasetVersion": "noponto-eta-gps-v1", "DataKind": "synthetic",
                            "SnapshotReference": "fixture-not-real-audit", "Trips": [
                                {"ViagemId": self.trip, "Inicio": self.ts(self.time-timedelta(minutes=1)),
                                 "Fim": self.ts(self.time+timedelta(minutes=10)), "CodigoLinha": "42",
                                 "Qualidade": "AuditadaSemProtecao", "ReferenciaAuditoria": "fixture-external-audit-only"}]}
        self.labels = [self.label()]
        self.records = [self.prediction(self.labels[0])]
        self.save()

    @staticmethod
    def ts(value):
        return value.strftime("%Y-%m-%dT%H:%M:%S")+f".{value.microsecond:06d}0+00:00"

    def label(self, second=0):
        gps = self.time+timedelta(seconds=second)
        passage = self.time+timedelta(seconds=120)
        row = {k: "" for k in subject.canonical_header()}
        row.update(viagem_id=self.trip, volta="0", veiculo="fixture-vehicle", modal="ONIBUS", provedor="GTFSRT_BUS",
                   timestamp_gps=self.ts(gps), codigo_linha="42", topologia="LINEAR", posicao_gps=".1", posicao_destino=".8",
                   distancia_metros="320", velocidade_kmh="20", velocidade_media_causal_kmh="18", hora_dia="9", dia_semana="0",
                   split="TEST", timestamp_passagem=self.ts(passage), label_segundos=str((passage-gps).total_seconds()))
        for i, key in enumerate(("linha_id", "sentido_id", "padrao_id", "versao_id", "ocorrencia_id", "parada_id"), 2):
            row[key] = str(UUID(int=i))
        identity = f'{row["modal"]}|{row["provedor"]}|{row["veiculo"].upper()}|{row["timestamp_gps"]}'
        row["observacao_id"] = hashlib.sha256(identity.encode()).hexdigest()
        return row

    def prediction(self, label):
        row = dict(zip(subject.IDENTITY, (label[k] for k in subject.CSV_NAMES)))
        row["lap"] = int(row["lap"])
        row.update(schema=subject.SCHEMA, data_kind="fixture", instance_id="fixture-instance",
                   captured_at=self.ts(self.time+timedelta(seconds=1)), recorded_at=self.ts(self.time+timedelta(seconds=2)),
                   prediction_valid_until=self.ts(self.time+timedelta(seconds=31)), status="success", model_digest="a"*64,
                   ml_eta=float(label["label_segundos"])-20, public_eta=80, physical_status="success", historical_status="unavailable_no_audited_baseline",
                   historical_eta=None, historical_digest=None, training_end_utc=self.ts(self.time-timedelta(days=1)), prediction_duration_ms=12)
        self.rekey(row)
        return row

    @staticmethod
    def rekey(row):
        value = f'{subject.CONTRACT}|{row["observation_id"]}|{row["trip_id"]}|{row["target_id"]}|{row["version_id"]}|{row["lap"]}'
        row["request_id"] = hashlib.sha256(value.encode()).hexdigest()

    def save(self):
        with self.csv.open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=subject.canonical_header())
            writer.writeheader(); writer.writerows(self.labels)
        self.audit.write_text(json.dumps(self.audit_value), encoding="utf-8")
        self.manifest.write_text(json.dumps({"dataset_version": "noponto-eta-gps-v1", "data_kind": "synthetic",
                                           "exporter_version": "eta-export-3b-v1", "collection": self.audit_value,
                                           "dataset_sha256": subject.sha(self.csv), "audit_sha256": subject.sha(self.audit),
                                           "trips": [{"viagem_id": t["ViagemId"], "inicio": t["Inicio"], "fim": t["Fim"]}
                                                     for t in self.audit_value["Trips"]]}), encoding="utf-8")
        self.predictions.write_text("".join(json.dumps(r)+"\n" for r in self.records), encoding="utf-8")

    def evaluate(self, **options):
        self.save()
        return subject.evaluate([self.predictions], self.csv, self.manifest, self.audit, fixtures=True, **options)

    def test_historical_export_never_promotes_fixture_labels_to_real(self):
        import export_shadow_baseline
        model=self.root/"model.fixture.json"
        model.write_text(json.dumps({"data_kind":"synthetic"}),encoding="utf-8")
        output=self.root/"baseline.json"
        with self.assertRaises(ValueError):
            export_shadow_baseline.export(self.csv,self.manifest,self.audit,model,output)
        self.assertFalse(output.exists())

    def test_sources_have_signed_bias_and_common_identity_pairs(self):
        self.records[0].update(historical_status="success", historical_eta=110, historical_digest="b"*64)
        report, _ = self.evaluate(minimum_rows=1, minimum_trips=1)
        sources = report["arrival_sources"]
        self.assertEqual(-40, sources["by_source_model"]["physical:causal-v1"]["bias_seconds"])
        self.assertEqual(20, sources["by_source_model"]["ml:"+"a"*64]["mae_seconds"])
        self.assertEqual(10, sources["by_source_model"]["historical:"+"b"*64]["median_absolute_error_seconds"])
        self.assertEqual(1, sources["paired_with_physical"]["ml:"+"a"*64]["same_identity_rows"])
        self.assertEqual(1,sources["three_way_common"][0]["same_identity_rows"])
        self.assertTrue(any(r["field"]=="actual_arrival_horizon" for r in sources["breakdowns"]))

    def test_no_model_still_evaluates_physical_against_audited_arrival(self):
        self.records[0].update(status="model_unavailable", model_digest=None, ml_eta=None)
        report, _ = self.evaluate()
        self.assertEqual(0, report["arrival_error"]["n"])
        self.assertEqual(1, report["arrival_sources"]["by_source_model"]["physical:causal-v1"]["n"])
        self.assertEqual(1, report["arrival_sources"]["losses"]["ml_unavailable"])

    def test_model_cutoff_after_observation_is_temporal_leakage(self):
        self.records[0]["training_end_utc"] = self.ts(self.time+timedelta(seconds=1))
        report, rows = self.evaluate()
        self.assertEqual([], rows)
        self.assertEqual(1, report["losses"]["model_temporal_leakage"])
        self.assertNotIn("ml:"+"a"*64,report["arrival_sources"]["by_source_model"])

    def test_exact_audited_observation_passage_match(self):
        report, rows = self.evaluate()
        self.assertEqual(1, report["arrival_error"]["n"])
        self.assertEqual(20, report["arrival_error"]["mae_seconds"])
        self.assertEqual(100, report["arrival_error"]["within_60_percent"])
        self.assertEqual(self.trip, rows[0]["trip_id"])
        self.assertEqual("fixture", report["data_kind"])

    def test_destination_version_trip_lap_and_timestamp_must_match_exactly(self):
        original = copy.deepcopy(self.records[0])
        for field, value in (("target_id", str(UUID(int=100))), ("version_id", str(UUID(int=100))),
                             ("trip_id", str(UUID(int=100))), ("lap", 1),
                             ("timestamp_gps", "2026-10-11T12:00:00.0000001+00:00"),
                             ("line_id", str(UUID(int=100))), ("direction_id", str(UUID(int=100))),
                             ("pattern_id", str(UUID(int=100))), ("stop_id", str(UUID(int=100)))):
            with self.subTest(field=field):
                self.records = [copy.deepcopy(original)]
                self.records[0][field] = value; self.rekey(self.records[0])
                report, rows = self.evaluate()
                self.assertEqual([], rows)
                self.assertEqual(1, report["losses"]["identity_mismatch"])

    def test_missing_label_is_coverage_loss_not_zero_error(self):
        self.labels = []
        report, rows = self.evaluate()
        self.assertEqual([], rows)
        self.assertEqual(1, report["losses"]["missing_label"])
        self.assertEqual(0, report["coverage"]["evaluated_prediction_fraction"])
        self.assertIsNone(report["arrival_error"]["mae_seconds"])
        self.assertEqual(20, report["prediction_comparison"]["mean_absolute_difference_seconds"])

    def test_identical_duplicate_is_counted_once(self):
        self.records.append(copy.deepcopy(self.records[0]))
        report, rows = self.evaluate()
        self.assertEqual(1, len(rows))
        self.assertEqual(1, report["losses"]["duplicate_prediction"])
        self.assertEqual(2, report["coverage"]["raw_records"])

    def test_conflicting_duplicate_rejects_all_without_cherry_picking(self):
        row = copy.deepcopy(self.records[0]); row["ml_eta"] += 10
        self.records.append(row)
        report, rows = self.evaluate()
        self.assertEqual([], rows)
        self.assertEqual(2, report["losses"]["conflicting_duplicate"])

    def test_expired_status_and_late_response_are_excluded(self):
        original = copy.deepcopy(self.records[0])
        for defect in ("status", "late"):
            self.records = [copy.deepcopy(original)]
            if defect == "status":
                self.records[0].update(status="expired", model_digest=None, ml_eta=None)
            else:
                self.records[0]["recorded_at"] = self.ts(self.time+timedelta(seconds=32))
            report, rows = self.evaluate()
            self.assertEqual([], rows)
            self.assertEqual(1, report["losses"].get("status.expired", report["losses"].get("expired_prediction")))

    def test_unavailable_model_does_not_fabricate_prediction(self):
        self.records[0].update(status="model_unavailable", model_digest=None, ml_eta=None)
        report, rows = self.evaluate()
        self.assertEqual([], rows)
        self.assertEqual(1, report["losses"]["status.model_unavailable"])
        self.assertEqual(0, report["prediction_comparison"]["n"])

    def test_mae_median_p90_thresholds_and_breakdown_limits(self):
        self.labels = [self.label(-i) for i in range(5)]
        self.records = [self.prediction(row) for row in self.labels]
        for record, label, error in zip(self.records, self.labels, (0, 30, 70, 130, 200)):
            record["ml_eta"] = float(label["label_segundos"])+error
        report, _ = self.evaluate(minimum_rows=5, minimum_trips=1)
        actual = report["arrival_error"]
        self.assertEqual((86, 70, 200, 40, 60), tuple(actual[k] for k in
                         ("mae_seconds", "median_absolute_error_seconds", "p90_absolute_error_seconds", "within_60_percent", "within_120_percent")))
        self.assertEqual(1, len(report["breakdowns"]["modal"]))
        report, _ = self.evaluate(minimum_rows=6, minimum_trips=1)
        self.assertEqual([], report["breakdowns"]["line_code"])
        report, _ = self.evaluate(minimum_rows=5, minimum_trips=3)
        self.assertEqual([], report["breakdowns"]["modal"])

    def test_models_are_reported_separately(self):
        other = copy.deepcopy(self.records[0]); other.update(model_digest="b"*64, ml_eta=120)
        self.records.append(other)
        report, _ = self.evaluate()
        self.assertEqual(20, report["arrival_error_by_model"]["a"*64]["mae_seconds"])
        self.assertEqual(0, report["arrival_error_by_model"]["b"*64]["mae_seconds"])
        self.assertEqual(1, report["coverage"]["matched_canonical_labels"])

    def test_unaudited_and_protected_trips_fail_before_evaluation(self):
        for quality in ("NaoVerificada", "ProtegidaOuAmbigua"):
            self.audit_value["Trips"][0]["Qualidade"] = quality
            with self.assertRaisesRegex(ValueError, "Unaudited"):
                self.evaluate()

    def test_hashes_and_fixture_opt_in_are_required(self):
        with self.assertRaises(ValueError):
            subject.evaluate([self.predictions], self.csv, self.manifest, self.audit)
        self.audit.write_text(self.audit.read_text()+" ")
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            subject.evaluate([self.predictions], self.csv, self.manifest, self.audit, fixtures=True)

    def test_corrupt_identity_or_digest_is_rejected(self):
        original = copy.deepcopy(self.records[0])
        for field in ("request_id", "model_digest"):
            self.records = [copy.deepcopy(original)]; self.records[0][field] = "invalid"
            report, rows = self.evaluate()
            self.assertEqual([], rows); self.assertEqual(1, report["losses"]["invalid_prediction"])

    def test_restart_between_capture_and_cli_evaluation_and_truncated_tail(self):
        with self.predictions.open("ab") as stream:
            stream.write(b'{"partial_crash":')
        output = self.root/"evaluation-after-restart"
        subprocess.run([sys.executable, "-B", str(Path(subject.__file__)), "--predictions", str(self.predictions),
                        "--dataset", str(self.csv), "--manifest", str(self.manifest), "--audit", str(self.audit),
                        "--output", str(output), "--fixtures"], check=True, capture_output=True)
        report = json.loads((output/"evaluation.json").read_text())
        self.assertEqual(1, report["arrival_error"]["n"])
        self.assertEqual(1, report["losses"]["truncated_tail"])
        self.assertTrue(report["capture_loss_counts_may_be_incomplete"])
        with (output/"evaluated.csv").open(newline="") as stream:
            self.assertEqual(1, len(list(csv.DictReader(stream))))

    def test_terminal_summary_preserves_known_queue_and_quota_losses(self):
        self.records.append({"schema": subject.SCHEMA, "data_kind": "fixture", "kind": "export_summary",
                             "instance_id": "fixture-instance", "complete": True,
                             "counters": {"queue_full_drop": 2, "quota_drop": 3, "written": 1}})
        report, _ = self.evaluate()
        self.assertEqual(2, report["known_export_counters"]["queue_full_drop"])
        self.assertEqual(3, report["known_export_counters"]["quota_drop"])
        self.assertTrue(report["all_export_instances_closed"])

    def test_duplicate_canonical_passage_is_rejected(self):
        self.labels.append(copy.deepcopy(self.labels[0]))
        with self.assertRaisesRegex(ValueError, "Duplicate canonical"):
            self.evaluate()

    def test_inconsistent_arrival_seconds_are_not_a_label(self):
        self.labels[0]["label_segundos"] = "999"
        with self.assertRaisesRegex(ValueError, "arrival"):
            self.evaluate()

    def test_partial_retained_capture_reports_gap_against_summary(self):
        self.records.append({"schema": subject.SCHEMA, "data_kind": "fixture", "kind": "export_summary",
                             "instance_id": "fixture-instance", "complete": True, "counters": {"written": 4}})
        report, _ = self.evaluate()
        self.assertEqual(3, report["losses"]["retained_record_gap"])

    def test_prediction_computed_after_arrival_is_not_forecast(self):
        self.records[0].update(recorded_at=self.ts(self.time+timedelta(seconds=120)),
                               prediction_valid_until=self.ts(self.time+timedelta(seconds=121)))
        report, rows = self.evaluate()
        self.assertEqual([], rows)
        self.assertEqual(1, report["losses"]["prediction_after_arrival"])

    def test_system_text_json_trimmed_fraction_preserves_dotnet_observation_hash(self):
        for key in ("timestamp_gps", "captured_at", "recorded_at", "prediction_valid_until"):
            self.records[0][key] = self.records[0][key].replace(".0000000", "")
        report, rows = self.evaluate()
        self.assertEqual(1, len(rows)); self.assertEqual(20, report["arrival_error"]["mae_seconds"])

    def test_header_reuses_existing_csharp_and_python_canonical_contract(self):
        source = Path(subject.__file__).resolve().parents[2]/"NoPonto/2-Application/Services/GPS/EtaDataset.cs"
        header = re.search(r'const string Cabecalho = "([^"]+)"', source.read_text(encoding="utf-8-sig"))[1].split(",")
        self.assertEqual(header, subject.canonical_header())

    def test_typed_csharp_manifest_timestamp_serialization_is_compatible(self):
        manifest = json.loads(self.manifest.read_text())
        for trip in manifest["collection"]["Trips"]:
            for k in ("Inicio", "Fim"):
                trip[k] = trip[k].replace(".0000000", "")
        self.manifest.write_text(json.dumps(manifest), encoding="utf-8")
        report, rows = subject.evaluate([self.predictions], self.csv, self.manifest, self.audit, fixtures=True)
        self.assertEqual(1, len(rows)); self.assertEqual(20, report["arrival_error"]["mae_seconds"])

    def test_finite_large_values_do_not_overflow_mean_or_median(self):
        result = subject.metrics([1e308, 1e308])
        self.assertEqual(1e308, result["mae_seconds"])
        self.assertEqual(1e308, result["median_absolute_error_seconds"])
        json.dumps(result, allow_nan=False)


if __name__ == "__main__":
    unittest.main()
