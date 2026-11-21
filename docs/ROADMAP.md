# Going Multiplayer — Roadmap to Steam Co-op

Status: planning document, 2026-11-21. This describes the path from the v0.10.0
research harness to a player-facing Steam co-op mod. It is a plan, not evidence of
implemented functionality. M0a (standalone 480 harness) and M1 (persistent
loopback session core) are implemented and passing their scoped gates; the
two-account harness now passes lobby-id join, invite delivery and P2P. M2
production Steam integration is in progress.

## Milestone status

- **M0a — complete.** Standalone 480 harness passed UDP positive/mutation,
  same-account Steam, and two-account lobby-id join plus invite delivery/P2P
  (`members=2`, ~10–15 ms RTT). The shipping invite-accept callback remains M2.
- **M1 — complete (scoped).** Persistent loopback session core, peer registry,
  interpolation and actor survival across the office-to-dungeon transition;
  positive and ignored-snapshot mutation gates passed. It uses the test-only
  loopback transport, not Steam.
- **M2 — in progress.** `SteamP2PTransport` and `SteamLobbyService` are
  implemented and pass two distinct accounts through the harness; in-game
  host-only lobby creation under AppID 1154810 passes. In-game distinct-account
  session, invite UI and the relay-socket decision remain.
- **M3 — action authority started.** Expanded input command, network-input
  state, command-backed seat input and a per-client camera/HUD view gate pass.
  The host now simulates the guest actor from 30 Hz commands and a session gate
  proves one native Attack plus command-driven movement with two mutation
  twins. The guest is corrected toward the host's authoritative pose
  (deadband + hard snap) with its own gate and twin. Rollback prediction, the
  remaining actions, animation, party HUD and party-tethered room transitions
  remain.

## Definition of done for v1

Two players (later up to four) each launch the modded game through Steam, the host
creates a friends-only co-op lobby and sends a Steam invite, the guest accepts it,
and both play the same run: same dungeon floors, shared enemies, independent health,
inventory and weapons, down/revive and a single party wipe, loot/currency ownership,
and a co-op save that resumes later. Disconnects are handled explicitly. A 30–60
minute session does not desync, hang or duplicate damage/loot.

Explicit non-goals for v1: dedicated servers, host migration, matchmaking with
strangers, PvP, anti-cheat, split-screen dual-camera/dual-HUD support, and
independent room movement. v1 uses party-tethered rooms; see
[the room, camera and HUD policy](ROOM-CAMERA-PLAN.md).

## 1. Baseline: what is actually proven today

| Area | Evidence |
| --- | --- |
| Loader, Proton, Steam auth, isolated installs/saves | 16-stage regression at v0.10.0; v0.13.0 quick 8/8; full 18-stage not rerun |
| Persistent session core (M1) | Two real clients, shared epoch, snapshots, interpolation, actor/health survival across a scene change; mutation control |
| Steam adapter harness (M0a) | UDP positive/mutation; two-account lobby-id join and invite delivery/P2P under AppID 480 |
| Steam transport/lobby (M2, in progress) | Production `SteamP2PTransport`/`SteamLobbyService`/`NetSession` two-account harness pass; in-game host-only real-AppID lobby |
| Player replication foundation (M3) | Expanded action/camera/inventory command, `NetworkInputState`, command-backed input and per-client `view_ok` (local camera/HUD, remote camera off) |
| Two clients on one Steam account, one machine | Coexistence gate |
| 2–5 local actors: data, health, inventory, input, outfits | Seat suite |
| P2 controller routing | Snapshot tests, no physical-press proof |
| Guest movement intent → host native simulation → host snapshots | O2 five-gate suite |
| Session ids, pose replication, stale/epoch/wrong-owner rejection | Loopback suite |
| One enemy target mirror, one native enemy movement mirror | O4a/O4b fixtures |
| Exactly-once damage for one electric pulse | O4c fixture |

Not proven: in-game distinct-account Steam session, production transport under the real AppID,
general actor lifecycle, animation, other player actions, general enemies, floors,
loot, revives, saves, reconnect, 4 players, shipping.

## 2. Steam feasibility (verified against the shipped library)

The game ships `Facepunch.Steamworks.Win64.dll`; its public surface includes:

- `SteamMatchmaking.CreateLobbyAsync(maxMembers)` / `JoinLobbyAsync(lobbyId)`
- `SteamMatchmaking.OnLobbyInvite`, `OnLobbyEntered`, `OnLobbyMemberJoined/Leave`
- `Lobby.InviteFriend(SteamId)`, `SetData`, `SetMemberData`, `SetFriendsOnly`,
  `SetJoinable`, `SetGameServer`
- `SteamFriends.OpenGameInviteOverlay(lobbyId)` (the Steam invite dialog)
- `SteamFriends.OnGameLobbyJoinRequested` (invite accepted / join from friends list)
- `SteamFriends.SetRichPresence` / `GetRichPresence`
- `SteamNetworkingSockets.CreateRelaySocket<T>()` and `ConnectRelay<T>(hostSteamId)`
  with reliable/unreliable `Connection.SendMessage`
- Legacy `SteamNetworking` P2P packets as a fallback

Conclusion: **Steam invite is not just possible, it is directly supported.** The
recommended transport is SteamNetworkingSockets relay sockets (NAT traversal and
relay included). The unknown is Proton/overlay behaviour, which M0 resolves before
UI work. Fallback if the overlay dialog misbehaves: join by pasted lobby id plus
`JoinLobbyAsync`, still with Steam invites where they work.

## 3. Target architecture

```
UI (host/join/invite) ── SteamLobbyService ── Steam callbacks
        │                        │
        └──────── Session ───────┘
                   │
             ITransport
          ┌────────┴────────┐
   LoopbackTransport   SteamRelayTransport
                   │
              NetSession (state machine, tick, peers, epoch)
                   │
   CommandCodec / SnapshotCodec / EventCodec
                   │
   ActorRegistry ── SceneCoordinator ── SaveCoordinator
                   │
   LocalActor | RemotePuppet | LocalCoopSeat
```

Rules:

- Host-authoritative simulation. Clients send intents/commands; the host owns health,
  enemies, physics, loot, currency and progression.
- Reliable channel for events (damage, pickup, scene change, join/leave); unreliable
  channel for poses and input at tick rate.
- Every network object gets a session-scoped id. SteamId maps to player id; clients
  cannot claim another player id.
- Snapshots are delta-compressed and bounded to a bandwidth budget.
- Remote puppets interpolate; the local player predicts and reconciles.
- One code path for loopback and Steam; loopback stays the deterministic CI path.

## 4. Milestones

### M0a — Steam harness spike (size: S) — COMPLETE

Goal: prove the risky Steam path before building on it.

Proven in the standalone 480 harness:

- Create a friends-only lobby and join it by lobby id from a second account.
- Deliver a Steam invite and join it; the harness auto-accepts with
  `OnLobbyInvite` for automation.
- Exchange reliable P2P ping/pong (legacy `SteamNetworking`) and measure RTT.
- Run across a Proton/Wine host and a Windows guest.

Deferred to M2, because they need the in-game UI or a transport decision:

- The shipping `OnGameLobbyJoinRequested` acceptance callback and overlay UI.
- Launch-from-invite (`+connect_lobby`).
- Choosing and validating `SteamNetworkingSockets` relay sockets versus the
  proven legacy P2P path.

Exit gate: two Steam accounts on two machines exchange messages and see the invite
path. This passed for lobby-id join and invite delivery with harness auto-accept
(`OnLobbyInvite`); the shipping `OnGameLobbyJoinRequested` acceptance callback and
overlay remain M2 checks. Evidence is a sanitized local summary plus both traces;
no overlay behavior is claimed.

Risks: same-account testing is not dependable; the harness used a second account.


### M1 — Production session core over loopback (size: L) — COMPLETE

Goal: stop being a probe that quits; become a session that persists.

- Extract `ITransport`; wrap `UdpTestLink` as `LoopbackTransport`.
- Add `NetSession` state machine: Idle → Lobby → Connecting → Handshake → Loading →
  Playing → Ending, with explicit leave/timeout.
- Add `PeerRegistry`, `SessionConfig` (protocol/build/game version, max players),
  session epoch and per-peer sequence state.
- Add `NetworkActor` descriptors and roles: local player, remote puppet, local
  co-op seat. Spawn remote actors from session state, not from a local save.
- Stop destroying extra actors on scene load; re-bind them by network id. Add an
  explicit despawn path for leaving.
- Add interpolation buffer for remote puppets and a basic remote animation state
  (speed, grounded, action id).
- Generalize `InputCommandLedger` and `DamageLedger` from player id 2 to any peer.

Exit gate: an automated long-session gate (2–3 minutes) where host and guest enter
the office, walk a scripted route, change scene once, and both exit cleanly with the
same actor ids; plus a manual unscripted 10-minute office session. Mutation twins:
dropped snapshot, wrong actor id, stale epoch, duplicate spawn.

### M2 — Steam lobby, invites and session join (size: M) — IN PROGRESS

Goal: replace loopback with Steam while keeping the same session core.

Started: `SteamP2PTransport` (legacy P2P, relay allowed), `SteamLobbyService`
(create/join, metadata, rich presence, invite callbacks, `+connect_lobby`) and
`SteamSessionProbe` are implemented. A two-account harness run of the production
classes passed with one shared session, one peer and `state=Playing`. In-game
host-only lobby creation under the real AppID passed. The remaining gates are
the in-game distinct-account session, invite acceptance UI, launch-from-invite,
and the legacy-P2P versus relay-socket decision.

- `SteamLobbyService` — implemented: create/join/leave, friends-only, metadata,
  member callbacks, rich presence, invite overlay, `OnGameLobbyJoinRequested`,
  `OnLobbyInvite` and `+connect_lobby` parsing.
- `SteamP2PTransport` — implemented over the proven legacy P2P API. Relay
  sockets remain an optional upgrade; the two-account harness validates the
  legacy path.
- `NetSession` wiring — implemented; loopback remains for deterministic suites.
- Host/join/invite UI and acceptance callback — F7 `SteamCoop` panel and ESC-menu
  Co-op tab are implemented, including member list, invite and host kick; real
  acceptance/launch-from-invite testing is pending.
- Production `SteamCoop` controller — implemented; minimized real-AppID host
  smoke passes.
- Lobby state/ready/member-data UI — pending.
- Each online player gets their own camera and HUD; v1 deliberately keeps the
  party in one active room (party-wide doors/floors, boss/arena co-location).
- Handle host leaving, guest leaving, lobby full, invite expired, version mismatch
  — partly implemented in the service; full UI/error coverage pending.

Exit gate: two machines over Steam, invite accepted, both reach the office, see each
other move with interpolation, and one leaves cleanly. Evidence includes RTT and a
5-minute no-desync run. This is the first true L3/L4 evidence.

### M3 — Player replication and first playable co-op (size: XL) — FOUNDATION

Goal: two humans can play together, not just stand together. Each client has
its own camera/HUD; rooms stay party-tethered.

Started: expanded `InputCommand` (actions, camera, inventory), `NetworkInputState`,
command-backed `LocalSeats` input, remote actor wiring, and a real-game
`view_ok` check. Action authority and animation remain.

- Host-authoritative actions: attack, alt attack, dodge, block, throw, interact,
  pickup, inventory slot changes, weapon use, revive input. Command plumbing is
  started; native action authority is not yet wired.
- Player state replication: health, armor, weapon/durability, status effects,
  equipment visuals, animation/action state.
- Client prediction and reconciliation for movement; interpolation for everyone
  else; camera-relative input with per-client yaw.
- Remote puppet presentation: animation graph parameters, weapon visuals, footstep
  and hit effects.
- Per-player HUD: health/armor, inventory, currency, connection quality.
- Per-client camera: each client follows its own local actor; remote cameras
  stay disabled. Keep the existing shared-camera local prototype separate.
- Scene loading contract: host drives the load, clients load the same scene, inputs
  frozen until all ready; failure to load is a visible error, not a hang.

Exit gate: two clients complete the office plus one authored dungeon room together
with all actions; mutation twins for omitted action, duplicated event and ignored
reconciliation. This is the **first internally playable co-op build**.

### M4 — General enemy and combat replication (size: XL)

Goal: replace the single electric fixture with the actual game.

- Host-only AI for all enemy types present on a floor, including subclasses that
  start work in `Awake`/`OnEnable`/manager coroutines.
- Enemy registry: spawn, despawn, scene binding, respawn; boss handling deferred to
  M4b if needed.
- Replicate transform, animation, aggro target, health, armor/invincibility,
  knockback, status effects, projectiles, area effects and death.
- Damage event protocol: source, target, amount, type, event id; exactly-once ledger
  generalized to N players; friendly fire off by default.
- Death policy: player death/down is an event, not a local game-over.
- Guest never simulates a live enemy; it only mirrors.

Exit gate: a full floor of native enemies fought by two clients; enemies acquire both
players, damage is applied once on both sides, deaths match, no guest AI runs.
Mutation twins: guest simulates AI, duplicate damage, wrong target, omitted death.

### M5 — Run loop, progression and persistence (size: XL)

Goal: it is a run, not a sandbox.

- Floor/dungeon identity: determine whether generation is deterministic from a seed.
  Resolve the `Environment.TickCount` reseeding in `GameManager.Start` and
  `CameraController.ShakeRoutine`. If not deterministic, host sends a layout
  manifest (rooms, special floors, seeds) and clients build from it.
- Floor transitions: all-ready barrier, epoch bump, stale-scene rejection, loading UI.
- Party-tethered door/floor transitions: either player can initiate, but both
  players move through together; block or leash the other player rather than
  splitting the room context.
- Loot and currency: host-arbitrated pickups, one owner per item, atomic purchases,
  physical item ownership; shared vs per-player currency decided in §7.
- Down/revive: downed state, revive interaction and timing, interruption rules;
  one party wipe; spectator state; run summary.
- Co-op save profile: separate slot, host writes, clients never write single-player
  saves; recovery/backup; resume a run.
- Late join and reconnect: join-in-progress snapshot, identity restore, explicit
  timeout; host leaving ends the run (v1).
- Run HUD/UI: floor, currency, party status, downed/revive prompts.

Exit gate: a two-player run from the office through at least two generated floors,
including a death/revive, a wipe, a save/quit/resume, and a mid-run reconnect.
Mutation twins: stale floor packet, duplicate pickup, second wipe, guest save write.

### M6 — Four players, chaos and scale (size: L)

Goal: make the architecture honest and stable at the target party size.

- Remove remaining two-player assumptions; per-peer channels and ledgers.
- Four-player lobby, invite flow and ready check.
- Network simulator: loss, jitter, reorder, duplication; 10–15 minute soak; measure
  bandwidth, CPU and memory; interpolation under loss.
- Reconnect and abandoned-session cleanup; host-leave handling; lobby cleanup.
- Performance pass on enemy counts and effects.
- Field test with the friend; reuse the same gates and scorers.

Exit gate: four clients on the same build complete a scripted run under a chaos
profile; no divergence beyond tolerance; memory flat; no duplicate damage/loot.

### M7 — Shipping (size: M)

- Player-facing install: BepInEx pack or standalone installer that finds the game,
  checks the build/hash, installs, updates and uninstalls; never touches saves.
- In-game host/join/invite menu with clear errors; no debug flags required.
- Version compatibility: detect unsupported game builds and refuse gameplay patches
  with a readable message; publish a support matrix.
- Crash/log bundle, privacy note, and known-issues list.
- Honest QA level, README and coop-anything write-up/PR **only with owner approval**.

### M8 — Independent rooms (Option B, post-v1, size: XL)

See [the room, camera and HUD policy](ROOM-CAMERA-PLAN.md). This is not a v1
gate: it requires per-player room identity, room occupancy/refcounts,
per-player door transitions, multi-room host simulation, and local-room
audio/lighting/minimap. Boss/arena rooms stay party-locked.

## 5. Cross-cutting workstreams

### Testing ladder (keep it)

- L0 unit: codecs, ledgers, snapshot apply, interpolation, score logic.
- L1 one process: actors, actions, enemies, downs, loot.
- L2 two processes over loopback: session, authority, scenes, chaos simulator.
- L3 two machines over Steam: lobby/invite/transport; manual or two-account.
- L4 friend field test: same gates and trace bundle.

Every gate keeps a positive run and a mutation twin that must fail. Loopback remains
the deterministic CI path; Steam is an adapter, not a second correctness model.
Add long-session and leak checks early; do not let the suite become “one scripted
minute” only.

### Protocol and security

- Session epoch, peer id from SteamId, monotonically increasing input sequence,
  bounded message sizes, rate limits, malformed-packet rejection.
- Host validates all state-changing events; clients never award damage, loot or
  currency. Join approval by host; friends-only lobby by default.
- No authentication bypass, no anti-cheat interaction, no OS input injection.

### Compatibility and save safety

- Pin the game build; detect changes and refuse gameplay patches.
- Separate co-op profile/save; hash-check the original install before/after tests.
- Backup before any save write; recovery path if a co-op save is corrupt.

### Observability

- Session id, peer ids, RTT, packet loss, snapshot/command rates, desync counters.
- In-game network overlay (F8 or similar) for field tests.
- Per-run evidence folders retained for every gate.

## 6. Ranked risks and unknowns

1. **Floor generation determinism.** The largest unknown. If layouts cannot be
   reproduced from a seed, M5 grows substantially and needs a layout manifest.
2. **Proton + Steam overlay invites.** Unverified until M0; fallback is lobby-id
   join, but the invite experience must be proven, not assumed.
3. **Remote player feel.** Animation, prediction and interpolation are not “polish”
   here; without them the mod is not playable. Budget for it in M3.
4. **General enemy coverage.** The game has many subclasses and manager coroutines;
   the current mirror is one prefab. M4 is likely the broadest reverse-engineering
   phase remaining.
5. **Native lifecycle hangs.** Intermittent two-client startup/shutdown hangs are
   still unexplained. They must be fixed or bounded before a player-facing build.
6. **Second account/machine availability.** Steam P2P cannot be validated on one
   same-account install; M0/M2 depend on this resource.
7. **4-player performance.** Unmeasured; only two rendered clients have coexisted.
8. **Game updates.** Patches are hash-pinned; every update is a compatibility event.

## 7. Decisions needed (recommended defaults)

1. **Host model:** listen server, host is a player. No dedicated server in v1.
2. **Party size:** build protocol for N, validate 2 first, ship 4 at M6.
3. **Join UX:** Steam invite primary; friends-only lobby; lobby-id fallback.
4. **Currency:** shared run currency in v1 (simpler, matches the roguelike loop);
   per-player permanent progression deferred.
5. **Guest progression:** guests keep no permanent progression in v1; cosmetics may
   carry over later. Host writes the co-op save.
6. **Host leaving:** ends the run in v1; no host migration.
7. **Friendly fire:** off by default; revisit only after combat is stable.
8. **Distribution:** BepInEx pack first, installer later; version-locked to a tested
   game build.

## 8. Immediate next tasks (M2)

The M0a harness and M1 session-core tasks are complete. Remaining:

1. Decide the transport: validate `SteamNetworkingSockets` relay sockets in the
   harness, or keep the proven legacy P2P path for M2.
2. ~~Add `SteamLobbyService` and the Steam `ITransport`; wire `NetSession`.~~
   Implemented and passing the two-account harness.
3. Add player-facing Host/Join/Invite UI and the actual acceptance callback.
4. Run the in-game distinct-account M0c/M2 checklist with a second owned copy.
5. Run the in-game launch-from-invite (`+connect_lobby`) check.
6. Add the loopback chaos simulator and loss/jitter profiles before M5.
7. Keep the M1/M2 scorers and mutation twins as the milestone gates.

## 9. What “working” will look like at each marker

- **M1:** two scripted clients share a persistent office session; nobody claims
  multiplayer yet.
- **M2:** two Steam accounts join through an invite; they can walk around together.
- **M3:** two humans can fight and act together in one room — first playable build.
- **M4:** a real floor of enemies is shared correctly.
- **M5:** a complete run can be started, lost, revived, saved and resumed.
- **M6:** four players survive chaos/soak.
- **M7:** a player can install it and invite a friend without developer flags.
