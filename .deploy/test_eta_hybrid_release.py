import importlib.util
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('release',pathlib.Path(__file__).with_name('eta-hybrid-release.py'))
release=importlib.util.module_from_spec(spec);spec.loader.exec_module(release)

class HybridReleaseTests(unittest.TestCase):
    def test_up_only_recreates_api_without_build_pull_or_dependencies(self):
        with patch.object(release,'run') as run:
            release.up(pathlib.Path('/private/config.json'))
        args=run.call_args.args[0]
        self.assertEqual(['up','-d','--no-deps','--no-build','--pull','never','api'],args[-7:])

    def test_health_requires_three_successes_of_exact_image_without_oom_restart(self):
        good={'Image':'candidate','State':{'Running':True,'OOMKilled':False},'RestartCount':0}
        bad={'Image':'previous','State':{'Running':True,'OOMKilled':False},'RestartCount':0}
        with patch.object(release,'inspect',side_effect=[bad,good,good,good]) as inspect, patch.object(release,'run',return_value='200'), patch.object(release.time,'sleep'):
            release.healthy('candidate')
        self.assertEqual(4,inspect.call_count)

    def test_health_rejects_restart_even_with_http_success(self):
        c={'Image':'candidate','State':{'Running':True,'OOMKilled':False},'RestartCount':1}
        with patch.object(release,'inspect',return_value=c), patch.object(release,'run',return_value='200'), patch.object(release.time,'sleep'):
            with self.assertRaises(RuntimeError):release.healthy('candidate')

if __name__=='__main__':unittest.main()
