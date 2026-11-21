# Design proposal

## Confirmed requirements

- Going Under co-op, with normal multiplayer roguelike mechanics.
- Four or more players supported by design; 2–4 recommended.
- Online co-op is the primary goal. Local split-screen with multiple controllers
  is a secondary goal, not a prerequisite for the first online release.
- Each online player has their own camera and HUD.
- v1 keeps the party in one active room. Players move independently inside it;
  door and floor transitions are party-wide. Independent room movement is a
  later phase, not a v1 requirement.
- Split-screen with two cameras/HUDs on one PC is out of scope.
- Original game install and saves must remain unchanged during development.

## Proposed defaults — not yet implemented

- Host owns dungeon generation, enemies, bosses, physics-object ownership,
  damage, pickup arbitration, currency transactions and progression.
- Clients send input/actions; they cannot directly award damage or loot.
- Each player has independent health, weapons, durability and skills.
- Friendly fire off by default.
- Shared run currency; purchases are host-validated atomic transactions.
  Physical weapons and healing are single-consumer pickups.
- Zero health downs a player. A living teammate can revive them; revive timing,
  health restored and interruption rules need tuning.
- Full-party incapacitation ends the run once, rather than triggering a solo
  game-over for each downed player.
- Disconnection is not death. Reserve the player's identity for reconnection;
  define the timeout explicitly before implementation.
- Floors change together through an all-ready barrier. Reject packets from old
  floor/session epochs.
- Join in the lobby first; mid-run joining is deferred until snapshot and
  reconnection correctness are proven.
- Scale combat for the active party at room entry; tune with bot measurements,
  not an untested assertion that four-player combat is balanced.
- Keep co-op progress separate from single-player. Guest permanent progression
  and host-disconnect behavior need explicit policies.
- No arbitrary hard-coded four-seat arrays. Advertise a configurable session
  capacity, but do not claim an untested maximum.

## First implementation gates

1. Identify exact game build and map player/input, health, enemy targeting,
   pickups, generation, scene changes, save writes and Steam initialization.
2. Prove a mod loader starts in an isolated profile without writing real saves.
3. Spawn a second independently controllable character; demonstrate animation,
   collisions, enemy targeting, damage and independent inventory.
4. Prove host/client movement and floor identity in two real game processes.
5. Synchronize combat, pickups, deaths, revives and transitions.
6. Run two-, four- and greater-than-four-player bot scenarios; verify unique
   pickups, no duplicate damage, wipe once, reconnect identity and stale-packet
   rejection under lag/loss.

Unit/fake-world tests are necessary but do not establish in-game co-op.
Each gate needs recorded evidence and a negative control that can fail.

## Unknowns

- Whether local split-screen seats can also join online sessions; split-screen
  itself is out of scope for v1.
- Independent room movement (Option B) needs room occupancy, per-player room
  identity and multi-room simulation; it is planned after the party-tethered
  online path works.
- Exact game hooks and whether any useful multiplayer logic already ships.
- Mod-loader compatibility with this build under Wine/Proton.
- Safe save isolation, including PlayerPrefs, Steam Cloud and achievements.
- Transport choice and Steam lobby/relay compatibility.
- Performance and recommended maximum above four players.
