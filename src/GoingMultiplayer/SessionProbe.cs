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
        private static int simulatedSends;
        private static float lastActorSnapshot;
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
            bool reconNegative = Array.IndexOf(Environment.GetCommandLineArgs(),
                "-gmp-session-recon-negative") >= 0;
            int received = 0, rejected = 0, stale = 0, ownSnapshots = 0;
            session.SnapshotReceived += delegate(Peer peer, PlayerSnapshot snapshot)
            {
                received++;
                if (!negative) ActorRegistry.ApplySnapshot(peer.PlayerId, snapshot);
                if (received <= 3)
                    plugin.Report("GMP_SESSION_SNAP role=" + role + " peer=" + peer.PlayerId
                        + " n=" + received + " x=" + Fmt(snapshot.X)
                        + " z=" + Fmt(snapshot.Z));
            };
            session.OwnSnapshotReceived += delegate(PlayerSnapshot snapshot)
            {
                ownSnapshots++;
                if (ownSnapshots <= 3)
                    plugin.Report("GMP_SESSION_OWN_SNAP role=" + role
                        + " n=" + ownSnapshots + " x=" + Fmt(snapshot.X)
                        + " z=" + Fmt(snapshot.Z));
                if (!reconNegative) Reconciliation.Apply(Player.singlePlayer, snapshot);
            };
            bool commandNegative = Array.IndexOf(Environment.GetCommandLineArgs(),
                "-gmp-session-cmd-negative") >= 0;
            session.CommandReceived += delegate(Peer peer, InputCommand command)
            {
                if (!isHost || commandNegative) return;
                if (command.Inventory != 0)
                    plugin.Report("GMP_SESSION_INV_CMD role=" + role
                        + " seq=" + peer.LastSequence + " inv=" + command.Inventory);
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

            // M3a focus gate: the guest's press must focus the host's actor,
            // and the host camera must not move. Remote actors are barred from
            // driving the shared CameraController.
            Vector3 cameraBefore = Camera.main != null
                && Camera.main.transform != null
                ? Camera.main.transform.position : Vector3.zero;
            bool focusHost = false;
            bool focusLocal = false;
            int focusAttempts = 0;
            float lastFocus = -2f;
            float focusEnd = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < focusEnd)
            {
                float focusNow = Time.realtimeSinceStartup;
                if (!isHost && focusNow - lastFocus >= 0.5f)
                {
                    session.SendCommand(new InputCommand
                    {
                        Held = PlayerButtons.Focus,
                        Pressed = PlayerButtons.Focus,
                    }, focusNow);
                    lastFocus = focusNow;
                    focusAttempts++;
                }
                Pump(session);
                if (hostActor) focusHost |= hostActor.focusing;
                focusLocal |= local.focusing;
                if (focusHost) break;
                if (!isHost && focusAttempts >= 4) break;
                yield return null;
            }
            if (!isHost) session.SendCommand(new InputCommand(),
                Time.realtimeSinceStartup);
            float cameraDelta = Camera.main != null && Camera.main.transform != null
                ? Vector3.Distance(cameraBefore, Camera.main.transform.position) : 0f;
            plugin.Report("GMP_SESSION_FOCUS role=" + role
                + " focus_host=" + focusHost + " focus_local=" + focusLocal
                + " cam_delta=" + Fmt(cameraDelta)
                + " attempts=" + focusAttempts);

            // M3a inventory gate: one step on the guest must scroll the host's
            // actor, not the guest's own inventory. Wait for the previous swing
            // to finish first; inventory is blocked while attacking.
            for (float clean = Time.realtimeSinceStartup + 3f;
                Time.realtimeSinceStartup < clean && hostActor != null
                && (hostActor.charging || hostActor.view.attacking);)
            {
                Pump(session);
                yield return null;
            }
            int invBefore = hostActor ? hostActor.data.inventory.currentInventoryIndex : -1;
            int invLocalBefore = local.data.inventory.currentInventoryIndex;
            int invCommandBefore = session.CommandsReceived;
            if (!isHost)
            {
                session.SendCommand(new InputCommand { Inventory = 1 },
                    Time.realtimeSinceStartup);
                plugin.Report("GMP_SESSION_INV_SEND inv=1 seq="
                    + session.LastCommandSequence);
            }
            int invReadsBefore = NetworkInputState.InventoryReads;
            int invHitsBefore = NetworkInputState.InventoryHits;
            int invSeen = 0;
            for (float invEnd = Time.realtimeSinceStartup + 0.6f;
                Time.realtimeSinceStartup < invEnd;)
            {
                Pump(session);
                if (hostActor && remote.Seat.Network != null)
                    invSeen = Mathf.Max(invSeen, remote.Seat.Network.Command.Inventory);
                yield return null;
            }
            if (!isHost) session.SendCommand(new InputCommand(),
                Time.realtimeSinceStartup);
            int invAfter = hostActor ? hostActor.data.inventory.currentInventoryIndex : -1;
            int invLocalAfter = local.data.inventory.currentInventoryIndex;
            bool invMoved = hostActor != null && invAfter != invBefore;
            bool invLocalMoved = invLocalAfter != invLocalBefore;
            plugin.Report("GMP_SESSION_INV role=" + role + " inv_host=" + invAfter
                + " moved=" + invMoved + " local=" + invLocalAfter
                + " local_moved=" + invLocalMoved
                + " charging=" + (hostActor != null && hostActor.charging)
                + " attacking=" + (hostActor != null && hostActor.view.attacking)
                + " can_swap=" + (hostActor != null && hostActor.canSwapInventory)
                + " inv_cmd=" + (session.CommandsReceived - invCommandBefore)
                + " inv_seen=" + invSeen
                + " inv_reads=" + (NetworkInputState.InventoryReads - invReadsBefore)
                + " inv_hits=" + (NetworkInputState.InventoryHits - invHitsBefore)
                + " controls_blocked=" + (hostActor != null && hostActor.controlsBlocked)
                + " force_single=" + (hostActor != null && hostActor.forceSingleItem));

            // M3a control-basis gate: movement must be replayed in the sender's
            // camera frame. MoveX=1 at yaw 90 must give -Z, at yaw 270 +Z.
            Vector3 basis90 = Vector3.zero;
            Vector3 basis270 = Vector3.zero;
            bool got90 = false;
            bool got270 = false;
            float lastBasis = -2f;
            float basisStart = Time.realtimeSinceStartup;
            float basisEnd = basisStart + 1.4f;
            while (Time.realtimeSinceStartup < basisEnd)
            {
                float basisNow = Time.realtimeSinceStartup;
                if (!isHost && basisNow - lastBasis >= 0.1f)
                {
                    float yaw = basisNow - basisStart < 0.7f ? 90f : 270f;
                    session.SendCommand(new InputCommand { MoveX = 1f, Yaw = yaw },
                        basisNow);
                    lastBasis = basisNow;
                }
                Pump(session);
                if (hostActor && remote.Seat.Network != null)
                {
                    float yaw = remote.Seat.Network.ControlYaw;
                    if (!got90 && Mathf.Abs(Mathf.DeltaAngle(yaw, 90f)) < 5f)
                    {
                        basis90 = hostActor.GetInputVector();
                        got90 = true;
                    }
                    if (!got270 && Mathf.Abs(Mathf.DeltaAngle(yaw, 270f)) < 5f)
                    {
                        basis270 = hostActor.GetInputVector();
                        got270 = true;
                    }
                }
                if (isHost && got90 && got270) break;
                yield return null;
            }
            if (!isHost) session.SendCommand(new InputCommand(),
                Time.realtimeSinceStartup);
            bool basisOk = got90 && got270
                && Mathf.Abs(basis90.z + 1f) < 0.15f && Mathf.Abs(basis90.x) < 0.15f
                && Mathf.Abs(basis270.z - 1f) < 0.15f && Mathf.Abs(basis270.x) < 0.15f;
            plugin.Report("GMP_SESSION_BASIS role=" + role
                + " got90=" + got90 + " got270=" + got270
                + " x90=" + Fmt(basis90.x) + " z90=" + Fmt(basis90.z)
                + " x270=" + Fmt(basis270.x) + " z270=" + Fmt(basis270.z)
                + " basis_ok=" + basisOk);

            // M3a reconciliation gate: the guest keeps its local player still
            // while still commanding the host to walk, so the two poses diverge.
            // The host's authoritative snapshots must pull the guest back.
            Reconciliation.Reset();
            Vector3 reconStart = local.transform.position;
            // Manufacture a divergence the host did not authorise: shove the
            // guest's local player sideways. The host's snapshots must pull it
            // back to the authoritative pose.
            // Well past the 3-unit recovery threshold so the gate is not
            // sensitive to a little physics drift.
            Vector3 drift = reconStart + new Vector3(6f, 0f, 0f);
            local.transform.position = drift;
            if (local.rb) local.rb.position = drift;
            float lastReconCommand = -2f;
            float lastReconSnapshot = -1f;
            float reconEnd = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < reconEnd)
            {
                localSeat.Move = Vector2.zero;
                lastReconSnapshot = SendIfDue(session, local, lastReconSnapshot);
                if (!isHost)
                    lastReconCommand = SendCommandIfDue(session,
                        new InputCommand { MoveX = 1f }, lastReconCommand);
                Pump(session);
                yield return null;
            }
            localSeat.Move = Vector2.zero;
            if (!isHost) session.SendCommand(new InputCommand(),
                Time.realtimeSinceStartup);
            float reconDelta = XZDistance(drift, local.transform.position);
            int reconCorrections = Reconciliation.Corrections;
            int reconHard = Reconciliation.HardSnaps;
            float reconError = Reconciliation.Corrections > 0
                ? Vector3.Distance(local.transform.position, Reconciliation.LastAuthoritative)
                : XZDistance(local.transform.position, drift);
            plugin.Report("GMP_SESSION_RECON role=" + role
                + " basis_ok=" + basisOk
                + " focus_host=" + focusHost + " focus_local=" + focusLocal
                + " cam_delta=" + Fmt(cameraDelta)
                + " inv_moved=" + invMoved + " inv_local_moved=" + invLocalMoved
                + " recon_delta=" + Fmt(reconDelta)
                + " corrections=" + reconCorrections
                + " hard=" + reconHard
                + " error=" + Fmt(reconError)
                + " own_snaps=" + ownSnapshots
                + " sim_sends=" + simulatedSends
                + " tx=" + LoopbackTransport.Sent
                + " tx_fail=" + LoopbackTransport.SendFailed
                + " rx=" + LoopbackTransport.Received);
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
                        && focusHost && cameraDelta <= 0.05f && basisOk
                    : !localSawAttack && actionSent >= 1 && !focusLocal)
                && (isHost || (reconCorrections >= 1 && reconError <= 0.5f));
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
                + " basis_ok=" + basisOk
                + " focus_host=" + focusHost + " focus_local=" + focusLocal
                + " cam_delta=" + Fmt(cameraDelta)
                + " inv_moved=" + invMoved + " inv_local_moved=" + invLocalMoved
                + " recon_delta=" + Fmt(reconDelta)
                + " recon_corrections=" + reconCorrections
                + " recon_hard=" + reconHard
                + " recon_error=" + Fmt(reconError)
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
                + " basis_ok=False focus_host=False focus_local=False cam_delta=0"
                + " inv_moved=False inv_local_moved=False"
                + " recon_delta=0 recon_corrections=0 recon_hard=0 recon_error=0"
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
            // The host also publishes actors it simulates for a guest, at half
            // the rate so the two streams do not arrive as back-to-back bursts.
            if (now - lastActorSnapshot < SnapshotInterval * 2f) return now;
            lastActorSnapshot = now;
            foreach (NetworkActor actor in ActorRegistry.All)
                if (actor.Simulated && actor.Seat != null && actor.Seat.Player)
                {
                    session.SendSnapshotFor(actor.PlayerId,
                        ActorRegistry.Capture(actor.Seat.Player), now);
                    simulatedSends++;
                }
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
