using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace GoingMultiplayer
{
    // O4a target tests plus optional O4b native host movement and guarded mirror
    // creation. One authored fixture enemy only: not combat, shared world
    // generation, general enemy support or a production session.
    internal static class NetAiProbe
    {
        private static Enemy mirror;
        private static bool bypass;
        private static int blocked;
        private static readonly FieldInfo RawTarget = AccessTools.Field(typeof(Enemy), "_aggroTarget");

        private static bool SkipAccountMutation() { return false; }
        private static bool AllowTarget(Enemy __instance)
        {
            if (__instance != mirror || bypass) return true;
            blocked++;
            return false;
        }

        internal static IEnumerator Run(Plugin plugin, string runId, string role, int port,
            bool aiNegative, bool targetNegative, bool brain = false,
            bool poseNegative = false, bool simNegative = false)
        {
            bool host = role == "host";
            string build;
            using (var hash = SHA256.Create())
                build = BitConverter.ToString(hash.ComputeHash(
                    System.IO.File.ReadAllBytes(typeof(Plugin).Assembly.Location)))
                    .Replace("-", "").ToLowerInvariant();
            var patches = new Harmony("goingmultiplayer.qa-net-ai");
            foreach (string name in new[] { "TestAchievementComplete", "TestStatIncremented", "ResetCheevos" })
                patches.Patch(AccessTools.Method(typeof(SteamManager), name),
                    prefix: new HarmonyMethod(typeof(NetAiProbe), "SkipAccountMutation"));
            patches.Patch(AccessTools.Method(typeof(Enemy), "GetNewAggroTarget"),
                prefix: new HarmonyMethod(typeof(NetAiProbe), "AllowTarget"));
            LocalSeats.Install();
            yield return NetProbe.LoadOffice(plugin);
            yield return DungeonEntry.EnterFromOffice(plugin);
            GameManager.instance.isPaused = false;
            Time.timeScale = 1f;
            yield return new WaitForSeconds(3f);
            Player local = Player.singlePlayer;
            LocalSeats.ScriptLocalInput(local);
            LocalSeats.Seat other = LocalSeats.Spawn(local, SeatProbe.FindSpawn(local), true);
            Player p1 = host ? local : other.Player;
            Player p2 = host ? other.Player : local;
            Freeze(p1);
            Freeze(p2);
            Room room = DungeonManager.currentRoom;
            string geometry = brain ? RoomFixture.Fingerprint(room) : "target-only";
            if (brain) EnemyMirror.Install();
            string scene = DungeonEntry.SelectedScene;
            int floor = DungeonManager.instance.currentFloor;
            string session = host ? Guid.NewGuid().ToString("N") : null;
            GameObject prefab = host ? TargetProbe.FindEnemyPrefab(room) : null;
            Enemy enemy = null;
            Vector3 pos1 = local.transform.position;
            Vector3 pos2 = SeatProbe.FindSpawn(local);
            Vector3 direction = Util.ZeroY(pos2 - pos1).normalized;
            if (direction.sqrMagnitude < 0.1f) direction = Vector3.forward;
            Vector3 near2 = pos2 + direction * 0.5f;
            Vector3 near1 = pos1 - direction * 0.5f;
            int phase = -1, observed = 0, samples = 0, lastSampleFrame = -1;
            bool acquired = false, swapped = false, frozen = true, targets = true;
            Vector3 heldEnemy = Vector3.zero;
            int heldTarget = 0;
            float maxDrift = 0f;
            float lastSend = -1f;
            float phaseStart = 0f;
            float doneAt = -1f;
            bool connected = false;
            int finalPhase = brain ? 4 : 2;
            int stateSeq = 0, lastStateSeq = -1, movingSamples = 0;
            float moved = 0f, approach = 0f, poseError = 0f, freezeDrift = 0f;
            Vector3 motionStart = Vector3.zero;
            float initialDistance = 0f;
            bool hostBrain = false, mirrorQuiet = true;
            string replicatedState = "Inactive";
            blocked = 0;
            bypass = aiNegative;
            var link = new UdpTestLink();
            if (host) link.StartHost(port); else link.StartGuest(port);
            plugin.Report("GMP_AI_STAGE ready role=" + role + " scene=" + scene + " floor=" + floor
                + " geometry=" + geometry + " brain=" + brain);
            float deadline = Time.realtimeSinceStartup + 90f;
            try
            {
                while (Time.realtimeSinceStartup < deadline)
                {
                    float now = Time.realtimeSinceStartup;
                    if (doneAt >= 0f && now >= doneAt) break;
                    string message;
                    System.Net.IPEndPoint from;
                    while (link.TryReceive(out message, out from))
                    {
                        string[] parts = message.Split('|');
                        if (host && parts.Length == 6 && parts[0] == "AI_HELLO"
                            && parts[1] == runId && parts[2] == build && parts[3] == scene
                            && parts[4] == floor.ToString(CultureInfo.InvariantCulture) && parts[5] == geometry)
                        {
                            link.SetPeer(from);
                            connected = true;
                        }
                        else if (!host && parts.Length == 8 && parts[0] == "AI_WELCOME"
                            && parts[1] == runId && parts[3] == build && parts[4] == scene
                            && parts[5] == floor.ToString(CultureInfo.InvariantCulture) && parts[7] == geometry)
                        {
                            session = parts[2];
                            connected = true;
                            if (!enemy)
                            {
                                prefab = TargetProbe.FindEnemyPrefab(room, parts[6]);
                                enemy = brain ? EnemyMirror.Spawn(room, prefab, local.transform.position)
                                    : room.NewEnemy(prefab, local.transform.position, -1f);
                                if (!enemy) throw new InvalidOperationException("Mirror spawn rejected");
                                mirror = enemy;
                                // The legacy target-only path disables after spawn;
                                // brain mode adds entry guards before instantiation.
                                // Neither certifies arbitrary child/manager tasks.
                                enemy.Active = false;
                                enemy.enabled = false;
                                Freeze(enemy);
                                plugin.Report("GMP_BRAIN_PREFAB role=" + role + " prefab=" + prefab.name
                                    + " type=" + enemy.GetType().Name);
                                if (brain)
                                {
                                    // Exercise both direct entry points before state
                                    // application. Both must remain suppressed.
                                    enemy.Activate(0f);
                                    enemy.StartThonk();
                                    mirrorQuiet &= !enemy.Active && !enemy.enabled && !enemy.hasBrain;
                                }
                            }
                        }
                        else if (!host && enemy && parts.Length == 20 && parts[0] == "AI_STATE"
                            && parts[1] == runId && parts[2] == session && parts[3] == "1" && parts[19] == "1")
                        {
                            int incoming = int.Parse(parts[4], CultureInfo.InvariantCulture);
                            int incomingSeq = int.Parse(parts[17], CultureInfo.InvariantCulture);
                            if (incomingSeq <= lastStateSeq || incoming > finalPhase
                                || (incoming != phase + 1 && !(brain && incoming == 3 && phase == 3))) continue;
                            lastStateSeq = incomingSeq;
                            // Only apply once per phase: repeated snapshots must not
                            // hide independent drift or local target recomputation.
                            phase = incoming;
                            SetPosition(p1, ReadPosition(parts, 5));
                            SetPosition(p2, ReadPosition(parts, 8));
                            heldEnemy = ReadPosition(parts, 11);
                            if (!(brain && phase == 3 && poseNegative)) SetPosition(enemy, heldEnemy);
                            heldTarget = int.Parse(parts[14], CultureInfo.InvariantCulture);
                            if (!targetNegative) enemy.aggroTarget = heldTarget == 2 ? p2 : p1;
                            acquired = parts[15] == "True";
                            swapped = parts[16] == "True";
                            replicatedState = parts[18];
                            samples = 0;
                            phaseStart = now;
                            lastSampleFrame = -1;
                            if (brain && phase == 3)
                            {
                                mirrorQuiet &= !enemy.Active && !enemy.enabled && !enemy.hasBrain
                                    && EnemyMirror.AllowedStarts == 0;
                                movingSamples++;
                                poseError = Mathf.Max(poseError, Vector3.Distance(enemy.transform.position, heldEnemy));
                                plugin.Report("GMP_BRAIN_SAMPLE role=guest seq=" + incomingSeq
                                    + " target=" + TargetId(enemy, p1, p2) + " state=" + replicatedState
                                    + " pose=" + Fmt(enemy.transform.position.x) + ","
                                    + Fmt(enemy.transform.position.y) + "," + Fmt(enemy.transform.position.z));
                                if (movingSamples == 1) motionStart = enemy.transform.position;
                                moved = Mathf.Max(moved, XZDistance(motionStart, enemy.transform.position));
                            }
                            if (brain && phase == 4 && simNegative)
                            {
                                EnemyMirror.AllowSimulation = true;
                                enemy.hasBrain = true;
                                enemy.enabled = true;
                                if (enemy.rb) enemy.rb.isKinematic = false;
                                enemy.Activate(0f);
                                enemy.StartThonk();
                            }
                        }
                        else if (host && parts.Length == 5 && parts[0] == "AI_ACK"
                            && parts[1] == runId && parts[2] == session
                            && parts[3] == "1" && parts[4] == phase.ToString(CultureInfo.InvariantCulture))
                        {
                            observed |= 1 << phase;
                            if (phase == finalPhase)
                            {
                                if (doneAt < 0f) doneAt = now + 2f;
                            }
                            else if (phase != 3) { phase++; phaseStart = -1f; }
                        }
                    }
                    if (host && connected && phase == -1) { phase = 0; phaseStart = -1f; }
                    if (host && phase >= 0 && phaseStart < 0f)
                    {
                        SetPosition(p1, pos1);
                        SetPosition(p2, pos2);
                        if (phase == 0)
                        {
                            enemy = room.NewEnemy(prefab, near2, -1f);
                            if (!enemy) throw new InvalidOperationException("Host enemy spawn rejected");
                            Freeze(enemy);
                            plugin.Report("GMP_BRAIN_PREFAB role=" + role + " prefab=" + prefab.name
                                + " type=" + enemy.GetType().Name);
                            enemy.aggroTarget = null;
                            DungeonManager.currentRoom = room;
                            enemy.Activate(0f); // Native activation must acquire P2, not a forced target.
                            float until = Time.realtimeSinceStartup + 6f;
                            while (!enemy.Active && Time.realtimeSinceStartup < until)
                            {
                                SetPosition(p1, pos1);
                                SetPosition(p2, pos2);
                                SetPosition(enemy, near2);
                                yield return null;
                            }
                            acquired = enemy.Active && (Entity)RawTarget.GetValue(enemy) == p2;
                            enemy.Active = false;
                            enemy.enabled = false;
                            SetPosition(enemy, near2);
                        }
                        else if (phase == 1)
                        {
                            SetPosition(enemy, near1);
                            enemy.GetNewAggroTarget();
                            swapped = (Entity)RawTarget.GetValue(enemy) == p1;
                        }
                        else if (phase == 2) SetPosition(enemy, near2); // Native sticky/frozen target.
                        else if (phase == 3)
                        {
                            // Prepare once; the probe must not move the enemy
                            // during the native brain's measurement interval.
                            Vector3 center = (room.minBound + room.maxBound) * 0.5f;
                            center.y = pos2.y;
                            Vector3 size = room.maxBound - room.minBound;
                            Vector3 axis = size.x >= size.z ? Vector3.right : Vector3.forward;
                            float extent = Mathf.Min((Mathf.Max(size.x, size.z) * 0.5f) - 1f, 5f);
                            if (extent < 3f) throw new InvalidOperationException("Room too small for native chase fixture");
                            pos1 = center - axis * extent;
                            pos2 = center + axis * extent * 0.65f;
                            SetPosition(p1, pos1);
                            SetPosition(p2, pos2);
                            enemy.StopThonk();
                            SetPosition(enemy, center);
                            motionStart = center;
                            initialDistance = XZDistance(center, pos2);
                            enemy.aggroTarget = null;
                            enemy.GetNewAggroTarget();
                            enemy.hasBrain = true;
                            enemy.SetPhysicalCollidersActive(true);
                            enemy.rb.isKinematic = false;
                            enemy.enabled = true;
                            EnemyMirror.ObservedHost = enemy;
                            enemy.Active = true;
                        }
                        else
                        {
                            enemy.StopThonk();
                            enemy.Active = false;
                            enemy.enabled = false;
                            Freeze(enemy);
                            heldEnemy = enemy.transform.position;
                        }
                        phaseStart = Time.realtimeSinceStartup;
                        plugin.Report("GMP_AI_HOST_PHASE phase=" + phase
                            + " target=" + TargetId(enemy, p1, p2) + " acquired=" + acquired
                            + " swapped=" + swapped);
                    }
                    if (brain && host && phase == 3)
                    {
                        moved = Mathf.Max(moved, XZDistance(motionStart, enemy.transform.position));
                        approach = Mathf.Max(approach, initialDistance - XZDistance(enemy.transform.position, pos2));
                        hostBrain |= EnemyMirror.HostStarts > 0 && enemy.Active && enemy.enabled
                            && NativeState(enemy) != "Inactive" && TargetId(enemy, p1, p2) == 2;
                        if (now - phaseStart >= 6f)
                        {
                            phase = 4;
                            phaseStart = -1f;
                            yield return null;
                            continue;
                        }
                    }
                    if (brain && host && phase == 4)
                        freezeDrift = Mathf.Max(freezeDrift, Vector3.Distance(heldEnemy, enemy.transform.position));
                    if (!host && phase >= 0 && phase != 3
                        && (samples < (brain && phase == 4 ? 60 : 20)
                            || (brain && phase == 4 && now - phaseStart < 2f))
                        && Time.frameCount != lastSampleFrame)
                    {
                        lastSampleFrame = Time.frameCount;
                        // Exercise the game's real method, not a fake AI counter.
                        // In phase 2 a broken veto changes the host's sticky P1
                        // target to the physically nearer P2.
                        if (phase <= 2) enemy.GetNewAggroTarget();
                        int actual = TargetId(enemy, p1, p2);
                        targets &= actual == heldTarget && actual == (phase == 0 || phase >= 3 ? 2 : 1);
                        float drift = Vector3.Distance(enemy.transform.position, heldEnemy);
                        if (phase <= 2)
                        {
                            maxDrift = Mathf.Max(maxDrift, drift);
                            frozen &= drift < 0.05f;
                        }
                        else
                        {
                            freezeDrift = Mathf.Max(freezeDrift, drift);
                            mirrorQuiet &= !enemy.Active && !enemy.enabled && !enemy.hasBrain
                                && EnemyMirror.AllowedStarts == 0;
                        }
                        samples++;
                        if (samples == 20)
                            plugin.Report("GMP_AI_GUEST_PHASE phase=" + phase + " target=" + actual
                                + " expected=" + heldTarget + " targets=" + targets
                                + " blocked=" + blocked + " drift=" + Fmt(maxDrift));
                    }
                    if (!host && phase >= 0 && phase != 3
                        && samples >= (brain && phase == 4 ? 60 : 20)
                        && now - phaseStart >= (brain && phase == 4 ? 2f : 0.5f))
                        observed |= 1 << phase;
                    if (!host && phase == 3 && movingSamples >= 20) observed |= 1 << phase;
                    if (host && brain && phase == 3) observed |= 1 << phase;
                    if (now - lastSend >= 0.05f)
                    {
                        lastSend = now;
                        if (!host && !connected)
                            link.Send("AI_HELLO|" + runId + "|" + build + "|" + scene + "|" + floor + "|" + geometry);
                        if (host && connected)
                        {
                            link.Send("AI_WELCOME|" + runId + "|" + session + "|" + build
                                + "|" + scene + "|" + floor + "|" + prefab.name + "|" + geometry);
                            if (phase >= 0)
                                link.Send("AI_STATE|" + runId + "|" + session + "|1|" + phase
                                    + Position(pos1) + Position(pos2) + Position(enemy.transform.position)
                                    + "|" + TargetId(enemy, p1, p2) + "|" + acquired + "|" + swapped
                                    + "|" + (++stateSeq) + "|" + NativeState(enemy) + "|1");
                            if (brain && phase == 3)
                            {
                                movingSamples++;
                                plugin.Report("GMP_BRAIN_SAMPLE role=host seq=" + stateSeq
                                    + " target=" + TargetId(enemy, p1, p2) + " state=" + NativeState(enemy)
                                    + " pose=" + Fmt(enemy.transform.position.x) + ","
                                    + Fmt(enemy.transform.position.y) + "," + Fmt(enemy.transform.position.z));
                            }
                        }
                        if (!host && phase >= 0 && (observed & (1 << phase)) != 0)
                        {
                            link.Send("AI_ACK|" + runId + "|" + session + "|1|" + phase);
                            if (phase == finalPhase && doneAt < 0f) doneAt = now + 1f;
                        }
                    }
                    yield return null;
                }
                bool brainOk = !brain || (host ? hostBrain && moved > 0.5f && approach > 0.5f && freezeDrift < 0.05f
                    : movingSamples >= 20 && moved > 0.5f && poseError < 0.05f
                        && freezeDrift < 0.05f && mirrorQuiet && EnemyMirror.Blocked >= 4);
                bool pass = connected && observed == (brain ? 31 : 7) && acquired && swapped && brainOk
                    && (host || (targets && frozen && blocked >= 60));
                plugin.Report("GMP_AI_RESULT " + (pass ? "PASS" : "FAIL")
                    + " run=" + runId + " role=" + role + " session=" + (session ?? "none")
                    + " build=" + build + " scene=" + scene + " floor=" + floor
                    + " phases=" + observed + " acquired=" + acquired + " swapped=" + swapped
                    + " targets=" + targets + " frozen=" + frozen + " blocked=" + blocked
                    + " drift=" + Fmt(maxDrift) + " ai_negative=" + aiNegative
                    + " target_negative=" + targetNegative);
                if (brain)
                    plugin.Report("GMP_BRAIN_RESULT run=" + runId + " role=" + role
                        + " geometry=" + geometry + " enemy_id=1 authority=1 epoch=1"
                        + " moved=" + Fmt(moved) + " approach=" + Fmt(approach)
                        + " samples=" + movingSamples + " pose_error=" + Fmt(poseError)
                        + " freeze_drift=" + Fmt(freezeDrift) + " host_brain=" + hostBrain
                        + " mirror_quiet=" + mirrorQuiet + " activation_blocks=" + EnemyMirror.Blocked
                        + " starts=" + (host ? EnemyMirror.HostStarts : EnemyMirror.AllowedStarts)
                        + " pose_negative=" + poseNegative + " sim_negative=" + simNegative);
                Application.Quit(pass ? 0 : 1);
            }
            finally
            {
                link.Close();
                mirror = null;
                bypass = false;
                // Application.Quit is asynchronous. Keep account-write guards
                // installed through the remaining shutdown callbacks; this
                // opt-in probe never resumes normal play in this process.
            }
        }

        private static float XZDistance(Vector3 a, Vector3 b) { return Util.ZeroY(a - b).magnitude; }

        private static string NativeState(Enemy enemy)
        {
            if (!enemy.Active) return "Inactive";
            var yupman = enemy as Yupman;
            if (yupman) return yupman.state.ToString();
            var underling = enemy as Underling;
            if (underling && underling.CurrentState != null) return underling.CurrentState.routineName;
            return "Active";
        }

        private static int TargetId(Enemy enemy, Player p1, Player p2)
        {
            Entity target = (Entity)RawTarget.GetValue(enemy);
            return target == p1 ? 1 : target == p2 ? 2 : 0;
        }

        private static void Freeze(Character actor)
        {
            actor.SetPhysicalCollidersActive(false);
            if (actor.rb)
            {
                actor.rb.velocity = Vector3.zero;
                actor.rb.isKinematic = true;
            }
        }

        private static void SetPosition(Character actor, Vector3 pose)
        {
            actor.transform.position = pose;
            if (actor.rb) actor.rb.position = pose;
        }

        private static string Position(Vector3 pose)
        {
            return "|" + Fmt(pose.x) + "|" + Fmt(pose.y) + "|" + Fmt(pose.z);
        }

        private static Vector3 ReadPosition(string[] parts, int start)
        {
            return new Vector3(float.Parse(parts[start], CultureInfo.InvariantCulture),
                float.Parse(parts[start + 1], CultureInfo.InvariantCulture),
                float.Parse(parts[start + 2], CultureInfo.InvariantCulture));
        }

        private static string Fmt(float value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
    }
}
