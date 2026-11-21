# O4c combat: native electric attack and health replication

Status: **complete for this scoped slice; all 14 full-regression stages passed
on v0.9.0**, including 64 Python tests, 27 input checks and 16 ledger checks.
This is a scoped, test-only loopback fixture, not playable online combat.

## Fixture and authority

`NetDamageProbe` selects the authored electric variant through
`CubicleGroundBot.myNode`, validating its view, glow, explosion prefab and radius.
Unused sibling nodes are disabled before their shared parent is enabled.
After initial spawn the stationary enemy's animator is held disabled: its active
track otherwise switches off the attack container. The authored electric-node
coroutine creates the explosion and native physics/`Player.TakeDamage` produce
the hit. The probe never constructs a `HitInfo`, forces damage or bypasses
`canBeDamaged`.

Both clients dismiss the entrance dialogue through native dialogue/cutscene
APIs before the handshake. Automated tests enable `Application.runInBackground`;
normal/manual launches are unchanged. The combat scorer requires both clients
to report dialogue cleared and background execution enabled.

P1 is separated from the blast; the native host brain acquires nearby P2.
The observed hit must identify the enemy attacker and P2 recipient. One native
Explosion hit must reduce P2 from 10 to 9 while P1 stays at 10. This establishes
attribution for an area attack, not a target-aimed melee/animation gate.

The guest creates a guarded enemy mirror and blocks native player damage. It
applies a session-tagged host health delta through `DamageLedger`, once per event
id. UDP loopback/session tags are QA isolation, **not cryptographic authentication**.
This is direct health-state replication, not a complete replication of native
damage effects, invincibility, armor, knockback or animation.

## Tests

```sh
python tools/test_damage_ledger.py
python tools/damage_test.py --suite
python tools/verify.py --integration
```

The ledger's 16 production checks cover epoch, id, target and amount validation,
duplicates and rejected attempts not spending an event id.

The real two-client suite passed all three gates:

- Positive: host one pulse/one hit on P2; guest receives three copies, applies
  once and rejects two duplicates; both have P1=10/P2=9.
- Wrong-target mutation: host remains healthy; guest deliberately routes the
  delta to P1, observes P1=9/P2=10 and exits with the expected failure.
- Duplicate mutation: host remains healthy; guest deliberately reapplies
  duplicates, observes three applications/P2=7 and exits with expected failure.

Both clients must match build/session/static-collider metadata/scene/floor.
The mirror must remain quiet with at least four guarded entry probes and no
observed native starts. Timeouts or unrelated failures never count as mutation
successes. Evidence is written under ignored `local/profiles/net-damage-*/`.

## Remaining scope

- Animated moving/attacking enemies, arbitrary subclasses and bosses.
- Broader per-player combat state and effects.
- Full host-authoritative player input (O2).
- General layout/world synchronization, Steam transport and multi-machine QA.

The collider fingerprint compares metadata, not full mesh/world state. Previous
no-pulse attempts failed correctly; intermittent native lifecycle hangs remain
a known risk, not a fixed issue.
