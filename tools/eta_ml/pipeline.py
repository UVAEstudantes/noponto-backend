"""ETA histórico offline 3B. Inputs auditados; nenhuma conexão de rede/banco."""
import csv
import hashlib
import json
import math
import pickle
import platform
from collections import Counter, defaultdict
from datetime import datetime, timezone, timedelta
from pathlib import Path
from statistics import median

import numpy as np
import scipy
import sklearn
from sklearn.compose import ColumnTransformer
from sklearn.ensemble import ExtraTreesRegressor
from sklearn.impute import SimpleImputer
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import OneHotEncoder

VERSION = "noponto-eta-gps-v1"
CUTOFF = "2026-10-07T14:53:59.225082Z"
CAT = ["modal", "linha_id", "codigo_linha", "sentido_id", "padrao_id", "versao_id",
       "ocorrencia_id", "parada_id", "topologia"]
NUM = ["posicao_gps", "posicao_destino", "distancia_metros", "velocidade_kmh",
       "velocidade_media_causal_kmh", "hora_dia", "dia_semana"]
FEATURES = CAT + NUM
HEADER = ["observacao_id", "viagem_id", "volta", "veiculo", "modal", "provedor",
          "timestamp_gps", "linha_id", "codigo_linha", "sentido_id", "padrao_id",
          "versao_id", "ocorrencia_id", "parada_id", "topologia", "posicao_gps",
          "posicao_destino", "distancia_metros", "velocidade_kmh",
          "velocidade_media_causal_kmh", "hora_dia", "dia_semana", "split",
          "timestamp_passagem", "label_segundos"]


def utc(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        raise ValueError("Timestamp sem timezone")
    return result.astimezone(timezone.utc)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")


def load(csv_path, manifest_path, config):
    manifest = json.loads(Path(manifest_path).read_text(encoding="utf-8-sig"))
    if manifest["dataset_version"] != VERSION or utc(manifest["cutoff_utc"]) != utc(CUTOFF):
        raise ValueError("Contrato/cutoff incompatível")
    if manifest["data_kind"] not in ("synthetic", "real") or manifest["dataset_sha256"] != sha(csv_path):
        raise ValueError("Tipo/hash de dataset inválido")
    if manifest["features"] != FEATURES:
        raise ValueError("Allowlist divergente")
    bounds = [utc(config[k]) for k in ("inicio", "fim_treino", "fim_validacao", "fim")]
    if not utc(CUTOFF) <= bounds[0] < bounds[1] < bounds[2] < bounds[3]:
        raise ValueError("Limites temporais inválidos")
    trips = {}
    for trip in manifest["trips"]:
        if trip["viagem_id"] in trips:
            raise ValueError("Viagem duplicada no manifesto")
        trips[trip["viagem_id"]] = trip
    rows, discarded, seen, splits = [], Counter(), set(), {}
    with open(csv_path, encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames != HEADER:
            raise ValueError("Cabeçalho diferente do contrato 3A")
        for row in reader:
            trip = trips[row["viagem_id"]]
            start, end = utc(trip["inicio"]), utc(trip["fim"])
            if end <= start:
                raise ValueError("Fronteiras de viagem inválidas")
            if start < bounds[0] or end >= bounds[3]:
                discarded["trip_outside_window"] += 1
                continue
            if any(start < cut <= end for cut in bounds[1:3]):
                discarded["trip_crosses_split"] += 1
                continue
            split = "TRAIN" if end < bounds[1] else "VALIDATION" if end < bounds[2] else "TEST"
            if row["split"] != split or splits.setdefault(row["viagem_id"], split) != split:
                raise ValueError("Split inconsistente para viagem completa")
            key = (row["observacao_id"], row["ocorrencia_id"])
            if key in seen:
                raise ValueError("Observação/alvo duplicado")
            seen.add(key)
            gps, passage = utc(row["timestamp_gps"]), utc(row["timestamp_passagem"])
            label = float(row["label_segundos"])
            if not start <= gps < passage <= end or not 0 < label <= 3600 or abs((passage-gps).total_seconds()-label) > 1e-5:
                raise ValueError("Label/tempo inválido")
            for key in NUM:
                row[key] = float(row[key]) if row[key] else float("nan")
                if key.startswith("velocidade"):
                    if not math.isfinite(row[key]) or not 0 <= row[key] <= 160:
                        row[key] = float("nan")
                elif not math.isfinite(row[key]):
                    raise ValueError("Feature numérica inválida")
            if not 0 <= row["posicao_gps"] < row["posicao_destino"] <= 1 or row["distancia_metros"] <= 0:
                raise ValueError("Geometria/distância inválida")
            local = gps.astimezone(timezone(timedelta(hours=-3)))
            if row["hora_dia"] != local.hour or row["dia_semana"] != (local.weekday()+1) % 7:
                raise ValueError("Hora/dia não causais")
            if row["modal"] not in ("ONIBUS", "BRT") or any(not row[k] for k in CAT):
                raise ValueError("Categoria obrigatória ausente")
            row["label_segundos"] = label
            rows.append(row)
            if len(rows) > 200000:
                raise ValueError("MVP limitado a 200000 linhas; escolher janela menor offline")
    if any(not any(r["split"] == s for r in rows) for s in ("TRAIN", "VALIDATION", "TEST")):
        raise ValueError("Cada split precisa de viagens elegíveis")
    return rows, manifest, dict(discarded)


def matrix(rows):
    # Seleção explícita: nunca usar todas as colunas do CSV.
    return np.array([[r[k] for k in FEATURES] for r in rows], dtype=object)


def distance_band(row):
    d = row["distancia_metros"]
    return "0-250m" if d < 250 else "250-1000m" if d < 1000 else "1000m+"


def group(row):
    return (row["modal"], row["codigo_linha"], row["sentido_id"], row["versao_id"],
            row["ocorrencia_id"], distance_band(row), int(row["hora_dia"]) // 4)


class Baselines:
    def fit(self, train):
        values = defaultdict(list)
        for r in train:
            values[group(r)].append(r["label_segundos"])
        self.groups = {k: median(v) for k, v in values.items() if len(v) >= 5}
        self.global_eta = median(r["label_segundos"] for r in train)
        # Mediana de velocidade efetiva somente TRAIN; fallback limitado e positivo.
        self.fallback_kmh = min(80, max(5, median(r["distancia_metros"] / r["label_segundos"] * 3.6 for r in train)))
        return self

    def predict(self, rows, kind):
        if kind == "historical":
            return np.array([self.groups.get(group(r), self.global_eta) for r in rows])
        result = []
        for r in rows:
            speed = next((r[k] for k in ("velocidade_media_causal_kmh", "velocidade_kmh")
                          if math.isfinite(r[k]) and 1 <= r[k] <= 160), self.fallback_kmh)
            result.append(r["distancia_metros"] * 3.6 / speed)
        return np.array(result)


def metrics(y, pred):
    pred = np.asarray(pred, dtype=float)
    valid = np.isfinite(pred) & (pred >= 0)
    errors = np.abs(np.asarray(y)[valid] - pred[valid])
    return {"rows": len(y), "predicted": int(valid.sum()), "coverage": float(valid.mean()) if len(y) else 0,
            "mae_seconds": float(errors.mean()) if len(errors) else None,
            "rmse_seconds": float(np.sqrt(np.mean(errors**2))) if len(errors) else None,
            "median_absolute_error_seconds": float(np.median(errors)) if len(errors) else None,
            "p90_absolute_error_seconds": float(np.quantile(errors, .9)) if len(errors) else None}


def evaluate(bundle, rows, minimum):
    report = {}
    for split in ("TRAIN", "VALIDATION", "TEST"):
        subset = [r for r in rows if r["split"] == split]
        y = [r["label_segundos"] for r in subset]
        predictions = {k: bundle["baselines"].predict(subset, k) for k in ("physical", "historical")}
        predictions["extra_trees"] = bundle["model"].predict(matrix(subset))
        report[split] = {}
        for name, pred in predictions.items():
            value = {"overall": metrics(y, pred), "breakdowns": {}}
            for dimension, getter in {"modal": lambda r:r["modal"], "linha": lambda r:r["codigo_linha"],
                                      "distance": distance_band, "hour": lambda r:str(int(r["hora_dia"]))}.items():
                groups = defaultdict(list)
                for i, r in enumerate(subset):
                    groups[getter(r)].append(i)
                value["breakdowns"][dimension] = {
                    key: metrics(np.array(y)[idx], pred[idx]) for key, idx in sorted(groups.items())
                    if len(idx) >= minimum and len({subset[i]["viagem_id"] for i in idx}) >= 3}
            report[split][name] = value
    return report


def train(csv_path, manifest_path, config_path, output):
    config = json.loads(Path(config_path).read_text(encoding="utf-8-sig"))
    rows, dataset, discarded = load(csv_path, manifest_path, config)
    readiness = volume_report(rows)
    if dataset["data_kind"] == "real" and not readiness["ready"]:
        raise ValueError("Volume real insuficiente: " + "; ".join(readiness["failures"]))
    train_rows = [r for r in rows if r["split"] == "TRAIN"]
    pre = ColumnTransformer([
        ("categories", OneHotEncoder(handle_unknown="ignore", min_frequency=5, sparse_output=True), list(range(len(CAT)))),
        ("numbers", SimpleImputer(strategy="median", add_indicator=True, keep_empty_features=True), list(range(len(CAT), len(FEATURES))))])
    model = Pipeline([("features", pre), ("regressor", ExtraTreesRegressor(
        n_estimators=64, max_depth=18, max_leaf_nodes=4096, min_samples_leaf=3, random_state=config["seed"], n_jobs=1))])
    model.fit(matrix(train_rows), [r["label_segundos"] for r in train_rows])
    bundle = {"model": model, "baselines": Baselines().fit(train_rows), "features": FEATURES,
              "dataset_version": VERSION, "training_dataset_sha256": dataset["dataset_sha256"]}
    output = Path(output)
    output.mkdir(parents=True, exist_ok=False)
    artifact = output / "model.pkl"
    artifact.write_bytes(pickle.dumps(bundle, protocol=5))
    report = evaluate(bundle, rows, config["minimum_breakdown_rows"])
    write_json(output / "metrics.json", report)
    counts = {s: {"rows": sum(r["split"] == s for r in rows),
                  "trips": len({r["viagem_id"] for r in rows if r["split"] == s}),
                  "lines": len({r["codigo_linha"] for r in rows if r["split"] == s}),
                  "first_gps": min(r["timestamp_gps"] for r in rows if r["split"] == s),
                  "last_gps": max(r["timestamp_gps"] for r in rows if r["split"] == s)} for s in report}
    trip_bounds = {t["viagem_id"]: t for t in dataset["trips"]}
    for split in counts:
        ids = {r["viagem_id"] for r in rows if r["split"] == split}
        counts[split]["first_trip_start_utc"] = min(utc(trip_bounds[i]["inicio"]) for i in ids).isoformat()
        counts[split]["last_trip_end_utc"] = max(utc(trip_bounds[i]["fim"]) for i in ids).isoformat()
    manifest = {"pipeline_version": "eta-historical-3b-v1", "dataset_version": VERSION,
                "data_kind": dataset["data_kind"], "real_performance_claim": False,
                "cutoff_utc": CUTOFF, "feature_list": FEATURES, "config": config, "splits": counts,
                "collection": dataset.get("collection"),
                "discarded_rows": discarded, "real_volume_readiness": readiness,
                "model": "ExtraTreesRegressor", "hyperparameters": model[-1].get_params(),
                "seed": config["seed"], "versions": {"python": platform.python_version(), "sklearn": sklearn.__version__,
                "numpy": np.__version__, "scipy": scipy.__version__}, "artifact_sha256": sha(artifact),
                "dataset_sha256": dataset["dataset_sha256"], "dataset_manifest_sha256": sha(manifest_path),
                "config_sha256": sha(config_path), "source_sha256": sha(__file__), "metrics": report,
                "coverage_definition": "predições válidas / linhas elegíveis avaliadas; não coverage da coleta",
                "training_only_statistics": {"physical_fallback_kmh": bundle["baselines"].fallback_kmh,
                                             "historical_fallback_seconds": bundle["baselines"].global_eta}}
    write_json(output / "model.manifest.json", manifest)
    return manifest


def volume_report(rows):
    """Gate inicial de engenharia, não prova de representatividade/desempenho."""
    failures = []
    if len(rows) < 10000:
        failures.append("menos de 10000 linhas elegíveis")
    dates = {utc(r["timestamp_gps"]).date() for r in rows}
    if len(dates) < 14:
        failures.append("menos de 14 dias UTC distintos")
    counts = {}
    for split, minimum in (("TRAIN", 200), ("VALIDATION", 50), ("TEST", 50)):
        selected = [r for r in rows if r["split"] == split]
        count = len({r["viagem_id"] for r in selected})
        counts[split] = count
        if count < minimum:
            failures.append(f"{split}: menos de {minimum} viagens completas")
    lines = {}
    for line in sorted({r["codigo_linha"] for r in rows}):
        line_counts = {s: len({r["viagem_id"] for r in rows if r["codigo_linha"] == line and r["split"] == s}) for s in counts}
        hours = len({r["hora_dia"] for r in rows if r["codigo_linha"] == line and r["split"] == "TRAIN"})
        ready = line_counts["TRAIN"] >= 30 and line_counts["VALIDATION"] >= 10 and line_counts["TEST"] >= 10 and hours >= 8
        lines[line] = {"trips": line_counts, "train_distinct_hours": hours, "ready_for_line_claim": ready}
    if not any(v["ready_for_line_claim"] for v in lines.values()):
        failures.append("nenhuma linha com 30/10/10 viagens e 8 horas de TRAIN")
    return {"ready": not failures, "failures": failures, "rows": len(rows), "days": len(dates), "trips": counts, "lines": lines}
