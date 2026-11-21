using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace GoingMultiplayer
{
    // M1 gate: a persistent host/guest session over the production transport
    // seam and session core. It proves presence, snapshots, interpolation and
    // actor identity/state across one scene transition. It is not O2 authority.
    internal static class SessionProbe
    {
        private const float MoveSeconds = 2.5f;
        private const float SnapshotInterval = 0.05f;

        internal static IEnumerator Run(Plugin plugin, string runId, string role, int port,
            ITransport transport = null, string mode = "loopback")
        {
            bool isHost = role == "host";
            transport = transport ?? new LoopbackTransport();
            string build;
            using (var hash = SHA256.Create())
                build = BitConverter.ToString(hash.ComputeHash(
                    File.ReadAllBytes(typeof(Plugin).Assembly.Location)))
                    .Replace("-", "").ToLowerInvariant();
            var config = new SessionConfig
            {
                Build = build,
                GameVersion = Application.unityVersion,
                PlayerName = role,
                MaxPlayers = 4,
                PeerTimeoutSeconds = 180f,
            };
            var session = new NetSession(config, transport);
            LocalSeats.Install();
            bool negative = Array.IndexOf(Environment.GetCommandLineArgs(),
                "-gmp-session-negative") >= 0;
            int received = 0, rejected = 0, stale = 0;
            session.SnapshotReceived += delegate(Peer peer, PlayerSnapshot snapshot)
            {
                received++;
                if (!negative) ActorRegistry.ApplySnapshot(peer.PlayerId, snapshot);
                if (received <= 3)
                    plugin.Report("GMP_SESSION_SNAP role=" + role + " peer=" + peer.PlayerId
                        + " n=" + received + " x=" + Fmt(snapshot.X)
                        + " z=" + Fmt(snapshot.Z));
            };
            bool commandNegative = Array.IndexOf(Environment.GetCommandLineArgs(),
                "-gmp-session-cmd-negative") >= 0;
            session.CommandReceived += delegate(Peer peer, InputCommand command)
            {
                if (!isHost || commandNegative) return;
                NetworkActor target = ActorRegistry.Get(peer.PlayerId);
                if (target != null && target.Seat != null && target.Seat.Network != null)
                    target.Seat.Network.Apply(command, peer.LastSequence);
            };

            plugin.Report("GMP_SESSION_STAGE role=" + role + " port=" + port
                + " mode=" + mode + " stage=start");
            if (isHost) session.StartHost(port, Time.realtimeSinceStartup);
            else session.StartGuest(port, Time.realtimeSinceStartup);

            yield return NetProbe.LoadOffice(plugin);
            if (GameManager.instance) GameManager.instance.isPaused = false;
            Time.timeScale = 1f;

            float deadline = Time.realtimeSinceStartup + 45f;
            while (session.State != SessionState.Playing
                && Time.realtimeSinceStartup < deadline)
            {
                Pump(session);
                yield return null;
            }
            if (session.State != SessionState.Playing)
            {
                Fail(plugin, session, runId, role, "handshake", received, rejected, stale, mode);
                yield break;
            }
            plugin.Report("GMP_SESSION_PLAYING run=" + runId + " role=" + role
                + " mode=" + mode + " session=" + session.SessionId + " epoch=" + session.Epoch
                + " local_id=" + session.LocalPlayerId + " peers=" + session.Peers.Count);

            session.SendReady(Time.realtimeSinceStartup);
            float readyEnd = Time.realtimeSinceStartup + 60f;
            while (!session.AllPeersReady && session.State == SessionState.Playing
                && Time.realtimeSinceStartup < readyEnd)
            {
                Pump(session);
                yield return null;
            }
            bool readyBarrier = session.AllPeersReady;
            plugin.Report("GMP_SESSION_READY role=" + role + " barrier=" + readyBarrier);

            Player local = Player.singlePlayer;
            if (!local) { Fail(plugin, session, runId, role, "no local player", received, rejected, stale, mode); yield break; }
            int remoteId = isHost ? 2 : 1;
            Vector3 remoteSpawn = FindSpawn(local);
            NetworkActor remote = ActorRegistry.AddRemote(local, remoteId, remoteSpawn, isHost);
            bool localCamera = Camera.main != null && Camera.main.isActiveAndEnabled;
            bool remoteCamera = false;
            foreach (Camera camera in remote.Seat.Player.GetComponentsInChildren<Camera>(true))
                if (camera.isActiveAndEnabled) remoteCamera = true;
            bool hudLocal = GUIManager.instance != null && Player.singlePlayer == local;
            bool viewOk = localCamera && !remoteCamera && hudLocal;
            plugin.Report("GMP_SESSION_VIEW role=" + role + " local_camera=" + localCamera
                + " remote_camera=" + remoteCamera + " hud_local=" + hudLocal
                + " view_ok=" + viewOk);
            plugin.Report("GMP_SESSION_REMOTE_START role=" + role + " remote_id=" + remoteId
                + " remote=" + Fmt(remote.Seat.Player.transform.position.x) + ","
                + Fmt(remote.Seat.Player.transform.position.z)
                + " local=" + Fmt(local.transform.position.x) + ","
                + Fmt(local.transform.position.z));
            plugin.Report("GMP_SESSION_REMOTE_STATE role=" + role
                + " charging=" + remote.Seat.Player.charging
                + " attacking=" + remote.Seat.Player.view.attacking
                + " windingUp=" + remote.Seat.Player.view.windingUp
                + " simulated=" + remote.Simulated);
            LocalSeats.Seat localSeat = LocalSeats.ScriptLocalInput(local);
            local.LockMovement(false);
            local.forceMove = true;
            Vector3 remoteStart = remote.Seat.Player.transform.position;
            Vector3 localMoveStart = local.transform.position;

            float moveEnd = Time.realtimeSinceStartup + MoveSeconds;
            float lastSend = -1f;
            float lastWalk = -2f;
            while (Time.realtimeSinceStartup < moveEnd)
            {
                localSeat.Move = new Vector2(1f, 0f);
                lastSend = SendIfDue(session, local, lastSend);
                if (!isHost)
                    lastWalk = SendCommandIfDue(session,
                        new InputCommand { MoveX = 1f }, lastWalk);
                Pump(session);
                yield return null;
            }
            localSeat.Move = Vector2.zero;
            local.forceMove = false;
            for (float settle = Time.realtimeSinceStartup + 0.6f;
                Time.realtimeSinceStartup < settle;)
            {
                lastSend = SendIfDue(session, local, lastSend);
                if (!isHost)
                    lastWalk = SendCommandIfDue(session,
                        new InputCommand { MoveX = 0f }, lastWalk);
                Pump(session);
                yield return null;
            }
            float localMoveDelta = XZDistance(localMoveStart, local.transform.position);
            if (localMoveDelta < 0.1f)
            {
                Vector3 destination = local.transform.position + new Vector3(2f, 0f, 0f);
                local.transform.position = destination;
                if (local.rb) local.rb.position = destination;
                for (float settle = Time.realtimeSinceStartup + 0.6f;
                    Time.realtimeSinceStartup < settle;)
                {
                    lastSend = SendIfDue(session, local, lastSend);
                    Pump(session);
                    yield return null;
                }
                plugin.Report("GMP_SESSION_TELEPORT role=" + role
                    + " phase=office local_delta=" + Fmt(localMoveDelta));
            }
            float remoteDelta = XZDistance(remoteStart, remote.Seat.Player.transform.position);
            plugin.Report("GMP_SESSION_REMOTE_END role=" + role + " local_start="
                + Fmt(localMoveStart.x) + "," + Fmt(localMoveStart.z) + " local_now="
                + Fmt(local.transform.position.x) + "," + Fmt(local.transform.position.z)
                + " remote_start=" + Fmt(remoteStart.x) + "," + Fmt(remoteStart.z)
                + " remote_now=" + Fmt(remote.Seat.Player.transform.position.x) + ","
                + Fmt(remote.Seat.Player.transform.position.z) + " remote_delta="
                + Fmt(remoteDelta) + " received=" + received);

            // M3a action gate: only the guest sends an Attack command. On the host
            // that command must drive the guest actor through its native input
            // path; the guest's own player must not act on it locally.
            Player hostActor = isHost ? remote.Seat.Player : null;
            if (hostActor) hostActor.LockMovement(false);
            local.LockMovement(false);
            int commandsSentBefore = session.CommandsSent;
            int commandsReceivedBefore = session.CommandsReceived;
            // Let any previous action finish so a stale swing is not read as ours.
            for (float clean = Time.realtimeSinceStartup + 2f;
                Time.realtimeSinceStartup < clean && hostActor != null
                && (hostActor.charging || hostActor.view.attacking);)
            {
                Pump(session);
                yield return null;
            }
            bool hostClean = hostActor == null
                || (!hostActor.charging && !hostActor.view.attacking);
            bool hostSawAttack = false;
            bool localSawAttack = false;
            int attackAttempts = 0;
            float lastAttack = -2f;
            float actionEnd = Time.realtimeSinceStartup + 2.5f;
            while (Time.realtimeSinceStartup < actionEnd)
            {
                float actionNow = Time.realtimeSinceStartup;
                if (hostActor) hostActor.LockMovement(false);
                if (!isHost && actionNow - lastAttack >= 0.5f)
                {
                    session.SendCommand(new InputCommand
                    {
                        Held = PlayerButtons.Attack,
                        Pressed = PlayerButtons.Attack,
                    }, actionNow);
                    lastAttack = actionNow;
                    attackAttempts++;
                }
                Pump(session);
                bool attackedNow = hostActor
                    && (hostActor.charging || hostActor.view.attacking);
                // Only count a transition that follows a command in this window.
                if (hostClean && attackedNow
                    && session.CommandsReceived > commandsReceivedBefore)
                    hostSawAttack = true;
                localSawAttack |= local.charging || local.view.attacking;
                if (hostSawAttack) break;
                if (!isHost && attackAttempts >= 4) break;
                yield return null;
            }
            // Neutral command so a held attack does not chain on the host.
            if (!isHost) session.SendCommand(new InputCommand(),
                Time.realtimeSinceStartup);
            int actionSent = session.CommandsSent - commandsSentBefore;
            int actionReceived = session.CommandsReceived - commandsReceivedBefore;
            plugin.Report("GMP_SESSION_ACTION role=" + role
                + " attack_host=" + hostSawAttack
                + " attack_local=" + localSawAttack
                + " cmd_sent=" + actionSent + " cmd_recv=" + actionReceived
                + " attempts=" + attackAttempts
                + " host_clean=" + hostClean
                + " host_actor=" + (hostActor != null));

            // Give the remote actor a distinct state, then change scene. The
            // data holder must survive and the scene object must rebind.
            remote.Seat.Data.currentHealth = 7f;
            float healthBefore = remote.Seat.Data.currentHealth;
            plugin.Report("GMP_SESSION_SCENE_STAGE role=" + role + " actor_alive="
                + (remote.Seat.Player != null) + " health=" + Fmt(healthBefore));
            yield return EnterDungeonPumped(session, plugin);

            Player newLocal = Player.singlePlayer;
            Vector3 fallback = newLocal ? newLocal.transform.position + new Vector3(1.5f, 0f, 0f) : remoteSpawn;
            try { if (newLocal) fallback = SeatProbe.FindSpawn(newLocal); }
            catch (Exception exception) { plugin.Report("GMP_SESSION_SPAWN_FALLBACK " + exception.GetType().Name); }
            ActorRegistry.RebindAll(fallback);
            bool actorAlive = remote.Seat.Player != null;
            bool healthKept = Mathf.Abs(remote.Seat.Data.currentHealth - healthBefore) < 0.01f;
            plugin.Report("GMP_SESSION_SCENE_READY role=" + role
                + " actor_alive=" + actorAlive + " health_kept=" + healthKept
                + " scene=" + DungeonEntry.SelectedScene);

            float dungeonDelta = 0f;
            if (newLocal && actorAlive)
            {
                local = newLocal;
                localSeat = LocalSeats.ScriptLocalInput(local);
                local.LockMovement(false);
                local.forceMove = true;
                Vector3 remoteBefore = remote.Seat.Player.transform.position;
                Vector3 localBefore = local.transform.position;
                float dungeonEnd = Time.realtimeSinceStartup + 1.5f;
                lastSend = -1f;
                while (Time.realtimeSinceStartup < dungeonEnd)
                {
                    localSeat.Move = new Vector2(0f, 1f);
                    lastSend = SendIfDue(session, local, lastSend);
                    Pump(session);
                    yield return null;
                }
                localSeat.Move = Vector2.zero;
                local.forceMove = false;
                for (float settle = Time.realtimeSinceStartup + 0.5f;
                    Time.realtimeSinceStartup < settle;)
                {
                    lastSend = SendIfDue(session, local, lastSend);
                    Pump(session);
                    yield return null;
                }
                float localDelta = XZDistance(localBefore, local.transform.position);
                if (localDelta < 0.1f)
                {
                    // Some dungeon entrances start with movement locked. Teleport
                    // the local actor so the gate still tests scene rebinding and
                    // snapshot interpolation rather than the entrance animation.
                    Vector3 destination = local.transform.position + new Vector3(2f, 0f, 0f);
                    local.transform.position = destination;
                    if (local.rb) local.rb.position = destination;
                    for (float settle = Time.realtimeSinceStartup + 0.6f;
                        Time.realtimeSinceStartup < settle;)
                    {
                        lastSend = SendIfDue(session, local, lastSend);
                        Pump(session);
                        yield return null;
                    }
                    plugin.Report("GMP_SESSION_TELEPORT role=" + role + " local_delta=" + Fmt(localDelta));
                }
                dungeonDelta = XZDistance(remoteBefore, remote.Seat.Player.transform.position);
            }

            session.SendDone(Time.realtimeSinceStartup);
            float barrierEnd = Time.realtimeSinceStartup + 15f;
            while (!session.AllPeersDone && session.State == SessionState.Playing
                && Time.realtimeSinceStartup < barrierEnd)
            {
                Pump(session);
                yield return null;
            }
            bool doneBarrier = session.AllPeersDone;

            bool pass = session.State == SessionState.Playing
                && viewOk
                && readyBarrier
                && doneBarrier
                && !string.IsNullOrEmpty(session.SessionId)
                && session.LocalPlayerId == (isHost ? 1 : 2)
                && session.Peers.Count == 1
                && session.SnapshotsSent >= 20
                && received >= 10
                && remoteDelta > 0.25f
                && actorAlive
                && healthKept
                && dungeonDelta > 0.05f
                && (isHost
                    ? hostSawAttack && actionReceived >= 1
                    : !localSawAttack && actionSent >= 1);
            plugin.Report("GMP_SESSION_RESULT " + (pass ? "PASS" : "FAIL")
                + " run=" + runId + " role=" + role
                + " session=" + (session.SessionId ?? "none")
                + " epoch=" + session.Epoch
                + " local_id=" + session.LocalPlayerId
                + " remote_id=" + remoteId
                + " peers=" + session.Peers.Count
                + " sent=" + session.SnapshotsSent
                + " received=" + received
                + " rejected=" + session.Rejected
                + " stale=" + session.Stale
                + " remote_delta=" + Fmt(remoteDelta)
                + " dungeon_delta=" + Fmt(dungeonDelta)
                + " actor_alive=" + actorAlive
                + " health_kept=" + healthKept
                + " attack_host=" + hostSawAttack
                + " attack_local=" + localSawAttack
                + " cmd_sent=" + actionSent + " cmd_recv=" + actionReceived
                + " view_ok=" + viewOk
                + " ready_barrier=" + readyBarrier + " done_barrier=" + doneBarrier
                + " negative=" + negative
                + " scene=" + (DungeonEntry.SelectedScene ?? "none")
                + " mode=" + mode);
            session.Stop(pass ? "complete" : "failed");
            ActorRegistry.Reset();
            Application.Quit(pass ? 0 : 1);
        }

        private static void Fail(Plugin plugin, NetSession session, string runId, string role,
            string reason, int received, int rejected, int stale, string mode)
        {
            plugin.Report("GMP_SESSION_RESULT FAIL run=" + runId + " role=" + role
                + " session=" + (session.SessionId ?? "none")
                + " epoch=" + session.Epoch + " local_id=" + session.LocalPlayerId
                + " remote_id=0 peers=" + session.Peers.Count
                + " sent=" + session.SnapshotsSent + " received=" + received
                + " rejected=" + session.Rejected + " stale=" + session.Stale
                + " remote_delta=0 dungeon_delta=0 actor_alive=False health_kept=False"
                + " attack_host=False attack_local=False cmd_sent=0 cmd_recv=0"
                + " view_ok=False ready_barrier=False done_barrier=False"
                + " negative=" + (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "-gmp-session-negative") >= 0)
                + " scene=none mode=" + mode + " reason=" + reason);
            session.Stop(reason);
            ActorRegistry.Reset();
            Application.Quit(1);
        }

        private static float SendIfDue(NetSession session, Player local, float lastSend)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastSend < SnapshotInterval) return lastSend;
            session.SendSnapshot(ActorRegistry.Capture(local), now);
            return now;
        }

        private static float SendCommandIfDue(NetSession session, InputCommand command,
            float lastSent)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastSent < SnapshotInterval) return lastSent;
            session.SendCommand(command, now);
            return now;
        }

        private static void Pump(NetSession session)
        {
            session.Pump(Time.realtimeSinceStartup);
        }

        private static IEnumerator EnterDungeonPumped(NetSession session, Plugin plugin)
        {
            IEnumerator enter = DungeonEntry.EnterFromOffice(plugin, 120f);
            while (true)
            {
                bool next;
                try { next = enter.MoveNext(); }
                catch (Exception exception)
                {
                    throw new InvalidOperationException("Dungeon entry failed: " + exception.Message, exception);
                }
                if (!next) yield break;
                session.Pump(Time.realtimeSinceStartup);
                yield return enter.Current;
            }
        }

        private static Vector3 FindSpawn(Player local)
        {
            try { return SeatProbe.FindSpawn(local); }
            catch (Exception) { return local.transform.position + new Vector3(1.5f, 0f, 0f); }
        }

        private static float XZDistance(Vector3 a, Vector3 b)
        {
            Vector3 delta = a - b;
            delta.y = 0f;
            return delta.magnitude;
        }

        private static string Fmt(float value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }
    }
}
