"""Compile against the isolated game's own framework; no NuGet restore or game-file distribution."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "local"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--deploy-test", action="store_true", help="Copy only our DLL into the isolated test install")
    args = parser.parse_args()
    dotnet = LOCAL / "tools/dotnet/dotnet"
    compiler = LOCAL / "tools/dotnet/sdk/8.0.428/Roslyn/bincore/csc.dll"
    managed = LOCAL / "test-install/Going Under_Data/Managed"
    bepinex = LOCAL / "test-install/BepInEx/core"
    references = [
        managed / name for name in (
            "mscorlib.dll", "netstandard.dll", "System.dll", "System.Core.dll", "Assembly-CSharp.dll",
            "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.IMGUIModule.dll",
            "UnityEngine.InputModule.dll",
            "UnityEngine.PhysicsModule.dll", "UnityEngine.AnimationModule.dll",
            "Rewired_Core.dll",
            "UnityEngine.ScreenCaptureModule.dll",
            "Assembly-CSharp-firstpass.dll",
            "Facepunch.Steamworks.Win64.dll",
        )
    ] + [bepinex / "BepInEx.dll", bepinex / "0Harmony.dll"]
    for path in [dotnet, compiler] + references:
        if not path.is_file():
            raise SystemExit(f"Missing build input: {path}")
    output = ROOT / "artifacts"
    output.mkdir(exist_ok=True)
    dll = output / "GoingMultiplayer.dll"
    sources = sorted((ROOT / "src/GoingMultiplayer").glob("*.cs"))
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_CLI_HOME=str(LOCAL / "dotnet-home"), DOTNET_NOLOGO="1")
    invocation = [str(dotnet), str(compiler), "-nologo", "-target:library",
                  "-nostdlib+", "-langversion:7.3", "-deterministic+",
                  "-optimize+", "-out:" + str(dll)]
    invocation += ["-reference:" + str(path) for path in references]
    invocation += [str(path) for path in sources]
    subprocess.run(invocation, env=env, check=True)
    record = {
        "plugin_sha256": hashlib.file_digest(dll.open("rb"), "sha256").hexdigest(),
        "inputs": {
            str(path.relative_to(ROOT)): hashlib.file_digest(path.open("rb"), "sha256").hexdigest()
            for path in references + sources
        },
        "status": "Experimental local-actor prototype; online multiplayer not implemented.",
    }
    (LOCAL / "plugin-build.json").write_text(json.dumps(record, indent=2) + "\n")
    if args.deploy_test:
        target = LOCAL / "test-install/BepInEx/plugins/GoingMultiplayer/GoingMultiplayer.dll"
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            backup = LOCAL / "plugin-backups" / (
                hashlib.file_digest(target.open("rb"), "sha256").hexdigest() + ".dll")
            backup.parent.mkdir(exist_ok=True)
            if not backup.exists():
                shutil.copy2(target, backup)
        shutil.copy2(dll, target)
    print("Built experimental plugin: artifacts/GoingMultiplayer.dll")
    print("SHA-256: " + record["plugin_sha256"])


if __name__ == "__main__":
    main()
