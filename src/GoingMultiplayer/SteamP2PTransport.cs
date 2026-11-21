using System;
using System.Collections.Generic;
using Steamworks;
using Steamworks.Data;

namespace GoingMultiplayer
{
    // M2 Steam transport over the proven legacy ISteamNetworking P2P path.
    // The session protocol is transport-agnostic; relay sockets can replace
    // this class later without changing NetSession.
    internal sealed class SteamP2PTransport : ITransport
    {
        private readonly Dictionary<string, SteamId> peers = new Dictionary<string, SteamId>();
        private readonly Queue<TransportPacket> incoming = new Queue<TransportPacket>();
        private SteamId host;
        private bool started;
        private bool closed;

        internal bool Available { get { return SteamClient.IsValid; } }

        public string LocalPeer
        {
            get { return SteamClient.IsValid ? SteamClient.SteamId.Value.ToString() : "none"; }
        }

        internal void SetHost(SteamId id)
        {
            host = id;
            if (id.Value != 0) peers[id.Value.ToString()] = id;
        }

        public void StartHost(int port)
        {
            Start();
        }

        public void StartGuest(int port)
        {
            Start();
        }

        private void Start()
        {
            if (started) return;
            if (!SteamClient.IsValid)
                throw new InvalidOperationException("Steam is not initialized");
            started = true;
            SteamNetworking.AllowP2PPacketRelay(true);
            SteamNetworking.OnP2PSessionRequest += OnSessionRequest;
            SteamNetworking.OnP2PConnectionFailed += OnConnectionFailed;
        }

        private void OnSessionRequest(SteamId peer)
        {
            SteamNetworking.AcceptP2PSessionWithUser(peer);
        }

        private void OnConnectionFailed(SteamId peer, P2PSessionError error)
        {
            // ReadP2PPacket will simply stop delivering; the session timeout
            // owns liveness. Keep this hook for diagnostics.
        }

        public bool TryReceive(out TransportPacket packet)
        {
            packet = default(TransportPacket);
            if (closed) return false;
            if (incoming.Count == 0)
            {
                P2Packet? read;
                while ((read = SteamNetworking.ReadP2PPacket(0)).HasValue)
                {
                    P2Packet value = read.Value;
                    if (value.Data == null || value.Data.Length == 0) continue;
                    string key = value.SteamId.Value.ToString();
                    peers[key] = value.SteamId;
                    incoming.Enqueue(new TransportPacket { Peer = key, Data = value.Data });
                }
            }
            if (incoming.Count == 0) return false;
            packet = incoming.Dequeue();
            return true;
        }

        public bool Send(string peer, byte[] data, bool reliable)
        {
            if (closed || data == null || data.Length == 0) return false;
            SteamId id;
            if (peer == "host") id = host;
            else if (!peers.TryGetValue(peer, out id))
            {
                ulong value;
                if (!ulong.TryParse(peer, out value)) return false;
                id = (SteamId)value;
                peers[peer] = id;
            }
            if (id.Value == 0) return false;
            return SteamNetworking.SendP2PPacket(id, data, data.Length, 0,
                reliable ? P2PSend.Reliable : P2PSend.Unreliable);
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            if (started)
            {
                SteamNetworking.OnP2PSessionRequest -= OnSessionRequest;
                SteamNetworking.OnP2PConnectionFailed -= OnConnectionFailed;
            }
            foreach (SteamId id in peers.Values)
            {
                try { SteamNetworking.CloseP2PSessionWithUser(id); }
                catch (Exception) { }
            }
            peers.Clear();
            incoming.Clear();
        }

        public void Dispose() { Close(); }
    }
}
