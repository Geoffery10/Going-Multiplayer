# Going Multiplayer

Co-op mod research and development for Going Under.

**Status (2026-11-21): experimental local actors, two-client loopback
networking, enemy targeting, native enemy-movement mirroring, scoped
electric-attack health replication, host-authoritative guest movement input,
a standalone Steam-adapter harness, a persistent session core and the first
Steam transport/lobby integration (v0.12.0, M2 in progress). Not a playable
online co-op release.**

## Project status

| Area | Verified progress |
| --- | --- |
| Loader and isolation | BepInEx loads under Proton with normal Steam authentication; tests use private installs, profiles and saves. Original 207 game files hashed unchanged in recorded integrity checks. |
| Local actors | Two- and five-character gates passed: independent data, health, inventory and input; native pickup and movement; distinct outfits. Five local actors do not prove five networked players. |
| P2 controller | Exclusive Rewired gamepad assignment and restoration passed with two detected compatible pads; 27 production input-mapping checks passed. Physical button presses were not tested. |
| Local enemy targeting | Dungeon-entry and targeting gates passed: enemies can acquire P2 and reacquire the nearer actor. Native aggro sticks to an existing valid target; a co-op retargeting policy is still undecided. |
| Two real clients | Two isolated copies reached Title concurrently on one Steam account with valid authentication. |
| Test-only networking | UDP loopback session/player ids, pose replication and packet rejection passed between two real clients. Guest movement of 3.066 m matched the host's remote actor; stale, wrong-epoch and host-id-claim packets were rejected. |
| Network enemy target mirror (O4a) | Native host acquisition of P2, a distance-swap control and guest target mirroring passed. Both intentional-failure controls passed: bypassing the guest reacquisition veto or omitting target application produced the expected mismatch. This is one inactive enemy mirror, not general AI or combat replication. |
| Native host enemy movement (O4b) | One authored CubicleGroundBot moved 3.71 m under its native brain, with 102 matching guest pose samples and zero measured freeze drift. Mirror entry guards and omitted-pose/enabled-guest-AI mutation controls passed. This is one fixture, not general enemy or combat support. |
| Combat replication (O4c) | One native electric pulse hit remote P2; the guest applied the health loss once despite duplicate delivery. Wrong-target and duplicate-application mutation controls passed. Stationary fixture with animator held, not general animated combat. See [the combat notes](docs/O4C-COMBAT.md). |
| Host-authoritative movement input (O2) | Guest movement intent is validated on the host, simulated by the native player path and returned as host snapshots. The guest followed host poses with zero measured error. Dropped input, accepted stale input, never-cleared input and ignored-snapshot mutation controls passed. Movement only; attack/dodge/interact/camera, prediction and reconciliation remain. See [the input notes](docs/O2-INPUT.md). |
| Persistent session core (M1) | Production transport seam, binary session protocol, peer registry and epoch/sequence validation; two real clients share a session, replicate poses, interpolate remote actors and keep an actor's network id and health across the office-to-dungeon transition. Positive and ignored-snapshot mutation gates passed. This uses the test-only loopback transport, not Steam. |
| Steam adapter harness (M0a) | Standalone harness under AppID 480: UDP positive/mutation passed; Steam lobby creation, join and P2P ping/pong passed both on one account and between two accounts (`members=2`, ~10–15 ms RTT) across a Proton/Wine host and a Windows guest. Lobby-id join and invite delivery with harness auto-accept passed. The real in-game invite-accept callback/UI remains an M2 check; no in-game Steam session is claimed. |
| Steam transport/lobby (M2, in progress) | `SteamP2PTransport` and `SteamLobbyService` implement a production-shaped Steam path over legacy P2P. Two distinct accounts passed through the harness `--transport m2` mode with one shared session, one peer and `state=Playing`. In-game host-only lobby creation under AppID 1154810 passed. Full in-game distinct-account session, invite acceptance UI and relay-socket choice remain. See [M2 notes](docs/M2-STEAM.md). |

The loopback suite passed its positive gate and three intentional-failure controls
(disabled pose application, accepted stale state and accepted host-id claims).
The O2 gate additionally shows host-simulated guest **movement** input, but the
remaining actions are still local. These checks **do not establish full
host-authoritative gameplay**, prediction, lag tolerance or a production session.

### Verification scope

- **74 Python tooling/scorer tests**, **27 production input-mapping checks**,
  **23 session-core checks**, **18 production damage-ledger checks** and
  **22 production input-command checks** pass.
- **v0.12.0 quick regression passed 8/8 stages** (offline, build, loader, M1
  positive) with DLL SHA-256
  `3d8959087038b35a6dc4cd8af1638ec3837862afeba80c649eb9b24d29993b50`.
- **M1 session suite passed on v0.12.0**: positive host/guest session with
  shared epoch, ready/done barriers, snapshots and scene actor survival; the
  ignored-snapshot mutation made only the intended guest fail. Evidence:
  `local/session-suite-summary.json`.
- **M2 steam path evidence**: two distinct accounts passed the harness
  `--transport m2` gate with one session, one peer and `state=Playing`
  (`local/m2-harness-summary.json`). In-game host-only lobby creation under
  AppID 1154810 passed (`local/steam-host-summary.json`,
  `python tools/steam_host_test.py`). The in-game distinct-account session is
  not yet validated.
- The **full 18-stage regression has not been rerun** since M1 was added. The
  last full 16-stage pass was v0.10.0; earlier lifecycle timeouts remain
  recorded failures.
- M0a evidence: UDP positive/mutation passed; two-account Steam lobby-id join
  and invite delivery/auto-accept join passed with `members=2` and ~10–15 ms
  RTT between a Proton/Wine host and a Windows guest. The shipping
  invite-accept callback and real-AppID session remain M2 work.
- The suite is long. Use the tiers below for routine work and reserve the full
  run for milestones; a quick pass is not a full regression.
- Earlier attempts hit lifecycle timeouts. Those remain recorded failures;
  the final pass does not establish that their unknown root cause is fixed.
- Physical controllers are **not required**. Tests use scripted in-game
  snapshots; hardware counts are informational. The final run detected no pads.

### Remaining work

- Investigate intermittent native startup/shutdown hangs in the two-client harness;
  a PASS trace does not override a failed or timed-out process exit.
- Extend combat beyond the stationary electric fixture: animated attacks,
  armor, invincibility, knockback and other damage effects.
- Generalize host-only enemy AI beyond the tested fixture.
- Remaining host-authoritative actions (attack, dodge, interact, camera,
  inventory), client prediction/reconciliation and per-client camera mapping.
- Host-authoritative combat effects, armor/invincibility, inventory, loot and
  currency.
- Down/revive, party wipes, shared dungeon/floor identity and save persistence.
- Remote animation, per-player HUD and camera work; local split-screen and
  controller assignment for four or more seats.
- Finish M2: in-game distinct-account session, invite acceptance UI,
  `+connect_lobby` launch, and the legacy-P2P versus relay-socket decision.
  The transport/lobby classes and two-account harness have passed.
- Disconnect/reconnect, packet loss/jitter/reordering and soak tests.
- Steam lobby/invite UI, multi-machine validation and networked 4+ player scale.

**Next milestone:** finish M2 with the in-game distinct-account M0c/M2 play
test and player-facing Host/Join/Invite UI. Continue the remaining O2 actions,
prediction and broader combat afterward.

## Target

- Support parties of four or more; recommend 2–4 players.
- Shared dungeon, enemies, bosses, loot and floor progression.
- Independent player input, health, weapons, durability and skills.
- Downed/revive system and full-party-wipe run failure.
- Host-authoritative world simulation and interaction validation.
- Protect original single-player saves through separate co-op profiles.

These are planned features, not verified functionality. Online co-op is the
primary goal; local split-screen with multiple controllers is a secondary goal.

See [the design](docs/DESIGN.md) for proposed rules and verification gates,
[the Steam co-op roadmap](docs/ROADMAP.md) for the milestone plan, and
[the online testing strategy](docs/ONLINE-QA.md) for the tier ladder and gates.
Private game copies, dumps, logs and machine-specific notes belong in `local/`
and must never be distributed.

## Development milestone

`artifacts/GoingMultiplayer.dll` is an experimental BepInEx plugin, not a co-op
release. Its default mode is diagnostic. The opt-in local prototype adds a second
character with independent data, health, input and inventory, using an alternate
authored outfit palette on private Costume/Material instances.
The networking probe is separately opt-in via `-gmp-net-probe` and binds only to
loopback; it is not a player-facing host/join mode.
Plugin loading and Steam initialization passed under the installed Proton
runtime. System Wine hit a cryptography error; Proton resolved it.

```sh
python tools/build_plugin.py --deploy-test
python -m unittest discover -s tests -v
python tools/smoke_test.py --suite
```

The build targets the isolated game's shipped framework and deploys only into
`local/test-install`. Do not copy game DLLs into a release.
See [toolchain setup](docs/TOOLCHAIN.md) and [integration findings](docs/RESEARCH.md).
The smoke suite runs the real game and verifies startup, Steam authentication,
hook discovery and clean exit. Its intentionally broken hook gate must fail.
**This does not test multiple players, networking or co-op gameplay.**

## Real-game test suites

Automated probes now skip the game's timed logo/stinger presentation and stop
title-screen videos automatically. The existing gameplay probes also bypass the
story intro in isolated saves. Ordinary play is unchanged; engine loading and
Steam authentication still run normally. Automated tests keep running without
window focus. The combat fixture clears entrance dialogue on both clients.

The startup control suite checks the shortcut against unskipped native startup:

```sh
python tools/startup_test.py --suite
```

The last measured pair reached Title **1.49 s vs 11.64 s** after stinger startup
(about **10 seconds saved**). This is presentation timing, not total process
startup time. `-gmp-normal-startup` restores normal presentation in a test launch.

```sh
python tools/seat_test.py --windowed --suite
python tools/target_test.py --suite
python tools/controller_test.py --suite
python tools/net_test.py --suite
python tools/net_ai_test.py --suite
python tools/net_ai_test.py --brain --suite
python tools/damage_test.py --suite
python tools/input_test.py --suite
python tools/session_test.py --suite
python tools/steam_host_test.py
python tools/test_harness.py
python tools/harness_steam_test.py
```

The bot loads the isolated office save, spawns additional characters, checks
separate health/data/inventory/input, performs a native item pickup, drives native
movement and captures the game's framebuffer. The suite also checks an
intentionally broken health accessor and five-character spawning.
The targeting suite enters a dungeon through the game's own entrance trigger and
checks that a second actor is a valid enemy target and that the nearer actor is
selected. It also records the game's native target stickiness.
The loopback suite runs two real clients, assigns a session/epoch and player
ids, rejects stale, wrong-epoch and host-id-claim packets, and replicates the
guest's movement to the host. Its stale, authority and remote-apply mutations
must each fail.
The O4a suite tests native host enemy acquisition and a guarded guest target
mirror; see [its narrow scope and controls](docs/NETWORK-AI.md).
The `--brain` suite adds spawn-time entry guards, a collision-fixture fingerprint,
native host movement, per-sequence pose agreement and a two-second frozen-mirror gate.
The damage suite checks a native electric pulse and exactly-once guest health
replication, plus wrong-target and duplicate-application controls.
The input suite sends guest movement intent to the host, lets the native player
path simulate P2, and checks that the guest follows host snapshots. Its dropped,
stale, timeout and ignored-snapshot mutations must each fail.
The session suite uses the production session core over the loopback transport:
two clients share a session and epoch, replicate poses, interpolate remote
actors, and keep actor identity/health across the office-to-dungeon transition.
Its ignored-snapshot mutation must fail only the intended client.
`tools/test_harness.py` builds and runs the standalone 480 harness over UDP; the
same-account Steam lobby/join/P2P smoke is `tools/harness_steam_test.py`.
`tools/steam_host_test.py` verifies in-game host-only lobby creation under the
real AppID. See [the M2 notes](docs/M2-STEAM.md). These are not a playable online
session.
See [prototype details and limitations](docs/LOCAL-PROTOTYPE.md).

### Test tiers

Most edits do not need every real-game gate. Use the smallest tier that covers
the change, and run the full regression before a milestone or commit that claims
a new phase.

```sh
python tools/verify.py                 # offline tests/scorers only; no game launch
python tools/verify.py --quick         # offline + build + loader + input positive gate
python tools/verify.py --quick --focus net-damage
python tools/verify.py --stages steam-host
python tools/verify.py --stages net-input,controllers
python tools/verify.py --list
python tools/verify.py --integration   # full 18-stage milestone regression
```

`--quick` runs one positive gate for the focused area, not its mutation twins
and not the other suites. `--stages` runs exactly the named stages. The full
integration command (build, loader, coexistence, networking, actors,
local/network targeting, native enemy movement, damage, movement input, the
persistent session gate and controllers) uses Steam and visible isolated game
windows:

```sh
python tools/verify.py --integration
```

These integration commands use Steam and visible isolated game windows. Do not
deploy a new DLL while a test is running.

## Two copies at once

```sh
python tools/two_instance_test.py --windowed
```

Starts two isolated clients on the same Steam account and verifies both reach the
title screen concurrently. This is a startup/isolation spike, not networking.

## Try P2 with a controller

```sh
python tools/play_local.py
```

Load into the office, then **F9** spawns P2 and prefers the second detected
gamepad. **F10** changes P2's controller/keyboard assignment; **F8** shows it.
With only one gamepad, use keyboard/mouse for P1. See the
[P2 control layout](docs/LOCAL-PROTOTYPE.md#p2-gamepad-controls).
