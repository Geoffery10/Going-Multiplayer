import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from net_ai_test import parse_brain, score_brain
from test_net_ai_scorer import BUILD, line


def trace(role, mutation=None):
    target = line(role).replace("phases=7", "phases=31")
    if mutation:
        target = target.replace("RESULT PASS", "RESULT FAIL")
    values = dict(run="abc", role=role, geometry="b" * 64, enemy_id=1, authority=1, epoch=1,
                  moved="1.500", approach="1.500" if role == "host" else "0.000",
                  samples=40, pose_error="2.000" if mutation == "pose" else "0.000",
                  freeze_drift="0.300" if mutation == "sim" else "0.000",
                  host_brain=role == "host", mirror_quiet=mutation != "sim",
                  activation_blocks=0 if role == "host" else 4,
                  starts=1 if role == "host" or mutation == "sim" else 0,
                  pose_negative=mutation == "pose", sim_negative=mutation == "sim")
    samples = []
    for seq in range(1, 41):
        x = 0 if mutation == "pose" else seq * 0.05
        samples.append(f"GMP_BRAIN_SAMPLE role={role} seq={seq} target=2 state=Chasing pose={x:.3f},0,0")
    return "\n".join([target, "GMP_BRAIN_RESULT " + " ".join(f"{k}={v}" for k, v in values.items())] + samples)


class BrainScorerTests(unittest.TestCase):
    def setUp(self):
        self.host = trace("host")
        self.guest = trace("guest")
        self.ok = dict(timed_out=False, exit_code=0)
        self.fail = dict(timed_out=False, exit_code=1)

    def score(self, host=None, guest=None, hp=None, gp=None, negative=None):
        return score_brain(host or self.host, guest or self.guest, "abc", BUILD,
                           hp or self.ok, gp or self.ok, negative)

    def test_positive(self):
        self.assertTrue(self.score())

    def test_native_host_movement_and_brain_are_required(self):
        for before, after in (("moved=1.500", "moved=0.000"), ("approach=1.500", "approach=0.000"),
                              ("host_brain=True", "host_brain=False"), ("starts=1", "starts=0")):
            self.assertFalse(self.score(host=self.host.replace(before, after)))

    def test_real_sample_agreement_not_just_reported_error(self):
        self.assertFalse(self.score(guest=self.guest.replace("pose=1.000,0,0", "pose=99.000,0,0")))
        self.assertFalse(self.score(guest=self.guest.replace("target=2", "target=1")))
        self.assertFalse(self.score(guest=self.guest.replace("state=Chasing", "state=Idle")))
        self.assertFalse(self.score(guest="\n".join(self.guest.splitlines()[:12])))

    def test_geometry_epoch_and_spawn_guards_required(self):
        for before, after in (("b" * 64, "c" * 64), ("epoch=1", "epoch=0"),
                              ("enemy_id=1", "enemy_id=2"), ("activation_blocks=4", "activation_blocks=0"),
                              ("mirror_quiet=True", "mirror_quiet=False")):
            self.assertFalse(self.score(guest=self.guest.replace(before, after)))

    def test_mutation_twins_need_their_specific_observed_failure(self):
        self.assertTrue(self.score(guest=trace("guest", "pose"), gp=self.fail, negative="pose"))
        self.assertTrue(self.score(guest=trace("guest", "sim"), gp=self.fail, negative="sim"))
        self.assertFalse(self.score(guest=trace("guest", "pose"), gp=self.fail, negative="sim"))
        self.assertFalse(self.score(guest=trace("guest", "sim").replace("starts=1", "starts=0"),
                                    gp=self.fail, negative="sim"))
        self.assertFalse(self.score(guest=trace("guest", "sim").replace("freeze_drift=0.300",
                                                                       "freeze_drift=0.000"),
                                    gp=self.fail, negative="sim"))
        self.assertFalse(self.score(guest=trace("guest", "pose"), gp=self.fail,
                                    hp=dict(timed_out=True, exit_code=-15), negative="pose"))

    def test_malformed_and_duplicate_records_rejected(self):
        self.assertIsNone(parse_brain(self.guest + "\nGMP_BRAIN_RESULT corrupt", "abc", BUILD))
        for before, after in (("moved=1.500", "moved=nan"), ("samples=40", "samples=no"),
                              ("pose=1.000,0,0", "pose=--,0,0")):
            self.assertFalse(self.score(guest=self.guest.replace(before, after)))
        self.assertFalse(self.score(guest=self.guest + "\n" + self.guest.splitlines()[-1]))


if __name__ == "__main__":
    unittest.main()
