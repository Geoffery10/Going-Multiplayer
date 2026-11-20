"""Verify test-only presentation skipping against the unskipped native control."""
import argparse
import json
import math
import re
import shutil
import uuid

from launch_isolated import ROOT, run_invocation
from launch_proton import command, DEFAULT_PROTON

RESULT = re.compile(r"^GMP_STARTUP_RESULT (PASS|FAIL) run=(\S+) stinger=(True|False) "
                    r"skipped=(True|False) title=(True|False) videos_stopped=(True|False) "
                    r"seconds=([-\d.]+)$", re.MULTILINE)


def parse(text, run_id):
    if sum(line.startswith("GMP_STARTUP_RESULT") for line in text.splitlines()) != 1:
        return None
    matches = RESULT.findall(text)
    if len(matches) != 1:
        return None
    status, run, stinger, skipped, title, videos, seconds = matches[0]
    try:
        seconds = float(seconds)
    except ValueError:
        return None
    if run != run_id or not math.isfinite(seconds) or seconds < 0:
        return None
    return dict(status=status, stinger=stinger == "True", skipped=skipped == "True",
                title=title == "True", videos_stopped=videos == "True", seconds=seconds)


def score(text, run_id, process, normal=False):
    result = parse(text, run_id)
    if sum(line.startswith("GMP_SMOKE_RESULT") for line in text.splitlines()) != 1:
        return False
    smoke = re.findall(r"^GMP_SMOKE_RESULT PASS run=" + re.escape(run_id)
                       + r" hooks=True steam=True scenes=(\d+)$", text, re.MULTILINE)
    if result is None or process["timed_out"] or len(smoke) != 1 or int(smoke[0]) < 2:
        return False
    if not result["stinger"] or not result["title"]:
        return False
    if normal:
        return (result["status"] == "FAIL" and process["exit_code"] == 1
                and not result["skipped"] and not result["videos_stopped"])
    return (result["status"] == "PASS" and process["exit_code"] == 0
            and result["skipped"] and result["videos_stopped"])


def run_gate(normal=False):
    run_id = uuid.uuid4().hex
    args = ["-gmp-smoke", "-gmp-startup-probe", "-gmp-smoke-min-scenes=2",
            "-gmp-run-id=" + run_id]
    if normal:
        args.append("-gmp-normal-startup")
    folder, invocation = command("startup-normal" if normal else "startup-fast",
                                  DEFAULT_PROTON, args, windowed=True)
    process = run_invocation(folder, invocation, 120, "proton-launch.log")
    trace = ROOT / ("local/test-install/gmp-trace-" + run_id + ".log")
    text = trace.read_text(errors="replace") if trace.exists() else ""
    if trace.exists():
        shutil.copy2(trace, folder / "logs/probe-trace.log")
    summary = dict(passed=score(text, run_id, process, normal), normal=normal,
                   run_id=run_id, observed=parse(text, run_id), process=process)
    (folder / "logs/summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2), flush=True)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", action="store_true")
    args = parser.parse_args()
    if DEFAULT_PROTON is None:
        parser.error("Configure local/runtime-config.json first")
    gates = [run_gate()]
    if args.suite:
        gates.append(run_gate(normal=True))
    passed = all(gate["passed"] for gate in gates)
    summary = dict(passed=passed, gates=gates)
    if passed and len(gates) == 2:
        summary["stinger_to_title_seconds_saved"] = (
            gates[1]["observed"]["seconds"] - gates[0]["observed"]["seconds"])
    (ROOT / "local/startup-suite-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2), flush=True)
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
