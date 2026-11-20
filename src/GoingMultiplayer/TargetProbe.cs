using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GoingMultiplayer
{
    // Single-process enemy-targeting gate. No networking is involved.
    internal static class TargetProbe
    {
        private static bool SkipAccountMutation() { return false; }

        internal static IEnumerator Run(Plugin plugin, string runId, bool negative)
        {
            var qa = new Harmony("goingmultiplayer.qa-account-guard");
            foreach (string name in new[] { "TestAchievementComplete", "TestStatIncremented", "ResetCheevos" })
                qa.Patch(AccessTools.Method(typeof(SteamManager), name),
                    prefix: new HarmonyMethod(typeof(TargetProbe).GetMethod("SkipAccountMutation",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            LocalSeats.Install();

            plugin.Report("GMP_TARGET_STAGE wait_for_title");
            float deadline = Time.realtimeSinceStartup + 90f;
            while ((!Steamworks.SteamClient.IsValid || !GameManager.instance || !GUIManager.instance
                || !Object.FindObjectOfType<TitleScreen>())
                && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Steamworks.SteamClient.IsValid) throw new InvalidOperationException("Steam not authenticated");
            if (!Object.FindObjectOfType<TitleScreen>()) throw new InvalidOperationException("Title screen did not load");
            yield return null;

            SaveLoad.MountSave(0);
            SaveData.instance.introCutsceneViewed = true;
            plugin.Report("GMP_TARGET_STAGE load_office");
            GUIManager.instance.LoadFizzle();
            deadline = Time.realtimeSinceStartup + 60f;
            while ((!Player.singlePlayer || GUIManager.instance.IsLoading())
                && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Player.singlePlayer || GUIManager.instance.IsLoading())
                throw new InvalidOperationException("Office player did not load");
            yield return null;

            plugin.Report("GMP_TARGET_STAGE load_dungeon");
            yield return DungeonEntry.EnterFromOffice(plugin);

            GameManager.instance.isPaused = false;
            Time.timeScale = 1f;
            Player local = Player.singlePlayer;
            local.LockMovement(false);
            local.forceMove = true;
            // Replace P1's live gamepad/keyboard input with a neutral scripted
            // seat. Otherwise connected-device drift moves P1 during the gate.
            LocalSeats.ScriptLocalInput(local);
            Room room = DungeonManager.currentRoom;
            DungeonManager.currentRoom = room;
            yield return null;
            // The floor slide-in moves and locks the player for ~2.5 s. Let it
            // finish so the distance control is not fighting native movement.
            plugin.Report("GMP_TARGET_STAGE settle");
            yield return new WaitForSeconds(3f);
            local.LockMovement(false);
            local.forceMove = true;
            plugin.Report("GMP_TARGET_STAGE ready room=" + room.name + " floor="
                + DungeonManager.instance.currentFloor + " players=" + GameManager.playerList.Count);

            GameObject prefab = FindEnemyPrefab(room);
            Vector3 p1Position = local.transform.position;
            Vector3 p2Position = SeatProbe.FindSpawn(local);
            Vector3 separation = Util.ZeroY(p2Position - p1Position);
            if (separation.sqrMagnitude < 0.01f) separation = Util.ZeroY(local.transform.forward);
            if (separation.sqrMagnitude < 0.01f) separation = Vector3.forward;
            separation.Normalize();
            // Keep both players at natural, valid positions and move the enemy
            // instead. Teleporting players fought room physics and made the
            // measured distances unreliable.
            Vector3 enemyNearP2 = p2Position + separation * 0.5f;
            Vector3 enemyNearP1 = p1Position - separation * 0.5f;

            Enemy enemy = room.NewEnemy(prefab, enemyNearP2, -1f);
            if (!enemy) throw new InvalidOperationException("Enemy spawn was rejected");
            enemy.aggroTarget = null;
            enemy.SetPhysicalCollidersActive(false);
            if (enemy.rb)
            {
                enemy.rb.isKinematic = true;
                enemy.rb.velocity = Vector3.zero;
            }
            MoveEnemy(enemy, enemyNearP2);
            LocalSeats.Seat seat = LocalSeats.Spawn(local, p2Position, true);
            if (seat.Player.rb) seat.Player.rb.isKinematic = true;
            // P1 can retain dungeon-intro movement while forceMove is on. Freeze
            // both actors so the measured distances are stable; targeting only
            // reads transforms and team/candidate state.
            local.inputVector = Vector3.zero;
            local.lookVector = Vector3.zero;
            if (local.rb)
            {
                local.rb.velocity = Vector3.zero;
                local.rb.isKinematic = true;
            }
            Hold(local, p1Position);
            Hold(seat.Player, p2Position);
            yield return null;
            yield return null;
            Hold(local, p1Position);
            Hold(seat.Player, p2Position);
            MoveEnemy(enemy, enemyNearP2);
            Player other = seat.Player;
            if (Vector3.Distance(Util.ZeroY(other.transform.position), Util.ZeroY(p2Position)) > 1f)
                throw new InvalidOperationException("Second actor did not stay at the prepared position");

            if (negative)
            {
                // Mutation twin: make the second actor non-hostile to enemies.
                // This keeps it alive and in Character.characters so the gate
                // can distinguish "not a candidate" from "not nearest".
                other.team = Team.Enemy;
                yield return null;
            }
            bool candidate = Character.characters.Contains(other);
            DungeonManager.currentRoom = room;
            bool sameRoom = other.room == enemy.room;
            bool hostile = enemy.IsHostile(other);
            bool alive = other.alive;
            bool active = other.gameObject.activeInHierarchy;
            bool singlePlayerRestored = Player.singlePlayer == local;
            string diagnostics = " p1=" + local.GetInstanceID() + " p2=" + other.GetInstanceID()
                + " enemy=" + enemy.GetInstanceID() + " room=" + room.name
                + " p1_dist=" + Distance(local, enemy).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " p2_dist=" + Distance(other, enemy).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " p2_in_characters=" + candidate + " p2_same_room=" + sameRoom
                + " p2_hostile=" + hostile + " p2_alive=" + alive + " p2_active=" + active
                + " single_player_restored=" + singlePlayerRestored;
            plugin.Report("GMP_TARGET_CANDIDATE" + diagnostics);

            plugin.Report("GMP_TARGET_STAGE activate");
            enemy.aggroTarget = null;
            DungeonManager.currentRoom = room;
            enemy.Activate(0f);
            float activateDeadline = Time.realtimeSinceStartup + 6f;
            while (!enemy.Active && Time.realtimeSinceStartup < activateDeadline)
            {
                Hold(local, p1Position);
                Hold(other, p2Position);
                MoveEnemy(enemy, enemyNearP2);
                yield return null;
            }
            yield return null;
            Hold(local, p1Position);
            Hold(other, p2Position);
            MoveEnemy(enemy, enemyNearP2);
            DungeonManager.currentRoom = room;
            Entity target = enemy.aggroTarget;
            bool targetP2 = target == other;
            bool enemyActive = enemy.Active;
            enemy.Active = false; // Freeze AI for deterministic distance controls.
            enemy.SetPhysicalCollidersActive(false);
            if (enemy.rb) enemy.rb.velocity = Vector3.zero;
            plugin.Report("GMP_TARGET_ACQUIRE enemy_active=" + enemyActive + " target_p2=" + targetP2
                + " target=" + (target ? target.GetInstanceID().ToString() : "null")
                + " p1_dist=" + Distance(local, enemy).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " p2_dist=" + Distance(other, enemy).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

            if (negative)
            {
                other.team = Team.Player;
                yield return null;
            }

            // Distance control: move the enemy next to P1 and recompute.
            Hold(local, p1Position);
            Hold(other, p2Position);
            MoveEnemy(enemy, enemyNearP1);
            DungeonManager.currentRoom = room;
            enemy.GetNewAggroTarget();
            Entity swapTarget = enemy.aggroTarget;
            bool swapP1 = swapTarget == local;
            plugin.Report("GMP_TARGET_SWAP target_p1=" + swapP1
                + " p1_dist=" + Distance(local, enemy).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " p2_dist=" + Distance(other, enemy).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " target=" + (swapTarget ? swapTarget.GetInstanceID().ToString() : "null")
                + " single_player=" + (Player.singlePlayer ? Player.singlePlayer.GetInstanceID().ToString() : "null"));

            // Exclusion control: make P2 non-hostile; only P1 can be chosen.
            other.team = Team.Enemy;
            enemy.aggroTarget = null;
            DungeonManager.currentRoom = room;
            enemy.GetNewAggroTarget();
            bool excludedP1 = enemy.aggroTarget == local;
            other.team = Team.Player;
            yield return null;
            plugin.Report("GMP_TARGET_EXCLUDED target_p1=" + excludedP1);

            // Native aggro is sticky: a valid target is not replaced merely by a
            // closer one. Record it, then force reacquisition and require P2.
            Vector3 stickyEnemy = p2Position + separation * 0.1f;
            enemy.aggroTarget = local;
            float stickyUntil = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < stickyUntil)
            {
                Hold(local, p1Position);
                Hold(other, p2Position);
                MoveEnemy(enemy, stickyEnemy);
                yield return null;
            }
            Hold(local, p1Position);
            Hold(other, p2Position);
            MoveEnemy(enemy, stickyEnemy);
            DungeonManager.currentRoom = room;
            bool stickyStayedP1 = enemy.aggroTarget == local;
            enemy.GetNewAggroTarget();
            Entity reacquired = enemy.aggroTarget;
            bool reacquireP2 = reacquired == other;
            plugin.Report("GMP_TARGET_STICKY stayed_p1=" + stickyStayedP1 + " reacquire_p2=" + reacquireP2
                + " p1_dist=" + Distance(local, enemy).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " p2_dist=" + Distance(other, enemy).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " target=" + (reacquired ? reacquired.GetInstanceID().ToString() : "null"));

            bool healthy = candidate && sameRoom && hostile && alive && active && singlePlayerRestored
                && enemyActive && targetP2 && swapP1 && excludedP1 && reacquireP2;
            bool expected = negative ? hostile == false && !targetP2 : healthy;
            plugin.Report("GMP_TARGET_RESULT " + (negative ? "FAIL" : (healthy ? "PASS" : "FAIL"))
                + " run=" + runId + " candidate_p2=" + candidate + " same_room=" + sameRoom
                + " hostile=" + hostile + " alive=" + alive + " active=" + active
                + " single_player_restored=" + singlePlayerRestored + " enemy_active=" + enemyActive
                + " target_p2=" + targetP2 + " swap_p1=" + swapP1 + " excluded_p1=" + excludedP1
                + " sticky_stayed_p1=" + stickyStayedP1 + " reacquire_p2=" + reacquireP2
                + " negative=" + negative);
            Application.Quit(expected && !negative ? 0 : 1);
        }

        internal static GameObject FindEnemyPrefab(Room room, string prefabName = null)
        {
            DungeonSettings settings = DungeonManager.CURRENT_SETTINGS;
            if (settings)
            {
                SpawnableEnemyData[] possible = settings.GetPossibleEnemies(
                    DungeonManager.instance.currentFloor, 0);
                if (possible != null)
                {
                    foreach (SpawnableEnemyData data in possible)
                    {
                        if (data == null) continue;
                        GameObject candidate = data.GetPrefab(room);
                        if (!candidate) continue;
                        Enemy enemy = candidate.GetComponent<Enemy>();
                        if (enemy && !enemy.passive
                            && (prefabName == null || candidate.name == prefabName)) return candidate;
                    }
                }
            }
            foreach (Room candidateRoom in DungeonManager.rooms)
                foreach (Enemy existing in candidateRoom.enemies)
                    if (existing && !existing.passive)
                        throw new InvalidOperationException(
                            "No spawnable enemy prefab found; refusing to reuse an existing enemy");
            throw new InvalidOperationException("No authored enemy prefab available for target probe");
        }

        private static void MoveEnemy(Enemy enemy, Vector3 position)
        {
            if (!enemy) return;
            enemy.transform.position = position;
            if (enemy.rb)
            {
                enemy.rb.position = position;
                enemy.rb.velocity = Vector3.zero;
            }
        }

        private static void Hold(Player player, Vector3 position)
        {
            if (!player) return;
            player.transform.position = position;
            if (player.rb)
            {
                player.rb.position = position;
                player.rb.velocity = Vector3.zero;
            }
        }

        private static float Distance(Character a, Enemy b)
        {
            if (!a || !b) return -1f;
            Vector3 delta = Util.ZeroY(a.transform.position - b.transform.position);
            return delta.magnitude;
        }
    }
}
