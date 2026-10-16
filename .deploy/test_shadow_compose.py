"""Real Compose rendering only: no pull/up, daemon, network or operational data."""
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent


class ShadowArtifactTests(unittest.TestCase):
    def test_transferred_tools_match_pinned_hashes(self):
        lines=(ROOT/'SHA256SUMS.shadow').read_text(encoding='utf-8').splitlines()
        self.assertEqual(3,len(lines))
        for line in lines:
            digest,name=line.split('  ')
            self.assertEqual(digest,hashlib.sha256((ROOT/name).read_bytes()).hexdigest(),name)


@unittest.skipUnless(shutil.which('docker'), 'Docker CLI/Compose not installed')
class ShadowComposeTests(unittest.TestCase):
    def test_both_modes_preserve_physical_other_environment_services_and_resources(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            baseline = {'services': {
                'api': {'image': 'example.invalid/fixture@sha256:'+'a'*64,
                        'environment': {'UNRELATED':'literal-$${UNSET}', 'ML__ETA__Enabled':'false',
                                        'EtaV2__Enabled':'false','EtaV2__ShadowEnabled':'false'},
                        'cpus':1,'mem_limit':'768m','pids_limit':256,
                        'volumes':[{'type':'bind','source':'/var/run/docker.sock','target':'/var/run/docker.sock'}]},
                'postgres':{'image':'example.invalid/fixture-postgres'},
                'redis':{'image':'example.invalid/fixture-redis'}}}
            (path/'base.json').write_text(json.dumps(baseline))
            (path/'gtfs.json').write_text(json.dumps({'services':{'api':{'environment':{'GtfsRealtimeGps__BusEnabled':'true'}}}}))
            command=['docker','compose','--project-name','shadow-fixture','--project-directory',str(path),
                     '-f',str(path/'base.json'),'-f',str(path/'gtfs.json'),'-f',str(ROOT/'physical-eta.override.yml')]
            def render(extra):
                result=subprocess.run(command+extra+['config','--format','json'],
                                      env=dict(os.environ,COMPOSE_DISABLE_ENV_FILE='1'),capture_output=True,check=True,timeout=30)
                return json.loads(result.stdout)
            original=render([])
            for mode in ('off','capture'):
                with self.subTest(mode=mode):
                    rendered=render(['-f',str(ROOT/f'historical-eta-shadow.{mode}.yml')])
                    normalized=copy.deepcopy(rendered)
                    api=normalized['services']['api']
                    settings={k:v for k,v in api['environment'].items() if k.startswith('HistoricalEtaShadow__')}
                    self.assertEqual(mode=='capture',settings['HistoricalEtaShadow__Enabled']=='true')
                    self.assertEqual('false',settings['HistoricalEtaShadow__MlEnabled'])
                    self.assertEqual('false',settings['HistoricalEtaShadow__HistoricalEnabled'])
                    for key in settings: del api['environment'][key]
                    mounts=[v for v in api['volumes'] if v['target']=='/var/lib/noponto-shadow']
                    self.assertEqual(1 if mode=='capture' else 0,len(mounts))
                    if mounts:
                        # Compose-go omits false defaults from its canonical JSON.
                        self.assertFalse(mounts[0].get('bind',{}).get('create_host_path',False))
                        mounts[0].setdefault('bind',{}).pop('create_host_path',None)
                        self.assertEqual({'type':'bind','source':'/home/guilherme/.noponto-deploy/shadow-capture-v2',
                                          'target':'/var/lib/noponto-shadow','bind':{}},mounts[0])
                    api['volumes']=[v for v in api['volumes'] if v not in mounts]
                    self.assertEqual(original,normalized)


if __name__ == '__main__':
    unittest.main()
