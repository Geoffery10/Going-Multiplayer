"""Real-game independent-player gate. Does not test online replication or combat."""
import argparse
import json
from pathlib import Path
import re
import shutil
import uuid

from launch_isolated import ROOT, run_invocation
from launch_proton import command, DEFAULT_PROTON

FIELDS = ("identities", "data", "health", "inventory", "input", "movement", "palette", "actor")


def score(text, run_id, count, result, negative=False):
    matches = re.findall(r"GMP_SEAT_RESULT (PASS|FAIL) run=" + re.escape(run_id)
                         + r" count=" + str(count) + r" ([^\r\n]+)", text)
    if result["timed_out"] or len(matches) != 1:
        return False
    status, details = matches[0]
    pairs = re.findall(r"(\w+)=(True|False)", details)
    if len(pairs) != len(FIELDS):
        return False
    fields = dict(pairs)
    expected = {field: "False" if negative and field == "health" else "True" for field in FIELDS}
    return (fields == expected and status == ("FAIL" if negative else "PASS")
            and result["exit_code"] == (1 if negative else 0))


def run_gate(proton, count=2, negative=False, windowed=False):
    run_id = uuid.uuid4().hex
    profile = "seats-" + str(count) + ("-negative" if negative else "")
    folder, invocation = command(profile, proton, [
        "-gmp-seat-probe", "-gmp-seat-count=" + str(count), "-gmp-run-id=" + run_id
    ] + (["-gmp-negative-health"] if negative else [])
      + (["-gmp-capture"] if windowed else []), windowed=windowed)
    result = run_invocation(folder, invocation, 120, "proton-launch.log")
    log = ROOT / "local/test-install/BepInEx/LogOutput.log"
    text = log.read_text(errors="replace") if log.exists() else ""
    if log.exists():
        shutil.copy2(log, folder / "logs/bepinex.log")
    trace = ROOT / ("local/test-install/gmp-trace-" + run_id + ".log")
    if trace.exists():
        shutil.copy2(trace, folder / "logs/probe-trace.log")
        # Trace writes are immediately flushed; do not depend on buffered Bep logs.
        text = trace.read_text(errors="replace")
    screenshot = ROOT / ("local/test-install/gmp-palette-" + run_id + ".png")
    if screenshot.exists():
        shutil.copy2(screenshot, folder / "logs/palette.png")
    passed = score(text, run_id, count, result, negative)
    summary = {"scope": "Single-process independent-player integration, not online co-op",
               "passed": passed, "count": count, "negative_health": negative,
               "run_id": run_id, "process": result}
    (folder / "logs/summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--count", type=int, default=2)
    parser.add_argument("--negative-health", action="store_true")
    parser.add_argument("--windowed", action="store_true", help="Use rendered graphics and capture only the game framebuffer")
    parser.add_argument("--suite", action="store_true", help="Two seats, negative health control, then five seats")
    args = parser.parse_args()
    if args.proton is None or not 2 <= args.count <= 8:
        parser.error("Configure Proton and a count between 2 and 8")
    results = [run_gate(args.proton, args.count, args.negative_health, args.windowed)]
    if args.suite and results[0]["passed"]:
        results.append(run_gate(args.proton, 2, True, args.windowed))
        if results[-1]["passed"]:
            results.append(run_gate(args.proton, 5, False, args.windowed))
    passed = all(r["passed"] for r in results) and (not args.suite or len(results) == 3)
    summary = {"scope": "Local actor integration, NOT online co-op", "passed": passed, "gates": results}
    (ROOT / "local/seat-suite-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
