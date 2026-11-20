"""O4a: two-client native enemy acquisition and a guarded target mirror.

Not full O4: damage, general enemy brains and shared dungeon generation remain.
"""
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

RESULT = re.compile(
    r"^GMP_AI_RESULT (PASS|FAIL) run=(\S+) role=(host|guest) session=(\S+) "
    r"build=([0-9a-f]{64}) scene=(\S+) floor=(\d+) phases=(\d+) "
    r"acquired=(True|False) swapped=(True|False) targets=(True|False) "
    r"frozen=(True|False) blocked=(\d+) drift=([-\d.]+) "
    r"ai_negative=(True|False) target_negative=(True|False)$", re.MULTILINE)


def parse(text, run_id, build):
    if sum(line.startswith("GMP_AI_RESULT") for line in text.splitlines()) != 1:
        return None
    matches = RESULT.findall(text)
    if len(matches) != 1:
        return None
    (status, run, role, session, observed_build, scene, floor, phases, acquired,
     swapped, targets, frozen, blocked, drift, ai_negative, target_negative) = matches[0]
    if run != run_id or build != observed_build:
        return None
    try:
        return dict(status=status, role=role, session=session, build=observed_build,
                    scene=scene, floor=int(floor), phases=int(phases),
                    acquired=acquired == "True", swapped=swapped == "True",
                    targets=targets == "True", frozen=frozen == "True",
                    blocked=int(blocked), drift=float(drift),
                    ai_negative=ai_negative == "True", target_negative=target_negative == "True")
    except ValueError:
        return None


def score_pair(host_text, guest_text, run_id, build, host_result, guest_result, negative=None):
    host, guest = (parse(text, run_id, build) for text in (host_text, guest_text))
    if host is None or guest is None:
        return False
    if host["role"] != "host" or guest["role"] != "guest":
        return False
    if host_result["timed_out"] or guest_result["timed_out"]:
        return False
    if host["status"] != "PASS" or host_result["exit_code"] != 0:
        return False
    if host["session"] == "none" or host["session"] != guest["session"]:
        return False
    if (host["scene"], host["floor"]) != (guest["scene"], guest["floor"]):
        return False
    for actor in (host, guest):
        if actor["phases"] != 7 or not actor["acquired"] or not actor["swapped"]:
            return False
        if not actor["frozen"] or not math.isfinite(actor["drift"]) or actor["drift"] > 0.05:
            return False
    if host["ai_negative"] or host["target_negative"]:
        return False
    if negative:
        if guest["status"] != "FAIL" or guest_result["exit_code"] != 1 or guest["targets"]:
            return False
        if negative == "ai":
            return guest["ai_negative"] and not guest["target_negative"] and guest["blocked"] == 0
        if negative == "target":
            return guest["target_negative"] and not guest["ai_negative"] and guest["blocked"] >= 60
        return False
    return (guest["status"] == "PASS" and guest_result["exit_code"] == 0
            and guest["targets"] and guest["blocked"] >= 60
            and not guest["ai_negative"] and not guest["target_negative"])


def run_gate(proton, timeout=300, negative=None):
    run_id, port = uuid.uuid4().hex, free_port()
    build = hashlib.sha256((ROOT / "artifacts/GoingMultiplayer.dll").read_bytes()).hexdigest()
    installs = {role: ensure_install(name) for role, name in (("host", "a"), ("guest", "b"))}
    clients = {}
    results = {role: dict(timed_out=False, exit_code=None) for role in installs}
    error = None
    deadline = time.monotonic() + timeout
    folder = ROOT / "local/profiles" / ("net-ai-" + run_id)
    try:
        for role in ("host", "guest"):
            args = ["-gmp-net-ai-probe"]
            if role == "guest" and negative:
                args.append("-gmp-net-" + ("ai" if negative == "ai" else "target") + "-negative")
            clients[role] = start_client("net-" + role, installs[role], proton, role,
                                         port, run_id, None, probe_args=args)
            if role == "host":
                time.sleep(2)
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
    summary = dict(scope=__doc__, run_id=run_id, build=build, negative=negative, error=error,
                   passed=error is None and score_pair(
                       texts["host"], texts["guest"], run_id, build,
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
    parser.add_argument("--negative", choices=("ai", "target"))
    parser.add_argument("--suite", action="store_true")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton or pass --proton")
    gates = [run_gate(args.proton, args.timeout, args.negative)]
    if args.suite and not args.negative:
        for mutation in ("ai", "target"):
            time.sleep(5)
            gates.append(run_gate(args.proton, args.timeout, mutation))
    passed = all(gate["passed"] for gate in gates)
    (ROOT / "local/net-ai-suite-summary.json").write_text(
        json.dumps(dict(passed=passed, gates=gates), indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
