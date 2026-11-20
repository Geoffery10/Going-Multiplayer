using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GoingMultiplayer
{
    internal static class SeatProbe
    {
        private static bool SkipAccountMutation() { return false; }

        internal static IEnumerator Run(Plugin plugin, string runId, int count, bool negative)
        {
            // Suppress only achievements/stat mutations during QA, never Steam authentication.
            var qa = new Harmony("goingmultiplayer.qa-account-guard");
            foreach (string name in new[] { "TestAchievementComplete", "TestStatIncremented", "ResetCheevos" })
                qa.Patch(AccessTools.Method(typeof(SteamManager), name),
                    prefix: new HarmonyMethod(typeof(SeatProbe).GetMethod("SkipAccountMutation",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            LocalSeats.Install();
            plugin.Report("GMP_SEAT_STAGE wait_for_title");
            float deadline = Time.realtimeSinceStartup + 30f;
            while ((!Steamworks.SteamClient.IsValid || !GameManager.instance || !GUIManager.instance
                || !Object.FindObjectOfType<TitleScreen>())
                && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Steamworks.SteamClient.IsValid) throw new InvalidOperationException("Steam not authenticated");
            if (!Object.FindObjectOfType<TitleScreen>()) throw new InvalidOperationException("Title screen did not load");
            yield return null;
            // This prefix is isolated by the launcher; no owner save is mounted.
            SaveLoad.MountSave(0);
            SaveData.instance.introCutsceneViewed = true;
            plugin.Report("GMP_SEAT_STAGE load_office");
            GUIManager.instance.LoadFizzle();
            deadline = Time.realtimeSinceStartup + 60f;
            while ((!Player.singlePlayer || GUIManager.instance.IsLoading())
                && Time.realtimeSinceStartup < deadline) yield return null;
            Player local = Player.singlePlayer;
            if (!local || GUIManager.instance.IsLoading()) throw new InvalidOperationException("Office player did not load");
            yield return null;
            var hostInput = LocalSeats.ScriptLocalInput(local);
            Material originalMaterial = local.view.jackieMeshRenderer.sharedMaterial;
            Costume originalCostume = local.view.currentCostume;
            var seats = new List<LocalSeats.Seat>();
            for (int i = 1; i < count; i++)
            {
                Vector3 spawn = FindSpawn(local);
                seats.Add(LocalSeats.Spawn(local, spawn, true));
            }
            yield return null;
            yield return null;
            plugin.Report("GMP_SEAT_STAGE spawned count=" + GameManager.playerList.Count);
            bool identities = GameManager.playerList.Count == count && Player.singlePlayer == local;
            bool data = true, health = true, palettes = true;
            var dataIdentities = new HashSet<PlayerData> { local.data };
            var inventories = new HashSet<PlayerInventory> { local.data.inventory };
            var inputsIdentities = new HashSet<PlayerInputRewired> { local.input };
            float originalHealth = local.health;
            foreach (LocalSeats.Seat seat in seats)
            {
                data &= !ReferenceEquals(local.data, seat.Player.data)
                    && !ReferenceEquals(local.data.inventory, seat.Player.data.inventory)
                    && !ReferenceEquals(local.input, seat.Player.input)
                    && dataIdentities.Add(seat.Player.data)
                    && inventories.Add(seat.Player.data.inventory)
                    && inputsIdentities.Add(seat.Player.input);
                seat.Player.health = Mathf.Max(1f, originalHealth - 1f);
                LocalSeats.NegativeHealth = negative;
                health &= Mathf.Abs(seat.Player.health - Mathf.Max(1f, originalHealth - 1f)) < 0.01f
                    && Mathf.Abs(local.health - originalHealth) < 0.01f;
                LocalSeats.NegativeHealth = false;
                palettes &= seat.Player.view.currentCostume == seat.Costume
                    && seat.Player.view.jackieMeshRenderer.sharedMaterial == seat.Material
                    && seat.Material != local.view.jackieMeshRenderer.sharedMaterial
                    && seat.Material.mainTexture != local.view.jackieMeshRenderer.sharedMaterial.mainTexture;
                plugin.Report("GMP_SEAT_PALETTE player=" + seat.Player.GetInstanceID() + " palette=" + seat.Palette);
            }
            palettes &= local.view.currentCostume == originalCostume
                && local.view.jackieMeshRenderer.sharedMaterial == originalMaterial;
            bool inventory = ProbeInventory(local, seats[0].Player, plugin);
            bool restoredAfterException = false;
            try { seats[0].Data.inventory.BarfOutItem(null); }
            catch (NullReferenceException) { restoredAfterException = Player.singlePlayer == local; }
            plugin.Report("GMP_SEAT_CONTEXT exception_restored=" + restoredAfterException);
            inventory &= restoredAfterException;
            bool inputs = hostInput.Player.input.GetHorizontalAxis() == 0f;
            LocalSeats.Seat moving = seats[0];
            local.forceMove = true;
            moving.Player.forceMove = true;
            GameManager.instance.isPaused = false;
            Time.timeScale = 1f;
            Vector3 before = moving.Player.transform.position;
            moving.Move = new Vector2(1f, 0f);
            inputs &= moving.Player.input.GetHorizontalAxis() == 1f && local.input.GetHorizontalAxis() == 0f;
            deadline = Time.realtimeSinceStartup + 0.75f;
            while (Time.realtimeSinceStartup < deadline) yield return null;
            moving.Move = Vector2.zero;
            Vector3 delta = moving.Player.transform.position - before;
            delta.y = 0f;
            bool movement = delta.magnitude > 0.1f;
            bool actor = moving.Player.enabled && moving.Player.view.anim.isActiveAndEnabled
                && moving.Player.view.jackieMeshRenderer.sharedMesh != null && !local.grabbable;
            foreach (LocalSeats.Seat seat in seats) actor &= !seat.Player.grabbable;
            bool passed = identities && data && health && palettes && inventory && inputs && movement && actor;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-capture") >= 0)
            {
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.GameRootPath,
                    "gmp-palette-" + runId + ".png"));
                yield return null;
                yield return null;
            }
            plugin.Report("GMP_SEAT_RESULT " + (passed ? "PASS" : "FAIL") + " run=" + runId
                + " count=" + count + " identities=" + identities + " data=" + data
                + " health=" + health + " inventory=" + inventory + " input=" + inputs
                + " movement=" + movement + " palette=" + palettes + " actor=" + actor
                + " distance=" + delta.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-controller-probe") >= 0)
            {
                IEnumerator probe = ControllerProbe.Run(plugin, runId, hostInput, moving, passed);
                while (probe.MoveNext()) yield return probe.Current;
                yield break;
            }
            Application.Quit(passed ? 0 : 1);
        }

        private static bool ProbeInventory(Player local, Player other, Plugin plugin)
        {
            foreach (Entity item in Object.FindObjectsOfType<Entity>())
            {
                if (item is Character || item is ShopEntity || !item.weaponStats || !item.isGrabbable
                    || !item.weaponStats.isStashable || item.weaponStats.isHeavy || item.wielder) continue;
                if (!other.GrabItem(item)) continue;
                bool held = other.heldItem == item && item.wielder == other
                    && other.data.inventory.inventory.Contains(item)
                    && !local.data.inventory.inventory.Contains(item)
                    && local.heldItem != item && Player.singlePlayer == local;
                plugin.Report("GMP_SEAT_INVENTORY native_pickup=" + held + " item=" + item.name);
                return held;
            }
            plugin.Report("GMP_SEAT_INVENTORY native_pickup=False reason=no_pickup_succeeded");
            return false;
        }

        internal static Vector3 FindSpawn(Player local)
        {
            for (int ring = 1; ring <= 4; ring++)
                for (int direction = 0; direction < 8; direction++)
                {
                    float angle = direction * Mathf.PI / 4f;
                    Vector3 candidate = local.transform.position
                        + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (ring * 1.5f);
                    RaycastHit hit;
                    if (!Physics.Raycast(candidate + Vector3.up * 2f, Vector3.down, out hit, 4f,
                        GlobalSettings.defaults.groundLayers, QueryTriggerInteraction.Ignore)
                        || hit.normal.y < 0.8f || Mathf.Abs(hit.point.y - candidate.y) > 1f) continue;
                    if (Physics.CheckCapsule(candidate + Vector3.up * 0.45f,
                        candidate + Vector3.up * 1.1f, 0.3f, ~0, QueryTriggerInteraction.Ignore)) continue;
                    return candidate;
                }
            throw new InvalidOperationException("No clear, grounded second-player spawn found");
        }
    }
}
