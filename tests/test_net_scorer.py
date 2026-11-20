import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from net_test import score_pair


def line(status, role, session="s1", local_delta=0.0, remote_delta=0.0,
         updates=100, local=(0.0, 0.0), remote=(0.0, 0.0),
         negative=False, stale_negative=False, authority_negative=False,
         stale=True, authority=True, epoch_rejected=True, controls=True,
         applied=100, epoch=1):
    local_id, peer_id = (1, 2) if role == "host" else (2, 1)
    b = lambda value: "True" if value else "False"
    return ("GMP_NET_RESULT " + status + " run=abc role=" + role
            + " session=" + session
            + " local_id=" + str(local_id) + " peer_id=" + str(peer_id)
            + " connected=True local_delta=" + f"{local_delta:.3f}"
            + " remote_delta=" + f"{remote_delta:.3f}"
            + " updates=" + str(updates) + " epoch=" + str(epoch)
            + " stale_rejected=" + b(stale)
            + " authority_rejected=" + b(authority)
            + " epoch_rejected=" + b(epoch_rejected)
            + " controls_sent=" + b(controls)
            + " applied=" + str(applied)
            + " final_local=" + f"{local[0]:.3f}," + f"{local[1]:.3f}"
            + " final_remote=" + f"{remote[0]:.3f}," + f"{remote[1]:.3f}"
            + " negative=" + b(negative)
            + " stale_negative=" + b(stale_negative)
            + " authority_negative=" + b(authority_negative))


class NetScorerTests(unittest.TestCase):
    def setUp(self):
        self.ok = {"timed_out": False, "exit_code": 0}
        self.host = line("PASS", "host", local_delta=0.0, remote_delta=1.2,
                         local=(0, 0), remote=(1.2, 0))
        self.guest = line("PASS", "guest", local_delta=1.2, remote_delta=0.0,
                          local=(1.2, 0), remote=(0, 0), stale=False,
                          authority=False, epoch_rejected=False)

    def test_positive(self):
        self.assertTrue(score_pair(self.host, self.guest, "abc", self.ok, self.ok))

    def test_session_ids_and_counts(self):
        self.assertFalse(score_pair(self.host, self.guest.replace("session=s1", "session=s2"),
                                    "abc", self.ok, self.ok))
        self.assertFalse(score_pair(self.host.replace("peer_id=2", "peer_id=3"),
                                    self.guest, "abc", self.ok, self.ok))
        self.assertFalse(score_pair(self.host.replace("updates=100", "updates=3"),
                                    self.guest, "abc", self.ok, self.ok))
        self.assertFalse(score_pair(self.host.replace("run=abc", "run=old"),
                                    self.guest, "abc", self.ok, self.ok))

    def test_controls_and_movement(self):
        for field in ("stale_rejected", "authority_rejected", "epoch_rejected"):
            self.assertFalse(score_pair(self.host.replace(field + "=True", field + "=False"),
                                        self.guest, "abc", self.ok, self.ok))
        self.assertFalse(score_pair(self.host, self.guest.replace("controls_sent=True",
                                                                  "controls_sent=False"),
                                    "abc", self.ok, self.ok))
        self.assertFalse(score_pair(self.host, self.guest.replace("local_delta=1.200",
                                                                  "local_delta=0.100"),
                                    "abc", self.ok, self.ok))
        self.assertFalse(score_pair(self.host.replace("remote_delta=1.200",
                                                      "remote_delta=0.100"),
                                    self.guest, "abc", self.ok, self.ok))
        self.assertFalse(score_pair(self.host.replace("epoch=1", "epoch=2"),
                                    self.guest, "abc", self.ok, self.ok))

    def test_timeouts_duplicates_and_failed_exit(self):
        self.assertFalse(score_pair(self.host, self.guest, "abc",
                                    {"timed_out": True, "exit_code": 0}, self.ok))
        self.assertFalse(score_pair(self.host, self.guest, "abc", self.ok,
                                    {"timed_out": False, "exit_code": 2}))
        self.assertFalse(score_pair(self.host + "\n" + self.host, self.guest,
                                    "abc", self.ok, self.ok))

    def test_negative_movement(self):
        host = line("FAIL", "host", remote_delta=0.0, negative=True)
        failed = {"timed_out": False, "exit_code": 1}
        self.assertTrue(score_pair(host, self.guest, "abc", failed, self.ok, "movement"))
        self.assertFalse(score_pair(host.replace("remote_delta=0.000", "remote_delta=1.200"),
                                    self.guest, "abc", failed, self.ok, "movement"))

    def test_negative_stale(self):
        host = line("FAIL", "host", remote_delta=99.0, stale=False, stale_negative=True,
                    local=(0, 0), remote=(99.0, 0))
        failed = {"timed_out": False, "exit_code": 1}
        self.assertTrue(score_pair(host, self.guest, "abc", failed, self.ok, "stale"))
        self.assertFalse(score_pair(host.replace("stale_rejected=False", "stale_rejected=True"),
                                    self.guest, "abc", failed, self.ok, "stale"))
        self.assertFalse(score_pair(host.replace("final_remote=99.000,0.000",
                                                 "final_remote=1.200,0.000"),
                                    self.guest, "abc", failed, self.ok, "stale"))

    def test_negative_authority(self):
        host = line("FAIL", "host", remote_delta=99.0, authority=False,
                    authority_negative=True, local=(0, 0), remote=(99.0, 0))
        failed = {"timed_out": False, "exit_code": 1}
        self.assertTrue(score_pair(host, self.guest, "abc", failed, self.ok, "authority"))
        self.assertFalse(score_pair(host.replace("authority_rejected=False",
                                                 "authority_rejected=True"),
                                    self.guest, "abc", failed, self.ok, "authority"))


if __name__ == "__main__":
    unittest.main()
