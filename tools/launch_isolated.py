"""Launch only the test copy, offline, in a fresh project-local Wine profile."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = Path("/tmp/opencode/going-under-coop")


def command(profile, timeout, wine_debug="-all", display=False):
    if not profile or any(c not in "abcdefghijklmnopqrstuvwxyz0123456789-_" for c in profile):
        raise ValueError("Profile must use lowercase letters, digits, hyphens or underscores")
    for tool in ("bwrap", "wine"):
        if not shutil.which(tool):
            raise RuntimeError(f"Required tool missing: {tool}")
    if not (ROOT / "local/test-install/Going Under.exe").is_file():
        raise RuntimeError("Isolated game copy missing")
    folder = ROOT / "local/profiles" / profile
    for name in ("logs",):
        (folder / name).mkdir(parents=True, exist_ok=True)
    runtime_profile = RUNTIME / "profiles" / profile
    (runtime_profile / "home").mkdir(parents=True, exist_ok=True)
    sandbox_root = str(ROOT)
    sandbox_folder = str(runtime_profile)
    args = [
        shutil.which("bwrap"), "--ro-bind", "/", "/", "--bind", str(ROOT), str(ROOT),
        "--tmpfs", "/home", "--tmpfs", "/tmp", "--tmpfs", "/run",
        "--bind", str(runtime_profile), str(runtime_profile),
        "--unshare-net", "--unshare-pid", "--die-with-parent", "--new-session",
        "--proc", "/proc", "--dev", "/dev",
        "--setenv", "HOME", sandbox_folder + "/home",
        "--setenv", "WINEPREFIX", sandbox_folder + "/wine",
        "--setenv", "WINEDLLOVERRIDES", "winhttp=n,b;mscoree,mshtml=",
        "--setenv", "WINEDEBUG", wine_debug,
        "--setenv", "GMP_LAUNCH_TOKEN", uuid.uuid4().hex,
        "--setenv", "XDG_CONFIG_HOME", sandbox_folder + "/home/.config",
        "--setenv", "XDG_DATA_HOME", sandbox_folder + "/home/.local/share",
        "--setenv", "XDG_CACHE_HOME", sandbox_folder + "/home/.cache",
        "--unsetenv", "DISPLAY", "--unsetenv", "WAYLAND_DISPLAY",
        "--unsetenv", "DBUS_SESSION_BUS_ADDRESS", "--unsetenv", "SteamAppId",
        "--unsetenv", "SteamGameId", "--chdir", sandbox_root + "/local/test-install",
        shutil.which("wine"), sandbox_root + "/local/test-install/Going Under.exe",
        "-batchmode", "-nographics",
        "-logFile", str(folder / "logs/unity.log"),
    ]
    if display:
        display_name = os.environ.get("DISPLAY", "")
        authority = os.environ.get("XAUTHORITY", "")
        if not display_name.startswith(":") or not Path(authority).is_file():
            raise RuntimeError("A local X display and XAUTHORITY file are required")
        additions = [
            "--ro-bind", "/tmp/.X11-unix", "/tmp/.X11-unix",
            "--ro-bind", authority, "/tmp/gmp-xauthority",
            "--setenv", "DISPLAY", display_name,
            "--setenv", "XAUTHORITY", "/tmp/gmp-xauthority",
        ]
        args[-6:-6] = additions
    return folder, args


def snapshot_owned(token, proc_root=Path("/proc")):
    """Diagnose only this launch's processes, without recording environments or command lines."""
    marker = ("GMP_LAUNCH_TOKEN=" + token).encode()
    processes = []
    for entry in proc_root.iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if marker not in (entry / "environ").read_bytes().split(b"\0"):
                continue
            status = dict(line.split(":", 1) for line in (entry / "status").read_text().splitlines()
                          if ":" in line)
            processes.append(dict(pid=int(entry.name), name=status.get("Name", "").strip(),
                                  parent=status.get("PPid", "").strip(),
                                  state=status.get("State", "").strip(),
                                  wait_channel=(entry / "wchan").read_text().strip()))
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            pass
    return sorted(processes, key=lambda process: process["pid"])


def cleanup_owned(token, proc_root=Path("/proc")):
    marker = ("GMP_LAUNCH_TOKEN=" + token).encode()
    stopped = 0
    for entry in proc_root.iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if marker in (entry / "environ").read_bytes().split(b"\0"):
                os.kill(int(entry.name), signal.SIGKILL)
                stopped += 1
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            pass
    return stopped


def run_invocation(folder, invocation, timeout, log_name="wine.log"):
    started = time.monotonic()
    timed_out = False
    with (folder / "logs" / log_name).open("w") as log:
        process = subprocess.Popen(invocation, stdout=log, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            os.killpg(process.pid, signal.SIGTERM)
            try:
                code = process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                code = process.wait()
    # Wine may create detached process groups. Kill only children that inherited
    # this invocation's unique token, never by image name or shared Steam PID.
    cleaned = cleanup_owned(invocation[invocation.index("GMP_LAUNCH_TOKEN") + 1]) \
        if "GMP_LAUNCH_TOKEN" in invocation else 0
    result = {
        "exit_code": code, "timed_out": timed_out,
        "elapsed_seconds": round(time.monotonic() - started, 2),
        "owned_processes_cleaned": cleaned,
        "note": "Launch observation only; not a multiplayer or loader PASS.",
    }
    (folder / "logs/result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", default="loader-smoke")
    parser.add_argument("--timeout", type=int, default=90)
    parser.add_argument("--run", action="store_true", help="Actually launch; default is dry run")
    parser.add_argument("--init-prefix", action="store_true", help="Initialize only the isolated Wine profile")
    parser.add_argument("--wine-debug", default="-all")
    parser.add_argument("--display", action="store_true", help="Allow the current local X display for Unity batch-mode initialization")
    args = parser.parse_args()
    if not 1 <= args.timeout <= 300:
        parser.error("Timeout must be 1–300 seconds")
    folder, invocation = command(args.profile, args.timeout, args.wine_debug, args.display)
    if args.init_prefix:
        invocation = invocation[:-6] + [shutil.which("wineboot"), "--init"]
    if not args.run:
        print(json.dumps(invocation, indent=2))
        return
    result = run_invocation(folder, invocation, args.timeout,
                            "wine-init.log" if args.init_prefix else "wine.log")
    raise SystemExit(1 if result["timed_out"] or result["exit_code"] else 0)


if __name__ == "__main__":
    main()
