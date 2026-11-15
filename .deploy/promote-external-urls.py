#!/usr/bin/env python3
"""Promote exactly five dotenv URLs; read-only unless --apply is explicit."""
import argparse
import copy
import importlib.util
import json
import os
import pathlib
import re
import signal
import subprocess
import sys
import time
import uuid
from urllib.parse import urlsplit


def load_release():
    spec = importlib.util.spec_from_file_location('noponto_release',
        pathlib.Path(__file__).with_name('eta-hybrid-release.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


release = load_release()
KEYS = ('ItsCurrent__BaseUrl', 'GtfsRealtimeGps__BusVehiclePositionsUrl',
        'GtfsRealtimeGps__BrtVehiclePositionsUrl', 'TremRealtime__BaseUrl', 'Radar__Push__BaseUrl')


def require(condition):
    if not condition:
        raise ValueError('Preflight recusado; valores omitidos.')


def valid_url(value):
    try:
        if not isinstance(value, str) or not value or any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in value):
            return False
        url = urlsplit(value)
        return (url.scheme in ('http', 'https') and bool(url.hostname)
                and url.port != 0 and url.username is None and url.password is None and not url.fragment
                and '\\' not in value and '$' not in value)
    except ValueError:
        return False


def read_values(path):
    """Read literal assignments only; never source/evaluate dotenv or other variables."""
    values = {}
    names = {key.casefold(): key for key in KEYS}
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        match = re.match(r'^\s*(?:export\s+)?([^\s=]+)\s*=(.*)$', line)
        if not match:
            continue
        key, raw = match.groups()
        canonical = names.get(key.replace(':', '__').casefold())
        if canonical is None:
            continue
        require(key == canonical and key not in values)
        raw = raw.strip()
        if raw.startswith(('"', "'")):
            quoted = re.fullmatch(r'''(["'])(.*?)\1\s*(?:#.*)?''', raw)
            require(quoted is not None)
            value = quoted[2]
        else:
            value = re.split(r'\s+#', raw, maxsplit=1)[0].rstrip()
        require(valid_url(value))
        values[key] = value
    require(set(values) == set(KEYS))
    return values


def candidate(base, values):
    require(set(values) == set(KEYS) and all(valid_url(v) for v in values.values()))
    api = base['services']['api']
    require(api['environment'].get('ASPNETCORE_ENVIRONMENT') == 'Production'
            and api['environment'].get('DOTNET_ENVIRONMENT', 'Production') == 'Production'
            and not api.get('build') and not api.get('command') and not api.get('entrypoint'))
    canonical = {key.casefold(): key for key in KEYS}
    for key in api['environment']:
        target = canonical.get(key.replace(':', '__').casefold())
        require(target is None or target == key)
    result = copy.deepcopy(base)
    result['services']['api']['environment'].update(values)
    return result


def render(config):
    result = subprocess.run(['docker', 'compose', '-p', 'noponto', '--project-directory',
        '/opt/stacks/noponto', '--env-file', '/dev/null', '-f', '-', 'config', '--format', 'json'],
        input=json.dumps(config), capture_output=True, text=True, check=True, timeout=30)
    return json.loads(result.stdout)


def validate_render(base, proposed, actual):
    before, after = render(base), render(proposed)
    # Refuse unintended Compose interpolation of an already frozen secret/value.
    require(before['services']['api']['environment'] == actual)
    normalized = copy.deepcopy(after)
    env = normalized['services']['api']['environment']
    old = before['services']['api']['environment']
    for key in KEYS:
        if key in old:
            env[key] = old[key]
        else:
            env.pop(key, None)
    require(normalized == before)
    require(after['services']['api']['environment'] == proposed['services']['api']['environment'])


def write_private(path, content):
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    with temporary.open('xb') as stream:
        os.chmod(temporary, 0o600)
        stream.write(content)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def encode(value):
    return json.dumps(value).encode()


def run(args):
    import fcntl
    require(__debug__ and not (args.apply and args.check))
    state = pathlib.Path(args.state)
    release.radar_private(state, directory=True)
    # Existing lock only: preflight must not create any file or directory.
    with (state / 'deploy.lock').open('r') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        metadata_path = state / 'active.manual.json'
        release.radar_private(metadata_path)
        metadata_bytes = metadata_path.read_bytes()
        metadata = json.loads(metadata_bytes)
        config_path = pathlib.Path(metadata['configuration'])
        release.radar_private(config_path)
        original_bytes = config_path.read_bytes()
        original = json.loads(original_bytes)
        before = release.inspect()
        release.radar_assert_running(original, before)
        actual = dict(item.split('=', 1) for item in before['Config']['Env'])
        require(len(actual) == len(before['Config']['Env'])
                and original['services']['api']['environment'] == actual)
        image = before['Image']
        # Both apply and rollback use --pull never; the frozen image reference must resolve locally.
        cached_image = release.run(['docker', 'image', 'inspect', '--format', '{{.Id}}',
                                    original['services']['api']['image']]).strip()
        require(cached_image == image)
        backup = None
        if args.rollback:
            backup = pathlib.Path(args.rollback)
            require(backup.parent == state / 'url-releases' and re.fullmatch('[a-f0-9]{32}', backup.name))
            release.radar_private(backup.parent, directory=True)
            release.radar_private(backup, directory=True)
            for name in ('previous.json', 'previous-metadata.json', 'applied.json'):
                release.radar_private(backup / name)
            applied = json.loads((backup / 'applied.json').read_bytes())
            # A later release/promotion invalidates this rollback; never undo another operator's work.
            require(metadata == applied['metadata'] and image == applied['image'])
            proposed = json.loads((backup / 'previous.json').read_bytes())
            proposed_metadata = json.loads((backup / 'previous-metadata.json').read_bytes())
            previous_path = pathlib.Path(proposed_metadata['configuration'])
            release.radar_private(previous_path)
            require(previous_path.read_bytes() == (backup / 'previous.json').read_bytes())
            values = {k: proposed['services']['api']['environment'].get(k) for k in KEYS}
            # This also permits restoring absence of the five keys (application defaults).
            expected = copy.deepcopy(original)
            for key, value in values.items():
                if value is None:
                    expected['services']['api']['environment'].pop(key, None)
                else:
                    require(valid_url(value))
                    expected['services']['api']['environment'][key] = value
            require(expected == proposed)
        else:
            env_file = pathlib.Path(args.env_file)
            release.radar_private(env_file)
            values = read_values(env_file)
            proposed = candidate(original, values)
            proposed_metadata = None
        validate_render(original, proposed, actual)
        for key in KEYS:
            changed = actual.get(key) != proposed['services']['api']['environment'].get(key)
            print(key + ': validação aprovada; ' + ('alteração proposta' if changed else 'inalterada'))
        if not args.apply:
            print('SIMULAÇÃO aprovada; nenhum arquivo ou container alterado.')
            return
        if proposed == original:
            print('Sem alterações necessárias; nenhuma aplicação realizada.')
            return
        require(metadata_path.read_bytes() == metadata_bytes and config_path.read_bytes() == original_bytes)
        os.umask(0o077)
        parent = state / 'url-releases'
        if not parent.exists():
            parent.mkdir(mode=0o700)
        release.radar_private(parent, directory=True)
        operation = parent / uuid.uuid4().hex
        operation.mkdir(mode=0o700)
        # Durable private backup precedes any runtime or active-metadata mutation.
        write_private(operation / 'previous.json', original_bytes)
        write_private(operation / 'previous-metadata.json', metadata_bytes)
        target = operation / 'candidate.json'
        write_private(target, encode(proposed))
        if proposed_metadata is None:
            proposed_metadata = dict(metadata, mode='urls-promote', configuration=str(target),
                                     image=image, at=time.time())
        release.radar_assert_running(original, release.inspect())
        def interrupted(signum, frame):
            raise RuntimeError('Promoção interrompida.')
        handlers = {s: signal.signal(s, interrupted) for s in (signal.SIGTERM, signal.SIGINT)}
        try:
            release.up(target)
            release.healthy(image)
            current = release.inspect()
            release.radar_assert_running(proposed, current)
            require(dict(x.split('=', 1) for x in current['Config']['Env']) == proposed['services']['api']['environment'])
            write_private(metadata_path, encode(proposed_metadata))
            write_private(operation / 'applied.json', encode({'metadata': proposed_metadata, 'image': image}))
        except BaseException:
            try:
                restored = operation / 'previous.json'
                release.up(restored)
                release.healthy(image)
                restored_container = release.inspect()
                release.radar_assert_running(original, restored_container)
                require(dict(x.split('=', 1) for x in restored_container['Config']['Env']) == actual)
                write_private(metadata_path, metadata_bytes)
                print('Rollback automático concluído; configuração anterior restaurada.')
            except BaseException:
                print('ROLLBACK NÃO CONFIRMADO; requer intervenção autorizada. Backup: ' + str(operation))
                raise
            raise
        finally:
            for signum, handler in handlers.items():
                signal.signal(signum, handler)
        print('Aplicação confirmada; mesma imagem, somente API. Backup: ' + str(operation))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--state', default='/home/guilherme/.noponto-deploy')
    parser.add_argument('--env-file', default='/opt/stacks/noponto/.env')
    parser.add_argument('--rollback', help='Diretório de backup da promoção ainda ativa')
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--apply', action='store_true')
    run(parser.parse_args())


if __name__ == '__main__':
    try:
        main()
    except (Exception, KeyboardInterrupt) as error:
        # No traceback or subprocess output: those can contain the frozen environment.
        print('Operação recusada/falhou; detalhes sensíveis omitidos. Tipo: ' + type(error).__name__)
        raise SystemExit(1)
