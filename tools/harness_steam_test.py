"""M0a Steam adapter smoke: lobby, join and P2P under Wine.

Uses the running Steam session, Spacewar (AppID 480), a pre-initialised
Proton/Wine prefix, and two harness processes. The default same-account mode is
partial evidence; a second account/machine makes `members>=2` true unchanged.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

from launch_proton import stage_runner

ROOT = Path(__file__).resolve().parents[1]
PUBLISH = ROOT / "local/harness/publish"
RUNTIME = Path("/tmp/opencode/going-under-coop")
DEFAULT_PREFIX = RUNTIME / "profiles/harness-steam10/compatdata/pfx"
RESULT = re.compile(
    r"GMP_HARNESS_RESULT (PASS|FAIL) transport=steam role=(\w+) run=(\S+) "
    r"session=(\S+) port=(\d+) lobby=(\d+) pings=(\d+) pongs=(\d+) "
    r"rtt_ms=([-\d.]+) members=(\d+) same_account=(True|False) reason=(\S+)")
LOBBY = re.compile(r"GMP_HARNESS_LOBBY (\d+)")


def find_proton(explicit):
    if explicit:
        return Path(explicit)
    base = Path.home() / ".local/share/Steam/steamapps/common"
    for name in ("Proton 10.0", "Proton 11.0", "Proton - Experimental"):
        candidate = base / name
        if candidate.is_dir():
            return candidate
    raise SystemExit("No Proton 10/11/Experimental install found; pass --proton")


def start(prefix, stage, role, run_id, seconds, lobby=0):
    env = os.environ.copy()
    env.update(WINEPREFIX=str(prefix), WINEDEBUG="-all", SteamAppId="480",
               SteamGameId="480",
               STEAM_COMPAT_CLIENT_INSTALL_PATH=str(Path.home() / ".local/share/Steam"))
    args = [str(stage / "files/bin/wine64"), "SteamHarness.exe", "--transport", "steam",
            "--role", role, "--run-id", run_id, "--seconds", str(seconds),
            "--allow-same-account"]
    if lobby:
        args += ["--lobby", str(lobby)]
    (ROOT / "local" / "harness-logs").mkdir(parents=True, exist_ok=True)
    path = ROOT / "local/harness-logs" / (role + "-" + run_id + ".log")
    handle = path.open("w")
    process = subprocess.Popen(args, cwd=str(PUBLISH), env=env, stdout=handle,
                               stderr=subprocess.STDOUT, start_new_session=True)
    return {"process": process, "log": handle, "path": path}


def wait_for_lobby(client, deadline):
    while time.monotonic() < deadline:
        if client["path"].exists():
            match = LOBBY.search(client["path"].read_text(errors="replace"))
            if match:
                return int(match.group(1))
        if client["process"].poll() is not None:
            return 0
        time.sleep(0.2)
    return 0


def finish(client, deadline):
    remaining = max(1.0, deadline - time.monotonic())
    timed_out = False
    try:
        code = client["process"].wait(timeout=remaining)
    except subprocess.TimeoutExpired:
        timed_out = True
        try:
            import signal
            os.killpg(client["process"].pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
        code = client["process"].wait()
    client["log"].close()
    return {"timed_out": timed_out, "exit_code": code}


def parse(text):
    match = RESULT.search(text)
    return match.groups() if match else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", default=None)
    parser.add_argument("--prefix", type=Path, default=DEFAULT_PREFIX)
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--seconds", type=int, default=18)
    parser.add_argument("--skip-build", action="store_true")
    args = parser.parse_args()
    proton = find_proton(args.proton)
    stage = stage_runner(proton)
    prefix = args.prefix
    if not any(prefix.rglob("steamclient64.dll")):
        raise SystemExit("Prefix lacks Steam: " + str(prefix)
                         + " (initialise it with one Proton run first)")
    if not args.skip_build:
        subprocess.run([sys.executable, str(ROOT / "tools/build_harness.py")], check=True)
    run_id = uuid.uuid4().hex[:12]
    deadline = time.monotonic() + args.timeout
    host = start(prefix, stage, "host", run_id, args.seconds + 8)
    guest = None
    try:
        lobby = wait_for_lobby(host, deadline)
        if lobby == 0:
            raise RuntimeError("Host did not create a lobby")
        guest = start(prefix, stage, "guest", run_id, args.seconds, lobby)
        results = {"host": finish(host, deadline), "guest": finish(guest, deadline)}
    finally:
        for client in (host, guest):
            if client is not None and client["process"].poll() is None:
                try:
                    import signal
                    os.killpg(client["process"].pid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    pass
            if client is not None and not client["log"].closed:
                client["log"].close()
    host_result = parse(host["path"].read_text(errors="replace"))
    guest_result = parse(guest["path"].read_text(errors="replace"))
    passed = (host_result is not None and guest_result is not None
              and host_result[0] == guest_result[0] == "PASS"
              and host_result[2] == guest_result[2] == run_id
              and host_result[3] == guest_result[3] != "none"
              and results["host"]["exit_code"] == 0
              and results["guest"]["exit_code"] == 0
              and not results["host"]["timed_out"]
              and not results["guest"]["timed_out"]
              and int(host_result[6]) >= 4 and int(guest_result[6]) >= 4
              and int(host_result[7]) >= 2 and int(guest_result[7]) >= 2
              and float(host_result[8]) < 1000 and float(guest_result[8]) < 1000)
    summary = {
        "scope": "M0a Steam lobby/join/P2P harness; same-account partial unless members>=2",
        "passed": passed, "proton": str(proton), "prefix": str(prefix),
        "run_id": run_id, "lobby": lobby,
        "host": {"observed": host_result, "process": results["host"]},
        "guest": {"observed": guest_result, "process": results["guest"]},
    }
    (ROOT / "local/harness-steam-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
