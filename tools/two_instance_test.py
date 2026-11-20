"""Start two isolated game copies on the same Steam account and verify both load.

This is the coexistence spike for the two-player test harness. It does not test
networking or gameplay; it proves that two copies can authenticate, load a scene
and exit concurrently with separate install, prefix, home and save folders.
"""
import argparse
import json
from pathlib import Path
import re
import shutil
import signal
import subprocess
import time
import uuid

from launch_isolated import ROOT, RUNTIME, cleanup_owned, snapshot_owned
from launch_proton import command, DEFAULT_PROTON

INSTALL_ROOT = ROOT / "local/test-installs"


def ensure_install(name):
    install = INSTALL_ROOT / name
    source = ROOT / "local/test-install"
    if not (install / "Going Under.exe").is_file():
        if install.exists():
            shutil.rmtree(install)
        shutil.copytree(source, install,
                        ignore=shutil.ignore_patterns("gmp-*", "LogOutput.log"))
    for pattern in ("gmp-trace-*.log", "gmp-palette-*.png"):
        for path in install.glob(pattern):
            path.unlink()
    log = install / "BepInEx/LogOutput.log"
    if log.exists():
        log.unlink()
    plugin = ROOT / "artifacts/GoingMultiplayer.dll"
    target = install / "BepInEx/plugins/GoingMultiplayer/GoingMultiplayer.dll"
    if plugin.is_file():
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(plugin, target)
    return install


def score_client(text, run_id, result, min_scenes=1):
    """Return PASS/FAIL/TIMEOUT for one client's smoke trace."""
    if result["timed_out"]:
        return "TIMEOUT"
    if result["exit_code"] != 0:
        return "FAIL"
    matches = re.findall(r"GMP_SMOKE_RESULT (PASS|FAIL) run=" + re.escape(run_id)
                         + r" hooks=(True|False) steam=(True|False) scenes=(\d+)", text)
    if len(matches) != 1:
        return "FAIL"
    status, hooks, steam, scenes = matches[0]
    return ("PASS" if status == "PASS" and hooks == steam == "True"
            and int(scenes) >= min_scenes else "FAIL")


def start_client(profile, install, proton, run_id, min_scenes, windowed):
    folder, invocation = command(profile, proton,
                                 ["-gmp-smoke", "-gmp-run-id=" + run_id,
                                  "-gmp-smoke-min-scenes=" + str(min_scenes)],
                                 install=install, windowed=windowed)
    log = (folder / "logs/two-instance.log").open("w")
    process = subprocess.Popen(invocation, stdout=log, stderr=subprocess.STDOUT,
                               start_new_session=True)
    token = invocation[invocation.index("GMP_LAUNCH_TOKEN") + 1]
    return {"profile": profile, "install": str(install), "run_id": run_id,
            "folder": folder, "process": process, "token": token, "log": log}


def wait_client(client, deadline):
    process = client["process"]
    remaining = max(1.0, deadline - time.monotonic())
    timed_out = False
    try:
        code = process.wait(timeout=remaining)
    except subprocess.TimeoutExpired:
        timed_out = True
        # Capture before cleanup: a PASS trace can still precede a native
        # shutdown hang. It must remain a timeout, not be promoted to success.
        snapshot = snapshot_owned(client["token"])
        (client["folder"] / "logs/timeout-processes.json").write_text(
            json.dumps(snapshot, indent=2) + "\n")
        os_kill_group(process.pid)
        code = process.wait()
    cleaned = cleanup_owned(client["token"])
    return {"timed_out": timed_out, "exit_code": code, "owned_processes_cleaned": cleaned}


def os_kill_group(pid):
    try:
        import os
        os.killpg(pid, signal.SIGTERM)
        time.sleep(1)
        os.killpg(pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--min-scenes", type=int, default=2,
                        help="Scenes both clients must reach; 2 is the title screen")
    parser.add_argument("--windowed", action="store_true",
                        help="Use rendered 1280x720 windows, matching the future two-client tests")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton in local/runtime-config.json or pass --proton")

    runs = {"a": uuid.uuid4().hex, "b": uuid.uuid4().hex}
    installs = {"a": ensure_install("a"), "b": ensure_install("b")}
    clients = {}
    deadline = time.monotonic() + args.timeout
    try:
        # Start the second only after the first has started, mirroring the
        # playbook's "copy k ready before copy k+1" rule without waiting for a
        # readiness signal that this smoke mode does not expose.
        for name in ("a", "b"):
            clients[name] = start_client("coexist-" + name, installs[name],
                                         args.proton, runs[name], args.min_scenes,
                                         args.windowed)
            time.sleep(2)
        results = {name: wait_client(client, deadline) for name, client in clients.items()}
    finally:
        for client in clients.values():
            if client["process"].poll() is None:
                os_kill_group(client["process"].pid)
            cleanup_owned(client["token"])
            client["log"].close()

    summary = {"scope": "Two same-account isolated clients; startup only, NOT co-op",
               "passed": True, "clients": {}}
    for name in ("a", "b"):
        client = clients[name]
        trace = Path(client["install"]) / ("gmp-trace-" + client["run_id"] + ".log")
        text = ""
        if trace.exists():
            shutil.copy2(trace, client["folder"] / "logs/probe-trace.log")
            text = trace.read_text(errors="replace")
        bep = Path(client["install"]) / "BepInEx/LogOutput.log"
        if bep.exists():
            shutil.copy2(bep, client["folder"] / "logs/bepinex.log")
        observed = score_client(text, client["run_id"], results[name], args.min_scenes)
        entry = {"profile": client["profile"], "run_id": client["run_id"],
                 "observed": observed, "process": results[name]}
        summary["clients"][name] = entry
        summary["passed"] = summary["passed"] and observed == "PASS"
    summary["distinct_installs"] = clients["a"]["install"] != clients["b"]["install"]
    summary["distinct_prefixes"] = (RUNTIME / "profiles/coexist-a"
                                    != RUNTIME / "profiles/coexist-b")
    summary["passed"] = (summary["passed"] and summary["distinct_installs"]
                         and summary["distinct_prefixes"])
    (ROOT / "local/two-instance-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    raise SystemExit(0 if summary["passed"] else 1)


if __name__ == "__main__":
    main()
