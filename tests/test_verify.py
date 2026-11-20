"""Test orchestration only; these mocks are not gameplay evidence."""
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import verify


class VerificationRunnerTests(unittest.TestCase):
    def run_fixture(self, integration, codes):
        parent = Path(__file__).resolve().parents[1] / "local/test-tools"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=parent) as directory:
            root = Path(directory)
            arguments = ["verify.py"] + (["--integration"] if integration else [])
            with patch.object(verify, "ROOT", root), patch.object(sys, "argv", arguments), \
                 patch.object(verify.subprocess, "run",
                              side_effect=[SimpleNamespace(returncode=code) for code in codes]) as run, \
                 patch("builtins.print"):
                with self.assertRaises(SystemExit) as exited:
                    verify.main()
            summary = json.loads((root / "local/integration-summary.json").read_text())
            return exited.exception.code, summary, run.call_args_list

    def test_coexistence_failure_stops_later_game_stages(self):
        code, summary, calls = self.run_fixture(True, [0, 0, 0, 0, 1])
        self.assertEqual(code, 1)
        self.assertFalse(summary["passed"])
        self.assertEqual(summary["stages"][-1], dict(stage="coexistence", exit_code=1))
        self.assertEqual(len(calls), 5)

    def test_expanded_serial_order_includes_network_ai(self):
        code, summary, calls = self.run_fixture(True, [0] * 11)
        self.assertEqual(code, 0)
        self.assertTrue(summary["passed"])
        self.assertEqual([stage["stage"] for stage in summary["stages"]],
                         ["tooling", "pad-mapping", "build", "loader", "coexistence",
                          "net-loopback", "actors", "targeting", "net-ai-targeting",
                          "net-ai-movement", "controllers"])
        self.assertEqual(len(calls), 11)
        self.assertEqual(calls[8].args[0][1:], ["tools/net_ai_test.py", "--suite"])
        self.assertEqual(calls[9].args[0][1:], ["tools/net_ai_test.py", "--brain", "--suite"])

    def test_default_does_not_launch_or_build_the_game(self):
        code, summary, calls = self.run_fixture(False, [0, 0])
        self.assertEqual(code, 0)
        self.assertFalse(summary["integration"])
        self.assertEqual([stage["stage"] for stage in summary["stages"]],
                         ["tooling", "pad-mapping"])
        self.assertEqual(len(calls), 2)


if __name__ == "__main__":
    unittest.main()
