"""Run tooling and, when requested, authenticated real-game test stages.

Profiles:
  default      offline tooling/scorers only (no game launch, no build)
  --quick      offline + build + loader + one positive gate (day to day)
  --stages ... run only the named stages, in the given order
  --integration full 16-stage milestone regression

Use --focus to choose the quick positive gate. Full suites are for milestones,
not every edit.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]

COMMANDS = {
    "tooling": ["-m", "unittest", "discover", "-s", "tests", "-v"],
    "pad-mapping": ["tools/test_pad.py"],
    "damage-ledger": ["tools/test_damage_ledger.py"],
    "input-ledger": ["tools/test_input_ledger.py"],
    "build": ["tools/build_plugin.py", "--deploy-test"],
    "startup": ["tools/startup_test.py", "--suite"],
    "loader": ["tools/smoke_test.py", "--suite"],
    "coexistence": ["tools/two_instance_test.py", "--windowed"],
    "net-loopback": ["tools/net_test.py", "--suite"],
    "actors": ["tools/seat_test.py", "--windowed", "--suite"],
    "targeting": ["tools/target_test.py", "--suite"],
    "net-ai-targeting": ["tools/net_ai_test.py", "--suite"],
    "net-ai-movement": ["tools/net_ai_test.py", "--brain", "--suite"],
    "net-damage": ["tools/damage_test.py", "--suite"],
    "net-input": ["tools/input_test.py", "--suite"],
    "controllers": ["tools/controller_test.py", "--suite"],
}
OFFLINE = ["tooling", "pad-mapping", "damage-ledger", "input-ledger"]
FULL = OFFLINE + ["build", "startup", "loader", "coexistence", "net-loopback",
                  "actors", "targeting", "net-ai-targeting", "net-ai-movement",
                  "net-damage", "net-input", "controllers"]
POSITIVE = {
    "startup": ["tools/startup_test.py"],
    "loader": ["tools/smoke_test.py"],
    "coexistence": ["tools/two_instance_test.py", "--windowed"],
    "net-loopback": ["tools/net_test.py"],
    "actors": ["tools/seat_test.py", "--windowed"],
    "targeting": ["tools/target_test.py"],
    "net-ai-targeting": ["tools/net_ai_test.py"],
    "net-ai-movement": ["tools/net_ai_test.py", "--brain"],
    "net-damage": ["tools/damage_test.py"],
    "net-input": ["tools/input_test.py"],
    "controllers": ["tools/controller_test.py"],
}


def quick_stages(focus):
    stages = OFFLINE + ["build"]
    if focus != "loader":
        stages.append("loader")
    stages.append("net-input-positive" if focus == "net-input" else focus + "-positive")
    return stages


def command_for(stage, focus):
    if stage.endswith("-positive"):
        name = stage[:-len("-positive")]
        if name == "net-input":
            return POSITIVE["net-input"]
        return POSITIVE.get(name, COMMANDS.get(stage, []))
    return COMMANDS.get(stage)


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--integration", action="store_true",
                      help="Full milestone regression; uses Steam, isolated profiles and visible windows")
    mode.add_argument("--quick", action="store_true",
                      help="Offline checks, build, loader and one positive gate")
    mode.add_argument("--stages", metavar="NAME[,NAME]",
                      help="Run only these stages, in order (see --list)")
    parser.add_argument("--focus", choices=sorted(POSITIVE), default="net-input",
                        help="Positive gate used by --quick (default: net-input)")
    parser.add_argument("--list", action="store_true", help="List stage names and exit")
    args = parser.parse_args()

    if args.list:
        for name in FULL:
            print(name)
        print("net-input-positive (quick gate; also --focus choices: "
              + ", ".join(sorted(POSITIVE)) + ")")
        return

    if args.stages:
        stages = [name.strip() for name in args.stages.split(",") if name.strip()]
        unknown = [name for name in stages if command_for(name, args.focus) is None]
        if unknown:
            parser.error("Unknown stage(s): " + ", ".join(unknown))
        profile = "stages"
    elif args.integration:
        stages, profile = FULL, "full"
    elif args.quick:
        stages, profile = quick_stages(args.focus), "quick"
    else:
        stages, profile = OFFLINE, "offline"

    results = []
    for stage in stages:
        arguments = command_for(stage, args.focus)
        if not arguments:
            raise SystemExit("No command configured for stage " + stage)
        print("VERIFY " + stage, flush=True)
        code = subprocess.run([sys.executable] + arguments, cwd=ROOT).returncode
        results.append({"stage": stage, "exit_code": code})
        if code != 0:
            break
    passed = len(results) == len(stages) and all(r["exit_code"] == 0 for r in results)
    names = {"full": "integration-summary.json", "quick": "quick-summary.json",
             "stages": "stages-summary.json", "offline": "offline-summary.json"}
    output = ROOT / "local" / names[profile]
    output.parent.mkdir(exist_ok=True)
    output.write_text(json.dumps({"passed": passed, "profile": profile,
                                  "focus": args.focus, "integration": profile == "full",
                                  "stages": results}, indent=2) + "\n")
    print("VERIFY: " + ("PASS" if passed else "FAIL"), flush=True)
    print("PROFILE: " + profile + " (" + str(len(results)) + "/" + str(len(stages)) + " stages)",
          flush=True)
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
