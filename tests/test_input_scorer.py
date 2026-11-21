from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from input_test import parse, score

BUILD = "a" * 64
FIELDS = dict(run="abc", build=BUILD, scene="Cubicle_Dungeon", floor=0,
              geometry="b" * 64, actors=2, connected=True, ready=True, go=True,
              done=True, ack=True)


def trace(role, negative=None):
    host = role == "host"
    status = "PASS"
    data = dict(FIELDS)
    data.update(role=role, session="s1")
    if host:
        data.update(applied_inputs=30, moved_inputs=30, rejected=2,
                    stale_rejected=True, stale_applied=False, timeout_cleared=True,
                    remote_delta="2.100", forward_delta="2.000", stale_backward="0.000",
                    post_stop_drift="0.020", snapshots_sent=80, sent=0,
                    snapshots_received=0, snapshots_applied=0, snapshots_ignored=0,
                    snapshots_rejected=0, local_delta="0.000", host_delta="0.000",
                    pose_error="0.000", clearance="3.500",
                    input_drop=False, stale_negative=False, timeout_negative=False,
                    snapshot_negative=False)
        if negative == "drop":
            status = "FAIL"
            data.update(input_drop=True, applied_inputs=0, moved_inputs=0,
                        remote_delta="0.000", forward_delta="0.000")
        elif negative == "stale":
            status = "FAIL"
            data.update(stale_negative=True, stale_rejected=False,
                        stale_applied=True, stale_backward="0.500")
        elif negative == "timeout":
            status = "FAIL"
            data.update(timeout_negative=True, timeout_cleared=False,
                        post_stop_drift="0.700")
    else:
        data.update(applied_inputs=0, moved_inputs=0, rejected=0,
                    stale_rejected=False, stale_applied=False, timeout_cleared=False,
                    remote_delta="0.000", forward_delta="0.000", stale_backward="0.000",
                    post_stop_drift="0.000", snapshots_sent=0, sent=30,
                    snapshots_received=80, snapshots_applied=79, snapshots_ignored=0,
                    snapshots_rejected=0, local_delta="2.050", host_delta="2.100",
                    pose_error="0.000", clearance="0.000",
                    input_drop=False, stale_negative=False, timeout_negative=False,
                    snapshot_negative=False)
        if negative == "snapshot":
            status = "FAIL"
            data.update(snapshot_negative=True, snapshots_applied=0,
                        snapshots_ignored=79, local_delta="2.100", pose_error="4.200")
        elif negative == "drop-follow":
            data.update(local_delta="0.000", host_delta="0.000")
    line = " ".join(f"{key}={value}" for key, value in data.items())
    return (f"GMP_INPUT_INTRO role={role} dialogue=False background=True\n"
            f"GMP_INPUT_RESULT {status} {line}")


class InputScorerTests(unittest.TestCase):
    def setUp(self):
        self.host, self.guest = trace("host"), trace("guest")
        self.ok = dict(timed_out=False, exit_code=0)
        self.fail = dict(timed_out=False, exit_code=1)

    def score(self, host=None, guest=None, hp=None, gp=None, negative=None):
        return score(host or self.host, guest or self.guest, "abc", BUILD,
                     hp or self.ok, gp or self.ok, negative)

    def test_positive(self):
        self.assertTrue(self.score())

    def test_host_authority_and_timeout_invariants_required(self):
        for old, new in (("applied_inputs=30", "applied_inputs=0"),
                         ("moved_inputs=30", "moved_inputs=0"),
                         ("rejected=2", "rejected=0"),
                         ("stale_rejected=True", "stale_rejected=False"),
                         ("timeout_cleared=True", "timeout_cleared=False"),
                         ("remote_delta=2.100", "remote_delta=0.100"),
                         ("stale_backward=0.000", "stale_backward=0.300"),
                         ("post_stop_drift=0.020", "post_stop_drift=0.600")):
            self.assertFalse(self.score(host=self.host.replace(old, new)))

    def test_guest_follows_snapshots_not_its_own_simulation(self):
        for old, new in (("snapshots_applied=79", "snapshots_applied=0"),
                         ("snapshots_ignored=0", "snapshots_ignored=20"),
                         ("pose_error=0.000", "pose_error=1.000"),
                         ("local_delta=2.050", "local_delta=0.100"),
                         ("sent=30", "sent=3")):
            self.assertFalse(self.score(guest=self.guest.replace(old, new)))

    def test_host_mutations_require_their_own_observed_mismatch(self):
        self.assertTrue(self.score(host=trace("host", "drop"), guest=trace("guest", "drop-follow"),
                                    hp=self.fail, negative="drop"))
        self.assertTrue(self.score(host=trace("host", "stale"), guest=trace("guest", "stale-follow"),
                                    hp=self.fail, negative="stale"))
        self.assertTrue(self.score(host=trace("host", "timeout"), guest=trace("guest", "timeout-follow"),
                                    hp=self.fail, negative="timeout"))
        self.assertFalse(self.score(host=trace("host", "drop"), guest=trace("guest", "drop-follow"),
                                     hp=self.fail, negative="stale"))
        self.assertFalse(self.score(host=trace("host", "stale"), guest=trace("guest", "stale-follow"),
                                     hp=self.ok, negative="stale"))

    def test_guest_snapshot_mutation_requires_divergence(self):
        self.assertTrue(self.score(guest=trace("guest", "snapshot"), gp=self.fail,
                                    negative="snapshot"))
        self.assertFalse(self.score(guest=trace("guest", "snapshot").replace(
            "pose_error=4.200", "pose_error=0.000"), gp=self.fail, negative="snapshot"))
        self.assertFalse(self.score(guest=trace("guest", "snapshot").replace(
            "snapshots_ignored=79", "snapshots_ignored=0"), gp=self.fail, negative="snapshot"))

    def test_identity_malformed_duplicate_and_timeout(self):
        for old, new in (("session=s1", "session=s2"), ("floor=0", "floor=1"),
                         ("geometry=" + "b" * 64, "geometry=" + "c" * 64),
                         ("actors=2", "actors=1"), ("ready=True", "ready=False"),
                         ("done=True", "done=False"), ("ack=True", "ack=False")):
            self.assertFalse(self.score(guest=self.guest.replace(old, new)))
        self.assertFalse(self.score(guest=self.guest.replace("pose_error=0.000", "pose_error=nan")))
        self.assertFalse(self.score(guest=self.guest + "\nGMP_INPUT_RESULT corrupt"))
        self.assertFalse(self.score(gp=self.fail))
        self.assertFalse(self.score(guest=self.guest.replace(
            "dialogue=False background=True", "dialogue=True background=True")))
        self.assertIsNone(parse("no result", "abc", BUILD))


if __name__ == "__main__":
    unittest.main()
