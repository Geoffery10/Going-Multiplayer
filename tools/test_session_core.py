"""Compile/run the Unity-free session core without the game or Steam."""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def main():
    sdk = ROOT / "local/tools/dotnet"
    refs = list((sdk / "packs/Microsoft.NETCore.App.Ref").glob("*/ref/net8.0"))
    if len(refs) != 1:
        raise SystemExit("Expected the pinned SDK reference pack")
    output = ROOT / "local/session-core-tests"
    output.mkdir(exist_ok=True)
    assembly = output / "SessionCoreTests.dll"
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(sdk), DOTNET_CLI_HOME=str(ROOT / "local/dotnet-home"),
               DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    command = [str(sdk / "dotnet"), str(sdk / "sdk/8.0.428/Roslyn/bincore/csc.dll"),
               "-nologo", "-nostdlib+", "-target:exe", "-out:" + str(assembly)]
    command += ["-reference:" + str(path) for path in refs[0].glob("*.dll")]
    command += [str(ROOT / "src/GoingMultiplayer/SessionProtocol.cs"),
                str(ROOT / "src/GoingMultiplayer/PadFrame.cs"),
                str(ROOT / "src/GoingMultiplayer/NetworkInputState.cs"),
                str(ROOT / "tests/SessionCoreTests.cs")]
    subprocess.run(command, env=env, check=True)
    assembly.with_suffix(".runtimeconfig.json").write_text(json.dumps({
        "runtimeOptions": {"tfm": "net8.0", "framework": {"name": "Microsoft.NETCore.App", "version": "8.0.0"}}
    }))
    subprocess.run([str(sdk / "dotnet"), str(assembly)], env=env, check=True)


if __name__ == "__main__":
    main()
