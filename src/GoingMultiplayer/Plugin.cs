using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GoingMultiplayer
{
    [BepInPlugin("goingmultiplayer.core", "Going Multiplayer", "0.16.1")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private bool showStatus;
        private int sceneCount;
        private bool hooksFound;
        private bool smokeMode;
        private bool startupProbe;
        private int smokeMinScenes = 1;
        private string runId = "manual";
        private bool seatProbe;
        private bool targetProbe;
        private bool netProbe;
        private bool netAiProbe;
        private bool netBrainProbe;
        private bool netDamageProbe;
        private bool netInputProbe;
        private bool sessionProbe;
        private bool steamProbe;
        private bool saveProbe;
        private bool coopMenuProbe;
        private string steamRole = "host";
        private ulong steamLobby;
        private ulong steamInvite;
        private bool steamHostOnly;
        private string sessionRole = "host";
        private int sessionPort;
        private bool netNegative;
        private bool netStaleNegative;
        private bool netAuthorityNegative;
        private string netRole = "host";
        private int netPort;
        private bool localPrototype;
        private SteamCoop coop;
        private bool showCoop;

        private void Awake()
        {
            Logger.LogInfo("GMP_LOADER_LOADED version=0.16.1 networking=steam_coop");
            smokeMode = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-smoke") >= 0;
            startupProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-startup-probe") >= 0;
            seatProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-seat-probe") >= 0;
            targetProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-target-probe") >= 0;
            netProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-probe") >= 0;
            netAiProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-ai-probe") >= 0;
            netBrainProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-brain-probe") >= 0;
            netDamageProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-damage-probe") >= 0;
            netInputProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-input-probe") >= 0;
            sessionProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-session-probe") >= 0;
            steamProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-steam-probe") >= 0;
            saveProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-save-probe") >= 0;
            coopMenuProbe = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-coop-menu-probe") >= 0;
            steamHostOnly = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-steam-host-only") >= 0;
            netAiProbe |= netBrainProbe;
            netNegative = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-negative") >= 0;
            netStaleNegative = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-stale-negative") >= 0;
            netAuthorityNegative = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-authority-negative") >= 0;
            localPrototype = Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-local-prototype") >= 0;
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (argument.StartsWith("-gmp-run-id=", StringComparison.Ordinal))
                    runId = argument.Substring("-gmp-run-id=".Length);
                else if (argument.StartsWith("-gmp-smoke-min-scenes=", StringComparison.Ordinal))
                    smokeMinScenes = Math.Max(1,
                        int.Parse(argument.Substring("-gmp-smoke-min-scenes=".Length)));
                else if (argument.StartsWith("-gmp-net=", StringComparison.Ordinal))
                    netRole = argument.Substring("-gmp-net=".Length);
                else if (argument.StartsWith("-gmp-net-port=", StringComparison.Ordinal))
                    netPort = int.Parse(argument.Substring("-gmp-net-port=".Length));
                else if (argument.StartsWith("-gmp-session=", StringComparison.Ordinal))
                    sessionRole = argument.Substring("-gmp-session=".Length);
                else if (argument.StartsWith("-gmp-session-port=", StringComparison.Ordinal))
                    sessionPort = int.Parse(argument.Substring("-gmp-session-port=".Length));
                else if (argument.StartsWith("-gmp-steam-role=", StringComparison.Ordinal))
                    steamRole = argument.Substring("-gmp-steam-role=".Length);
                else if (argument.StartsWith("-gmp-steam-lobby=", StringComparison.Ordinal))
                    ulong.TryParse(argument.Substring("-gmp-steam-lobby=".Length), out steamLobby);
                else if (argument.StartsWith("-gmp-steam-invite=", StringComparison.Ordinal))
                    ulong.TryParse(argument.Substring("-gmp-steam-invite=".Length), out steamInvite);
            }
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
            SaveGuard.Install();
            SceneSync.Install();
            SessionRevive.Install();
            SessionSave.CheckLeftovers(Report);
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (smokeMode || seatProbe || targetProbe || netProbe || netAiProbe || netDamageProbe
                || netInputProbe || sessionProbe || steamProbe || saveProbe || coopMenuProbe)
                TestStartup.Install(this,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-normal-startup") < 0);
            if (localPrototype) LocalSeats.Install();
            if (!smokeMode && !seatProbe && !targetProbe && !netProbe && !netAiProbe
                && !netDamageProbe && !netInputProbe && !sessionProbe && !steamProbe
                && !saveProbe)
                coop = new SteamCoop(this);
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
            if (sessionProbe)
            {
                yield return StartCoroutine(RunSessionProbe());
                yield break;
            }
            if (coopMenuProbe)
            {
                yield return NetProbe.LoadOffice(this);
                if (GameManager.instance != null)
                {
                    GameManager.instance.isPaused = false;
                    Time.timeScale = 1f;
                }
                float settle = Time.realtimeSinceStartup + 120f;
                while (Time.realtimeSinceStartup < settle)
                {
                    bool busy = GUIManager.instance != null
                        && GUIManager.instance.IsLoading();
                    if (!busy && UnityEngine.Object.FindObjectOfType<LoadingScreen>() == null
                        && !CutsceneManager.sceneRunning)
                        break;
                    yield return null;
                }
                Report("GMP_COOP_SETTLE loading="
                    + (GUIManager.instance != null && GUIManager.instance.IsLoading())
                    + " loader=" + (UnityEngine.Object.FindObjectOfType<LoadingScreen>() != null)
                    + " cutscene=" + CutsceneManager.sceneRunning
                    + " paused=" + (GameManager.instance != null && GameManager.instance.isPaused)
                    + " scale=" + Time.timeScale);
                yield return new WaitForSecondsRealtime(3f);
                if (coop != null)
                {
                    coop.Host();
                    float lobbyDeadline = Time.realtimeSinceStartup + 20f;
                    while (!coop.HasLobby && Time.realtimeSinceStartup < lobbyDeadline)
                        yield return null;
                    yield return new WaitForSecondsRealtime(1.5f);
                    coop.MenuProbeReady = true;
                }
                float timeout = Time.realtimeSinceStartup + 40f;
                while (coop != null && !coop.MenuCaptured
                    && Time.realtimeSinceStartup < timeout)
                    yield return null;
                yield return new WaitForSecondsRealtime(1f);
                Application.Quit(0);
                yield break;
            }
            if (saveProbe)
            {
                yield return StartCoroutine(SaveProbe.Run(this, runId));
                yield break;
            }
            if (steamProbe)
            {
                yield return StartCoroutine(RunSteamProbe());
                yield break;
            }
            if (netProbe || netAiProbe || netDamageProbe || netInputProbe)
            {
                yield return StartCoroutine(RunNetProbe());
                yield break;
            }
            if (!smokeMode) yield break;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline
                && (!Steamworks.SteamClient.IsValid || sceneCount < smokeMinScenes
                    || (startupProbe && !TestStartup.TitleReady)))
                yield return null;
            bool passed = hooksFound && Steamworks.SteamClient.IsValid && sceneCount >= smokeMinScenes;
            Report("GMP_SMOKE_RESULT " + (passed ? "PASS" : "FAIL")
                + " run=" + runId + " hooks=" + hooksFound + " steam=" + Steamworks.SteamClient.IsValid
                + " scenes=" + sceneCount);
            if (startupProbe)
            {
                bool startupPassed = TestStartup.StingerSeen && TestStartup.Skipped
                    && TestStartup.TitleReady && TestStartup.VideosStopped;
                Report("GMP_STARTUP_RESULT " + (passed && startupPassed ? "PASS" : "FAIL")
                    + " run=" + runId + " stinger=" + TestStartup.StingerSeen
                    + " skipped=" + TestStartup.Skipped + " title=" + TestStartup.TitleReady
                    + " videos_stopped=" + TestStartup.VideosStopped + " seconds="
                    + TestStartup.TitleSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                passed &= startupPassed;
            }
            TestStartup.StopForQuit();
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

        private IEnumerator RunSteamProbe()
        {
            if (steamRole != "host" && steamRole != "guest")
            {
                Report("GMP_STEAM_EXCEPTION run=" + runId + " invalid role " + steamRole);
                Application.Quit(1);
                yield break;
            }
            IEnumerator probe = SteamSessionProbe.Run(this, runId, steamRole,
                steamLobby, steamInvite, steamHostOnly);
            while (true)
            {
                bool next = false;
                Exception failure = null;
                try { next = probe.MoveNext(); }
                catch (Exception exception) { failure = exception; }
                if (failure != null)
                {
                    Logger.LogError("GMP_STEAM_EXCEPTION " + failure);
                    Report("GMP_STEAM_EXCEPTION run=" + runId + " " + failure.GetType().Name
                        + ": " + failure.Message);
                    Application.Quit(1);
                    yield break;
                }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private IEnumerator RunSessionProbe()
        {
            if (sessionRole != "host" && sessionRole != "guest")
            {
                Report("GMP_SESSION_EXCEPTION run=" + runId + " invalid role " + sessionRole);
                Application.Quit(1);
                yield break;
            }
            if (sessionPort < 1024 || sessionPort > 65535)
            {
                Report("GMP_SESSION_EXCEPTION run=" + runId + " invalid port " + sessionPort);
                Application.Quit(1);
                yield break;
            }
            IEnumerator probe = SessionProbe.Run(this, runId, sessionRole, sessionPort);
            while (true)
            {
                bool next = false;
                Exception failure = null;
                try { next = probe.MoveNext(); }
                catch (Exception exception) { failure = exception; }
                if (failure != null)
                {
                    Logger.LogError("GMP_SESSION_EXCEPTION " + failure);
                    Report("GMP_SESSION_EXCEPTION run=" + runId + " " + failure.GetType().Name
                        + ": " + failure.Message);
                    Application.Quit(1);
                    yield break;
                }
                if (!next) yield break;
                yield return probe.Current;
            }
        }

        private IEnumerator RunNetProbe()
        {
            if (netRole != "host" && netRole != "guest")
            {
                Report("GMP_NET_EXCEPTION run=" + runId + " invalid role " + netRole);
                Application.Quit(1);
                yield break;
            }
            if (netPort < 1024 || netPort > 65535)
            {
                Report("GMP_NET_EXCEPTION run=" + runId + " invalid port " + netPort);
                Application.Quit(1);
                yield break;
            }
            IEnumerator probe = netInputProbe
                ? NetInputProbe.Run(this, runId, netRole, netPort,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-input-drop-negative") >= 0,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-input-stale-negative") >= 0,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-input-timeout-negative") >= 0,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-input-snapshot-negative") >= 0)
                : netDamageProbe
                ? NetDamageProbe.Run(this, runId, netRole, netPort,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-damage-target-negative") >= 0,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-damage-duplicate-negative") >= 0)
                : netAiProbe
                ? NetAiProbe.Run(this, runId, netRole, netPort,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-ai-negative") >= 0,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-target-negative") >= 0,
                    netBrainProbe,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-pose-negative") >= 0,
                    Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-net-sim-negative") >= 0)
                : NetProbe.Run(this, runId, netRole, netPort, netNegative,
                    netStaleNegative, netAuthorityNegative);
            while (true)
            {
                bool next = false;
                Exception failure = null;
                try { next = probe.MoveNext(); }
                catch (Exception exception) { failure = exception; }
                if (failure != null)
                {
                    Logger.LogError("GMP_NET_EXCEPTION " + failure);
                    Report("GMP_NET_EXCEPTION run=" + runId + " " + failure.GetType().Name
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
            if (coop != null) coop.OnSceneLoaded();
            Report("GMP_SCENE_LOADED name=" + scene.name + " count=" + sceneCount);
            if (localPrototype)
            {
                LocalSeats.Clear();
                LocalSeats.Install();
            }
        }

        private void Update()
        {
            NetworkInputState.Frame = Time.frameCount;
            Reconciliation.Tick(Time.deltaTime);
            if (coop != null)
            {
                coop.Tick();
                if (Input.GetKeyDown(KeyCode.F7)) { coop.Toggle(); showCoop = coop.Visible; }
                if (showCoop != coop.Visible) showCoop = coop.Visible;
            }
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
            if (seatProbe || smokeMode || targetProbe || netProbe || netAiProbe || netDamageProbe
                || netInputProbe || sessionProbe || steamProbe || saveProbe || coopMenuProbe)
                File.AppendAllText(Path.Combine(BepInEx.Paths.GameRootPath, "gmp-trace-" + runId + ".log"),
                    text + Environment.NewLine);
        }
        private void OnGUI()
        {
            if (showCoop && coop != null) coop.DrawGui();
            SessionRevive.DrawGui();
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
            if (coop != null) coop.Shutdown();
            LocalSeats.Clear();
        }
    }
}
