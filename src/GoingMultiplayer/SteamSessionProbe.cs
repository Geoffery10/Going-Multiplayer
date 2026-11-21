using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using Steamworks;
using UnityEngine;

namespace GoingMultiplayer
{
    // M2 gate: the same M1 scenario over the Steam lobby/P2P transport.
    // Host-only mode is available for a single-account lobby smoke.
    internal static class SteamSessionProbe
    {
        internal static IEnumerator Run(Plugin plugin, string runId, string role,
            ulong lobbyId, ulong inviteId, bool hostOnly)
        {
            bool isHost = role == "host";
            string build;
            using (var hash = SHA256.Create())
                build = BitConverter.ToString(hash.ComputeHash(
                    File.ReadAllBytes(typeof(Plugin).Assembly.Location)))
                    .Replace("-", "").ToLowerInvariant();
            string gameVersion = Application.unityVersion;
            var service = new SteamLobbyService();
            service.AutoAcceptInvites = !isHost;

            float steamDeadline = Time.realtimeSinceStartup + 60f;
            while (!SteamClient.IsValid && Time.realtimeSinceStartup < steamDeadline)
                yield return null;
            if (!SteamClient.IsValid)
            {
                plugin.Report("GMP_STEAM_RESULT FAIL run=" + runId + " role=" + role
                    + " reason=steam-not-ready mode=steam");
                Application.Quit(1);
                yield break;
            }
            service.Install();
            plugin.Report("GMP_STEAM_STAGE role=" + role + " stage=steam_ready self="
                + SteamClient.SteamId.Value);

            if (isHost)
            {
                string session = Guid.NewGuid().ToString("N");
                service.Create(4, build, gameVersion, session);
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!service.HasLobby && service.State != SteamLobbyState.Failed
                    && Time.realtimeSinceStartup < deadline)
                {
                    service.Pump();
                    SteamClient.RunCallbacks();
                    yield return null;
                }
                if (!service.HasLobby)
                {
                    Fail(plugin, service, runId, role, "lobby-create", 0);
                    yield break;
                }
                ulong created = service.Current.Value.Id.Value;
                plugin.Report("GMP_STEAM_LOBBY " + created);
                plugin.Report("GMP_STEAM_HOST run=" + runId + " lobby=" + created
                    + " build=" + build + " game=" + gameVersion);
                if (inviteId != 0)
                {
                    bool invited = service.Invite((SteamId)inviteId);
                    plugin.Report("GMP_STEAM_INVITE sent=" + invited + " lobby=" + created);
                }
                if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "-gmp-steam-overlay") >= 0)
                    service.OpenInviteOverlay();
                if (hostOnly)
                {
                    for (float wait = Time.realtimeSinceStartup + 3f;
                        Time.realtimeSinceStartup < wait;)
                    {
                        service.Pump();
                        SteamClient.RunCallbacks();
                        yield return null;
                    }
                    plugin.Report("GMP_STEAM_RESULT PASS run=" + runId + " role=host"
                        + " lobby=" + created + " host_only=True mode=steam");
                    service.Leave();
                    service.Shutdown();
                    Application.Quit(0);
                    yield break;
                }
            }
            else
            {
                ulong joinId = lobbyId;
                if (joinId == 0) joinId = SteamLobbyService.CommandLineLobby();
                if (joinId == 0) joinId = service.PendingInvite;
                if (joinId == 0)
                {
                    float wait = Time.realtimeSinceStartup + 45f;
                    while (joinId == 0 && Time.realtimeSinceStartup < wait
                        && service.State != SteamLobbyState.Failed)
                    {
                        service.Pump();
                        SteamClient.RunCallbacks();
                        joinId = service.PendingInvite;
                        yield return null;
                    }
                }
                if (joinId == 0)
                {
                    Fail(plugin, service, runId, role, "no-lobby-or-invite", 0);
                    yield break;
                }
                service.Join(joinId, build, gameVersion);
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!service.HasLobby && service.State != SteamLobbyState.Failed
                    && Time.realtimeSinceStartup < deadline)
                {
                    service.Pump();
                    SteamClient.RunCallbacks();
                    yield return null;
                }
                if (!service.HasLobby)
                {
                    Fail(plugin, service, runId, role, "lobby-join", joinId);
                    yield break;
                }
                string hostBuild = service.Current.Value.GetData("build");
                string hostGame = service.Current.Value.GetData("gameVersion");
                if (!string.IsNullOrEmpty(hostBuild) && hostBuild != build)
                {
                    Fail(plugin, service, runId, role, "build-mismatch", joinId);
                    yield break;
                }
                if (!string.IsNullOrEmpty(hostGame) && hostGame != gameVersion)
                {
                    Fail(plugin, service, runId, role, "game-mismatch", joinId);
                    yield break;
                }
                plugin.Report("GMP_STEAM_JOINED run=" + runId + " role=guest lobby="
                    + joinId + " host=" + service.Owner.Value + " members="
                    + service.MemberCount);
            }

            var transport = new SteamP2PTransport();
            if (!isHost)
            {
                if (service.Owner.Value == 0)
                {
                    Fail(plugin, service, runId, role, "no-host-id", 0);
                    yield break;
                }
                transport.SetHost(service.Owner);
            }
            IEnumerator scenario = SessionProbe.Run(plugin, runId, role, 0, transport, "steam");
            while (true)
            {
                bool next;
                Exception failure = null;
                try { next = scenario.MoveNext(); }
                catch (Exception exception) { failure = exception; next = false; }
                if (failure != null)
                {
                    plugin.Report("GMP_STEAM_EXCEPTION run=" + runId + " role=" + role
                        + " " + failure.GetType().Name + ": " + failure.Message);
                    service.Leave();
                    service.Shutdown();
                    Application.Quit(1);
                    yield break;
                }
                if (!next) break;
                service.Pump();
                if (SteamClient.IsValid) SteamClient.RunCallbacks();
                yield return scenario.Current;
            }
            service.Leave();
            service.Shutdown();
        }

        private static void Fail(Plugin plugin, SteamLobbyService service, string runId,
            string role, string reason, ulong lobby)
        {
            plugin.Report("GMP_STEAM_RESULT FAIL run=" + runId + " role=" + role
                + " lobby=" + lobby + " reason=" + reason + " mode=steam");
            service.Leave();
            service.Shutdown();
            Application.Quit(1);
        }
    }
}
