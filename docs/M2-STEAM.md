# M2 Steam transport and lobby

Status: **in progress**, started v0.12.0, current build v0.13.0, 2026-11-21.
This is the first production-shaped Steam path, not a playable online co-op
session.

## Implemented

- `SteamP2PTransport` — an `ITransport` over the shipped legacy
  `ISteamNetworking` P2P API. It enables Steam relay fallback, accepts P2P
  session requests, sends reliable/unreliable packets, and keeps the session
  protocol unchanged.
- `SteamLobbyService` — creates/joins/leaves a friends-only lobby, sets
  build/game/protocol metadata, handles member callbacks, rich presence, invite
  overlay, `OnGameLobbyJoinRequested`, `OnLobbyInvite`, and the
  `+connect_lobby` command line.
- `SteamSessionProbe` — runs the M1 scenario over the Steam transport.
  Supported flags:
  - `-gmp-steam-probe`
  - `-gmp-steam-role=host|guest`
  - `-gmp-steam-lobby=<id>`
  - `-gmp-steam-invite=<steamid64>`
  - `-gmp-steam-host-only`
  - `-gmp-steam-overlay`
- `SessionProbe.Run` now accepts any `ITransport` and reports `mode=loopback`
  or `mode=steam`. It also has a ready barrier so one client cannot start
  measuring before the other has finished loading.
- The standalone harness has a `--transport m2` mode that links the production
  `SteamP2PTransport`, `SteamLobbyService` and `NetSession` sources. It needs no
  game copy.

## Verified

- **Two distinct Steam accounts, harness M2 mode: PASS.**
  Host and guest shared one session, each had one peer, `state=Playing`, and
  exchanged snapshots. Evidence: `local/m2-harness-summary.json`.
- **In-game host-only lobby under the real AppID 1154810: PASS.**
  Steam initialized, a friends-only lobby was created, and the process exited 0.
  Evidence: `local/steam-host-summary.json`; command
  `python tools/steam_host_test.py`.
- **M1 loopback suite on the M2 build: PASS**, including the ignored-snapshot
  mutation. `python tools/verify.py --quick --focus session-loopback` passed 8/8.
- DLL SHA-256:
  `3d8959087038b35a6dc4cd8af1638ec3837862afeba80c649eb9b24d29993b50`.

## Not yet verified

- An in-game session between **two distinct accounts** (host and guest both
  running the game). This is the M0c/M2 field gate and needs a second owned copy
  or the friend.
- In-game invite acceptance through the shipping UI and launch-from-invite.
- A player-facing Host/Join/Invite menu; the first `SteamCoop` F7 panel and an
  ESC-menu Co-op tab exist, with Host, Join clipboard, Invite, Leave, copy-lobby-id
  and host-side kick controls. The list shows lobby members.
- Relay-socket transport. The implemented path is the proven legacy P2P path;
  `SteamNetworkingSockets` remains an optional upgrade.

### Same-account in-game limitation

A two-process game test on one Steam account reaches `state=Playing`, but both
processes share the same SteamID. The P2P peers cannot be distinguished, so
snapshot sender identity collides and the gate fails. That is a test-topology
artifact, not evidence about distinct-account play. Do not use same-account
in-game sessions as an M2 pass.

## Harness commands

Two accounts are required for the meaningful `m2` mode:

```sh
SteamHarness.exe --transport m2 --role host --seconds 55
SteamHarness.exe --transport m2 --role guest --lobby <lobby-id> --seconds 40
```

The host prints `GMP_M2_LOBBY <id>`; the guest joins by that id. A pass requires
both sides to report `GMP_M2_RESULT PASS`, the same session id, `state=Playing`,
`peers=1`, and at least 10 snapshots received.

## Production controller

`SteamCoop` owns a persistent lobby and `NetSession` outside the probes. It is
available from the F7 panel and the ESC-menu Co-op tab in normal play, and has
command-line host mode (`-gmp-coop-host`). The tab shows lobby members and, on
the host, Invite and Kick actions. A minimized host smoke under the real AppID
passes with the tab installed: `local/coop-host-summary.json`,
`python tools/coop_host_test.py`.

Kick is a game-session message: the host tells the guest to leave and removes
its peer. Steam's lobby API does not expose a direct kick operation here, so a
guest that ignores the message remains in the Steam lobby until it leaves.

## Next

1. Run the real-AppID M0c/M2 checklist with a second owned copy/friend.
2. Test the real invite acceptance path and `+connect_lobby` with the F7 panel.
3. Decide whether to keep legacy P2P or validate relay sockets for v1.
4. Wire `steam-host` into the milestone regression after the field gate.
