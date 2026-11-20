from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from damage_test import parse, score

BUILD = "a" * 64


def trace(role, negative=None):
    host = role == "host"
    applied = 0 if host else 3 if negative == "duplicate" else 1
    p1, p2 = ((9, 10) if negative == "target" else (10, 10 - applied)) if not host else (10, 9)
    data = dict(run="abc", role=role, session="s1", build=BUILD, geometry="b" * 64,
                scene="Cubicle_Dungeon", floor=0, connected=True, ready=True, ack=True,
                pulses=1 if host else 0, hits=1 if host else 0, selected=2 if host else 0,
                native_target=2, source="Explosion" if host else "none", amount="1.000",
                received=0 if host else 3, applied=applied, duplicates=0 if host else 2,
                rejected=0, routed=0 if host else 1 if negative == "target" else 2,
                p1=f"{p1:.3f}", p2=f"{p2:.3f}", mirror_quiet=True,
                blocks=0 if host else 4, starts=0,
                target_negative=negative == "target", duplicate_negative=negative == "duplicate")
    lines = []
    for index in range(applied):
        route = 1 if negative == "target" else 2
        a, b = (9, 10) if route == 1 else (10, 9 - index)
        lines.append(f"GMP_DAMAGE_APPLY event=1 source_target=2 routed_target={route} "
                     f"amount=1.000 p1={a:.3f} p2={b:.3f}")
    status = "FAIL" if negative else "PASS"
    lines.append("GMP_DAMAGE_RESULT " + status + " " + " ".join(f"{k}={v}" for k, v in data.items()))
    return "\n".join(lines)


class DamageScorerTests(unittest.TestCase):
    def setUp(self):
        self.host, self.guest = trace("host"), trace("guest")
        self.ok = dict(timed_out=False, exit_code=0)
        self.fail = dict(timed_out=False, exit_code=1)

    def score(self, host=None, guest=None, hp=None, gp=None, negative=None):
        return score(host or self.host, guest or self.guest, "abc", BUILD,
                     hp or self.ok, gp or self.ok, negative)

    def test_positive(self):
        self.assertTrue(self.score())

    def test_native_pulse_hit_source_and_target_required(self):
        for old, new in (("pulses=1", "pulses=0"), ("hits=1", "hits=2"),
                         ("selected=2", "selected=1"), ("native_target=2", "native_target=1"),
                         ("source=Explosion", "source=God"), ("p1=10.000", "p1=9.000")):
            self.assertFalse(self.score(host=self.host.replace(old, new)))

    def test_duplicates_are_received_but_only_applied_once(self):
        self.assertFalse(self.score(guest=self.guest.replace("duplicates=2", "duplicates=0")))
        self.assertFalse(self.score(guest=self.guest.replace("received=3", "received=1")))
        self.assertFalse(self.score(guest=self.guest.replace("applied=1", "applied=2")))

    def test_controls_require_specific_observed_mismatch_and_healthy_host(self):
        self.assertTrue(self.score(guest=trace("guest", "target"), gp=self.fail, negative="target"))
        self.assertTrue(self.score(guest=trace("guest", "duplicate"), gp=self.fail, negative="duplicate"))
        self.assertFalse(self.score(guest=trace("guest", "target"), gp=self.fail, negative="duplicate"))
        self.assertFalse(self.score(guest=trace("guest", "target").replace("mirror_quiet=True",
                                                                          "mirror_quiet=False"),
                                    gp=self.fail, negative="target"))
        self.assertFalse(self.score(guest=trace("guest", "target"), gp=self.fail,
                                    hp=dict(timed_out=True, exit_code=0), negative="target"))

    def test_identity_missing_duplicate_malformed_and_crash(self):
        for old, new in (("run=abc", "run=old"), (BUILD, "c" * 64),
                         ("session=s1", "session=s2"), ("floor=0", "floor=1"),
                         ("p2=9.000", "p2=nan"), ("blocks=4", "blocks=0")):
            self.assertFalse(self.score(guest=self.guest.replace(old, new)))
        self.assertFalse(self.score(guest=self.guest + "\nGMP_DAMAGE_RESULT corrupt"))
        self.assertIsNone(parse("no result", "abc", BUILD))
        self.assertFalse(self.score(gp=self.fail))


if __name__ == "__main__":
    unittest.main()
