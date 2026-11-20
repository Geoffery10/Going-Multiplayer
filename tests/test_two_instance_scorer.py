import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from two_instance_test import score_client


class TwoInstanceScorerTests(unittest.TestCase):
    def setUp(self):
        self.process = {"timed_out": False, "exit_code": 0}
        self.text = ("GMP_SCENE_LOADED name=Stinger count=1\n"
                     "GMP_SCENE_LOADED name=Title count=2\n"
                     "GMP_SMOKE_RESULT PASS run=abc hooks=True steam=True scenes=2")

    def test_both_clients_must_reach_minimum_scenes(self):
        self.assertEqual(score_client(self.text, "abc", self.process, 2), "PASS")
        self.assertEqual(score_client(self.text.replace("scenes=2", "scenes=1"),
                                      "abc", self.process, 2), "FAIL")
        self.assertEqual(score_client(self.text.replace("scenes=2", "scenes=1"),
                                      "abc", self.process, 1), "PASS")

    def test_run_id_duplicates_and_process_errors(self):
        self.assertEqual(score_client(self.text, "stale", self.process, 2), "FAIL")
        self.assertEqual(score_client(self.text + "\n" + self.text, "abc", self.process, 2), "FAIL")
        self.assertEqual(score_client(self.text, "abc",
                                      {"timed_out": True, "exit_code": 0}, 2), "TIMEOUT")
        self.assertEqual(score_client(self.text, "abc",
                                      {"timed_out": False, "exit_code": 3}, 2), "FAIL")

    def test_hooks_steam_and_status_are_required(self):
        cases = {
            "hooks=False": self.text.replace("hooks=True", "hooks=False"),
            "steam=False": self.text.replace("steam=True", "steam=False"),
            "status=FAIL": self.text.replace("PASS run=abc", "FAIL run=abc"),
            "scenes=0": self.text.replace("scenes=2", "scenes=0"),
        }
        for label, text in cases.items():
            self.assertEqual(score_client(text, "abc", self.process, 2), "FAIL", label)


if __name__ == "__main__":
    unittest.main()
