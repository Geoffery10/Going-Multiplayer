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
    def run_fixture(self, arguments, codes):
        parent = Path(__file__).resolve().parents[1] / "local/test-tools"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=parent) as directory:
            root = Path(directory)
            argv = ["verify.py"] + arguments
            with patch.object(verify, "ROOT", root), patch.object(sys, "argv", argv), \
                 patch.object(verify.subprocess, "run",
                              side_effect=[SimpleNamespace(returncode=code) for code in codes]) as run, \
                 patch("builtins.print"):
                with self.assertRaises(SystemExit) as exited:
                    verify.main()
            if "--integration" in arguments:
                name = "integration-summary.json"
            elif "--quick" in arguments:
                name = "quick-summary.json"
            elif "--stages" in arguments:
                name = "stages-summary.json"
            else:
                name = "offline-summary.json"
            summary = json.loads((root / "local" / name).read_text())
            return exited.exception.code, summary, run.call_args_list

    def test_coexistence_failure_stops_later_game_stages(self):
        code, summary, calls = self.run_fixture(["--integration"], [0] * 7 + [1])
        self.assertEqual(code, 1)
        self.assertFalse(summary["passed"])
        self.assertEqual(summary["stages"][-1], dict(stage="coexistence", exit_code=1))
        self.assertEqual(len(calls), 8)

    def test_expanded_serial_order_includes_network_ai(self):
        code, summary, calls = self.run_fixture(["--integration"], [0] * 16)
        self.assertEqual(code, 0)
        self.assertTrue(summary["passed"])
        self.assertEqual(summary["profile"], "full")
        self.assertEqual([stage["stage"] for stage in summary["stages"]],
                         ["tooling", "pad-mapping", "damage-ledger", "input-ledger", "build", "startup",
                          "loader", "coexistence", "net-loopback", "actors", "targeting",
                          "net-ai-targeting", "net-ai-movement", "net-damage", "net-input",
                          "controllers"])
        self.assertEqual(len(calls), 16)
        self.assertEqual(calls[5].args[0][1:], ["tools/startup_test.py", "--suite"])
        self.assertEqual(calls[11].args[0][1:], ["tools/net_ai_test.py", "--suite"])
        self.assertEqual(calls[12].args[0][1:], ["tools/net_ai_test.py", "--brain", "--suite"])
        self.assertEqual(calls[13].args[0][1:], ["tools/damage_test.py", "--suite"])
        self.assertEqual(calls[14].args[0][1:], ["tools/input_test.py", "--suite"])

    def test_quick_runs_build_loader_and_one_positive_gate(self):
        code, summary, calls = self.run_fixture(["--quick"], [0] * 7)
        self.assertEqual(code, 0)
        self.assertTrue(summary["passed"])
        self.assertEqual(summary["profile"], "quick")
        self.assertFalse(summary["integration"])
        self.assertEqual([stage["stage"] for stage in summary["stages"]],
                         ["tooling", "pad-mapping", "damage-ledger", "input-ledger", "build",
                          "loader", "net-input-positive"])
        self.assertEqual(calls[-1].args[0][1:], ["tools/input_test.py"])

    def test_quick_focus_selects_only_its_gate(self):
        code, summary, calls = self.run_fixture(["--quick", "--focus", "net-damage"],
                                                [0] * 7)
        self.assertEqual(code, 0)
        self.assertEqual(summary["stages"][-1]["stage"], "net-damage-positive")
        self.assertEqual(calls[-1].args[0][1:], ["tools/damage_test.py"])
        self.assertNotIn(["tools/input_test.py", "--suite"], [call.args[0][1:] for call in calls])

    def test_stage_selection_stops_on_failure(self):
        code, summary, calls = self.run_fixture(["--stages", "net-input,controllers"], [1])
        self.assertEqual(code, 1)
        self.assertEqual(summary["stages"], [dict(stage="net-input", exit_code=1)])
        self.assertEqual(len(calls), 1)

    def test_list_does_not_execute_anything(self):
        with patch.object(sys, "argv", ["verify.py", "--list"]), \
             patch("builtins.print") as printed, \
             patch.object(verify.subprocess, "run") as run:
            verify.main()
        run.assert_not_called()
        self.assertTrue(printed.called)

    def test_default_does_not_launch_or_build_the_game(self):
        code, summary, calls = self.run_fixture([], [0, 0, 0, 0])
        self.assertEqual(code, 0)
        self.assertFalse(summary["integration"])
        self.assertEqual(summary["profile"], "offline")
        self.assertEqual([stage["stage"] for stage in summary["stages"]],
                         ["tooling", "pad-mapping", "damage-ledger", "input-ledger"])
        self.assertEqual(len(calls), 4)


if __name__ == "__main__":
    unittest.main()
