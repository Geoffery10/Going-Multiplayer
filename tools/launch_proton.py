"""Authenticated isolated test launch; requires explicit Steam-use approval."""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import uuid

from launch_isolated import ROOT, RUNTIME, run_invocation

CONFIG = ROOT / "local/runtime-config.json"
DEFAULT_PROTON = (
    Path(json.loads(CONFIG.read_text())["proton"])
    if CONFIG.exists() else None
)


def stage_runner(proton):
    """Keep Proton's runner locks/caches local and its installed runtime read-only."""
    stage = RUNTIME / "proton9-runner"
    stage.mkdir(parents=True, exist_ok=True)
    for name in ("proton", "filelock.py", "version"):
        source = proton / name
        target = stage / name
        if target.exists():
            if source.read_bytes() != target.read_bytes():
                raise RuntimeError("Staged Proton runner differs; use a fresh named stage")
        else:
            shutil.copy2(source, target)
    files = stage / "files"
    if not files.exists():
        files.symlink_to(proton / "files", target_is_directory=True)
    # No fixup manifest is copied: the installed runtime is already restored.
    # Any attempt to change the installed runtime is denied by the read-only bind.
    return stage


def command(profile, proton, game_args, windowed=False):
    if not profile or any(c not in "abcdefghijklmnopqrstuvwxyz0123456789-_" for c in profile):
        raise ValueError("Invalid profile name")
    proton = proton.resolve()
    steam = (Path.home() / ".local/share/Steam").resolve()
    steam_dot = Path.home() / ".steam"
    authority = Path(os.environ.get("XAUTHORITY", "/nonexistent"))
    if not steam.is_dir() or not authority.is_file() or not os.environ.get("DISPLAY", "").startswith(":"):
        raise RuntimeError("Running local Steam and X display are required")
    stage = stage_runner(proton)
    runtime = RUNTIME / "profiles" / profile
    for name in ("home", "compatdata"):
        (runtime / name).mkdir(parents=True, exist_ok=True)
    folder = ROOT / "local/profiles" / profile
    (folder / "logs").mkdir(parents=True, exist_ok=True)
    args = [
        shutil.which("bwrap"), "--ro-bind", "/", "/", "--bind", str(ROOT), str(ROOT),
        "--tmpfs", "/home", "--tmpfs", "/tmp", "--tmpfs", "/run",
        "--bind", str(RUNTIME), str(RUNTIME),
        "--ro-bind", str(steam), str(steam),
        "--ro-bind", str(steam_dot), str(steam_dot),
        "--ro-bind", str(steam_dot), str(runtime / "home/.steam"),
        "--ro-bind", "/tmp/.X11-unix", "/tmp/.X11-unix",
        "--ro-bind", str(authority), "/tmp/gmp-xauthority",
        "--dev", "/dev", "--bind", "/dev/shm", "/dev/shm",
        "--proc", "/proc", "--die-with-parent", "--new-session",
        "--setenv", "HOME", str(runtime / "home"),
        "--setenv", "GMP_LAUNCH_TOKEN", uuid.uuid4().hex,
        "--setenv", "XAUTHORITY", "/tmp/gmp-xauthority",
        "--setenv", "DISPLAY", os.environ["DISPLAY"],
        "--setenv", "XDG_CONFIG_HOME", str(runtime / "home/.config"),
        "--setenv", "XDG_DATA_HOME", str(runtime / "home/.local/share"),
        "--setenv", "XDG_CACHE_HOME", str(runtime / "home/.cache"),
        "--unsetenv", "DBUS_SESSION_BUS_ADDRESS",
        "--setenv", "STEAM_COMPAT_CLIENT_INSTALL_PATH", str(steam),
        "--setenv", "STEAM_COMPAT_DATA_PATH", str(runtime / "compatdata"),
        "--setenv", "STEAM_COMPAT_INSTALL_PATH", str(ROOT / "local/test-install"),
        "--setenv", "STEAM_COMPAT_APP_ID", "1154810",
        "--setenv", "SteamAppId", "1154810",
        "--setenv", "SteamGameId", "1154810",
        "--setenv", "PROTON_LOG", "1",
        "--setenv", "PROTON_LOG_DIR", str(folder / "logs"),
        "--setenv", "PROTON_USE_WINED3D", "0" if windowed else "1",
        "--setenv", "PROTON_NO_ESYNC", "1",
        "--setenv", "PROTON_NO_FSYNC", "1",
        "--setenv", "WINEDLLOVERRIDES", "winhttp=n,b;mscoree,mshtml=",
        "--chdir", str(ROOT / "local/test-install"),
        sys.executable, str(ROOT / "tools/proton_entry.py"), str(stage / "proton"),
        str(ROOT / "local/test-install/Going Under.exe"),
        *(["-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720"]
          if windowed else ["-batchmode", "-nographics"]),
        "-logFile", str(folder / "logs/unity.log"),
    ] + game_args
    if windowed:
        devices = [Path("/dev/dri"), Path("/dev/input")] + list(Path("/dev").glob("nvidia*")) \
            + list(Path("/dev").glob("hidraw*"))
        additions = []
        for device in devices:
            if device.exists():
                additions += ["--dev-bind", str(device), str(device)]
        if Path("/run/udev").is_dir():
            additions += ["--ro-bind", "/run/udev", "/run/udev"]
        args[args.index(sys.executable):args.index(sys.executable)] = additions
    return folder, args


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", default="proton-loader")
    parser.add_argument("--proton", type=Path, default=DEFAULT_PROTON)
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--run", action="store_true")
    parser.add_argument("--windowed", action="store_true", help="Visible isolated 1280x720 test window")
    args, game_args = parser.parse_known_args()
    if args.proton is None:
        parser.error("Set --proton to an installed Proton directory, or configure local/runtime-config.json")
    if not 1 <= args.timeout <= 300:
        parser.error("Timeout must be 1–300 seconds")
    folder, invocation = command(args.profile, args.proton, game_args, args.windowed)
    if not args.run:
        print(json.dumps(invocation, indent=2))
        return
    result = run_invocation(folder, invocation, args.timeout, "proton-launch.log")
    raise SystemExit(1 if result["timed_out"] or result["exit_code"] else 0)


if __name__ == "__main__":
    main()
