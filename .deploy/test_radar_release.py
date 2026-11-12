"""Focused pure/mocked tests. Never calls Docker, SSH, HTTP or production."""
import copy
import importlib.util
import json
import pathlib
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('radar_release', pathlib.Path(__file__).with_name('eta-hybrid-release.py'))
release = importlib.util.module_from_spec(spec); spec.loader.exec_module(release)
IMAGE = 'sha256:' + 'b' * 64
PREVIOUS = 'sha256:' + 'a' * 64

def base():
    return {'services': {'api': {'image': PREVIOUS, 'environment': {'ASPNETCORE_ENVIRONMENT': 'Production',
        'PhysicalEta__HybridPublicEnabled': 'true', 'UNRELATED': 'synthetic'},
        'ports': [{'target': 8080, 'published': '8080'}],
        'volumes': [{'type': 'bind', 'source': '/var/run/docker.sock', 'target': '/var/run/docker.sock'}]},
        'redis': {'image': 'redis:7'}, 'postgres': {'image': 'fixture-postgres'}}}

def overlay():
    flags = {k: 'true' for k in release.RADAR_FLAGS}
    flags.update(Radar__Push__ControlledTestsEnabled='false', Radar__Push__ExperimentalEnabled='false',
                 Radar__Push__ProjectId='ab3db7e8-2478-49d6-a339-e639dcce1a34',
                 Radar__Push__EncryptionKeyFile=release.RADAR_FILES[0])
    return {'services': {'api': {'environment': flags, 'volumes': [{'type': 'bind',
        'source': '/private/synthetic.key', 'target': release.RADAR_FILES[0], 'read_only': True,
        'bind': {'create_host_path': False}}]}}}

def running(config, image_id=PREVIOUS):
    api = config['services']['api']
    return {'Image': image_id, 'Config': {'Image': api['image'], 'Env': [k+'='+v for k,v in api['environment'].items()],
        'Labels': {'com.docker.compose.project': 'noponto', 'com.docker.compose.service': 'api'}},
        'Mounts': [{'Destination': v['target'], 'Source': v['source'], 'Type': v['type'],
                    'RW': not v.get('read_only', False)} for v in api.get('volumes', [])],
        'HostConfig': {'PortBindings': {'8080/tcp': [{'HostPort': '8080', 'HostIp': ''}]}},
        'State': {'Running': True, 'OOMKilled': False}}

class RadarReleaseTests(unittest.TestCase):
    def test_public_overlay_preserves_eta_dependencies_ports_and_base(self):
        original = base(); before = copy.deepcopy(original)
        candidate = release.radar_candidate(original, overlay(), IMAGE)
        self.assertEqual(before, original)
        self.assertEqual(IMAGE, candidate['services']['api']['image'])
        self.assertEqual(before['services']['redis'], candidate['services']['redis'])
        self.assertEqual(before['services']['postgres'], candidate['services']['postgres'])
        self.assertEqual(before['services']['api']['ports'], candidate['services']['api']['ports'])
        self.assertEqual('true', candidate['services']['api']['environment']['PhysicalEta__HybridPublicEnabled'])
        self.assertEqual('synthetic', candidate['services']['api']['environment']['UNRELATED'])
        self.assertEqual(2, len(candidate['services']['api']['volumes']))

    def test_rejects_non_radar_edits_mutable_image_testing_and_writable_secret(self):
        for kind in ('service', 'environment', 'mount', 'testing', 'image', 'alias', 'key', 'limit'):
            with self.subTest(kind=kind):
                original = base(); changes = overlay(); image = IMAGE
                if kind == 'service': changes['services']['redis'] = {}
                if kind == 'environment': changes['services']['api']['environment']['GPS_ENABLED'] = 'false'
                if kind == 'mount': changes['services']['api']['volumes'][0]['read_only'] = False
                if kind == 'testing': original['services']['api']['environment']['ASPNETCORE_ENVIRONMENT'] = 'Testing'
                if kind == 'image': image = 'noponto-api:latest'
                if kind == 'alias': original['services']['api']['environment']['radar:enabled'] = 'true'
                if kind == 'key': changes['services']['api']['volumes'] = []
                if kind == 'limit': changes['services']['api']['environment']['Radar__MaxConcorrencia'] = '100'
                with self.assertRaises(AssertionError): release.radar_candidate(original, changes, image)

    def test_disable_clears_all_seven_flags_and_preserves_readonly_key(self):
        enabled = release.radar_candidate(base(), overlay(), IMAGE)
        disabled = release.radar_candidate(enabled, {'services': {'api': {'environment': {k:'false' for k in release.RADAR_FLAGS}}}})
        self.assertTrue(all(disabled['services']['api']['environment'][k]=='false' for k in release.RADAR_FLAGS))
        self.assertEqual(enabled['services']['api']['volumes'], disabled['services']['api']['volumes'])
        self.assertEqual(IMAGE, disabled['services']['api']['image'])

    def test_runtime_drift_rejected_for_environment_mount_port_and_image(self):
        original = base(); current = running(original)
        release.radar_assert_running(original, current)
        for kind in ('environment', 'mount', 'port', 'image'):
            with self.subTest(kind=kind):
                changed = copy.deepcopy(current)
                if kind == 'environment': changed['Config']['Env'].append('Radar__Push__PublicEnabled=true')
                if kind == 'mount': changed['Mounts'][0]['RW'] = False
                if kind == 'port': changed['HostConfig']['PortBindings']['8080/tcp'][0]['HostPort'] = '9999'
                if kind == 'image': changed['Config']['Image'] = IMAGE; changed['Image'] = IMAGE
                with self.assertRaises(AssertionError): release.radar_assert_running(original, changed)

    def test_failed_health_restores_original_image_full_configuration_and_manual_state(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); original = base(); config = root/'base.json'
            config.write_text(json.dumps(original)); (root/'active.manual.json').write_text('{"previous":true}')
            args = types.SimpleNamespace(config=str(config), mode='radar-activate',
                radar_overlay=str(root/'overlay.json'), image=IMAGE, check=False, commit='c'*40)
            (root/'overlay.json').write_text(json.dumps(overlay()))
            fcntl = types.SimpleNamespace(flock=lambda *a: None, LOCK_EX=1, LOCK_NB=2)
            with patch.object(release, 'ROOT', root), patch.object(release, 'STATE', root), \
                 patch.object(release, 'radar_private'), patch.object(release, 'inspect', side_effect=[running(original), running(original)]), \
                 patch.object(release, 'run', return_value=IMAGE), patch.object(release, 'up') as up, \
                 patch.object(release, 'healthy', side_effect=[RuntimeError('synthetic'), None]), \
                 patch.dict(sys.modules, {'fcntl': fcntl}):
                with self.assertRaises(RuntimeError): release.radar_release(args)
            self.assertEqual(['radar-active.json', 'radar-failed-previous.json'], [call.args[0].name for call in up.call_args_list])
            self.assertEqual(original, json.loads((root/'radar-active.json').read_text()))
            self.assertEqual({'previous':True}, json.loads((root/'active.manual.json').read_text()))
            self.assertFalse((root/'radar-result.json').exists())

    def test_check_does_not_up_save_or_change_manual_state(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); original = base(); config = root/'base.json'; config.write_text(json.dumps(original))
            args = types.SimpleNamespace(config=str(config), mode='radar-disable', check=True)
            fcntl = types.SimpleNamespace(flock=lambda *a: None, LOCK_EX=1, LOCK_NB=2)
            with patch.object(release, 'ROOT', root), patch.object(release, 'STATE', root), \
                 patch.object(release, 'radar_private'), patch.object(release, 'inspect', return_value=running(original)), \
                 patch.object(release, 'run', return_value=PREVIOUS), patch.object(release, 'up') as up, \
                 patch.object(release, 'radar_save') as save, patch.dict(sys.modules, {'fcntl': fcntl}):
                release.radar_release(args)
            up.assert_not_called(); save.assert_not_called()
            self.assertFalse((root/'active.manual.json').exists())

    def test_restore_requires_current_release_and_restores_previous_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); previous = base()
            active = release.radar_candidate(previous, overlay(), IMAGE)
            config = root/'radar-active.json'; config.write_text(json.dumps(active))
            (root/'radar-previous.json').write_text(json.dumps(previous))
            (root/'radar-result.json').write_text(json.dumps({'image':PREVIOUS}))
            args = types.SimpleNamespace(config=str(config), mode='radar-restore', check=False, commit='c'*40)
            fcntl = types.SimpleNamespace(flock=lambda *a: None, LOCK_EX=1, LOCK_NB=2)
            with patch.object(release, 'ROOT', root), patch.object(release, 'STATE', root), \
                 patch.object(release, 'radar_private'), patch.object(release, 'inspect', return_value=running(active, IMAGE)), \
                 patch.object(release, 'up') as up, patch.dict(sys.modules, {'fcntl': fcntl}):
                with self.assertRaises(AssertionError): release.radar_release(args)
                up.assert_not_called()
            (root/'radar-result.json').write_text(json.dumps({'image':IMAGE}))
            with patch.object(release, 'ROOT', root), patch.object(release, 'STATE', root), \
                 patch.object(release, 'radar_private'), patch.object(release, 'inspect', side_effect=[running(active, IMAGE), running(previous)]), \
                 patch.object(release, 'run', return_value=PREVIOUS), patch.object(release, 'up') as up, \
                 patch.object(release, 'healthy'), patch.dict(sys.modules, {'fcntl': fcntl}):
                release.radar_release(args)
            up.assert_called_once_with(root/'radar-active.json')
            self.assertEqual(previous, json.loads(config.read_text()))
            self.assertEqual(PREVIOUS, json.loads((root/'active.manual.json').read_text())['image'])

if __name__ == '__main__': unittest.main()
