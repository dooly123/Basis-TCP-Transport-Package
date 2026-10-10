using Basis.Network.Core;
using BasisNetworkCore.Security;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System.Net;
using System.Net.Sockets;
using Xunit;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class WebSocketMultiTransportTests
{
    [Fact]
    public void TheRegistryBuildsAWebSocketCompositeFromTheStackList()
    {
        BasisLiteNetLibTransport.Register();
        BasisWebSocketTransport.Register();
        string original = BasisNetworkStackRegistry.ActiveStackId;
        try
        {
            NetManager both = BasisNetworkStackRegistry.Create("litenetlib, websocket", new EventBasedNetListener(), new Configuration());
            BasisMultiTransportNetManager composite = Assert.IsType<BasisMultiTransportNetManager>(both);
            Assert.IsType<LNLNetManager>(composite.Transports[0]);
            Assert.IsType<BasisWebSocketNetManager>(composite.Transports[1]);
            Assert.Equal("litenetlib,websocket", BasisNetworkStackRegistry.ActiveStackId);
            Assert.NotNull(both.LiteNetLibManager());
            Assert.NotNull(both.FindTransport<BasisWebSocketNetManager>());

            NetManager one = BasisNetworkStackRegistry.Create("websocket, not-a-real-stack", new EventBasedNetListener(), new Configuration());
            Assert.IsType<BasisWebSocketNetManager>(one);
            Assert.Null(one.LiteNetLibManager());
            Assert.Single(one.Transports());
        }
        finally
        {
            BasisNetworkStackRegistry.SetActiveStackId(original);
        }
    }

    [Fact]
    public async Task LiteNetLibAndWebSocketPlayersGetDistinctIdsFromOneSpace()
    {
        BasisLiteNetLibTransport.Register();
        int port = TransportTestSupport.FreeTcpAndUdpPort();
        EventBasedNetListener serverListener = new EventBasedNetListener();
        RecordedEvents server = new RecordedEvents(serverListener);
        List<NetPeer> accepted = new();
        server.OnRequest = request =>
        {
            NetPeer peer = request.Accept();
            lock (accepted) accepted.Add(peer);
            return peer;
        };
        BasisMultiTransportNetManager composite = new BasisMultiTransportNetManager(
            new[] { BasisNetworkStackRegistry.LiteNetLibId, BasisWebSocketTransport.StackId },
            serverListener,
            new Configuration(),
            (id, listener, configuration) => id == BasisWebSocketTransport.StackId
                ? new BasisWebSocketNetManager(listener, WebSocketTestSupport.FastConfig())
                : BasisNetworkStackRegistry.CreateSingle(id, listener, configuration));
        composite.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, port);
        EventBasedNetListener webListener = new EventBasedNetListener();
        RecordedEvents web = new RecordedEvents(webListener);
        BasisWebSocketNetManager webClient = new BasisWebSocketNetManager(webListener, WebSocketTestSupport.FastConfig());
        EventBasedNetListener udpListener = new EventBasedNetListener();
        RecordedEvents udp = new RecordedEvents(udpListener);
        NetManager udpClient = BasisNetworkStackRegistry.CreateSingle(BasisNetworkStackRegistry.LiteNetLibId, udpListener, new Configuration());
        try
        {
            Assert.True(((NetManager)composite).IsRunning);
            ((NetManager)webClient).Start();
            udpClient.Start();
            NetPeer webPeer = webClient.Connect("127.0.0.1", port, TransportTestSupport.Writer(1));
            NetPeer udpPeer = udpClient.Connect("127.0.0.1", port, TransportTestSupport.Writer(2));
            await web.NextConnected();
            await udp.NextConnected();
            await TransportTestSupport.Until(() => { lock (accepted) return accepted.Count == 2; });
            NetPeer[] peers;
            lock (accepted) peers = accepted.ToArray();
            Assert.NotEqual(peers[0].Id, peers[1].Id);
            Assert.All(peers, peer => Assert.True(composite.PeerIds.IsLive(peer.Id)));
            Assert.Contains(peers, peer => peer.StackId == BasisWebSocketTransport.StackId && peer.Id == webPeer.RemoteId);
            Assert.Contains(peers, peer => peer.StackId == BasisNetworkStackRegistry.LiteNetLibId && peer.Id == udpPeer.RemoteId);
            Assert.Equal(2, ((NetManager)composite).ConnectedPeersCount);

            NetPeer serverSideWeb = peers.First(peer => peer.StackId == BasisWebSocketTransport.StackId);
            NetPeer serverSideUdp = peers.First(peer => peer.StackId == BasisNetworkStackRegistry.LiteNetLibId);
            serverSideWeb.Send(new byte[] { 42 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
            serverSideUdp.Send(new byte[] { 43 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
            Assert.Equal(new byte[] { 42 }, (await web.NextReceived()).Data);
            Assert.Equal(new byte[] { 43 }, (await udp.NextReceived()).Data);
        }
        finally
        {
            webClient.Stop();
            udpClient.Stop();
            composite.Stop();
        }
    }

    [Fact]
    public void ACompositeWithATransportThatCannotBindIsNotRunning()
    {
        BasisLiteNetLibTransport.Register();
        int port = TransportTestSupport.FreeTcpAndUdpPort();
        TcpListener blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        BasisMultiTransportNetManager composite = new BasisMultiTransportNetManager(
            new[] { BasisNetworkStackRegistry.LiteNetLibId, BasisWebSocketTransport.StackId },
            new EventBasedNetListener(),
            new Configuration(),
            (id, listener, configuration) => id == BasisWebSocketTransport.StackId
                ? new BasisWebSocketNetManager(listener, WebSocketTestSupport.FastConfig())
                : BasisNetworkStackRegistry.CreateSingle(id, listener, configuration));
        try
        {
            composite.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, port);
            NetManager manager = composite;
            Assert.False(manager.IsRunning);
            Assert.True(composite.Transports[0].IsRunning);
            Assert.False(composite.Transports[1].IsRunning);
        }
        finally
        {
            composite.Stop();
            blocker.Stop();
        }
    }
}

[Collection("BasisServer shared network statics")]
public class MixedTransportServerTests
{
    [Fact]
    public async Task TheServerAdmitsAWebPlayerAndAUdpPlayerIntoOneWorld()
    {
        BasisLiteNetLibTransport.Register();
        using ServerStaticsScope scope = new ServerStaticsScope();
        int port = TransportTestSupport.FreeTcpAndUdpPort();
        Configuration config = new Configuration
        {
            SetPort = (ushort)port,
            PeerLimit = 100,
            UseAuth = false,
            UseAuthIdentity = false,
            HasFileSupport = false,
            EnableStatistics = false,
            BasisUserRestrictionMode = BasisUserRestrictionMode.Normal,
            NetworkStackId = "litenetlib,websocket",
            OverrideAutoDiscoveryOfIpv = true,
            IPv4Address = "127.0.0.1",
            IPv6Address = "::1",
        };
        NetworkServer.Configuration = config;
        NetworkServer.Auth = new FakeAuth { Result = true };
        NetworkServer.AuthIdentity = new MapAuthIdentity();
        NetworkServer.AllowList = new BasisAllowList();
        NetworkServer.BanList = new BasisBanList();
        NetworkServer.HighQualityLength = ConvertToSize(BitQuality.High);
        BasisWebSocketTransport.Register();
        Assert.True(NetworkServer.SetupServer(config));
        BasisServerHandleEvents.SubscribeServerEvents();

        EventBasedNetListener webListener = new EventBasedNetListener();
        RecordedEvents web = new RecordedEvents(webListener);
        BasisWebSocketNetManager webClient = new BasisWebSocketNetManager(webListener, WebSocketTestSupport.FastConfig());
        EventBasedNetListener udpListener = new EventBasedNetListener();
        RecordedEvents udp = new RecordedEvents(udpListener);
        NetManager udpClient = BasisNetworkStackRegistry.CreateSingle(BasisNetworkStackRegistry.LiteNetLibId, udpListener, new Configuration());
        try
        {
            Assert.IsType<BasisMultiTransportNetManager>(NetworkServer.Server);
            ((NetManager)webClient).Start();
            udpClient.Start();
            byte[] webJoin = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, Array.Empty<byte>(), LifecycleSupport.MakeReady(LifecycleSupport.NewUuid(), "Browser Player"));
            NetPeer webPeer = webClient.Connect("127.0.0.1", port, TransportTestSupport.Writer(webJoin));
            await web.NextConnected();
            await TransportTestSupport.Until(() => web.Received.Any(item => item.Channel == BasisNetworkCommons.metaDataChannel));
            await TransportTestSupport.Until(() => NetworkServer.AuthenticatedPeers.Count == 1);

            byte[] udpJoin = LifecycleSupport.ConnectPayload(BasisNetworkVersion.ServerVersion, Array.Empty<byte>(), LifecycleSupport.MakeReady(LifecycleSupport.NewUuid(), "Desktop Player"));
            NetPeer udpPeer = udpClient.Connect("127.0.0.1", port, TransportTestSupport.Writer(udpJoin));
            await udp.NextConnected();
            await TransportTestSupport.Until(() => udp.Received.Any(item => item.Channel == BasisNetworkCommons.metaDataChannel));
            await TransportTestSupport.Until(() => NetworkServer.AuthenticatedPeers.Count == 2);

            NetPeer[] admitted = NetworkServer.AuthenticatedPeers.Values.ToArray();
            Assert.Contains(admitted, peer => peer.StackId == BasisWebSocketTransport.StackId && peer.Id == webPeer.RemoteId);
            Assert.Contains(admitted, peer => peer.StackId == BasisNetworkStackRegistry.LiteNetLibId && peer.Id == udpPeer.RemoteId);
            Assert.NotEqual(webPeer.RemoteId, udpPeer.RemoteId);
            Assert.Single(NetworkServer.PeersOnTransport(BasisWebSocketTransport.StackId));

            await TransportTestSupport.Until(() => web.Received.Any(item => item.Channel == BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel), 8000);
            await TransportTestSupport.Until(() => udp.Received.Any(item => item.Channel == BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel), 8000);

            webPeer.Disconnect();
            await TransportTestSupport.Until(() => NetworkServer.AuthenticatedPeers.Count == 1);
            Assert.Equal(udpPeer.RemoteId, NetworkServer.AuthenticatedPeers.Keys.Single());
        }
        finally
        {
            webClient.Stop();
            udpClient.Stop();
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!NetworkServer.AuthenticatedPeers.IsEmpty && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            foreach (NetPeer straggler in NetworkServer.AuthenticatedPeers.Values.ToArray())
            {
                BasisServerHandleEvents.HandlePeerDisconnected(straggler, new DisconnectInfo { Reason = DisconnectReason.DisconnectPeerCalled });
            }
            BasisServerHandleEvents.StopWorker();
            NetworkServer.Server = null!;
            NetworkServer.Listener = null!;
        }
    }
}
