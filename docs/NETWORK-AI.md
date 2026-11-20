# Network enemy tests: O4a targeting and O4b movement

These are deliberately narrow slices of O4, not general networked enemy or
combat support. They are inert without `-gmp-net-ai-probe` (targeting) or
`-gmp-net-brain-probe` (native host movement).

```sh
python tools/build_plugin.py --deploy-test
python tools/net_ai_test.py --suite
python tools/net_ai_test.py --brain --suite
```

Use the existing authenticated sandbox setup with a local X display. Do not
build/deploy or run another suite while either client is active.

## O4a recorded result (v0.7.0)

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

## O4a scenario and oracle

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

## O4b: native moving host enemy and guarded mirror

The `--brain` suite runs O4a's three phases, then:

1. Prepares the two stationary player actors and one authored enemy once.
   The native host brain runs for six seconds. No probe-driven enemy movement
   occurs during that interval. Require native brain entry observations,
   displacement over 0.5 m and approach toward P2 over 0.5 m.
2. Sends sequenced enemy-id-1 snapshots from authority 1 at epoch 1. The guest
   applies the pose/target and records its actual transform. The scorer joins
   at least 20 host/guest samples by sequence and compares positions, target id
   and the transported native state label. State labels are diagnostic metadata,
   not proof of guest animation playback or guest brain execution.
3. Stops the host brain and freezes its body. The guest applies that phase once,
   then observes at least two seconds and 60 frames without repeated corrections.
   Host and guest drift must remain below 0.05 m.

`EnemyMirror` installs guards on declared `Activate`, `OnActivated` and
`StartThonk` overrides before instantiation. A short creation transaction
protects calls before the clone can be registered. Two native entry probes
inside that transaction and two after registration must be blocked. The mirror
has its root brain disabled, `hasBrain=false`, a kinematic body and inactive
colliders. Root-owned coroutines are stopped. This is verified for the tested
prefab; arbitrary child scripts or tasks already launched on other managers
are not thereby certified.

The handshake compares a collision-fixture fingerprint: stationary,
non-trigger, non-entity, non-rigidbody collider metadata (world bounds,
rotation/scale, mesh name/vertex count), quantized to two decimal places.
It is not a full mesh-content digest, navigation/world-state snapshot or shared
procedural-generation implementation.

### O4b mutation controls

- `--brain --negative pose`: omit guest pose application during native motion.
  Require a real cross-trace pose discrepancy over 0.5 m, not merely a failure flag.
- `--brain --negative sim`: deliberately enable the guest native brain during
  the frozen interval. Require both observed native entries and autonomous
  drift over 0.05 m; a missing client or unrelated host failure is not accepted.

### O4b recorded result (v0.8.0)

The complete 11-stage serial regression passed, including 55 Python tests,
27 input-mapping checks, O4a and all three O4b gates. No physical controllers
were connected or required.

- Positive: authored `CubicleGroundBotElectricity Variant` / `CubicleGroundBot`;
  host displacement 3.706 m, approach 1.760 m, 102 matching pose samples,
  guest pose error and both frozen drifts 0 m, four blocked guest entries,
  no guest native starts.
- Omitted-pose control: host passed; guest failed with 0 m applied movement
  and 6.451 m pose error.
- Guest-simulation control: host passed; guest failed with native entries
  observed and 2.135 m autonomous freeze-interval drift.
- All final launches exited with expected codes and zero owned processes left
  to clean. Original 207 game files rehashed unchanged afterward.

Verified DLL SHA-256:
`06ad9a5aafbde985af731bb6fe4d14204eaf98c2f4e1d7df969ad9477b4a18e5`.
Private evidence is under `local/profiles/net-brain-<run>/`; the aggregate is
`local/net-brain-suite-summary.json`.

The v0.8.1 presentation-only startup shortcut was subsequently verified with the
complete 12-stage regression, including both O4 suites and their mutation twins.
It does not bypass dungeon generation, Steam initialization or gameplay setup.

## Not covered

- Shared floor seed, layout, room identity or general scene replication.
  Matching scene/floor numbers is only a fixture check.
- Host-simulated guest input, networked combat or damage following aggro.
- Other enemy types, native attack/damage replication, animations, health,
  deaths, loot, bosses or general multiple-enemy identity/ownership.
- Loss/jitter/reordering, reconnects, Steam transport or separate machines.

These remain required before full O4 or playable online co-op can be claimed.

## Next slice: native attack/damage attribution

Require a native host attack to produce a traceable hit on the selected remote
player, apply health changes only under host authority, and replicate the
result to the guest exactly once. Add wrong-target and duplicate-damage controls.
Target selection, movement and transported state labels alone do not establish
networked combat. Full host-simulated player input (O2) also remains incomplete.

Scene/floor number equality is not a shared-room/layout oracle. Establish a
common fixture or validate the relevant geometry before relying on movement or
combat across the two generated worlds. Keep these findings separate from the
already passed O4a/O4b fixture results.

Source inspection also identified two traps for that work:

- Some enemies activate from Awake, not just OnEnable. Creating under an
  inactive parent and disabling the component is therefore not sufficient
  without guarding activation when the clone becomes active.
- The game seeds Unity's global random generator from wall-clock time, and
  camera shake can reseed that same generator again. A one-time shared seed is
  not a demonstrated deterministic floor-generation solution.

Continue using verified common fixtures for early combat gates. Broader
host-authored layout replication belongs to the later floor/world work;
do not extrapolate O4b's matching collider metadata to arbitrary floors.
