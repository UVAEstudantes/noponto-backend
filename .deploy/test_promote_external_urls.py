"""URL promotion tests with fake Docker/health/lock; no network or production access."""
import contextlib
import copy
import importlib.util
import io
import json
import pathlib
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('url_promotion', pathlib.Path(__file__).with_name('promote-external-urls.py'))
promotion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(promotion)
IMAGE = 'sha256:' + 'a' * 64
VALUES = {key: 'https://example.invalid/' + str(i) + '/' for i, key in enumerate(promotion.KEYS)}


def base():
    return {'services': {'api': {'image': IMAGE, 'environment': {
        'ASPNETCORE_ENVIRONMENT': 'Production', 'GpsSources__BusPrimarySource': 'GTFSRT_BUS',
        'PhysicalEta__Enabled': 'true', 'Radar__Enabled': 'true', 'PRIVATE_FIXTURE': 'synthetic-only'},
        'ports': [{'target': 8080, 'published': '8080'}],
        'volumes': [{'type': 'bind', 'source': '/private/key', 'target': '/run/key', 'read_only': True}]},
        'postgres': {'image': 'fixture-postgres'}, 'redis': {'image': 'redis:7'}},
        'networks': {'default': {'name': 'noponto_default'}}}


def container(config):
    api = config['services']['api']
    return {'Image': IMAGE, 'Config': {'Image': api['image'],
        'Env': [k + '=' + v for k, v in api['environment'].items()],
        'Labels': {'com.docker.compose.project': 'noponto', 'com.docker.compose.service': 'api'}},
        'Mounts': [{'Destination': '/run/key', 'Source': '/private/key', 'Type': 'bind', 'RW': False}],
        'HostConfig': {'PortBindings': {'8080/tcp': [{'HostPort': '8080', 'HostIp': ''}]}},
        'State': {'Running': True, 'OOMKilled': False}}


class UrlValuesTests(unittest.TestCase):
    def test_literal_dotenv_reads_only_five_keys_without_evaluating_other_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / '.env'
            path.write_text('OTHER=$(never-execute)\n' + '\n'.join(
                ('export ' if i == 0 else '') + key + ' = ' +
                ("'" + value + "' # comment" if i % 2 else value + ' # comment')
                for i, (key, value) in enumerate(VALUES.items())))
            self.assertEqual(VALUES, promotion.read_values(path))

    def test_missing_duplicate_alias_interpolation_and_malformed_quote_are_rejected(self):
        valid = '\n'.join(k + '=' + v for k, v in VALUES.items())
        first = promotion.KEYS[0]
        for index, bad in enumerate((valid.split('\n', 1)[1], valid + '\n' + first + '=https://example.invalid/',
                    valid.replace(first, first.lower()), valid.replace(first, first.replace('__', ':')),
                    valid.replace(VALUES[first], '${HOST}'), valid.replace(VALUES[first], "'unterminated"))):
            with self.subTest(case=index), tempfile.TemporaryDirectory() as directory:
                path = pathlib.Path(directory) / '.env'; path.write_text(bad)
                with self.assertRaises(ValueError): promotion.read_values(path)

    def test_url_partitions(self):
        for value in ('http://example.invalid/', 'https://example.invalid/feed?v=2'):
            self.assertTrue(promotion.valid_url(value))
        for value in ('', ' ', '/relative', 'ftp://example.invalid/', 'http://',
                      'https://example.invalid:99999/', 'https://example.invalid:0/',
                      'https://user:pass@example.invalid/', 'https://example.invalid/#fragment',
                      'https://example.invalid/\n', 'https://example.invalid/\\path', '${HOST}'):
            self.assertFalse(promotion.valid_url(value))

    def test_candidate_changes_exactly_five_keys_and_does_not_mutate_base(self):
        original = base(); saved = copy.deepcopy(original)
        proposed = promotion.candidate(original, VALUES)
        self.assertEqual(saved, original)
        for key in promotion.KEYS:
            self.assertEqual(VALUES[key], proposed['services']['api']['environment'].pop(key))
        self.assertEqual(original, proposed)

    def test_each_key_accepts_double_quoted_values_and_rejects_invalid_override(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / '.env'
            path.write_text('\n'.join(k + '="' + v + '"' for k, v in VALUES.items()))
            self.assertEqual(VALUES, promotion.read_values(path))
            for key in promotion.KEYS:
                values = dict(VALUES); values[key] = 'file:///tmp/feed'
                with self.subTest(key=key), self.assertRaises(ValueError): promotion.candidate(base(), values)

    def test_candidate_rejects_extra_key_alias_testing_and_command(self):
        for kind in ('extra', 'alias', 'testing', 'command'):
            original = base(); values = dict(VALUES)
            if kind == 'extra': values['GpsSources__BusPrimarySource'] = 'ITS_CURRENT'
            if kind == 'alias': original['services']['api']['environment']['itscurrent:baseurl'] = VALUES[promotion.KEYS[0]]
            if kind == 'testing': original['services']['api']['environment']['ASPNETCORE_ENVIRONMENT'] = 'Testing'
            if kind == 'command': original['services']['api']['command'] = ['custom']
            with self.subTest(kind=kind), self.assertRaises(ValueError): promotion.candidate(original, values)

    def test_radar_preserves_promoted_url_but_cannot_edit_it_through_radar_overlay(self):
        original = base(); original['services']['api']['environment']['Radar__Enabled'] = 'false'
        original['services']['api']['environment']['Radar__Push__BaseUrl'] = VALUES[promotion.KEYS[-1]]
        proposed = promotion.release.radar_candidate(original, {'services': {'api': {'environment': {}}}})
        self.assertEqual(original, proposed)
        with self.assertRaises(AssertionError):
            promotion.release.radar_candidate(original, {'services': {'api': {'environment': {
                'Radar__Push__BaseUrl': 'https://example.invalid/changed/'}}}})


class UrlPromotionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name); self.config = self.root / 'ci-active.json'
        self.original = base(); self.config.write_text(json.dumps(self.original))
        self.metadata = {'mode': 'ci', 'commit': 'a' * 40, 'configuration': str(self.config), 'image': IMAGE}
        (self.root / 'active.manual.json').write_text(json.dumps(self.metadata))
        (self.root / 'active.run').write_text('123')
        (self.root / 'deploy.lock').touch()
        self.env = self.root / '.env'; self.env.write_text('\n'.join(k + '=' + v for k, v in VALUES.items()))
        self.current = copy.deepcopy(self.original)
        self.calls = []
        self.stack = contextlib.ExitStack(); self.addCleanup(self.stack.close)
        lock = types.SimpleNamespace(flock=lambda *args: None, LOCK_EX=1, LOCK_NB=2)
        self.stack.enter_context(patch.dict(sys.modules, {'fcntl': lock}))
        self.stack.enter_context(patch.object(promotion.release, 'radar_private'))
        self.stack.enter_context(patch.object(promotion.release, 'inspect', side_effect=lambda: container(self.current)))
        self.stack.enter_context(patch.object(promotion.release, 'run', return_value=IMAGE))
        self.up = self.stack.enter_context(patch.object(promotion.release, 'up', side_effect=self.fake_up))
        self.health = self.stack.enter_context(patch.object(promotion.release, 'healthy'))
        self.stack.enter_context(patch.object(promotion, 'render', side_effect=copy.deepcopy))
        self.stack.enter_context(patch.object(promotion.signal, 'signal', return_value=promotion.signal.SIG_DFL))
        self.output = io.StringIO(); self.stack.enter_context(contextlib.redirect_stdout(self.output))
        # os.umask is process-global; restore it even when a failure is injected.
        import os
        previous = os.umask(0o077); os.umask(previous); self.addCleanup(os.umask, previous)

    def args(self, apply=False, rollback=None):
        return types.SimpleNamespace(state=str(self.root), env_file=str(self.env),
                                     apply=apply, check=False, rollback=rollback)

    def snapshot(self):
        return {str(p.relative_to(self.root)): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}

    def fake_up(self, path):
        self.calls.append(path)
        self.assertTrue((path.parent / 'previous.json').exists())
        self.assertTrue((path.parent / 'previous-metadata.json').exists())
        self.current = json.loads(path.read_bytes())

    def test_preflight_writes_nothing_and_prints_only_keys_and_status(self):
        saved = self.snapshot(); promotion.run(self.args())
        self.assertEqual(saved, self.snapshot()); self.up.assert_not_called(); self.health.assert_not_called()
        for key, value in VALUES.items():
            self.assertIn(key, self.output.getvalue()); self.assertNotIn(value, self.output.getvalue())
        self.assertNotIn('synthetic-only', self.output.getvalue())

    def test_apply_backs_up_first_preserves_image_services_and_ci_sequence(self):
        saved = self.snapshot(); promotion.run(self.args(apply=True))
        self.assertEqual(1, self.up.call_count); self.health.assert_called_once_with(IMAGE)
        metadata = json.loads((self.root / 'active.manual.json').read_bytes())
        operation = pathlib.Path(metadata['configuration']).parent
        self.assertEqual(saved['ci-active.json'], (operation / 'previous.json').read_bytes())
        self.assertEqual(saved['active.manual.json'], (operation / 'previous-metadata.json').read_bytes())
        self.assertEqual(saved['ci-active.json'], self.config.read_bytes())
        self.assertEqual(saved['active.run'], (self.root / 'active.run').read_bytes())
        self.assertEqual(saved['.env'], self.env.read_bytes())
        self.assertEqual(promotion.candidate(self.original, VALUES), self.current)
        self.assertEqual(IMAGE, self.current['services']['api']['image'])
        self.assertTrue((operation / 'applied.json').exists())

    def test_noop_apply_does_not_create_backup_or_restart(self):
        self.current = promotion.candidate(self.original, VALUES)
        self.config.write_text(json.dumps(self.current)); saved = self.snapshot()
        promotion.run(self.args(apply=True)); self.assertEqual(saved, self.snapshot()); self.up.assert_not_called()

    def test_health_failure_restores_full_configuration_and_metadata(self):
        saved = self.snapshot(); self.health.side_effect = [RuntimeError('synthetic'), None]
        with self.assertRaises(RuntimeError): promotion.run(self.args(apply=True))
        self.assertEqual(2, self.up.call_count); self.assertEqual(self.original, self.current)
        self.assertEqual(saved['active.manual.json'], (self.root / 'active.manual.json').read_bytes())
        self.assertIn('Rollback automático concluído', self.output.getvalue())

    def test_up_failure_restores_previous_configuration(self):
        def fail_first(path):
            self.fake_up(path)
            if len(self.calls) == 1: raise RuntimeError('synthetic up failure')
        self.up.side_effect = fail_first
        with self.assertRaises(RuntimeError): promotion.run(self.args(apply=True))
        self.assertEqual(2, self.up.call_count); self.assertEqual(self.original, self.current)
        self.assertEqual(self.metadata, json.loads((self.root / 'active.manual.json').read_bytes()))

    def test_metadata_write_failure_restores_previous_configuration_and_metadata(self):
        write = promotion.write_private
        failed = []
        def fail_once(path, content):
            if path.name == 'active.manual.json' and not failed:
                failed.append(True); raise OSError('synthetic write failure')
            write(path, content)
        with patch.object(promotion, 'write_private', side_effect=fail_once), self.assertRaises(OSError):
            promotion.run(self.args(apply=True))
        self.assertEqual(2, self.up.call_count); self.assertEqual(self.original, self.current)
        self.assertEqual(self.metadata, json.loads((self.root / 'active.manual.json').read_bytes()))

    def test_interruption_rolls_back(self):
        self.health.side_effect = [KeyboardInterrupt(), None]
        with self.assertRaises(KeyboardInterrupt): promotion.run(self.args(apply=True))
        self.assertEqual(2, self.up.call_count); self.assertEqual(self.original, self.current)

    def test_failed_rollback_is_reported_without_success_claim(self):
        self.health.side_effect = [RuntimeError('synthetic'), RuntimeError('synthetic rollback')]
        with self.assertRaises(RuntimeError): promotion.run(self.args(apply=True))
        self.assertIn('ROLLBACK NÃO CONFIRMADO', self.output.getvalue())
        self.assertNotIn('Rollback automático concluído', self.output.getvalue())

    def test_manual_rollback_preflight_and_apply_restore_absent_keys(self):
        promotion.run(self.args(apply=True))
        operation = pathlib.Path(json.loads((self.root / 'active.manual.json').read_bytes())['configuration']).parent
        saved = self.snapshot(); calls = self.up.call_count
        promotion.run(self.args(rollback=str(operation)))
        self.assertEqual(saved, self.snapshot()); self.assertEqual(calls, self.up.call_count)
        promotion.run(self.args(apply=True, rollback=str(operation)))
        self.assertEqual(self.original, self.current)
        self.assertEqual(self.metadata, json.loads((self.root / 'active.manual.json').read_bytes()))

    def test_manual_rollback_refuses_a_later_release(self):
        promotion.run(self.args(apply=True))
        metadata = json.loads((self.root / 'active.manual.json').read_bytes())
        operation = pathlib.Path(metadata['configuration']).parent
        metadata['commit'] = 'b' * 40; (self.root / 'active.manual.json').write_text(json.dumps(metadata))
        calls = self.up.call_count
        with self.assertRaises(ValueError): promotion.run(self.args(apply=True, rollback=str(operation)))
        self.assertEqual(calls, self.up.call_count)

    def test_next_ci_preserves_promoted_urls_without_rereading_dotenv(self):
        promotion.run(self.args(apply=True))
        promoted = copy.deepcopy(self.current['services']['api']['environment'])
        self.env.write_text('\n'.join(k + '=https://example.invalid/not-promoted/' for k in VALUES))
        image = 'ghcr.io/guilhermedesales/noponto-api@sha256:' + 'b' * 64
        args = types.SimpleNamespace(image=image, commit='b' * 40, state=str(self.root), sequence='124', check=False)
        def ci_up(path):
            self.current = json.loads(path.read_bytes())
        with patch.object(promotion.release, 'ROOT', self.root), patch.object(promotion.release, 'STATE', self.root), \
             patch.object(promotion.release, 'run', return_value='sha256:' + 'b' * 64), \
             patch.object(promotion.signal, 'SIGHUP', 1, create=True), \
             patch.object(promotion.release, 'up', side_effect=ci_up):
            promotion.release.ci_release(args)
        self.assertEqual(promoted, self.current['services']['api']['environment'])
        self.assertEqual(image, self.current['services']['api']['image'])
        self.assertEqual('124\n', (self.root / 'active.run').read_text())

    def test_runtime_drift_or_compose_interpolation_is_rejected_before_writes(self):
        self.current['services']['api']['environment']['EXTRA'] = 'synthetic'
        saved = self.snapshot()
        with self.assertRaises(ValueError): promotion.run(self.args(apply=True))
        self.assertEqual(saved, self.snapshot()); self.up.assert_not_called()

        self.current = copy.deepcopy(self.original)
        def bad_render(config):
            result = copy.deepcopy(config); result['services']['api']['environment']['PRIVATE_FIXTURE'] = ''
            return result
        with patch.object(promotion, 'render', side_effect=bad_render), self.assertRaises(ValueError):
            promotion.run(self.args(apply=True))
        self.assertEqual(saved, self.snapshot()); self.up.assert_not_called()

    def test_unavailable_image_reference_refuses_apply_before_backup(self):
        saved = self.snapshot()
        with patch.object(promotion.release, 'run', return_value='sha256:' + 'b' * 64), self.assertRaises(ValueError):
            promotion.run(self.args(apply=True))
        self.assertEqual(saved, self.snapshot()); self.up.assert_not_called()

    def test_render_rejects_non_url_service_changes(self):
        proposed = promotion.candidate(self.original, VALUES)
        proposed['services']['redis']['image'] = 'changed'
        with self.assertRaises(ValueError):
            promotion.validate_render(self.original, proposed, self.original['services']['api']['environment'])

    def test_lock_conflict_and_invalid_dotenv_do_not_apply(self):
        lock = types.SimpleNamespace(flock=lambda *a: (_ for _ in ()).throw(BlockingIOError()), LOCK_EX=1, LOCK_NB=2)
        saved = self.snapshot()
        with patch.dict(sys.modules, {'fcntl': lock}), self.assertRaises(BlockingIOError): promotion.run(self.args(apply=True))
        self.assertEqual(saved, self.snapshot()); self.up.assert_not_called()
        self.env.write_text('ItsCurrent__BaseUrl=/relative')
        saved = self.snapshot()
        with self.assertRaises(ValueError): promotion.run(self.args(apply=True))
        self.assertEqual(saved, self.snapshot()); self.up.assert_not_called()


if __name__ == '__main__':
    unittest.main()
