#!/usr/bin/env python3
"""API-only SSH release preserving the already published manual production base.

Run on the server from a private release directory containing NoPonto.dll/pdb.
No rendered Compose/env/credentials are printed. Rollback toggles the flag on the
same image; failed activation restores the frozen pre-release configuration.
"""
import argparse, copy, hashlib, json, os, pathlib, shutil, subprocess, time

ROOT = pathlib.Path(__file__).resolve().parent
STATE = pathlib.Path('/home/guilherme/.noponto-deploy')
BASE = STATE / 'network-27/promoted.json'

def run(args, timeout=120):
    return subprocess.check_output(args, stderr=subprocess.STDOUT, timeout=timeout, text=True)

def inspect():
    return json.loads(run(['docker', 'inspect', 'noponto_api']))[0]

def save(name, value):
    path = ROOT / name
    path.write_text(json.dumps(value)); path.chmod(0o600)
    return path

def up(path):
    run(['docker', 'compose', '-p', 'noponto', '--project-directory', '/opt/stacks/noponto',
         '--env-file', '/dev/null', '-f', str(path), 'up', '-d', '--no-deps', '--no-build', '--pull', 'never', 'api'])

def healthy(image):
    good = 0
    for _ in range(60):
        c = inspect()
        try:
            code = run(['curl', '-sS', '-o', '/dev/null', '-w', '%{http_code}', '--max-time', '5', 'http://127.0.0.1:8080/'])
            good = good + 1 if c['Image'] == image and c['State']['Running'] and not c['State']['OOMKilled'] and c['RestartCount'] == 0 and code == '200' else 0
        except Exception:
            good = 0
        if good >= 3: return
        time.sleep(2)
    raise RuntimeError('API health validation failed')

def main():
    import fcntl
    parser = argparse.ArgumentParser(); parser.add_argument('mode', choices=['activate', 'rollback-flag', 'restore-base'])
    parser.add_argument('--commit'); args = parser.parse_args()
    os.umask(0o077)
    with (STATE / 'deploy.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        before = inspect(); before_image = before['Image']
        if args.mode == 'activate':
            assert args.commit and len(args.commit) == 40
            assert not (ROOT / 'activated.json').exists(), 'Already activated'
            base = json.loads(BASE.read_text()); api = base['services']['api']
            assert api['image'] == before['Config']['Image'], 'Production base changed'
            env = dict(x.split('=', 1) for x in before['Config']['Env'])
            assert all(env.get(k) == str(v) for k, v in api.get('environment', {}).items()), 'Production environment drift'
            assert not before['State']['OOMKilled'] and before['State']['Running']
            save('original.json', base)
            if (STATE/'active.manual.json').exists(): shutil.copy2(STATE/'active.manual.json',ROOT/'original-active.manual.json')
            tag = 'noponto-api:eta-hybrid-2i-' + args.commit[:12]
            (ROOT / 'Dockerfile').write_text('FROM ' + api['image'] + '\nCOPY NoPonto.dll NoPonto.pdb /app/\nLABEL org.opencontainers.image.revision="' + args.commit + '" org.opencontainers.image.version="eta-hybrid-v1-2i"\n')
            run(['docker', 'build', '--network=none', '--pull=false', '-t', tag, str(ROOT)], 180)
            candidate = copy.deepcopy(base); candidate['services']['api']['image'] = tag
            candidate['services']['api']['environment'].update(PhysicalEta__HybridPublicEnabled='true', PhysicalEta__HybridFixedReferenceKmh='20')
            path = save('active.json', candidate)
        else:
            active = json.loads((ROOT / 'active.json').read_text())
            assert before['Config']['Image'] == active['services']['api']['image'], 'Another release is active'
            if args.mode == 'restore-base':
                path = ROOT / 'original.json'
            else:
                active['services']['api']['environment']['PhysicalEta__HybridPublicEnabled'] = 'false'
                path = save('active.json', active)
        try:
            up(path)
            expected = json.loads(path.read_text())['services']['api']['image']
            image_id = run(['docker', 'image', 'inspect', '--format', '{{.Id}}', expected]).strip()
            healthy(image_id)
        except Exception:
            original=json.loads((ROOT / 'original.json').read_text())
            original_id=run(['docker','image','inspect','--format','{{.Id}}',original['services']['api']['image']]).strip()
            up(ROOT / 'original.json'); healthy(original_id)
            print('Activation failed: pre-release API restored; no dependency service changed.')
            raise
        published_commit=args.commit or inspect()['Config'].get('Labels',{}).get('org.opencontainers.image.revision')
        metadata = {'mode': args.mode, 'commit': published_commit, 'image': image_id,
                    'configuration': str(path), 'at': time.time(), 'dllSha256': hashlib.sha256((ROOT / 'NoPonto.dll').read_bytes()).hexdigest()}
        save('activated.json', metadata)
        save('result.json', metadata)
        metadata['hybridPublicEnabled']=str(json.loads(path.read_text())['services']['api'].get('environment',{}).get('PhysicalEta__HybridPublicEnabled','false')).lower()=='true'
        temporary=STATE/'active.manual.eta-2i.tmp'
        temporary.write_text(json.dumps(metadata));temporary.chmod(0o600)
        temporary.replace(STATE/'active.manual.json')
        print(json.dumps(metadata))

if __name__ == '__main__':
    try: main()
    except Exception as error:
        # Never echo subprocess output: Compose/SQL/provider errors may include secrets.
        print('Release failed:', type(error).__name__); raise SystemExit(1)
