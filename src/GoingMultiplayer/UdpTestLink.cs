using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GoingMultiplayer
{
    // Test-only UDP link. No production transport uses this class.
    internal sealed class UdpTestLink
    {
        private Socket socket;
        private IPEndPoint peer;
        private readonly byte[] buffer = new byte[1024];

        internal bool StartHost(int port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Blocking = false;
            return true;
        }

        internal bool StartGuest(int port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Blocking = false;
            peer = new IPEndPoint(IPAddress.Loopback, port);
            return true;
        }

        internal void SetPeer(IPEndPoint endpoint)
        {
            if (endpoint != null) peer = endpoint;
        }

        internal void Send(string message)
        {
            if (socket == null || peer == null || string.IsNullOrEmpty(message)) return;
            byte[] data = Encoding.ASCII.GetBytes(message);
            try { socket.SendTo(data, peer); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        internal bool TryReceive(out string message, out IPEndPoint from)
        {
            message = null;
            from = null;
            if (socket == null) return false;
            try
            {
                if (!socket.Poll(0, SelectMode.SelectRead)) return false;
                EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
                int count = socket.ReceiveFrom(buffer, ref endpoint);
                message = Encoding.ASCII.GetString(buffer, 0, count);
                from = endpoint as IPEndPoint;
                return true;
            }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        internal void Close()
        {
            if (socket == null) return;
            try { socket.Close(); } catch { }
            socket = null;
            peer = null;
        }
    }
}
