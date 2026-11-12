#!/usr/bin/env python3
"""API-only SSH release preserving the already published manual production base.

Run on the server from a private release directory containing NoPonto.dll/pdb.
No rendered Compose/env/credentials are printed. Rollback toggles the flag on the
same image; failed activation restores the frozen pre-release configuration.
"""
import argparse, copy, hashlib, json, os, pathlib, re, shutil, stat, subprocess, time, uuid

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

# Radar extends the existing frozen/API-only release path, using the same lock,
# health checks and no-build/no-pull up(). Existing ETA modes remain unchanged.
RADAR_FLAGS = ('Radar__Enabled', 'Radar__WorkerEnabled', 'Radar__PushEnabled',
               'Radar__Push__ControlledTestsEnabled', 'Radar__Push__ExperimentalEnabled',
               'Radar__Push__PublicEnabled', 'Radar__Push__ExternalTransportEnabled')
RADAR_LIMITS = {
    'MaxLinhas': (1, 50), 'MaxContextos': (1, 50), 'MaxRegistrosPorDia': (1, 5000),
    'MaxSessoesAtivas': (1, 10000), 'IntervalSeconds': (10, 300), 'MaxParadasPorCiclo': (1, 100),
    'MaxSessoesPorCiclo': (1, 2000), 'MaxSessoesPorParada': (1, 1000),
    'MaxContextosPorParada': (1, 200), 'MaxEventosPorParada': (1, 2000),
    'MaxConcorrencia': (1, 4), 'QueryTimeoutSeconds': (1, 30), 'CandidateTtlSeconds': (1, 120),
    'Push__MaxRequestsPerMinute': (1, 600), 'Push__MaxPendingReceipts': (1, 1024),
    'Push__MaxAttempts': (1, 3), 'Push__MaxConcurrency': (1, 2), 'Push__MinIntervalSeconds': (1, 300)}
RADAR_FILES = ('/run/secrets/radar-push-encryption.key', '/run/secrets/radar-expo-access-token')

def radar_candidate(base, overlay, image=None):
    """Only Radar environment and two readonly secret mounts can change."""
    assert set(overlay) == {'services'} and set(overlay['services']) == {'api'}
    changes = overlay['services']['api']
    assert set(changes) <= {'environment', 'volumes'}
    candidate = copy.deepcopy(base); api = candidate['services']['api']
    env = api.setdefault('environment', {})
    assert env.get('ASPNETCORE_ENVIRONMENT') == 'Production'
    assert env.get('DOTNET_ENVIRONMENT', 'Production') == 'Production'
    assert not api.get('build') and not api.get('command') and not api.get('entrypoint')
    # Reject configuration aliases, never silently normalize conflicting flags.
    for key in env:
        if key.replace(':', '__').lower().startswith('radar__'):
            assert key.startswith('Radar__') and ':' not in key
    allowed = set(RADAR_FLAGS) | {'Radar__' + k for k in RADAR_LIMITS} | {
        'Radar__Push__ProjectId', 'Radar__Push__EncryptionKeyFile', 'Radar__Push__ExpoAccessTokenFile'}
    for key in env:
        if key.startswith('Radar__'):
            assert key in allowed or re.fullmatch(r'Radar__Push__AllowedInstallationIds__(?:[0-9]|[1-9][0-9])', key)
    for key, value in changes.get('environment', {}).items():
        assert key in allowed and isinstance(value, str)
        if key in RADAR_FLAGS: assert value in ('true', 'false')
        if key.startswith('Radar__') and key[7:] in RADAR_LIMITS:
            low, high = RADAR_LIMITS[key[7:]]; assert low <= int(value) <= high
        env[key] = value
    for volume in changes.get('volumes', []):
        assert set(volume) == {'type', 'source', 'target', 'read_only', 'bind'}
        assert volume['type'] == 'bind' and volume['read_only'] is True
        assert volume['bind'] == {'create_host_path': False}
        assert volume['target'] in RADAR_FILES and pathlib.PurePosixPath(volume['source']).is_absolute()
        api['volumes'] = [v for v in api.get('volumes', []) if v['target'] != volume['target']] + [copy.deepcopy(volume)]
    assert int(env.get('Radar__MaxContextos', 40)) >= int(env.get('Radar__MaxLinhas', 20))
    assert env.get('Radar__Push__ControlledTestsEnabled', 'false') == 'false'
    assert env.get('Radar__Push__ExperimentalEnabled', 'false') == 'false'
    if env.get('Radar__Push__PublicEnabled', 'false') == 'true':
        assert all(env.get(k) == 'true' for k in RADAR_FLAGS if k not in
                   ('Radar__Push__ControlledTestsEnabled', 'Radar__Push__ExperimentalEnabled'))
        assert uuid.UUID(env['Radar__Push__ProjectId']).int != 0
        assert env.get('Radar__Push__EncryptionKeyFile') == RADAR_FILES[0]
        for option, target in [('EncryptionKeyFile', RADAR_FILES[0]), ('ExpoAccessTokenFile', RADAR_FILES[1])]:
            path = env.get('Radar__Push__' + option, '')
            if path:
                assert path == target
                mounts = [v for v in api.get('volumes', []) if v['target'] == target]
                assert len(mounts) == 1 and mounts[0]['type'] == 'bind' and mounts[0].get('read_only') is True
    else:
        assert all(env.get(k, 'false') == 'false' for k in RADAR_FLAGS)
    if image:
        assert re.fullmatch(r'(?:sha256:[a-f0-9]{64}|ghcr.io/guilhermedesales/noponto-api@sha256:[a-f0-9]{64})', image)
        api['image'] = image
    return candidate

def radar_assert_running(base, running):
    api = base['services']['api']; actual = dict(x.split('=', 1) for x in running['Config']['Env'])
    assert api['image'] in (running['Config']['Image'], running['Image'])
    assert running['Config']['Labels']['com.docker.compose.project'] == 'noponto'
    assert running['Config']['Labels']['com.docker.compose.service'] == 'api'
    assert all(actual.get(k) == str(v) for k, v in api.get('environment', {}).items())
    select = lambda env: {k: v for k, v in env.items() if k.replace(':', '__').lower().startswith('radar__')}
    assert select(actual) == select(api.get('environment', {}))
    expected = {v['target']: (v['type'], v['source'], not v.get('read_only', False)) for v in api.get('volumes', [])}
    mounted = {v['Destination']: (v['Type'], v.get('Source', ''), v['RW']) for v in running['Mounts']}
    assert expected == mounted
    ports = {(str(p['target']) + '/' + p.get('protocol', 'tcp'), str(p['published']), p.get('host_ip') or '0.0.0.0')
             for p in api.get('ports', [])}
    actual_ports = {(target, p['HostPort'], p.get('HostIp') or '0.0.0.0')
                    for target, bindings in (running['HostConfig'].get('PortBindings') or {}).items() for p in bindings or []}
    assert ports == actual_ports
    assert running['State']['Running'] and not running['State']['OOMKilled']

def radar_private(path, directory=False):
    assert path.is_absolute() and not path.is_symlink()
    info = path.stat()
    assert info.st_uid == os.geteuid() and stat.S_IMODE(info.st_mode) & 0o077 == 0
    assert path.is_dir() if directory else path.is_file()

def radar_save(name, value):
    path = ROOT / name; temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value)); temporary.chmod(0o600); temporary.replace(path)
    return path

def radar_release(args):
    import fcntl
    os.umask(0o077); radar_private(ROOT, directory=True)
    assert args.config
    config = pathlib.Path(args.config); radar_private(config)
    with (STATE / 'deploy.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        base = json.loads(config.read_text()); before = inspect(); radar_assert_running(base, before)
        original = copy.deepcopy(base); original['services']['api']['image'] = before['Image']
        if args.mode == 'radar-restore':
            result = json.loads((ROOT / 'radar-result.json').read_text())
            assert before['Image'] == result['image']
            candidate = json.loads((ROOT / 'radar-previous.json').read_text())
        elif args.mode == 'radar-disable':
            candidate = radar_candidate(base, {'services': {'api': {'environment': {k: 'false' for k in RADAR_FLAGS}}}})
        else:
            assert args.radar_overlay
            overlay = pathlib.Path(args.radar_overlay); radar_private(overlay)
            candidate = radar_candidate(base, json.loads(overlay.read_text()), args.image)
        # Metadata only: never read the actual encryption/access-token secret here.
        for volume in candidate['services']['api'].get('volumes', []):
            if volume['target'] in RADAR_FILES: radar_private(pathlib.Path(volume['source']))
        image = candidate['services']['api']['image']
        image_id = run(['docker', 'image', 'inspect', '--format', '{{.Id}}', image]).strip()
        assert re.fullmatch('sha256:[a-f0-9]{64}', image_id)
        candidate['services']['api']['image'] = image_id
        if args.check:
            print('Radar preflight aprovado; sem troca de API.'); return
        assert args.commit and re.fullmatch('[a-f0-9]{40}', args.commit)
        previous_manual = (STATE / 'active.manual.json').read_text() if (STATE / 'active.manual.json').exists() else None
        previous_result = (ROOT / 'radar-result.json').read_text() if (ROOT / 'radar-result.json').exists() else None
        if args.mode != 'radar-restore': radar_save('radar-previous.json', original)
        path = radar_save('radar-active.json', candidate)
        try:
            up(path); healthy(image_id); radar_assert_running(candidate, inspect())
            metadata = {'mode': args.mode, 'commit': args.commit, 'image': image_id,
                        'configuration': str(path), 'at': time.time()}
            radar_save('radar-result.json', metadata)
            temporary = STATE / 'active.manual.radar.tmp'
            temporary.write_text(json.dumps(metadata)); temporary.chmod(0o600)
            temporary.replace(STATE / 'active.manual.json')
        except Exception:
            restored = radar_save('radar-failed-previous.json', original)
            up(restored); healthy(before['Image']); radar_assert_running(original, inspect())
            radar_save('radar-active.json', original)
            if previous_result is not None: radar_save('radar-result.json', json.loads(previous_result))
            else: (ROOT / 'radar-result.json').unlink(missing_ok=True)
            if previous_manual is not None:
                temporary = STATE / 'active.manual.radar.tmp'
                temporary.write_text(previous_manual); temporary.chmod(0o600); temporary.replace(STATE / 'active.manual.json')
            raise
        print('Radar release confirmado; somente API alterada. Tickets nao confirmam entrega.')

def ci_release(args):
    """Promote one digest using the same frozen configuration, lock and API rollback."""
    import fcntl
    global ROOT, STATE
    assert re.fullmatch(r'ghcr\.io/guilhermedesales/noponto-api@sha256:[a-f0-9]{64}', args.image or '')
    assert re.fullmatch('[a-f0-9]{40}', args.commit or '')
    assert args.sequence and re.fullmatch('[1-9][0-9]{0,19}', args.sequence)
    STATE = ROOT = pathlib.Path(args.state)
    radar_private(STATE, directory=True)
    os.umask(0o077)
    # --check does not create a lock file or any other remote artifact.
    with (STATE / 'deploy.lock').open('r+' if args.check else 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        metadata_path = STATE / 'active.manual.json'; radar_private(metadata_path)
        metadata = json.loads(metadata_path.read_text())
        config = pathlib.Path(metadata['configuration']); radar_private(config)
        original = json.loads(config.read_text()); before = inspect()
        # Manual releases may have replaced only the image since the last metadata update.
        original['services']['api']['image'] = before['Image']
        radar_assert_running(original, before)
        api = original['services']['api']
        assert not api.get('build') and not api.get('command') and not api.get('entrypoint')
        assert api.get('environment', {}).get('ASPNETCORE_ENVIRONMENT') == 'Production'
        actual = dict(item.split('=', 1) for item in before['Config']['Env'])
        for prefix in ('PhysicalEta__', 'HistoricalEtaShadow__'):
            select = lambda env: {k: str(v) for k, v in env.items() if k.replace(':', '__').lower().startswith(prefix.lower())}
            assert select(actual) == select(api.get('environment', {}))
        # Freeze image defaults and manually injected variables too, not only Compose entries.
        api['environment'] = actual
        for volume in api.get('volumes', []):
            if volume['target'] in RADAR_FILES: radar_private(pathlib.Path(volume['source']))
        marker = STATE / 'active.run'
        previous_sequence = marker.read_text() if marker.exists() else None
        if previous_sequence is not None:
            assert re.fullmatch('[0-9]+', previous_sequence.strip())
            assert int(args.sequence) > int(previous_sequence)
        if args.check:
            print('CI preflight aprovado: composicao ativa, mounts, Radar, PhysicalEta e Shadow preservados.'); return
        # Keep rollback bytes locally even if the previous registry reference has changed.
        run(['docker', 'image', 'inspect', '--format', '{{.Id}}', before['Image']])
        run(['docker', 'image', 'tag', before['Image'], 'noponto-api:rollback-' + before['Image'][7:]])
        run(['docker', 'pull', args.image], 300)
        image_id = run(['docker', 'image', 'inspect', '--format', '{{.Id}}', args.image]).strip()
        assert re.fullmatch('sha256:[a-f0-9]{64}', image_id)
        candidate = copy.deepcopy(original); candidate['services']['api']['image'] = args.image
        radar_save('ci-previous.json', original)
        path = radar_save('ci-active.json', candidate)
        import signal
        def interrupted(signum, frame): raise RuntimeError('CI release interrupted')
        for signum in (signal.SIGHUP, signal.SIGINT, signal.SIGTERM): signal.signal(signum, interrupted)
        try:
            up(path); healthy(image_id); radar_assert_running(candidate, inspect())
            radar_save('active.manual.json', {'mode': 'ci', 'commit': args.commit, 'image': image_id,
                'configuration': str(path), 'digest': args.image, 'at': time.time()})
            temporary = STATE / 'active.run.tmp'
            temporary.write_text(args.sequence + '\n'); temporary.chmod(0o600); temporary.replace(marker)
        except BaseException:
            restored = STATE / 'ci-previous.json'
            up(restored); healthy(before['Image']); radar_assert_running(original, inspect())
            radar_save('ci-active.json', original)
            radar_save('active.manual.json', metadata)
            if previous_sequence is not None:
                temporary = STATE / 'active.run.tmp'; temporary.write_text(previous_sequence); temporary.replace(marker)
            else: marker.unlink(missing_ok=True)
            raise
        print('CI release confirmado; somente API atualizada.')


def main():
    import fcntl
    parser = argparse.ArgumentParser(); parser.add_argument('mode', choices=['activate', 'rollback-flag', 'restore-base',
        'radar-activate', 'radar-disable', 'radar-restore', 'ci'])
    parser.add_argument('--commit'); parser.add_argument('--config'); parser.add_argument('--image')
    parser.add_argument('--state'); parser.add_argument('--sequence'); parser.add_argument('--radar-overlay'); parser.add_argument('--check', action='store_true'); args = parser.parse_args()
    if args.mode == 'ci': return ci_release(args)
    if args.mode.startswith('radar-'): return radar_release(args)
    assert not (args.config or args.image or args.radar_overlay or args.check), 'Radar arguments require a Radar mode'
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
