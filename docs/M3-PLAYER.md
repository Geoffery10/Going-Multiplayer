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
  `e38e1b312d4fa1111ab2d4e91c532d8cd35d309c946de9143050da9f3fc2ddb9`.
  The ESC Co-op tab uses the game's own card sprite, fonts and capsule buttons
  (cloned from the settings menu) instead of an IMGUI overlay.
- `SessionCoreTests` covers the expanded command codec and network-input
  clamping/edges: 31 checks.

## Not implemented yet

- The production session does not yet send commands from the guest; the probe
  only exercises the command boundary.
- No host-authoritative action gate: attack, dodge, interact, throw, focus and
  inventory actions still use local input.
- No client prediction/reconciliation for actions.
- No animation-state snapshot; one-shot triggers, weapons and effects are not
  replicated.
- No party HUD/teammate health.
- No party-tethered door/floor transition coordinator.

## Next slices

### M3a — action authority

1. Send `InputCommand` at 30–60 Hz from the guest; host validates sequence and
   applies it to the remote `NetworkInputState`.
2. Add a real-game gate for one safe action (`Focus` first, then `Attack`,
   `Dodge`, `Interact`) proving the host actor's native input sees the command
   and the guest does not simulate it.
3. Add mutation twins: dropped command, stale command, wrong player, local
   guest simulation.
4. Keep movement prediction separate; do not predict actions yet.

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
