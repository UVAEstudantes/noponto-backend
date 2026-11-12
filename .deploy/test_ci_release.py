"""CI promotion exercises the existing frozen/manual executor without Docker or SSH."""
import copy
import json
import pathlib
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

from test_radar_release import release, base, running, IMAGE, PREVIOUS


class CiReleaseTests(unittest.TestCase):
    def execute(self, check=False, failure=None, drift=False, sequence='167'):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            original = base()
            original['services']['api']['environment'].update(
                PhysicalEta__Enabled='true', HistoricalEtaShadow__Enabled='false', Radar__Enabled='true')
            config = root / 'manual.json'
            config.write_text(json.dumps(original))
            metadata = {'configuration': str(config), 'image': PREVIOUS}
            (root / 'active.manual.json').write_text(json.dumps(metadata))
            (root / 'active.run').write_text('166\n')
            (root / 'deploy.lock').touch()
            before = running(original)
            # A newer manual image is intentionally adopted only when configuration matches.
            before['Image'] = 'sha256:' + 'd' * 64
            before['Config']['Image'] = 'noponto-api:manual-newer'
            if drift:
                before['Config']['Env'].append('PhysicalEta__Unexpected=true')
            candidate = copy.deepcopy(original)
            candidate['services']['api']['image'] = 'ghcr.io/guilhermedesales/noponto-api@' + IMAGE
            after = running(candidate, IMAGE)
            args = types.SimpleNamespace(state=str(root), sequence=sequence, image=candidate['services']['api']['image'],
                                         commit='c' * 40, check=check)
            fcntl = types.SimpleNamespace(flock=lambda *a: None, LOCK_EX=1, LOCK_NB=2)
            signals = types.SimpleNamespace(SIGHUP=1, SIGINT=2, SIGTERM=15, signal=lambda *a: None)
            def command(command, *args):
                return IMAGE if command[:3] == ['docker', 'image', 'inspect'] else ''
            with (patch.object(release, 'radar_private'), patch.object(release, 'inspect', side_effect=[before, before] if failure else [before, after]),
                 patch.object(release, 'run', side_effect=command) as run, patch.object(release, 'up') as up,
                 patch.object(release, 'healthy', side_effect=[failure, None] if failure else None),
                 patch.dict(sys.modules, {'fcntl': fcntl, 'signal': signals})):
                error = None
                try:
                    release.ci_release(args)
                except BaseException as ex:
                    error = ex
            result = {p.name: p.read_bytes() for p in root.iterdir() if p.is_file()}
            return original, metadata, before, result, run, up, error

    def test_preflight_is_readonly_and_checks_drift_and_sequence(self):
        for drift, sequence, denied in ((False, '167', False), (True, '167', True), (False, '166', True)):
            with self.subTest(drift=drift, sequence=sequence):
                _, _, _, files, run, up, error = self.execute(check=True, drift=drift, sequence=sequence)
                self.assertEqual(denied, error is not None)
                run.assert_not_called(); up.assert_not_called()
                self.assertEqual({'manual.json', 'active.manual.json', 'active.run', 'deploy.lock'}, set(files))
                self.assertEqual('166', files['active.run'].decode().strip())

    def test_success_changes_only_api_image_and_persists_digest_sequence_and_live_rollback(self):
        original, _, before, files, run, up, error = self.execute()
        self.assertIsNone(error)
        candidate = json.loads(files['ci-active.json'])
        self.assertEqual('ghcr.io/guilhermedesales/noponto-api@' + IMAGE, candidate['services']['api']['image'])
        candidate['services']['api']['image'] = original['services']['api']['image']
        self.assertEqual(original, candidate)
        self.assertEqual(before['Image'], json.loads(files['ci-previous.json'])['services']['api']['image'])
        self.assertEqual(IMAGE, json.loads(files['active.manual.json'])['image'])
        self.assertEqual('167', files['active.run'].decode().strip())
        self.assertEqual(1, up.call_count)
        self.assertTrue(any(c.args[0][:2] == ['docker', 'pull'] for c in run.call_args_list))

    def test_failed_health_or_interruption_restores_live_image_configuration_and_metadata(self):
        for failure in (RuntimeError('health'), KeyboardInterrupt()):
            with self.subTest(failure=type(failure).__name__):
                original, metadata, before, files, _, up, error = self.execute(failure=failure)
                self.assertIsInstance(error, type(failure))
                restored = json.loads(files['ci-active.json'])
                self.assertEqual(before['Image'], restored['services']['api']['image'])
                restored['services']['api']['image'] = original['services']['api']['image']
                self.assertEqual(original, restored)
                self.assertEqual(metadata, json.loads(files['active.manual.json']))
                self.assertEqual('166', files['active.run'].decode().strip())
                self.assertEqual(['ci-active.json', 'ci-previous.json'], [c.args[0].name for c in up.call_args_list])
