"""Exercise native P2 input adapters using deterministic in-game pad snapshots."""
import argparse
import json
import re
import shutil
import uuid

from launch_isolated import ROOT, run_invocation
from launch_proton import command, DEFAULT_PROTON

FIELDS = ("base", "mapping", "isolation", "movement", "disconnect", "ownership")


def score(text, run_id, result, negative=False):
    matches = re.findall(r"GMP_CONTROLLER_RESULT (PASS|FAIL) run=" + re.escape(run_id)
                         + r" ([^\r\n]+)", text)
    if result["timed_out"] or len(matches) != 1:
        return False
    status, details = matches[0]
    pairs = re.findall(r"(\w+)=(True|False)", details)
    expected = {f: "False" if negative and f == "isolation" else "True" for f in FIELDS}
    return (len(pairs) == len(FIELDS) and dict(pairs) == expected
            and status == ("FAIL" if negative else "PASS")
            and result["exit_code"] == (1 if negative else 0))


def run_gate(negative=False):
    run_id = uuid.uuid4().hex
    folder, invocation = command("controller-probe" + ("-negative" if negative else ""), DEFAULT_PROTON,
        ["-gmp-seat-probe", "-gmp-controller-probe", "-gmp-run-id=" + run_id]
        + (["-gmp-negative-controller-route"] if negative else []), windowed=True)
    result = run_invocation(folder, invocation, 120, "proton-launch.log")
    trace = ROOT / ("local/test-install/gmp-trace-" + run_id + ".log")
    text = trace.read_text(errors="replace") if trace.exists() else ""
    if trace.exists():
        shutil.copy2(trace, folder / "logs/probe-trace.log")
    passed = score(text, run_id, result, negative)
    hardware = re.findall(r"GMP_CONTROLLER_RESULT [^\r\n]* hardware=(\d+)", text)
    summary = {"passed": passed, "negative": negative, "run_id": run_id,
               "hardware_gamepads_seen": int(hardware[0]) if len(hardware) == 1 else None,
               "scope": "Native adapter via simulated snapshots; physical button presses NOT tested",
               "process": result}
    (folder / "logs/summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", action="store_true")
    args = parser.parse_args()
    if DEFAULT_PROTON is None:
        parser.error("Configure local/runtime-config.json first")
    results = [run_gate()]
    if args.suite and results[0]["passed"]:
        results.append(run_gate(True))
    passed = all(r["passed"] for r in results) and (not args.suite or len(results) == 2)
    (ROOT / "local/controller-suite-summary.json").write_text(json.dumps({
        "passed": passed, "gates": results}, indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
