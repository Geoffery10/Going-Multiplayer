import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from seat_test import score, FIELDS


class SeatScorerTests(unittest.TestCase):
    def setUp(self):
        self.process = {"timed_out": False, "exit_code": 0}
        self.text = "GMP_SEAT_RESULT PASS run=abc count=2 " + " ".join(f + "=True" for f in FIELDS)

    def test_all_invariants_required(self):
        self.assertTrue(score(self.text, "abc", 2, self.process))
        for field in FIELDS:
            self.assertFalse(score(self.text.replace(field + "=True", field + "=False"), "abc", 2, self.process))
            self.assertFalse(score(self.text.replace(field + "=True", ""), "abc", 2, self.process))

    def test_stale_wrong_count_and_duplicate_results(self):
        self.assertFalse(score(self.text, "stale", 2, self.process))
        self.assertFalse(score(self.text, "abc", 5, self.process))
        self.assertFalse(score(self.text + "\n" + self.text, "abc", 2, self.process))
        self.assertFalse(score(self.text + " health=True", "abc", 2, self.process))

    def test_process_errors_override_success(self):
        self.assertFalse(score(self.text, "abc", 2, {"timed_out": True, "exit_code": 0}))
        self.assertFalse(score(self.text, "abc", 2, {"timed_out": False, "exit_code": 3}))

    def test_negative_requires_only_intended_invariant_failure(self):
        text = self.text.replace(" PASS ", " FAIL ").replace("health=True", "health=False")
        result = {"timed_out": False, "exit_code": 1}
        self.assertTrue(score(text, "abc", 2, result, negative=True))
        self.assertFalse(score(text.replace("movement=True", "movement=False"), "abc", 2, result, negative=True))
        self.assertFalse(score("", "abc", 2, result, negative=True))


if __name__ == "__main__":
    unittest.main()
