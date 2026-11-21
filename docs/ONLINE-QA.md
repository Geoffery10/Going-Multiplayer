# Two-player online testing strategy

Status: L1 local-actor gates, L2 loopback session/movement/packet-rejection
gates, the O4 enemy slices and the O2 movement-authority suite have passed. M1
adds a persistent loopback session core with interpolation and actor survival
across a scene change. M0a adds a standalone 480 harness that passed UDP,
same-account and two-account lobby/invite/P2P checks. This is a development
strategy, not evidence of playable online co-op; the in-game Steam path and
shared gameplay remain unfinished.

Scope for now: **one owned install and account for gameplay development, plus a
second account for the 480 harness**. Real-AppID in-game validation (M0c/M2)
needs a second owned copy. Four-player scale remains deferred. The
friend-assisted multi-machine test is still a late validation step.

## Why this order

A single-player mod milestone can be tested in one process. Online co-op cannot:
the thing being tested is agreement between two real game clients. The first
automated online gate must therefore start two copies, give them the same
scenario and compare their own logs. Until that exists, "works online" is an
unverified claim.

Two full game copies are produced from the existing isolated test install; that
does not require a second license. Each copy gets its own Proton prefix, home,
save folder and logs. The owner's install and saves are never mounted writable.

## Test tiers

| Tier | What it proves | Runs |
| --- | --- | --- |
| L0 | Protocol, codec, ownership/epochs, snapshot and scorer logic, with a fake world | Unit tests, seconds |
| L1 | One process, two or more actors: identity, input, health, inventory, **enemy targeting**, local combat | One game window, under a minute |
| L2 | Two real processes over a loopback test transport: session, replication, host authority | Two game windows, one to two minutes |
| L3 | Steam transport adapter with the same two clients | Later |
| L4 | Friend on another machine using the same instrumented build | Late validation |

L2 is the primary automated tier. L3 must reuse the L2 suite rather than
redefine correctness. L4 is a field test of the same gates on separate hardware.

## Two-client constraints

- Both clients run on this machine, under the same Steam session and account.
- Networking uses a **UDP loopback test transport** behind the production
  transport interface. Same-account Steam P2P is not a dependable test and is
  not required until the Steam adapter is written.
- Rendered windows are required: this build exits during batch-mode GUI
  initialization. Resource use is measured before claiming any larger scale.
- Automated probes skip timed startup logos and title videos; engine and Steam
  initialization still execute. `tools/startup_test.py --suite` compares the
  shortcut with an unskipped control. Ordinary play is not accelerated.
- Two clients only. Do not add a third or fourth player, or capacity claims,
  until the late-development scale phase.
- The two-instance spike passed: two rendered copies on the same Steam account
  reached the title screen concurrently, each from its own install and Proton
  prefix, and exited cleanly. Both traces reported Steam valid, hooks found and
  two scenes loaded.
- Two copies use separate installs, prefixes, homes, logs and save folders.
  The owner's install is hashed unchanged after each run.

## Observability

Every client writes immediately flushed structured trace lines with:

- run id, build hash, process id, role (host/guest), session id and epoch;
- tick and wall-clock time, player id, controlled actor id;
- actor positions, health, inventory ownership;
- enemy id, active state, aggro target id and enemy mode;
- explicit event lines for join, leave, damage, pickup, floor change and error.

The runner treats a missing, duplicate, stale-run-id or wrong-build trace as a
failure. Game framebuffer captures are evidence; desktop capture is not used.

## Bot control

The bot is inside the plugin and inert without a test flag. It:

- loads a fixed save/dungeon and a fixed seed;
- starts as host or guest from the command line;
- waits for the expected player count instead of a fixed delay;
- drives scripted input through the existing seat input path, never physical
  keys, virtual devices or OS input injection;
- exposes test-only commands for states it cannot reach: teleport an actor,
  spawn or freeze an enemy, apply a hit, down/revive, drop an item, advance a
  floor and disconnect a client;
- ends with one result line and a clean exit code.

## Gates

Each gate needs a positive result, a **mutation twin** that must fail, and a
scorer unit test. A gate that prints no result, times out or loses a client is a
failure, not a skip.

| Gate | Oracle | Mutation / control |
| --- | --- | --- |
| O0 harness | Both clients start, authenticate, load the scenario, same build and run id | Missing result, stale run id and wrong build must fail |
| O1 presence | Each client sees two unique players and the same session/epoch | Silent single-player fallback must fail |
| O2 input authority | Guest input moves only the guest actor; host rejects guest state writes | Client-authoritative movement must fail |
| O3 movement | Sampled positions agree within tolerance | Replication disabled must fail |
| O4 AI and targeting | Host is the only AI authority; both clients report the same enemy target; an enemy acquires the nearby guest on its own; damage follows the target | Freeze the host enemy and require the guest not to move it; distance-swap control |
| O5 combat | One hit produces one damage event; health is per player; down/revive; exactly one party wipe | Duplicate-damage switch must fail |
| O6 pickups and currency | One owner per item; atomic host purchase; no duplicate pickup | Deliberate pickup race must fail |
| O7 floors | Same seed and floor; all-ready barrier; stale epoch rejected; late join snapshot | Stale packet accepted must fail |
| O8 liveness | A killed client stops counting as live; reconnect restores identity; timeout explicit | Ghost client counted as joined must fail |
| O9 chaos and soak | Loss, jitter, reorder and duplication profiles; 10–15 minute run; no divergence; memory flat | Injected desync must be caught |

Every gate writes into a per-run evidence folder with both client traces, the
runner summary and any framebuffer image. The original game install is rehashed
before and after.

## Near-term order

1. **L1 targeting gate — implemented.** It places P1 and P2 at prepared
   positions, moves one authored enemy between them and asserts the actual
   target. The gate, its nearer-actor swap and its non-hostile control pass.
   It also confirmed native aggro stickiness: a valid P1 target is not replaced
   just because P2 is closer. This is the O4 oracle.
   Dungeon entry is automated through the game's own office entrance trigger;
   no human has to walk to the elevator.
2. **Two-instance spike — implemented and passed.**
   `tools/two_instance_test.py --windowed` starts two rendered same-account
   clients from separate installs.
   Both reached the title screen (`scenes=2`), reported Steam valid and found
   the required hooks, then exited 0. Prefixes and installs were distinct.
3. **L2 loopback harness — first gates implemented.**
   `tools/net_test.py --suite` starts a host and guest, exchanges a session id,
   assigns ids 1/2, reports presence on both sides, and replicates the guest's
   scripted movement to the host's remote actor. A mutation that skips applying
   remote poses must fail the host.
   Positive run: guest moved 3.07 m over 97 updates, host applied 3.07 m, and
   the final cross-checked positions matched. The host also rejected a stale
   sequence, a wrong epoch and a guest packet claiming host id; each has its own
   mutation that must fail.
   Not yet covered: loss/chaos, scene/floor epochs, remote animation, combat.
4. **O4a targeting mirror — implemented and passed.** The positive gate and
   both mutation controls passed on v0.7.0. See
   [the scoped scenario, results and mutation controls](NETWORK-AI.md).
   The corrected v0.7.0 build also passed the full expanded serial regression;
   earlier lifecycle timeouts remain known flakiness, not silently retried passes.
5. **O4b native host enemy movement — implemented and passed.**
   `tools/net_ai_test.py --brain --suite` adds guarded mirror creation, matching
   collision-fixture metadata and sequenced movement samples for one authored
   CubicleGroundBot. Omitted poses and enabled guest AI each fail as intended.
   All 11 serial regression stages passed on v0.8.0, with no physical controllers.
   Full O4 is not complete.
   Full O2 input authority remains incomplete: rejecting host-id claims is not
   host simulation of guest input. Complete online O4 and O2, then O5–O9.
6. **O4c electric attack/health slice — implemented and passed.**
   `tools/damage_test.py --suite` observes one native host pulse hitting remote
   P2 and exactly-once guest health replication despite duplicate delivery.
   Wrong-target and duplicate-application mutations each produce their specific
   expected failure. Both clients clear entrance dialogue and run unfocused.
   This stationary fixture holds the animator: it does not prove animated or
   general combat. See [scope and controls](O4C-COMBAT.md).
   All 14 serial regression stages passed on v0.9.0 (64 Python tests, 27 input
   checks, 16 ledger checks), including the earlier suites. Broader O4 combat
   and later online gates remain incomplete.
7. **O2 movement authority — implemented and passing its five-gate suite.**
   `tools/input_test.py --suite` sends guest movement intent to the host,
   validates session/epoch/player/sequence/axes, feeds the native player path on
   the host and returns authoritative snapshots. Dropped input, accepted stale
   input, never-cleared input and ignored-snapshot twins each produce their
   specific expected failure. This is movement only; attack/dodge/interact/
   camera, prediction and reconciliation remain local or absent. See
   [scope and limits](O2-INPUT.md). The full 16-stage v0.10.0 regression passed
   afterward. The current stage list is 18 stages: `session-core` and
   `session-loopback` were added for M1, and the full 18-stage run has not been
   rerun as a single pass. The v0.12.0 quick run passed 8/8 including the M1
   positive gate.

8. **M1 persistent session core — implemented and passing.**
   `tools/session_test.py --suite` runs two real clients over the loopback
   transport with the production session seam, binary protocol, peer registry,
   epoch/sequence checks and interpolation. The positive gate keeps actor
   identity and health across office -> dungeon; the ignored-snapshot mutation
   fails only the intended guest. This is not Steam and not a playable session.
9. **M0a Steam harness — implemented and passing.**
   `tools/test_harness.py` covers UDP positive/mutation. The 480 harness passed
   same-account Steam and two-account lobby-id join plus invite delivery/P2P
   (`members=2`, ~10–15 ms RTT) across a Proton/Wine host and a Windows guest.
   The guest auto-accepts with `OnLobbyInvite`; the shipping
   `OnGameLobbyJoinRequested` acceptance callback and real AppID remain M2 work.
10. **M2 Steam transport/lobby — started, not complete.**
    `SteamP2PTransport` and `SteamLobbyService` pass a two-distinct-account
    harness run with one shared session, one peer and `state=Playing`. In-game
    host-only lobby creation under AppID 1154810 passes. The in-game
    distinct-account session, acceptance UI, launch-from-invite and the
    relay-socket decision remain.

## Late validation with a friend

When the in-game Steam adapter exists, prepare a small field kit:

- the mod build and install instructions;
- a fixed host/guest scenario and run id;
- the same test flags used at L2;
- a trace bundle from both machines.

The friend runs the guest side once, sends the trace bundle, and the existing
scorer evaluates it. A short human checklist covers only things automation
cannot judge: camera, animation, UI and general feel. This keeps the favour
small and the result comparable with L2 evidence.

## Not claimed

The recorded L2 gates demonstrate test-only loopback functionality between two
real clients, not internet play or shared dungeon gameplay. The M0a harness
demonstrates a test-only 480 Steam adapter between two accounts, not an in-game
Steam session. M2 has host-only in-game lobby creation and a two-account harness
pass, but no in-game distinct-account session. L3 in-game Steam transport under
AppID 1154810, L4 multi-machine validation and the remaining gameplay gates have
not passed.
