import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from startup_test import score


def trace(normal=False):
    return ("GMP_SMOKE_RESULT PASS run=abc hooks=True steam=True scenes=2\n"
            + ("GMP_STARTUP_RESULT FAIL run=abc stinger=True skipped=False title=True "
               "videos_stopped=False seconds=8.500" if normal else
               "GMP_STARTUP_RESULT PASS run=abc stinger=True skipped=True title=True "
               "videos_stopped=True seconds=0.500"))


class StartupScorerTests(unittest.TestCase):
    def setUp(self):
        self.ok = dict(timed_out=False, exit_code=0)
        self.fail = dict(timed_out=False, exit_code=1)

    def test_positive_and_normal_control(self):
        self.assertTrue(score(trace(), "abc", self.ok))
        self.assertTrue(score(trace(True), "abc", self.fail, normal=True))
        self.assertFalse(score(trace(True), "abc", self.fail))
        self.assertFalse(score(trace(), "abc", self.ok, normal=True))

    def test_no_bypass_of_title_steam_or_loader(self):
        for old, new in (("stinger=True", "stinger=False"), ("title=True", "title=False"),
                         ("steam=True", "steam=False"), ("hooks=True", "hooks=False"),
                         ("scenes=2", "scenes=1"), ("skipped=True", "skipped=False"),
                         ("videos_stopped=True", "videos_stopped=False")):
            self.assertFalse(score(trace().replace(old, new), "abc", self.ok))

    def test_missing_duplicate_stale_malformed_and_process_failure(self):
        for text in ("", trace() + "\n" + trace(), trace().replace("run=abc", "run=old"),
                     trace().replace("seconds=0.500", "seconds=--"),
                     trace() + "\nGMP_STARTUP_RESULT corrupt",
                     trace() + "\nGMP_SMOKE_RESULT corrupt"):
            self.assertFalse(score(text, "abc", self.ok))
        self.assertFalse(score(trace(), "abc", self.fail))
        self.assertFalse(score(trace(), "abc", dict(timed_out=True, exit_code=0)))
        self.assertFalse(score(trace(True), "abc", dict(timed_out=True, exit_code=1), normal=True))


if __name__ == "__main__":
    unittest.main()
