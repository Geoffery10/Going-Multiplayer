# Going Under integration findings

These findings come from static inspection of the installed Windows Steam build
20206372, Unity 2018.4.24f1. Decompiled files are private and not distributed.
Runtime behavior remains unverified unless explicitly stated.

## Observed in managed code

- `Player.Awake` destroys a duplicate player, or replaces the original if
  `keepThisPlayer` is set. Neither behavior permits normal co-op spawning.
- `Player.health`, `overHealth`, `MaxHealth` and `data` route through
  `PlayerData.instance`. A cloned character would not automatically get its own
  health or inventory.
- `PlayerData.Awake` also rejects duplicate instances.
- `PlayerData.SpawnPlayer(Vector3)` instantiates `playerPrefab`.
- `Player.OnEnable` and `OnDisable` maintain `GameManager.playerList`.
- `PlayerInput` exposes movement, focus, attack, dodge, interact, throw and
  inventory commands. `PlayerInputRewired` is its shipped controller-backed
  implementation. However, `Player.input` returns the concrete
  `PlayerInputRewired`, not the interface, and its methods are nonvirtual.
  A network-input implementation needs patching/adaptation, not just a new
  implementation of `PlayerInput`.
- `DungeonManager` exposes generation, room-entry, floor-advance, run-save and
  player-death paths. Co-op must replace solo wipe decisions and coordinate
  procedural results, not just put a remote avatar in a separate dungeon.
- `SaveLoad` uses Easy Save persistent-data paths for three save slots and
  settings. `GetSavePath`, settings construction and deletion all require review
  when introducing separate co-op profiles.
- `SteamManager.Awake` initializes Steam for app 1154810 and quits on failure.
  Do not bypass that check; real Steam-backed tests need explicit account-use
  permission and an authorized Steam session.
- `Enemy.GetNewAggroTarget` and `aggroTarget` exist; many other enemy paths
  reference `Player.singlePlayer`. Full targeting and combat review is pending.

## Existing mod evidence

[Cornercutter](https://github.com/franomai/cornercutter) documents an existing
BepInEx/Harmony plugin for Going Under with challenge/debug features.
It does not document co-op. Its README does not establish compatibility with our
exact build; no code has been copied.

## Implementation direction — proposal

First prove loader execution. Then separate player identity/data/input from the
single-player globals before spawning an active second character. Use the
host's world results for enemies, shared pickups and progression; choose a
transport only after its API and the game's Steam integration are inspected.

## Current runtime limitations

- Project storage rejects symlink creation, so it cannot hold a working Wine
  prefix. The isolated Wine prefix uses the approved local temporary workspace.
- Isolated Wine initialization succeeded on local temporary storage.
- Unity initialized in batch mode using the existing X display and a null
  graphics device. Without an X display it failed to create its batch window.
- The game reached Steam initialization and exited because the isolated offline
  launch could not authenticate. No Steam check was modified.
- BepInEx preloader failed during cryptographic random-source initialization
  under system Wine. Switching to the existing Proton runtime resolved this.
- The real-game loader smoke suite passed under Proton: our plugin loaded,
  required hooks were found, Steam was valid, at least one scene loaded, and
  the game exited with code zero. A second run with intentionally broken hook
  validation correctly reported FAIL and exited with code one.
- Tests use a unique run identifier and reject stale/missing results, duplicate
  results, process failure and timeouts.
- No online co-op or combat synchronization has been tested.

## Independent-actor milestone

Rendered, single-process office tests have now demonstrated two and five
characters with distinct data/input/inventory identities, independent health
state writes, native movement and a native item pickup held by the extra actor.
A deliberately disabled remote health accessor failed for the intended reason.
Native inventory exception handling restored the original player singleton.

The game's framebuffer was inspected: palette-bearing costume meshes must be
selected along with their materials. Material-only swapping retained the
original visible colors. Complete cloned authored costumes produce visibly
different outfits while preserving player one's material and costume.

This is not a multi-machine, controller-assignment, combat, save-persistence
or synchronized-floor test. See [the prototype notes](LOCAL-PROTOTYPE.md).
