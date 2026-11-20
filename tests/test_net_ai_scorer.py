from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from net_ai_test import run_gate, score_pair

BUILD = "a" * 64


def line(role):
    return (f"GMP_AI_RESULT PASS run=abc role={role} session=s1 build={BUILD} "
            "scene=Cubicle_Dungeon floor=0 phases=7 acquired=True swapped=True "
            "targets=True frozen=True blocked=60 drift=0.000 "
            "ai_negative=False target_negative=False")


class NetAiScorerTests(unittest.TestCase):
    def setUp(self):
        self.host, self.guest = line("host"), line("guest")
        self.ok = dict(timed_out=False, exit_code=0)
        self.fail = dict(timed_out=False, exit_code=1)

    def score(self, host=None, guest=None, hp=None, gp=None, negative=None):
        return score_pair(host or self.host, guest or self.guest, "abc", BUILD,
                          hp or self.ok, gp or self.ok, negative)

    def test_positive(self):
        self.assertTrue(self.score())

    def test_required_invariants(self):
        for before, after in (("phases=7", "phases=3"), ("acquired=True", "acquired=False"),
                              ("swapped=True", "swapped=False"), ("targets=True", "targets=False"),
                              ("frozen=True", "frozen=False"), ("blocked=60", "blocked=0"),
                              ("drift=0.000", "drift=1.000")):
            with self.subTest(before=before):
                self.assertFalse(self.score(guest=self.guest.replace(before, after)))

    def test_identity_and_process_failures(self):
        for before, after in (("run=abc", "run=old"), ("session=s1", "session=s2"),
                              (BUILD, "b" * 64), ("floor=0", "floor=1"),
                              ("scene=Cubicle_Dungeon", "scene=Other"), ("role=guest", "role=host")):
            self.assertFalse(self.score(guest=self.guest.replace(before, after)))
        self.assertFalse(self.score(guest=self.guest + "\n" + self.guest))
        self.assertFalse(self.score(guest="no result"))
        self.assertFalse(self.score(gp=self.fail))
        self.assertFalse(self.score(hp=dict(timed_out=True, exit_code=0)))

    def test_ai_mutation(self):
        guest = self.guest.replace("PASS", "FAIL").replace("ai_negative=False", "ai_negative=True")
        guest = guest.replace("targets=True", "targets=False").replace("blocked=60", "blocked=0")
        self.assertTrue(self.score(guest=guest, gp=self.fail, negative="ai"))
        self.assertFalse(self.score(guest=guest.replace("phases=7", "phases=1"),
                                    gp=self.fail, negative="ai"))
        self.assertFalse(self.score(guest=guest.replace("targets=False", "targets=True"),
                                    gp=self.fail, negative="ai"))

    def test_target_mutation(self):
        guest = self.guest.replace("PASS", "FAIL").replace("target_negative=False",
                                                          "target_negative=True")
        guest = guest.replace("targets=True", "targets=False")
        self.assertTrue(self.score(guest=guest, gp=self.fail, negative="target"))
        self.assertFalse(self.score(guest=guest.replace("blocked=60", "blocked=0"),
                                    gp=self.fail, negative="target"))

    def test_mutation_cannot_hide_host_failure_or_guest_crash(self):
        guest = self.guest.replace("PASS", "FAIL").replace("target_negative=False",
                                                          "target_negative=True")
        guest = guest.replace("targets=True", "targets=False")
        self.assertFalse(self.score(host=self.host.replace("PASS", "FAIL"), guest=guest,
                                    hp=self.fail, gp=self.fail, negative="target"))
        self.assertFalse(self.score(guest=guest, gp=dict(timed_out=False, exit_code=2),
                                    negative="target"))
        self.assertFalse(self.score(guest=guest.replace("frozen=True", "frozen=False"),
                                    gp=self.fail, negative="target"))

    def test_malformed_numbers_fail_without_crashing(self):
        for drift in ("--", ".", "1.2.3", "9" * 500):
            self.assertFalse(self.score(guest=self.guest.replace("drift=0.000",
                                                                "drift=" + drift)))

    def test_malformed_duplicate_result_is_not_ignored(self):
        self.assertFalse(self.score(guest=self.guest + "\nGMP_AI_RESULT corrupt"))

    def test_launch_error_writes_failed_summary(self):
        parent = Path(__file__).resolve().parents[1] / "local/test-tools"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=parent) as directory:
            root = Path(directory)
            (root / "artifacts").mkdir()
            (root / "artifacts/GoingMultiplayer.dll").write_bytes(b"fixture")
            with patch("net_ai_test.ROOT", root), \
                 patch("net_ai_test.ensure_install", return_value=root), \
                 patch("net_ai_test.start_client", side_effect=RuntimeError("no display")), \
                 patch("builtins.print"):
                result = run_gate(root)
            self.assertFalse(result["passed"])
            self.assertEqual(result["error"], "RuntimeError: no display")
            self.assertEqual(len(list(root.glob("local/profiles/net-ai-*/summary.json"))), 1)


if __name__ == "__main__":
    unittest.main()
