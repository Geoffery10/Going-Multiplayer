using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using GoingMultiplayer;
using Socket = System.Net.Sockets.Socket;

// Standalone Steam/UDP adapter harness for M0a. UDP mode runs under Wine and
// needs no Steam account. Steam mode runs under Proton with steam_appid.txt=480
// and is intended for two test accounts; it is not part of the game.
internal static class SteamHarness
{
    private static string runId = "harness";
    private static string session = "";
    private static int pings;
    private static int pongs;
    private static float rttMs = -1f;
    private static string lastReason = "";
    private static int memberCount;
    private static bool sameAccount;
    private static bool lobbyMemberJoined;

    private static int Main(string[] args)
    {
        string transport = "udp";
        string role = "host";
        int port = 39001;
        int seconds = 20;
        ulong lobbyId = 0;
        ulong invite = 0;
        bool overlay = false;
        bool allowSameAccount = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--transport" && i + 1 < args.Length) transport = args[++i];
            else if (arg == "--role" && i + 1 < args.Length) role = args[++i];
            else if (arg == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
            else if (arg == "--seconds" && i + 1 < args.Length) int.TryParse(args[++i], out seconds);
            else if (arg == "--lobby" && i + 1 < args.Length) ulong.TryParse(args[++i], out lobbyId);
            else if (arg == "--invite" && i + 1 < args.Length) ulong.TryParse(args[++i], out invite);
            else if (arg == "--run-id" && i + 1 < args.Length) runId = args[++i];
            else if (arg == "--overlay") overlay = true;
            else if (arg == "--allow-same-account") allowSameAccount = true;
        }
        if (role != "host" && role != "guest")
        {
            Console.WriteLine("GMP_HARNESS_RESULT FAIL transport=" + transport
                + " role=" + role + " reason=invalid-role");
            return 2;
        }
        if (seconds < 3 || seconds > 120) seconds = 20;
        try
        {
            if (transport == "udp") return RunUdp(role, port, seconds);
            if (transport == "steam") return RunSteam(role, seconds, lobbyId, invite, overlay, allowSameAccount);
            if (transport == "m2") return RunM2(role, seconds, lobbyId, invite);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            lastReason = exception.GetType().Name + ":" + exception.Message.Replace(' ', '-');
        }
        Report("FAIL", transport, role, 0, 0);
        return 1;
    }

    private static int RunUdp(string role, int port, int seconds)
    {
        bool host = role == "host";
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (host)
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                session = Guid.NewGuid().ToString("N");
            }
            else
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }
            socket.Blocking = false;
            EndPoint peer = host ? null : new IPEndPoint(IPAddress.Loopback, port);
            var buffer = new byte[512];
            double deadline = Now() + seconds;
            double nextSend = 0;
            int sequence = 0;
            bool connected = false;
            while (Now() < deadline)
            {
                if (socket.Poll(15, SelectMode.SelectRead))
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int count;
                    try { count = socket.ReceiveFrom(buffer, ref from); }
                    catch (SocketException) { count = 0; }
                    if (count > 0)
                    {
                        string message = Encoding.ASCII.GetString(buffer, 0, count);
                        string[] parts = message.Split('|');
                        if (parts.Length >= 3 && parts[1] == "HELLO" && parts[2] == runId)
                        {
                            peer = from;
                            if (host) Send(socket, peer, "H|WELCOME|" + runId + "|" + session);
                            connected = true;
                        }
                        else if (parts.Length >= 4 && parts[1] == "WELCOME" && parts[2] == runId)
                        {
                            session = parts[3];
                            connected = true;
                        }
                        else if (parts.Length >= 4 && parts[1] == "PING")
                        {
                            int seq;
                            int.TryParse(parts[2], out seq);
                            Send(socket, peer, "H|PONG|" + parts[2] + "|" + parts[3]);
                        }
                        else if (parts.Length >= 4 && parts[1] == "PONG")
                        {
                            pongs++;
                            long sentTicks;
                            if (long.TryParse(parts[3], out sentTicks))
                                rttMs = (float)((NowTicks() - sentTicks)
                                    / (double)TimeSpan.TicksPerMillisecond);
                        }
                    }
                }
                if (!connected && !host && Now() >= nextSend)
                {
                    Send(socket, peer, "H|HELLO|" + runId);
                    nextSend = Now() + 0.25;
                }
                if (connected && Now() >= nextSend)
                {
                    sequence++;
                    pings++;
                    Send(socket, peer, "H|PING|" + sequence + "|" + NowTicks().ToString());
                    nextSend = Now() + 0.25;
                }
            }
            bool pass = connected && pings >= 4 && pongs >= 2;
            if (!pass) lastReason = connected ? "insufficient-pings" : "no-peer";
            Report(pass ? "PASS" : "FAIL", "udp", role, port, 0);
            return pass ? 0 : 1;
        }
    }


    private static int RunM2(string role, int seconds, ulong lobbyId, ulong invite)
    {
        bool host = role == "host";
        SteamClient.Init(480, false);
        if (!SteamClient.IsValid) throw new InvalidOperationException("Steam init failed");
        var service = new SteamLobbyService();
        service.AutoAcceptInvites = !host;
        service.Install();
        var config = new SessionConfig
        {
            Build = "m2-harness",
            GameVersion = "m2",
            PlayerName = role,
            MaxPlayers = 4,
            PeerTimeoutSeconds = 60f,
        };
        var transport = new SteamP2PTransport();
        var session = new NetSession(config, transport);
        int received = 0;
        session.SnapshotReceived += delegate(Peer peer, PlayerSnapshot snapshot) { received++; };

        if (host)
        {
            service.Create(4, config.Build, config.GameVersion, Guid.NewGuid().ToString("N"));
            double wait = Now() + 30;
            while (!service.HasLobby && service.State != SteamLobbyState.Failed && Now() < wait)
            {
                service.Pump();
                SteamClient.RunCallbacks();
                Thread.Sleep(10);
            }
            if (!service.HasLobby)
            {
                Console.WriteLine("GMP_M2_RESULT FAIL transport=m2 role=host reason=lobby-create");
                return 1;
            }
            Console.WriteLine("GMP_M2_LOBBY " + service.Current.Value.Id.Value);
            Console.Out.Flush();
            if (invite != 0)
            {
                bool sent = service.Invite((SteamId)invite);
                Console.WriteLine("GMP_M2_INVITE sent=" + sent);
                Console.Out.Flush();
            }
            session.StartHost(0, Now());
        }
        else
        {
            ulong join = lobbyId;
            if (join == 0)
            {
                double wait = Now() + 30;
                while (join == 0 && Now() < wait)
                {
                    service.Pump();
                    SteamClient.RunCallbacks();
                    join = service.PendingInvite;
                    Thread.Sleep(10);
                }
            }
            if (join == 0)
            {
                Console.WriteLine("GMP_M2_RESULT FAIL transport=m2 role=guest reason=no-lobby");
                return 1;
            }
            service.Join(join, config.Build, config.GameVersion);
            double joinWait = Now() + 30;
            while (!service.HasLobby && service.State != SteamLobbyState.Failed && Now() < joinWait)
            {
                service.Pump();
                SteamClient.RunCallbacks();
                Thread.Sleep(10);
            }
            if (!service.HasLobby)
            {
                Console.WriteLine("GMP_M2_RESULT FAIL transport=m2 role=guest reason=lobby-join");
                return 1;
            }
            string hostBuild = service.Current.Value.GetData("build");
            if (!string.IsNullOrEmpty(hostBuild) && hostBuild != config.Build)
            {
                Console.WriteLine("GMP_M2_RESULT FAIL transport=m2 role=guest reason=build-mismatch");
                return 1;
            }
            transport.SetHost(service.Owner);
            session.StartGuest(0, Now());
        }

        double deadline = Now() + seconds;
        double nextSend = 0;
        int sequence = 0;
        while (Now() < deadline)
        {
            service.Pump();
            SteamClient.RunCallbacks();
            session.Pump(Now());
            if (session.State == SessionState.Playing && Now() >= nextSend)
            {
                sequence++;
                session.SendSnapshot(new PlayerSnapshot
                {
                    X = sequence,
                    Y = 0,
                    Z = 0,
                    Yaw = 0,
                    Health = 10,
                    Animation = 0,
                }, Now());
                nextSend = Now() + 0.05;
            }
            Thread.Sleep(10);
        }
        bool pass = session.State == SessionState.Playing
            && service.HasLobby
            && session.Peers.Count == 1
            && received >= 10
            && session.SnapshotsSent >= 10
            && !string.IsNullOrEmpty(session.SessionId);
        Console.WriteLine("GMP_M2_RESULT " + (pass ? "PASS" : "FAIL")
            + " transport=m2 role=" + role + " lobby=" + service.Current.Value.Id.Value
            + " session=" + (session.SessionId ?? "none")
            + " peers=" + session.Peers.Count + " sent=" + session.SnapshotsSent
            + " received=" + received + " members=" + service.MemberCount
            + " state=" + session.State);
        Console.Out.Flush();
        session.Stop("m2-complete");
        service.Leave();
        return pass ? 0 : 1;
    }

    private static void Send(Socket socket, EndPoint peer, string message)
    {
        if (peer == null) return;
        try { socket.SendTo(Encoding.ASCII.GetBytes(message), peer); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static int RunSteam(string role, int seconds, ulong lobbyId, ulong invite,
        bool overlay, bool allowSameAccount)
    {
        bool host = role == "host";
        SteamClient.Init(480, false);
        if (!SteamClient.IsValid) throw new InvalidOperationException("Steam init failed");
        SteamNetworking.AllowP2PPacketRelay(true);
        SteamNetworking.OnP2PSessionRequest += delegate(SteamId id)
        {
            SteamNetworking.AcceptP2PSessionWithUser(id);
        };
        SteamMatchmaking.OnLobbyMemberJoined += delegate(Lobby lobby, Friend friend)
        {
            if (!friend.IsMe) lobbyMemberJoined = true;
        };
        ulong pendingLobby = lobbyId;
        SteamFriends.OnGameLobbyJoinRequested += delegate(Lobby lobby, SteamId friend)
        {
            pendingLobby = lobby.Id.Value;
        };
        SteamMatchmaking.OnLobbyInvite += delegate(Friend friend, Lobby invited)
        {
            // Harness-only auto-accept. The shipping UI uses
            // OnGameLobbyJoinRequested when the player accepts the invite.
            pendingLobby = invited.Id.Value;
            Console.WriteLine("GMP_HARNESS_INVITE_RECEIVED " + invited.Id.Value);
            Console.Out.Flush();
        };

        Lobby room = default(Lobby);
        bool haveRoom = false;
        if (host)
        {
            Task<Lobby?> create = SteamMatchmaking.CreateLobbyAsync(4);
            Lobby? created = Wait(create, 20);
            if (!created.HasValue) throw new InvalidOperationException("CreateLobby failed");
            room = created.Value;
            haveRoom = true;
            room.SetFriendsOnly();
            room.SetJoinable(true);
            session = Guid.NewGuid().ToString("N");
            room.SetData("gmp", "1");
            room.SetData("build", "gmp-harness-1");
            room.SetData("session", session);
            if (room.Id.Value == 0) throw new InvalidOperationException("Lobby id is zero");
            Console.WriteLine("GMP_HARNESS_LOBBY " + room.Id.Value);
            Console.Out.Flush();
            // Facepunch lazily initialises the rich-presence dictionary with the
            // friends interface; touch it first to avoid a framework NRE.
            try
            {
                foreach (Friend ignored in SteamFriends.GetFriends()) { }
                SteamFriends.SetRichPresence("connect", "+connect_lobby " + room.Id.Value);
            }
            catch (Exception exception)
            {
                Console.WriteLine("GMP_HARNESS_RICHPRESENCE_WARN " + exception.GetType().Name);
                Console.Out.Flush();
            }
            if (invite != 0)
            {
                bool invited = room.InviteFriend((SteamId)invite);
                Console.WriteLine("GMP_HARNESS_INVITE sent=" + invited);
                Console.Out.Flush();
            }
            if (overlay)
            {
                try { SteamFriends.OpenGameInviteOverlay(room.Id); }
                catch (Exception exception)
                {
                    Console.WriteLine("GMP_HARNESS_OVERLAY_WARN " + exception.GetType().Name);
                    Console.Out.Flush();
                }
            }
        }
        else
        {
            double wait = Now() + 20;
            while (pendingLobby == 0 && Now() < wait)
            {
                SteamClient.RunCallbacks();
                Thread.Sleep(16);
            }
            if (pendingLobby == 0) throw new InvalidOperationException("No lobby id or invite");
            Task<Lobby?> join = SteamMatchmaking.JoinLobbyAsync((SteamId)pendingLobby);
            Lobby? joined = Wait(join, 20);
            if (!joined.HasValue) throw new InvalidOperationException("JoinLobby failed");
            room = joined.Value;
            haveRoom = true;
            session = room.GetData("session") ?? "";
            Console.WriteLine("GMP_HARNESS_JOINED " + room.Id.Value);
            Console.Out.Flush();
        }

        SteamId peerId = default(SteamId);
        bool connected = false;
        int sequence = 0;
        double deadline = Now() + seconds;
        double nextSend = 0;
        while (Now() < deadline)
        {
            SteamClient.RunCallbacks();
            if (host && !connected)
            {
                foreach (Friend member in room.Members)
                    if (!member.IsMe) peerId = member.Id;
            }
            else if (!host && !connected)
            {
                peerId = room.Owner.Id;
            }
            P2Packet? packet;
            while ((packet = SteamNetworking.ReadP2PPacket(0)).HasValue)
            {
                string message = Encoding.ASCII.GetString(packet.Value.Data);
                string[] parts = message.Split('|');
                SteamId from = packet.Value.SteamId;
                if (peerId.Value == 0) peerId = from;
                if (from.Value != 0) connected = true;
                if (parts.Length >= 3 && parts[1] == "HELLO" && parts[2] == runId)
                {
                    peerId = from;
                    if (host) SteamSend(from, "H|WELCOME|" + runId + "|" + session);
                    connected = true;
                }
                else if (parts.Length >= 4 && parts[1] == "WELCOME" && parts[2] == runId)
                {
                    session = parts[3];
                    connected = true;
                }
                else if (parts.Length >= 4 && parts[1] == "PING")
                {
                    SteamSend(from, "H|PONG|" + parts[2] + "|" + parts[3]);
                }
                else if (parts.Length >= 4 && parts[1] == "PONG")
                {
                    pongs++;
                    long sentTicks;
                    if (long.TryParse(parts[3], out sentTicks))
                        rttMs = (float)((NowTicks() - sentTicks)
                            / (double)TimeSpan.TicksPerMillisecond);
                }
            }
            if (haveRoom && !connected && !host && Now() >= nextSend && peerId.Value != 0)
            {
                SteamSend(peerId, "H|HELLO|" + runId);
                nextSend = Now() + 0.25;
            }
            if (connected && Now() >= nextSend)
            {
                sequence++;
                pings++;
                SteamSend(peerId, "H|PING|" + sequence + "|" + NowTicks().ToString());
                nextSend = Now() + 0.25;
            }
            Thread.Sleep(10);
        }
        int members = room.MemberCount;
        if (lobbyMemberJoined && members < 2) members = 2;
        memberCount = members;
        sameAccount = peerId.Value != 0 && peerId.Value == SteamClient.SteamId.Value;
        bool pass = connected && pings >= 4 && pongs >= 2
            && (members >= 2 || (allowSameAccount && sameAccount));
        if (!pass) lastReason = !haveRoom ? "no-lobby" : !connected ? "no-peer"
            : members < 2 && !allowSameAccount ? "members=" + members : "insufficient-pings";
        Report(pass ? "PASS" : "FAIL", "steam", role, 0, room.Id.Value);
        if (peerId.Value != 0)
        {
            try { SteamNetworking.CloseP2PSessionWithUser(peerId); }
            catch (Exception) { }
        }
        try { SteamClient.Shutdown(); } catch (Exception) { }
        return pass ? 0 : 1;
    }

    private static void SteamSend(SteamId peer, string message)
    {
        if (peer.Value == 0) return;
        SteamNetworking.SendP2PPacket(peer, Encoding.ASCII.GetBytes(message), -1, 0, P2PSend.Reliable);
    }

    private static T Wait<T>(Task<T> task, int seconds)
    {
        double deadline = Now() + seconds;
        while (!task.IsCompleted && Now() < deadline)
        {
            SteamClient.RunCallbacks();
            Thread.Sleep(10);
        }
        if (!task.IsCompleted) throw new TimeoutException("Steam call timed out");
        return task.Result;
    }

    private static void Report(string status, string transport, string role, int port, ulong lobby)
    {
        Console.WriteLine("GMP_HARNESS_RESULT " + status + " transport=" + transport
            + " role=" + role + " run=" + runId + " session="
            + (string.IsNullOrEmpty(session) ? "none" : session)
            + " port=" + port + " lobby=" + lobby + " pings=" + pings
            + " pongs=" + pongs + " rtt_ms=" + Fmt(rttMs)
            + " members=" + memberCount + " same_account=" + sameAccount
            + " reason=" + (lastReason.Length == 0 ? "none" : lastReason));
        Console.Out.Flush();
    }

    private static double Now()
    {
        return (double)NowTicks() / (double)TimeSpan.TicksPerSecond;
    }

    private static long NowTicks()
    {
        return DateTime.UtcNow.Ticks;
    }

    private static string Fmt(double value)
    {
        return value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }
}
