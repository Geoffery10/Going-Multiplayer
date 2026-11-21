"""O4c: one native authored host attack, target attribution and idempotent guest health deltas."""
import argparse
import json
import math
import re
import time

from launch_isolated import ROOT
from launch_proton import DEFAULT_PROTON
from net_ai_test import run_gate

INTS = ("floor", "pulses", "hits", "selected", "native_target", "received", "applied",
        "duplicates", "rejected", "routed", "blocks", "starts")
BOOLS = ("connected", "ready", "ack", "mirror_quiet", "target_negative", "duplicate_negative")
FLOATS = ("amount", "p1", "p2")
STRINGS = ("run", "role", "session", "build", "geometry", "scene", "source")


def parse(text, run_id, build):
    lines = [line for line in text.splitlines() if line.startswith("GMP_DAMAGE_RESULT")]
    if len(lines) != 1:
        return None
    match = re.fullmatch(r"GMP_DAMAGE_RESULT (PASS|FAIL) (.+)", lines[0])
    if match is None:
        return None
    pairs = re.findall(r"(\w+)=(\S+)", match[2])
    result = dict(pairs)
    fields = set(INTS + BOOLS + FLOATS + STRINGS)
    if len(pairs) != len(fields) or set(result) != fields:
        return None
    if result["run"] != run_id or result["build"] != build or \
            not re.fullmatch("[0-9a-f]{64}", result["geometry"]):
        return None
    try:
        for field in INTS:
            result[field] = int(result[field])
            if result[field] < 0:
                return None
        for field in FLOATS:
            result[field] = float(result[field])
            if not math.isfinite(result[field]) or result[field] < 0:
                return None
        for field in BOOLS:
            if result[field] not in ("True", "False"):
                return None
            result[field] = result[field] == "True"
        result["status"] = match[1]
        applies = [line for line in text.splitlines() if line.startswith("GMP_DAMAGE_APPLY")]
        if len(applies) != result["applied"]:
            return None
        for line in applies:
            if re.fullmatch(r"GMP_DAMAGE_APPLY event=1 source_target=2 routed_target=[12] "
                            r"amount=1\.000 p1=[\d.]+ p2=[\d.]+", line) is None:
                return None
        return result
    except ValueError:
        return None


def score(host_text, guest_text, run_id, build, host_process, guest_process, negative=None):
    host, guest = (parse(text, run_id, build) for text in (host_text, guest_text))
    if host is None or guest is None or host_process["timed_out"] or guest_process["timed_out"]:
        return False
    if host["role"] != "host" or guest["role"] != "guest" or host["status"] != "PASS" \
            or host_process["exit_code"] != 0:
        return False
    if host["session"] == "none" or host["session"] != guest["session"] or any(
            host[key] != guest[key] for key in ("geometry", "scene", "floor")):
        return False
    for text, role in ((host_text, "host"), (guest_text, "guest")):
        intros = [line for line in text.splitlines() if line.startswith("GMP_DAMAGE_INTRO")]
        if intros != [f"GMP_DAMAGE_INTRO role={role} dialogue=False background=True"]:
            return False
    if not all(actor[key] for actor in (host, guest) for key in ("connected", "ready", "ack")):
        return False
    if any(host[key] for key in ("target_negative", "duplicate_negative")):
        return False
    if (host["pulses"], host["hits"], host["selected"], host["native_target"]) != (1, 1, 2, 2) \
            or host["source"] != "Explosion" or host["amount"] != 1 \
            or (host["p1"], host["p2"]) != (10, 9):
        return False
    if guest["pulses"] != 0 or guest["hits"] != 0 or guest["native_target"] != 2 \
            or guest["received"] < 3 or guest["duplicates"] < 2 or guest["rejected"] != 0 \
            or guest["amount"] != host["amount"]:
        return False
    if not guest["mirror_quiet"] or guest["blocks"] < 4 or guest["starts"] != 0:
        return False
    if negative:
        if guest["status"] != "FAIL" or guest_process["exit_code"] != 1:
            return False
        if negative == "target":
            return (guest["target_negative"] and not guest["duplicate_negative"]
                    and guest["applied"] == 1 and guest["routed"] == 1
                    and (guest["p1"], guest["p2"]) == (9, 10))
        if negative == "duplicate":
            return (guest["duplicate_negative"] and not guest["target_negative"]
                    and guest["applied"] >= 3 and guest["routed"] == 2
                    and guest["p1"] == 10 and guest["p2"] == 10 - guest["applied"])
        return False
    return (guest["status"] == "PASS" and guest_process["exit_code"] == 0
            and not guest["target_negative"] and not guest["duplicate_negative"]
            and guest["applied"] == 1 and guest["routed"] == 2
            and (guest["p1"], guest["p2"]) == (10, 9))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", action="store_true")
    parser.add_argument("--negative", choices=("target", "duplicate"))
    args = parser.parse_args()
    if DEFAULT_PROTON is None:
        parser.error("Configure local/runtime-config.json")
    mutations = [args.negative] if args.negative or not args.suite else [None, "target", "duplicate"]
    gates = []
    for negative in mutations:
        if gates:
            time.sleep(5)
        gates.append(run_gate(
            DEFAULT_PROTON, 300, negative, probe_flag="-gmp-net-damage-probe",
            score_fn=score, parse_fn=parse, evidence_prefix="net-damage-",
            mutation_flag="-gmp-net-damage-" + (negative or "") + "-negative", scope=__doc__))
    passed = all(gate["passed"] for gate in gates)
    (ROOT / "local/net-damage-suite-summary.json").write_text(
        json.dumps(dict(passed=passed, gates=gates), indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
