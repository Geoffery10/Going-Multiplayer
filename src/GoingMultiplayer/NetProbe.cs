using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GoingMultiplayer
{
    // L2 test-only gate: two real clients exchange presence and player poses over
    // loopback UDP. No production transport, lobby or gameplay sync is involved.
    internal static class NetProbe
    {
        private const float StateInterval = 0.05f;
        private const float MovementSeconds = 1.5f;
        private const int Epoch = 1;
        private static bool SkipAccountMutation() { return false; }

        internal static IEnumerator Run(Plugin plugin, string runId, string role, int port,
            bool negative, bool staleNegative, bool authorityNegative)
        {
            bool isHost = role == "host";
            var qa = new Harmony("goingmultiplayer.qa-account-guard");
            foreach (string name in new[] { "TestAchievementComplete", "TestStatIncremented", "ResetCheevos" })
                qa.Patch(AccessTools.Method(typeof(SteamManager), name),
                    prefix: new HarmonyMethod(typeof(NetProbe).GetMethod("SkipAccountMutation",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            LocalSeats.Install();
            yield return LoadOffice(plugin);

            Player local = Player.singlePlayer;
            GameManager.instance.isPaused = false;
            Time.timeScale = 1f;
            local.LockMovement(false);
            local.forceMove = true;
            LocalSeats.Seat localSeat = LocalSeats.ScriptLocalInput(local);
            LocalSeats.Seat remoteSeat = LocalSeats.Spawn(local, SeatProbe.FindSpawn(local), true);
            if (remoteSeat.Player.rb) remoteSeat.Player.rb.isKinematic = true;
            remoteSeat.Player.SetPhysicalCollidersActive(false);
            Vector3 localStart = local.transform.position;

            int localId = isHost ? 1 : 2;
            int peerId = isHost ? 2 : 1;
            string session = null;
            bool welcomed = isHost;
            bool connected = false;
            bool haveApplied = false;
            bool controlsSent = false;
            bool staleRejected = false;
            bool authorityRejected = false;
            bool epochRejected = false;
            int updates = 0;
            int applied = 0;
            int lastPeerSeq = -1;
            int seq = 0;
            float lastSend = 0f;
            float lastHello = 0f;
            Vector3 firstApplied = Vector3.zero;
            Vector3 lastApplied = Vector3.zero;
            Vector3 lastRemote = Vector3.zero;

            var link = new UdpTestLink();
            plugin.Report("GMP_NET_STAGE listen role=" + role + " port=" + port);
            if (isHost) link.StartHost(port);
            else link.StartGuest(port);

            float handshakeDeadline = Time.realtimeSinceStartup + 60f;
            float endTime = handshakeDeadline;
            bool runWindow = false;
            float moveStart = -1f;
            try
            {
                while (true)
                {
                    float now = Time.realtimeSinceStartup;
                    if (!runWindow && connected)
                    {
                        runWindow = true;
                        moveStart = now + 0.5f;
                        endTime = now + 6f;
                        plugin.Report("GMP_NET_PRESENT role=" + role + " session=" + session
                            + " epoch=" + Epoch + " local_id=" + localId + " peer_id=" + peerId);
                    }
                    if (now >= endTime) break;

                    string message;
                    System.Net.IPEndPoint from;
                    while (link.TryReceive(out message, out from))
                    {
                        if (message.StartsWith("HELLO|", StringComparison.Ordinal))
                        {
                            if (!isHost) continue;
                            string[] hello = message.Split('|');
                            if (hello.Length >= 2 && hello[1] == runId)
                            {
                                if (string.IsNullOrEmpty(session)) session = Guid.NewGuid().ToString("N");
                                link.SetPeer(from);
                                link.Send("WELCOME|" + runId + "|" + session + "|"
                                    + Epoch + "|2|1");
                            }
                        }
                        else if (message.StartsWith("WELCOME|", StringComparison.Ordinal) && !isHost)
                        {
                            string[] welcome = message.Split('|');
                            if (welcome.Length >= 6 && welcome[1] == runId
                                && int.TryParse(welcome[3], out int welcomeEpoch)
                                && welcomeEpoch == Epoch)
                            {
                                session = welcome[2];
                                welcomed = true;
                            }
                        }
                        else if (message.StartsWith("STATE|", StringComparison.Ordinal))
                        {
                            int packetEpoch, id, peerSeq;
                            Vector3 pose;
                            if (!TryParseState(message, out packetEpoch, out id, out peerSeq, out pose)) continue;

                            if (packetEpoch != Epoch)
                            {
                                epochRejected = true;
                                continue;
                            }
                            if (id != peerId)
                            {
                                // Mutation: accept a guest packet claiming host id.
                                if (isHost && authorityNegative) { }
                                else
                                {
                                    if (isHost) authorityRejected = true;
                                    continue;
                                }
                            }
                            if (peerSeq <= lastPeerSeq)
                            {
                                // Mutation: accept an out-of-order packet.
                                if (isHost && staleNegative) { }
                                else
                                {
                                    if (isHost) staleRejected = true;
                                    continue;
                                }
                            }
                            if (peerSeq > lastPeerSeq) lastPeerSeq = peerSeq;
                            updates++;
                            connected = true;
                            lastRemote = pose;
                            if (!(negative && isHost))
                            {
                                ApplyRemote(remoteSeat, pose);
                                if (!haveApplied) firstApplied = pose;
                                lastApplied = pose;
                                haveApplied = true;
                                applied++;
                            }
                        }
                    }

                    if (!isHost && !welcomed && now - lastHello > 0.25f)
                    {
                        link.Send("HELLO|" + runId);
                        lastHello = now;
                    }

                    if ((isHost && !string.IsNullOrEmpty(session)) || (!isHost && welcomed))
                    {
                        // The guest stops normal state after sending its control
                        // packets so a rejected control cannot be overwritten.
                        if (now - lastSend >= StateInterval && !(!isHost && controlsSent))
                        {
                            Vector3 pose = local.transform.position;
                            link.Send("STATE|" + Epoch + "|" + localId + "|" + (++seq) + "|"
                                + Fmt(pose.x) + "|" + Fmt(pose.y) + "|" + Fmt(pose.z));
                            lastSend = now;
                        }
                    }

                    if (!isHost && runWindow)
                    {
                        if (now >= moveStart && now <= moveStart + MovementSeconds)
                            localSeat.Move = new Vector2(1f, 0f);
                        else
                            localSeat.Move = Vector2.zero;

                        // At the end, try one stale-sequence, one stale-epoch and
                        // one host-id claim. A correct host rejects all three.
                        if (!controlsSent && now >= moveStart + 3.5f)
                        {
                            link.Send("STATE|" + Epoch + "|2|1|999|0|999");
                            link.Send("STATE|0|2|6000|999|0|999");
                            link.Send("STATE|" + Epoch + "|1|7000|999|0|999");
                            controlsSent = true;
                        }
                    }
                    yield return null;
                }
            }
            finally
            {
                localSeat.Move = Vector2.zero;
                link.Close();
            }

            float localDelta = XZDistance(localStart, local.transform.position);
            float remoteDelta = haveApplied ? XZDistance(firstApplied, lastApplied) : 0f;
            bool idsOk = localId != peerId;
            bool sessionOk = !string.IsNullOrEmpty(session);
            bool localOk = isHost || localDelta > 0.5f;
            bool remoteOk = !isHost || remoteDelta > 0.5f;
            bool controlsOk = isHost
                ? (staleRejected && authorityRejected && epochRejected)
                : controlsSent;
            bool mutation = isHost && (negative || staleNegative || authorityNegative);
            bool pass = connected && sessionOk && idsOk && updates >= 20
                && localOk && remoteOk && controlsOk && !mutation;
            Vector3 finalLocal = local.transform.position;
            Vector3 finalRemote = haveApplied ? lastApplied : lastRemote;
            plugin.Report("GMP_NET_RESULT " + (pass ? "PASS" : "FAIL")
                + " run=" + runId + " role=" + role + " session=" + (session ?? "none")
                + " local_id=" + localId + " peer_id=" + peerId
                + " connected=" + connected + " local_delta=" + Fmt(localDelta)
                + " remote_delta=" + Fmt(remoteDelta) + " updates=" + updates
                + " epoch=" + Epoch + " stale_rejected=" + staleRejected
                + " authority_rejected=" + authorityRejected
                + " epoch_rejected=" + epochRejected
                + " controls_sent=" + controlsSent + " applied=" + applied
                + " final_local=" + Fmt(finalLocal.x) + "," + Fmt(finalLocal.z)
                + " final_remote=" + Fmt(finalRemote.x) + "," + Fmt(finalRemote.z)
                + " negative=" + negative
                + " stale_negative=" + staleNegative
                + " authority_negative=" + authorityNegative);
            Application.Quit(pass ? 0 : 1);
        }

        internal static IEnumerator LoadOffice(Plugin plugin)
        {
            plugin.Report("GMP_NET_STAGE wait_for_title");
            float deadline = Time.realtimeSinceStartup + 90f;
            while ((!Steamworks.SteamClient.IsValid || !GameManager.instance || !GUIManager.instance
                || !Object.FindObjectOfType<TitleScreen>())
                && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Steamworks.SteamClient.IsValid) throw new InvalidOperationException("Steam not authenticated");
            if (!Object.FindObjectOfType<TitleScreen>()) throw new InvalidOperationException("Title screen did not load");
            yield return null;
            SaveLoad.MountSave(0);
            SaveData.instance.introCutsceneViewed = true;
            plugin.Report("GMP_NET_STAGE load_office");
            GUIManager.instance.LoadFizzle();
            deadline = Time.realtimeSinceStartup + 60f;
            while ((!Player.singlePlayer || GUIManager.instance.IsLoading())
                && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Player.singlePlayer || GUIManager.instance.IsLoading())
                throw new InvalidOperationException("Office player did not load");
            yield return null;
        }

        private static bool TryParseState(string message, out int epoch, out int id,
            out int seq, out Vector3 pose)
        {
            epoch = -1;
            id = -1;
            seq = -1;
            pose = Vector3.zero;
            string[] parts = message.Split('|');
            if (parts.Length != 7) return false;
            if (!int.TryParse(parts[1], out epoch)
                || !int.TryParse(parts[2], out id)
                || !int.TryParse(parts[3], out seq)) return false;
            float x, y, z;
            if (!float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            pose = new Vector3(x, y, z);
            return true;
        }

        private static void ApplyRemote(LocalSeats.Seat seat, Vector3 pose)
        {
            if (seat == null || !seat.Player) return;
            seat.Player.transform.position = pose;
            if (seat.Player.rb)
            {
                seat.Player.rb.position = pose;
                seat.Player.rb.velocity = Vector3.zero;
            }
        }

        private static float XZDistance(Vector3 a, Vector3 b)
        {
            Vector3 delta = Util.ZeroY(a - b);
            return delta.magnitude;
        }

        private static string Fmt(float value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }
    }
}
