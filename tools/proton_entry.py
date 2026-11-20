"""Keep the sandbox alive until Proton's detached game/helpers have exited."""
import os
from pathlib import Path
import subprocess
import sys


def main():
    runner = Path(sys.argv[1])
    result = subprocess.call([sys.executable, str(runner), "run"] + sys.argv[2:])
    env = os.environ.copy()
    env["WINEPREFIX"] = str(Path(env["STEAM_COMPAT_DATA_PATH"]) / "pfx")
    # Proton's Steam launcher can finish before its game child. A server wait
    # prevents the outer sandbox cleanup from killing an otherwise healthy game.
    subprocess.call([str(runner.parent / "files/bin/wineserver"), "-w"], env=env)
    raise SystemExit(result)


if __name__ == "__main__":
    main()
