# M0a/M2 standalone Steam/UDP harness

Builds a Windows console harness used to validate the Steam lobby, invite and
P2P adapter without running the game. It is a test tool, not part of the mod.

```sh
python tools/build_harness.py
python tools/test_harness.py
```

Output is local-only: `local/harness/publish/`.

## UDP mode

No Steam account is required. Run a host and guest in separate processes:

```sh
SteamHarness.exe --transport udp --role host --port 39140 --seconds 8
SteamHarness.exe --transport udp --role guest --port 39140 --seconds 8
```

## Steam mode

Uses the running Steam client and Spacewar (AppID 480). `steam_appid.txt` must
contain `480` next to the exe; the build script writes it. Start both sides
before accepting an invite, otherwise Steam may try to launch Spacewar.

```sh
SteamHarness.exe --transport steam --role host --seconds 60 --invite <steamid64>
SteamHarness.exe --transport steam --role guest --lobby <lobby-id> --seconds 40
```

The host prints `GMP_HARNESS_LOBBY <id>`. For automation the guest
auto-accepts the invite via `OnLobbyInvite`; the shipping UI must use
`OnGameLobbyJoinRequested` when a player accepts. A pass requires both sides to report
`GMP_HARNESS_RESULT PASS`, the same session id and successful ping/pong
exchanges. One account can exercise the transport (`--allow-same-account`);
two accounts are required for `members=2` and a real invite/join.

## M2 production transport mode

`--transport m2` links the production `SteamP2PTransport`, `SteamLobbyService`
and `NetSession` sources. It needs two distinct Steam accounts; same-account P2P
is not a valid peer-identity test.

```sh
SteamHarness.exe --transport m2 --role host --seconds 55
SteamHarness.exe --transport m2 --role guest --lobby <lobby-id> --seconds 40
```

A pass requires both sides to report `GMP_M2_RESULT PASS`, the same session id,
`state=Playing`, `peers=1` and at least 10 snapshots received.

This mode is for test accounts only. It must not be used to run the game without
ownership, and it is not a production transport.
