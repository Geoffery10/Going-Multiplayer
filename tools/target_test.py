"""Real-game enemy-targeting gate for a second local actor. No networking."""
import argparse
import json
from pathlib import Path
import re
import shutil
import uuid

from launch_isolated import ROOT, run_invocation
from launch_proton import command, DEFAULT_PROTON

FIELDS = ("candidate_p2", "same_room", "hostile", "alive", "active",
          "single_player_restored", "enemy_active", "target_p2",
          "swap_p1", "excluded_p1", "reacquire_p2")


def score(text, run_id, result, negative=False):
    matches = re.findall(r"GMP_TARGET_RESULT (PASS|FAIL) run=" + re.escape(run_id) + r" ([^\r\n]+)", text)
    if result["timed_out"] or len(matches) != 1:
        return False
    status, details = matches[0]
    pairs = re.findall(r"(\w+)=(True|False)", details)
    fields = dict(pairs)
    if negative:
        # The mutation makes P2 non-hostile, so the gate must report FAIL and
        # must show that the actor was not a valid target.
        return (status == "FAIL" and fields.get("hostile") == "False"
                and fields.get("target_p2") == "False" and result["exit_code"] == 1)
    expected = {field: "True" for field in FIELDS}
    return (status == "PASS" and set(FIELDS) <= set(fields)
            and all(fields[field] == expected[field] for field in FIELDS)
            and result["exit_code"] == 0)


def run_gate(proton, negative=False):
    run_id = uuid.uuid4().hex
    profile = "target-p2" + ("-negative" if negative else "")
    arguments = ["-gmp-target-probe", "-gmp-run-id=" + run_id]
    if negative:
        arguments.append("-gmp-target-negative")
    folder, invocation = command(profile, proton, arguments, windowed=True)
    result = run_invocation(folder, invocation, 300, "proton-launch.log")
    trace = ROOT / ("local/test-install/gmp-trace-" + run_id + ".log")
    text = ""
    if trace.exists():
        shutil.copy2(trace, folder / "logs/probe-trace.log")
        text = trace.read_text(errors="replace")
    else:
        log = ROOT / "local/test-install/BepInEx/LogOutput.log"
        if log.exists():
            shutil.copy2(log, folder / "logs/bepinex.log")
            text = log.read_text(errors="replace")
    passed = score(text, run_id, result, negative)
    summary = {
        "scope": "Single-process enemy targeting for a second local actor; not online co-op",
        "passed": passed,
        "negative": negative,
        "run_id": run_id,
        "process": result,
    }
    (folder / "logs/summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--negative", action="store_true",
                        help="Disable P2 before acquisition; the gate must fail for that reason")
    parser.add_argument("--suite", action="store_true", help="Positive gate then the disabled-P2 control")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton in local/runtime-config.json or pass --proton")
    results = [run_gate(args.proton, args.negative)]
    if args.suite and not args.negative:
        results.append(run_gate(args.proton, True))
    passed = all(r["passed"] for r in results) and (not args.suite or len(results) == 2)
    summary = {"scope": "Local enemy-targeting gate, NOT online co-op",
               "passed": passed, "gates": results}
    (ROOT / "local/target-suite-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
