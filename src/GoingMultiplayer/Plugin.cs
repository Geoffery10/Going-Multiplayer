using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GoingMultiplayer
{
    [BepInPlugin("goingmultiplayer.core", "Going Multiplayer", "0.4.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private bool showStatus;
        private int sceneCount;
        private bool hooksFound;
        private bool smokeMode;
        private string runId = "manual";
        private bool seatProbe;
        private bool targetProbe;
        private bool localPrototype;

        private void Awake()
        {
            Logger.LogInfo("GMP_LOADER_LOADED version=0.4.0 networking=not_implemented");
            smokeMode = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-smoke") >= 0;
            seatProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-seat-probe") >= 0;
            targetProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-target-probe") >= 0;
            localPrototype = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-local-prototype") >= 0;
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument.StartsWith("-gmp-run-id=", StringComparison.Ordinal))
                    runId = argument.Substring("-gmp-run-id=".Length);
            foreach (char character in runId)
                if (!char.IsLetterOrDigit(character) && character != '-' && character != '_')
                    throw new ArgumentException("Invalid test run identifier");
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            hooksFound = HasMethod(typeof(Player), "Awake")
                && HasMethod(typeof(Player), "TakeDamage")
                && HasMethod(typeof(Player), "Die")
                && HasMethod(typeof(PlayerData), "SpawnPlayer")
                && HasMethod(typeof(DungeonManager), "GenerateFloorRoutine")
                && HasMethod(typeof(DungeonManager), "AdvanceFloor")
                && HasMethod(typeof(SaveLoad), "GetSavePath");
            Logger.LogInfo("GMP_HOOK_INSPECTION result=" + (hooksFound ? "FOUND" : "MISSING")
                + " unity=" + Application.unityVersion);
            if (smokeMode && Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-negative-hook") >= 0)
                hooksFound = false;
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (localPrototype) LocalSeats.Install();
        }

        private IEnumerator Start()
        {
            if (seatProbe)
            {
                int count = 2;
                foreach (string argument in Environment.GetCommandLineArgs())
                    if (argument.StartsWith("-gmp-seat-count="))
                        count = Math.Max(2, Math.Min(8, int.Parse(argument.Substring("-gmp-seat-count=".Length))));
                yield return StartCoroutine(RunSeatProbe(count,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-negative-health") >= 0));
                yield break;
            }
            if (targetProbe)
            {
                yield return StartCoroutine(RunTargetProbe(
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-target-negative") >= 0));
                yield break;
            }
            if (!smokeMode) yield break;
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline
                && (!Steamworks.SteamClient.IsValid || sceneCount == 0))
                yield return null;
            bool passed = hooksFound && Steamworks.SteamClient.IsValid && sceneCount > 0;
            Report("GMP_SMOKE_RESULT " + (passed ? "PASS" : "FAIL")
                + " run=" + runId + " hooks=" + hooksFound + " steam=" + Steamworks.SteamClient.IsValid
                + " scenes=" + sceneCount);
            Application.Quit(passed ? 0 : 1);
        }

        private IEnumerator RunSeatProbe(int count, bool negative)
        {
            IEnumerator probe = SeatProbe.Run(this, runId, count, negative);
            while (true)
            {
                bool next = false;
                Exception failure = null;
                try { next = probe.MoveNext(); }
                catch (Exception exception) { failure = exception; }
                if (failure != null)
                {
                    Logger.LogError("GMP_SEAT_EXCEPTION " + failure);
                    Application.Quit(1);
                    yield break;
                }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private IEnumerator RunTargetProbe(bool negative)
        {
            IEnumerator probe = TargetProbe.Run(this, runId, negative);
            while (true)
            {
                bool next = false;
                Exception failure = null;
                try { next = probe.MoveNext(); }
                catch (Exception exception) { failure = exception; }
                if (failure != null)
                {
                    Logger.LogError("GMP_TARGET_EXCEPTION " + failure);
                    Report("GMP_TARGET_EXCEPTION run=" + runId + " " + failure.GetType().Name
                        + ": " + failure.Message);
                    Application.Quit(1);
                    yield break;
                }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private static bool HasMethod(Type type, string name)
        {
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == name) return true;
            }
            return false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            sceneCount++;
            Report("GMP_SCENE_LOADED name=" + scene.name + " count=" + sceneCount);
            if (localPrototype)
            {
                LocalSeats.Clear();
                LocalSeats.Install();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) showStatus = !showStatus;
            if (localPrototype && Input.GetKeyDown(KeyCode.F10)) ControllerSeats.Cycle();
            if (localPrototype && Input.GetKeyDown(KeyCode.F9) && Player.singlePlayer && LocalSeats.Count == 0)
            {
                try { LocalSeats.Spawn(Player.singlePlayer, SeatProbe.FindSpawn(Player.singlePlayer), false); }
                catch (Exception exception) { Logger.LogError(exception); }
            }
        }

        internal void Report(string text)
        {
            Logger.LogInfo(text);
            if (seatProbe || smokeMode || targetProbe)
                File.AppendAllText(Path.Combine(BepInEx.Paths.GameRootPath, "gmp-trace-" + runId + ".log"),
                    text + Environment.NewLine);
        }
        private void OnGUI()
        {
            if (!showStatus) return;
            GUI.Box(new Rect(20, 20, 600, 205), "Going Multiplayer — experimental local prototype");
            GUI.Label(new Rect(35, 50, 410, 25), "Online co-op is NOT implemented in this build.");
            GUI.Label(new Rect(35, 75, 410, 25), "Required hook names: " + (hooksFound ? "found" : "missing"));
            GUI.Label(new Rect(35, 100, 410, 25), "Scenes observed: " + sceneCount);
            GUI.Label(new Rect(35, 125, 410, 25), "F8: show/hide diagnostics");
            GUI.Label(new Rect(35, 150, 410, 25), "Prototype: F9 spawns P2; IJKL move; U/O/P actions");
            GUI.Label(new Rect(35, 175, 560, 25), "P2: " + ControllerSeats.Status);
        }

        private void OnDestroy()
        {
            if (seatProbe) Report("GMP_PLUGIN_DESTROYED run=" + runId);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            LocalSeats.Clear();
        }
    }
}
