using Basis.Network.Core;
using System.Net;
using System.Net.WebSockets;
using Xunit;
using CryptoRng = System.Security.Cryptography.RandomNumberGenerator;

namespace BasisServerTests;

public class WebSocketSealingTests
{
    private sealed class Pair : IDisposable
    {
        public readonly BasisWebSocketNetManager Server, Client;
        public readonly RecordedEvents ServerEvents, ClientEvents;
        public readonly int Port;

        public Pair(BasisWebSocketTransportConfig? config = null)
        {
            config ??= WebSocketTestSupport.FastConfig();
            Port = TransportTestSupport.FreeTcpPort();
            EventBasedNetListener serverListener = new EventBasedNetListener();
            ServerEvents = new RecordedEvents(serverListener);
            Server = new BasisWebSocketNetManager(serverListener, config);
            Server.Start(IPAddress.Loopback, null!, Port);
            Assert.True(Server.IsRunning);
            EventBasedNetListener clientListener = new EventBasedNetListener();
            ClientEvents = new RecordedEvents(clientListener);
            Client = new BasisWebSocketNetManager(clientListener, config);
            ((NetManager)Client).Start();
        }

        public async Task<(NetPeer Client, NetPeer Server)> ConnectAsync()
        {
            NetPeer client = Client.Connect("127.0.0.1", Port, TransportTestSupport.Writer());
            await ClientEvents.NextConnected();
            return (client, await ServerEvents.NextConnected());
        }

        public void Dispose()
        {
            Client.Stop();
            Server.Stop();
        }
    }

    private sealed class RawClient : IDisposable
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();

        public static async Task<RawClient> ConnectAsync(int port)
        {
            RawClient raw = new RawClient();
            raw._socket.Options.AddSubProtocol(BasisWebSocketProtocol.SubProtocol);
            await raw._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
            byte[] body = new byte[BasisWebSocketProtocol.ConnectHeaderBytes + 1];
            BasisWebSocketProtocol.WriteUInt32(body, 0, BasisWebSocketProtocol.ConnectMagic);
            body[4] = BasisWebSocketProtocol.Version;
            body[5] = 7;
            await raw.SendFrameAsync(BasisWebSocketProtocol.KindConnect, body);
            return raw;
        }

        public async Task<bool> WaitForAsync(byte kind, int milliseconds = 5000)
        {
            byte[] buffer = new byte[64 * 1024];
            using CancellationTokenSource timeout = new CancellationTokenSource(milliseconds);
            while (true)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return false;
                int offset = 0;
                while (BasisWebSocketProtocol.TryReadFrame(buffer, ref offset, result.Count, out byte frameKind, out _, out _))
                {
                    if (frameKind == kind) return true;
                }
            }
        }

        public Task SendDataAsync(byte channel, DeliveryMethod method, byte[] payload, BasisMessageCipher? sealer = null)
        {
            byte[] body = new byte[BasisWebSocketProtocol.DataHeaderBytes + payload.Length + (sealer == null ? 0 : BasisMessageCipher.Overhead)];
            body[0] = channel;
            body[1] = (byte)method;
            Buffer.BlockCopy(payload, 0, body, BasisWebSocketProtocol.DataHeaderBytes, payload.Length);
            sealer?.Seal(0, channel, method, body, BasisWebSocketProtocol.DataHeaderBytes, payload.Length);
            return SendFrameAsync(BasisWebSocketProtocol.KindData, body);
        }

        public Task SendControlAsync(byte kind, byte[] payload, BasisMessageCipher? sealer = null)
        {
            byte[] body = new byte[payload.Length + (sealer == null ? 0 : BasisMessageCipher.Overhead)];
            Buffer.BlockCopy(payload, 0, body, 0, payload.Length);
            sealer?.Seal(0, kind, BasisWebSocketProtocol.ControlMethod, body, 0, payload.Length);
            return SendFrameAsync(kind, body);
        }

        private Task SendFrameAsync(byte kind, byte[] body)
        {
            byte[] frame = new byte[BasisWebSocketProtocol.FrameSize(body.Length)];
            int header = BasisWebSocketProtocol.WriteFrameHeader(frame, 0, kind, body.Length);
            Buffer.BlockCopy(body, 0, frame, header, body.Length);
            return _socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }

    private static byte[] Key()
    {
        byte[] key = new byte[32];
        CryptoRng.Fill(key);
        return key;
    }

    [Fact]
    public async Task SealedClientDataReachesAServerThatRequiresIt()
    {
        using Pair pair = new Pair();
        var (client, server) = await pair.ConnectAsync();
        byte[] key = Key();
        int mtu = client.Mtu;
        Assert.True(server.RequireSealedInbound(key));
        Assert.True(client.SealOutbound(key));
        Assert.Equal(mtu - BasisMessageCipher.Overhead, client.Mtu);

        client.Send(new byte[] { 1, 2, 3 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
        client.SendUnreliableRawMerge(new byte[] { 4, 5, 6 }, 0, 3, BasisNetworkCommons.CompressedAvatarBundleChannel, 2, 77);
        var first = await pair.ServerEvents.NextReceived();
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Data);
        Assert.Equal(BasisNetworkCommons.ChatChannel, first.Channel);
        Assert.Equal(DeliveryMethod.ReliableOrdered, first.Method);
        var merged = await pair.ServerEvents.NextReceived();
        Assert.Equal(new byte[] { 4, 5, 77 }, merged.Data);
        Assert.Equal(DeliveryMethod.Unreliable, merged.Method);

        server.Send(new byte[] { 8 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
        Assert.Equal(new byte[] { 8 }, (await pair.ClientEvents.NextReceived()).Data);
    }

    [Fact]
    public async Task UnsealedOrWronglySealedDataIsDroppedOnceTheServerRequiresSealing()
    {
        using Pair pair = new Pair();
        var (client, server) = await pair.ConnectAsync();
        server.RequireSealedInbound(Key());
        client.Send(new byte[] { 1 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
        client.SealOutbound(Key());
        client.Send(new byte[] { 2 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
        await Task.Delay(300);
        Assert.Empty(pair.ServerEvents.Received);
        Assert.Equal(1, pair.Server.ConnectedPeersCount);
    }

    [Fact]
    public async Task ConcurrentSealedSendersNeverLoseReliableData()
    {
        using Pair pair = new Pair();
        var (client, server) = await pair.ConnectAsync();
        byte[] key = Key();
        server.RequireSealedInbound(key);
        client.SealOutbound(key);
        const int perThread = 1500, threads = 4;
        await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
        {
            for (int index = 0; index < perThread; index++)
            {
                client.Send(BitConverter.GetBytes(thread * perThread + index), BasisNetworkCommons.SceneChannel, DeliveryMethod.ReliableOrdered);
            }
        })));
        await TransportTestSupport.Until(() => pair.ServerEvents.Received.Count == perThread * threads, 15000);
        Assert.Equal(Enumerable.Range(0, perThread * threads), pair.ServerEvents.Received.Select(item => BitConverter.ToInt32(item.Data)).OrderBy(value => value));
    }

    [Fact]
    public async Task AcceptedAwaitingSeal_DropsDataUntilTheServerHasTheKey()
    {
        using Pair pair = new Pair();
        pair.ServerEvents.OnRequest = request => request.AcceptAwaitingSeal();
        var (client, server) = await pair.ConnectAsync();
        client.Send(new byte[] { 1 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
        await Task.Delay(300);
        Assert.Empty(pair.ServerEvents.Received);

        byte[] key = Key();
        Assert.True(server.RequireSealedInbound(key));
        Assert.True(client.SealOutbound(key));
        client.Send(new byte[] { 2 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
        Assert.Equal(new byte[] { 2 }, (await pair.ServerEvents.NextReceived()).Data);
        Assert.Empty(pair.ServerEvents.Received);
        Assert.Equal(1, pair.Server.ConnectedPeersCount);
    }

    [Fact]
    public async Task SealedControlFramesKeepTheConnectionAliveAndCarryTheDisconnect()
    {
        BasisWebSocketTransportConfig config = WebSocketTestSupport.FastConfig();
        config.DisconnectTimeout = 1000;
        using Pair pair = new Pair(config);
        pair.ServerEvents.OnRequest = request => request.AcceptAwaitingSeal();
        var (client, server) = await pair.ConnectAsync();
        byte[] key = Key();
        server.RequireSealedInbound(key);
        client.SealOutbound(key);
        client.Send(new byte[] { 1 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
        Assert.Equal(new byte[] { 1 }, (await pair.ServerEvents.NextReceived()).Data);

        await Task.Delay(2500);
        Assert.Empty(pair.ServerEvents.Disconnected);
        Assert.Equal(1, pair.Server.ConnectedPeersCount);

        client.Disconnect();
        var gone = await pair.ServerEvents.NextDisconnected();
        Assert.Equal(DisconnectReason.RemoteConnectionClose, gone.Reason);
    }

    [Fact]
    public async Task OnceSealedDataArrives_UnsealedFramesOfAnyKindAreIgnored()
    {
        BasisWebSocketTransportConfig config = WebSocketTestSupport.FastConfig();
        int port = TransportTestSupport.FreeTcpPort();
        EventBasedNetListener listener = new EventBasedNetListener();
        RecordedEvents events = new RecordedEvents(listener);
        events.OnRequest = request => request.AcceptAwaitingSeal();
        BasisWebSocketNetManager server = new BasisWebSocketNetManager(listener, config);
        server.Start(IPAddress.Loopback, null!, port);
        try
        {
            using RawClient raw = await RawClient.ConnectAsync(port);
            Assert.True(await raw.WaitForAsync(BasisWebSocketProtocol.KindAccept));
            NetPeer peer = await events.NextConnected();

            await raw.SendDataAsync(BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered, new byte[] { 1 });
            byte[] key = Key();
            Assert.True(peer.RequireSealedInbound(key));
            BasisMessageCipher sealer = new BasisMessageCipher(key);
            await raw.SendControlAsync(BasisWebSocketProtocol.KindPing, new byte[BasisWebSocketProtocol.PingBodyBytes]);
            await raw.SendDataAsync(BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered, new byte[] { 2 }, sealer);
            Assert.Equal(new byte[] { 2 }, (await events.NextReceived()).Data);

            await raw.SendControlAsync(BasisWebSocketProtocol.KindDisconnect, Array.Empty<byte>());
            await raw.SendDataAsync(BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered, new byte[] { 3 });
            await raw.SendDataAsync(BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered, new byte[] { 4 }, sealer);
            Assert.Equal(new byte[] { 4 }, (await events.NextReceived()).Data);
            Assert.Empty(events.Disconnected);
            Assert.Equal(1, server.ConnectedPeersCount);

            await raw.SendControlAsync(BasisWebSocketProtocol.KindDisconnect, Array.Empty<byte>(), sealer);
            var gone = await events.NextDisconnected();
            Assert.Equal(DisconnectReason.RemoteConnectionClose, gone.Reason);
            Assert.Empty(events.Received);
        }
        finally
        {
            server.Stop();
        }
    }
}
