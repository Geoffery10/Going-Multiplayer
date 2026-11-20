using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingMultiplayer
{
    // Opt-in QA primitive. Entry guards are installed before instantiation so
    // Awake/OnEnable cannot get ahead of post-spawn component disabling.
    // This does not certify arbitrary child scripts or external manager tasks.
    internal static class EnemyMirror
    {
        private static readonly HashSet<Enemy> Mirrors = new HashSet<Enemy>();
        private static int creating;
        private static Harmony patches;
        internal static Enemy ObservedHost;
        internal static bool AllowSimulation;
        internal static int Blocked;
        internal static int AllowedStarts;
        internal static int HostStarts;

        internal static void Install()
        {
            if (patches != null) return;
            patches = new Harmony("goingmultiplayer.qa-enemy-mirror");
            foreach (Type type in typeof(Enemy).Assembly.GetTypes())
            {
                if (!typeof(Enemy).IsAssignableFrom(type)) continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "Activate" && method.Name != "OnActivated"
                        && method.Name != "StartThonk") continue;
                    if (method.ReturnType != typeof(void)) continue;
                    patches.Patch(method, prefix: new HarmonyMethod(typeof(EnemyMirror), "AllowEntry"));
                }
            }
        }

        private static bool AllowEntry(Enemy __instance, MethodBase __originalMethod)
        {
            bool remote = creating > 0 || Mirrors.Contains(__instance);
            if (remote && !AllowSimulation)
            {
                Blocked++;
                return false;
            }
            if (__originalMethod.Name == "StartThonk")
            {
                if (remote) AllowedStarts++;
                if (__instance == ObservedHost) HostStarts++;
            }
            return true;
        }

        internal static Enemy Spawn(Room room, GameObject prefab, Vector3 pose)
        {
            Install();
            Enemy enemy;
            creating++;
            try
            {
                enemy = room.NewEnemy(prefab, pose, -1f);
                // Exercise native entry while still in the creation transaction,
                // before this instance is in the mirror registry.
                if (enemy)
                {
                    enemy.Activate(0f);
                    enemy.StartThonk();
                }
            }
            finally { creating--; }
            if (!enemy) throw new InvalidOperationException("Mirror spawn rejected");
            Mirrors.Add(enemy);
            enemy.hasBrain = false;
            enemy.Active = false;
            enemy.StopThonk();
            enemy.StopAllCoroutines();
            enemy.enabled = false;
            enemy.SetPhysicalCollidersActive(false);
            if (enemy.rb)
            {
                enemy.rb.velocity = Vector3.zero;
                enemy.rb.isKinematic = true;
            }
            return enemy;
        }
    }
}
