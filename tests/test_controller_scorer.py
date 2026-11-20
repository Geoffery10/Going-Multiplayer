import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from controller_test import score, FIELDS


class ControllerScorerTests(unittest.TestCase):
    def setUp(self):
        self.text = "GMP_CONTROLLER_RESULT PASS run=abc " + " ".join(f + "=True" for f in FIELDS) + " hardware=0"
        self.result = {"exit_code": 0, "timed_out": False}

    def test_success_and_required_checks(self):
        self.assertTrue(score(self.text, "abc", self.result))
        for field in FIELDS:
            self.assertFalse(score(self.text.replace(field + "=True", field + "=False"), "abc", self.result))
        self.assertFalse(score("", "abc", self.result))
        self.assertFalse(score(self.text, "old", self.result))

    def test_process_failure_and_duplicate(self):
        self.assertFalse(score(self.text, "abc", {"exit_code": 3, "timed_out": False}))
        self.assertFalse(score(self.text, "abc", {"exit_code": 0, "timed_out": True}))
        self.assertFalse(score(self.text + "\n" + self.text, "abc", self.result))

    def test_negative_requires_isolation_failure_only(self):
        text = self.text.replace(" PASS ", " FAIL ").replace("isolation=True", "isolation=False")
        result = {"exit_code": 1, "timed_out": False}
        self.assertTrue(score(text, "abc", result, True))
        self.assertFalse(score(text.replace("disconnect=True", "disconnect=False"), "abc", result, True))


if __name__ == "__main__":
    unittest.main()
