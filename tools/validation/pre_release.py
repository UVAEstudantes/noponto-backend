"""Local pre-release selection using the existing CI filter/TRX verifier.

No build, application startup, network, database or configuration mutation.
Does not modify the CI/CD allowlist or publication policy.
"""
import importlib.util
from pathlib import Path
import sys

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("existing_offline_tests", HERE.parents[1] / ".ci/offline_tests.py")
offline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(offline)
offline.MANIFEST = HERE / "pre-release-tests.json"

if __name__ == "__main__":
    if sys.argv[1:] == ["filter"]:
        print(offline.test_filter())
    elif len(sys.argv) == 3 and sys.argv[1] == "verify":
        print(f"Pre-release local: {offline.verify(sys.argv[2])} testes aprovados; todas as classes presentes.")
    else:
        raise SystemExit("Uso: pre_release.py filter | verify arquivo.trx")
