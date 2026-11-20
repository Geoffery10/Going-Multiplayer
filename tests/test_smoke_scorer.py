import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from smoke_test import score


class SmokeScorerTests(unittest.TestCase):
    def setUp(self):
        self.result = {"timed_out": False, "exit_code": 0}
        self.text = "[Info :Going Multiplayer] GMP_SMOKE_RESULT PASS run=abc hooks=True steam=True scenes=1"

    def test_valid_result(self):
        self.assertEqual(score(self.text, "abc", self.result), "PASS")

    def test_missing_or_stale_result(self):
        for text in ("", self.text.replace("run=abc", "run=previous")):
            self.assertEqual(score(text, "abc", self.result), "FAIL")

    def test_duplicate_results(self):
        self.assertEqual(score(self.text + "\n" + self.text, "abc", self.result), "FAIL")

    def test_failure_is_not_a_pass(self):
        for text in (self.text.replace("PASS", "FAIL"),
                     self.text.replace("steam=True", "steam=False"),
                     self.text.replace("hooks=True", "hooks=False"),
                     self.text.replace("scenes=1", "scenes=0")):
            self.assertEqual(score(text, "abc", self.result), "FAIL")

    def test_timeout_or_crash_overrides_pass(self):
        self.assertEqual(score(self.text, "abc", {"timed_out": True, "exit_code": 0}), "TIMEOUT")
        self.assertEqual(score(self.text, "abc", {"timed_out": False, "exit_code": 3}), "FAIL")


if __name__ == "__main__":
    unittest.main()
