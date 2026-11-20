"""Decompile the isolated managed game assembly; never run game code."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "local"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--type", help="Inspect one full type name instead of listing classes")
    parser.add_argument("--project", action="store_true", help="Decompile all types into local/decompiled")
    parser.add_argument("--assembly", default="Assembly-CSharp.dll", help="Filename within the isolated Managed directory")
    args = parser.parse_args()
    if args.type and args.project:
        parser.error("--type and --project are mutually exclusive")
    dotnet = LOCAL / "tools/dotnet/dotnet"
    candidates = list((LOCAL / "tools/ilspy/tools").glob("net8.0/any/ilspycmd.dll"))
    if not dotnet.is_file() or len(candidates) != 1:
        raise SystemExit("Toolchain not ready; complete tools/setup_toolchain.py first.")
    managed = LOCAL / "test-install/Going Under_Data/Managed"
    if Path(args.assembly).name != args.assembly:
        parser.error("--assembly must be a filename, not a path")
    assembly = managed / args.assembly
    output = LOCAL / "inspection"
    output.mkdir(exist_ok=True)
    env = os.environ.copy()
    env.update({
        "DOTNET_ROOT": str(dotnet.parent),
        "DOTNET_CLI_HOME": str(LOCAL / "dotnet-home"),
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_NOLOGO": "1",
        "NUGET_PACKAGES": str(LOCAL / "nuget-packages"),
        "XDG_CACHE_HOME": str(LOCAL / "cache"),
        "XDG_CONFIG_HOME": str(LOCAL / "config"),
    })
    command = [str(dotnet), str(candidates[0]), "-r", str(managed)]
    if args.project:
        destination = LOCAL / "decompiled"
        destination.mkdir(exist_ok=True)
        command += ["-p", "-o", str(destination)]
        filename = "decompile.log"
    elif args.type:
        command += ["-t", args.type]
        # Type names from command line cannot escape the output directory.
        filename = hashlib.sha256((args.assembly + ":" + args.type).encode()).hexdigest()[:16] + ".cs"
    else:
        command += ["-l", "c"]
        filename = "classes.txt"
    command.append(str(assembly))
    with (output / filename).open("w") as stdout:
        subprocess.run(command, env=env, stdout=stdout, check=True)
    record = {
        "assembly_sha256": hashlib.file_digest(assembly.open("rb"), "sha256").hexdigest(),
        "assembly": args.assembly,
        "type": args.type, "project": args.project, "output": filename,
    }
    (output / (filename + ".json")).write_text(json.dumps(record, indent=2) + "\n")
    print(f"Inspection saved to local/inspection/{filename}")


if __name__ == "__main__":
    main()
