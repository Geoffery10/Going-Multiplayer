# O2 movement authority: guest input, host simulation, guest display

Status: **five-gate suite passed, and the full 16-stage v0.10.0 regression
passed** (DLL SHA-256
`3a165a59acb5e7103af4deb7f4260634e7accea9f02ea5e45b12af4da5edf363`).
This covers movement input only. It is not full O2 and not playable online co-op.

## What is proven

`tools/input_test.py --suite` runs a host and guest on the test-only UDP loopback
transport. Both enter the same authored dungeon room and compare scene, floor,
static-collider metadata and build.

1. The guest sends its movement intent as camera/yaw-relative axes plus a yaw.
2. The host validates the session, epoch, player id, sequence and finite axis
   range with `InputCommandLedger`.
3. The host converts the intent into its own camera frame and feeds the native
   player input path. The game's own `Player.HandleWalkFixed` moves the host-side
   P2. The probe does not set positions while input is active.
4. The host sends authoritative P2 poses at 20 Hz.
5. The guest aligns its local P2 to the first pose, then applies subsequent host
   poses instead of simulating its own authoritative movement.

The positive gate requires more than 20 accepted and moving commands, native
host displacement over 0.8 m, the guest following more than 20 snapshots with
zero measured pose error, a matching scene/floor/fixture, two actors, rejected
malformed and wrong-player commands, a rejected stale command, and an input
timeout that clears movement after native deceleration.

## Failure twins

| Mutation | Observed mismatch |
| --- | --- |
| `drop` | Host applies no guest commands and the guest receives stationary snapshots. |
| `stale` | Host accepts an old sequence and moves backward along the chosen path. |
| `timeout` | Host never clears the last command; the guest keeps receiving movement. |
| `snapshot` | Guest ignores host poses, moves locally in the opposite direction and diverges. |

Each mutation must make the intended client exit 1 with its own fields; a
timeout or unrelated failure is not accepted. The other client must remain
healthy. All four controls passed in the five-gate suite.

## Limits

- Movement only: no attack, dodge, interact, inventory, camera or weapon input
  authority yet. Those actions are still local.
- No client prediction or reconciliation. The guest is a direct display of host
  snapshots, which is acceptable for a gate but not for a responsive player.
- The bot supplies a yaw with its axes. A production client would supply its
  camera-relative input; per-client camera mapping and server reconciliation are
  open work.
- One guest and one host actor; two-player only. Four-player scale is untested.
- UDP loopback and session tags are QA isolation, **not** authentication, a
  production transport, lag compensation, loss handling or a Steam adapter.
- The test disables the idle second actor during the gate so its native
  `FixedUpdate` cannot re-enable colliders and shove the simulated actor. That is
  a fixture rule, not a gameplay feature.
- Geometry comparison is static collider metadata, not full mesh/world identity.

## Commands

```sh
python tools/test_input_ledger.py
python tools/input_test.py --suite
python tools/verify.py --quick --focus net-input   # routine positive check
python tools/verify.py --integration               # milestone regression
```

`InputCommandLedger` passes 22 production checks without the game.
Private evidence is written under `local/profiles/net-input-*/`.
