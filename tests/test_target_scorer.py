import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from target_test import score, FIELDS


class TargetScorerTests(unittest.TestCase):
    def setUp(self):
        self.process = {"timed_out": False, "exit_code": 0}
        self.text = ("GMP_TARGET_RESULT PASS run=abc "
                     + " ".join(f + "=True" for f in FIELDS)
                     + " sticky_stayed_p1=True negative=False")

    def test_all_fields_required(self):
        self.assertTrue(score(self.text, "abc", self.process))
        for field in FIELDS:
            self.assertFalse(score(self.text.replace(field + "=True", field + "=False"),
                                   "abc", self.process))
            self.assertFalse(score(self.text.replace(field + "=True", ""),
                                   "abc", self.process))

    def test_stale_run_duplicates_and_process_errors(self):
        self.assertFalse(score(self.text, "stale", self.process))
        self.assertFalse(score(self.text + "\n" + self.text, "abc", self.process))
        self.assertFalse(score(self.text, "abc", {"timed_out": True, "exit_code": 0}))
        self.assertFalse(score(self.text, "abc", {"timed_out": False, "exit_code": 3}))
        self.assertFalse(score("", "abc", self.process))

    def test_negative_requires_non_hostile_actor_failure(self):
        negative = ("GMP_TARGET_RESULT FAIL run=abc candidate_p2=True hostile=False "
                    "target_p2=False swap_p1=False excluded_p1=False reacquire_p2=False negative=True")
        failed = {"timed_out": False, "exit_code": 1}
        self.assertTrue(score(negative, "abc", failed, negative=True))
        self.assertFalse(score(negative, "abc", self.process, negative=True))
        # A control that did not actually make P2 non-hostile must not pass.
        self.assertFalse(score(negative.replace("hostile=False", "hostile=True"),
                               "abc", failed, negative=True))
        # The positive gate must reject a FAIL result.
        self.assertFalse(score(negative, "abc", failed, negative=False))


if __name__ == "__main__":
    unittest.main()
