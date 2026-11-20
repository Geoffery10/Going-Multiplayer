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


def parse_brain(text, run_id, build):
    target = parse(text, run_id, build)
    lines = [line for line in text.splitlines() if line.startswith("GMP_BRAIN_RESULT")]
    if target is None or len(lines) != 1:
        return None
    pairs = re.findall(r"(\w+)=(\S+)", lines[0])
    data = dict(pairs)
    expected = {"run", "role", "geometry", "enemy_id", "authority", "epoch", "moved", "approach",
                "samples", "pose_error", "freeze_drift", "host_brain", "mirror_quiet",
                "activation_blocks", "starts", "pose_negative", "sim_negative"}
    if len(pairs) != len(expected) or set(data) != expected:
        return None
    if data["run"] != run_id or data["role"] != target["role"]:
        return None
    if not re.fullmatch("[0-9a-f]{64}", data["geometry"]):
        return None
    try:
        for key in ("enemy_id", "authority", "epoch", "samples", "activation_blocks", "starts"):
            data[key] = int(data[key])
            if data[key] < 0:
                return None
        for key in ("moved", "approach", "pose_error", "freeze_drift"):
            data[key] = float(data[key])
            if not math.isfinite(data[key]) or data[key] < 0:
                return None
        for key in ("host_brain", "mirror_quiet", "pose_negative", "sim_negative"):
            if data[key] not in ("True", "False"):
                return None
            data[key] = data[key] == "True"
        samples = {}
        for line in text.splitlines():
            if not line.startswith("GMP_BRAIN_SAMPLE"):
                continue
            match = re.fullmatch(r"GMP_BRAIN_SAMPLE role=(host|guest) seq=(\d+) target=(\d+) "
                                 r"state=(\w+) pose=([-\d.]+),([-\d.]+),([-\d.]+)", line)
            if match is None:
                return None
            role, seq, player, state, x, y, z = match.groups()
            seq = int(seq)
            pose = tuple(float(v) for v in (x, y, z))
            if role != target["role"] or seq in samples or not all(map(math.isfinite, pose)):
                return None
            samples[seq] = dict(target=int(player), state=state, pose=pose)
        return dict(target=target, brain=data, samples=samples)
    except ValueError:
        return None


def score_brain(host_text, guest_text, run_id, build, host_result, guest_result, negative=None):
    host, guest = (parse_brain(text, run_id, build) for text in (host_text, guest_text))
    if host is None or guest is None or host_result["timed_out"] or guest_result["timed_out"]:
        return False
    ht, gt, hb, gb = host["target"], guest["target"], host["brain"], guest["brain"]
    if ht["role"] != "host" or gt["role"] != "guest" or ht["status"] != "PASS" \
            or host_result["exit_code"] != 0:
        return False
    if ht["session"] == "none" or ht["session"] != gt["session"] \
            or (ht["scene"], ht["floor"]) != (gt["scene"], gt["floor"]) \
            or hb["geometry"] != gb["geometry"]:
        return False
    for target, brain in ((ht, hb), (gt, gb)):
        if target["phases"] != 31 or not all(target[key] for key in
                                           ("acquired", "swapped", "targets", "frozen")):
            return False
        if target["ai_negative"] or target["target_negative"] or target["drift"] > 0.05:
            return False
        if (brain["enemy_id"], brain["authority"], brain["epoch"]) != (1, 1, 1) \
                or brain["samples"] < 20:
            return False
    if not hb["host_brain"] or hb["moved"] <= 0.5 or hb["approach"] <= 0.5 or hb["freeze_drift"] >= 0.05 \
            or hb["starts"] < 1 or hb["pose_negative"] or hb["sim_negative"]:
        return False
    common = host["samples"].keys() & guest["samples"].keys()
    if len(common) < 20:
        return False
    errors = []
    for seq in common:
        a, b = host["samples"][seq], guest["samples"][seq]
        if a["target"] != 2 or b["target"] != 2 or a["state"] != b["state"]:
            return False
        errors.append(math.dist(a["pose"], b["pose"]))
    error = max(errors)
    if negative == "pose":
        return (gt["status"] == "FAIL" and guest_result["exit_code"] == 1
                and gb["pose_negative"] and not gb["sim_negative"]
                and error > 0.5 and gb["pose_error"] > 0.5 and gb["mirror_quiet"]
                and gb["starts"] == 0 and gb["activation_blocks"] >= 4
                and gb["freeze_drift"] < 0.05)
    if gb["pose_negative"] or error > 0.05 or gb["pose_error"] > 0.05 or gb["moved"] <= 0.5:
        return False
    if negative == "sim":
        return (gt["status"] == "FAIL" and guest_result["exit_code"] == 1
                and gb["sim_negative"] and not gb["mirror_quiet"] and gb["starts"] > 0
                and gb["activation_blocks"] >= 4 and gb["freeze_drift"] > 0.05)
    return (negative is None and gt["status"] == "PASS" and guest_result["exit_code"] == 0
            and not gb["sim_negative"] and gb["mirror_quiet"] and gb["starts"] == 0
            and gb["activation_blocks"] >= 4 and gb["freeze_drift"] < 0.05)


def run_gate(proton, timeout=300, negative=None, brain=False):
    run_id, port = uuid.uuid4().hex, free_port()
    build = hashlib.sha256((ROOT / "artifacts/GoingMultiplayer.dll").read_bytes()).hexdigest()
    installs = {role: ensure_install(name) for role, name in (("host", "a"), ("guest", "b"))}
    clients = {}
    results = {role: dict(timed_out=False, exit_code=None) for role in installs}
    error = None
    deadline = time.monotonic() + timeout
    folder = ROOT / "local/profiles" / (("net-brain-" if brain else "net-ai-") + run_id)
    try:
        for role in ("host", "guest"):
            args = ["-gmp-net-brain-probe" if brain else "-gmp-net-ai-probe"]
            if role == "guest" and negative:
                args.append("-gmp-net-" + negative + "-negative")
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
        if results[role]["timed_out"]:
            for name in ("unity.log", "timeout-processes.json"):
                source = client["folder"] / "logs" / name
                if source.exists():
                    shutil.copy2(source, evidence / name)
    parser, scorer = (parse_brain, score_brain) if brain else (parse, score_pair)
    summary = dict(scope="O4b scoped native host brain/mirror" if brain else __doc__,
                   run_id=run_id, build=build, negative=negative, error=error,
                   passed=error is None and scorer(
                       texts["host"], texts["guest"], run_id, build,
                       results["host"], results["guest"], negative),
                   clients={role: dict(observed=parser(texts[role], run_id, build),
                                       process=results[role]) for role in installs})
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    display = json.loads(json.dumps(summary))
    if brain:
        for client in display["clients"].values():
            observed = client["observed"]
            if observed is not None:
                observed["samples_recorded"] = len(observed.pop("samples"))
    print(json.dumps(display, indent=2), flush=True)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--negative", choices=("ai", "target", "pose", "sim"))
    parser.add_argument("--brain", action="store_true", help="O4b native host movement and mirror entry guards")
    parser.add_argument("--suite", action="store_true")
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton or pass --proton")
    if args.negative and (args.negative in ("pose", "sim")) != args.brain:
        parser.error("Use --brain with pose/sim mutations; omit it for ai/target")
    gates = [run_gate(args.proton, args.timeout, args.negative, args.brain)]
    if args.suite and not args.negative:
        for mutation in (("pose", "sim") if args.brain else ("ai", "target")):
            time.sleep(5)
            gates.append(run_gate(args.proton, args.timeout, mutation, args.brain))
    passed = all(gate["passed"] for gate in gates)
    (ROOT / ("local/net-brain-suite-summary.json" if args.brain else "local/net-ai-suite-summary.json")).write_text(
        json.dumps(dict(passed=passed, gates=gates), indent=2) + "\n")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
