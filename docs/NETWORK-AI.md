# Network enemy targeting: O4a

This is the first, deliberately narrow slice of O4, not a general networked
enemy implementation. It is inert without `-gmp-net-ai-probe`.

```sh
python tools/build_plugin.py --deploy-test
python tools/net_ai_test.py --suite
```

Use the existing authenticated sandbox setup with a local X display. Do not
build/deploy or run another suite while either client is active.

## Recorded result

On v0.7.0, the positive gate and both mutation controls passed. Both clients
completed all three phases with matching DLL hashes, scene and floor.

- Positive: host acquired P2 and swapped to P1; guest targets matched,
  60 native reacquisition calls were blocked and measured pose drift was 0 m.
- AI mutation: host passed; guest target agreement failed with zero vetoed calls.
- Target-application mutation: host passed; guest target agreement failed with
  60 vetoed calls.
- All six client processes exited with the expected codes; no owned processes
  remained to clean up.

The final corrected v0.7.0 DLL passed the complete expanded serial regression,
including 49 Python tooling/scorer tests and 27 production input-mapping checks.
Its SHA-256 is
`1991e7d55d15a429def0a56b88ea91912f2c8b4292ef89da6bb03ca2bf66cf4f`.
Achievement/stat guards remain active through shutdown rather than being
removed immediately after the asynchronous quit request.

Earlier attempts encountered a coexistence shutdown timeout after a valid
Title/PASS trace and a host timeout without an O4a result. Both remain recorded
failures; the final pass is not a root-cause fix for lifecycle flakiness.
Timeout diagnostics identify only processes owned by the exact launch token.

## Scenario and oracle

Two real game processes enter a dungeon using the native office entrance.
The handshake requires the same run id, DLL SHA-256, scene and floor number.
The host selects one authored enemy prefab; the guest resolves that same
prefab by name. Player ids are mapped consistently: host=1, guest=2.

The host sends epoch-1 snapshots for three acknowledged phases:

1. **Native acquisition:** place an enemy near the host's remote P2 actor and
   activate it normally. Its actual target must become P2 without assigning
   that target from the probe.
2. **Distance swap:** move the enemy near P1 and explicitly call the game's
   reacquisition method. Its actual target must become P1. This is a distance
   control, not a new automatic aggro policy.
3. **Frozen sticky target:** move the inactive host enemy back near P2 while
   retaining P1 as its target. The guest must not decide to target P2 itself.

The guest applies each phase once, samples 20 frames, and waits at least
half a second before acknowledging, without repeated snapshot corrections masking divergence.
It calls the real native `GetNewAggroTarget()` on the mirror each frame; a
mirror-specific Harmony prefix must block these calls. The raw target is read
without triggering the game's target getter, which can itself reacquire.
The guest checks target ids and pose drift (under 0.05 m).

The probe sets the guest mirror inactive, disables its component, makes its
body kinematic and disables its colliders after spawning. This blocks the usual
Update/FixedUpdate route; it does not prove its native brain never started.
Native spawning and some enemy OnEnable methods can schedule activation
coroutines before those settings are applied. The prefix additionally guards
explicit target reacquisition; subclass/coroutine/attack suppression remains
unproven.

## Mutation controls

- `--negative ai`: bypass the guest reacquisition veto. The real method must
  select the nearer P2 during the frozen-target phase and fail the target oracle.
- `--negative target`: omit applying replicated targets. The mirror must fail
  target agreement despite the veto remaining active.

The suite accepts a mutation only when the host passes, the guest exits 1 with
the intended mismatch, all three phases completed and unrelated frozen-pose
checks still pass. Missing results, timeouts, stale run ids, mismatched builds,
scenes/floors, or duplicate results fail scoring.

Per-run evidence is under `local/profiles/net-ai-<run>/`; the aggregate is
`local/net-ai-suite-summary.json`. Evidence stays private.

## Not covered

- Shared floor seed, layout, room identity or general scene replication.
  Matching scene/floor numbers is only a fixture check.
- Host-simulated guest input, networked combat or damage following aggro.
- Native moving/attacking enemy brains mirrored across clients, animations,
  health, deaths, loot, bosses or multiple-enemy identity.
- Loss/jitter/reordering, reconnects, Steam transport or separate machines.

These remain required before full O4 or playable online co-op can be claimed.

## Next slice: moving host brain and spawn-time suppression

Implementation must first prevent mirror activation **during creation**, not
only disable a component afterward. Inspect subclass overrides and coroutines
started on other components/managers; stopping coroutines on the mirror alone
does not establish that all work stopped.

The next oracle should require:

1. A logged authored prefab/type and a stable host-assigned enemy id.
2. Native host brain transitions and measurable native movement toward a valid
   remote-player target, without probe-driven enemy movement.
3. Guest snapshots of the same enemy id, pose, target and brain-state label,
   with sampled agreement against host observations.
4. A host-freeze interval with no guest corrections masking autonomous drift.
5. Mutation controls for omitted pose replication and enabled guest simulation.
6. Native attack/damage evidence attributed to the selected target before
   claiming target-driven combat; target selection alone is insufficient.

Scene/floor number equality is not a shared-room/layout oracle. Establish a
common fixture or validate the relevant geometry before relying on movement or
combat across the two generated worlds. Keep these findings separate from the
already passed O4a target-only result.

Source inspection also identified two traps for that work:

- Some enemies activate from Awake, not just OnEnable. Creating under an
  inactive parent and disabling the component is therefore not sufficient
  without guarding activation when the clone becomes active.
- The game seeds Unity's global random generator from wall-clock time, and
  camera shake can reseed that same generator again. A one-time shared seed is
  not a demonstrated deterministic floor-generation solution.

Prefer a verified common prebuilt fixture for the first moving-brain gate.
Log and compare its room/geometry identity before measuring movement; broader
host-authored layout replication belongs to the later floor/world work.
