"""Synthetic-only TCC lab. Reuses the existing trainer, baseline and HTTP service.
Never connects to GPS, Postgres, Redis or a non-loopback inference endpoint.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import pickle
import socket
import subprocess
import sys
import time
from datetime import datetime, timezone

HERE = Path(__file__).resolve().parent
BACKEND = HERE.parents[2]
IDENTITY = ("observacao_id", "viagem_id", "volta", "modal", "codigo_linha", "linha_id",
            "sentido_id", "padrao_id", "versao_id", "ocorrencia_id", "parada_id", "timestamp_gps")
METHODS = ("physical", "historical", "extra_trees")


def key(row):
    return tuple(str(row[k]) for k in IDENTITY)


def physical(row):
    """Formula/instantaneous-speed policy of PhysicalEta, NOT its matching proof.
    Synthetic rows assume a valid forward causal matching pair. The actual C#
    proof/eligibility implementation is exercised separately by ShadowTccTests.
    """
    instantaneous = row["velocidade_kmh"]
    if not math.isfinite(instantaneous) or not 1 <= instantaneous <= 160:
        return None
    mean = row["velocidade_media_causal_kmh"]
    speed = mean if math.isfinite(mean) and 1 <= mean <= 160 else instantaneous
    return row["distancia_metros"] * 3.6 / speed


def join(predictions, labels):
    """Exact structural AND causal join; no nearest-time/vehicle heuristic."""
    index = {}
    for label in labels:
        if label.get("data_kind") != "synthetic":
            raise ValueError("This demonstration cannot certify real labels")
        k = key(label)
        if k in index:
            raise ValueError("Duplicate label")
        index[k] = label
    seen, result = set(), []
    for prediction in predictions:
        k = key(prediction)
        if k in seen:
            raise ValueError("Duplicate prediction")
        seen.add(k)
        if k not in index:
            raise ValueError("Unmatched causal/structural identity")
        result.append((prediction, index[k]))
    return result


def report(pairs, metrics):
    def evaluate(selected):
        result = {"observations": len(selected),
                  "trips": len({p["viagem_id"] for p, _ in selected}),
                  "occurrences": len({(p["versao_id"], p["ocorrencia_id"]) for p, _ in selected})}
        for method in METHODS:
            y = [l["label_segundos"] for _, l in selected]
            values = [p["predictions"][method] if p["predictions"][method] is not None else float("nan") for p, _ in selected]
            m = metrics(y, values)
            errors = [abs(a-b) for a,b in zip(y, values) if math.isfinite(b)]
            m["within_60_seconds"] = sum(e <= 60 for e in errors)/len(errors) if errors else None
            m["within_120_seconds"] = sum(e <= 120 for e in errors)/len(errors) if errors else None
            # Existing metrics keys remain authoritative; expose minutes explicitly.
            m["minutes"] = {k.replace("_seconds", "_minutes"): v/60 for k,v in m.items() if k in ("mae_seconds", "median_absolute_error_seconds", "p90_absolute_error_seconds") and v is not None}
            result[method] = m
        return result
    common = [(p,l) for p,l in pairs if all(p["predictions"][m] is not None for m in METHODS)]
    slices = {}
    # Horizon is based on deterministic physical prediction, not future arrival.
    for name, low, high in (("0_120",0,120), ("120_300",120,300), ("300_3600",300,3600)):
        part = [(p,l) for p,l in common if low <= p["predictions"]["physical"] < high]
        if len(part) >= 30 and len({p["viagem_id"] for p,_ in part}) >= 3:
            slices[name] = evaluate(part)
    breakdowns = {}
    for field in ("modal", "codigo_linha"):
        breakdowns[field] = {}
        for value in sorted({p[field] for p,_ in common}):
            part = [(p,l) for p,l in common if p[field] == value]
            if len(part) >= 30 and len({p["viagem_id"] for p,_ in part}) >= 3:
                breakdowns[field][value] = evaluate(part)
    differences = {}
    for method in ("historical", "extra_trees"):
        values = [abs(p["predictions"][method]-p["predictions"]["physical"]) for p,_ in common]
        differences[method + "_vs_physical_mean_absolute_seconds"] = sum(values)/len(values) if values else None
    return {"individual_coverage": evaluate(pairs), "common_sample": evaluate(common),
            "prediction_disagreement_NOT_arrival_error": differences,
            "horizon_seconds_common": slices, "breakdowns_common": breakdowns}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ml-root", required=True, type=Path)
    parser.add_argument("--shadow-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    ml, shadow, output = args.ml_root.resolve(), args.shadow_root.resolve(), args.output.resolve()
    # Never overwrite previous evidence or run against externally supplied real data.
    output.mkdir(parents=True, exist_ok=False)
    sys.path[:0] = [str(ml / "tools/eta_ml"), str(ml)]
    import pipeline
    import synthetic
    import httpx
    synthetic.generate(output / "synthetic")
    csv = output / "synthetic/dataset.csv"
    manifest = output / "synthetic/dataset.manifest.json"
    config_path = ml / "tools/eta_ml/config.synthetic.json"
    config = json.loads(config_path.read_text(encoding="utf-8"))
    first = pipeline.train(csv, manifest, config_path, output / "model")
    second = pipeline.train(csv, manifest, config_path, output / "repeat-model")
    rows, _, _ = pipeline.load(csv, manifest, config)
    assert len(rows) <= 200000
    bundle = pickle.loads((output / "model/model.pkl").read_bytes())  # Only self-generated trusted fixture.
    repeat = pickle.loads((output / "repeat-model/model.pkl").read_bytes())
    import numpy as np
    if not np.array_equal(bundle["model"].predict(pipeline.matrix(rows)), repeat["model"].predict(pipeline.matrix(rows))):
        raise AssertionError("Non-reproducible predictions")
    splits = {s: {r["viagem_id"] for r in rows if r["split"] == s} for s in ("TRAIN","VALIDATION","TEST")}
    assert not splits["TRAIN"] & splits["VALIDATION"] and not splits["TRAIN"] & splits["TEST"] and not splits["VALIDATION"] & splits["TEST"]
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1",0))
        port = reserved.getsockname()[1]
    url = f"http://127.0.0.1:{port}"
    env = os.environ.copy()
    env.update(ETA_MODEL=str(output / "model/model.pkl"), ETA_MODEL_MANIFEST=str(output / "model/model.manifest.json"),
               ETA_ALLOW_SYNTHETIC="1", PYTHONPATH=os.pathsep.join([str(ml / "tools/eta_ml"),str(ml)]))
    predictions, labels = [], []
    with (output / "fastapi.log").open("w",encoding="utf-8") as log:
        server = subprocess.Popen([sys.executable,"-m","uvicorn","eta_history_service:app","--host","127.0.0.1","--port",str(port),"--no-access-log"],
                                  cwd=ml,env=env,stdout=log,stderr=subprocess.STDOUT)
        try:
            with httpx.Client(base_url=url,timeout=10,trust_env=False) as client:
                ready = False
                for _ in range(100):
                    if server.poll() is not None:
                        raise RuntimeError("FastAPI stopped; see private local fastapi.log")
                    try:
                        ready = client.get("/ready").status_code == 200
                        if ready: break
                    except httpx.TransportError: pass
                    time.sleep(.1)
                if not ready: raise TimeoutError("FastAPI readiness")
                for offset in range(0,len(rows),200):
                    batch = rows[offset:offset+200]
                    features = [{k:(None if isinstance(r[k],float) and not math.isfinite(r[k]) else r[k]) for k in pipeline.FEATURES} for r in batch]
                    # Correlation is echoed by the existing service, validated before retaining.
                    for i,f in enumerate(features):
                        f.update(shadow_contract="noponto-eta-shadow-history-v1",shadow_request_id=str(offset+i))
                    started = time.monotonic()
                    response = client.post("/eta/batch",json=features)
                    duration_ms = (time.monotonic()-started)*1000
                    response.raise_for_status()
                    if response.headers["X-Model-Version"] != first["artifact_sha256"] or response.headers["X-Model-Data-Kind"] != "synthetic":
                        raise ValueError("Wrong model identity")
                    result = response.json()
                    assert len(result) == len(batch)
                    historical = bundle["baselines"].predict(batch,"historical")
                    for i,(r,pred,hist) in enumerate(zip(batch,result,historical)):
                        assert pred["shadow_request_id"] == str(offset+i)
                        identity = {k:r[k] for k in IDENTITY}
                        p = dict(identity, schema="noponto-tcc-synthetic-prediction-v1",data_kind="synthetic", split=r["split"],
                                 predicted_at=r["timestamp_gps"], recorded_at=datetime.now(timezone.utc).isoformat(),
                                 model_sha256=first["artifact_sha256"], baseline_version="existing-TRAIN-group-median-v1",
                                 physical_version="physical-causal-formula-no-train-fallback-v1",
                                 horizon_seconds=physical(r), distance_metres=r["distancia_metros"],
                                 ml_status="success", batch_inference_duration_ms=duration_ms,
                                 predictions={"physical":physical(r),"historical":float(hist),"extra_trees":pred["eta_segundos"]})
                        predictions.append(p)
                        labels.append(dict(identity,data_kind="synthetic",timestamp_passagem=r["timestamp_passagem"],label_segundos=r["label_segundos"]))
                invalid = dict(features[0],posicao_destino=0)
                assert client.post("/eta/batch",json=[invalid]).status_code == 422
            bridge_env = os.environ.copy()
            bridge_env.update(TCC_ETA_URL=url,TCC_BRIDGE_RECORD=str(output / "csharp-bridge.json"))
            command = ["dotnet","test",str(BACKEND / "NoPonto/NoPonto.csproj"),"--no-restore",
                       f"-p:CustomAfterMicrosoftCommonTargets={HERE / 'Lab.targets'}",f"-p:ShadowRoot={shadow}",
                       f"-p:OutputPath={output / 'dotnet-bin'}/", "--filter",
                       "FullyQualifiedName~ShadowTccTests|FullyQualifiedName~HistoricalEtaShadowTests|FullyQualifiedName~PhysicalEtaMvpTests|FullyQualifiedName~PhysicalEtaSelectedStopTests",
                       "--logger",f"trx;LogFileName=tcc.trx","--results-directory",str(output / "test-results")]
            with (output / "dotnet.log").open("w",encoding="utf-8") as testlog:
                subprocess.run(command,cwd=BACKEND,env=bridge_env,stdout=testlog,stderr=subprocess.STDOUT,timeout=240,check=True)
            bridge = json.loads((output / "csharp-bridge.json").read_text(encoding="utf-8"))
            bridge["historical_seconds"] = float(bundle["baselines"].predict([bridge["features"]],"historical")[0])
            pipeline.write_json(output / "csharp-bridge.json",bridge)
        finally:
            server.terminate()
            try: server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill(); server.wait(timeout=5)
    pairs = join(predictions,labels)
    for name,values in (("predictions.jsonl",predictions),("synthetic-labels.jsonl",labels)):
        with (output / name).open("w",encoding="utf-8") as stream:
            for value in values: stream.write(json.dumps(value,allow_nan=False)+"\n")
    evaluation = {s:report([(p,l) for p,l in pairs if p["split"] == s],pipeline.metrics) for s in splits}
    pipeline.write_json(output / "comparison.json",evaluation)
    source_paths = [Path(pipeline.__file__),Path(synthetic.__file__),ml / "eta_history_service.py",Path(__file__),HERE / "ShadowTccTests.cs",HERE / "Lab.targets",
                    BACKEND / "NoPonto/2-Application/Services/GPS/PhysicalEta.cs"]
    source_paths += list((shadow / "NoPonto/2-Application/Services/GPS").glob("HistoricalEta*.cs"))
    # Include dirty/new application sources, but never walk ignored lab contexts,
    # obj/bin or unrelated retained experiments when computing provenance.
    names = subprocess.check_output(["git","-C",str(BACKEND),"ls-files","-z","--cached","--others","--exclude-standard","--","NoPonto"],text=True).split("\0")
    source_paths += [BACKEND / name for name in names if name.endswith(".cs") and (BACKEND / name).is_file()]
    pipeline.write_json(output / "experiment.json",{
        "data_kind":"synthetic","real_performance_claim":False,"production_capture_enabled":False,
        "model_sha256":first["artifact_sha256"],"repeat_model_sha256":second["artifact_sha256"],"identical_predictions":True,
        "versions":first["versions"],"seed":config["seed"],"splits":first["splits"],
        "features":pipeline.FEATURES,"config":config,"training_only_statistics":first["training_only_statistics"],
        "source_sha256":{str(p):pipeline.sha(p) for p in source_paths},
        "real_data_readiness":{"ready":False,"reason":"No independently audited eligible real dataset supplied; synthetic fixtures do not qualify real training"},
        "qualification_example_only":pipeline.volume_report(rows),
        "limits":{"max_rows":200000,"http_batch":200,"http_timeout_seconds":10,"dotnet_timeout_seconds":240,"server_shutdown_seconds":15},
        "physical_policy":"No TRAIN speed fallback; require instantaneous 1..160. Forward matching assumed for generated rows, verified separately in C#.",
        "shadow_gate":"Existing worker must reject synthetic responses; lab adapter HTTP inference is not production worker acceptance.",
        "sources":{str(root):{"revision":subprocess.check_output(["git","-C",str(root),"rev-parse","HEAD"],text=True).strip(),
                              "dirty_paths":subprocess.check_output(["git","-C",str(root),"status","--short"],text=True).splitlines()}
                   for root in (BACKEND,ml,shadow)},
        "artifact_sha256":{str(p.relative_to(output)):pipeline.sha(p) for p in output.rglob("*") if p.is_file() and p.suffix in (".json",".jsonl",".csv",".trx")}})
    print(json.dumps({"output":str(output),"synthetic_only":True,"observations":len(rows),"model_sha256":first["artifact_sha256"]}))


if __name__ == "__main__":
    main()
