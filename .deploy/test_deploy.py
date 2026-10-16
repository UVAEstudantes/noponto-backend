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
 if 'Config.Env' in fmt: print(json.dumps([k+'='+v for k,v in json.loads((p/'running-shadow').read_text()).get('environment',{}).items()]))
 elif 'Mounts' in fmt: print(json.dumps([{'Type':'bind','Source':v['source'],'Destination':v['target'],'RW':True} for v in json.loads((p/'running-shadow').read_text()).get('volumes',[])]))
 elif 'Labels' in fmt: print('noponto/api')
 elif 'Running' in fmt: print(current+'/true/false/0')
 else: print(current)
elif args[:2]==['image','inspect']: print(candidate)
elif args[:2]==['image','tag']: (p/'tagged-previous').write_text(current)
elif args[0]=='pull':
 active=p/'state/active.env'
 (p/'env-at-pull').write_text(active.read_text() if active.exists() else 'absent')
 sys.exit(1 if os.environ.get('FAIL')=='pull' else 0)
elif args[0]=='compose':
 image=os.environ['GTFSRT_API_IMAGE']
 if 'config' in args:
  env={'ASPNETCORE_ENVIRONMENT':'Production','GpsSources__BusPrimarySource':'GTFSRT_BUS','GpsSources__BrtPrimarySource':'GTFSRT_BRT','ML__ETA__Enabled':'false','EtaV2__Enabled':'false','EtaV2__ShadowEnabled':'false','GtfsRealtimeGps__BusEnabled':'true','GtfsRealtimeGps__BrtEnabled':'true','GtfsRealtimeGps__CrosswalkValidated':'true'}
  if os.environ.get('FAIL')=='config': env['ASPNETCORE_ENVIRONMENT']='Development'
  services={'api':{'image':image,'environment':env,'cpus':1.0,'mem_limit':'805306368','pids_limit':256},'postgres':{'image':'fixture-postgres'},'redis':{'image':'fixture-redis'}}
  files=[args[i+1] for i,x in enumerate(args) if x=='-f']
  if len(files)>=3:
   env.update(json.loads(Path(files[2]).read_text()))
   if os.environ.get('FAIL')=='extra-service': services['redis']['image']='forbidden'
   if os.environ.get('FAIL')=='extra-env': env['UNRELATED']='forbidden'
  if len(files)==4:
   shadow=json.loads(Path(files[3]).read_text())['services']['api']
   env.update(shadow.get('environment',{}))
   if 'volumes' in shadow: services['api']['volumes']=shadow['volumes']
   if os.environ.get('FAIL')=='shadow-extra-env': env['UNRELATED']='SECRET_NOT_TO_PRINT'
   if os.environ.get('FAIL')=='shadow-extra-service': services['redis']['image']='forbidden'
  print(json.dumps({'services':services}))
 elif 'up' in args:
  project=Path(args[args.index('--project-directory')+1])
  base=Path(args[args.index('-f')+1])
  for line in base.read_text().splitlines():
   if line.startswith('relative-file: '):
    if not (project/line.split(': ',1)[1]).is_file(): sys.exit(98)
  files=[args[i+1] for i,x in enumerate(args) if x=='-f']
  physical=json.loads(Path(files[2]).read_text())
  assert physical=={'PhysicalEta__Enabled':'true','PhysicalEta__MaxAgeSeconds':'90'}
  if os.environ.get('FAIL')=='mutate-override' and image.startswith('ghcr.io'):
   (p/'physical-eta.override.yml').write_text('tampered')
   sys.exit(1)
  shadow=json.loads(Path(files[3]).read_text())['services']['api']
  (p/'running-shadow').write_text(json.dumps(shadow))
  if os.environ.get('FAIL')=='runtime-shadow' and image.startswith('ghcr.io'):
   shadow['environment']['HistoricalEtaShadow__MlEnabled']='true'
   (p/'running-shadow').write_text(json.dumps(shadow))
  if os.environ.get('FAIL')=='mutate-shadow' and image.startswith('ghcr.io'):
   Path(files[3]).write_text('tampered')
   (p/'image').write_text(candidate); sys.exit(1)
  (p/'image').write_text((p/'tagged-previous').read_text() if image.startswith('noponto-api:rollback-') else candidate)
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
        (self.path/'running-shadow').write_text('{}')
        (self.path/'shadow.override.yml').write_bytes((ROOT/'historical-eta-shadow.off.yml').read_bytes())
        for name in ('base.yml', 'overlay.yml', 'runtime.env'):
            (self.path / name).write_text('fixture-only\n')
        (self.path/'physical-eta.override.yml').write_text(json.dumps({'PhysicalEta__Enabled':'true','PhysicalEta__MaxAgeSeconds':'90'}))
        self.env = dict(os.environ, PATH=str(binaries)+':'+os.environ['PATH'],
                        FIXTURE=str(self.path), NOPONTO_BASE_COMPOSE=str(self.path/'base.yml'),
                        NOPONTO_OVERLAY_COMPOSE=str(self.path/'overlay.yml'), NOPONTO_HEALTH_TIMEOUT='10',
                        NOPONTO_SHADOW_OVERRIDE=str(self.path/'managed-shadow.yml'))
        (self.path/'managed-shadow.yml').write_bytes((ROOT/'historical-eta-shadow.off.yml').read_bytes())

    def run_deploy(self, failure='', candidate=CANDIDATE, sequence='109', check=False, configure=False):
        self.env['FAIL'] = failure
        return subprocess.run(['bash', str(ROOT/'deploy-api.sh'), candidate, sequence,
                               str(self.path/'runtime.env'), str(self.path/'state'),
                               str(self.path/'physical-eta.override.yml'),str(self.path/'shadow.override.yml')]+(['--check'] if check else ['--configure-shadow'] if configure else []),
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
        self.assertIn(str(self.path/'physical-eta.override.yml'), ups[0])
        self.assertEqual((self.path/'physical-eta.override.yml').read_bytes(),(self.path/'state/previous/physical-eta.override.yml').read_bytes())
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

    def test_preflight_is_read_only_and_accepts_same_sequence(self):
        self.seed_active()
        result=self.run_deploy(sequence='108',check=True)
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertFalse((self.path/'state/deploy.lock').exists())
        self.assertFalse((self.path/'state/previous').exists())
        self.assertFalse(any('up' in c or 'pull' in c or 'tag' in c for c in self.calls()))
        self.assertEqual('108\n',(self.path/'state/active.run').read_text())

    def test_preflight_does_not_create_state(self):
        self.assertEqual(0,self.run_deploy(check=True).returncode)
        self.assertFalse((self.path/'state').exists())

    def test_physical_override_required_and_exact_policy(self):
        physical=self.path/'physical-eta.override.yml'
        for settings in ({'PhysicalEta__Enabled':'false','PhysicalEta__MaxAgeSeconds':'90'},
                         {'PhysicalEta__Enabled':'true','PhysicalEta__MaxAgeSeconds':'30'}):
            physical.write_text(json.dumps(settings))
            self.assertNotEqual(0,self.run_deploy().returncode)
            self.assertFalse(any('up' in c or 'pull' in c for c in self.calls()))
        physical.unlink()
        self.assertNotEqual(0,self.run_deploy().returncode)

    def test_override_cannot_change_other_services_or_environment(self):
        for reason in ('extra-service','extra-env'):
            self.assertNotEqual(0,self.run_deploy(reason).returncode)
            self.assertFalse(any('up' in c or 'pull' in c for c in self.calls()))

    def test_rollback_uses_frozen_physical_override_despite_original_mutation(self):
        result=self.run_deploy('mutate-override')
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Rollback confirmado',result.stderr)
        ups=[c for c in self.calls() if 'up' in c]
        self.assertIn(str(self.path/'state/previous/physical-eta.override.yml'),ups[-1])
        self.assertEqual(PREVIOUS,(self.path/'image').read_text())

    def shadow(self, capture=False):
        value=json.loads((ROOT/('historical-eta-shadow.capture.yml' if capture else 'historical-eta-shadow.off.yml')).read_text())
        if capture:
            directory=self.path/'state/shadow-capture-v2'
            directory.mkdir(parents=True,exist_ok=True,mode=0o700)
            directory.chmod(0o700)
            value['services']['api']['volumes'][0]['source']=str(directory)
        (self.path/'shadow.override.yml').write_text(json.dumps(value))
        return value['services']['api']

    def test_capture_only_success_and_private_snapshot(self):
        candidate=self.shadow(True)
        result=self.run_deploy()
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertEqual(candidate,json.loads((self.path/'running-shadow').read_text()))
        previous=self.path/'state/previous/shadow.override.json'
        returned=json.loads(previous.read_text())['services']['api']
        self.assertEqual('false',returned['environment']['HistoricalEtaShadow__Enabled'])
        self.assertNotIn('volumes',returned)
        self.assertEqual(0o600,previous.stat().st_mode & 0o777)
        ups=[c for c in self.calls() if 'up' in c]
        self.assertEqual(1,len(ups))
        self.assertEqual('api',ups[0][-1])

    def test_first_capture_failure_returns_absent_shadow_not_requested_on(self):
        self.shadow(True)
        result=self.run_deploy('up')
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Rollback confirmado',result.stderr)
        returned=json.loads((self.path/'running-shadow').read_text())
        self.assertEqual('false',returned['environment']['HistoricalEtaShadow__Enabled'])
        self.assertNotIn('volumes',returned)
        self.assertEqual(PREVIOUS,(self.path/'image').read_text())

    def test_rollback_preserves_actual_previous_mode_in_both_directions(self):
        for previous_on in (False,True):
            with self.subTest(previous_on=previous_on):
                prior=self.shadow(previous_on)
                (self.path/'running-shadow').write_text(json.dumps(prior))
                self.shadow(not previous_on)
                result=self.run_deploy('up')
                self.assertIn('Rollback confirmado',result.stderr)
                self.assertEqual(prior,json.loads((self.path/'running-shadow').read_text()))

    def test_rollback_does_not_reread_mutated_candidate_shadow_file(self):
        self.shadow(True)
        result=self.run_deploy('mutate-shadow')
        self.assertIn('Rollback confirmado',result.stderr)
        self.assertEqual('false',json.loads((self.path/'running-shadow').read_text())['environment']['HistoricalEtaShadow__Enabled'])

    def test_shadow_rejects_inference_partial_modes_fixture_and_unsafe_limits(self):
        invalid={'MlEnabled':'true','HistoricalEnabled':'true','OfflineExport__Fixture':'true',
                 'QueueCapacity':'1000','BatchSize':'200','SinkCapacity':'500','TimeoutMs':'1000',
                 'MaxAgeSeconds':'300','OfflineExport__QueueCapacity':'1024',
                 'OfflineExport__MaxFileBytes':'8388608','OfflineExport__MaxTotalBytes':'268435456',
                 'OfflineExport__MaxFiles':'256','OfflineExport__RetentionHours':'168',
                 'OfflineExport__DirectoryPath':'/tmp/public','Enabled':'true','OfflineExport__Enabled':'true'}
        for key,value in invalid.items():
            with self.subTest(key=key):
                api=self.shadow()
                api['environment']['HistoricalEtaShadow__'+key]=value
                (self.path/'shadow.override.yml').write_text(json.dumps({'services':{'api':api}}))
                result=self.run_deploy(check=True)
                self.assertNotEqual(0,result.returncode)
        self.assertFalse(any('up' in c or 'pull' in c for c in self.calls()))

    def test_shadow_cannot_change_other_services_environment_or_hide_aliases(self):
        for failure in ('shadow-extra-service','shadow-extra-env'):
            result=self.run_deploy(failure,check=True)
            self.assertNotEqual(0,result.returncode)
            self.assertNotIn('SECRET_NOT_TO_PRINT',result.stderr+result.stdout)
        api=self.shadow()
        api['environment']['historicaletashadow:mlenabled']='true'
        (self.path/'shadow.override.yml').write_text(json.dumps({'services':{'api':api}}))
        self.assertNotEqual(0,self.run_deploy(check=True).returncode)

    def test_capture_refuses_missing_public_or_symlinked_directory(self):
        api=self.shadow(True)
        directory=Path(api['volumes'][0]['source'])
        directory.chmod(0o755)
        self.assertNotEqual(0,self.run_deploy(check=True).returncode)
        directory.rmdir()
        self.assertNotEqual(0,self.run_deploy(check=True).returncode)
        other=self.path/'private'; other.mkdir(mode=0o700)
        directory.symlink_to(other,target_is_directory=True)
        self.assertNotEqual(0,self.run_deploy(check=True).returncode)

    def test_capture_refuses_extra_mount_or_automatic_host_creation(self):
        for field,value in (('read_only',True),('source','/tmp/untrusted')):
            api=self.shadow(True); api['volumes'][0][field]=value
            (self.path/'shadow.override.yml').write_text(json.dumps({'services':{'api':api}}))
            self.assertNotEqual(0,self.run_deploy(check=True).returncode)
        api=self.shadow(True); api['volumes'][0]['bind']['create_host_path']=True
        (self.path/'shadow.override.yml').write_text(json.dumps({'services':{'api':api}}))
        self.assertNotEqual(0,self.run_deploy(check=True).returncode)

    def test_unknown_running_inference_or_mount_blocks_before_pull(self):
        for prior in ({'environment':{'HistoricalEtaShadow__MlEnabled':'true'}},
                      {'volumes':[{'type':'bind','source':'/untrusted','target':'/var/lib/noponto-shadow'}]}):
            (self.path/'running-shadow').write_text(json.dumps(prior))
            self.assertNotEqual(0,self.run_deploy().returncode)
        self.assertFalse(any('up' in c or 'pull' in c for c in self.calls()))

    def test_configure_shadow_requires_same_active_digest_sequence_and_local_image(self):
        self.shadow(True)
        self.assertNotEqual(0,self.run_deploy(sequence='108',configure=True).returncode)
        (self.path/'state/active.run').write_text('108\n')
        (self.path/'state/active.env').write_text('GTFSRT_API_IMAGE='+CANDIDATE+'\n')
        self.assertNotEqual(0,self.run_deploy(sequence='109',configure=True).returncode)
        self.assertNotEqual(0,self.run_deploy(sequence='108',configure=True).returncode)
        (self.path/'image').write_text(CANDIDATE_ID)
        result=self.run_deploy(sequence='108',configure=True)
        self.assertEqual(0,result.returncode,result.stderr)
        self.assertEqual('108\n',(self.path/'state/active.run').read_text())
        self.assertFalse(any('pull' in c for c in self.calls()))
        self.assertEqual((self.path/'shadow.override.yml').read_bytes(),(self.path/'managed-shadow.yml').read_bytes())

    def test_configure_failure_restores_off_and_release_sequence(self):
        self.shadow(True)
        (self.path/'state/active.run').write_text('108\n')
        (self.path/'state/active.env').write_text('GTFSRT_API_IMAGE='+CANDIDATE+'\n')
        (self.path/'image').write_text(CANDIDATE_ID)
        result=self.run_deploy('up',sequence='108',configure=True)
        self.assertIn('Rollback confirmado',result.stderr)
        self.assertEqual('false',json.loads((self.path/'running-shadow').read_text())['environment']['HistoricalEtaShadow__Enabled'])
        self.assertEqual('108\n',(self.path/'state/active.run').read_text())
        self.assertFalse(any('pull' in c for c in self.calls()))
        self.assertEqual('false',json.loads((self.path/'managed-shadow.yml').read_text())['services']['api']['environment']['HistoricalEtaShadow__Enabled'])

    def test_configure_persistence_failure_restores_active_file_and_actual_mode(self):
        self.shadow(True)
        original=(self.path/'managed-shadow.yml').read_bytes()
        (self.path/'state/active.run').write_text('108\n')
        (self.path/'state/active.env').write_text('GTFSRT_API_IMAGE='+CANDIDATE+'\n')
        (self.path/'image').write_text(CANDIDATE_ID)
        result=self.run_deploy('persist-env',sequence='108',configure=True)
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Rollback confirmado',result.stderr)
        self.assertEqual(original,(self.path/'managed-shadow.yml').read_bytes())
        self.assertEqual('false',json.loads((self.path/'running-shadow').read_text())['environment']['HistoricalEtaShadow__Enabled'])
        self.assertEqual('108\n',(self.path/'state/active.run').read_text())
        self.assertEqual(CANDIDATE_ID,(self.path/'image').read_text())

    def test_runtime_shadow_divergence_is_detected_and_rolled_back(self):
        self.shadow(True)
        result=self.run_deploy('runtime-shadow')
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Estado Shadow do container diverge',result.stderr)
        self.assertIn('Rollback confirmado',result.stderr)
        self.assertEqual('false',json.loads((self.path/'running-shadow').read_text())['environment']['HistoricalEtaShadow__Enabled'])

    def test_python_optimization_cannot_disable_safety_gates(self):
        self.env['PYTHONOPTIMIZE']='1'
        self.assertNotEqual(0,self.run_deploy('shadow-extra-env',check=True).returncode)

    def test_configure_refuses_drift_between_managed_file_and_running_mode(self):
        self.shadow(True)
        (self.path/'managed-shadow.yml').write_bytes((self.path/'shadow.override.yml').read_bytes())
        (self.path/'state/active.run').write_text('108\n')
        (self.path/'state/active.env').write_text('GTFSRT_API_IMAGE='+CANDIDATE+'\n')
        (self.path/'image').write_text(CANDIDATE_ID)
        self.assertNotEqual(0,self.run_deploy(sequence='108',configure=True).returncode)
        self.assertFalse(any('up' in c or 'pull' in c for c in self.calls()))

    def test_ssh_launcher_preserves_host_verification_and_cleans_key(self):
        ssh = self.path/'bin/ssh'
        ssh.write_text('#!/usr/bin/env python3\nimport os,sys,json\nfrom pathlib import Path\n'
                       'args=sys.argv[1:]; key=Path(args[args.index("-i")+1]); '
                       'assert key.stat().st_mode & 0o777 == 0o600\n'
                       'assert "StrictHostKeyChecking=yes" in args\n'
                       'assert "BatchMode=yes" in args\n'
                       'assert args[-1].endswith(" /fixture/state/historical-eta-shadow.override.yml")\n'
                       'data=sys.stdin.read(); assert "flock -n 9" in data\n'
                       'Path(os.environ["FIXTURE"],"ssh-key-path").write_text(str(key))\n')
        ssh.chmod(0o700)
        env = dict(self.env, IMAGE_DIGEST='sha256:'+'c'*64, RELEASE_SEQUENCE='109',
                   SERVER_TAILSCALE_IP='100.64.0.1', SERVER_USER='fixture',
                   SERVER_SSH_KEY='fake-only', SERVER_SSH_KNOWN_HOSTS='fake-only',
                   NOPONTO_DEPLOY_ENV_FILE='/fixture/runtime.env', NOPONTO_DEPLOY_STATE_DIR='/fixture/state',
                   NOPONTO_PHYSICAL_ETA_OVERRIDE='/fixture/physical-eta.override.yml',NOPONTO_SHADOW_OVERRIDE='')
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
