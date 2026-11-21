"""Open the isolated build that has the Steam co-op plugin.

This is not the Steam library install. The original install is intentionally
untouched. Use ESC -> Co-op, or F7 for the quick panel.
"""
import argparse

from launch_isolated import run_invocation
from launch_proton import command, DEFAULT_PROTON


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--minutes", type=int, default=60,
                        help="Safety time limit, default 60 minutes")
    args = parser.parse_args()
    if DEFAULT_PROTON is None or not 1 <= args.minutes <= 180:
        parser.error("Configure local/runtime-config.json and use 1–180 minutes")
    folder, invocation = command("coop-preview", DEFAULT_PROTON, [], windowed=True)
    print("Starting the isolated modded copy.")
    print("Open ESC and select the Co-op tab, or press F7 for the quick panel.")
    print("Do not launch the Steam library copy: it does not contain this plugin.")
    result = run_invocation(folder, invocation, args.minutes * 60, "coop-preview.log")
    raise SystemExit(1 if result["timed_out"] or result["exit_code"] else 0)


if __name__ == "__main__":
    main()
