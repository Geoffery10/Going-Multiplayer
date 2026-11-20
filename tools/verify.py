"""Run tooling tests; optionally build and run all authenticated real-game gates."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--integration", action="store_true",
                        help="Uses approved Steam session, isolated profiles and visible game windows")
    args = parser.parse_args()
    steps = [("tooling", ["-m", "unittest", "discover", "-s", "tests", "-v"]),
             ("pad-mapping", ["tools/test_pad.py"])]
    if args.integration:
        steps += [
            ("build", ["tools/build_plugin.py", "--deploy-test"]),
            ("loader", ["tools/smoke_test.py", "--suite"]),
            ("coexistence", ["tools/two_instance_test.py", "--windowed"]),
            ("net-loopback", ["tools/net_test.py", "--suite"]),
            ("actors", ["tools/seat_test.py", "--windowed", "--suite"]),
            ("targeting", ["tools/target_test.py", "--suite"]),
            ("net-ai-targeting", ["tools/net_ai_test.py", "--suite"]),
            ("net-ai-movement", ["tools/net_ai_test.py", "--brain", "--suite"]),
            ("controllers", ["tools/controller_test.py", "--suite"]),
        ]
    results = []
    for name, arguments in steps:
        print("VERIFY " + name, flush=True)
        code = subprocess.run([sys.executable] + arguments, cwd=ROOT).returncode
        results.append({"stage": name, "exit_code": code})
        if code != 0:
            break
    passed = len(results) == len(steps) and all(r["exit_code"] == 0 for r in results)
    output = ROOT / "local/integration-summary.json"
    output.parent.mkdir(exist_ok=True)
    output.write_text(json.dumps({"passed": passed, "integration": args.integration, "stages": results},
                                 indent=2) + "\n")
    print("VERIFY: " + ("PASS" if passed else "FAIL"), flush=True)
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
