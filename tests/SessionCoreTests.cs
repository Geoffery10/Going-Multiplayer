using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GoingMultiplayer;

internal static class SessionCoreTests
{
    private static int checks;

    private static void Check(bool value, string name)
    {
        checks++;
        if (!value) throw new Exception("Session core check " + checks + " failed: " + name);
    }

    private static void Pump(NetSession a, NetSession b, ref double now)
    {
        a.Pump(now);
        b.Pump(now);
        now += 0.01;
        Thread.Sleep(1);
    }

    private static void WaitPlaying(NetSession host, NetSession guest, ref double now)
    {
        for (int i = 0; i < 4000 && (host.State != SessionState.Playing
            || guest.State != SessionState.Playing); i++)
            Pump(host, guest, ref now);
    }

    private static int FreePort()
    {
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.LocalEndPoint).Port;
        }
    }

    private static SessionConfig Config()
    {
        return new SessionConfig
        {
            Build = "session-test-build",
            GameVersion = "test-game",
            PlayerName = "tester",
            MaxPlayers = 4,
            PeerTimeoutSeconds = 2f,
        };
    }

    private static void Main()
    {
        CodecRoundTrip();
        HandshakeAndState();
        Console.WriteLine("SESSION_CORE PASS checks=" + checks);
    }

    private static void CodecRoundTrip()
    {
        var config = Config();
        byte[] hello = SessionCodec.Hello(config);
        string build, game, name;
        Check(SessionCodec.TryHello(hello, out build, out game, out name), "hello decode");
        Check(build == config.Build && game == config.GameVersion && name == config.PlayerName, "hello fields");

        var snapshot = new PlayerSnapshot { X = 1.5f, Y = 2.5f, Z = -3.5f, Yaw = 45f, Health = 7f, Animation = 2, Flags = 1 };
        byte[] encoded = SessionCodec.Snapshot(1, 42, 2, 9, snapshot);
        MessageType type;
        ushort protocol;
        uint epoch, sequence;
        int player;
        Check(SessionCodec.TryHeader(encoded, out type, out protocol, out epoch, out player, out sequence), "snapshot header");
        Check(type == MessageType.Snapshot && protocol == 1 && epoch == 42 && player == 2 && sequence == 9, "snapshot header fields");
        PlayerSnapshot decoded;
        Check(SessionCodec.TrySnapshot(encoded, out decoded), "snapshot decode");
        Check(decoded.X == snapshot.X && decoded.Z == snapshot.Z && decoded.Health == snapshot.Health
            && decoded.Animation == snapshot.Animation, "snapshot fields");

        var command = new InputCommand { MoveX = 1f, MoveY = -1f, Yaw = 90f, Buttons = 3 };
        byte[] commandBytes = SessionCodec.Command(1, 42, 2, 4, command);
        InputCommand decodedCommand;
        Check(SessionCodec.TryCommand(commandBytes, out decodedCommand), "command decode");
        Check(decodedCommand.MoveX == 1f && decodedCommand.MoveY == -1f
            && decodedCommand.Buttons == 3, "command fields");

        byte[] truncated = new byte[SessionCodec.HeaderSize - 1];
        Check(!SessionCodec.TryHeader(truncated, out type, out protocol, out epoch, out player, out sequence), "truncated header");
    }

    private static void HandshakeAndState()
    {
        int port = FreePort();
        var hostConfig = Config();
        var guestConfig = Config();
        var host = new NetSession(hostConfig, new LoopbackTransport(), delegate { return 77u; });
        var guest = new NetSession(guestConfig, new LoopbackTransport(), delegate { return 99u; });
        var receivedByHost = new List<PlayerSnapshot>();
        var receivedByGuest = new List<PlayerSnapshot>();
        host.SnapshotReceived += delegate(Peer peer, PlayerSnapshot snapshot) { receivedByHost.Add(snapshot); };
        guest.SnapshotReceived += delegate(Peer peer, PlayerSnapshot snapshot) { receivedByGuest.Add(snapshot); };

        double now = 0;
        host.StartHost(port, now);
        guest.StartGuest(port, now);
        WaitPlaying(host, guest, ref now);

        Check(host.State == SessionState.Playing, "host playing");
        Check(guest.State == SessionState.Playing, "guest playing");
        Check(host.LocalPlayerId == 1 && guest.LocalPlayerId == 2, "player ids");
        Check(host.SessionId == guest.SessionId && !string.IsNullOrEmpty(host.SessionId), "session id");
        Check(host.Epoch == 77u && guest.Epoch == 77u, "host epoch adopted");
        Check(host.Peers.Count == 1 && guest.Peers.Count == 1, "one remote peer each");

        var hostPose = new PlayerSnapshot { X = 10f, Y = 0f, Z = 4f, Yaw = 30f, Health = 9f, Animation = 1 };
        var guestPose = new PlayerSnapshot { X = -2f, Y = 0f, Z = 8f, Yaw = 180f, Health = 8f, Animation = 2 };
        for (int i = 0; i < 20; i++)
        {
            host.SendSnapshot(hostPose, now);
            guest.SendSnapshot(guestPose, now);
            Pump(host, guest, ref now);
        }
        Check(receivedByGuest.Count >= 10 && receivedByHost.Count >= 10, "bidirectional snapshots");
        Check(Math.Abs(receivedByGuest[receivedByGuest.Count - 1].X - 10f) < 0.001f, "host snapshot value");
        Check(Math.Abs(receivedByHost[receivedByHost.Count - 1].X + 2f) < 0.001f, "guest snapshot value");

        // Duplicate and wrong-player packets must not be applied.
        int before = receivedByHost.Count;
        Peer hostPeer = null;
        foreach (Peer peer in host.Peers.All) hostPeer = peer;
        Check(hostPeer != null, "host peer key");
        byte[] duplicate = SessionCodec.Snapshot((ushort)1, host.Epoch, 1, 1,
            new PlayerSnapshot { X = 999f });
        host.Transport.Send(hostPeer.Key, duplicate, false);
        byte[] wrongPlayer = SessionCodec.Snapshot((ushort)1, host.Epoch, 3, 500,
            new PlayerSnapshot { X = 888f });
        host.Transport.Send(hostPeer.Key, wrongPlayer, false);
        Pump(host, guest, ref now);
        Check(receivedByHost.Count == before, "duplicate and wrong-player rejected");
        Check(guest.Stale >= 1 && guest.Rejected >= 1, "stale and rejected counters");

        // Expire the guest and verify the host removes it.
        now += 3.0;
        host.Pump(now);
        Check(host.Peers.Count == 0, "guest expired");

        host.Stop("done");
        Check(host.State == SessionState.Closed, "host closed");
        host.Dispose();
        guest.Dispose();
    }
}
