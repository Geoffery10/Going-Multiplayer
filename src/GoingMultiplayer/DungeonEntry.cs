using System;
using System.Collections;
using UnityEngine;

namespace GoingMultiplayer
{
    // Loads a dungeon from the office without a human walking to the elevator.
    // Prefers a real DungeonTrigger whose scene exists in this build, then the
    // game's own entrance method, so the normal scene/settings path is used.
    internal static class DungeonEntry
    {
        private static readonly string[] FallbackScenes =
            { "Dungeon_Joblin", "Cubicle_Dungeon", "TutorialDungeon" };

        internal static string SelectedScene { get; private set; }

        internal static IEnumerator EnterFromOffice(Plugin plugin, float timeoutSeconds = 120f)
        {
            SelectedScene = null;
            DungeonTrigger chosen = null;
            foreach (DungeonTrigger trigger in UnityEngine.Object.FindObjectsOfType<DungeonTrigger>())
            {
                string scene = trigger.currentDungeon;
                bool loadable = !string.IsNullOrEmpty(scene) && Application.CanStreamedLevelBeLoaded(scene);
                plugin.Report("GMP_DUNGEON_TRIGGER type=" + trigger.GetType().Name
                    + " scene=" + scene + " loadable=" + loadable + " in_use=" + trigger.inUse);
                if (!loadable) continue;
                if (trigger is CubicleElevator) chosen = trigger;
                else if (chosen == null) chosen = trigger;
            }

            if (chosen != null)
            {
                SelectedScene = chosen.currentDungeon;
                plugin.Report("GMP_DUNGEON_ENTRY source=trigger type=" + chosen.GetType().Name
                    + " scene=" + SelectedScene);
                chosen.Enter();
            }
            else
            {
                foreach (string scene in FallbackScenes)
                {
                    if (!Application.CanStreamedLevelBeLoaded(scene)) continue;
                    SelectedScene = scene;
                    break;
                }
                if (SelectedScene == null)
                    throw new InvalidOperationException("No loadable dungeon scene found");
                plugin.Report("GMP_DUNGEON_ENTRY source=fallback scene=" + SelectedScene);
                GUIManager.instance.LoadDungeonScene(SelectedScene, null);
            }

            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while ((!DungeonManager.instance || !Player.singlePlayer || GUIManager.instance.IsLoading()
                || !DungeonManager.instance.floorInitialized || DungeonManager.currentRoom == null)
                && Time.realtimeSinceStartup < deadline) yield return null;
            if (!DungeonManager.instance || !Player.singlePlayer
                || !DungeonManager.instance.floorInitialized || DungeonManager.currentRoom == null)
                throw new InvalidOperationException("Dungeon did not become ready via " + SelectedScene);
            plugin.Report("GMP_DUNGEON_READY scene=" + SelectedScene
                + " room=" + DungeonManager.currentRoom.name
                + " floor=" + DungeonManager.instance.currentFloor);
            yield return null;
        }
    }
}
