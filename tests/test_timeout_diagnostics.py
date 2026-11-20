from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from launch_isolated import snapshot_owned
from two_instance_test import wait_client


class TimeoutDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.parent = Path(__file__).resolve().parents[1] / "local/test-tools"
        self.parent.mkdir(parents=True, exist_ok=True)

    def test_snapshot_includes_only_exact_owned_token_and_no_secrets(self):
        with tempfile.TemporaryDirectory(dir=self.parent) as directory:
            root = Path(directory)
            for pid, token in ((12, "abc"), (13, "abcd"), (14, "unrelated")):
                entry = root / str(pid)
                entry.mkdir()
                (entry / "environ").write_bytes(
                    ("GMP_LAUNCH_TOKEN=" + token + "\0SECRET=private\0").encode())
                (entry / "status").write_text("Name:\tGoing Under.exe\nPPid:\t1\nState:\tS (sleeping)\n")
                (entry / "wchan").write_text("futex_wait")
            result = snapshot_owned("abc", root)
            self.assertEqual([item["pid"] for item in result], [12])
            self.assertEqual(result[0]["wait_channel"], "futex_wait")
            self.assertNotIn("private", str(result))
            self.assertNotIn("abc", str(result))

    def test_timeout_captures_before_cleanup_and_is_still_failure(self):
        with tempfile.TemporaryDirectory(dir=self.parent) as directory:
            folder = Path(directory)
            (folder / "logs").mkdir()
            process = Mock(pid=123)
            process.wait.side_effect = [subprocess.TimeoutExpired("fixture", 1), -15]
            client = dict(process=process, token="abc", folder=folder)
            order = []
            with patch("two_instance_test.snapshot_owned",
                       side_effect=lambda token: order.append("snapshot") or []), \
                 patch("two_instance_test.os_kill_group",
                       side_effect=lambda pid: order.append("kill")), \
                 patch("two_instance_test.cleanup_owned", return_value=0):
                result = wait_client(client, 0)
            self.assertEqual(order, ["snapshot", "kill"])
            self.assertTrue(result["timed_out"])
            self.assertEqual(result["exit_code"], -15)
            self.assertTrue((folder / "logs/timeout-processes.json").is_file())


if __name__ == "__main__":
    unittest.main()
