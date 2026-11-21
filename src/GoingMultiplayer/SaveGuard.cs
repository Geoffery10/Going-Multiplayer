using HarmonyLib;

namespace GoingMultiplayer
{
    // Blocks the game's save writes while a session save is mounted. Guests play
    // the host's progression from a dedicated slot; letting the game autosave
    // there is fine, but a missed case must never touch a real save, so the save
    // entry points are gated centrally.
    internal static class SaveGuard
    {
        private static Harmony patches;

        internal static void Install()
        {
            if (patches != null) return;
            patches = new Harmony("goingmultiplayer.save-guard");
            patches.Patch(AccessTools.Method(typeof(SaveLoad), "SaveGame", new System.Type[0]),
                prefix: new HarmonyMethod(typeof(SaveGuard)
                    .GetMethod("Block", System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.NonPublic)));
            patches.Patch(AccessTools.Method(typeof(SaveLoad), "SaveGame",
                    new[] { typeof(int) }),
                prefix: new HarmonyMethod(typeof(SaveGuard)
                    .GetMethod("Block", System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.NonPublic)));
        }

        private static bool Block()
        {
            return !SessionSave.Active;
        }
    }
}
