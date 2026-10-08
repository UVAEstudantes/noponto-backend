import argparse
import json
import pickle
from pathlib import Path
from pipeline import FEATURES, VERSION, evaluate, load, sha, write_json
from collection_profiles import dataset_profile, instant
from validation_policies import policy

if __name__ == "__main__":
    p = argparse.ArgumentParser()
    for key in ("dataset", "manifest", "config", "model", "model-manifest", "output"):
        p.add_argument("--" + key, required=True)
    a = p.parse_args()
    config = json.loads(Path(a.config).read_text(encoding="utf-8-sig"))
    metadata = json.loads(Path(a.model_manifest).read_text(encoding="utf-8-sig"))
    if sha(a.model) != metadata["artifact_sha256"] or sha(a.config) != metadata["config_sha256"]:
        raise ValueError("Hash de artefato/config divergente")
    rows, dataset, discarded = load(a.dataset, a.manifest, config)
    profile_name, _ = dataset_profile(dataset, config)
    if policy(metadata, config) != policy(dataset, config):
        raise ValueError("Model/dataset validation policy mismatch")
    if (metadata["dataset_manifest_sha256"] != sha(a.manifest)
            or metadata.get("collection_profile", profile_name) != profile_name
            or instant(metadata["cutoff_utc"]) != instant(dataset["cutoff_utc"])
            or metadata.get("collection") != dataset.get("collection")
            or metadata["data_kind"] != dataset["data_kind"]):
        raise ValueError("Modelo/manifesto/config divergem do perfil ou dataset original")
    # Carregar somente artefatos locais confiáveis: pickle pode executar código.
    bundle = pickle.loads(Path(a.model).read_bytes())
    if bundle["features"] != FEATURES or bundle["dataset_version"] != VERSION:
        raise ValueError("Modelo incompatível")
    if bundle["training_dataset_sha256"] != dataset["dataset_sha256"]:
        raise ValueError("Esta avaliação reproduz apenas a divisão original")
    write_json(a.output, {"data_kind": dataset["data_kind"], "discarded": discarded,
                          "metrics": evaluate(bundle, rows, config["minimum_breakdown_rows"])})
