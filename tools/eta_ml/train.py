import argparse
from pipeline import train

if __name__ == "__main__":
    p = argparse.ArgumentParser()
    for key in ("dataset", "manifest", "config", "output"):
        p.add_argument("--" + key, required=True)
    a = p.parse_args()
    result = train(a.dataset, a.manifest, a.config, a.output)
    print(result["data_kind"], result["splits"], result["artifact_sha256"])
