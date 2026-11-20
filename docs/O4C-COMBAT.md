# O4c combat: native attack and health replication (in progress)

Status: **not passing.** The host-side native attack fixture has not yet produced a
validated hit. Do not read any part of this page as O4c being complete.

## What is implemented and verified

`DamageLedger` (`src/GoingMultiplayer/DamageLedger.cs`) is the authority boundary
for replicated health events. `python tools/test_damage_ledger.py` compiles and
runs it against the pinned SDK: **16 checks pass**.

The ledger:

- rejects a wrong epoch (older or newer than the session's),
- rejects unknown target ids, non-finite or out-of-range amounts and non-positive ids,
- drops duplicates (same event id applied twice) without spending the id on a
  rejected attempt,
- returns `Apply` only for the first valid application of an event.

This is the piece the duplicate-damage mutation depends on: one event may be
applied exactly once, whatever the transport delivers.

`tools/damage_test.py` and its scorer unit tests are also in place. The scorer
joins both client traces and requires: matching build/session/geometry/scene/floor,
a healthy host attack, one applied delta on the guest, at least two received
duplicates, the right routed target, and a specific observed mismatch for each
mutation (`--negative target`, `--negative duplicate`). Scorer tests currently
pass; they are unit tests over synthetic traces, not game evidence.

## Why the gate does not pass yet

Two real-game attempts were run with `-gmp-net-damage-probe`, with these findings:

1. **Entrance-room conversation.** `Player.canBeDamaged` is gated on
   `DialogueManager.instance.isConversationActive` (PixelCrushers), which is a
   separate flag from `GameManager.conversationActive`. Entering the dungeon
   starts a scripted intro conversation that waits for player input. The probe
   now ends it through the game's own paths (`Player.CutsceneEnded`,
   `CameraController.StopCutscene`, `DialogueSystemController.StopConversation`)
   and both actors then report `canBeDamaged=True`. No invincibility is forced.
2. **Authored attack nodes are variant-specific.** `CubicleGroundBot*` prefabs
   carry `CubicleGroundBotFireNode`, `CubicleGroundBotElectricNode` and
   `CubicleGroundBotBladeNode` plus one `Hitbox`. Re-activating the `Nodes`
   container to obtain a pulse enables every sibling node; unconfigured siblings
   throw `NullReferenceException` in `BladeRoutine` at its first statement
   (`view` is null). Assigning the node's public `view` reference to the enemy's
   own view is not sufficient for the siblings, and no explosion or melee hit
   has been observed on the host.

Because no native hit occurred, no damage was replicated and the gate correctly
reported FAIL. Nothing was coerced: the probe never creates a `HitInfo`, never
forces `forceDamage`, and never bypasses `canBeDamaged`.

## Next steps

- Spawn the attack node through a path that does not enable sibling nodes, or
  select a prefab whose attack node is actually authored for that variant.
- Confirm a native host hit (`GMP_DAMAGE_RESULT` `hits=1`, `source=Melee` or
  `Explosion`) before anything is replicated.
- Then validate the ledger path with the two mutation controls.
- Keep wrong-target and duplicate-damage as the gate's failure twins; a mutation
  is accepted only with its own observed mismatch, never with a host timeout.

Full player-input authority (O2) is separate and still incomplete.
