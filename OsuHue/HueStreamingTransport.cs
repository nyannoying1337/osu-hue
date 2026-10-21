using HueApi.Entertainment.Models;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OsuHue;

// The SDK's StreamingHueClient activates via an HTTPS client that accepts any
// certificate. Reuse its colour conversion, but own the authenticated transports.
internal sealed class HueStreamingTransport(string ip, string appKey, string clientKey, Guid area) : IDisposable
{
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly byte[] areaBytes = Encoding.ASCII.GetBytes(area.ToString("D"));
    private DtlsTransport? transport;
    private int disposed;

    internal async Task ConnectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(IPAddress.Parse(ip), 2100, timeout.Token);
        var secret = Convert.FromHexString(clientKey);
        try
        {
            var identity = new BasicTlsPskIdentity(appKey, secret);
            transport = new DtlsClientProtocol(new SecureRandom()).Connect(new BridgePskClient(identity), new SocketDatagrams(socket));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal void Send(StreamingGroup group)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var active = transport ?? throw new InvalidOperationException("Hue stream is not connected.");
        foreach (var packet in EncodePackets(group, areaBytes))
            active.Send(packet, 0, packet.Length);
    }

    internal static IEnumerable<byte[]> EncodePackets(StreamingGroup group, byte[] areaBytes)
    {
        foreach (var chunk in group.GetChunksForUpdate(forceUpdate: true))
        {
            // HueStream v2: name, version, sequence, reserved, RGB mode,
            // reserved, 36 ASCII configuration bytes, then seven bytes/channel.
            var packet = new List<byte>(52 + 20 * 7);
            packet.AddRange("HueStream"u8.ToArray());
            packet.AddRange(new byte[] { 2, 0, 1, 0, 0, 0, 0 });
            packet.AddRange(areaBytes);
            foreach (var channel in chunk)
            {
                packet.Add(channel.Id);
                packet.AddRange(channel.State.ToByteArray());
            }
            yield return packet.ToArray();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        // Closing the socket also interrupts a handshake that exceeded its deadline.
        socket.Dispose();
    }

    private sealed class BridgePskClient(TlsPskIdentity identity) : PskTlsClient(identity)
    {
        public override ProtocolVersion MinimumVersion => ProtocolVersion.DTLSv12;
        public override ProtocolVersion ClientVersion => ProtocolVersion.DTLSv12;
        public override int[] GetCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
    }

    private sealed class SocketDatagrams(Socket socket) : DatagramTransport
    {
        public int GetReceiveLimit() => 4096;
        public int GetSendLimit() => 4096;
        public int Receive(byte[] buffer, int offset, int length, int waitMillis)
        {
            if (!socket.Poll(TimeSpan.FromMilliseconds(Math.Max(0, waitMillis)), SelectMode.SelectRead))
                return -1;
            return socket.Receive(buffer, offset, Math.Min(length, GetReceiveLimit()), SocketFlags.None);
        }
        public void Send(byte[] buffer, int offset, int length) => socket.Send(buffer, offset, length, SocketFlags.None);
        public void Close() => socket.Dispose();
    }
}
