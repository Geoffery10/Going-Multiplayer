# Two-player online testing strategy

Status: plan. No online transport, session or replication exists yet.

Scope for now: **one owned game install, one Steam account, two players**.
Four-player scale, Steam lobbies and a second account are deliberately deferred.
The friend-assisted multi-machine test is a late validation step, not a
development dependency.

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
- Two clients only. Do not add a third or fourth player, or capacity claims,
  until the late-development scale phase.
- A test starts only after the two-instance spike has shown both copies can
  authenticate, load and exit concurrently with isolated saves.

## Observability

Every client writes an immediately flushed JSONL trace with:

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
2. **Two-instance spike.** Prove two same-account copies coexist and keep saves
   and processes separate.
3. **L2 harness.** UDP loopback transport, host/guest bot, JSONL traces, scorer,
   O1 and O3.
4. O4, then O5, O6, O7, O8 and O9.

## Late validation with a friend

When the Steam adapter exists, prepare a small field kit:

- the mod build and install instructions;
- a fixed host/guest scenario and run id;
- the same test flags used at L2;
- a trace bundle from both machines.

The friend runs the guest side once, sends the trace bundle, and the existing
scorer evaluates it. A short human checklist covers only things automation
cannot judge: camera, animation, UI and general feel. This keeps the favour
small and the result comparable with L2 evidence.

## Not claimed

Nothing in this plan is evidence of online play. Until L2 gates pass with two
real clients, and L3/L4 pass later, the mod has no verified multiplayer
functionality.
