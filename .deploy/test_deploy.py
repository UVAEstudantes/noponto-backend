"""Linux-only shell regressions. All Docker/HTTP/SSH operations are fakes."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent
PREVIOUS = 'sha256:' + 'a' * 64
CANDIDATE_ID = 'sha256:' + 'b' * 64
CANDIDATE = 'ghcr.io/guilhermedesales/noponto-api@sha256:' + 'c' * 64
FAKE = r'''#!/usr/bin/env python3
import json,os,sys
from pathlib import Path
p=Path(os.environ['FIXTURE']); args=sys.argv[1:]; tool=Path(sys.argv[0]).name
with (p/'calls').open('a') as f: f.write(json.dumps([tool,*args])+'\n')
previous='sha256:'+'a'*64; candidate='sha256:'+'b'*64
current=(p/'image').read_text()
if tool=='mv':
 target=Path(args[-1]); failure=os.environ.get('FAIL','')
 if failure=='persist-'+target.suffix.lstrip('.') and not (p/'persist-failed').exists():
  (p/'persist-failed').touch(); sys.exit(1)
 if target.name=='active.run':
  (p/'env-at-run-commit').write_text((target.parent/'active.env').read_text())
 os.replace(args[-2],args[-1]); sys.exit(0)
if tool=='sleep': sys.exit(0)
if tool=='curl':
 print('503' if os.environ.get('FAIL') in ('boot','rollback') and current==candidate or os.environ.get('FAIL')=='rollback' else '200',end=''); sys.exit(0)
if args[0]=='inspect':
 fmt=args[2]
 if 'Labels' in fmt: print('noponto/api')
 elif 'Running' in fmt: print(current+'/true/false/0')
 else: print(current)
elif args[:2]==['image','inspect']: print(candidate)
elif args[:2]==['image','tag']: pass
elif args[0]=='pull':
 active=p/'state/active.env'
 (p/'env-at-pull').write_text(active.read_text() if active.exists() else 'absent')
 sys.exit(1 if os.environ.get('FAIL')=='pull' else 0)
elif args[0]=='compose':
 image=os.environ['GTFSRT_API_IMAGE']
 if 'config' in args:
  env={'ASPNETCORE_ENVIRONMENT':'Production','GpsSources__BusPrimarySource':'GTFSRT_BUS','GpsSources__BrtPrimarySource':'GTFSRT_BRT','ML__ETA__Enabled':'false','EtaV2__Enabled':'false','EtaV2__ShadowEnabled':'false','GtfsRealtimeGps__BusEnabled':'true','GtfsRealtimeGps__BrtEnabled':'true','GtfsRealtimeGps__CrosswalkValidated':'true'}
  if os.environ.get('FAIL')=='config': env['ASPNETCORE_ENVIRONMENT']='Development'
  print(json.dumps({'services':{'api':{'image':image,'environment':env,'cpus':1.0,'mem_limit':'805306368','pids_limit':256}}}))
 elif 'up' in args:
  project=Path(args[args.index('--project-directory')+1])
  base=Path(args[args.index('-f')+1])
  for line in base.read_text().splitlines():
   if line.startswith('relative-file: '):
    if not (project/line.split(': ',1)[1]).is_file(): sys.exit(98)
  (p/'image').write_text(previous if image.startswith('noponto-api:rollback-') else candidate)
  if os.environ.get('FAIL')=='up' and image.startswith('ghcr.io'): sys.exit(1)
 else: sys.exit(99)
else: sys.exit(99)
'''


@unittest.skipUnless(os.name == 'posix', 'requires bash/flock on Linux')
class DeployTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name)
        binaries = self.path / 'bin'
        binaries.mkdir()
        for name in ('docker', 'curl', 'sleep', 'mv'):
            file = binaries / name
            file.write_text(FAKE)
            file.chmod(0o700)
        (self.path / 'image').write_text(PREVIOUS)
        for name in ('base.yml', 'overlay.yml', 'runtime.env'):
            (self.path / name).write_text('fixture-only\n')
        self.env = dict(os.environ, PATH=str(binaries)+':'+os.environ['PATH'],
                        FIXTURE=str(self.path), NOPONTO_BASE_COMPOSE=str(self.path/'base.yml'),
                        NOPONTO_OVERLAY_COMPOSE=str(self.path/'overlay.yml'), NOPONTO_HEALTH_TIMEOUT='10')

    def run_deploy(self, failure='', candidate=CANDIDATE, sequence='109'):
        self.env['FAIL'] = failure
        return subprocess.run(['bash', str(ROOT/'deploy-api.sh'), candidate, sequence,
                               str(self.path/'runtime.env'), str(self.path/'state')],
                              env=self.env, text=True, capture_output=True, timeout=45)

    def calls(self):
        return [json.loads(x) for x in (self.path/'calls').read_text().splitlines()]

    def test_success_persists_digest_and_preserves_scope(self):
        result = self.run_deploy()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('GTFSRT_API_IMAGE='+CANDIDATE+'\n', (self.path/'state/active.env').read_text())
        calls = self.calls()
        ups = [c for c in calls if c[1:2]==['compose'] and 'up' in c]
        self.assertEqual(1, len(ups))
        self.assertEqual(['up','-d','--no-deps','--no-build','--pull','never','api'], ups[0][-7:])
        self.assertIn(str(self.path/'base.yml'), ups[0])
        self.assertIn(str(self.path/'overlay.yml'), ups[0])
        self.assertFalse(any(x in ('down','restart','rm','prune') for c in calls for x in c))
        self.assertNotEqual(0, self.run_deploy(sequence='108').returncode)

    def test_invalid_digest_does_not_call_docker(self):
        self.assertNotEqual(0, self.run_deploy(candidate='ghcr.io/guilhermedesales/noponto-api:latest').returncode)
        self.assertFalse((self.path/'calls').exists())

    def test_invalid_config_never_replaces_api(self):
        self.assertNotEqual(0, self.run_deploy('config').returncode)
        self.assertFalse(any('up' in c for c in self.calls()))

    def test_pull_failure_keeps_previous(self):
        self.assertNotEqual(0, self.run_deploy('pull').returncode)
        self.assertEqual(PREVIOUS, (self.path/'image').read_text())
        self.assertFalse(any('up' in c for c in self.calls()))
        self.assertFalse((self.path/'state/active.env').exists())
        self.assertEqual('absent', (self.path/'env-at-pull').read_text())

    def seed_active(self):
        state = self.path/'state'
        state.mkdir()
        prior = 'GTFSRT_API_IMAGE=ghcr.io/guilhermedesales/noponto-api@'+PREVIOUS+'\n'
        (state/'active.env').write_text(prior)
        (state/'active.run').write_text('108\n')
        return prior

    def test_pull_failure_preserves_existing_state(self):
        prior = self.seed_active()
        self.assertNotEqual(0, self.run_deploy('pull').returncode)
        self.assertEqual(prior, (self.path/'state/active.env').read_text())
        self.assertEqual(prior, (self.path/'env-at-pull').read_text())
        self.assertEqual('108\n', (self.path/'state/active.run').read_text())

    def test_staging_write_failures_do_not_replace_api_or_state(self):
        prior = self.seed_active()
        for name in ('active.env.tmp', 'active.run.tmp'):
            with self.subTest(name=name):
                obstruction = self.path/'state'/name
                # O estágio anterior pode ter deixado o outro arquivo temporário.
                if obstruction.exists(): obstruction.unlink()
                obstruction.mkdir()
                self.assertNotEqual(0, self.run_deploy().returncode)
                obstruction.rmdir()
                self.assertEqual(PREVIOUS, (self.path/'image').read_text())
                self.assertEqual(prior, (self.path/'state/active.env').read_text())
                self.assertEqual('108\n', (self.path/'state/active.run').read_text())
                self.assertFalse(any('up' in c for c in self.calls()))

    def test_persistence_failures_restore_state_and_allow_same_sequence_retry(self):
        prior = self.seed_active()
        for failure in ('persist-env', 'persist-run'):
            with self.subTest(failure=failure):
                failed = self.path/'persist-failed'
                if failed.exists(): failed.unlink()
                result = self.run_deploy(failure)
                self.assertNotEqual(0, result.returncode)
                self.assertIn('Rollback confirmado', result.stderr)
                self.assertEqual(PREVIOUS, (self.path/'image').read_text())
                self.assertEqual(prior, (self.path/'state/active.env').read_text())
                self.assertEqual('108\n', (self.path/'state/active.run').read_text())
        retry = self.run_deploy()
        self.assertEqual(0, retry.returncode, retry.stderr)
        self.assertEqual('109\n', (self.path/'state/active.run').read_text())
        self.assertEqual('GTFSRT_API_IMAGE='+CANDIDATE+'\n', (self.path/'env-at-run-commit').read_text())

    def test_persistence_failure_first_deploy_does_not_create_run_marker(self):
        result = self.run_deploy('persist-run')
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(PREVIOUS, (self.path/'image').read_text())
        self.assertFalse((self.path/'state/active.run').exists())
        self.assertIn('rollback-', (self.path/'state/active.env').read_text())
        self.assertEqual(0, self.run_deploy().returncode)

    def test_rollback_snapshots_keep_original_project_directory_for_relative_paths(self):
        relative = self.path/'relative'
        relative.mkdir()
        (relative/'marker').write_text('fixture')
        (self.path/'base.yml').write_text('relative-file: ./relative/marker\n')
        result = self.run_deploy('up')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Rollback confirmado', result.stderr)
        ups = [c for c in self.calls() if c[1:2]==['compose'] and 'up' in c]
        self.assertEqual(2, len(ups))
        for call in ups:
            self.assertEqual(str(self.path), call[call.index('--project-directory')+1])
        self.assertNotEqual(str(self.path), str(self.path/'state/previous'))

    def test_up_failure_rolls_back(self):
        result = self.run_deploy('up')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Rollback confirmado', result.stderr)
        self.assertEqual(PREVIOUS, (self.path/'image').read_text())
        self.assertIn('rollback-', (self.path/'state/active.env').read_text())
        ups = [c for c in self.calls() if 'up' in c]
        self.assertIn(str(self.path/'state/previous/base.yml'), ups[-1])
        self.assertIn(str(self.path/'state/previous/runtime.env'), ups[-1])

    def test_boot_failure_rolls_back(self):
        result = self.run_deploy('boot')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Rollback confirmado', result.stderr)

    def test_rollback_failure_is_explicit(self):
        result = self.run_deploy('rollback')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('ROLLBACK FALHOU', result.stderr)

    def test_concurrent_deploy_is_rejected(self):
        import fcntl
        state = self.path/'state'
        state.mkdir()
        with (state/'deploy.lock').open('w') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.assertNotEqual(0, self.run_deploy().returncode)
        self.assertFalse((self.path/'calls').exists())

    def test_ssh_launcher_preserves_host_verification_and_cleans_key(self):
        ssh = self.path/'bin/ssh'
        ssh.write_text('#!/usr/bin/env python3\nimport os,sys,json\nfrom pathlib import Path\n'
                       'args=sys.argv[1:]; key=Path(args[args.index("-i")+1]); '
                       'assert key.stat().st_mode & 0o777 == 0o600\n'
                       'assert "StrictHostKeyChecking=yes" in args\n'
                       'assert "BatchMode=yes" in args\n'
                       'data=sys.stdin.read(); assert "flock -n 9" in data\n'
                       'Path(os.environ["FIXTURE"],"ssh-key-path").write_text(str(key))\n')
        ssh.chmod(0o700)
        env = dict(self.env, IMAGE_DIGEST='sha256:'+'c'*64, RELEASE_SEQUENCE='109',
                   SERVER_TAILSCALE_IP='100.64.0.1', SERVER_USER='fixture',
                   SERVER_SSH_KEY='fake-only', SERVER_SSH_KNOWN_HOSTS='fake-only',
                   NOPONTO_DEPLOY_ENV_FILE='/fixture/runtime.env', NOPONTO_DEPLOY_STATE_DIR='/fixture/state')
        result = subprocess.run(['bash', str(ROOT/'ssh-deploy.sh')], env=env,
                                cwd=ROOT.parent, text=True, capture_output=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(Path((self.path/'ssh-key-path').read_text()).exists())
        env['NOPONTO_DEPLOY_ENV_FILE']='/fixture/$(touch injected)'
        result = subprocess.run(['bash', str(ROOT/'ssh-deploy.sh')], env=env,
                                cwd=ROOT.parent, capture_output=True, timeout=10)
        self.assertNotEqual(0, result.returncode)


if __name__ == '__main__':
    unittest.main()
