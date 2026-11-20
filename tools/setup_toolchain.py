"""Download pinned official tools into ignored local storage; never launch the game."""
import hashlib
import json
from pathlib import Path
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / "local"
DOWNLOADS = LOCAL / "downloads"


def download(url, name, expected_sha512=None, expected_sha256=None):
    DOWNLOADS.mkdir(parents=True, exist_ok=True)
    path = DOWNLOADS / name
    if not path.exists():
        partial = path.with_suffix(path.suffix + ".partial")
        print(f"Downloading {name}", flush=True)
        with urllib.request.urlopen(url, timeout=120) as response, partial.open("wb") as out:
            while chunk := response.read(1024 * 1024):
                out.write(chunk)
        partial.rename(path)
    sha512 = hashlib.sha512(path.read_bytes()).hexdigest()
    if expected_sha512 and sha512.lower() != expected_sha512.lower():
        raise RuntimeError(f"Official SHA-512 mismatch: {name}")
    sha256 = hashlib.sha256(path.read_bytes()).hexdigest()
    if expected_sha256 and sha256.lower() != expected_sha256.lower():
        raise RuntimeError(f"Pinned SHA-256 mismatch: {name}")
    record = {"url": url, "sha256": sha256, "sha512": sha512,
              "publisher_sha512_verified": bool(expected_sha512)}
    manifest_path = DOWNLOADS / "manifest.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
    if name in manifest and manifest[name] != record:
        raise RuntimeError(f"Pinned download changed: {name}")
    manifest[name] = record
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"SHA-256 {name}: {sha256}", flush=True)
    return path


def extract_zip(path, target):
    with zipfile.ZipFile(path) as archive:
        for name in archive.namelist():
            destination = (target / name).resolve()
            if not destination.is_relative_to(target.resolve()):
                raise RuntimeError(f"Unsafe archive path: {name}")
            if destination.exists() and not name.endswith("/"):
                raise RuntimeError(f"Refusing to overwrite: {destination}")
        archive.extractall(target)


def main():
    sdk = download(
        "https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.428/"
        "dotnet-sdk-8.0.428-linux-x64.tar.gz",
        "dotnet-sdk-8.0.428-linux-x64.tar.gz",
        "33212bb99a2bf003973d41106c7e8fe94208a12958ff601d4a476be379403c85a7"
        "dc9183a6ec64dfb44ded7102965be09f35add1758e68e9251bae7891d7b7bf",
    )
    sdk_dir = LOCAL / "tools/dotnet"
    if not sdk_dir.exists():
        sdk_dir.mkdir(parents=True)
        with tarfile.open(sdk) as archive:
            archive.extractall(sdk_dir, filter="data")
    ilspy = download(
        "https://api.nuget.org/v3-flatcontainer/ilspycmd/9.1.0.7988/"
        "ilspycmd.9.1.0.7988.nupkg", "ilspycmd.9.1.0.7988.nupkg",
        expected_sha256="2b5058f5ccc164c33b7aabf1a5eb0cf3d3a6af6c145aaf58efd3ed891443af7c",
    )
    ilspy_dir = LOCAL / "tools/ilspy"
    if not ilspy_dir.exists():
        extract_zip(ilspy, ilspy_dir)
    bepinex = download(
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/"
        "BepInEx_win_x64_5.4.23.2.zip", "BepInEx_win_x64_5.4.23.2.zip",
        expected_sha256="f752ce4e838f4c305b9da1404b6745f2cff23b8bfd494f79f0c84d0a01f59b46",
    )
    test_dir = LOCAL / "test-install"
    if not (test_dir / "Going Under.exe").is_file():
        raise RuntimeError("Isolated test copy missing")
    if not (test_dir / "BepInEx").exists():
        extract_zip(bepinex, test_dir)
    print("Tools extracted. No game launched. See local/downloads/manifest.json.", flush=True)


if __name__ == "__main__":
    main()
