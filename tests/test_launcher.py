import importlib.util
from pathlib import Path
import tempfile
import unittest
import signal
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("launch_isolated", ROOT / "tools/launch_isolated.py")
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


class LauncherTests(unittest.TestCase):
    def test_cleanup_requires_exact_launch_token(self):
        parent = ROOT / "local/test-tools"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=parent) as temporary:
            proc = Path(temporary)
            for pid, value in ((101, b"GMP_LAUNCH_TOKEN=ours\0WINEPREFIX=test"),
                               (102, b"GMP_LAUNCH_TOKEN=ours-extra\0"),
                               (103, b"GMP_LAUNCH_TOKEN=someone-else\0"),
                               (104, b"WINEPREFIX=test\0")):
                (proc / str(pid)).mkdir()
                (proc / str(pid) / "environ").write_bytes(value)
            with patch.object(launcher.os, "kill") as kill:
                self.assertEqual(launcher.cleanup_owned("ours", proc), 1)
                kill.assert_called_once_with(101, signal.SIGKILL)

    def test_invalid_profile_cannot_escape_workspace(self):
        with self.assertRaises(ValueError):
            launcher.command("../real-save", 10)

    def test_command_uses_only_test_copy_and_isolates_accounts(self):
        parent = ROOT / "local/test-tools"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=parent) as temporary:
            root = Path(temporary)
            game = root / "local/test-install/Going Under.exe"
            game.parent.mkdir(parents=True)
            game.write_bytes(b"test fixture, never executed")
            with patch.object(launcher, "ROOT", root), \
                 patch.object(launcher, "RUNTIME", root / "runtime"), \
                 patch.object(launcher.shutil, "which", side_effect=lambda name: "/usr/bin/" + name):
                folder, args = launcher.command("test", 10)
            self.assertIn("--unshare-net", args)
            self.assertIn("--unshare-pid", args)
            self.assertIn("--ro-bind", args)
            self.assertIn("--tmpfs", args)
            self.assertIn(str(game), args)
            self.assertIn("SteamAppId", args)
            self.assertIn("SteamGameId", args)
            self.assertIn("--unsetenv", args)
            self.assertEqual(folder, root / "local/profiles/test")
            prefix = args[args.index("WINEPREFIX") + 1]
            self.assertTrue(prefix.startswith(str(root / "runtime")))


if __name__ == "__main__":
    unittest.main()
