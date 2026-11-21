"""M1 long-session gate: persistent core, snapshots, interpolation, scene survival.

Two real clients over the loopback test transport. This is not a production
transport and not a playable co-op session.
"""
import argparse
import json
import math
import re
import shutil
import socket
import subprocess
import time
import uuid
from pathlib import Path

from two_instance_test import ensure_install, wait_client
from launch_isolated import ROOT, cleanup_owned
from launch_proton import command, DEFAULT_PROTON

RESULT = re.compile(
    r"GMP_SESSION_RESULT (PASS|FAIL) run=(\S+) role=(\w+) session=(\S+) "
    r"epoch=(\d+) local_id=(\d+) remote_id=(\d+) peers=(\d+) sent=(\d+) "
    r"received=(\d+) rejected=(\d+) stale=(\d+) remote_delta=([-\d.]+) "
    r"dungeon_delta=([-\d.]+) actor_alive=(True|False) "
    r"health_kept=(True|False) attack_host=(True|False) "
    r"attack_local=(True|False) cmd_sent=(\d+) cmd_recv=(\d+) "
    r"focus_host=(True|False) focus_local=(True|False) "
    r"cam_delta=([-\d.]+) inv_moved=(True|False) inv_local_moved=(True|False) "
    r"recon_delta=([-\d.]+) recon_corrections=(\d+) recon_hard=(\d+) "
    r"recon_error=([-\d.]+) view_ok=(True|False) "
    r"ready_barrier=(True|False) done_barrier=(True|False) "
    r"negative=(True|False) scene=(\S+)")


def parse(text, run_id):
    matches = RESULT.findall(text)
    if len(matches) != 1:
        return None
    (status, run, role, session, epoch, local_id, remote_id, peers, sent,
     received, rejected, stale, remote_delta, dungeon_delta, actor_alive,
     health_kept, attack_host, attack_local, cmd_sent, cmd_recv,
     focus_host, focus_local, cam_delta, inv_moved, inv_local_moved,
     recon_delta, recon_corrections, recon_hard, recon_error,
     view_ok, ready_barrier, done_barrier, negative, scene) = matches[0]
    if run != run_id:
        return None
    return {
        "status": status, "role": role, "session": session,
        "epoch": int(epoch), "local_id": int(local_id), "remote_id": int(remote_id),
        "peers": int(peers), "sent": int(sent), "received": int(received),
        "rejected": int(rejected), "stale": int(stale),
        "remote_delta": float(remote_delta), "dungeon_delta": float(dungeon_delta),
        "actor_alive": actor_alive == "True",
        "health_kept": health_kept == "True",
        "attack_host": attack_host == "True",
        "attack_local": attack_local == "True",
        "focus_host": focus_host == "True",
        "focus_local": focus_local == "True",
        "cam_delta": float(cam_delta),
        "inv_moved": inv_moved == "True",
        "inv_local_moved": inv_local_moved == "True",
        "recon_delta": float(recon_delta),
        "recon_corrections": int(recon_corrections),
        "recon_hard": int(recon_hard),
        "recon_error": float(recon_error),
        "cmd_sent": int(cmd_sent), "cmd_recv": int(cmd_recv),
        "view_ok": view_ok == "True",
        "ready_barrier": ready_barrier == "True",
        "done_barrier": done_barrier == "True",
        "negative": negative == "True", "scene": scene,
    }


def score_pair(host_text, guest_text, run_id, host_result, guest_result, negative=None):
    host = parse(host_text, run_id)
    guest = parse(guest_text, run_id)
    if host is None or guest is None:
        return False
    if host["role"] != "host" or guest["role"] != "guest":
        return False
    if host["local_id"] != 1 or guest["local_id"] != 2:
        return False
    if host["remote_id"] != 2 or guest["remote_id"] != 1:
        return False
    if host["session"] == "none" or guest["session"] == "none":
        return False
    if host["session"] != guest["session"]:
        return False
    if host["epoch"] != guest["epoch"]:
        return False
    if host_result["timed_out"] or guest_result["timed_out"]:
        return False

    healthy = (host["peers"] == 1 and guest["peers"] == 1
               and host["sent"] >= 20 and guest["sent"] >= 20
               and host["received"] >= 10 and guest["received"] >= 10
               and host["actor_alive"] and guest["actor_alive"]
               and host["health_kept"] and guest["health_kept"]
               and host["view_ok"] and guest["view_ok"]
               and host["ready_barrier"] and guest["ready_barrier"]
               and host["done_barrier"] and guest["done_barrier"]
               and host["scene"] != "none" and guest["scene"] != "none")
    if not healthy:
        return False

    if negative == "snapshot":
        return (host["status"] == "PASS" and host_result["exit_code"] == 0
                and not host["negative"]
                and guest["status"] == "FAIL" and guest["negative"]
                and guest_result["exit_code"] == 1
                and guest["remote_delta"] < 0.2)

    if negative == "command":
        return (host["status"] == "FAIL" and host_result["exit_code"] == 1
                and not host["negative"]
                and not host["attack_host"])

    if negative == "recon":
        return (host["status"] == "PASS" and guest["status"] == "FAIL"
                and guest_result["exit_code"] == 1
                and guest["recon_delta"] < 0.5
                and guest["recon_corrections"] == 0)

    if host["status"] != "PASS" or guest["status"] != "PASS":
        return False
    if host_result["exit_code"] != 0 or guest_result["exit_code"] != 0:
        return False
    if host["negative"] or guest["negative"]:
        return False
    return (host["remote_delta"] > 0.25 and guest["remote_delta"] > 0.25
            and host["dungeon_delta"] > 0.05 and guest["dungeon_delta"] > 0.05
            and host["attack_host"] and host["cmd_recv"] >= 1
            and guest["cmd_sent"] >= 1 and not guest["attack_local"]
            and not host["attack_local"]
            and host["focus_host"] and host["cam_delta"] <= 0.05
            and not guest["focus_local"]
            and guest["recon_corrections"] >= 1 and guest["recon_error"] <= 0.5)


def free_port():
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]
    finally:
        probe.close()


def start_client(profile, install, proton, role, port, run_id, negative):
    arguments = ["-gmp-session-probe", "-gmp-session=" + role,
                 "-gmp-session-port=" + str(port), "-gmp-run-id=" + run_id]
    if negative == "snapshot" and role == "guest":
        arguments.append("-gmp-session-negative")
    if negative == "command" and role == "host":
        arguments.append("-gmp-session-cmd-negative")
    if negative == "recon" and role == "guest":
        arguments.append("-gmp-session-recon-negative")
    folder, invocation = command(profile, proton, arguments,
                                 install=install, windowed=True)
    log = (folder / "logs/session.log").open("w")
    process = subprocess.Popen(invocation, stdout=log, stderr=subprocess.STDOUT,
                               start_new_session=True)
    token = invocation[invocation.index("GMP_LAUNCH_TOKEN") + 1]
    return {"profile": profile, "install": str(install), "run_id": run_id,
            "folder": folder, "process": process, "token": token, "log": log}


def run_gate(proton, timeout=300, negative=None):
    run_id = uuid.uuid4().hex
    port = free_port()
    installs = {"host": ensure_install("a"), "guest": ensure_install("b")}
    clients = {}
    deadline = time.monotonic() + timeout
    try:
        clients["host"] = start_client("session-host", installs["host"], proton,
                                       "host", port, run_id, negative)
        time.sleep(2)
        clients["guest"] = start_client("session-guest", installs["guest"], proton,
                                        "guest", port, run_id, negative)
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
    run_folder = ROOT / "local/profiles" / ("session-" + run_id)
    run_folder.mkdir(parents=True, exist_ok=True)
    for name, client in clients.items():
        trace = Path(client["install"]) / ("gmp-trace-" + client["run_id"] + ".log")
        texts[name] = ""
        evidence = run_folder / name
        evidence.mkdir(parents=True, exist_ok=True)
        if trace.exists():
            shutil.copy2(trace, client["folder"] / "logs/probe-trace.log")
            shutil.copy2(trace, evidence / "probe-trace.log")
            texts[name] = trace.read_text(errors="replace")
        bep = Path(client["install"]) / "BepInEx/LogOutput.log"
        if bep.exists():
            shutil.copy2(bep, client["folder"] / "logs/bepinex.log")
            shutil.copy2(bep, evidence / "bepinex.log")

    passed = score_pair(texts["host"], texts["guest"], run_id,
                        results["host"], results["guest"], negative)
    summary = {
        "scope": "M1 persistent session core, snapshots, interpolation and scene actor survival; NOT online co-op",
        "passed": passed, "negative": negative, "run_id": run_id, "port": port,
        "host": {"observed": parse(texts["host"], run_id), "process": results["host"]},
        "guest": {"observed": parse(texts["guest"], run_id), "process": results["guest"]},
    }
    (run_folder / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--negative", choices=["snapshot", "command", "recon"],
                        default=None)
    parser.add_argument("--suite", action="store_true",
                        help="Positive gate, then the ignored-snapshot mutation")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton in local/runtime-config.json or pass --proton")
    if args.negative:
        results = [run_gate(args.proton, args.timeout, args.negative)]
    else:
        results = [run_gate(args.proton, args.timeout, None)]
        if args.suite:
            time.sleep(5)
            results.append(run_gate(args.proton, args.timeout, "snapshot"))
            time.sleep(5)
            results.append(run_gate(args.proton, args.timeout, "command"))
            time.sleep(5)
            results.append(run_gate(args.proton, args.timeout, "recon"))
    passed = all(result["passed"] for result in results)
    summary = {"scope": "M1 long-session suite; NOT online co-op",
               "passed": passed, "gates": results}
    (ROOT / "local/session-suite-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
