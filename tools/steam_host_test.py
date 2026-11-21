"""M2 host-only in-game gate: real AppID Steam init + lobby creation."""
import json
import re
import sys
import time
import uuid
from pathlib import Path

from launch_isolated import ROOT, run_invocation
from launch_proton import command, DEFAULT_PROTON

LOBBY = re.compile(r"GMP_STEAM_LOBBY (\d+)")
RESULT = re.compile(
    r"GMP_STEAM_RESULT (PASS|FAIL) run=(\S+) role=host lobby=(\d+) "
    r"host_only=(True|False) mode=steam")


def main():
    parser = __import__("argparse").ArgumentParser(description=__doc__)
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--timeout", type=int, default=150)
    args = parser.parse_args()
    if args.proton is None:
        parser.error("Configure Proton in local/runtime-config.json or pass --proton")
    import subprocess
    subprocess.run([sys.executable, str(ROOT / "tools/build_plugin.py"), "--deploy-test"],
                   check=True)
    run_id = uuid.uuid4().hex
    folder, invocation = command("steam-host-only", args.proton,
                                 ["-gmp-steam-probe", "-gmp-steam-role=host",
                                  "-gmp-steam-host-only", "-gmp-run-id=" + run_id],
                                 windowed=True)
    result = run_invocation(folder, invocation, args.timeout, "steam-host.log")
    trace = ROOT / "local/test-install" / ("gmp-trace-" + run_id + ".log")
    text = trace.read_text(errors="replace") if trace.exists() else ""
    lobby = LOBBY.search(text)
    match = RESULT.search(text)
    passed = (result["exit_code"] == 0 and not result["timed_out"]
              and match is not None and match.group(1) == "PASS"
              and match.group(4) == "True")
    summary = {
        "scope": "M2 host-only in-game Steam lobby creation under AppID 1154810",
        "passed": passed, "run_id": run_id,
        "lobby": int(lobby.group(1)) if lobby else 0,
        "result": match.groups() if match else None,
        "process": result,
    }
    (ROOT / "local/steam-host-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
