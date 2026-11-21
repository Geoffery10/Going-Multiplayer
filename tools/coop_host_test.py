"""Production Steam coop host smoke: real AppID lobby + session controller.

Launches the game with the player-facing SteamCoop controller in host mode,
waits for a lobby, minimizes the window when xdotool is available, and exits.
"""
import argparse
import json
import os
import re
import signal
import subprocess
import sys
import time
import uuid
from pathlib import Path

from launch_isolated import ROOT, cleanup_owned, run_invocation
from launch_proton import command, DEFAULT_PROTON

LOBBY = re.compile(r"GMP_COOP_LOBBY (\d+)")
SESSION = re.compile(r"GMP_COOP_SESSION role=host lobby=(\d+) local_id=(\d+)")
TAB = re.compile(r"GMP_COOP_TAB installed")


def minimize_window():
    if not subprocess.run(["which", "xdotool"], capture_output=True).returncode == 0:
        return False
    try:
        found = subprocess.run(["xdotool", "search", "--name", "Going Under"],
                               capture_output=True, text=True, timeout=3)
        for window in found.stdout.split():
            subprocess.run(["xdotool", "windowminimize", window],
                           capture_output=True, timeout=3)
        return bool(found.stdout.strip())
    except (OSError, subprocess.TimeoutExpired):
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--timeout", type=int, default=90)
    parser.add_argument("--skip-build", action="store_true")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton in local/runtime-config.json or pass --proton")
    if not args.skip_build:
        subprocess.run([sys.executable, str(ROOT / "tools/build_plugin.py"), "--deploy-test"],
                       check=True)
    log = ROOT / "local/test-install/BepInEx/LogOutput.log"
    if log.exists():
        log.unlink()
    run_id = uuid.uuid4().hex
    folder, invocation = command("coop-host", args.proton, ["-gmp-coop-host"],
                                 windowed=True)
    process = subprocess.Popen(invocation, stdout=open("/tmp/gmp-coop-host.out", "w"),
                               stderr=subprocess.STDOUT, start_new_session=True)
    token = invocation[invocation.index("GMP_LAUNCH_TOKEN") + 1]
    text = ""
    minimized = False
    deadline = time.monotonic() + args.timeout
    try:
        while time.monotonic() < deadline:
            time.sleep(0.5)
            if log.exists():
                text = log.read_text(errors="replace")
            if not minimized and time.monotonic() + 10 > deadline:
                pass
            if not minimized and LOBBY.search(text):
                minimized = minimize_window()
            if LOBBY.search(text) and SESSION.search(text):
                break
    finally:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            process.wait(timeout=8)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
        cleaned = cleanup_owned(token)

    lobby = LOBBY.search(text)
    session = SESSION.search(text)
    tab_installed = TAB.search(text) is not None
    passed = (lobby is not None and session is not None and session.group(2) == "1"
              and tab_installed)
    summary = {
        "scope": "Production SteamCoop host controller under real AppID 1154810",
        "passed": passed, "run_id": run_id,
        "lobby": int(lobby.group(1)) if lobby else 0,
        "local_id": int(session.group(2)) if session else 0,
        "tab_installed": tab_installed,
        "minimized": minimized, "owned_processes_cleaned": cleaned,
    }
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "coop-host-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    (ROOT / "local/coop-host-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
