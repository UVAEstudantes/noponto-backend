import argparse
import json
from pathlib import Path
from pipeline import load, volume_report, write_json

if __name__ == "__main__":
    p = argparse.ArgumentParser()
    for key in ("dataset", "manifest", "config", "output"):
        p.add_argument("--" + key, required=True)
    a = p.parse_args()
    rows, dataset, discarded = load(a.dataset, a.manifest, json.loads(Path(a.config).read_text(encoding="utf-8-sig")))
    report = volume_report(rows)
    report.update(data_kind=dataset["data_kind"], discarded=discarded)
    write_json(a.output, report)
    print(json.dumps(report, ensure_ascii=False))
    raise SystemExit(0 if report["ready"] and dataset["data_kind"] == "real" else 2)
