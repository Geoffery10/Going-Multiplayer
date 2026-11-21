using System.Collections;
using UnityEngine;

namespace GoingMultiplayer
{
    internal static class SaveProbe
    {
        internal static IEnumerator Run(Plugin plugin, string runId)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while ((!Steamworks.SteamClient.IsValid
                || !Object.FindObjectOfType<TitleScreen>())
                && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!Steamworks.SteamClient.IsValid || !Object.FindObjectOfType<TitleScreen>())
            {
                plugin.Report("GMP_SAVE_RESULT FAIL run=" + runId + " reason=title-timeout");
                Application.Quit(1);
                yield break;
            }
            try
            {
                SaveLoad.UnCacheSaveData();
                SaveLoad.MountSave(0);
                SaveData data = SaveData.instance;
                bool ok = data != null;
                plugin.Report("GMP_SAVE_RESULT " + (ok ? "PASS" : "FAIL")
                    + " run=" + runId
                    + " intro=" + (ok && data.introCutsceneViewed)
                    + " gone_under=" + (ok && data.goneUnder)
                    + " tutorial=" + (ok && data.tutorialComplete)
                    + " runs_won=" + (ok ? data.runsWon : -1)
                    + " runs_lost=" + (ok ? data.runsLost : -1)
                    + " relics=" + (ok ? data.relicsInstalled : -1)
                    + " cubicle=" + (ok && data.cubicleUnlocked)
                    + " final_boss=" + (ok && data.beatFinalBoss)
                    + " mentor=" + (ok ? data.currentMentor.ToString() : "none"));
                Application.Quit(ok ? 0 : 1);
            }
            catch (System.Exception exception)
            {
                plugin.Report("GMP_SAVE_RESULT FAIL run=" + runId + " reason="
                    + exception.GetType().Name + ":" + exception.Message);
                Application.Quit(1);
            }
        }
    }
}
