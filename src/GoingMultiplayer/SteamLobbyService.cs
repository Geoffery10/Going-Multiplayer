using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

namespace GoingMultiplayer
{
    internal enum SteamLobbyState { Idle, Creating, Joining, InLobby, Failed, Closed }

    // Owns the Steam lobby lifecycle and marshals Steam callbacks onto the Unity
    // main thread. It does not start the game session; callers own NetSession.
    internal sealed class SteamLobbyService
    {
        private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private bool installed;

        internal SteamLobbyState State { get; private set; }
        internal Lobby? Current { get; private set; }
        internal string LastError { get; private set; }
        internal ulong PendingInvite { get; private set; }
        internal bool AutoAcceptInvites;
        internal Action<Lobby> Entered = delegate { };
        internal Action<string> Failed = delegate { };
        internal Action Changed = delegate { };

        internal bool HasLobby
        {
            get { return Current.HasValue && Current.Value.Id.Value != 0; }
        }

        internal static ulong CommandLineLobby()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase))
                {
                    ulong value;
                    if (ulong.TryParse(args[i + 1], out value)) return value;
                }
                if (args[i].StartsWith("+connect_lobby=", StringComparison.OrdinalIgnoreCase))
                {
                    ulong value;
                    if (ulong.TryParse(args[i].Substring("+connect_lobby=".Length), out value))
                        return value;
                }
            }
            return 0;
        }

        internal void Install()
        {
            if (installed) return;
            installed = true;
            SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
            SteamMatchmaking.OnLobbyInvite += OnLobbyInvite;
            SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberChanged;
        }

        internal void Pump()
        {
            Action action;
            while (mainThread.TryDequeue(out action))
            {
                try { action(); }
                catch (Exception exception) { Fail(exception.GetType().Name + ": " + exception.Message); }
            }
        }

        internal Task<Lobby?> Create(int maxMembers, string build, string gameVersion, string session)
        {
            if (!SteamClient.IsValid) { Fail("steam-not-ready"); return null; }
            State = SteamLobbyState.Creating;
            try
            {
                Task<Lobby?> task = SteamMatchmaking.CreateLobbyAsync(maxMembers);
                task.ContinueWith(delegate(Task<Lobby?> completed)
                {
                    if (completed.IsFaulted || !completed.Result.HasValue)
                    {
                        Enqueue(delegate { Fail("create-lobby"); });
                        return;
                    }
                    Lobby lobby = completed.Result.Value;
                    Enqueue(delegate { Adopt(lobby, true, build, gameVersion, session); });
                });
                return task;
            }
            catch (Exception exception)
            {
                Fail(exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        internal Task<Lobby?> Join(ulong lobbyId, string build, string gameVersion)
        {
            if (!SteamClient.IsValid) { Fail("steam-not-ready"); return null; }
            if (lobbyId == 0) { Fail("invalid-lobby-id"); return null; }
            State = SteamLobbyState.Joining;
            try
            {
                Task<Lobby?> task = SteamMatchmaking.JoinLobbyAsync((SteamId)lobbyId);
                task.ContinueWith(delegate(Task<Lobby?> completed)
                {
                    if (completed.IsFaulted || !completed.Result.HasValue)
                    {
                        Enqueue(delegate { Fail("join-lobby"); });
                        return;
                    }
                    Lobby lobby = completed.Result.Value;
                    Enqueue(delegate { Adopt(lobby, false, build, gameVersion, null); });
                });
                return task;
            }
            catch (Exception exception)
            {
                Fail(exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        internal void Leave()
        {
            if (Current.HasValue)
            {
                try { Current.Value.Leave(); }
                catch (Exception) { }
            }
            Current = null;
            State = SteamLobbyState.Closed;
            if (Changed != null) Changed();
        }

        internal bool OpenInviteOverlay()
        {
            if (!HasLobby) return false;
            try
            {
                SteamFriends.OpenGameInviteOverlay(Current.Value.Id);
                return true;
            }
            catch (Exception exception)
            {
                Fail(exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        internal bool Invite(SteamId friend)
        {
            if (!HasLobby || friend.Value == 0) return false;
            try { return Current.Value.InviteFriend(friend); }
            catch (Exception exception)
            {
                Fail(exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        internal IEnumerable<Friend> Members
        {
            get
            {
                if (!HasLobby) return new Friend[0];
                try { return Current.Value.Members; }
                catch (Exception) { return new Friend[0]; }
            }
        }

        internal SteamId Owner
        {
            get
            {
                if (!HasLobby) return default(SteamId);
                try { return Current.Value.Owner.Id; }
                catch (Exception) { return default(SteamId); }
            }
        }

        internal SteamId FirstOtherMember()
        {
            foreach (Friend member in Members)
                if (!member.IsMe) return member.Id;
            return default(SteamId);
        }

        internal int MemberCount
        {
            get
            {
                if (!HasLobby) return 0;
                try { return Current.Value.MemberCount; }
                catch (Exception) { return 0; }
            }
        }

        internal void Reset()
        {
            Current = null;
            State = SteamLobbyState.Idle;
            LastError = null;
            PendingInvite = 0;
            if (Changed != null) Changed();
        }

        internal void Shutdown()
        {
            Leave();
            if (installed)
            {
                SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
                SteamMatchmaking.OnLobbyInvite -= OnLobbyInvite;
                SteamMatchmaking.OnLobbyEntered -= OnLobbyEntered;
                SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberChanged;
                SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberChanged;
                installed = false;
            }
        }

        private void OnGameLobbyJoinRequested(Lobby lobby, SteamId friend)
        {
            Enqueue(delegate
            {
                PendingInvite = lobby.Id.Value;
                Join(lobby.Id.Value, null, null);
            });
        }

        private void OnLobbyInvite(Friend friend, Lobby lobby)
        {
            Enqueue(delegate
            {
                PendingInvite = lobby.Id.Value;
                if (AutoAcceptInvites) Join(lobby.Id.Value, null, null);
                if (Changed != null) Changed();
            });
        }

        private void OnLobbyEntered(Lobby lobby)
        {
            // The JoinLobbyAsync result also adopts the lobby. This callback is a
            // safety net for a join accepted through the overlay.
            Enqueue(delegate
            {
                if (!HasLobby || Current.Value.Id.Value != lobby.Id.Value)
                    Adopt(lobby, lobby.Owner.Id.Value == SteamClient.SteamId.Value, null, null, null);
            });
        }

        private void OnLobbyMemberChanged(Lobby lobby, Friend member)
        {
            Enqueue(delegate { if (Changed != null) Changed(); });
        }

        private void Adopt(Lobby lobby, bool owner, string build, string gameVersion,
            string session)
        {
            Current = lobby;
            State = SteamLobbyState.InLobby;
            LastError = null;
            if (owner)
            {
                try
                {
                    lobby.SetFriendsOnly();
                    lobby.SetJoinable(true);
                    if (!string.IsNullOrEmpty(build)) lobby.SetData("build", build);
                    if (!string.IsNullOrEmpty(gameVersion)) lobby.SetData("gameVersion", gameVersion);
                    if (!string.IsNullOrEmpty(session)) lobby.SetData("session", session);
                    lobby.SetData("protocol", "1");
                    lobby.SetData("mode", "goingmultiplayer");
                    // Facepunch's rich-presence dictionary is initialised by the
                    // friends interface; touch it before setting the connect key.
                    foreach (Friend ignored in SteamFriends.GetFriends()) { }
                    SteamFriends.SetRichPresence("connect", "+connect_lobby " + lobby.Id.Value);
                    SteamFriends.SetRichPresence("status", "In co-op lobby");
                }
                catch (Exception exception)
                {
                    Fail(exception.GetType().Name + ": " + exception.Message);
                }
            }
            if (Entered != null) Entered(lobby);
            if (Changed != null) Changed();
        }

        private void Enqueue(Action action)
        {
            if (action != null) mainThread.Enqueue(action);
        }

        private void Fail(string reason)
        {
            LastError = reason;
            State = SteamLobbyState.Failed;
            if (Failed != null) Failed(reason);
            if (Changed != null) Changed();
        }
    }
}
