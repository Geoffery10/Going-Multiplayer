"""O2: host-authoritative guest movement input, snapshots and expected-failure twins."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import shutil
import time
import uuid

from launch_isolated import ROOT, cleanup_owned
from launch_proton import DEFAULT_PROTON
from net_test import free_port, start_client
from two_instance_test import ensure_install, os_kill_group, wait_client

INTS = ("floor", "applied_inputs", "moved_inputs", "rejected", "snapshots_sent", "sent",
        "snapshots_received", "snapshots_applied", "snapshots_ignored", "snapshots_rejected",
        "actors")
FLOATS = ("remote_delta", "forward_delta", "stale_backward", "post_stop_drift",
          "local_delta", "host_delta", "pose_error", "clearance")
BOOLS = ("connected", "ready", "go", "done", "ack", "stale_rejected", "stale_applied",
         "timeout_cleared", "input_drop", "stale_negative", "timeout_negative",
         "snapshot_negative")
STRINGS = ("run", "role", "session", "build", "scene", "geometry")
HOST_FLAGS = {"drop": "-gmp-net-input-drop-negative",
              "stale": "-gmp-net-input-stale-negative",
              "timeout": "-gmp-net-input-timeout-negative"}
GUEST_FLAGS = {"snapshot": "-gmp-net-input-snapshot-negative"}


def parse(text, run_id, build):
    lines = [line for line in text.splitlines() if line.startswith("GMP_INPUT_RESULT")]
    if len(lines) != 1:
        return None
    match = re.fullmatch(r"GMP_INPUT_RESULT (PASS|FAIL) (.+)", lines[0])
    if match is None:
        return None
    pairs = re.findall(r"(\w+)=(\S+)", match[2])
    result = dict(pairs)
    fields = set(INTS + FLOATS + BOOLS + STRINGS)
    if len(pairs) != len(fields) or set(result) != fields:
        return None
    if result["run"] != run_id or result["build"] != build \
            or not re.fullmatch("[0-9a-f]{64}", result["geometry"]):
        return None
    try:
        for field in INTS:
            result[field] = int(result[field])
            if result[field] < 0:
                return None
        for field in FLOATS:
            result[field] = float(result[field])
            if not math.isfinite(result[field]) or result[field] < 0:
                return None
        for field in BOOLS:
            if result[field] not in ("True", "False"):
                return None
            result[field] = result[field] == "True"
        result["status"] = match[1]
        role = result["role"]
        intros = [line for line in text.splitlines() if line.startswith("GMP_INPUT_INTRO")]
        if intros != [f"GMP_INPUT_INTRO role={role} dialogue=False background=True"]:
            return None
        return result
    except ValueError:
        return None


def healthy_host(host):
    return (host["status"] == "PASS" and host["applied_inputs"] >= 20
            and host["moved_inputs"] >= 20 and host["rejected"] >= 2
            and host["stale_rejected"] and not host["stale_applied"]
            and host["timeout_cleared"] and host["remote_delta"] > 0.8
            and host["forward_delta"] > 0.8 and host["stale_backward"] < 0.05
            and host["post_stop_drift"] < 0.5 and host["snapshots_sent"] >= 20
            and not host["input_drop"] and not host["stale_negative"]
            and not host["timeout_negative"] and not host["snapshot_negative"])


def guest_follows_host(guest):
    return (guest["status"] == "PASS" and guest["sent"] >= 20
            and guest["snapshots_received"] >= 20 and guest["snapshots_applied"] >= 20
            and guest["snapshots_ignored"] == 0 and guest["snapshots_rejected"] == 0
            and guest["pose_error"] <= 0.05 and not guest["snapshot_negative"])


def healthy_guest(guest):
    return (guest_follows_host(guest) and guest["local_delta"] > 0.8
            and guest["host_delta"] > 0.8
            and abs(guest["local_delta"] - guest["host_delta"]) <= 0.1)


def score(host_text, guest_text, run_id, build, host_process, guest_process, negative=None):
    host, guest = (parse(text, run_id, build) for text in (host_text, guest_text))
    if host is None or guest is None or host_process["timed_out"] or guest_process["timed_out"]:
        return False
    if host["role"] != "host" or guest["role"] != "guest":
        return False
    if host["session"] == "none" or host["session"] != guest["session"]:
        return False
    if any(host[key] != guest[key] for key in ("scene", "floor", "geometry")):
        return False
    if host["actors"] != 2 or guest["actors"] != 2:
        return False
    if not all(actor[key] for actor in (host, guest) for key in ("connected", "ready", "go", "done", "ack")):
        return False
    if not healthy_host(host) and negative not in HOST_FLAGS:
        return False
    if negative is None:
        return (host_process["exit_code"] == 0 and guest_process["exit_code"] == 0
                and healthy_host(host) and healthy_guest(guest))
    if negative in HOST_FLAGS:
        if host_process["exit_code"] != 1 or guest_process["exit_code"] != 0:
            return False
        if not guest_follows_host(guest):
            return False
        if negative == "drop":
            return (host["status"] == "FAIL" and host["input_drop"]
                    and host["applied_inputs"] == 0 and host["moved_inputs"] == 0
                    and guest["host_delta"] < 0.2 and guest["local_delta"] < 0.2)
        follows = (guest["local_delta"] > 0.8 and guest["host_delta"] > 0.8
                   and abs(guest["local_delta"] - guest["host_delta"]) <= 0.1)
        if negative == "stale":
            return (host["status"] == "FAIL" and host["stale_negative"]
                    and not host["stale_rejected"] and host["stale_applied"]
                    and host["stale_backward"] > 0.1 and follows)
        if negative == "timeout":
            return (host["status"] == "FAIL" and host["timeout_negative"]
                    and not host["timeout_cleared"] and follows)
        return False
    if negative == "snapshot":
        if host_process["exit_code"] != 0 or guest_process["exit_code"] != 1:
            return False
        if not healthy_host(host):
            return False
        return (guest["status"] == "FAIL" and guest["snapshot_negative"]
                and guest["snapshots_applied"] == 0 and guest["snapshots_ignored"] >= 20
                and guest["pose_error"] > 0.5 and guest["local_delta"] > 0.5)
    return False


def run_gate(proton, timeout, negative):
    run_id, port = uuid.uuid4().hex, free_port()
    build = hashlib.sha256((ROOT / "artifacts/GoingMultiplayer.dll").read_bytes()).hexdigest()
    installs = {role: ensure_install(name) for role, name in (("host", "a"), ("guest", "b"))}
    clients = {}
    results = {role: dict(timed_out=False, exit_code=None) for role in installs}
    error = None
    deadline = time.monotonic() + timeout
    folder = ROOT / "local/profiles" / ("net-input-" + run_id)
    try:
        host_args = ["-gmp-net-input-probe"]
        guest_args = ["-gmp-net-input-probe"]
        if negative in HOST_FLAGS:
            host_args.append(HOST_FLAGS[negative])
        if negative in GUEST_FLAGS:
            guest_args.append(GUEST_FLAGS[negative])
        clients["host"] = start_client("input-host", installs["host"], proton, "host",
                                       port, run_id, None, probe_args=host_args)
        time.sleep(2)
        clients["guest"] = start_client("input-guest", installs["guest"], proton, "guest",
                                        port, run_id, None, probe_args=guest_args)
        for role, client in clients.items():
            results[role] = wait_client(client, deadline)
    except Exception as failure:
        error = type(failure).__name__ + ": " + str(failure)
    finally:
        for client in clients.values():
            if client["process"].poll() is None:
                os_kill_group(client["process"].pid)
            cleanup_owned(client["token"])
            client["log"].close()
    texts = {role: "" for role in installs}
    for role, client in clients.items():
        evidence = folder / role
        evidence.mkdir(parents=True, exist_ok=True)
        trace = installs[role] / ("gmp-trace-" + run_id + ".log")
        texts[role] = trace.read_text(errors="replace") if trace.exists() else ""
        for source, name in ((trace, "probe-trace.log"),
                             (installs[role] / "BepInEx/LogOutput.log", "bepinex.log"),
                             (client["folder"] / "logs/net.log", "launcher.log")):
            if source.exists():
                shutil.copy2(source, evidence / name)
        if results[role]["timed_out"]:
            for name in ("unity.log", "timeout-processes.json"):
                source = client["folder"] / "logs" / name
                if source.exists():
                    shutil.copy2(source, evidence / name)
    summary = dict(scope=__doc__, run_id=run_id, build=build, negative=negative, error=error,
                   passed=error is None and score(texts["host"], texts["guest"], run_id, build,
                                                  results["host"], results["guest"], negative),
                   clients={role: dict(observed=parse(texts[role], run_id, build),
                                       process=results[role]) for role in installs})
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2), flush=True)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--negative", choices=("drop", "stale", "timeout", "snapshot"))
    parser.add_argument("--suite", action="store_true")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton or pass --proton")
    mutations = [args.negative] if args.negative else [None]
    if args.suite and not args.negative:
        mutations += ["drop", "stale", "timeout", "snapshot"]
    gates = []
    for negative in mutations:
        if gates:
            time.sleep(5)
        gates.append(run_gate(args.proton, args.timeout, negative))
    passed = all(gate["passed"] for gate in gates)
    (ROOT / "local/net-input-suite-summary.json").write_text(
        json.dumps(dict(passed=passed, gates=gates), indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
