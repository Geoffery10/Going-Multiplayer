# M2/M3 playtest guide

Status: **internal playtest build**, v0.15.0. This is the first in-game Steam
co-op path. It is not a finished co-op run.

## What the build currently does

- Host creates a friends-only Steam lobby under the real AppID.
- Host can open the Steam invite overlay or copy the lobby id.
- Guest can accept a Steam invite, join by pasted lobby id, or launch through
  `+connect_lobby`.
- Both clients exchange presence, snapshots and remote-player poses.
- On join the guest receives the host's save (written to a temporary co-op slot
  with the previous contents backed up) and is moved to the host's scene, so
  both play the same progression. Saves are blocked on the guest while the
  co-op save is mounted.
- Each client uses its own camera and HUD; the remote actor's camera is
  disabled.
- The remote player is a display puppet. Movement is not yet host-authoritative;
  the party stays in one active room.

## Setup

Both players need the same game build and plugin build.

- **Do not launch the normal Steam library copy.** It does not contain the
  plugin; the original install is intentionally untouched.
- Host on Linux/Proton: `python tools/play_coop.py` opens the isolated modded
  copy.
- Host/guest on Windows: launch the modded test copy (the install with
  `BepInEx/plugins/GoingMultiplayer/GoingMultiplayer.dll`) directly with Steam
  running.
- Do not share accounts or game files.
- Both should load into the office before judging whether the other is visible.

If the Co-op tab is missing, check `BepInEx/LogOutput.log` for
`GMP_LOADER_LOADED version=0.15.0 networking=steam_coop` and `GMP_COOP_TAB installed`. If those lines
are absent, the running copy is not the modded one.

## Friend test kit (Windows)

The plugin, BepInEx core, Doorstop and a short `INSTALL.txt` are packed as
`artifacts/GoingMultiplayer-0.15.0-win64.zip`. The zip root
is the game folder: `winhttp.dll`, `.doorstop_version`, `doorstop_config.ini`,
`BepInEx/core/*`, `BepInEx/plugins/GoingMultiplayer/GoingMultiplayer.dll`.

The kit is built from the local, already-verified install; no game files or
save data are included. The session compares the plugin DLL SHA-256, so both
sides must use the same zip.

Ask the guest for their `BepInEx/LogOutput.log` (whole file, unedited) and a
screenshot of the Co-op tab. The loader line to confirm is
`GMP_LOADER_LOADED version=0.15.0 networking=steam_coop`.

## Test save

The isolated co-op profile has a copy of the main save, so the title screen can
continue mid-story instead of starting a new run. The source save is never
modified.

To refresh it from the original save directory:

```sh
python tools/copy_save.py \
  --source "<Steam compatdata>/1154810/pfx/drive_c/users/steamuser/AppData/LocalLow/Aggro Crab/Going Under" \
  --profile coop-preview --profile prototype-preview
```

`--slot N` selects a different save slot (0–2). Existing destination saves are
backed up under `local/save-backups/`.

The copied slot was loaded in a save probe and reported story progress
(`introCutsceneViewed=True`, tutorial complete, prior runs/relics present).

## Host

1. Open **ESC** and select the **Co-op** tab, or press **F7** for the quick
   panel.
2. Press **Host**. Wait for the lobby id and `Hosting Steam co-op`.
3. Press **Invite** to open the Steam invite overlay, or give the guest the
   lobby id.
4. The Co-op tab lists lobby members and has **Invite** / **Kick** controls.
5. Load into the office.
6. The remote player should appear when the guest joins and enters the office.

## Guest

1. Accept the Steam invite, or open the panel with **F7**, copy the host's
   lobby id, and press **Join clipboard**.
2. Load into the office.
3. The host should appear as the remote actor.

If the game was launched from a Steam invite, the `+connect_lobby` path joins
automatically once Steam is ready.

## What to check

- Both clients show their own HUD and their own camera.
- The other player appears and moves with interpolation.
- The remote actor has no active camera.
- Pressing **Leave** removes the remote actor and leaves the lobby.
- The Co-op tab lists players; the host can kick a connected guest.
- Re-hosting/rejoining does not leave duplicate actors.
- A version/build mismatch shows an error instead of joining.

## Known limits

- Scene-changing doors (elevator/floors) move the whole party: the guest's
  transition is forwarded to the host, which performs it and re-announces.
  Room-to-room doors are host-replayed and not yet verified with two players.
- The window can be unfocused: co-op keeps running (and so does the game).

- No host-authoritative actions yet: attack, dodge, throw, interact, focus and
  inventory are still local.
- No animation/weapon/effect replication; the remote character may slide.
- One active room only. Do not split up through different doors; this is
  Option A in [the room/camera/HUD policy](ROOM-CAMERA-PLAN.md).
- Boss/arena and floor transitions are not party-tethered yet.
- Lobby member count can be stale on the host; the session peer list is the
  source of truth.
- Kick is a game-session message. A guest that ignores it may remain in the
  Steam lobby until it leaves; Steam's lobby API here has no direct kick.
- The Steam invite acceptance UI is only exercised in the real in-game flow,
  not the standalone harness.

## Automated checks

```sh
python tools/coop_host_test.py          # production host smoke, minimized
python tools/verify.py --stages coop-host
python tools/steam_host_test.py         # probe host-only lobby smoke
python tools/test_harness.py            # UDP harness
python tools/verify.py --quick --focus session-loopback
```

`coop_host_test.py` uses `xdotool` to minimize the test window when available.
It verifies the production `SteamCoop` controller creates a real-AppID lobby and
starts a host session without crashing.

To check the ESC-menu tab renders, run the isolated copy with
`-gmp-coop-menu-probe`; it loads the office, waits for the intro to settle, opens
the pause menu on the Co-op tab and writes `gmp-coop-menu.png` next to the game
executable; it hosts a real lobby first so the player row and lobby buttons are
in the capture. Add `-gmp-ui-dump` to also log the pause-menu hierarchy. The last
run reported `tab_active=True open=True name=GMP Co-op Tab` after invoking the
cloned button's real click handler.

## Evidence

- `local/coop-host-summary.json`
- Per-run profile folder printed by the harness
- Both clients' `BepInEx/LogOutput.log`
