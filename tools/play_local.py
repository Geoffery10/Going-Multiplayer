"""Open the isolated controller-enabled local prototype; close its window to finish."""
import argparse

from launch_isolated import run_invocation
from launch_proton import command, DEFAULT_PROTON


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--minutes", type=int, default=60, help="Safety time limit, default 60 minutes")
    args = parser.parse_args()
    if DEFAULT_PROTON is None or not 1 <= args.minutes <= 180:
        parser.error("Configure local/runtime-config.json and use 1–180 minutes")
    folder, invocation = command("prototype-preview", DEFAULT_PROTON,
                                  ["-gmp-local-prototype"], windowed=True)
    print("Starting isolated local prototype. Load the office, then F9 spawns P2.")
    print("P2 uses controller 2 (or the only controller if P1 uses keyboard). F10 cycles assignments.")
    result = run_invocation(folder, invocation, args.minutes * 60, "proton-launch.log")
    raise SystemExit(1 if result["timed_out"] or result["exit_code"] else 0)


if __name__ == "__main__":
    main()
