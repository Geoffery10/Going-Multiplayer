"""Copy a Going Under save slot into an isolated test profile.

The source save is read only. Existing destination saves are backed up under
local/save-backups/ before being replaced.
"""
import argparse
import hashlib
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = Path("/tmp/opencode/going-under-coop")
SAVE_DIR = Path("drive_c/users/steamuser/AppData/LocalLow/Aggro Crab/Going Under")


def digest(path):
    return hashlib.file_digest(path.open("rb"), "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True,
                        help="Original save directory containing GoingUnderSave_N.dat")
    parser.add_argument("--profile", action="append", default=[],
                        help="Isolated profile name; may be repeated")
    parser.add_argument("--slot", type=int, default=0, choices=(0, 1, 2))
    args = parser.parse_args()
    if not args.profile:
        args.profile = ["coop-preview", "prototype-preview"]

    source = args.source / ("GoingUnderSave_%d.dat" % args.slot)
    if not source.is_file():
        raise SystemExit("Source save not found: " + str(source))
    source_hash = digest(source)
    print("Source " + str(source))
    print("SHA-256 " + source_hash)

    backup_root = ROOT / "local/save-backups"
    for profile in args.profile:
        target_dir = RUNTIME / "profiles" / profile / "compatdata/pfx" / SAVE_DIR
        target_dir.mkdir(parents=True, exist_ok=True)
        target = target_dir / source.name
        if target.exists():
            backup = backup_root / (profile + "-" + target.name)
            backup.parent.mkdir(parents=True, exist_ok=True)
            if not backup.exists():
                shutil.copy2(target, backup)
            print("Backed up " + str(target) + " -> " + str(backup))
        shutil.copy2(source, target)
        print("Copied to " + str(target))

    if digest(source) != source_hash:
        raise SystemExit("Source save changed during copy")
    print("Source save unchanged.")


if __name__ == "__main__":
    main()
