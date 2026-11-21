using System;
using System.Collections.Generic;
using BepInEx;
using System.IO;
using System.Security.Cryptography;
using Steamworks;
using UnityEngine;

namespace GoingMultiplayer
{
    // Player-facing M2/M3 bridge: owns a Steam lobby and a persistent
    // NetSession outside the automated probes.
    internal sealed class SteamCoop
    {
        private enum CoopState { Idle, Creating, Joining, Running, Failed }

        private readonly Plugin plugin;
        private readonly SteamLobbyService lobby = new SteamLobbyService();
        private SteamP2PTransport transport;
        private NetSession session;
        private CoopState state;
        private bool isHost;
        private bool commandLineHandled;
        private bool installed;
        private string status = "Steam co-op idle. Press F7 for the panel.";
        private string build;
        private float lastSnapshot = -1f;
        private float lastCommand = -1f;
        private bool initialSyncSent;
        private string pendingScene;
        private string hostScene;
        private Vector3 lastLocalPosition;
        private bool hasLocalPosition;
        private bool forcedBackground;
        private float lastSceneBeat;
        private float lastStats;
        private int statCorrections;
        private int statHard;
        private int remoteId;
        private NetworkActor remote;
        private bool kicked;
        private bool pauseTabInstalled;
        private bool menuProbe;
        private bool uiDump;
        private bool menuOpened;
        private bool menuCaptured;
        private float menuCaptureAt;
        private int menuRetries;

        internal bool MenuCaptured { get { return menuCaptured; } }
        internal bool MenuProbeReady { get; set; }

        internal SteamCoop(Plugin plugin)
        {
            this.plugin = plugin;
            menuProbe = Array.IndexOf(Environment.GetCommandLineArgs(),
                "-gmp-coop-menu-probe") >= 0;
            uiDump = Array.IndexOf(Environment.GetCommandLineArgs(),
                "-gmp-ui-dump") >= 0;
            using (var hash = SHA256.Create())
                build = BitConverter.ToString(hash.ComputeHash(
                    File.ReadAllBytes(typeof(Plugin).Assembly.Location)))
                    .Replace("-", "").ToLowerInvariant();
        }

        internal bool Visible { get; private set; }
        internal string Status { get { return status; } }
        internal bool IsHost { get { return isHost; } }

        internal struct CoopMember
        {
            internal SteamId Id;
            internal string Name;
            internal bool IsHost;
            internal bool IsMe;
        }

        internal List<CoopMember> Members
        {
            get
            {
                var list = new List<CoopMember>();
                if (!lobby.HasLobby) return list;
                SteamId owner = lobby.Owner;
                foreach (Friend member in lobby.Members)
                {
                    list.Add(new CoopMember
                    {
                        Id = member.Id,
                        Name = string.IsNullOrEmpty(member.Name) ? member.Id.Value.ToString() : member.Name,
                        IsHost = member.Id.Value == owner.Value,
                        IsMe = member.IsMe,
                    });
                }
                return list;
            }
        }
        internal string LobbyId
        {
            get
            {
                return lobby.HasLobby ? lobby.Current.Value.Id.Value.ToString() : "";
            }
        }

        internal bool HasLobby { get { return lobby.HasLobby; } }

        internal void Toggle()
        {
            Visible = !Visible;
        }

        internal void Tick()
        {
            if (!SteamClient.IsValid)
            {
                status = "Steam is not ready.";
                return;
            }
            if (!installed)
            {
                lobby.Install();
                installed = true;
            }
            lobby.Pump();
            if (!pauseTabInstalled && GUIManager.instance != null
                && GUIManager.instance.pauseMenu is PauseMenu)
            {
                bool ok;
                string detail = CoopPauseTab.Install(
                    (PauseMenu)GUIManager.instance.pauseMenu, this, out ok);
                pauseTabInstalled = ok;
                if (ok && detail.IndexOf("already installed") < 0)
                    plugin.Report("GMP_COOP_TAB installed" + detail);
            }
            if (menuProbe && MenuProbeReady && pauseTabInstalled && !menuOpened
                && Player.singlePlayer != null
                && GUIManager.instance != null
                && GUIManager.instance.pauseMenu is PauseMenu)
            {
                PauseMenu pause = (PauseMenu)GUIManager.instance.pauseMenu;
                if (uiDump) CoopPauseTab.Dump(pause.transform, plugin.Report, 500);
                pause.Open();
                int last = pause.tabs.Count - 1;
                // Drive the real click path: the cloned button must resolve its
                // parent pause menu and switch tabs.
                GameObject buttonObject = GameObject.Find("GMP Co-op Tab Button");
                PauseTabButton tabButton = buttonObject != null
                    ? buttonObject.GetComponent<PauseTabButton>() : null;
                if (tabButton != null && tabButton.button != null)
                    tabButton.button.onClick.Invoke();
                else
                    pause.SetTab(last, false);
                menuOpened = true;
                menuCaptureAt = Time.realtimeSinceStartup + 2f;
                plugin.Report("GMP_COOP_MENU open=True tabs=" + pause.tabs.Count
                    + " tab=" + (last + 1) + " active=" + pause.TabIsActive(last)
                    + " tab_name=" + pause.tabs[last].name);
            }
            if (menuOpened && !menuCaptured && Time.realtimeSinceStartup >= menuCaptureAt)
            {
                PauseMenu pause = GUIManager.instance != null
                    ? GUIManager.instance.pauseMenu as PauseMenu : null;
                if (pause != null && !pause.gameObject.activeInHierarchy && menuRetries < 3)
                {
                    menuRetries++;
                    pause.Open();
                    GameObject buttonObject = GameObject.Find("GMP Co-op Tab Button");
                    PauseTabButton tabButton = buttonObject != null
                        ? buttonObject.GetComponent<PauseTabButton>() : null;
                    if (tabButton != null && tabButton.button != null)
                        tabButton.button.onClick.Invoke();
                    menuCaptureAt = Time.realtimeSinceStartup + 1.5f;
                    plugin.Report("GMP_COOP_MENU reopen count=" + menuRetries);
                }
                else
                {
                    string image = Path.Combine(BepInEx.Paths.GameRootPath,
                        "gmp-coop-menu.png");
                    ScreenCapture.CaptureScreenshot(image);
                    menuCaptured = true;
                    if (pause != null && pause.tabs.Count > 0)
                        plugin.Report("GMP_COOP_MENU capture tab_active="
                            + pause.TabIsActive(pause.tabs.Count - 1)
                            + " open=" + pause.gameObject.activeInHierarchy
                            + " name=" + pause.tabs[pause.tabs.Count - 1].name);
                }
            }

            if (!commandLineHandled)
            {
                commandLineHandled = true;
                ulong id = SteamLobbyService.CommandLineLobby();
                if (id != 0) Join(id);
                else if (Array.IndexOf(Environment.GetCommandLineArgs(), "-gmp-coop-host") >= 0)
                    Host();
            }

            if ((state == CoopState.Creating || state == CoopState.Joining) && lobby.HasLobby)
                BeginSession();
            else if (state == CoopState.Idle && lobby.HasLobby)
            {
                // A lobby was entered through the Steam invite callback.
                isHost = lobby.Owner.Value == SteamClient.SteamId.Value;
                BeginSession();
            }

            if (state != CoopState.Running || session == null) return;

            session.Pump(Time.realtimeSinceStartup);
            if (kicked)
            {
                kicked = false;
                status = "Kicked by the host.";
                Leave();
                return;
            }
            if (session.State == SessionState.Closed || session.State == SessionState.Ending)
            {
                status = "Session ended.";
                Stop();
                return;
            }
            if (session.State != SessionState.Playing) return;

            EnsureRemoteActor();
            Player local = Player.singlePlayer;
            if (!local) return;
            float now = Time.realtimeSinceStartup;
            SessionRevive.Active = true;
            Player partner = remote != null && remote.Seat != null ? remote.Seat.Player : null;
            SessionRevive.Tick(local, partner, remoteId,
                delegate(int id, float amount)
                {
                    session.SendRevive(id, amount, Time.realtimeSinceStartup);
                    plugin.Report("GMP_COOP_REVIVE_SEND target=" + id
                        + " amount=" + amount.ToString("F0", Culture));
                });
            if (isHost && now - lastSceneBeat >= 2f)
            {
                lastSceneBeat = now;
                // Re-announce with the seed the current floor was built from, so
                // a guest that reloads the scene regenerates the same layout.
                session.SendScene(UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name, SceneSync.LastAppliedSeed, now);
            }
            if (now - lastStats >= 10f)
            {
                lastStats = now;
                ReportStats(now);
            }
            // A scripted move (special door, elevator) moves only the local
            // player; take the party with it.
            if (hasLocalPosition)
            {
                Vector3 delta = local.transform.position - lastLocalPosition;
                delta.y = 0f;
                if (delta.magnitude > 4f)
                {
                    if (isHost)
                    {
                        foreach (NetworkActor actor in ActorRegistry.All)
                            if (actor.Simulated && actor.Seat != null && actor.Seat.Player)
                                Move(actor.Seat.Player,
                                    actor.Seat.Player.transform.position + delta);
                        plugin.Report("GMP_COOP_TELEPORT role=host delta="
                            + delta.x.ToString("F2", Culture) + ","
                            + delta.z.ToString("F2", Culture));
                    }
                    else
                    {
                        // Ask the host to bring the party along.
                        plugin.Report("GMP_COOP_TELEPORT role=guest request delta="
                            + delta.x.ToString("F2", Culture) + ","
                            + delta.z.ToString("F2", Culture));
                    }
                    session.SendTeleport(delta.x, delta.y, delta.z, now);
                }
            }
            lastLocalPosition = local.transform.position;
            hasLocalPosition = true;
            if (isHost && !initialSyncSent)
            {
                initialSyncSent = true;
                byte[] save = ReadSaveSlot();
                if (save != null && save.Length > 0)
                {
                    session.SendSave(save, now);
                    plugin.Report("GMP_COOP_SAVE_SEND bytes=" + save.Length
                        + " hash=" + SessionSave.Hash(save));
                }
                string scene = UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name;
                int seed = SceneSync.LastAppliedSeed;
                session.SendScene(scene, seed, now);
                plugin.Report("GMP_COOP_SCENE_SEND name=" + scene + " seed=" + seed
                    + " initial=True");
            }
            if (now - lastSnapshot >= 0.05f)
            {
                session.SendSnapshot(ActorRegistry.Capture(local), now);
                if (isHost)
                {
                    // The host owns the guest's actor; publish its state so the
                    // guest can render its own puppet from the authority.
                    foreach (NetworkActor actor in ActorRegistry.All)
                        if (actor.Simulated && actor.Seat != null && actor.Seat.Player)
                            session.SendSnapshotFor(actor.PlayerId,
                                ActorRegistry.Capture(actor.Seat.Player), now);
                }
                lastSnapshot = now;
            }
            if (now - lastCommand >= 0.033f)
            {
                session.SendCommand(InputCapture.From(local), now);
                lastCommand = now;
            }
        }

        internal void Host()
        {
            if (!SteamClient.IsValid) { status = "Steam is not ready."; return; }
            Stop();
            KeepRunningInBackground();
            isHost = true;
            state = CoopState.Creating;
            lobby.Install();
            lobby.AutoAcceptInvites = false;
            lobby.Create(4, build, Application.unityVersion, Guid.NewGuid().ToString("N"));
            status = "Creating Steam lobby...";
            plugin.Report("GMP_COOP_HOST_START build=" + build);
        }

        private void KeepRunningInBackground()
        {
            // Online co-op must keep simulating while the window is out of
            // focus, otherwise Steam callbacks, the session pump and the game
            // itself freeze until the window is focused again.
            if (Application.runInBackground) return;
            Application.runInBackground = true;
            forcedBackground = true;
        }

        internal void Join(ulong lobbyId)
        {
            if (!SteamClient.IsValid) { status = "Steam is not ready."; return; }
            if (lobbyId == 0) { status = "No lobby id in the clipboard."; return; }
            Stop();
            KeepRunningInBackground();
            isHost = false;
            state = CoopState.Joining;
            lobby.Install();
            lobby.AutoAcceptInvites = false;
            lobby.Join(lobbyId, build, Application.unityVersion);
            status = "Joining Steam lobby " + lobbyId + "...";
            plugin.Report("GMP_COOP_JOIN_START lobby=" + lobbyId);
        }

        internal void JoinClipboard()
        {
            ulong id;
            if (ulong.TryParse(GUIUtility.systemCopyBuffer, out id)) Join(id);
            else status = "Clipboard does not contain a numeric lobby id.";
        }

        internal void Invite()
        {
            if (!lobby.HasLobby) { status = "Create or join a lobby first."; return; }
            lobby.OpenInviteOverlay();
            status = "Steam invite overlay requested.";
        }

        internal void Kick(SteamId member)
        {
            if (!isHost || session == null)
            {
                status = "Only the host can kick a player.";
                return;
            }
            Peer peer;
            if (!session.Peers.TryByKey(member.Value.ToString(), out peer))
            {
                status = "That player is not connected yet.";
                return;
            }
            session.KickPeer(peer);
            if (remote != null && peer.PlayerId == remoteId)
            {
                ActorRegistry.Remove(remoteId);
                remote = null;
            }
            status = "Kicked a player from the co-op session.";
            plugin.Report("GMP_COOP_KICK peer=" + peer.PlayerId);
        }

        internal void Leave()
        {
            Stop();
            status = "Left Steam co-op.";
        }

        internal void CopyLobbyId()
        {
            if (lobby.HasLobby) GUIUtility.systemCopyBuffer = LobbyId;
        }

        internal void OnSceneLoaded()
        {
            pauseTabInstalled = false;
            hasLocalPosition = false;
            if (session == null || state != CoopState.Running) return;
            if (!isHost && session.State == SessionState.Playing)
            {
                string guestScene = UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name;
                if (!string.IsNullOrEmpty(hostScene) && guestScene != hostScene)
                {
                    session.SendSceneIntent(guestScene, Time.realtimeSinceStartup);
                    plugin.Report("GMP_COOP_SCENE_INTENT name=" + guestScene
                        + " host=" + hostScene);
                }
            }
            if (isHost && session.State == SessionState.Playing)
            {
                string scene = UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name;
                int seed = SceneSync.NextSeed();
                SceneSync.PendingSeed = seed;
                session.SendScene(scene, seed, Time.realtimeSinceStartup);
                plugin.Report("GMP_COOP_SCENE_SEND name=" + scene + " seed=" + seed);
            }
            Player local = Player.singlePlayer;
            if (!local) return;
            Vector3 spawn;
            try { spawn = SeatProbe.FindSpawn(local); }
            catch (Exception) { spawn = local.transform.position + new Vector3(1.5f, 0f, 0f); }
            ActorRegistry.RebindAll(spawn);
            remote = ActorRegistry.Get(remoteId);
            pauseTabInstalled = false;
        }

        internal void Shutdown()
        {
            Stop();
            lobby.Shutdown();
        }

        internal void DrawGui()
        {
            GUI.Box(new Rect(20, 20, 500, 255), "Steam Co-op (experimental)");
            GUI.Label(new Rect(35, 45, 470, 40), status);
            GUI.Label(new Rect(35, 85, 470, 25), "Lobby: " + (lobby.HasLobby ? LobbyId : "-")
                + "   Role: " + (isHost ? "host" : "guest"));
            GUI.Label(new Rect(35, 110, 470, 25), "Session: "
                + (session != null ? session.State.ToString() : "none")
                + "   Peers: " + (session != null ? session.Peers.Count : 0));
            if (GUI.Button(new Rect(35, 145, 100, 30), "Host")) Host();
            if (GUI.Button(new Rect(145, 145, 140, 30), "Join clipboard")) JoinClipboard();
            if (GUI.Button(new Rect(295, 145, 100, 30), "Invite")) Invite();
            if (GUI.Button(new Rect(405, 145, 70, 30), "Leave")) Leave();
            if (lobby.HasLobby && GUI.Button(new Rect(35, 185, 120, 30), "Copy lobby id"))
                CopyLobbyId();
            GUI.Label(new Rect(165, 190, 320, 25), "F7 hides this panel");
        }

        private void BeginSession()
        {
            if (!lobby.HasLobby)
            {
                Fail("Lobby unavailable.");
                return;
            }
            string hostBuild = lobby.Current.Value.GetData("build");
            if (!string.IsNullOrEmpty(hostBuild) && hostBuild != build)
            {
                Fail("Build mismatch: host " + hostBuild + " local " + build);
                return;
            }
            try
            {
                remoteId = isHost ? 2 : 1;
                transport = new SteamP2PTransport();
                var config = new SessionConfig
                {
                    Build = build,
                    GameVersion = Application.unityVersion,
                    PlayerName = SteamClient.Name,
                    MaxPlayers = 4,
                    PeerTimeoutSeconds = 30f,
                };
                session = new NetSession(config, transport);
                session.SnapshotReceived += delegate(Peer peer, PlayerSnapshot snapshot)
                {
                    ActorRegistry.ApplySnapshot(peer.PlayerId, snapshot);
                };
                session.OwnSnapshotReceived += delegate(PlayerSnapshot snapshot)
                {
                    // The host's authoritative pose for this player.
                    Reconciliation.Apply(Player.singlePlayer, snapshot);
                };
                session.CommandReceived += delegate(Peer peer, InputCommand command)
                {
                    // The host simulates the guest from these commands. The
                    // guest also feeds them to the host's puppet: its transform
                    // stays snapshot-driven, but the native animator gets the
                    // real input, so the host's animations are visible.
                    NetworkActor actor = ActorRegistry.Get(peer.PlayerId);
                    if (actor != null && actor.Seat != null && actor.Seat.Network != null)
                        actor.Seat.Network.Apply(command, peer.LastSequence);
                };
                session.SaveReceived += delegate(byte[] bytes)
                {
                    if (isHost) return;
                    SessionSave.Install(bytes, plugin.Report);
                    if (!string.IsNullOrEmpty(pendingScene))
                    {
                        string scene = pendingScene;
                        pendingScene = null;
                        ApplyHostScene(scene);
                    }
                };
                session.SceneReceived += delegate(string scene, int seed)
                {
                    if (isHost) return;
                    hostScene = scene;
                    if (seed != 0) SceneSync.PendingSeed = seed;
                    ApplyHostScene(scene);
                };
                session.ReviveReceived += delegate(int targetId, float amount)
                {
                    if (session == null || targetId != session.LocalPlayerId) return;
                    SessionRevive.Revive(Player.singlePlayer, amount);
                    plugin.Report("GMP_COOP_REVIVED amount=" + amount.ToString("F0",
                        Culture));
                };
                session.TeleportReceived += delegate(float dx, float dy, float dz)
                {
                    Vector3 delta = new Vector3(dx, dy, dz);
                    Player local = Player.singlePlayer;
                    if (isHost)
                    {
                        // A guest opened a scripted door: move the party too.
                        if (local != null)
                        {
                            Move(local, local.transform.position + delta);
                            lastLocalPosition = local.transform.position;
                        }
                        foreach (NetworkActor actor in ActorRegistry.All)
                            if (actor.Simulated && actor.Seat != null && actor.Seat.Player)
                                Move(actor.Seat.Player,
                                    actor.Seat.Player.transform.position + delta);
                        plugin.Report("GMP_COOP_TELEPORT role=host from=guest delta="
                            + delta.x.ToString("F2", Culture) + ","
                            + delta.z.ToString("F2", Culture));
                        return;
                    }
                    if (local == null) return;
                    Move(local, local.transform.position + delta);
                    // Suppress our own jump detection: this delta came from the
                    // host and must not be echoed back.
                    lastLocalPosition = local.transform.position;
                    plugin.Report("GMP_COOP_TELEPORT role=guest delta="
                        + delta.x.ToString("F2", Culture) + ","
                        + delta.y.ToString("F2", Culture) + ","
                        + delta.z.ToString("F2", Culture));
                };
                session.SceneIntentReceived += delegate(string scene)
                {
                    if (!isHost) return;
                    if (string.IsNullOrEmpty(scene))
                    {
                        return;
                    }
                    if (string.Equals(scene, UnityEngine.SceneManagement.SceneManager
                        .GetActiveScene().name, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (!Application.CanStreamedLevelBeLoaded(scene))
                    {
                        plugin.Report("GMP_COOP_INTENT_MISSING name=" + scene);
                        return;
                    }
                    plugin.Report("GMP_COOP_INTENT_FOLLOW name=" + scene);
                    GUIManager.instance.LoadScene(scene);
                };
                session.Kicked += delegate(Peer peer) { kicked = true; };
                session.PeerLeft += delegate(Peer peer)
                {
                    if (remote != null && peer.PlayerId == remoteId)
                    {
                        ActorRegistry.Remove(remoteId);
                        remote = null;
                    }
                    status = "A player left the session.";
                };
                if (isHost) session.StartHost(0, Time.realtimeSinceStartup);
                else
                {
                    if (lobby.Owner.Value == 0) { Fail("Lobby owner is missing."); return; }
                    transport.SetHost(lobby.Owner);
                    session.StartGuest(0, Time.realtimeSinceStartup);
                }
                KeepRunningInBackground();
                state = CoopState.Running;
                status = isHost ? "Hosting Steam co-op." : "Joined Steam co-op.";
                plugin.Report("GMP_COOP_LOBBY " + LobbyId);
                plugin.Report("GMP_COOP_SESSION role=" + (isHost ? "host" : "guest")
                    + " lobby=" + LobbyId + " local_id=" + session.LocalPlayerId);
            }
            catch (Exception exception)
            {
                Fail(exception.GetType().Name + ": " + exception.Message);
            }
        }

        private void EnsureRemoteActor()
        {
            Player local = Player.singlePlayer;
            if (!local) return;
            if (remote != null && remote.Seat != null && remote.Seat.Player) return;
            if (remote != null)
            {
                ActorRegistry.Remove(remoteId);
                remote = null;
            }
            try
            {
                LocalSeats.Install();
                Vector3 spawn;
                try { spawn = SeatProbe.FindSpawn(local); }
                catch (Exception) { spawn = local.transform.position + new Vector3(1.5f, 0f, 0f); }
                remote = ActorRegistry.AddRemote(local, remoteId, spawn, isHost);
                status = "Connected to the other player.";
                plugin.Report("GMP_COOP_REMOTE role=" + (isHost ? "host" : "guest")
                    + " local_id=" + session.LocalPlayerId + " remote_id=" + remoteId);
            }
            catch (Exception exception)
            {
                status = "Remote actor failed: " + exception.Message;
            }
        }

        private void Stop()
        {
            if (session != null)
            {
                session.Stop("coop-stop");
                session = null;
            }
            if (transport != null)
            {
                transport.Close();
                transport = null;
            }
            SessionRevive.Reset();
            if (!isHost) SessionSave.Restore(plugin.Report);
            if (forcedBackground)
            {
                Application.runInBackground = false;
                forcedBackground = false;
            }
            initialSyncSent = false;
            lastSceneBeat = 0f;
            lastStats = 0f;
            pendingScene = null;
            ActorRegistry.Reset();
            remote = null;
            lobby.Leave();
            state = CoopState.Idle;
            lastSnapshot = -1f;
            lastCommand = -1f;
        }

        private void ReportStats(float now)
        {
            int corrections = Reconciliation.Corrections - statCorrections;
            int hard = Reconciliation.HardSnaps - statHard;
            statCorrections = Reconciliation.Corrections;
            statHard = Reconciliation.HardSnaps;
            Peer peer = null;
            foreach (Peer candidate in session.Peers.All) peer = candidate;
            plugin.Report("GMP_COOP_STATS role=" + (isHost ? "host" : "guest")
                + " scene=" + UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name
                + " corrections=" + corrections
                + " hard=" + hard
                + " error=" + Reconciliation.LastError.ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture)
                + " rtt=" + (peer != null ? peer.RttMs.ToString("F0",
                    System.Globalization.CultureInfo.InvariantCulture) : "none")
                + " peers=" + session.Peers.Count
                + " commands=" + session.CommandsReceived
                + " focus=" + (Player.singlePlayer != null && Player.singlePlayer.focusing));
        }

        private static readonly System.Globalization.CultureInfo Culture =
            System.Globalization.CultureInfo.InvariantCulture;

        private static void Move(Player player, Vector3 position)
        {
            player.transform.position = position;
            if (player.rb)
            {
                player.rb.position = position;
                player.rb.velocity = Vector3.zero;
            }
        }

        private static byte[] ReadSaveSlot()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath,
                    SaveLoad.GetSavePath(SaveLoad.CURRENT_SAVE_INDEX));
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception) { return null; }
        }

        private void ApplyHostScene(string name)
        {
            if (isHost || string.IsNullOrEmpty(name)) return;
            if (session != null && session.SaveInProgress)
            {
                pendingScene = name;
                return;
            }
            string active = UnityEngine.SceneManagement.SceneManager
                .GetActiveScene().name;
            if (active == name) return;
            if (!Application.CanStreamedLevelBeLoaded(name))
            {
                status = "Host is in an unknown scene: " + name;
                plugin.Report("GMP_COOP_SCENE_MISSING name=" + name);
                return;
            }
            plugin.Report("GMP_COOP_SCENE_LOAD name=" + name);
            status = "Loading the host's scene: " + name;
            GUIManager.instance.LoadScene(name);
        }

        private void Fail(string reason)
        {
            status = "Steam co-op failed: " + reason;
            plugin.Report("GMP_COOP_FAIL reason=" + reason);
            Stop();
            state = CoopState.Failed;
        }
    }
}
