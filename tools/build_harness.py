"""Build the standalone M0a Steam/UDP adapter harness (local output only)."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "local"
OUTPUT = LOCAL / "harness/publish"


def main():
    dotnet = LOCAL / "tools/dotnet/dotnet"
    project = ROOT / "harness/SteamHarness.csproj"
    steam_api = LOCAL / "test-install/Going Under_Data/Plugins/steam_api64.dll"
    for path in (dotnet, project, steam_api):
        if not path.is_file():
            raise SystemExit("Missing harness build input: " + str(path))
    OUTPUT.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_CLI_HOME=str(LOCAL / "dotnet-home"),
               NUGET_PACKAGES=str(LOCAL / "nuget-packages"), DOTNET_NOLOGO="1")
    subprocess.run([str(dotnet), "publish", str(project), "-c", "Release",
                    "-o", str(OUTPUT), "--nologo"], env=env, check=True)
    exe = OUTPUT / "SteamHarness.exe"
    if not exe.is_file():
        raise SystemExit("Harness publish did not produce " + str(exe))
    shutil.copy2(steam_api, OUTPUT / "steam_api64.dll")
    (OUTPUT / "steam_appid.txt").write_text("480\n")
    digest = hashlib.file_digest(exe.open("rb"), "sha256").hexdigest()
    print("Built Steam/UDP/M2 harness: " + str(exe))
    print("SHA-256: " + digest)
    print("Steam modes use AppID 480; UDP needs no account. The m2 mode links the production transport/lobby classes.")


if __name__ == "__main__":
    main()
