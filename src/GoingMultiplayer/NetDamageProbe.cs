using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace GoingMultiplayer
{
    // O4c (IN PROGRESS, NOT PASSING): one authored attack, native damage on the
    // host, and exactly-once health-delta application on the guest. The host-side
    // attack fixture does not yet produce a verified hit; see docs/O4C-COMBAT.md.
    // Not wired into verify.py.
    internal static class NetDamageProbe
    {
        private static Enemy enemy;
        private static Player p1, p2;
        private static bool host;
        private static Plugin pluginRef;
        private static int pulses, hits, nativeTarget, selectedTarget;
        private static float amount;
        private static string sourceType = "none";
        private static readonly FieldInfo Aggro = AccessTools.Field(typeof(Enemy), "_aggroTarget");

        private static bool SkipAccountMutation() { return false; }

        private static int explosionsSeen;

        private static void ObserveExplosion(GameObject prefab, Entity source, Entity attacker)
        {
            explosionsSeen++;
            if (explosionsSeen <= 5 && pluginRef)
                pluginRef.Report("GMP_DAMAGE_EXPLOSION seen=" + explosionsSeen
                    + " source=" + (source ? source.name : "null")
                    + " attacker=" + (attacker ? attacker.name : "null"));
            if (enemy && source == enemy && attacker == enemy) pulses++;
        }

        private static bool BeforeDamage(Player __instance, out float __state)
        {
            __state = __instance.health;
            // In this fixture the guest may only apply authenticated host
            // deltas, never damage from its independently running world.
            return host || (__instance != p1 && __instance != p2);
        }

        private static void AfterDamage(Player __instance, HitInfo info, float __state)
        {
            if (!host || !enemy || info.attacker != enemy
                || (__instance != p1 && __instance != p2) || __instance.health >= __state) return;
            hits++;
            nativeTarget = __instance == p2 ? 2 : 1;
            selectedTarget = (Entity)Aggro.GetValue(enemy) == p2 ? 2 : 1;
            amount = __state - __instance.health;
            sourceType = info.sourceType.ToString();
            StopEnemy(); // One native attack only; do not manufacture a HitInfo.
        }

        private static void StopEnemy()
        {
            if (!enemy) return;
            foreach (var node in enemy.GetComponentsInChildren<CubicleGroundBotElectricNode>(true))
                node.gameObject.SetActive(false);
            enemy.StopThonk();
            enemy.Active = false;
            enemy.enabled = false;
            enemy.SetPhysicalCollidersActive(false);
            if (enemy.rb) { enemy.rb.velocity = Vector3.zero; enemy.rb.isKinematic = true; }
        }

        internal static IEnumerator Run(Plugin plugin, string runId, string role, int port,
            bool targetNegative, bool duplicateNegative)
        {
            host = role == "host";
            pluginRef = plugin;
            explosionsSeen = 0;
            string build;
            using (var hash = SHA256.Create())
                build = BitConverter.ToString(hash.ComputeHash(
                    System.IO.File.ReadAllBytes(typeof(Plugin).Assembly.Location)))
                    .Replace("-", "").ToLowerInvariant();
            var patches = new Harmony("goingmultiplayer.qa-net-damage");
            foreach (string name in new[] { "TestAchievementComplete", "TestStatIncremented", "ResetCheevos" })
                patches.Patch(AccessTools.Method(typeof(SteamManager), name),
                    prefix: new HarmonyMethod(typeof(NetDamageProbe), "SkipAccountMutation"));
            patches.Patch(AccessTools.Method(typeof(Player), "TakeDamage",
                    new[] { typeof(HitInfo), typeof(bool), typeof(int) }),
                prefix: new HarmonyMethod(typeof(NetDamageProbe), "BeforeDamage"),
                postfix: new HarmonyMethod(typeof(NetDamageProbe), "AfterDamage"));
            patches.Patch(AccessTools.Method(typeof(Explosion), "Create"),
                prefix: new HarmonyMethod(typeof(NetDamageProbe), "ObserveExplosion"));
            LocalSeats.Install();
            yield return NetProbe.LoadOffice(plugin);
            yield return DungeonEntry.EnterFromOffice(plugin);
            GameManager.instance.isPaused = false;
            Time.timeScale = 1f;
            yield return new WaitForSeconds(3f);
            Player local = Player.singlePlayer;
            LocalSeats.ScriptLocalInput(local);
            var other = LocalSeats.Spawn(local, SeatProbe.FindSpawn(local), true);
            p1 = host ? local : other.Player;
            p2 = host ? other.Player : local;
            foreach (Player player in new[] { p1, p2 })
            {
                player.MaxHealth = 10f;
                player.health = 10f;
                player.overHealth = 0f;
                player.armor = 0f;
                player.rb.velocity = Vector3.zero;
                player.rb.isKinematic = true;
                player.SetPhysicalCollidersActive(host);
            }
            Room room = DungeonManager.currentRoom;
            if (host && DungeonManager.CURRENT_SETTINGS)
            {
                foreach (var data in DungeonManager.CURRENT_SETTINGS.GetPossibleEnemies(
                    DungeonManager.instance.currentFloor, 0))
                {
                    if (data == null) continue;
                    var candidate = data.GetPrefab(room);
                    if (!candidate) continue;
                    var bot = candidate.GetComponent<Enemy>();
                    string kinds = "";
                    foreach (var n in candidate.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (n is iCubicleNode) kinds += n.GetType().Name + ",";
                        if (n is Hitbox) kinds += "Hitbox,";
                    }
                    plugin.Report("GMP_DAMAGE_PREFAB name=" + candidate.name + " enemy="
                        + (bot ? bot.GetType().Name : "none") + " nodes=" + kinds);
                }
            }
            plugin.Report("GMP_DAMAGE_ROOM name=" + room.name + " dialogue_active="
                + PixelCrushers.DialogueSystem.DialogueManager.instance.isConversationActive);
            string geometry = RoomFixture.Fingerprint(room);
            GameObject prefab = FindBladeBot(room);
            if (!prefab || !prefab.GetComponentInChildren<Hitbox>(true))
                throw new InvalidOperationException("Expected an authored hitbox enemy fixture");
            // Keep P1 at its own spawn point so a proximity conversation is not
            // triggered by a teleport. The bot swings next to the remote actor.
            Vector3 pos1 = local.transform.position;
            Vector3 axis = room.maxBound.x - room.minBound.x >= room.maxBound.z - room.minBound.z
                ? Vector3.right : Vector3.forward;
            Vector3 pos2 = pos1 + axis * 6f;
            Vector3 enemyPos = pos2 + axis * 0.6f;
            if (!room.PointWithinBoundaries(pos2, 0.5f) || !room.PointWithinBoundaries(enemyPos, 0.5f))
            {
                axis = -axis;
                pos2 = pos1 - axis * 6f;
                enemyPos = pos2 - axis * 0.6f;
            }
            if (!room.PointWithinBoundaries(pos2, 0.5f) || !room.PointWithinBoundaries(enemyPos, 0.5f)
                || Util.ZeroY(pos1 - pos2).magnitude < 4f)
                throw new InvalidOperationException("Attack fixture cannot separate P1 from the bot");
            SetPosition(p1, pos1);
            SetPosition(p2, pos2);
            string scene = DungeonEntry.SelectedScene;
            int floor = DungeonManager.instance.currentFloor;
            string session = host ? Guid.NewGuid().ToString("N") : null;
            bool connected = false, ready = false, armed = false, acknowledged = false;
            bool attackTimedOut = false;
            bool cutsceneDismissed = false;
            int received = 0, applied = 0, duplicateSeen = 0, rejected = 0, routedTarget = 0, sent = 0;
            var ledger = new DamageLedger(1);
            float lastSend = -1f, doneAt = -1f, armedAt = -1f, readyAt = -1f, lastBlockerLog = -1f;
            float deadline = Time.realtimeSinceStartup + 75f;
            var link = new UdpTestLink();
            if (host) link.StartHost(port); else link.StartGuest(port);
            plugin.Report("GMP_DAMAGE_STAGE ready role=" + role + " geometry=" + geometry
                + " p1_damageable=" + p1.canBeDamaged
                + " p2_damageable=" + p2.canBeDamaged);
            plugin.Report(Blockers());
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
                        if (host && parts.Length == 6 && parts[0] == "HIT_HELLO" && parts[1] == runId
                            && parts[2] == build && parts[3] == scene && parts[4] == floor.ToString()
                            && parts[5] == geometry)
                        {
                            connected = true;
                            link.SetPeer(from);
                        }
                        else if (!host && parts.Length == 8 && parts[0] == "HIT_WELCOME"
                            && parts[1] == runId && parts[3] == build && parts[4] == scene
                            && parts[5] == floor.ToString() && parts[6] == geometry && parts[7] == prefab.name)
                        {
                            session = parts[2];
                            connected = true;
                            if (!enemy)
                            {
                                enemy = EnemyMirror.Spawn(room, prefab, enemyPos);
                                foreach (var node in enemy.GetComponentsInChildren<MonoBehaviour>(true))
                                    if (node is iCubicleNode) node.gameObject.SetActive(false);
                                foreach (var box in enemy.GetComponentsInChildren<Hitbox>(true))
                                    box.enabled = false;
                                enemy.Activate(0f);
                                enemy.StartThonk();
                                ready = true;
                            }
                        }
                        else if (host && parts.Length == 4 && parts[0] == "HIT_READY"
                            && parts[1] == runId && parts[2] == session && parts[3] == "1")
                            ready = true;
                        else if (!host && ready && parts.Length == 10 && parts[0] == "DAMAGE"
                            && parts[1] == runId && parts[2] == session && parts[3] == "1"
                            && parts[4] == "1" && parts[5] == "1")
                        {
                            int target = int.Parse(parts[6], CultureInfo.InvariantCulture);
                            float delta = float.Parse(parts[7], CultureInfo.InvariantCulture);
                            float before = float.Parse(parts[8], CultureInfo.InvariantCulture);
                            float after = float.Parse(parts[9], CultureInfo.InvariantCulture);
                            if (float.IsNaN(before) || float.IsInfinity(before)
                                || float.IsNaN(after) || float.IsInfinity(after)
                                || Mathf.Abs(before - after - delta) > 0.001f)
                            { rejected++; continue; }
                            DamageDecision decision = ledger.Accept(1, 1, target, delta);
                            received++;
                            if (decision == DamageDecision.Reject) { rejected++; continue; }
                            if (decision == DamageDecision.Duplicate)
                            {
                                duplicateSeen++;
                                if (!duplicateNegative) continue;
                            }
                            routedTarget = targetNegative ? 1 : target;
                            Player receiver = routedTarget == 2 ? p2 : p1;
                            receiver.health -= delta;
                            applied++;
                            amount = delta;
                            nativeTarget = target;
                            plugin.Report("GMP_DAMAGE_APPLY event=1 source_target=" + target
                                + " routed_target=" + routedTarget + " amount=" + Fmt(delta)
                                + " p1=" + Fmt(p1.health) + " p2=" + Fmt(p2.health));
                        }
                        else if (host && parts.Length == 4 && parts[0] == "DAMAGE_ACK"
                            && parts[1] == runId && parts[2] == session && parts[3] == "1")
                        {
                            acknowledged = true;
                            if (doneAt < 0f) doneAt = now + 1f;
                        }
                    }
                    if (host && ready && readyAt < 0f) readyAt = now;
                    if (host && ready && !armed)
                    {
                        // Native protections (loading cutscene, conversation)
                        // must clear through normal game flow. The probe never
                        // forces damage through an invincibility check.
                        bool settled = CameraController.instance.state != CameraState.Cutscene
                            && !GameManager.conversationActive
                            && p1.canBeDamaged && p2.canBeDamaged;
                        if (!settled && !cutsceneDismissed && now - readyAt > 2f)
                        {
                            // The entrance room starts a scripted conversation that
                            // waits for player input. Use the game's own end-of-
                            // cutscene path instead of waiting on a human or faking
                            // a damage result. No invincibility is overridden.
                            cutsceneDismissed = true;
                            p1.CutsceneEnded();
                            // CutsceneEnded on the camera is a no-op in this
                            // build; StopCutscene is its own exit path and ends
                            // in CameraState.TrackingPlayer.
                            CameraController.instance.StopCutscene(snap: true);
                            // The PixelCrushers dialogue system keeps its own
                            // conversation flag, which is what gates damage.
                            // Stop it through its public controller API.
                            PixelCrushers.DialogueSystem.DialogueManager.instance.StopConversation();
                            plugin.Report("GMP_DAMAGE_CUTSCENE_DISMISSED reason=scripted_room_intro"
                                + " dialogue=" + PixelCrushers.DialogueSystem.DialogueManager.instance.isConversationActive);
                        }
                        if (!settled)
                        {
                            if (now - readyAt > 20f)
                                throw new InvalidOperationException("Native combat protections did not settle: "
                                    + Blockers());
                            if (now - lastBlockerLog > 2f)
                            {
                                lastBlockerLog = now;
                                plugin.Report(Blockers());
                            }
                            yield return null;
                            continue;
                        }
                        armed = true;
                        armedAt = now;
                        enemy = room.NewEnemy(prefab, enemyPos, -1f);
                        if (!enemy) throw new InvalidOperationException("Attack enemy spawn rejected");
                        var node = enemy.GetComponentInChildren<CubicleGroundBotBladeNode>(true);
                        var attackBox = enemy.GetComponentInChildren<Hitbox>(true);
                        if (!node || !attackBox)
                            throw new InvalidOperationException("Blade node or hitbox missing on fixture");
                        node.gameObject.SetActive(false);
                        attackBox.enabled = false;
                        enemy.rb.constraints = RigidbodyConstraints.FreezeAll;
                        enemy.rb.isKinematic = false;
                        DungeonManager.currentRoom = room;
                        enemy.aggroTarget = null;
                        enemy.Activate(0f);
                        float until = Time.realtimeSinceStartup + 6f;
                        while ((!enemy.Active || !enemy.thonking) && Time.realtimeSinceStartup < until)
                            yield return null;
                        if (!enemy.Active || !enemy.thonking)
                            throw new InvalidOperationException("Native enemy brain did not start");
                        selectedTarget = (Entity)Aggro.GetValue(enemy) == p2 ? 2 : 1;
                        // Re-enable one authored attack node once to obtain a
                        // reproducible pulse. Its own routine creates the
                        // explosion; physics must find the player and damage it.
                        // The node's authored parent container starts inactive,
                        // so activate its ancestors up to (not including) the
                        // enemy root before enabling the node itself.
                        Transform chain = node.transform;
                        while (chain && chain.gameObject != enemy.gameObject)
                        {
                            chain.gameObject.SetActive(true);
                            chain = chain.parent;
                        }
                        // This variant's serialized node reference can be
                        // unassigned; bind it to the enemy's own view before the
                        // authored routine starts. This wires a reference, it
                        // does not create damage or a hit.
                        node.view = enemy.view;
                        attackBox.enabled = true;
                        node.gameObject.SetActive(true);
                        string chainNames = "";
                        Transform walk = node.transform;
                        while (walk) { chainNames += walk.name + (walk.gameObject.activeSelf ? "[on]" : "[off]") + "<"; walk = walk.parent; }
                        plugin.Report("GMP_DAMAGE_ARM target=" + selectedTarget + " native_node="
                            + node.GetType().Name + " prefab=" + prefab.name
                            + " node_self=" + node.gameObject.activeSelf
                            + " node_hierarchy=" + node.gameObject.activeInHierarchy
                            + " node_enabled=" + node.enabled 
                            + " chain=" + chainNames);
                    }
                    if (host && armed && hits == 0)
                    {
                        // A trigger needs a moving body. Kinematic MovePosition on
                        // P1 supplies it without injecting hits or damage.
                        // Move the attacker's own body so its blade hitbox
                        // sweeps P2's collider. Kinematic motion is required for
                        // trigger events between two otherwise static bodies.
                        float wave = Mathf.Sin((now - armedAt) * 6f) * 0.35f;
                        enemy.rb.MovePosition(enemyPos - axis * (0.35f + wave));
                        if (now - armedAt > 2f && Mathf.Repeat(now - armedAt, 2f) < 0.3f && !attackTimedOut)
                        {
                            var live = enemy ? enemy.GetComponentInChildren<CubicleGroundBotElectricNode>(true) : null;
                            plugin.Report("GMP_DAMAGE_PROBE pulses=" + pulses + " explosions=" + explosionsSeen
                                + " node=" + (live ? live.isActiveAndEnabled.ToString() : "missing")
                                + " thonking=" + (enemy && enemy.thonking)
                                + " room=" + (DungeonManager.currentRoom ? DungeonManager.currentRoom.name : "null"));
                        }
                        if (now - armedAt > 12f && !attackTimedOut)
                        {
                            attackTimedOut = true;
                            plugin.Report("GMP_DAMAGE_ATTACK_TIMEOUT pulses=" + pulses
                                + " target=" + TargetId(enemy, p1, p2)
                                + " enemy_active=" + (enemy && enemy.Active)
                                + " thonking=" + (enemy && enemy.thonking)
                                + " p1_damageable=" + p1.canBeDamaged
                                + " p2_damageable=" + p2.canBeDamaged);
                        }
                    }
                    if (now - lastSend >= 0.1f)
                    {
                        lastSend = now;
                        if (!host && !connected)
                            link.Send("HIT_HELLO|" + runId + "|" + build + "|" + scene + "|" + floor + "|" + geometry);
                        if (host && connected && !ready)
                            link.Send("HIT_WELCOME|" + runId + "|" + session + "|" + build + "|" + scene
                                + "|" + floor + "|" + geometry + "|" + prefab.name);
                        if (!host && ready && received == 0)
                            link.Send("HIT_READY|" + runId + "|" + session + "|1");
                        if (host && hits > 0 && !acknowledged && sent < 5)
                        {
                            link.Send("DAMAGE|" + runId + "|" + session + "|1|1|1|" + nativeTarget
                                + "|" + Fmt(amount) + "|10|" + Fmt(10f - amount));
                            sent++;
                        }
                        if (!host && received >= 3 && applied > 0)
                        {
                            link.Send("DAMAGE_ACK|" + runId + "|" + session + "|1");
                            acknowledged = true;
                            if (doneAt < 0f) doneAt = now + 1f;
                        }
                    }
                    yield return null;
                }
                bool hostOk = pulses == 1 && hits == 1 && selectedTarget == 2 && nativeTarget == 2
                    && sourceType == "Explosion" && Mathf.Abs(amount - 1f) < 0.001f
                    && p1.health == 10f && p2.health == 9f;
                if (!armed && host) plugin.Report(Blockers());
                bool quiet = host || (enemy && !enemy.enabled && !enemy.Active && !enemy.hasBrain
                    && EnemyMirror.AllowedStarts == 0);
                bool guestOk = pulses == 0 && applied == 1 && received >= 3 && duplicateSeen >= 2
                    && routedTarget == 2 && p1.health == 10f && p2.health == 9f
                    && quiet
                    && EnemyMirror.Blocked >= 4;
                bool pass = connected && ready && acknowledged && (host ? hostOk : guestOk);
                plugin.Report("GMP_DAMAGE_RESULT " + (pass ? "PASS" : "FAIL") + " run=" + runId
                    + " role=" + role + " session=" + (session ?? "none") + " build=" + build
                    + " geometry=" + geometry + " scene=" + scene + " floor=" + floor
                    + " connected=" + connected + " ready=" + ready + " ack=" + acknowledged
                    + " pulses=" + pulses + " hits=" + hits + " selected=" + selectedTarget
                    + " native_target=" + nativeTarget + " source=" + sourceType
                    + " amount=" + Fmt(amount) + " received=" + received + " applied=" + applied
                    + " duplicates=" + duplicateSeen + " rejected=" + rejected + " routed=" + routedTarget
                    + " p1=" + Fmt(p1.health) + " p2=" + Fmt(p2.health)
                    + " mirror_quiet=" + quiet + " blocks=" + EnemyMirror.Blocked
                    + " starts=" + EnemyMirror.AllowedStarts
                    + " target_negative=" + targetNegative + " duplicate_negative=" + duplicateNegative);
                Application.Quit(pass ? 0 : 1);
            }
            finally
            {
                link.Close();
                StopEnemy();
                // QA account-write guards intentionally survive shutdown.
            }
        }

        private static int TargetId(Enemy instance, Player first, Player second)
        {
            Entity target = (Entity)Aggro.GetValue(instance);
            return target == first ? 1 : target == second ? 2 : 0;
        }

        private static GameObject FindBladeBot(Room room)
        {
            DungeonSettings settings = DungeonManager.CURRENT_SETTINGS;
            if (!settings) throw new InvalidOperationException("No dungeon settings for enemy fixture");
            foreach (SpawnableEnemyData data in settings.GetPossibleEnemies(
                DungeonManager.instance.currentFloor, 0))
            {
                if (data == null) continue;
                GameObject candidate = data.GetPrefab(room);
                if (!candidate) continue;
                Enemy enemy = candidate.GetComponent<Enemy>();
                if (!enemy || enemy.passive) continue;
                if (candidate.GetComponentInChildren<CubicleGroundBotBladeNode>(true)
                    && candidate.GetComponentInChildren<Hitbox>(true)) return candidate;
            }
            throw new InvalidOperationException("No authored hitbox enemy prefab available");
        }

        private static void SetPosition(Player player, Vector3 position)
        {
            player.transform.position = position;
            player.rb.position = position;
        }

        private static string Fmt(float value) { return value.ToString("F3", CultureInfo.InvariantCulture); }

        private static string Blockers()
        {
            return "GMP_DAMAGE_PROTECTIONS camera=" + CameraController.instance.state
                + " conversation=" + GameManager.conversationActive + " scale=" + Fmt(Time.timeScale)
                + " p1_inv=" + p1.hasInvincibility + " p2_inv=" + p2.hasInvincibility
                + " p1_special=" + p1.hasSpecialInvincibility + " p2_special=" + p2.hasSpecialInvincibility
                + " p1_alive=" + p1.alive + " p2_alive=" + p2.alive
                + " p1_mod=" + p1.modInvincibility + " p2_mod=" + p2.modInvincibility
                + " p1_crawling=" + p1.crawling + " p2_crawling=" + p2.crawling
                + " p1_dodge=" + p1.dodgeInvincibility + " p2_dodge=" + p2.dodgeInvincibility
                + " p1_cutscene=" + p1.cutsceneActive + " p2_cutscene=" + p2.cutsceneActive
                + " p1_can=" + p1.canBeDamaged + " p2_can=" + p2.canBeDamaged
                + " dialogue=" + PixelCrushers.DialogueSystem.DialogueManager.instance.isConversationActive + " p1_last=" + Fmt(p1.lastDamaged) + " p2_last=" + Fmt(p2.lastDamaged)
                + " now=" + Fmt(Time.time) + " cooldown=" + Fmt(p1.stats.damageCooldown);
        }
    }
}
