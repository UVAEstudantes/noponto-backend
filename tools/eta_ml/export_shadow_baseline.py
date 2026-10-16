"""Export TRAIN-only group medians, reusing canonical audited labels; never train a model.
Requires a previously qualified real model manifest binding the SAME dataset.
No network/DB and no automatic approval. Synthetic artifacts are not accepted.
"""
import argparse
import csv
import json
from collections import defaultdict
from pathlib import Path
from statistics import median
import evaluate_shadow as audit


def export(dataset, manifest, audit_file, model_manifest, output):
    trusted, _ = audit.labels(dataset, manifest, audit_file, fixtures=False)
    model = audit.document(model_manifest)
    qualified = model.get("qualification") or {}
    if (model.get("data_kind") != "real" or model.get("dataset_sha256") != audit.sha(dataset)
            or model.get("real_volume_readiness", {}).get("ready") is not True
            or qualified.get("ready") is not True or qualified.get("schema") != "eta-real-qualification-3g1-v1"
            or model.get("validation_policy") != "eta-route-versioned-3g2-v2"):
        raise ValueError("Qualified real model for the same audited dataset required")
    cutoff = audit.instant(model["config"]["fim_treino"])[0]
    groups = defaultdict(list)
    with Path(dataset).open(encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream):
            identity = audit.identity(row, audit.CSV_NAMES)
            if row["split"] != "TRAIN":
                continue
            if identity not in trusted or audit.instant(row["timestamp_passagem"])[0] > cutoff:
                raise ValueError("Untrusted or temporally leaking TRAIN label")
            distance = audit.finite(row["distancia_metros"])
            if distance <= 0:
                raise ValueError("Invalid longitudinal distance")
            band = "0-250m" if distance < 250 else "250-1000m" if distance < 1000 else "1000m+"
            key = "|".join((row["modal"],row["codigo_linha"],identity[5],identity[7],identity[8],band,str(int(row["hora_dia"])//4)))
            groups[key].append(trusted[identity])
            if len(groups)>2000:
                raise ValueError("Historical group bound exceeded")
    value = {"schema":"noponto-historical-train-groups-v1","data_kind":"real", "dataset_sha256":audit.sha(dataset),
             "training_end_utc":model["config"]["fim_treino"], "groups":[{"key":k,"count":len(v),"median_seconds":median(v)} for k,v in sorted(groups.items()) if len(v)>=5]}
    data = json.dumps(value,allow_nan=False,indent=2).encode()
    if len(data)>512*1024:
        raise ValueError("Historical artifact bound exceeded")
    with Path(output).open("xb") as stream:stream.write(data)
    return audit.sha(output)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ("dataset","manifest","audit","model-manifest","output"):parser.add_argument("--"+key,required=True)
    args=parser.parse_args()
    print(export(args.dataset,args.manifest,args.audit,args.model_manifest,args.output))

if __name__=="__main__":main()
