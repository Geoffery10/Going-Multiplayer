"""L2 two-client loopback gate: presence and movement replication over UDP.

Two real clients, same machine, test-only transport. This is not a production
session, lobby or gameplay-sync implementation.
"""
import argparse
import json
import math
from pathlib import Path
import re
import shutil
import socket
import time
import uuid

from two_instance_test import ensure_install, wait_client
from launch_isolated import ROOT, cleanup_owned
from launch_proton import command, DEFAULT_PROTON

RESULT = re.compile(
    r"GMP_NET_RESULT (PASS|FAIL) run=(\S+) role=(\w+) session=(\S+) "
    r"local_id=(\d+) peer_id=(\d+) connected=(True|False) "
    r"local_delta=([-\d.]+) remote_delta=([-\d.]+) updates=(\d+) "
    r"final_local=([-\d.]+),([-\d.]+) final_remote=([-\d.]+),([-\d.]+) "
    r"negative=(True|False)")


def parse(text, run_id):
    matches = RESULT.findall(text)
    if len(matches) != 1:
        return None
    (status, run, role, session, local_id, peer_id, connected,
     local_delta, remote_delta, updates, lx, lz, rx, rz, negative) = matches[0]
    if run != run_id:
        return None
    return {"status": status, "role": role, "session": session,
            "local_id": int(local_id), "peer_id": int(peer_id),
            "connected": connected == "True",
            "local_delta": float(local_delta), "remote_delta": float(remote_delta),
            "updates": int(updates),
            "final_local": (float(lx), float(lz)),
            "final_remote": (float(rx), float(rz)),
            "negative": negative == "True"}


def distance(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def score_pair(host_text, guest_text, run_id, host_result, guest_result, negative=False):
    host = parse(host_text, run_id)
    guest = parse(guest_text, run_id)
    if host is None or guest is None:
        return False
    if host["role"] != "host" or guest["role"] != "guest":
        return False
    if host["local_id"] != 1 or guest["local_id"] != 2:
        return False
    if host["peer_id"] != 2 or guest["peer_id"] != 1:
        return False
    if not host["connected"] or not guest["connected"]:
        return False
    if host["session"] == "none" or guest["session"] == "none":
        return False
    if host["session"] != guest["session"]:
        return False
    if host["updates"] < 20 or guest["updates"] < 20:
        return False
    if host_result["timed_out"] or guest_result["timed_out"]:
        return False

    if negative:
        # The host mutation skips applying remote poses. The host must fail for
        # that reason; the guest is allowed to pass.
        return (host["status"] == "FAIL" and host["negative"]
                and host["remote_delta"] < 0.2 and host_result["exit_code"] == 1)

    if host["status"] != "PASS" or guest["status"] != "PASS":
        return False
    if host_result["exit_code"] != 0 or guest_result["exit_code"] != 0:
        return False
    if host["remote_delta"] <= 0.5 or guest["local_delta"] <= 0.5:
        return False
    if host["local_delta"] > 1.0:
        return False  # the host actor is a stationary control in this gate
    # Cross-check what each side finally saw against the other's local truth.
    if distance(host["final_remote"], guest["final_local"]) > 0.75:
        return False
    if distance(guest["final_remote"], host["final_local"]) > 0.75:
        return False
    return True


def free_port():
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]
    finally:
        probe.close()


def start_client(profile, install, proton, role, port, run_id, negative):
    arguments = ["-gmp-net-probe", "-gmp-net=" + role,
                 "-gmp-net-port=" + str(port), "-gmp-run-id=" + run_id]
    if negative:
        arguments.append("-gmp-net-negative")
    folder, invocation = command(profile, proton, arguments,
                                 install=install, windowed=True)
    log = (folder / "logs/net.log").open("w")
    import subprocess
    process = subprocess.Popen(invocation, stdout=log, stderr=subprocess.STDOUT,
                               start_new_session=True)
    token = invocation[invocation.index("GMP_LAUNCH_TOKEN") + 1]
    return {"profile": profile, "install": str(install), "run_id": run_id,
            "folder": folder, "process": process, "token": token, "log": log}


def run_gate(proton, timeout=180, negative=False):
    run_id = uuid.uuid4().hex
    port = free_port()
    installs = {"host": ensure_install("a"), "guest": ensure_install("b")}
    clients = {}
    deadline = time.monotonic() + timeout
    try:
        clients["host"] = start_client("net-host", installs["host"], proton,
                                       "host", port, run_id, negative)
        time.sleep(2)
        clients["guest"] = start_client("net-guest", installs["guest"], proton,
                                        "guest", port, run_id, False)
        results = {name: wait_client(client, deadline) for name, client in clients.items()}
    finally:
        for client in clients.values():
            if client["process"].poll() is None:
                try:
                    import os, signal
                    os.killpg(client["process"].pid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    pass
            cleanup_owned(client["token"])
            client["log"].close()

    texts = {}
    for name, client in clients.items():
        trace = Path(client["install"]) / ("gmp-trace-" + client["run_id"] + ".log")
        texts[name] = ""
        if trace.exists():
            shutil.copy2(trace, client["folder"] / "logs/probe-trace.log")
            texts[name] = trace.read_text(errors="replace")
        bep = Path(client["install"]) / "BepInEx/LogOutput.log"
        if bep.exists():
            shutil.copy2(bep, client["folder"] / "logs/bepinex.log")
    passed = score_pair(texts["host"], texts["guest"], run_id,
                        results["host"], results["guest"], negative)
    host = parse(texts["host"], run_id)
    guest = parse(texts["guest"], run_id)
    summary = {"scope": "Two-process loopback presence/movement; NOT production online co-op",
               "passed": passed, "negative": negative, "run_id": run_id, "port": port,
               "host": {"observed": host, "process": results["host"]},
               "guest": {"observed": guest, "process": results["guest"]}}
    folder = ROOT / "local/profiles" / ("net-" + run_id)
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--negative", action="store_true",
                        help="Host skips applying remote poses; the gate must fail")
    parser.add_argument("--suite", action="store_true", help="Positive gate then mutation control")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton in local/runtime-config.json or pass --proton")
    results = [run_gate(args.proton, args.timeout, args.negative)]
    if args.suite and not args.negative:
        results.append(run_gate(args.proton, args.timeout, True))
    passed = all(r["passed"] for r in results) and (not args.suite or len(results) == 2)
    summary = {"scope": "L2 loopback presence/movement suite, NOT online co-op",
               "passed": passed, "gates": results}
    (ROOT / "local/net-suite-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
