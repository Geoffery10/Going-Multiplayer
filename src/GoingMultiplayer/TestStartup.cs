using HarmonyLib;
using UnityEngine;

namespace GoingMultiplayer
{
    // QA only: remove presentation delays, never Steam or game initialization.
    internal static class TestStartup
    {
        private static Plugin plugin;
        private static bool fast;
        private static bool stopping;
        private static AggroCrabStinger stinger;
        private static float stingerStarted;
        internal static bool StingerSeen;
        internal static bool Skipped;
        internal static bool TitleReady;
        internal static bool VideosStopped;
        internal static float TitleSeconds;

        internal static void Install(Plugin owner, bool skip)
        {
            plugin = owner;
            fast = skip;
            var harmony = new Harmony("goingmultiplayer.qa-startup");
            harmony.Patch(AccessTools.Method(typeof(AggroCrabStinger), "Start"),
                prefix: new HarmonyMethod(typeof(TestStartup), "StartStinger"));
            harmony.Patch(AccessTools.Method(typeof(TitleScreen), "Awake"),
                postfix: new HarmonyMethod(typeof(TestStartup), "TitleAwake"));
            harmony.Patch(AccessTools.Method(typeof(TitleScreen), "InitVideo"),
                prefix: new HarmonyMethod(typeof(TestStartup), "AllowVideo"));
            harmony.Patch(AccessTools.Method(typeof(TitleScreen), "Start"),
                postfix: new HarmonyMethod(typeof(TestStartup), "TitleStarted"));
        }

        private static bool StartStinger(AggroCrabStinger __instance)
        {
            if (stopping) return false;
            stinger = __instance;
            StingerSeen = true;
            stingerStarted = Time.realtimeSinceStartup;
            Skipped = fast;
            plugin.Report("GMP_STARTUP_STINGER fast=" + fast);
            if (!fast) return true;
            // Defer to the next frame so other native Start initialization
            // finishes. Invoke the game's own private Title-loading method.
            __instance.Invoke("LoadScene", 0f);
            return false;
        }

        private static bool AllowVideo() { return !fast; }

        internal static void StopForQuit()
        {
            // A loader gate can finish before native Stinger.Start executes.
            // Never schedule a scene load after that test has requested exit.
            stopping = true;
            if (stinger) stinger.CancelInvoke("LoadScene");
        }

        private static void TitleAwake(TitleScreen __instance)
        {
            if (fast) StopVideos(__instance);
        }

        private static void TitleStarted(TitleScreen __instance)
        {
            if (fast) StopVideos(__instance);
            TitleReady = true;
            TitleSeconds = Time.realtimeSinceStartup - stingerStarted;
            plugin.Report("GMP_STARTUP_TITLE fast=" + fast + " skipped=" + Skipped
                + " videos_stopped=" + VideosStopped + " seconds="
                + TitleSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void StopVideos(TitleScreen title)
        {
            if (title.introVideo)
            {
                title.introVideo.Stop();
                title.introVideo.enabled = false;
            }
            if (title.backgroundVideo)
            {
                title.backgroundVideo.Stop();
                title.backgroundVideo.enabled = false;
            }
            VideosStopped = (!title.introVideo || !title.introVideo.enabled)
                && (!title.backgroundVideo || !title.backgroundVideo.enabled);
        }
    }
}
