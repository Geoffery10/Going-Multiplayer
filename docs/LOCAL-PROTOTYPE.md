# Independent-character prototype

This is a local prototype plus a two-process **test-only loopback** milestone,
not an online/co-op release or split-screen implementation. The loopback
transport exists only inside test probes and is not a production session.

## Verified build

Version **0.5.0**, Windows Steam build **20206372**, Unity **2018.4.24f1**:

| Check | Result |
| --- | --- |
| Tooling/scorer/isolation tests | 33 passed |
| Production pad-frame mapping | 27 checks passed |
| Authenticated loader and broken-hook control | Both behaved as expected |
| Two same-account clients concurrently | Both reached Title, Steam valid, hooks found, exit 0 |
| Two-client loopback presence/movement | PASS; guest moved 3.07 m, host applied it, positions matched |
| Loopback remote-apply mutation | Expected FAIL; host applied 0 m |
| Two independent characters | PASS |
| Intentionally broken health isolation | Expected FAIL; other checks stayed true |
| Five independent characters with alternate outfits | PASS |
| Native inventory exception context restoration | PASS |
| Enemy targeting for the second actor | PASS; nearer-actor swap and exclusion controls PASS |
| Intentionally non-hostile second actor | Expected FAIL; no second-actor target |
| Controller routing and intentionally shared-input control | Both behaved as expected |
| Detected controller ownership, neutral polling and restoration | PASS with two compatible gamepads |
| Original game file integrity | 207 files checked, zero changes |

The gates listed were run individually after this change; a full
`python tools/verify.py --integration` rerun is pending. These results establish
only the specific local-state, pickup, movement and targeting checks below, not
online or complete roguelike mechanics.
Controller action tests used deterministic snapshots; physical button presses
and full controller combat have not been verified.

## What it implements

- Additional Jackie actors survive the game's duplicate-player rejection without
  replacing player one.
- Each extra actor gets its own data holder, health/armor values, inventory and
  concrete Rewired input adapter.
- Native inventory operations select the correct actor, including nested calls.
  Singleton context is restored even if the original operation throws.
- Extra actors use cloned authored Costume/Material instances. Both the material
  and palette-bearing mesh are selected, since a material-only change retained
  the original visible colors.
- P2 uses the Joblin outfit palette (purple/black rather than the default pink
  trousers); further seats cycle through available alternate authored palettes.
  This does not unlock outfits in the save or alter shared game assets.
- Teammates are marked non-grabbable so the game's item selector cannot offer
  to pick up another Jackie as a weapon.
- Gameplay patches refuse an unknown Assembly-CSharp SHA-256.
- Prototype characters are cleared on scene changes; this is not persistence.

## Automated gate

```sh
python tools/build_plugin.py --deploy-test
python tools/two_instance_test.py --windowed
python tools/net_test.py --suite
python tools/seat_test.py --windowed --suite
python tools/target_test.py --suite
python -m unittest discover -s tests -v
```

The seat suite uses the real game with a separate Proton prefix/save and
suppresses achievement/stat mutations, without bypassing Steam initialization.
It checks:

1. Correct actor count and preservation of the original player singleton.
2. Distinct data, inventory and input identities, including between extra seats.
3. Health-state writes on P2 do not change P1.
4. A native pickup is held by P2 and absent from P1's inventory.
5. Native inventory exceptions restore the player context.
6. Separate inputs and actual horizontal movement through the game's physics.
7. An active animated actor and alternate material/texture, with P1's material
   and costume unchanged.
8. A unique run result, a clean expected exit and an intentional health failure.

### Two-client coexistence

`python tools/two_instance_test.py --windowed` creates two private install copies
under `local/test-installs/`, gives each its own Proton prefix/home and launches
both on the same Steam account with rendered windows. Each client must report a
valid Steam client, the required hooks, and at least the title screen
(`scenes=2`), then exit 0. The runner records distinct installs and prefixes.
This proves process/prefix/save coexistence only, not networking.

### Two-client loopback gate

`python tools/net_test.py --suite` starts a host and a guest from the two
private installs and exchanges presence and player poses over loopback UDP
through a test-only transport. Each side logs a session id, player ids, update
count and final local/remote positions. The guest drives scripted movement; the
host applies the received pose to its remote actor. The scorer cross-checks both
traces and requires the mutation run (host skips applying poses) to fail.

This is not the production transport, lobby or gameplay sync. It records the
first verified two-process replication: presence plus position.

### Enemy-targeting gate

`python tools/target_test.py --suite` loads the office, then enters a dungeon
through the game's own entrance trigger instead of a human walking to the
elevator. It keeps P1 and P2 at prepared positions and moves one authored enemy
between them:

1. P2 is a valid target: it is in `Character.characters`, shares the enemy's
   room, is hostile, alive and active.
2. An enemy activated near P2 selects P2 by itself.
3. Moving the enemy next to P1 makes the same enemy select P1.
4. Making P2 non-hostile removes it from consideration; the gate must fail for
   that reason.
5. A valid P1 target is not replaced merely because P2 is closer; forcing
   reacquisition then selects P2.

The last point is native aggro stickiness, not necessarily a co-op bug. It is
recorded because it matches the observed "enemies ignore P2" symptom and will
need an explicit co-op policy decision.

Logs: `local/profiles/target-p2*/logs/`. Summary:
`local/target-suite-summary.json`.

### Automated dungeon entry

`DungeonEntry` finds the office's real `DungeonTrigger` instances, rejects the
legacy `GeneratedDungeon` placeholder that is not in this build, and enters a
loadable authored scene through the game's own `Enter()` path. This removes the
manual "walk to the elevator" step from automated gates.

Logs: `local/profiles/seats-*/logs/`. Summary: `local/seat-suite-summary.json`.
Screenshots are captured by the game, not from the desktop.
An inspected [five-character game capture](../local/profiles/seats-5/logs/palette.png)
is retained locally; game captures/assets are not part of a distributable package.

Rendered/windowed mode is currently required for this gate. Batch mode exits
during fresh-profile title initialization after a GUI null-reference error;
a startup-only smoke pass does not establish scene-transition support.

## Optional preview

```sh
python tools/play_local.py
```

This opens only the isolated copy with separate saves and a one-hour safety
limit (`--minutes 30` changes it). Close the game window to finish. A fresh
Proton profile can take longer to start than subsequent launches. Development
encountered a cold-profile startup timeout that succeeded after relaunching;
this remains a known startup limitation.

After loading into the office, press **F9** to spawn P2. Two recognized gamepads:
P2 prefers the second; P1 keeps the first plus keyboard/mouse. With one gamepad,
P2 takes it and P1 uses keyboard/mouse. **F10** cycles the P2 device and keyboard
fallback; **F8** shows the selected device.

The selected pad is removed from the existing Rewired players' assignments, so
it does not simultaneously drive P1. Assignment changes are in memory only and
previous ownership is restored on release/scene cleanup. Disconnecting P2's pad
stops P2 input; reconnect and use F10 to explicitly choose it again.

### P2 gamepad controls

Uses standard Xbox button **positions** (PlayStation labels shown alongside):

| Control | Action |
| --- | --- |
| Left stick | Move |
| X / Square | Attack |
| Y / Triangle | Interact/pick up |
| A / Cross | Dodge |
| B / Circle | Throw |
| RT / R2 | Block/alternate attack |
| LT / L2 | Focus |
| LB/RB / L1/R1 | Previous/next inventory slot |
| Right stick | Focus selection |

P2 uses this fixed prototype layout, not custom P1 remappings. A controller must
be recognized by the game's Rewired gamepad-template support. There is still
one shared P1 camera, not split-screen; stay nearby while testing.

### Keyboard fallback

| Control | Action |
| --- | --- |
| F8 | Diagnostic panel |
| F9 | Spawn P2 |
| F10 | Cycle P2 controller/keyboard |
| I/J/K/L | P2 movement |
| U | P2 attack |
| O | P2 interact |
| Right Shift | P2 dodge |
| P | P2 throw |
| H / N | P2 inventory cycling |

Manual play is optional, not the QA plan. The controller probe uses deterministic
in-game pad snapshots to exercise native input routing, movement, button edges
and disconnect neutralization. Detected hardware can additionally be checked for
exclusive assignment/restoration. These are not physical button-press tests:

```sh
python tools/test_pad.py
python tools/controller_test.py --suite
```

## Not implemented or not established

- Production online transport, Steam lobbies/invites or multi-machine
  synchronization. The loopback transport is test-only.
- Assignment for more than the prototype P2 seat, or separate cameras/split-screen.
- Physical button-press automation or a verified full controller combat run.
- Full combat correctness, friendly-fire rules, downed/revive and party wipes.
- Per-player HUD, skill effects, currency rules or dialogue ownership.
- Inventory/save persistence or synchronized floor transitions.
- A stable balanced five-player run. Spawning five actors is not such a run.
- Clean startup with zero exceptions: fresh-profile GUI/settings warnings remain
  logged. Their stacks originate in game code, but a full unmodded-title baseline
  has not established whether all are vanilla. They must not be confused with
  proof of multiplayer correctness.
