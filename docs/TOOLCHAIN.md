# Local toolchain

Run `python tools/setup_toolchain.py` from this project.
It downloads pinned official packages and extracts them under ignored `local/`:

| Tool | Version | Purpose |
| --- | --- | --- |
| .NET SDK, Linux x64 | 8.0.428 | Build and run development tools |
| ILSpy command-line | 9.1.0.7988 | Inspect shipped managed assemblies |
| BepInEx, Windows x64 | 5.4.23.2 | Load plugins in the Windows test copy |

The SDK archive is checked against Microsoft's published SHA-512.
SHA-256 and SHA-512 are recorded for every download in
`local/downloads/manifest.json`; subsequent runs reject changed bytes.
ILSpy and BepInEx SHA-256 values are pinned in the setup script from the first
official-source downloads, not independent publisher checksum verification.

The script refuses to overwrite files while extracting ZIPs. It does not
launch the game, access Steam, modify the original install or write saves.
Downloaded tools are not proof that mod loading works.

`tools/launch_isolated.py` isolates Wine, save paths, PlayerPrefs and Steam
integration using Bubblewrap. The Wine profile lives in the approved local
temporary workspace because project NAS storage does not permit symlinks.
The launcher blocks network access, hides the real home directory and host IPC
directories, and makes the rest of the host filesystem read-only.
`--display` exposes only the local X socket and an authority file for batch-mode
initialization; no desktop capture or input automation is performed.
Do not use the owner's normal Wine/Proton prefix.

## Authenticated loader test

With explicit Steam-use permission:

```sh
python tools/smoke_test.py --proton "<installed Proton directory>" --suite
```

Alternatively, store `{"proton": "<installed Proton directory>"}` in ignored
`local/runtime-config.json`. The authenticated launcher stages Proton's small
runner scripts and locks in temporary storage, keeps installed runtime files
read-only, and supplies a separate compatibility prefix and home directory.
It exposes the running Steam client's files read-only and shared IPC so normal
Steam authentication works. Unlike the offline launcher, it shares host
network/process visibility. It never uses the original game's compatibility
prefix. Steam authentication is not patched or bypassed.
Windowed launches also expose input/hidraw devices and read-only udev metadata
for gamepad detection; existing Unix device permissions still apply. The
launcher does not change permissions, install drivers or change Steam Input.

Results: `local/smoke-summary.json`, with per-gate logs under `local/profiles/`.
The suite covers loader startup only, not co-op.
