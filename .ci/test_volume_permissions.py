"""Regressões POSIX do preflight, executadas no runner sem Docker/root."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("check-volume-permissions.sh")


@unittest.skipUnless(os.name == "posix" and os.getuid() != 0, "Requer usuário POSIX não root")
class VolumePermissionsTests(unittest.TestCase):
    def check(self, *directories):
        return subprocess.run(
            ["sh", str(SCRIPT), *map(str, directories)],
            capture_output=True, text=True, check=False,
        )

    def test_writable_directories_leave_no_probes(self):
        with tempfile.TemporaryDirectory() as temporary:
            directories = [Path(temporary) / name for name in ("nuget", "workspace", "results", "ci home")]
            for directory in directories:
                directory.mkdir(mode=0o700)
            self.assertEqual(0, self.check(*directories).returncode)
            for directory in directories:
                self.assertEqual([], list(directory.iterdir()))

    def test_readonly_directory_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / "readonly"
            directory.mkdir(mode=0o500)
            try:
                self.assertEqual(1, self.check(directory).returncode)
            finally:
                directory.chmod(0o700)

    def test_missing_directory_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            self.assertEqual(1, self.check(Path(temporary) / "missing").returncode)

    def test_empty_directory_list_is_rejected(self):
        self.assertEqual(1, self.check().returncode)


if __name__ == "__main__":
    unittest.main()
