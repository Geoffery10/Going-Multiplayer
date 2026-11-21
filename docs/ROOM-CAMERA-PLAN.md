# Room, camera and HUD policy

Decision recorded 2026-11-21.

- **Option A — v1:** each online player has their own camera and HUD, but the
  party shares one active room. Players move independently inside that room.
  Door and floor transitions are party-wide. Boss/arena rooms remain
  party-locked.
- **Option B — later:** independent room movement for normal rooms. Planned,
  not implemented in v1.
- **Option C — split-screen two cameras/HUDs on one PC:** not planned. The
  camera, HUD, room manager and player singleton assumptions make it a
  separate project-scale refactor.

## Why A first

The game has one `DungeonManager.currentRoom`, one active room GameObject, one
`CameraController.instance`, one `GUIManager` and one `Player.singlePlayer`
per process. Online, one process per player makes own cameras/HUDs natural.
Independent rooms require changing the room lifecycle, door transitions,
camera bounds, lighting/audio, room events and host-side simulation.

Relevant code:

- `Player.room => DungeonManager.currentRoom` (`Player.cs`).
- `DungeonManager.EnterDoorRoutine` locks `Player.singlePlayer`, moves the
  camera and activates/deactivates rooms.
- `Room.ActivateRoom`/`DeactivateRoom` enable/disable the whole room.
- `Door.TestDoorOpen` requires `DungeonManager.currentRoom == room`.
- `CameraController.CorrectTarget` clamps to `DungeonManager.currentRoom`.
- `AmbientLightController` and room music/minimap/events use the global room.
- `Enemy.GetNewAggroTarget` already iterates `Character.characters` and filters
  by `character.room`, which helps Option B once player room identity is real.
- `DungeonManager.currentRoom` appears across 96 decompiled files; `Player.singlePlayer`
  appears across 286. Only a subset needs changing, but the central assumptions
  are widespread.

## Current implementation

- v0.13.0 has the first per-client view gate: each client reports its local
  camera active, no enabled camera on the remote actor, and HUD bound to the
  local player (`view_ok=True` in the M1 suite).
- Party-tethered door/floor transitions are not implemented yet.
- Option B is not started; no per-player room identity or room occupancy layer
  exists.
- Split-screen remains out of scope.

## Option A gates (v1)

1. Each client has exactly one enabled gameplay camera, following its local
   actor; remote actor cameras remain disabled.
2. Each client HUD reads its local actor's health/inventory. Teammate/party HUD
   is an additive UI feature, not a second local HUD.
3. Both players can move independently inside the current room.
4. A door transition is initiated by either player but transitions the party:
   both clients load/activate the same next room and the non-initiator is moved
   through the same door or re-leashed inside the room.
5. Boss/arena rooms force the party together; leaving the shared room is
   blocked until the encounter ends.
6. Floor transitions use an all-ready barrier and reject stale floor/session
   epochs.
7. A split party state cannot occur: door, floor, cutscene and revive flows
   either move both players or block the initiator.

## Option B design (later)

Do not treat this as a small toggle. A bounded normal-room implementation needs:

- `Player.CurrentRoom` derived from actual room bounds, not the global current
  room; adjust `Player.room` and room-bound logic.
- Room occupancy/refcounts: a room stays active while any player is inside or
  its enemies are being simulated; empty rooms may deactivate.
- Per-player door transitions: no global `Player.singlePlayer` camera lock or
  room swoop for the other player.
- Camera bounds from the local player's room.
- Ambient light, music, minimap and room events scoped to the local player's
  room; remote-only rooms are muted/hidden on each client.
- Host simulates both occupied rooms; enemies target players in their own room.
- `Room.StartCombat` uses the nearest player in that room, not
  `Player.singlePlayer`.
- Boss/arena/special rooms remain party-locked or get their own room-local
  managers.
- Room-clear, loot and floor-state events are host-authoritative and replicated.

### Option B gates

1. P1 and P2 occupy different normal rooms simultaneously; both rooms remain
   active and both cameras render the correct room.
2. Enemies in each room acquire only players in that room.
3. Door transitions for one player do not deactivate or teleport the other.
4. Audio/lighting/minimap reflect each client's local room.
5. Reuniting players does not duplicate enemies, events or room-clear state.
6. Boss/arena rooms reject independent entry or force co-location.
7. Stale room/floor epochs are rejected; reconnection restores the correct room.

## What not to do

- Do not let the party split in v1 without a room-occupancy implementation.
- Do not use the global `DungeonManager.currentRoom` as a per-player room.
- Do not add a second local camera/HUD for split-screen before online co-op is
  stable.
