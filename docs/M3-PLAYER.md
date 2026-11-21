# M3 player replication (started)

Status: **foundation started**, v0.13.0, 2026-11-21. Not a playable M3 slice yet.

## Implemented foundation

- `InputCommand` now carries movement, yaw, camera axes, held/pressed/released
  action flags, and inventory.
- `NetworkInputState` stores the latest accepted host-side command and clamps
  axes.
- `LocalSeats` has a command-backed input path for every `PlayerInputRewired`
  method currently used: movement, camera, attack/alt-attack, dodge, interact,
  throw, focus, focus-swap and inventory.
- `ActorRegistry` attaches a `NetworkInputState` to remote actors.
- `SessionProbe` applies received commands to the peer actor and now checks
  per-client view identity:
  - local camera present and active,
  - no enabled camera on the remote actor,
  - HUD bound to the local `Player.singlePlayer`.
- M1 suite passes on v0.13.0 with `view_ok=True` on both clients; quick
  regression passes 8/8. DLL SHA-256
  `76a9b868071c73e4188d8d8ebb2dd06bcf467c61ff0f34a9cb05add6fa847eaa`.
  The ESC Co-op tab uses the game's own card sprite, fonts and capsule buttons
  (cloned from the settings menu) instead of an IMGUI overlay.
- `SessionCoreTests` covers the expanded command codec and network-input
  clamping/edges: 31 checks.

## Command/action authority (2026-11-21)

- The host now simulates the guest's actor from its commands: `InputCapture`
  samples the local player's real input, `SteamCoop` sends it at 30 Hz, and the
  host applies it through the remote seat's patched `PlayerInputRewired`.
- `ActorRegistry.AddRemote(..., simulate)` creates the guest actor unlocked with
  physics on the host; the guest keeps a snapshot puppet of the host.
- `NetworkInputState` gives presses/releases one-frame edge semantics (30 Hz
  commands would otherwise repeat attacks or double-toggle focus), including
  inventory steps.
- `NetSession` uses one sender sequence across snapshots and commands, and the
  host publishes snapshots for the actors it simulates.
- Gate: guest sends MoveX and an Attack press over the real session; the host
  reports `attack_host=True` from the guest actor's native state and command
  driven `remote_delta`; the guest reports `attack_local=False`. Mutation twins
  fail as expected: `--negative command` (host ignores commands) and
  `--negative snapshot` (guest ignores host snapshots).

## Reconciliation (2026-11-21)

- The host publishes the guest's actor as an authority snapshot
  (`SendSnapshotFor`), delivered through a separate `OwnSnapshotReceived`
  callback so it cannot be confused with a peer's own pose.
- `Reconciliation.Apply` pulls the guest's locally simulated player back toward
  that pose: no correction inside a 0.75 unit deadband, a 15% step (max 0.6) for
  small errors, and a hard snap above 3 units.
- The deadband is sized for 20 Hz snapshots at walking speed; there is no
  rollback or input replay yet, so this is correction, not full prediction.
- Session identity rule: a peer may only speak for itself, except that the host
  may publish the guest's own player id. A guest claiming the host's id is
  rejected (unit-tested).
- Gate: the guest's local player is shoved 3.5 units off its authoritative pose
  while it keeps commanding the host to walk; the guest must apply at least one
  correction and end within 0.5 units of the authority. Twin:
  `--negative recon` (guest ignores its own snapshots) fails with 0 corrections.

## Not implemented yet

- No rollback/prediction: the guest simulates locally and is corrected when the
  error exceeds the deadband; there is no input replay or smoothing of the
  correction.
- Only attack has been exercised through the authority path; dodge, interact,
  throw, focus and inventory are plumbed but not gated.
- Remote actors can still run local-only effects (camera snap on focus) because
  `Player.Update` is shared.
- No client prediction/reconciliation for actions.
- No animation-state snapshot; one-shot triggers, weapons and effects are not
  replicated.
- No party HUD/teammate health.
- No party-tethered door/floor transition coordinator.

## Next slices

### M3a — action authority (started)

1. ~~Send `InputCommand` at 30–60 Hz; host applies it to `NetworkInputState`.~~
2. ~~Gate one action (`Attack`) on the host actor with the guest not acting.~~
   `Focus`, `Dodge`, `Interact` and inventory need the same gates.
3. Mutation twins: ~~host ignores commands~~, ~~guest ignores snapshots~~,
   ~~guest ignores authority snapshots~~; dropped/stale command and wrong player
   still to add.
4. ~~Reconciliation for the guest's own actor.~~ Rollback/prediction and
   correction smoothing remain; actions are intentionally not predicted yet.

### M3b — animation and HUD

1. Snapshot compact animation state: `Walking`, `Forward`, `Sideways`,
   `ChargingThrow`, `Blocking`, `Prone`, `Dead`, stance/aiming, weapon id.
2. Use reliable events for one-shot triggers: attack chain, throw, dodge,
   pickup, jump, damage/death.
3. Add teammate health/status HUD; keep each client's own HUD local.
4. Verify local camera follows the local actor after door/cutscene transitions.

### M3c — party-tethered rooms

1. Door/floor transition coordinator: either player initiates, both move
   together; block or leash the non-initiator.
2. Boss/arena rooms forced together.
3. All-ready barriers and stale-room rejection.

Option B (independent rooms) remains post-v1/M8; see
[the room, camera and HUD policy](ROOM-CAMERA-PLAN.md).
