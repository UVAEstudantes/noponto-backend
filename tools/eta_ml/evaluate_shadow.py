"""4D: evaluate retained Shadow results against externally audited canonical labels.

Standard library only. Does not load a model, train, certify data, access DB/network,
or change qualification gates. Audit authenticity remains the operator's responsibility.
"""
import argparse
import ast
import csv
import hashlib
import json
import math
import re
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from statistics import mean
from uuid import UUID

SCHEMA = "noponto-eta-shadow-offline-v2"
CONTRACT = "noponto-eta-shadow-history-v1"
MAX_ROWS = 200_000
MAX_BYTES = 256 * 1024 * 1024
STATUSES = {"success", "expired", "queue_full", "circuit_open", "stopped", "geometry_refused",
            "model_unavailable", "http_failure", "timeout", "failure"}
IDENTITY = ("observation_id", "trip_id", "modal", "line_code", "line_id", "direction_id",
            "pattern_id", "version_id", "target_id", "stop_id", "lap", "timestamp_gps")
CSV_NAMES = ("observacao_id", "viagem_id", "modal", "codigo_linha", "linha_id", "sentido_id",
             "padrao_id", "versao_id", "ocorrencia_id", "parada_id", "volta", "timestamp_gps")


def canonical_header():
    # Reuse the existing literal contract without importing numpy/sklearn/training.
    tree = ast.parse(Path(__file__).with_name("pipeline.py").read_text(encoding="utf-8-sig"))
    return next(ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign)
                and any(isinstance(t, ast.Name) and t.id == "HEADER" for t in n.targets))


def sha(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(65536), b""):
            h.update(block)
    return h.hexdigest()


def document(path):
    if Path(path).stat().st_size > 2 * 1024 * 1024:
        raise ValueError("Metadata exceeds offline limit")
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def instant(value):
    # Preserve .NET's seventh fractional digit; identity is not a time-neighbour join.
    match = re.fullmatch(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?(Z|[+-]\d\d:\d\d)", value)
    if not match:
        raise ValueError("Invalid timezone/precision")
    base = datetime.fromisoformat(match[1] + match[3].replace("Z", "+00:00")).astimezone(timezone.utc)
    ticks = int((match[2] or "").ljust(7, "0"))
    epoch = datetime(1970, 1, 1, tzinfo=timezone.utc)
    delta = base - epoch
    total = (delta.days * 86400 + delta.seconds) * 10_000_000 + ticks
    dotnet = base.strftime("%Y-%m-%dT%H:%M:%S") + f".{ticks:07d}+00:00"
    return total, dotnet


def guid(value):
    result = str(UUID(value))
    if UUID(result).int == 0:
        raise ValueError("Empty structural identity")
    return result


def finite(value):
    number = float(value)
    if not math.isfinite(number):
        raise ValueError("Non-finite number")
    return number


def identity(row, names):
    values = [row[k] for k in names]
    for i in (1, 4, 5, 6, 7, 8, 9):
        values[i] = guid(values[i])
    values[10] = int(values[10])
    if values[10] < 0 or values[2] not in ("ONIBUS", "BRT"):
        raise ValueError("Invalid lap/modal")
    values[11] = instant(values[11])[0]
    if not re.fullmatch(r"[a-f0-9]{64}", values[0]):
        raise ValueError("Invalid observation identity")
    return tuple(values)


def collection_identity(value, exporter):
    # The C# exporter serializes typed timestamps/UUIDs and default optional fields.
    # Normalize representation, never use a temporal tolerance or infer an association.
    value = dict(value)
    value.setdefault("DataKind", "real")
    if exporter == "eta-export-3g2-v2":
        value.setdefault("CollectionProfile", None)
    if "CollectionStartedAtUtc" in value:
        value["CollectionStartedAtUtc"] = instant(value["CollectionStartedAtUtc"])[0]
    value["Trips"] = [{**t, "ViagemId": guid(t["ViagemId"]), "Inicio": instant(t["Inicio"])[0],
                       "Fim": instant(t["Fim"])[0]} for t in value["Trips"]]
    return value


def labels(csv_path, manifest_path, audit_path, fixtures):
    manifest, audit = document(manifest_path), document(audit_path)
    exporter = manifest.get("exporter_version")
    kind = "synthetic" if fixtures else "real"
    if (manifest.get("dataset_version") != "noponto-eta-gps-v1"
            or audit.get("DatasetVersion") != manifest["dataset_version"]
            or manifest.get("data_kind") != kind or audit.get("DataKind", "real") != kind
            or manifest.get("dataset_sha256") != sha(csv_path) or manifest.get("audit_sha256") != sha(audit_path)
            or collection_identity(manifest["collection"], exporter) != collection_identity(audit, exporter)):
        raise ValueError("Canonical dataset/audit kind, binding or hash mismatch")
    if exporter not in ("eta-export-3b-v1", "eta-export-3g2-v2"):
        raise ValueError("Unsupported canonical exporter")
    if exporter == "eta-export-3g2-v2" and manifest.get("validation_policy") != "eta-route-versioned-3g2-v2":
        raise ValueError("Canonical validation policy mismatch")
    if not audit.get("SnapshotReference") or audit["SnapshotReference"].startswith("PREENCHER"):
        raise ValueError("Missing externally audited snapshot")
    trips = {}
    for trip in audit["Trips"]:
        key = guid(trip["ViagemId"])
        if (key in trips or trip.get("Qualidade") != "AuditadaSemProtecao"
                or not trip.get("ReferenciaAuditoria", "").strip()
                or trip["ReferenciaAuditoria"].startswith("PREENCHER")):
            raise ValueError("Unaudited/protected/duplicate trip; no automatic certification")
        start, end = instant(trip["Inicio"])[0], instant(trip["Fim"])[0]
        if start >= end:
            raise ValueError("Invalid audited trip boundaries")
        trips[key] = (start, end, trip["CodigoLinha"])
    bound_trips = {}
    for trip in manifest["trips"]:
        key = guid(trip["viagem_id"])
        if key in bound_trips:
            raise ValueError("Duplicate manifest trip")
        bound_trips[key] = (instant(trip["inicio"])[0], instant(trip["fim"])[0])
    if bound_trips != {k: v[:2] for k, v in trips.items()}:
        raise ValueError("Trip manifest/audit mismatch")
    if Path(csv_path).stat().st_size > MAX_BYTES:
        raise ValueError("Dataset exceeds offline limit")
    result, observations = {}, defaultdict(list)
    seen = set()
    with Path(csv_path).open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames != canonical_header():
            raise ValueError("Canonical header mismatch")
        for row in reader:
            key = identity(row, CSV_NAMES)
            duplicate = (key[0], key[8])
            if duplicate in seen or len(seen) >= MAX_ROWS:
                raise ValueError("Duplicate canonical label or row limit exceeded")
            seen.add(duplicate)
            start, end, line = trips[key[1]]
            passage = instant(row["timestamp_passagem"])[0]
            seconds = finite(row["label_segundos"])
            observation = f'{row["modal"].strip().upper()}|{row["provedor"].strip().upper()}|{row["veiculo"].strip().upper()}|{instant(row["timestamp_gps"])[1]}'
            if (hashlib.sha256(observation.encode()).hexdigest() != key[0]
                    or key[3] != line or not start <= key[11] < passage <= end
                    or not 0 < seconds <= 3600 or abs((passage-key[11])/10_000_000-seconds) > 1e-5):
                raise ValueError("Canonical label identity/arrival/boundaries invalid")
            result[key] = seconds
            observations[key[0]].append(key)
    return result, observations


def predictions(paths, fixtures):
    if not 1 <= len(paths) <= 256 or sum(Path(p).stat().st_size for p in paths) > MAX_BYTES:
        raise ValueError("Prediction file count/size exceeded")
    rows, losses, summaries, instances = [], Counter(), {}, set()
    for path in paths:
        with Path(path).open("rb") as stream:
            while line := stream.readline(8193):
                if len(line) > 8192:
                    raise ValueError("Prediction line exceeds offline limit")
                if not line.endswith(b"\n"):
                    losses["truncated_tail"] += 1
                    continue
                try:
                    row = json.loads(line)
                    if row.get("schema") != SCHEMA or row.get("data_kind") != ("fixture" if fixtures else "real"):
                        raise ValueError("Prediction schema/data kind mismatch")
                    if row.get("kind") == "export_summary":
                        if row["instance_id"] in summaries:
                            raise ValueError("Duplicate export summary")
                        summaries[row["instance_id"]] = row
                        continue
                    instances.add(row["instance_id"])
                    if len(rows) >= MAX_ROWS:
                        raise ValueError("Prediction row limit exceeded")
                    if not set(IDENTITY).union({"request_id", "model_digest", "status", "captured_at", "recorded_at",
                                                "prediction_valid_until", "ml_eta", "public_eta", "prediction_duration_ms"}) <= row.keys():
                        losses["invalid_record"] += 1
                        continue
                    rows.append(row)
                except (UnicodeDecodeError, json.JSONDecodeError):
                    losses["malformed_record"] += 1
    known = Counter()
    for row in summaries.values():
        for k, v in row["counters"].items():
            if not isinstance(v, int) or v < 0:
                raise ValueError("Invalid export counters")
            known[k] += v
    complete = bool(instances) and instances <= summaries.keys() and all(r.get("complete") is True for r in summaries.values())
    # Even a clean terminal summary describes this capture only, never all GPS traffic.
    losses["retained_record_gap"] += max(0, known["written"]-len(rows))
    return rows, losses, dict(known), complete


def metrics(values):
    values = sorted(values)
    if not values:
        return {"n": 0, "mae_seconds": None, "median_absolute_error_seconds": None,
                "p90_absolute_error_seconds": None, "within_60_percent": None, "within_120_percent": None}
    return {"n": len(values), "mae_seconds": mean(values),
            "median_absolute_error_seconds": mean(values[(len(values)-1)//2:len(values)//2+1]),
            "p90_absolute_error_seconds": values[math.ceil(.9*len(values))-1],
            "within_60_percent": 100*sum(v <= 60 for v in values)/len(values),
            "within_120_percent": 100*sum(v <= 120 for v in values)/len(values)}


def source_statistics(rows, trusted, minimum_rows, minimum_trips):
    errors = defaultdict(list)
    groups = defaultdict(list)
    pairs = defaultdict(list)
    triples = defaultdict(list)
    losses = Counter()
    by_request = defaultdict(list)
    for row in rows:
        by_request[row.get("request_id")].append(row)
    for request, candidates in by_request.items():
        if len(candidates) != 1:
            losses["duplicate_request"] += len(candidates)
            continue
        row = candidates[0]
        try:
            key = identity(row, IDENTITY)
            expected = hashlib.sha256(f'{CONTRACT}|{key[0]}|{key[1]}|{key[8]}|{key[7]}|{key[10]}'.encode()).hexdigest()
            if request != expected or key not in trusted:
                losses["missing_or_incompatible_audited_label"] += 1
                continue
            gps = key[11]
            captured, recorded, expiry = (instant(row[k])[0] for k in ("captured_at", "recorded_at", "prediction_valid_until"))
            label = trusted[key]
            if not gps <= captured <= recorded <= expiry or not 0 < expiry-gps <= 300*10_000_000 or recorded >= gps+round(label*10_000_000):
                losses["noncausal_expired_or_after_arrival"] += 1
                continue
            source = {}
            if row.get("physical_status") == "success" and row.get("public_eta") is not None:
                source["physical:causal-v1"] = finite(row["public_eta"])
            for name, status, field, digest in (("ml", row.get("status"), "ml_eta", row.get("model_digest")),
                                                ("historical", row.get("historical_status"), "historical_eta", row.get("historical_digest"))):
                if status != "success":
                    losses[name+"_unavailable"] += 1
                    continue
                if (not re.fullmatch(r"[a-fA-F0-9]{64}", digest or "") or not row.get("training_end_utc")
                        or instant(row["training_end_utc"])[0] >= gps):
                    losses[name+"_untrusted_or_temporal_leakage"] += 1
                    continue
                source[name+":"+digest.lower()] = finite(row[field])
            losses["invalid_prediction"] += sum(v < 0 for v in source.values())
            source = {k:v for k,v in source.items() if v >= 0}
            horizon = "0-60s" if label <= 60 else "60-300s" if label <= 300 else "300s+"
            for model, value in source.items():
                if value < 0:
                    losses["invalid_prediction"] += 1
                    continue
                error = value-label
                errors[model].append(error)
                for field, value_group in (("line_code",key[3]),("modal",key[2]),("actual_arrival_horizon",horizon)):
                    groups[(model,field,value_group)].append((error,key[1]))
            for other in source:
                if other.startswith(("historical:","ml:")) and "physical:causal-v1" in source:
                    pairs[other].append((source["physical:causal-v1"]-label,source[other]-label))
            ml_key = next((k for k in source if k.startswith("ml:")),None)
            historical_key = next((k for k in source if k.startswith("historical:")),None)
            if ml_key and historical_key and "physical:causal-v1" in source:
                triples[(ml_key,historical_key)].append((source["physical:causal-v1"]-label,source[historical_key]-label,source[ml_key]-label))
        except (KeyError, ValueError, TypeError, OverflowError):
            losses["invalid_identity_or_prediction"] += 1
    def stats(values):
        return {**metrics([abs(x) for x in values]),"bias_seconds":mean(values) if values else None}
    return {"scope":"retained_unique_requests_with_exact_audited_canonical_labels",
            "coverage_by_origin":{origin:{"available_and_audited":sum(len(v) for k,v in errors.items() if k.startswith(origin+":")),"retained_requests":len(by_request)} for origin in ("physical","historical","ml")},
            "by_source_model":{k:{**stats(v),"coverage_fraction_of_retained_requests":len(v)/len(by_request) if by_request else None} for k,v in sorted(errors.items())},
            "three_way_common":[{"ml_model":ml,"historical_model":history,"same_identity_rows":len(values),"physical":stats([p for p,_,_ in values]),"historical":stats([h for _,h,_ in values]),"ml":stats([m for _,_,m in values])} for (ml,history),values in sorted(triples.items())],
            "paired_with_physical":{k:{"same_identity_rows":len(v),"physical":stats([x for x,_ in v]),"candidate":stats([y for _,y in v])} for k,v in sorted(pairs.items())},
            "breakdowns":[{"source_model":model,"field":field,"value":value,**stats([e for e,_ in values])} for (model,field,value),values in sorted(groups.items()) if len(values)>=minimum_rows and len({trip for _,trip in values})>=minimum_trips],
            "losses":dict(losses)}


def evaluate(paths, csv_path, manifest_path, audit_path, fixtures=False, minimum_rows=30, minimum_trips=3):
    if not 1 <= minimum_rows <= MAX_ROWS or not 1 <= minimum_trips <= MAX_ROWS:
        raise ValueError("Invalid breakdown limits")
    trusted, by_observation = labels(csv_path, manifest_path, audit_path, fixtures)
    rows, losses, export_losses, complete = predictions(paths, fixtures)
    grouped = defaultdict(list)
    for row in rows:
        grouped[(row["request_id"], row["model_digest"])].append(row)
    evaluated, comparison, models, matched_labels = [], [], defaultdict(list), set()
    for group in grouped.values():
        row = group[0]
        # Copies of one outcome are counted once; conflicting outcomes are all rejected.
        stable = lambda r: {k: v for k, v in r.items() if k != "instance_id"}
        if any(stable(r) != stable(row) for r in group[1:]):
            losses["conflicting_duplicate"] += len(group)
            continue
        losses["duplicate_prediction"] += len(group)-1
        try:
            key = identity(row, IDENTITY)
            request = f"{CONTRACT}|{key[0]}|{key[1]}|{key[8]}|{key[7]}|{key[10]}"
            if hashlib.sha256(request.encode()).hexdigest() != row["request_id"]:
                raise ValueError("Request identity mismatch")
            if row["status"] not in STATUSES:
                raise ValueError("Unknown outcome status")
            if row["status"] != "success":
                losses["status." + row["status"]] += 1
                continue
            if not re.fullmatch(r"[a-fA-F0-9]{64}", row["model_digest"] or ""):
                raise ValueError("Untrusted model digest")
            captured, recorded, expiry = (instant(row[k])[0] for k in ("captured_at", "recorded_at", "prediction_valid_until"))
            if not key[11] <= captured <= recorded or not 0 < expiry-captured <= 300*10_000_000:
                raise ValueError("Non-causal prediction timestamps")
            if recorded > expiry:
                losses["expired_prediction"] += 1
                continue
            if not row.get("training_end_utc") or instant(row["training_end_utc"])[0] >= key[11]:
                losses["model_temporal_leakage"] += 1
                continue
            eta = finite(row["ml_eta"])
            if eta < 0 or finite(row["prediction_duration_ms"]) < 0:
                raise ValueError("Invalid prediction")
            if row["public_eta"] is not None:
                public = finite(row["public_eta"])
                if public < 0:
                    raise ValueError("Invalid public comparison")
                comparison.append(abs(eta-public))
            if key not in trusted:
                losses["identity_mismatch" if key[0] in by_observation else "missing_label"] += 1
                continue
            if recorded >= key[11] + int(round(trusted[key]*10_000_000)):
                losses["prediction_after_arrival"] += 1
                continue
            error = abs(eta-trusted[key])
            item = {k: row[k] for k in IDENTITY}
            item.update(request_id=row["request_id"], model_digest=row["model_digest"].lower(),
                        ml_eta=eta, label_seconds=trusted[key], absolute_error_seconds=error)
            evaluated.append(item)
            matched_labels.add(key)
            models[item["model_digest"]].append(error)
        except (KeyError, TypeError, ValueError, OverflowError):
            losses["invalid_prediction"] += 1
    breakdown = {}
    for field in ("line_code", "modal"):
        groups = defaultdict(list)
        for row in evaluated:
            groups[(row["model_digest"], row[field])].append(row)
        breakdown[field] = [{"model_digest": model, field: value,
                             **metrics([r["absolute_error_seconds"] for r in subset])}
                            for (model, value), subset in sorted(groups.items())
                            if len(subset) >= minimum_rows and len({r["trip_id"] for r in subset}) >= minimum_trips]
    report = {"schema": "noponto-eta-shadow-arrival-evaluation-v1", "data_kind": "fixture" if fixtures else "real",
              "audit_scope": "externally_audited_canonical_labels_no_certification_no_training_qualification",
              "coverage": {"raw_records": len(rows), "unique_prediction_keys": len(grouped), "evaluated": len(evaluated),
                           "evaluated_prediction_fraction": len(evaluated)/len(grouped) if grouped else None,
                           "canonical_labels": len(trusted), "matched_canonical_labels": len(matched_labels),
                           "canonical_label_fraction": len(matched_labels)/len(trusted) if trusted else None,
                           "denominator": "retained_request_id_and_model_digest_not_all_gps"},
              "losses": dict(sorted(losses.items())), "known_export_counters": export_losses,
              "all_export_instances_closed": complete,
              "capture_loss_counts_may_be_incomplete": not complete or any(losses[k] for k in
                  ("truncated_tail", "malformed_record", "invalid_record", "retained_record_gap")),
              "arrival_error": metrics([r["absolute_error_seconds"] for r in evaluated]),
              "arrival_error_scope": "pooled_diagnostic_use_by_model_for_model_quality",
              "arrival_error_by_model": {k: metrics(v) for k, v in sorted(models.items())},
              "breakdown_minimum_rows": minimum_rows, "breakdown_minimum_trips": minimum_trips, "breakdowns": breakdown,
              "prediction_comparison": {"kind": "prediction_difference_not_arrival_error", "n": len(comparison),
                                        "mean_absolute_difference_seconds": mean(comparison) if comparison else None}}
    report["arrival_sources"] = source_statistics(rows, trusted, minimum_rows, minimum_trips)
    return report, evaluated


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--predictions", nargs="+", required=True)
    for name in ("dataset", "manifest", "audit", "output"):
        parser.add_argument("--"+name, required=True)
    parser.add_argument("--fixtures", action="store_true", help="Explicitly synthetic fixtures; never real readiness")
    parser.add_argument("--minimum-breakdown-rows", type=int, default=30)
    parser.add_argument("--minimum-breakdown-trips", type=int, default=3)
    args = parser.parse_args()
    output = Path(args.output)
    if output.exists():
        raise ValueError("Output must be a new explicitly selected local directory")
    report, evaluated = evaluate(args.predictions, args.dataset, args.manifest, args.audit, args.fixtures,
                                 args.minimum_breakdown_rows, args.minimum_breakdown_trips)
    inputs = args.predictions + [args.dataset, args.manifest, args.audit]
    report["input_sha256"] = [{"file": Path(p).name, "sha256": sha(p)} for p in inputs]
    output.mkdir(parents=True)
    (output/"evaluation.json").write_text(json.dumps(report, indent=2, allow_nan=False)+"\n", encoding="utf-8")
    with (output/"evaluated.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(IDENTITY)+["request_id", "model_digest", "ml_eta", "label_seconds", "absolute_error_seconds"])
        writer.writeheader()
        writer.writerows(evaluated)


if __name__ == "__main__":
    main()
