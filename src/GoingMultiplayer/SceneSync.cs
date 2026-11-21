using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingMultiplayer
{
    // Dungeon generation is reseeded per process (GameManager.Start uses
    // Environment.TickCount), so two clients build different floors. The host
    // picks a seed per scene and both sides re-seed immediately before the floor
    // is generated, which makes the layout identical when the generator is
    // otherwise deterministic.
    internal static class SceneSync
    {
        internal static int PendingSeed;
        private static Harmony patches;

        internal static void Install()
        {
            if (patches != null) return;
            patches = new Harmony("goingmultiplayer.scene-sync");
            patches.Patch(AccessTools.Method(typeof(DungeonManager), "GenerateFloorRoutine"),
                prefix: new HarmonyMethod(typeof(SceneSync).GetMethod("BeforeGenerate",
                    BindingFlags.Static | BindingFlags.NonPublic)));
        }

        internal static int NextSeed()
        {
            int seed = new System.Random().Next(1, int.MaxValue);
            return seed;
        }

        private static void BeforeGenerate()
        {
            if (PendingSeed == 0) return;
            Random.InitState(PendingSeed);
            PendingSeed = 0;
        }
    }
}
