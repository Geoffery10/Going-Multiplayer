# M3 player replication (started)

Status: **foundation started**, v0.15.0, 2026-11-21. Not a playable M3 slice yet.

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

## Down and revive (2026-11-21)

- While a session is live, `Entity.TriggerDeath` is intercepted for players: a
  lethal hit puts them **down** (health 0, prone pose, no input) instead of
  running the game's death/game-over flow.
- Snapshot health is now authoritative for its owner and is applied to the peer
  actor on both sides, so both clients agree on who is down.
- A living player within 2.5 units who holds Interact for 1.2 s revives them:
  half of the giver's current health (rounded down) is transferred, the giver
  must have at least 2 health and keep at least 1 afterwards. A `Revive` message
  carries the target id and amount so the revived client restores its own
  player.
- Downed HUD: the downed player sees "You are down", the rescuer sees "Hold
  Interact to revive" with a progress bar. Reviving plays the heart poof.
- Party wipe: if the host and the peer are both down for 4 s, the host hands the
  run back to the game's own death flow (`TriggerDeath` with the revive guard
  disabled), which ends the run; the guest follows the scene change.
- Revives are host-authoritative: a guest sends `ReviveRequest`, the host
  validates (target down, giver at least 2 health, within 3 units, giver keeps
  at least 1) and broadcasts a `Revive` result carrying giver, target and
  amount. The giver's client deducts, the target's client resurrects.
- Taking damage cancels a revive hold. The revived player plays the `Yay`
  trigger plus the heart poof.

## Animation and door tether (2026-11-21)

- Peer commands are now applied on both sides. The host still simulates the
  guest; the guest feeds the host's commands to the host puppet, whose transform
  stays snapshot-driven. `RemotePuppet` also writes the command into the
  player's native `inputVector`, so `UpdateAnimator` runs the real walking,
  strafing and attack poses. Before this only the host saw animations, because
  only its copy was simulated.
- A guest that opens a scene-changing door sends a **scene intent**; the host
  performs the same transition, then re-announces the scene and seed, so the
  party moves together instead of the guest being pulled back.
- Scripted moves inside a scene (special-room doors, elevators) are detected as
  a >4 unit jump by the local player. The host moves its simulated actors by
  the same delta and broadcasts it; a guest that initiates the jump asks the
  host to follow. The receiving side suppresses its own detection so the delta
  is never echoed back.
- Room-to-room doors inside a floor do not change scenes; they depend on the
  host replaying the guest's interact command, which is unverified in a real
  session.

## Background running and session health (2026-11-21)

- `Host`/`Join` set `Application.runInBackground = true` and `Stop` restores it.
  Without it Unity stops the player loop when the window loses focus, which
  freezes Steam callbacks, the session pump and the game itself.
- The host re-announces its scene every 2 s (with the seed the current floor was
  built from). A guest that drifts to another scene — for example by taking a
  door itself — is reloaded back to the host's scene instead of desyncing
  forever. Host-initiated transitions still lead.
- Every 10 s both sides log `GMP_COOP_STATS` (scene, corrections, hard snaps,
  last error, RTT, peers, commands, focus) so the next session can tune
  reconciliation from real numbers.

## Session save and scene sync (2026-11-21)

- On join the host sends its active save file in reliable chunks. The guest
  writes it to slot 2, first backing up that slot to
  `GoingUnderSave_2.coopbak`, then mounts it. While it is mounted every game
  save is blocked (`SaveGuard`), so an autosave can never touch the guest's
  real slots 0/1. Leaving restores slot 2 and remounts the guest's own slot; a
  crash leaves the backup and the next launch logs `GMP_SAVE_BACKUP pending=`.
- The host announces its scene and the guest loads it when it differs, so both
  clients play the same office variant (`FizzleFlagshipFinal` vs `FizzleDARK`
  was a real mismatch) and the same dungeon selection.
- Every scene announcement carries a seed. Both clients call
  `Random.InitState(seed)` immediately before `DungeonManager.GenerateFloorRoutine`
  so a floor starts from the same RNG state. Not yet proven with two clients;
  a host-built layout manifest is the fallback.
- Only the host drives scene changes. Guest-initiated doors/floors are not
  forwarded yet, and joining mid-dungeon cannot reproduce a floor that was
  already generated.

## Reconciliation (2026-11-21)

- The host publishes the guest's actor as an authority snapshot
  (`SendSnapshotFor`), delivered through a separate `OwnSnapshotReceived`
  callback so it cannot be confused with a peer's own pose.
- `Reconciliation.Apply` is desync recovery, not a leash: no correction at all
  below a 3 unit error, then a fast glide (speed scales with the error, up to
  60 u/s). Ordinary lag between two moving players is roughly speed x RTT and
  stays well under the threshold, so walking is never dragged back.
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
