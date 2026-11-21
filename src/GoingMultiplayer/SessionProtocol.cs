using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GoingMultiplayer
{
    internal enum SessionState { Idle, Hosting, Connecting, Playing, Ending, Closed }

    internal enum MessageType : byte
    {
        Hello = 1,
        Welcome = 2,
        Snapshot = 3,
        Command = 4,
        Ping = 5,
        Pong = 6,
        Bye = 7,
        Done = 8,
        Ready = 9,
        Kick = 10,
        Save = 11,
        Scene = 12,
        SceneIntent = 13,
        Teleport = 14,
        Revive = 15,
    }

    internal struct PlayerSnapshot
    {
        internal float X;
        internal float Y;
        internal float Z;
        internal float Yaw;
        internal float Health;
        internal byte Animation;
        internal byte Flags;
    }

    [Flags]
    internal enum PlayerButtons
    {
        None = 0,
        Attack = 1,
        AltAttack = 2,
        Dodge = 4,
        Interact = 8,
        Throw = 16,
        Focus = 32,
        FocusSwapLeft = 64,
        FocusSwapRight = 128,
        PreviousItem = 256,
        NextItem = 512,
    }

    internal struct InputCommand
    {
        internal float MoveX;
        internal float MoveY;
        internal float Yaw;
        internal float CameraX;
        internal float CameraY;
        internal PlayerButtons Held;
        internal PlayerButtons Pressed;
        internal PlayerButtons Released;
        internal int Inventory;
    }

    internal sealed class SessionConfig
    {
        internal ushort ProtocolVersion = 1;
        internal string Build = "unknown";
        internal string GameVersion = "unknown";
        internal string PlayerName = "player";
        internal int MaxPlayers = 4;
        internal float PeerTimeoutSeconds = 10f;
        internal float PingIntervalSeconds = 1f;
        internal float HelloIntervalSeconds = 0.25f;
    }

    internal struct TransportPacket
    {
        internal string Peer;
        internal byte[] Data;
    }

    internal interface ITransport : IDisposable
    {
        string LocalPeer { get; }
        void StartHost(int port);
        void StartGuest(int port);
        bool TryReceive(out TransportPacket packet);
        bool Send(string peer, byte[] data, bool reliable);
        void Close();
    }

    // Loopback is the deterministic test transport. It is not a production
    // transport and binds only to 127.0.0.1.
    internal sealed class LoopbackTransport : ITransport
    {
        internal static int Sent;
        internal static int SendFailed;
        internal static int Received;
        private Socket socket;
        private IPEndPoint hostEndpoint;
        private bool isHost;
        private readonly byte[] buffer = new byte[2048];

        internal bool IsHost { get { return isHost; } }

        public string LocalPeer
        {
            get
            {
                if (socket == null || socket.LocalEndPoint == null) return "none";
                return socket.LocalEndPoint.ToString();
            }
        }

        public void StartHost(int port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Blocking = false;
            isHost = true;
            hostEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        }

        public void StartGuest(int port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Blocking = false;
            isHost = false;
            hostEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        }

        public bool TryReceive(out TransportPacket packet)
        {
            packet = default(TransportPacket);
            if (socket == null) return false;
            try
            {
                if (!socket.Poll(0, SelectMode.SelectRead)) return false;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int count = socket.ReceiveFrom(buffer, ref from);
                if (count <= 0) return false;
                byte[] data = new byte[count];
                Buffer.BlockCopy(buffer, 0, data, 0, count);
                packet.Peer = from.ToString();
                packet.Data = data;
                Received++;
                return true;
            }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        public bool Send(string peer, byte[] data, bool reliable)
        {
            if (socket == null || data == null) return false;
            IPEndPoint target = Resolve(peer);
            if (target == null) return false;
            try
            {
                socket.SendTo(data, target);
                Sent++;
                return true;
            }
            catch (SocketException) { SendFailed++; return false; }
            catch (ObjectDisposedException) { SendFailed++; return false; }
        }

        private IPEndPoint Resolve(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return null;
            if (peer == "host") return hostEndpoint;
            int split = peer.LastIndexOf(':');
            if (split <= 0) return null;
            IPAddress address;
            int port;
            if (!IPAddress.TryParse(peer.Substring(0, split), out address)) return null;
            if (!int.TryParse(peer.Substring(split + 1), out port)) return null;
            if (port < 1 || port > 65535) return null;
            return new IPEndPoint(address, port);
        }

        public void Close()
        {
            if (socket == null) return;
            try { socket.Close(); } catch { }
            socket = null;
            hostEndpoint = null;
        }

        public void Dispose() { Close(); }
    }

    internal static class SessionCodec
    {
        internal const int HeaderSize = 15;
        private const int MaxStringBytes = 256;

        internal static byte[] Hello(SessionConfig config)
        {
            return Build(MessageType.Hello, config.ProtocolVersion, 0, 0, 0, delegate(BinaryWriter writer)
            {
                WriteString(writer, config.Build);
                WriteString(writer, config.GameVersion);
                WriteString(writer, config.PlayerName);
            });
        }

        internal static byte[] Welcome(ushort protocol, uint epoch, int playerId,
            string session, int hostId, int maxPlayers, SessionConfig config)
        {
            return Build(MessageType.Welcome, protocol, epoch, playerId, 0, delegate(BinaryWriter writer)
            {
                WriteString(writer, session);
                writer.Write(hostId);
                writer.Write(maxPlayers);
                WriteString(writer, config.Build);
                WriteString(writer, config.GameVersion);
            });
        }

        internal static byte[] Snapshot(ushort protocol, uint epoch, int playerId,
            uint sequence, PlayerSnapshot snapshot)
        {
            return Build(MessageType.Snapshot, protocol, epoch, playerId, sequence,
                delegate(BinaryWriter writer)
                {
                    writer.Write(snapshot.X);
                    writer.Write(snapshot.Y);
                    writer.Write(snapshot.Z);
                    writer.Write(snapshot.Yaw);
                    writer.Write(snapshot.Health);
                    writer.Write(snapshot.Animation);
                    writer.Write(snapshot.Flags);
                });
        }

        internal static byte[] Command(ushort protocol, uint epoch, int playerId,
            uint sequence, InputCommand command)
        {
            return Build(MessageType.Command, protocol, epoch, playerId, sequence,
                delegate(BinaryWriter writer)
                {
                    writer.Write(command.MoveX);
                    writer.Write(command.MoveY);
                    writer.Write(command.Yaw);
                    writer.Write(command.CameraX);
                    writer.Write(command.CameraY);
                    writer.Write((int)command.Held);
                    writer.Write((int)command.Pressed);
                    writer.Write((int)command.Released);
                    writer.Write(command.Inventory);
                });
        }

        internal static byte[] SaveChunk(ushort protocol, uint epoch, int playerId,
            uint sequence, uint transfer, int index, int total, byte[] chunk)
        {
            return Build(MessageType.Save, protocol, epoch, playerId, sequence,
                delegate(BinaryWriter writer)
                {
                    writer.Write(transfer);
                    writer.Write((ushort)index);
                    writer.Write((ushort)total);
                    writer.Write((ushort)chunk.Length);
                    writer.Write(chunk);
                });
        }

        internal static bool TrySaveChunk(byte[] data, out uint transfer, out int index,
            out int total, out byte[] chunk)
        {
            transfer = 0;
            index = 0;
            total = 0;
            chunk = null;
            try
            {
                using (var reader = OpenBody(data, MessageType.Save))
                {
                    if (reader == null) return false;
                    transfer = reader.ReadUInt32();
                    index = reader.ReadUInt16();
                    total = reader.ReadUInt16();
                    int length = reader.ReadUInt16();
                    if (total <= 0 || total > 1024 || index < 0 || index >= total || length > 1024)
                        return false;
                    chunk = reader.ReadBytes(length);
                    return chunk.Length == length;
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static byte[] Scene(ushort protocol, uint epoch, int playerId,
            uint sequence, string scene, int seed)
        {
            return Build(MessageType.Scene, protocol, epoch, playerId, sequence,
                delegate(BinaryWriter writer)
                {
                    WriteString(writer, scene);
                    writer.Write(seed);
                });
        }

        internal static byte[] Revive(ushort protocol, uint epoch, int playerId,
            uint sequence, int targetId, float amount)
        {
            return Build(MessageType.Revive, protocol, epoch, playerId, sequence,
                delegate(BinaryWriter writer)
                {
                    writer.Write(targetId);
                    writer.Write(amount);
                });
        }

        internal static bool TryRevive(byte[] data, out int targetId, out float amount)
        {
            targetId = 0;
            amount = 0f;
            try
            {
                using (var reader = OpenBody(data, MessageType.Revive))
                {
                    if (reader == null) return false;
                    targetId = reader.ReadInt32();
                    amount = reader.ReadSingle();
                    return targetId > 0 && amount > 0f && amount < 1000f
                        && !float.IsNaN(amount) && !float.IsInfinity(amount);
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static byte[] Teleport(ushort protocol, uint epoch, int playerId,
            uint sequence, float x, float y, float z)
        {
            return Build(MessageType.Teleport, protocol, epoch, playerId, sequence,
                delegate(BinaryWriter writer)
                {
                    writer.Write(x);
                    writer.Write(y);
                    writer.Write(z);
                });
        }

        internal static bool TryTeleport(byte[] data, out float x, out float y, out float z)
        {
            x = 0f;
            y = 0f;
            z = 0f;
            try
            {
                using (var reader = OpenBody(data, MessageType.Teleport))
                {
                    if (reader == null) return false;
                    x = reader.ReadSingle();
                    y = reader.ReadSingle();
                    z = reader.ReadSingle();
                    return !float.IsNaN(x) && !float.IsInfinity(x)
                        && !float.IsNaN(y) && !float.IsInfinity(y)
                        && !float.IsNaN(z) && !float.IsInfinity(z);
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static byte[] SceneIntent(ushort protocol, uint epoch, int playerId,
            uint sequence, string scene)
        {
            return Build(MessageType.SceneIntent, protocol, epoch, playerId, sequence,
                delegate(BinaryWriter writer) { WriteString(writer, scene); });
        }

        internal static bool TrySceneIntent(byte[] data, out string scene)
        {
            scene = null;
            try
            {
                using (var reader = OpenBody(data, MessageType.SceneIntent))
                {
                    if (reader == null) return false;
                    scene = ReadString(reader);
                    return !string.IsNullOrEmpty(scene);
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static bool TryScene(byte[] data, out string scene, out int seed)
        {
            scene = null;
            seed = 0;
            try
            {
                using (var reader = OpenBody(data, MessageType.Scene))
                {
                    if (reader == null) return false;
                    scene = ReadString(reader);
                    seed = reader.ReadInt32();
                    return !string.IsNullOrEmpty(scene);
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static byte[] Control(MessageType type, ushort protocol, uint epoch,
            int playerId, uint sequence)
        {
            return Build(type, protocol, epoch, playerId, sequence, null);
        }

        internal static bool TryHeader(byte[] data, out MessageType type, out ushort protocol,
            out uint epoch, out int playerId, out uint sequence)
        {
            type = MessageType.Hello;
            protocol = 0;
            epoch = 0;
            playerId = 0;
            sequence = 0;
            if (data == null || data.Length < HeaderSize) return false;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(data, false)))
                {
                    type = (MessageType)reader.ReadByte();
                    protocol = reader.ReadUInt16();
                    epoch = reader.ReadUInt32();
                    playerId = reader.ReadInt32();
                    sequence = reader.ReadUInt32();
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static bool TryHello(byte[] data, out string build, out string gameVersion,
            out string playerName)
        {
            build = null;
            gameVersion = null;
            playerName = null;
            try
            {
                using (var reader = OpenBody(data, MessageType.Hello))
                {
                    if (reader == null) return false;
                    build = ReadString(reader);
                    gameVersion = ReadString(reader);
                    playerName = ReadString(reader);
                    return build != null && gameVersion != null && playerName != null;
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static bool TryWelcome(byte[] data, out string session, out int hostId,
            out int maxPlayers, out string build, out string gameVersion)
        {
            session = null;
            hostId = 0;
            maxPlayers = 0;
            build = null;
            gameVersion = null;
            try
            {
                using (var reader = OpenBody(data, MessageType.Welcome))
                {
                    if (reader == null) return false;
                    session = ReadString(reader);
                    hostId = reader.ReadInt32();
                    maxPlayers = reader.ReadInt32();
                    build = ReadString(reader);
                    gameVersion = ReadString(reader);
                    return session != null && build != null && gameVersion != null;
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static bool TrySnapshot(byte[] data, out PlayerSnapshot snapshot)
        {
            snapshot = default(PlayerSnapshot);
            try
            {
                using (var reader = OpenBody(data, MessageType.Snapshot))
                {
                    if (reader == null) return false;
                    snapshot.X = reader.ReadSingle();
                    snapshot.Y = reader.ReadSingle();
                    snapshot.Z = reader.ReadSingle();
                    snapshot.Yaw = reader.ReadSingle();
                    snapshot.Health = reader.ReadSingle();
                    snapshot.Animation = reader.ReadByte();
                    snapshot.Flags = reader.ReadByte();
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static bool TryCommand(byte[] data, out InputCommand command)
        {
            command = default(InputCommand);
            try
            {
                using (var reader = OpenBody(data, MessageType.Command))
                {
                    if (reader == null) return false;
                    command.MoveX = reader.ReadSingle();
                    command.MoveY = reader.ReadSingle();
                    command.Yaw = reader.ReadSingle();
                    command.CameraX = reader.ReadSingle();
                    command.CameraY = reader.ReadSingle();
                    command.Held = (PlayerButtons)reader.ReadInt32();
                    command.Pressed = (PlayerButtons)reader.ReadInt32();
                    command.Released = (PlayerButtons)reader.ReadInt32();
                    command.Inventory = reader.ReadInt32();
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static byte[] Build(MessageType type, ushort protocol, uint epoch,
            int playerId, uint sequence, Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)type);
                writer.Write(protocol);
                writer.Write(epoch);
                writer.Write(playerId);
                writer.Write(sequence);
                if (body != null) body(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static BinaryReader OpenBody(byte[] data, MessageType expected)
        {
            MessageType type;
            ushort protocol;
            uint epoch;
            int playerId;
            uint sequence;
            if (!TryHeader(data, out type, out protocol, out epoch, out playerId, out sequence))
                return null;
            if (type != expected) return null;
            var stream = new MemoryStream(data, HeaderSize, data.Length - HeaderSize, false);
            return new BinaryReader(stream);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > MaxStringBytes) throw new ArgumentException("Session string too long");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            ushort length = reader.ReadUInt16();
            if (length > MaxStringBytes) throw new ArgumentException("Session string too long");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new IOException("Truncated session string");
            return Encoding.UTF8.GetString(bytes);
        }
    }

    internal sealed class Peer
    {
        internal int PlayerId;
        internal string Key;
        internal string Name;
        internal bool IsHost;
        internal bool Connected;
        internal double LastSeen;
        internal uint LastSequence;
        internal float RttMs;
        internal double PingSent;
        internal bool Done;
        internal bool Ready;
    }

    internal sealed class PeerRegistry
    {
        private readonly Dictionary<string, Peer> byKey = new Dictionary<string, Peer>();
        private readonly Dictionary<int, Peer> byId = new Dictionary<int, Peer>();

        internal int Count { get { return byKey.Count; } }

        internal IEnumerable<Peer> All { get { return byKey.Values; } }

        internal Peer Add(int playerId, string key, string name, bool isHost, double now)
        {
            Peer peer;
            if (byKey.TryGetValue(key, out peer))
            {
                peer.LastSeen = now;
                return peer;
            }
            peer = new Peer
            {
                PlayerId = playerId,
                Key = key,
                Name = name,
                IsHost = isHost,
                Connected = true,
                LastSeen = now,
            };
            byKey[key] = peer;
            byId[playerId] = peer;
            return peer;
        }

        internal bool TryByKey(string key, out Peer peer)
        {
            peer = null;
            return !string.IsNullOrEmpty(key) && byKey.TryGetValue(key, out peer);
        }

        internal bool TryById(int playerId, out Peer peer)
        {
            peer = null;
            return byId.TryGetValue(playerId, out peer);
        }

        internal int AllocateId(int maxPlayers)
        {
            for (int id = 2; id <= maxPlayers; id++)
                if (!byId.ContainsKey(id)) return id;
            return 0;
        }

        internal void Remove(Peer peer)
        {
            if (peer == null) return;
            byKey.Remove(peer.Key);
            Peer existing;
            if (byId.TryGetValue(peer.PlayerId, out existing) && existing == peer)
                byId.Remove(peer.PlayerId);
        }

        internal List<Peer> Expire(double now, float timeout)
        {
            var expired = new List<Peer>();
            foreach (Peer peer in byKey.Values)
                if (now - peer.LastSeen > timeout) expired.Add(peer);
            foreach (Peer peer in expired) Remove(peer);
            return expired;
        }
    }

    internal sealed class NetSession : IDisposable
    {
        private readonly SessionConfig config;
        private readonly ITransport transport;
        private readonly Func<uint> epochSource;
        private double lastHello;
        private double lastPing;
        private bool disposed;
        // One sender sequence for every message type. The receiver keeps a
        // single per-peer sequence, so snapshots and commands must share it.
        private uint sequence;
        private uint saveTransferId;
        private uint saveTransfer;
        private int saveTotal;
        private int saveCount;
        private byte[][] saveChunks;
        private const int MaxSaveBytes = 512 * 1024;

        internal NetSession(SessionConfig config, ITransport transport, Func<uint> epochSource = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (transport == null) throw new ArgumentNullException("transport");
            this.config = config;
            this.transport = transport;
            this.epochSource = epochSource ?? NewEpoch;
            Peers = new PeerRegistry();
            State = SessionState.Idle;
        }

        internal SessionConfig Config { get { return config; } }
        internal ITransport Transport { get { return transport; } }
        internal SessionState State { get; private set; }
        internal bool IsHost { get; private set; }
        internal int LocalPlayerId { get; private set; }
        internal int HostPlayerId { get; private set; }
        internal string SessionId { get; private set; }
        internal uint Epoch { get; private set; }
        internal string LastError { get; private set; }
        internal PeerRegistry Peers { get; private set; }
        internal uint LastSnapshotSequence { get; private set; }
        internal uint LastCommandSequence { get; private set; }
        internal int SnapshotsSent { get; private set; }
        internal int SnapshotsReceived { get; private set; }
        internal int CommandsSent { get; private set; }
        internal int CommandsReceived { get; private set; }
        internal int SavesReceived { get; private set; }
        internal bool SaveInProgress { get { return saveChunks != null; } }
        internal int Rejected { get; private set; }
        internal int Stale { get; private set; }
        internal bool AllPeersReady
        {
            get
            {
                int count = 0;
                foreach (Peer peer in Peers.All)
                {
                    count++;
                    if (!peer.Ready) return false;
                }
                return count > 0;
            }
        }

        internal bool AllPeersDone
        {
            get
            {
                int count = 0;
                foreach (Peer peer in Peers.All)
                {
                    count++;
                    if (!peer.Done) return false;
                }
                return count > 0;
            }
        }

        internal Action<Peer> PeerJoined = delegate { };
        internal Action<Peer> PeerLeft = delegate { };
        internal Action<Peer, PlayerSnapshot> SnapshotReceived = delegate { };
        // Host-published state for this client's own actor (server correction).
        internal Action<PlayerSnapshot> OwnSnapshotReceived = delegate { };
        internal Action<Peer, InputCommand> CommandReceived = delegate { };
        internal Action<byte[]> SaveReceived = delegate { };
        internal Action<string, int> SceneReceived = delegate { };
        internal Action<string> SceneIntentReceived = delegate { };
        internal Action<float, float, float> TeleportReceived = delegate { };
        internal Action<int, float> ReviveReceived = delegate { };
        internal Action<SessionState> StateChanged = delegate { };
        internal Action<Peer> Kicked = delegate { };

        internal void StartHost(int port, double now)
        {
            if (State != SessionState.Idle) throw new InvalidOperationException("Session already started");
            transport.StartHost(port);
            IsHost = true;
            HostPlayerId = 1;
            LocalPlayerId = 1;
            SessionId = Guid.NewGuid().ToString("N");
            Epoch = epochSource();
            SetState(SessionState.Hosting);
            lastPing = now;
        }

        internal void StartGuest(int port, double now)
        {
            if (State != SessionState.Idle) throw new InvalidOperationException("Session already started");
            transport.StartGuest(port);
            IsHost = false;
            HostPlayerId = 1;
            LocalPlayerId = 0;
            State = SessionState.Connecting;
            lastHello = now;
            SendHello(now);
        }

        internal void Pump(double now)
        {
            if (State == SessionState.Closed) return;
            TransportPacket packet;
            while (transport.TryReceive(out packet)) Handle(packet, now);

            if (IsHost && State == SessionState.Hosting && ConnectedCount() > 0)
                SetState(SessionState.Playing);

            if (!IsHost && State == SessionState.Connecting && now - lastHello >= config.HelloIntervalSeconds)
                SendHello(now);

            if (State == SessionState.Hosting || State == SessionState.Playing)
            {
                if (now - lastPing >= config.PingIntervalSeconds)
                {
                    lastPing = now;
                    foreach (Peer peer in Peers.All)
                        SendControl(MessageType.Ping, peer, now);
                }
                List<Peer> expired = Peers.Expire(now, config.PeerTimeoutSeconds);
                foreach (Peer peer in expired)
                {
                    Rejected++;
                    if (PeerLeft != null) PeerLeft(peer);
                    if (!IsHost && peer.IsHost)
                    {
                        LastError = "host timeout";
                        SetState(SessionState.Ending);
                    }
                }
            }
        }

        internal void SendSnapshot(PlayerSnapshot snapshot, double now)
        {
            SendSnapshotFor(LocalPlayerId, snapshot, now);
        }

        // The host also publishes snapshots for actors it simulates on behalf of
        // a guest, so the guest can render its own puppet from the authority.
        internal void SendSnapshotFor(int playerId, PlayerSnapshot snapshot, double now)
        {
            if (State != SessionState.Playing && State != SessionState.Hosting) return;
            sequence++;
            LastSnapshotSequence = sequence;
            byte[] data = SessionCodec.Snapshot(config.ProtocolVersion, Epoch, playerId,
                sequence, snapshot);
            foreach (Peer peer in Peers.All)
                if (peer.Connected) transport.Send(peer.Key, data, false);
            SnapshotsSent++;
        }

        // Save transfer: the host pushes its active save so the guest plays the
        // same progression. Small chunks keep every transport happy (the
        // loopback transport buffers 2 KB).
        internal void SendSave(byte[] data, double now)
        {
            if (data == null || data.Length == 0 || data.Length > MaxSaveBytes) return;
            if (State != SessionState.Playing && State != SessionState.Hosting) return;
            const int ChunkSize = 700;
            int total = (data.Length + ChunkSize - 1) / ChunkSize;
            if (total > 1024) return;
            saveTransferId++;
            for (int i = 0; i < total; i++)
            {
                int offset = i * ChunkSize;
                int length = Math.Min(ChunkSize, data.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(data, offset, chunk, 0, length);
                sequence++;
                byte[] packet = SessionCodec.SaveChunk(config.ProtocolVersion, Epoch,
                    LocalPlayerId, sequence, saveTransferId, i, total, chunk);
                foreach (Peer peer in Peers.All)
                    if (peer.Connected) transport.Send(peer.Key, packet, true);
            }
        }

        // A guest that opened a door itself asks the host to take the party
        // through the same transition instead of being pulled back.
        // Scripted moves (special doors, elevators) must take the whole party.
        internal void SendRevive(int targetId, float amount, double now)
        {
            if (State != SessionState.Playing) return;
            sequence++;
            byte[] data = SessionCodec.Revive(config.ProtocolVersion, Epoch,
                LocalPlayerId, sequence, targetId, amount);
            foreach (Peer peer in Peers.All)
                if (peer.Connected) transport.Send(peer.Key, data, true);
        }

        internal void SendTeleport(float x, float y, float z, double now)
        {
            if (State != SessionState.Playing) return;
            sequence++;
            byte[] data = SessionCodec.Teleport(config.ProtocolVersion, Epoch,
                LocalPlayerId, sequence, x, y, z);
            foreach (Peer peer in Peers.All)
                if (peer.Connected) transport.Send(peer.Key, data, true);
        }

        internal void SendSceneIntent(string scene, double now)
        {
            if (string.IsNullOrEmpty(scene)) return;
            if (State != SessionState.Playing) return;
            sequence++;
            byte[] data = SessionCodec.SceneIntent(config.ProtocolVersion, Epoch,
                LocalPlayerId, sequence, scene);
            foreach (Peer peer in Peers.All)
                if (peer.Connected) transport.Send(peer.Key, data, true);
        }

        internal void SendScene(string scene, int seed, double now)
        {
            if (string.IsNullOrEmpty(scene)) return;
            if (State != SessionState.Playing && State != SessionState.Hosting) return;
            sequence++;
            byte[] data = SessionCodec.Scene(config.ProtocolVersion, Epoch, LocalPlayerId,
                sequence, scene, seed);
            foreach (Peer peer in Peers.All)
                if (peer.Connected) transport.Send(peer.Key, data, true);
        }

        internal void SendCommand(InputCommand command, double now)
        {
            if (State != SessionState.Playing && State != SessionState.Hosting) return;
            sequence++;
            LastCommandSequence = sequence;
            byte[] data = SessionCodec.Command(config.ProtocolVersion, Epoch, LocalPlayerId,
                sequence, command);
            foreach (Peer peer in Peers.All)
                if (peer.Connected) transport.Send(peer.Key, data, true);
            CommandsSent++;
        }

        internal void SendReady(double now)
        {
            foreach (Peer peer in Peers.All)
                if (peer.Connected) SendControl(MessageType.Ready, peer, now);
        }

        internal void KickPeer(Peer peer)
        {
            if (peer == null) return;
            SendControl(MessageType.Kick, peer, 0);
            Peers.Remove(peer);
            PeerLeft(peer);
        }

        internal void SendDone(double now)
        {
            foreach (Peer peer in Peers.All)
                if (peer.Connected) SendControl(MessageType.Done, peer, now);
        }

        internal void Stop(string reason)
        {            if (State == SessionState.Closed) return;
            LastError = reason;
            foreach (Peer peer in Peers.All)
                SendControl(MessageType.Bye, peer, 0);
            SetState(SessionState.Closed);
            transport.Close();
        }

        private void Handle(TransportPacket packet, double now)
        {
            MessageType type;
            ushort protocol;
            uint epoch;
            int playerId;
            uint sequence;
            if (!SessionCodec.TryHeader(packet.Data, out type, out protocol, out epoch,
                out playerId, out sequence))
            {
                Rejected++;
                return;
            }
            if (protocol != config.ProtocolVersion)
            {
                Rejected++;
                LastError = "protocol mismatch";
                return;
            }

            if (type == MessageType.Hello)
            {
                HandleHello(packet, now);
                return;
            }
            if (type == MessageType.Welcome)
            {
                HandleWelcome(packet, now);
                return;
            }
            if (type == MessageType.Kick)
            {
                Peer kicked;
                if (Peers.TryByKey(packet.Peer, out kicked))
                {
                    LastError = "kicked";
                    Kicked(kicked);
                    SetState(SessionState.Ending);
                }
                return;
            }
            if (type == MessageType.Bye)
            {
                Peer bye;
                if (Peers.TryByKey(packet.Peer, out bye))
                {
                    Peers.Remove(bye);
                    if (PeerLeft != null) PeerLeft(bye);
                    if (!IsHost && bye.IsHost)
                    {
                        LastError = "host left";
                        SetState(SessionState.Ending);
                    }
                }
                return;
            }

            Peer peer;
            if (!Peers.TryByKey(packet.Peer, out peer))
            {
                Rejected++;
                return;
            }
            peer.LastSeen = now;
            if (type == MessageType.Ready)
            {
                if (epoch != Epoch)
                {
                    Rejected++;
                    return;
                }
                peer.Ready = true;
                return;
            }
            if (type == MessageType.Done)
            {
                peer.Done = true;
                return;
            }
            if (type == MessageType.Ping)
            {
                SendControl(MessageType.Pong, peer, now);
                return;
            }
            if (type == MessageType.Pong)
            {
                peer.RttMs = (float)Math.Max(0, (now - peer.PingSent) * 1000.0);
                return;
            }
            if (State != SessionState.Playing)
            {
                Rejected++;
                return;
            }
            // A peer may only speak for itself, except that the host is the
            // authority for the guest's own actor (server correction).
            bool identityOk = playerId == peer.PlayerId
                || (!IsHost && peer.IsHost && playerId == LocalPlayerId);
            if (epoch != Epoch || !identityOk)
            {
                Rejected++;
                return;
            }
            if (sequence == 0 || sequence <= peer.LastSequence)
            {
                Stale++;
                return;
            }
            peer.LastSequence = sequence;

            if (type == MessageType.Snapshot)
            {
                PlayerSnapshot snapshot;
                if (!SessionCodec.TrySnapshot(packet.Data, out snapshot))
                {
                    Rejected++;
                    return;
                }
                SnapshotsReceived++;
                if (playerId == LocalPlayerId)
                {
                    if (OwnSnapshotReceived != null) OwnSnapshotReceived(snapshot);
                }
                else if (SnapshotReceived != null) SnapshotReceived(peer, snapshot);
            }
            else if (type == MessageType.Save)
            {
                AcceptSaveChunk(packet.Data);
            }
            else if (type == MessageType.Scene)
            {
                string scene;
                int seed;
                if (!SessionCodec.TryScene(packet.Data, out scene, out seed))
                {
                    Rejected++;
                    return;
                }
                if (SceneReceived != null) SceneReceived(scene, seed);
            }
            else if (type == MessageType.SceneIntent)
            {
                string scene;
                if (!SessionCodec.TrySceneIntent(packet.Data, out scene))
                {
                    Rejected++;
                    return;
                }
                if (SceneIntentReceived != null) SceneIntentReceived(scene);
            }
            else if (type == MessageType.Teleport)
            {
                float tx, ty, tz;
                if (!SessionCodec.TryTeleport(packet.Data, out tx, out ty, out tz))
                {
                    Rejected++;
                    return;
                }
                if (TeleportReceived != null)
                    TeleportReceived(tx, ty, tz);
            }
            else if (type == MessageType.Revive)
            {
                int targetId;
                float amount;
                if (!SessionCodec.TryRevive(packet.Data, out targetId, out amount))
                {
                    Rejected++;
                    return;
                }
                if (ReviveReceived != null) ReviveReceived(targetId, amount);
            }
            else if (type == MessageType.Command)
            {
                InputCommand command;
                if (!SessionCodec.TryCommand(packet.Data, out command))
                {
                    Rejected++;
                    return;
                }
                CommandsReceived++;
                if (CommandReceived != null) CommandReceived(peer, command);
            }
            else
            {
                Rejected++;
            }
        }

        private void AcceptSaveChunk(byte[] data)
        {
            uint transfer;
            int index, total;
            byte[] chunk;
            if (!SessionCodec.TrySaveChunk(data, out transfer, out index, out total, out chunk))
            {
                Rejected++;
                return;
            }
            if (saveChunks == null || transfer != saveTransfer || total != saveTotal)
            {
                saveTransfer = transfer;
                saveTotal = total;
                saveChunks = new byte[total][];
                saveCount = 0;
            }
            if (saveChunks[index] == null)
            {
                saveChunks[index] = chunk;
                saveCount++;
            }
            if (saveCount < saveTotal) return;
            int length = 0;
            foreach (byte[] part in saveChunks) length += part.Length;
            if (length <= 0 || length > MaxSaveBytes)
            {
                saveChunks = null;
                Rejected++;
                return;
            }
            var assembled = new byte[length];
            int offset = 0;
            foreach (byte[] part in saveChunks)
            {
                Buffer.BlockCopy(part, 0, assembled, offset, part.Length);
                offset += part.Length;
            }
            saveChunks = null;
            SavesReceived++;
            if (SaveReceived != null) SaveReceived(assembled);
        }

        private void HandleHello(TransportPacket packet, double now)
        {
            if (!IsHost)
            {
                Rejected++;
                return;
            }
            string build, gameVersion, name;
            if (!SessionCodec.TryHello(packet.Data, out build, out gameVersion, out name)
                || build != config.Build || gameVersion != config.GameVersion)
            {
                Rejected++;
                LastError = "build mismatch";
                return;
            }
            Peer peer;
            bool created = false;
            if (!Peers.TryByKey(packet.Peer, out peer))
            {
                int id = Peers.AllocateId(config.MaxPlayers);
                if (id == 0)
                {
                    Rejected++;
                    LastError = "lobby full";
                    return;
                }
                peer = Peers.Add(id, packet.Peer, name, false, now);
                created = true;
            }
            peer.LastSeen = now;
            peer.Connected = true;
            byte[] welcome = SessionCodec.Welcome(config.ProtocolVersion, Epoch,
                peer.PlayerId, SessionId, HostPlayerId, config.MaxPlayers, config);
            transport.Send(peer.Key, welcome, true);
            if (created && PeerJoined != null) PeerJoined(peer);
        }

        private void HandleWelcome(TransportPacket packet, double now)
        {
            if (IsHost || State != SessionState.Connecting)
            {
                Rejected++;
                return;
            }
            string session, build, gameVersion;
            int hostId, maxPlayers;
            if (!SessionCodec.TryWelcome(packet.Data, out session, out hostId,
                out maxPlayers, out build, out gameVersion))
            {
                Rejected++;
                return;
            }
            if (build != config.Build || gameVersion != config.GameVersion)
            {
                Rejected++;
                LastError = "build mismatch";
                return;
            }
            MessageType type;
            ushort protocol;
            uint epoch;
            int playerId;
            uint sequence;
            SessionCodec.TryHeader(packet.Data, out type, out protocol, out epoch,
                out playerId, out sequence);
            if (playerId < 2)
            {
                Rejected++;
                return;
            }
            SessionId = session;
            Epoch = epoch;
            LocalPlayerId = playerId;
            HostPlayerId = hostId;
            config.MaxPlayers = maxPlayers;
            Peer peer = Peers.Add(hostId, packet.Peer, "host", true, now);
            peer.LastSeen = now;
            SetState(SessionState.Playing);
            if (PeerJoined != null) PeerJoined(peer);
        }

        private void SendHello(double now)
        {
            transport.Send("host", SessionCodec.Hello(config), true);
            lastHello = now;
        }

        private void SendControl(MessageType type, Peer peer, double now)
        {
            if (peer == null) return;
            if (type == MessageType.Ping) peer.PingSent = now;
            transport.Send(peer.Key, SessionCodec.Control(type, config.ProtocolVersion,
                Epoch, LocalPlayerId, 0), true);
        }

        private int ConnectedCount()
        {
            int count = 0;
            foreach (Peer peer in Peers.All) if (peer.Connected) count++;
            return count;
        }

        private void SetState(SessionState state)
        {
            if (State == state) return;
            State = state;
            if (StateChanged != null) StateChanged(state);
        }

        private static uint NewEpoch()
        {
            uint value = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0);
            return value == 0 ? 1u : value;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            transport.Dispose();
        }
    }
}
