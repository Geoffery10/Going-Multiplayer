# Going Multiplayer

Co-op mod research and development for Going Under.

**Status (2026-11-20): experimental local actors plus two-client loopback
networking and enemy-target mirror tests (v0.7.0). Not a playable online co-op release.**

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

The loopback suite passed its positive gate and three intentional-failure controls
(disabled pose application, accepted stale state and accepted host-id claims).
These checks **do not establish full host-authoritative gameplay**: the probe
replicates guest poses, not host-simulated guest input.

### Verification scope

- **49 Python tooling/scorer tests** and **27 production input-mapping checks passed**.
- **v0.7.0 full serial regression passed:** loader, coexistence, loopback
  networking, local actors, local targeting, O4a network targeting and controller
  routing, including their intentional-failure controls.
- Earlier attempts hit lifecycle timeouts. Those remain recorded failures;
  the final pass does not establish that their unknown root cause is fixed.
- No physical pads were detected in the final controller run; it covered
  scripted snapshots. The earlier two-device ownership result is historical.

### Remaining work

- Investigate intermittent native startup/shutdown hangs in the two-client harness;
  a PASS trace does not override a failed or timed-out process exit.
- Host-only enemy AI and replicated targeting/damage across clients.
- Host-authoritative input, combat, inventory, loot and currency.
- Down/revive, party wipes, shared dungeon/floor identity and save persistence.
- Remote animation, per-player HUD and camera work; local split-screen and
  controller assignment for four or more seats.
- Disconnect/reconnect, packet loss/jitter/reordering and soak tests.
- Steam transport/lobbies, multi-machine validation and networked 4+ player scale.

**Next milestone:** extend the O4a target mirror to native host enemy-brain
simulation and replicated movement/damage, then network-chaos coverage.
Full input authority (O2) remains incomplete.

## Target

- Support parties of four or more; recommend 2–4 players.
- Shared dungeon, enemies, bosses, loot and floor progression.
- Independent player input, health, weapons, durability and skills.
- Downed/revive system and full-party-wipe run failure.
- Host-authoritative world simulation and interaction validation.
- Protect original single-player saves through separate co-op profiles.

These are planned features, not verified functionality. Online co-op is the
primary goal; local split-screen with multiple controllers is a secondary goal.

See [the design](docs/DESIGN.md) for proposed rules and verification gates, and
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

```sh
python tools/seat_test.py --windowed --suite
python tools/target_test.py --suite
python tools/controller_test.py --suite
python tools/net_test.py --suite
python tools/net_ai_test.py --suite
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
See [prototype details and limitations](docs/LOCAL-PROTOTYPE.md).

To run the expanded regression serially (build, loader, coexistence, networking,
actors, local/network targeting and controllers):

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
