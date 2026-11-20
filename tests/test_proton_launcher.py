import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import launch_proton


class ProtonLauncherTests(unittest.TestCase):
    def test_authenticated_mode_preserves_save_isolation(self):
        parent = launch_proton.ROOT / "local/test-tools"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=parent) as temporary:
            root = Path(temporary)
            home = root / "real-home"
            (home / ".local/share/Steam").mkdir(parents=True)
            (home / ".steam").mkdir()
            authority = root / "xauth"
            authority.write_text("fixture")
            with patch.object(launch_proton, "ROOT", root / "project"), \
                 patch.object(launch_proton, "RUNTIME", root / "runtime"), \
                 patch.object(launch_proton.Path, "home", return_value=home), \
                 patch.object(launch_proton, "stage_runner", return_value=root / "runner"), \
                 patch.dict(os.environ, DISPLAY=":99", XAUTHORITY=str(authority)):
                folder, args = launch_proton.command("test", root / "proton", ["-gmp-smoke"])
                _, windowed = launch_proton.command("test-visible", root / "proton", [], windowed=True)
            self.assertNotIn("--unshare-net", args)
            self.assertIn("--ro-bind", args)
            self.assertIn(str(home / ".steam"), args)
            self.assertEqual(args[args.index("HOME") + 1], str(root / "runtime/profiles/test/home"))
            self.assertEqual(args[args.index("STEAM_COMPAT_DATA_PATH") + 1],
                             str(root / "runtime/profiles/test/compatdata"))
            self.assertIn(str(root / "project/local/test-install/Going Under.exe"), args)
            self.assertNotIn(str(home), args)
            self.assertEqual(folder, root / "project/local/profiles/test")
            self.assertIn("-nographics", args)
            self.assertNotIn("-nographics", windowed)
            self.assertIn("-screen-fullscreen", windowed)
            self.assertNotEqual(args[args.index("GMP_LAUNCH_TOKEN") + 1],
                                windowed[windowed.index("GMP_LAUNCH_TOKEN") + 1])


if __name__ == "__main__":
    unittest.main()
