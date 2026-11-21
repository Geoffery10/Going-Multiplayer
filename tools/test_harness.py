"""M0a harness UDP positive/mutation test (no Steam account, no game)."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
PUBLISH = ROOT / "local/harness/publish"
PREFIX = Path("/tmp/opencode/going-under-coop/harness-prefix")
RESULT = re.compile(
    r"GMP_HARNESS_RESULT (PASS|FAIL) transport=udp role=(\w+) run=(\S+) "
    r"session=(\S+) port=(\d+) lobby=(\d+) pings=(\d+) pongs=(\d+) "
    r"rtt_ms=([-\d.]+) members=(\d+) same_account=(True|False) reason=(\S+)")


def parse(text):
    matches = RESULT.findall(text)
    return matches[-1] if matches else None


def ensure_prefix():
    PREFIX.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update(WINEPREFIX=str(PREFIX), WINEDEBUG="-all")
    marker = PREFIX / "drive_c"
    if not marker.is_dir():
        subprocess.run(["wineboot", "--init"], env=env, check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return env


def run_udp(env, run_id, port, seconds=6, guest_run_id=None):
    logs = {}
    processes = {}
    for role in ("host", "guest"):
        args = ["wine", "SteamHarness.exe", "--transport", "udp", "--role", role,
                "--port", str(port), "--seconds", str(seconds),
                "--run-id", guest_run_id if role == "guest" and guest_run_id else run_id]
        path = PUBLISH / (role + "-" + run_id + ".out")
        processes[role] = subprocess.Popen(args, cwd=str(PUBLISH), env=env,
                                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                           text=True)
        logs[role] = path
        if role == "host":
            time.sleep(2)
    outputs = {}
    for role, process in processes.items():
        try:
            outputs[role] = process.communicate(timeout=seconds + 20)[0]
        except subprocess.TimeoutExpired:
            process.kill()
            outputs[role] = process.communicate()[0]
        logs[role].write_text(outputs[role])
    return outputs


def positive(env):
    run_id = "udp-" + str(int(time.time()))
    outputs = run_udp(env, run_id, 39140)
    host = parse(outputs["host"])
    guest = parse(outputs["guest"])
    if host is None or guest is None:
        return False, {"reason": "missing-result", "outputs": outputs}
    ok = (host[0] == "PASS" and guest[0] == "PASS"
          and host[2] == guest[2] == run_id
          and host[3] == guest[3] != "none"
          and int(host[6]) >= 4 and int(guest[6]) >= 4
          and int(host[7]) >= 2 and int(guest[7]) >= 2
          and float(host[8]) < 1000 and float(guest[8]) < 1000)
    return ok, {"host": host, "guest": guest}


def mutation(env):
    outputs = run_udp(env, "udp-a", 39141, guest_run_id="udp-b")
    host = parse(outputs["host"])
    guest = parse(outputs["guest"])
    ok = host is not None and guest is not None and host[0] == "FAIL" and guest[0] == "FAIL"
    return ok, {"host": host, "guest": guest}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-build", action="store_true")
    args = parser.parse_args()
    if not args.skip_build:
        subprocess.run([sys.executable, str(ROOT / "tools/build_harness.py")], check=True)
    env = ensure_prefix()
    results = []
    ok, detail = positive(env)
    results.append({"gate": "positive", "passed": ok, "detail": detail})
    print("HARNESS UDP POSITIVE: " + ("PASS" if ok else "FAIL"))
    ok2, detail2 = mutation(env)
    results.append({"gate": "mismatched-run", "passed": ok2, "detail": detail2})
    print("HARNESS UDP MUTATION: " + ("PASS" if ok2 else "FAIL"))
    passed = ok and ok2
    (ROOT / "local/harness-summary.json").write_text(json.dumps(
        {"scope": "M0a standalone harness UDP positive/mutation; Steam path requires a second account",
         "passed": passed, "gates": results}, indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
