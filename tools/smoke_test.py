"""Run authenticated real-game loader gates, including an intentionally broken control."""
import argparse
import json
from pathlib import Path
import re
import shutil
import uuid

from launch_isolated import ROOT, run_invocation
from launch_proton import command, DEFAULT_PROTON


def score(text, run_id, result):
    if result["timed_out"]:
        return "TIMEOUT"
    if result["exit_code"] != 0:
        return "FAIL"
    pattern = r"GMP_SMOKE_RESULT (PASS|FAIL) run=" + re.escape(run_id) + r" hooks=(True|False) steam=(True|False) scenes=(\d+)"
    matches = re.findall(pattern, text)
    if len(matches) != 1:
        return "FAIL"
    status, hooks, steam, scenes = matches[0]
    return "PASS" if status == "PASS" and hooks == steam == "True" and int(scenes) > 0 else "FAIL"


def run_gate(proton, negative=False):
    run_id = uuid.uuid4().hex
    name = "negative-hook" if negative else "loader"
    folder, invocation = command("smoke-" + name, proton,
                                 ["-gmp-smoke", "-gmp-run-id=" + run_id]
                                 + (["-gmp-negative-hook"] if negative else []))
    result = run_invocation(folder, invocation, 90, "proton-launch.log")
    bepinex = ROOT / "local/test-install/BepInEx/LogOutput.log"
    text = bepinex.read_text(errors="replace") if bepinex.exists() else ""
    if bepinex.exists():
        shutil.copy2(bepinex, folder / "logs/bepinex.log")
    trace = ROOT / ("local/test-install/gmp-trace-" + run_id + ".log")
    if trace.exists():
        shutil.copy2(trace, folder / "logs/probe-trace.log")
        text = trace.read_text(errors="replace")
    outcome = score(text, run_id, result)
    # A negative control must fail for our injected reason, not any launch failure.
    expected_failure = (
        negative and not result["timed_out"] and result["exit_code"] == 1
        and re.search(r"GMP_SMOKE_RESULT FAIL run=" + run_id
                      + r" hooks=False steam=True scenes=[1-9]\d*", text) is not None
    )
    return {"gate": name, "run_id": run_id, "observed": outcome,
            "expected": "FAIL" if negative else "PASS",
            "matched_expectation": expected_failure if negative else outcome == "PASS"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--suite", action="store_true", help="Also run the negative control")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Set --proton to an installed Proton directory, or configure local/runtime-config.json")
    results = [run_gate(args.proton)]
    if args.suite and results[0]["matched_expectation"]:
        results.append(run_gate(args.proton, negative=True))
    passed = all(r["matched_expectation"] for r in results) and (not args.suite or len(results) == 2)
    summary = {"scope": "Real-game loader only; NOT co-op gameplay", "passed": passed, "gates": results}
    (ROOT / "local/smoke-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
