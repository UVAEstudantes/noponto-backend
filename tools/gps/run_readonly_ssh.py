"""Bundle read-only probes over SSH stdin; no remote file installation or scheduling."""
import argparse
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('probe', choices=('inspect', 'observe'))
parser.add_argument('--host', default='guilherme@100.122.72.14')
args = parser.parse_args()
root = Path(__file__).resolve().parent
bundle = 'import sys,types\n'
modules = ['probe_http'] + (['its_window'] if args.probe == 'observe' else [])
for name in modules:
    source = (root / (name + '.py')).read_text(encoding='utf-8')
    bundle += f"m=types.ModuleType({name!r});m.__file__='/tmp/{name}.py';sys.modules[{name!r}]=m;exec({source!r},m.__dict__)\n"
entry = 'observe_its_readonly.py' if args.probe == 'observe' else 'inspect_its_homeserver.py'
bundle += (root / entry).read_text(encoding='utf-8')
result = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
                         '-o', 'ConnectTimeout=10', args.host, 'python3 -'],
                        input=bundle.encode(), timeout=300 if args.probe == 'observe' else 90)
raise SystemExit(result.returncode)
