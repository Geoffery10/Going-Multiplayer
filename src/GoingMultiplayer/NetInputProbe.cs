using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace GoingMultiplayer
{
    // O2 movement-authority QA. The guest sends camera/yaw + axis intent; the
    // host converts it to its own camera frame and lets the game's native player
    // movement simulate P2. The guest then displays host snapshots. Test-only
    // loopback transport; no production session, lobby or prediction.
    internal static class NetInputProbe
    {
        private const int Epoch = 1;
        private const float InputInterval = 0.05f;
        private const float SnapshotInterval = 0.05f;
        private const float InputTimeout = 0.25f;
        private const float MoveSeconds = 1.5f;
        private const float SilenceSeconds = 0.6f;
        private const float StaleDelay = 0.9f;

        private static bool host;
        private static Player simulated;
        private static Player idle;
        private static LocalSeats.Seat simulatedSeat;
        private static LocalSeats.Seat idleSeat;
        private static Plugin pluginRef;
        private static Vector3 moveDir;
        private static float moveYaw;
        private static float initialClearance;
        private static Vector3 simulatedStartPos;

        private static bool SkipAccountMutation() { return false; }

        internal static IEnumerator Run(Plugin plugin, string runId, string role, int port,
            bool inputDrop, bool staleNegative, bool timeoutNegative, bool snapshotNegative)
        {
            host = role == "host";
            pluginRef = plugin;
            string build;
            using (var hash = SHA256.Create())
                build = BitConverter.ToString(hash.ComputeHash(
                    System.IO.File.ReadAllBytes(typeof(Plugin).Assembly.Location)))
                    .Replace("-", "").ToLowerInvariant();
            var patches = new Harmony("goingmultiplayer.qa-net-input");
            foreach (string name in new[] { "TestAchievementComplete", "TestStatIncremented", "ResetCheevos" })
                patches.Patch(AccessTools.Method(typeof(SteamManager), name),
                    prefix: new HarmonyMethod(typeof(NetInputProbe), "SkipAccountMutation"));
            LocalSeats.Install();
            yield return NetProbe.LoadOffice(plugin);
            yield return DungeonEntry.EnterFromOffice(plugin);
            GameManager.instance.isPaused = false;
            Time.timeScale = 1f;
            Player local = Player.singlePlayer;
            if (!local) throw new InvalidOperationException("No local player after dungeon entry");
            Room room = DungeonManager.currentRoom;
            if (!room) throw new InvalidOperationException("No current room after dungeon entry");
            string scene = DungeonEntry.SelectedScene;
            int floor = DungeonManager.instance.currentFloor;
            // Match the O4 fixture timing: let generated floor setup settle,
            // then spawn and hash the static room collision state.
            yield return new WaitForSeconds(3f);
            LocalSeats.Seat localSeat = LocalSeats.ScriptLocalInput(local);
            LocalSeats.Seat remoteSeat = LocalSeats.Spawn(local, SeatProbe.FindSpawn(local), true);
            foreach (Player player in new[] { local, remoteSeat.Player })
            {
                player.MaxHealth = 10f;
                player.health = 10f;
                player.overHealth = 0f;
                player.armor = 0f;
                if (player.rb) player.rb.velocity = Vector3.zero;
            }
            // The host simulates P2 (its spawned remote actor). The guest's
            // local actor is P2 and displays host snapshots.
            simulated = host ? remoteSeat.Player : local;
            idle = host ? local : remoteSeat.Player;
            simulatedSeat = host ? remoteSeat : localSeat;
            idleSeat = host ? localSeat : remoteSeat;
            SnapToGround(simulated);
            if (host)
            {
                SnapToGround(idle);
                ParkIdle(idle);
                simulated.SetPhysicalCollidersActive(true);
                if (simulated.rb) simulated.rb.isKinematic = false;
                FindSafeSpawn(simulated, idle);
                simulated.forceMove = true;
                simulated.LockMovement(false);
                moveDir = ChooseDirection(simulated, idle, room);
                moveYaw = Mathf.Atan2(moveDir.x, moveDir.z) * Mathf.Rad2Deg;
                initialClearance = Clearance(simulated, idle, moveDir, 4f);
            }
            else
            {
                simulated.forceMove = snapshotNegative;
                simulated.LockMovement(!snapshotNegative);
                simulated.SetPhysicalCollidersActive(snapshotNegative);
                if (simulated.rb) simulated.rb.isKinematic = !snapshotNegative;
                ParkIdle(idle);
                moveDir = Vector3.forward;
            }
            simulatedStartPos = simulated.transform.position;
            if (simulated.rb) simulated.rb.velocity = Vector3.zero;
            if (idle.rb) idle.rb.velocity = Vector3.zero;
            string geometry = RoomFixture.Fingerprint(room);
            DismissIntro();
            yield return null;
            plugin.Report("GMP_INPUT_INTRO role=" + role
                + " dialogue=" + PixelCrushers.DialogueSystem.DialogueManager.instance.isConversationActive
                + " background=" + Application.runInBackground);
            int actors = Math.Max(GameManager.playerList.Count, 1 + LocalSeats.Count);
            if (host)
                plugin.Report("GMP_INPUT_START pos=" + Fmt(simulated.transform.position.x) + ","
                    + Fmt(simulated.transform.position.z) + " kinematic="
                    + (simulated.rb && simulated.rb.isKinematic));
            plugin.Report("GMP_INPUT_FIXTURE role=" + role + " room=" + room.name
                + " scene=" + scene + " floor=" + floor + " actors=" + actors
                + " geometry=" + geometry
                + (host ? " yaw=" + Fmt(moveYaw) + " clearance=" + Fmt(initialClearance) : ""));

            string session = host ? Guid.NewGuid().ToString("N") : null;
            bool connected = false, ready = false, go = false, done = false, ack = false;
            bool staleRejected = false, staleApplied = false, timeoutCleared = false;
            bool stopMeasured = false, driftMeasured = false, staleMeasured = false;
            bool lastInputWasMove = false, released = false;
            int appliedInputs = 0, movedInputs = 0, rejected = 0, snapshotsSent = 0;
            int sent = 0, snapshotsReceived = 0, snapshotsApplied = 0, snapshotsIgnored = 0, snapshotsRejected = 0;
            int lastSnapSeq = -1, seq = 0;
            float lastInputAt = 0f, stopAt = 0f, staleAt = 0f;
            Vector3 stopPos = Vector3.zero, stalePos = Vector3.zero;
            float staleBackward = 0f, postStopDrift = 0f;
            Vector2 pendingMove = Vector2.zero;
            float moveStart = -1f, moveEnd = -1f, silenceEnd = -1f, staleDue = -1f, doneDue = -1f;
            bool staleSent = false, doneSent = false, ackSent = false, sentControls = false;
            bool haveHostPose = false;
            Vector3 firstHostPos = Vector3.zero, lastHostPos = Vector3.zero, guestStartPos = Vector3.zero;
            float lastSend = -1f, lastSnap = -1f, lastHello = -1f, lastWelcome = -1f, lastGo = -1f, lastAck = -1f;
            float lastDiag = -1f;
            var ledger = new InputCommandLedger(Epoch, 2);
            var link = new UdpTestLink();
            if (host) link.StartHost(port); else link.StartGuest(port);
            float deadline = Time.realtimeSinceStartup + 75f;
            float readyAt = -1f, endAt = -1f;
            plugin.Report("GMP_INPUT_STAGE role=" + role + " port=" + port);
            try
            {
                while (Time.realtimeSinceStartup < deadline)
                {
                    float now = Time.realtimeSinceStartup;
                    if (endAt >= 0f && now >= endAt) break;
                    string message;
                    System.Net.IPEndPoint from;
                    while (link.TryReceive(out message, out from))
                    {
                        string[] parts = message.Split('|');
                        if (host && parts.Length == 7 && parts[0] == "INPUT_HELLO"
                            && parts[1] == runId && parts[2] == build && parts[3] == scene
                            && parts[4] == floor.ToString() && parts[5] == geometry
                            && parts[6] == actors.ToString())
                        {
                            connected = true;
                            link.SetPeer(from);
                        }
                        else if (!host && parts.Length == 10 && parts[0] == "INPUT_WELCOME"
                            && parts[1] == runId && parts[3] == Epoch.ToString()
                            && parts[5] == build && parts[6] == scene && parts[7] == floor.ToString()
                            && parts[8] == geometry && parts[9] == actors.ToString())
                        {
                            if (float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw)
                                && !float.IsNaN(yaw) && !float.IsInfinity(yaw))
                            {
                                session = parts[2];
                                moveYaw = yaw;
                                moveDir = Quaternion.Euler(0f, moveYaw, 0f) * Vector3.forward;
                                connected = true;
                            }
                        }
                        else if (host && parts.Length == 4 && parts[0] == "INPUT_READY"
                            && parts[1] == runId && parts[2] == session && parts[3] == "1")
                        {
                            if (!ready) readyAt = now;
                            ready = true;
                        }
                        else if (!host && parts.Length == 4 && parts[0] == "INPUT_GO"
                            && parts[1] == runId && parts[2] == session && parts[3] == Epoch.ToString())
                        {
                            if (!go)
                            {
                                go = true;
                                moveStart = now + 0.3f;
                                moveEnd = moveStart + MoveSeconds;
                                silenceEnd = moveEnd + SilenceSeconds;
                                staleDue = moveEnd + StaleDelay;
                                doneDue = staleDue + 0.8f;
                                link.Send("INPUT_GO_ACK|" + runId + "|" + session + "|1");
                            }
                        }
                        else if (host && parts.Length == 4 && parts[0] == "INPUT_GO_ACK"
                            && parts[1] == runId && parts[2] == session && parts[3] == "1")
                        {
                            go = true;
                        }
                        else if (host && parts.Length == 9 && parts[0] == "INPUT"
                            && parts[1] == runId && parts[2] == session)
                        {
                            if (!int.TryParse(parts[3], out int packetEpoch)
                                || !int.TryParse(parts[4], out int playerId)
                                || !long.TryParse(parts[5], out long packetSeq)
                                || !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float h)
                                || !float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                                || !float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw))
                            { rejected++; continue; }
                            InputDecision decision = ledger.Accept(packetSeq, packetEpoch, playerId, h, v);
                            if (decision == InputDecision.Reject) { rejected++; continue; }
                            if (decision == InputDecision.Stale)
                            {
                                stalePos = simulated.transform.position;
                                staleAt = now;
                                if (!staleNegative) { staleRejected = true; continue; }
                                staleApplied = true;
                                plugin.Report("GMP_INPUT_STALE_ACCEPTED seq=" + packetSeq
                                    + " last=" + ledger.LastSequence);
                            }
                            if (inputDrop) continue;
                            if (!released)
                            {
                                released = true;
                                if (simulated.rb)
                                {
                                    simulated.rb.isKinematic = false;
                                    simulated.rb.velocity = Vector3.zero;
                                }
                            }
                            Vector3 world = Quaternion.Euler(0f, yaw, 0f) * new Vector3(h, 0f, v);
                            Vector3 axes = CameraController.instance.relativeControlSpot.InverseTransformDirection(world);
                            Vector2 move = new Vector2(axes.x, axes.z);
                            if (move.sqrMagnitude < 0.01f) move = new Vector2(h, v);
                            else move.Normalize();
                            if (appliedInputs == 0 && lastInputAt == 0f)
                                plugin.Report("GMP_INPUT_FIRST world=" + Fmt(world.x) + "," + Fmt(world.z)
                                    + " axes=" + Fmt(axes.x) + "," + Fmt(axes.z)
                                    + " move=" + Fmt(move.x) + "," + Fmt(move.y));
                            pendingMove = move;
                            lastInputAt = now;
                            lastInputWasMove = h * h + v * v > 0.001f;
                            if (decision == InputDecision.Apply) appliedInputs++;
                            if (lastInputWasMove) movedInputs++;
                        }
                        else if (host && parts.Length == 4 && parts[0] == "INPUT_DONE"
                            && parts[1] == runId && parts[2] == session && parts[3] == "1")
                        {
                            done = true;
                        }
                        else if (!host && parts.Length == 4 && parts[0] == "INPUT_ACK"
                            && parts[1] == runId && parts[2] == session && parts[3] == "1")
                        {
                            ack = true;
                            if (endAt < 0f) endAt = now + 0.5f;
                        }
                        else if (!host && parts.Length == 9 && parts[0] == "ISNAP"
                            && parts[1] == runId && parts[2] == session
                            && parts[3] == Epoch.ToString() && parts[4] == "2")
                        {
                            if (!int.TryParse(parts[5], out int snapSeq)
                                || !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                                || !float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                                || !float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                                || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                                || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
                            { snapshotsRejected++; continue; }
                            if (snapSeq <= lastSnapSeq) { snapshotsRejected++; continue; }
                            lastSnapSeq = snapSeq;
                            snapshotsReceived++;
                            Vector3 pose = new Vector3(x, y, z);
                            if (!haveHostPose)
                            {
                                haveHostPose = true;
                                firstHostPos = pose;
                                Align(simulated, pose);
                                guestStartPos = pose;
                            }
                            else if (snapshotNegative)
                            {
                                snapshotsIgnored++;
                            }
                            else
                            {
                                Align(simulated, pose);
                                snapshotsApplied++;
                            }
                            lastHostPos = pose;
                        }
                    }

                    if (!host && !connected && now - lastHello > 0.25f)
                    {
                        lastHello = now;
                        link.Send("INPUT_HELLO|" + runId + "|" + build + "|" + scene + "|"
                            + floor + "|" + geometry + "|" + actors);
                    }
                    if (host && connected && !ready && now - lastWelcome > 0.2f)
                    {
                        lastWelcome = now;
                        link.Send("INPUT_WELCOME|" + runId + "|" + session + "|" + Epoch + "|"
                            + Fmt(moveYaw) + "|" + build + "|" + scene + "|" + floor + "|"
                            + geometry + "|" + actors);
                    }
                    if (!host && connected && !go)
                    {
                        ready = true;
                        link.Send("INPUT_READY|" + runId + "|" + session + "|1");
                    }
                    if (host && connected && ready && !go && now - lastGo > 0.2f)
                    {
                        lastGo = now;
                        link.Send("INPUT_GO|" + runId + "|" + session + "|" + Epoch);
                    }

                    if (host)
                    {
                        // Hold the remote actor at its spawn until the first
                        // accepted command so spawn depenetration cannot be
                        // mistaken for guest-driven movement.
                        if (!released)
                        {
                            simulated.transform.position = simulatedStartPos;
                            if (simulated.rb)
                            {
                                simulated.rb.position = simulatedStartPos;
                                simulated.rb.velocity = Vector3.zero;
                            }
                        }
                        bool silent = lastInputAt > 0f && now - lastInputAt > InputTimeout;
                        if (silent && lastInputWasMove && !stopMeasured)
                        {
                            stopMeasured = true;
                            stopAt = now;
                            stopPos = simulated.transform.position;
                            if (!timeoutNegative) timeoutCleared = true;
                        }
                        if (stopMeasured && !driftMeasured && now >= stopAt + 0.3f)
                        {
                            driftMeasured = true;
                            postStopDrift = XZDistance(stopPos, simulated.transform.position);
                        }
                        if (silent && lastInputWasMove && !timeoutNegative) pendingMove = Vector2.zero;
                        simulatedSeat.Move = (lastInputAt > 0f && (!silent || timeoutNegative))
                            ? pendingMove : Vector2.zero;

                        if (staleAt > 0f && !staleMeasured && now >= staleAt + 0.25f)
                        {
                            staleMeasured = true;
                            staleBackward = Mathf.Max(0f, Vector3.Dot(
                                simulated.transform.position - stalePos, -moveDir));
                        }

                        if (released && now - lastDiag >= 0.25f)
                        {
                            lastDiag = now;
                            Vector3 p = simulated.transform.position;
                            plugin.Report("GMP_INPUT_MOVE t=" + Fmt(now) + " pos=" + Fmt(p.x) + ","
                                + Fmt(p.z) + " vel=" + Fmt(simulated.rb ? simulated.rb.velocity.x : 0f)
                                + "," + Fmt(simulated.rb ? simulated.rb.velocity.z : 0f)
                                + " move=" + Fmt(simulatedSeat.Move.x) + "," + Fmt(simulatedSeat.Move.y)
                                + " kinematic=" + (simulated.rb && simulated.rb.isKinematic));
                        }
                        if (go && connected && now - lastSnap >= SnapshotInterval)
                        {
                            lastSnap = now;
                            snapshotsSent++;
                            Vector3 pose = simulated.transform.position;
                            link.Send("ISNAP|" + runId + "|" + session + "|" + Epoch + "|2|"
                                + snapshotsSent + "|" + Fmt(pose.x) + "|" + Fmt(pose.y) + "|" + Fmt(pose.z));
                        }
                        if (done)
                        {
                            if (!ackSent)
                            {
                                ackSent = true;
                                link.Send("INPUT_ACK|" + runId + "|" + session + "|1");
                                endAt = now + 0.5f;
                            }
                            else if (now - lastAck > 0.1f)
                            {
                                lastAck = now;
                                link.Send("INPUT_ACK|" + runId + "|" + session + "|1");
                            }
                        }
                    }

                    if (!host && go)
                    {
                        if (now >= moveStart && now < moveEnd)
                        {
                            if (now - lastSend >= InputInterval)
                            {
                                lastSend = now;
                                SendInput(link, runId, session, ++seq, 0f, 1f, moveYaw);
                                sent++;
                            }
                        }
                        else if (now >= silenceEnd && now < staleDue)
                        {
                            if (now - lastSend >= InputInterval)
                            {
                                lastSend = now;
                                SendInput(link, runId, session, ++seq, 0f, 0f, moveYaw);
                                sent++;
                            }
                        }
                        else if (!staleSent && now >= staleDue)
                        {
                            staleSent = true;
                            SendInput(link, runId, session, 1, 0f, -1f, moveYaw);
                        }
                        else if (staleSent && now >= staleDue + 0.4f && now < doneDue)
                        {
                            if (!sentControls)
                            {
                                sentControls = true;
                                link.Send("INPUT|" + runId + "|" + session + "|" + Epoch
                                    + "|1|" + (seq + 1) + "|0|1|" + Fmt(moveYaw));
                                link.Send("INPUT|" + runId + "|" + session + "|" + Epoch
                                    + "|2|" + (seq + 2) + "|nan|0|" + Fmt(moveYaw));
                            }
                            if (now - lastSend >= InputInterval)
                            {
                                lastSend = now;
                                SendInput(link, runId, session, ++seq, 0f, 0f, moveYaw);
                                sent++;
                            }
                        }
                        else if (!doneSent && now >= doneDue)
                        {
                            doneSent = true;
                            link.Send("INPUT_DONE|" + runId + "|" + session + "|1");
                        }

                        if (snapshotNegative && now >= moveStart && now < moveEnd)
                        {
                            // Mutation: ignore host snapshots and run an
                            // independent local move opposite the host intent.
                            Vector3 opposite = Quaternion.Euler(0f, moveYaw + 180f, 0f) * Vector3.forward;
                            Vector3 axes = CameraController.instance.relativeControlSpot.InverseTransformDirection(opposite);
                            Vector2 localMove = new Vector2(axes.x, axes.z);
                            if (localMove.sqrMagnitude < 0.01f) localMove = new Vector2(0f, -1f);
                            else localMove.Normalize();
                            simulatedSeat.Move = localMove;
                        }
                        else if (snapshotNegative)
                        {
                            simulatedSeat.Move = Vector2.zero;
                        }
                        if (readyAt < 0f && connected) readyAt = now;
                    }
                    if (host && readyAt < 0f && ready) readyAt = now;
                    if (readyAt > 0f && endAt < 0f && now - readyAt > 45f) endAt = now;
                    yield return null;
                }
            }
            finally
            {
                if (simulatedSeat != null) simulatedSeat.Move = Vector2.zero;
                if (idleSeat != null) idleSeat.Move = Vector2.zero;
                link.Close();
                // QA account-write guards intentionally survive shutdown.
            }

            if (host) ack = ackSent;
            else done = ack;
            float remoteDelta = host ? XZDistance(simulatedStartPos, simulated.transform.position) : 0f;
            float forwardDelta = host ? Mathf.Max(0f, Vector3.Dot(
                simulated.transform.position - simulatedStartPos, moveDir)) : 0f;
            float clearance = host ? initialClearance : 0f;
            float localDelta = !host && haveHostPose
                ? XZDistance(guestStartPos, simulated.transform.position) : 0f;
            float hostDelta = !host && haveHostPose ? XZDistance(firstHostPos, lastHostPos) : 0f;
            float poseError = !host && haveHostPose ? XZDistance(simulated.transform.position, lastHostPos) : 0f;
            bool hostCommon = connected && ready && go && done && ackSent;
            bool guestCommon = connected && ready && go && ack;
            bool hostOk;
            if (host)
            {
                // Deliberate host mutations must fail this client; their
                // observable fields let the scorer verify the intended bug.
                if (inputDrop || staleNegative || timeoutNegative)
                    hostOk = false;
                else
                    hostOk = hostCommon && appliedInputs >= 20 && movedInputs >= 20
                        && remoteDelta > 0.8f && forwardDelta > 0.8f
                        && staleRejected && !staleApplied && staleBackward < 0.05f
                        && timeoutCleared && postStopDrift < 0.5f
                        && snapshotsSent >= 20 && rejected >= 2 && actors == 2;
            }
            else
            { hostOk = false; }
            bool guestOk;
            if (!host)
            {
                if (snapshotNegative)
                    guestOk = false; // Deliberate guest mutation; scorer checks its fields.
                else
                    guestOk = guestCommon && sent >= 20 && snapshotsApplied >= 20
                        && snapshotsIgnored == 0 && snapshotsReceived >= 20
                        && poseError <= 0.05f && actors == 2
                        && (snapshotNegative || Mathf.Abs(localDelta - hostDelta) <= 0.1f);
            }
            else
            { guestOk = false; }
            bool pass = host ? hostOk : guestOk;
            // Host mutations are deliberately failing clients; the scorer
            // expects those failures. A healthy host must still report PASS
            // for the guest-side snapshot mutation.
            plugin.Report("GMP_INPUT_RESULT " + (pass ? "PASS" : "FAIL") + " run=" + runId
                + " role=" + role + " session=" + (session ?? "none") + " build=" + build
                + " scene=" + scene + " floor=" + floor + " geometry=" + geometry
                + " connected=" + connected + " ready=" + ready + " go=" + go
                + " done=" + done + " ack=" + ack
                + " applied_inputs=" + appliedInputs + " moved_inputs=" + movedInputs
                + " rejected=" + rejected + " stale_rejected=" + staleRejected
                + " stale_applied=" + staleApplied + " timeout_cleared=" + timeoutCleared
                + " remote_delta=" + Fmt(remoteDelta) + " forward_delta=" + Fmt(forwardDelta)
                + " stale_backward=" + Fmt(staleBackward) + " post_stop_drift=" + Fmt(postStopDrift)
                + " snapshots_sent=" + snapshotsSent + " sent=" + sent
                + " snapshots_received=" + snapshotsReceived + " snapshots_applied=" + snapshotsApplied
                + " snapshots_ignored=" + snapshotsIgnored + " snapshots_rejected=" + snapshotsRejected
                + " local_delta=" + Fmt(localDelta) + " host_delta=" + Fmt(hostDelta)
                + " pose_error=" + Fmt(poseError) + " clearance=" + Fmt(clearance)
                + " actors=" + actors + " input_drop=" + inputDrop
                + " stale_negative=" + staleNegative + " timeout_negative=" + timeoutNegative
                + " snapshot_negative=" + snapshotNegative);
            Application.Quit(pass ? 0 : 1);
        }

        private static void SendInput(UdpTestLink link, string runId, string session,
            int sequence, float h, float v, float yaw)
        {
            link.Send("INPUT|" + runId + "|" + session + "|" + Epoch + "|2|" + sequence
                + "|" + Fmt(h) + "|" + Fmt(v) + "|" + Fmt(yaw));
        }

        private static void ParkIdle(Player idle)
        {
            idle.SetPhysicalCollidersActive(false);
            if (idle.rb) idle.rb.isKinematic = true;
            // A live Player re-enables its colliders in FixedUpdate, which can
            // shove the remotely simulated actor. Disable the idle actor only.
            idle.enabled = false;
        }

        private static void FindSafeSpawn(Player actor, Player other)
        {
            Vector3 origin = actor.transform.position;
            for (int ring = 0; ring <= 6; ring++)
            {
                for (int direction = 0; direction < (ring == 0 ? 1 : 8); direction++)
                {
                    float angle = direction * Mathf.PI / 4f;
                    Vector3 candidate = origin + new Vector3(Mathf.Cos(angle), 0f,
                        Mathf.Sin(angle)) * (ring * 0.6f);
                    RaycastHit ground;
                    if (!Physics.Raycast(candidate + Vector3.up * 2f, Vector3.down, out ground,
                        4f, GlobalSettings.defaults.groundLayers, QueryTriggerInteraction.Ignore)
                        || ground.normal.y < 0.8f
                        || Mathf.Abs(ground.point.y - origin.y) > 1.5f) continue;
                    bool blocked = false;
                    foreach (Collider hit in Physics.OverlapCapsule(ground.point + Vector3.up * 0.6f,
                        ground.point + Vector3.up * 1.6f, 0.4f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.transform.IsChildOf(actor.transform)
                            || hit.transform.IsChildOf(other.transform)) continue;
                        blocked = true;
                        break;
                    }
                    if (blocked) continue;
                    actor.transform.position = ground.point;
                    if (actor.rb)
                    {
                        actor.rb.position = ground.point;
                        actor.rb.velocity = Vector3.zero;
                    }
                    if (ring > 0)
                        pluginRef.Report("GMP_INPUT_SAFE_SPAWN ring=" + ring
                            + " dir=" + direction + " pos=" + Fmt(ground.point.x)
                            + "," + Fmt(ground.point.z));
                    return;
                }
            }
            throw new InvalidOperationException("No unblocked remote-actor spawn found");
        }

        private static void SnapToGround(Player player)
        {
            if (!player) return;
            RaycastHit hit;
            if (!Physics.Raycast(player.transform.position + Vector3.up * 1.5f, Vector3.down,
                out hit, 4f, GlobalSettings.defaults.groundLayers, QueryTriggerInteraction.Ignore))
                return;
            player.transform.position = hit.point;
            if (player.rb)
            {
                player.rb.position = hit.point;
                player.rb.velocity = Vector3.zero;
            }
        }

        private static void Align(Player player, Vector3 pose)
        {
            player.transform.position = pose;
            if (player.rb)
            {
                player.rb.position = pose;
                player.rb.velocity = Vector3.zero;
            }
        }

        private static void DismissIntro()
        {
            PixelCrushers.DialogueSystem.DialogueManager.instance.StopConversation();
            Player local = Player.singlePlayer;
            if (local) local.CutsceneEnded();
            foreach (LocalSeats.Seat seat in LocalSeats.Seats.Values)
                if (seat.Player) seat.Player.CutsceneEnded();
            CameraController.instance.StopCutscene(snap: true);
        }

        private static Vector3 ChooseDirection(Player actor, Player other, Room room)
        {
            Vector3 origin = actor.transform.position;
            float[] angles = { 0f, 90f, 180f, 270f, 45f, 135f, 225f, 315f };
            foreach (float distance in new[] { 4f, 3f, 2.2f })
            {
                foreach (float angle in angles)
                {
                    Vector3 dir = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0f,
                        Mathf.Sin(angle * Mathf.Deg2Rad));
                    if (!room.PointWithinBoundaries(origin + dir * distance, 0.5f)) continue;
                    if (Clearance(actor, other, dir, distance) < distance - 0.01f) continue;
                    pluginRef.Report("GMP_INPUT_FIXTURE_DIR dir=" + Fmt(dir.x) + "," + Fmt(dir.z)
                        + " distance=" + Fmt(distance));
                    return dir;
                }
            }
            throw new InvalidOperationException("No clear native-movement direction found");
        }

        private static float Clearance(Player actor, Player other, Vector3 dir, float distance)
        {
            Vector3 origin = actor.transform.position;
            float cleared = 0f;
            for (float d = 0.6f; d <= distance; d += 0.3f)
            {
                Vector3 p = origin + dir * d;
                RaycastHit ground;
                if (!Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out ground, 4f,
                    GlobalSettings.defaults.groundLayers, QueryTriggerInteraction.Ignore)
                    || ground.normal.y < 0.8f || Mathf.Abs(ground.point.y - origin.y) > 1.2f)
                    return cleared;
                foreach (Collider hit in Physics.OverlapCapsule(p + Vector3.up * 0.3f,
                    p + Vector3.up * 1.1f, 0.3f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.transform.IsChildOf(actor.transform) || hit.transform.IsChildOf(other.transform))
                        continue;
                    return cleared;
                }
                cleared = d;
            }
            return cleared;
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
