# Going Multiplayer

Co-op mod research and development for Going Under.

**Status: experimental local second-character prototype. Online co-op is not implemented.**

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

## Independent-character tests

```sh
python tools/seat_test.py --windowed --suite
python tools/target_test.py --suite
python tools/net_test.py --suite
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
See [prototype details and limitations](docs/LOCAL-PROTOTYPE.md).

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
