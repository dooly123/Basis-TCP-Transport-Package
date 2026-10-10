using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

internal sealed class RecordingHealthWriter : IBasisHealthWriter
{
    public readonly List<string> Fields = new();
    public void Number(string name, long value) => Fields.Add(name + "=" + value);
    public void Flag(string name, bool value) => Fields.Add(name + "=" + value);
    public void Text(string name, string value) => Fields.Add(name + "=" + value);
}

[Collection("BasisServer shared network statics")]
public class WebSocketPackageTests
{
    [Fact]
    public void ThePackageEntryRegistersTheWebSocketStack()
    {
        BasisNetworkPackages.Initialize(typeof(BasisWebSocketTransport).Assembly);

        Assert.Contains(typeof(BasisWebSocketTransport), BasisNetworkPackages.Loaded);
        Assert.True(BasisNetworkStackRegistry.IsRegistered(BasisWebSocketTransport.StackId));
        Assert.Equal(BasisWebSocketTransport.DisplayName, BasisNetworkStackRegistry.GetDisplayName(BasisWebSocketTransport.StackId));
        Assert.IsType<BasisWebSocketConnectionTargetParser>(BasisNetworkStackRegistry.GetParser(BasisWebSocketTransport.StackId));
        Assert.Equal(typeof(BasisWebSocketTransportConfig), BasisTransportConfigStore.RegisteredTypes[BasisWebSocketTransport.StackId]);
    }

    [Fact]
    public void WebSocketAddressesGoToTheWebSocketStack()
    {
        BasisWebSocketTransport.Register();

        Assert.True(BasisNetworkStackRegistry.TryMatchAddress("wss://play.example.com/basis#secret", out string stack));
        Assert.Equal(BasisWebSocketTransport.StackId, stack);
        ConnectionTarget target = BasisNetworkStackRegistry.ParseAddress("https://play.example.com/basis#secret");
        Assert.Equal(BasisWebSocketTransport.StackId, target.StackId);
        Assert.Equal("wss://play.example.com/basis", target.Get(ConnectionTarget.Keys.Address));
        Assert.Equal("443", target.Get(ConnectionTarget.Keys.Port));
        Assert.Equal("secret", target.Get(ConnectionTarget.Keys.Password));
        Assert.False(BasisNetworkStackRegistry.TryMatchAddress("play.example.com:4296", out _));
    }

    [Fact]
    public void TheSidecarIsWrittenWithItsDocs()
    {
        BasisWebSocketTransport.Register();
        using ConfigTestSupport.TempDir dir = new ConfigTestSupport.TempDir();

        BasisTransportConfigStore.LoadAll(dir.Path);

        string xml = File.ReadAllText(Path.Combine(dir.Path, BasisTransportConfigStore.TransportsFolderName, BasisWebSocketTransport.StackId + ".xml"));
        Assert.Contains("WebSocket transport (sidecar for the 'websocket' network stack)", xml);
        Assert.Contains("<!-- TCP port to listen on.", xml);
        Assert.Contains("<TrustedProxies>", xml);
    }

    [Fact]
    public void TheTransportReportsItsHealthAndTimeout()
    {
        BasisWebSocketNetManager manager = new BasisWebSocketNetManager(new EventBasedNetListener(), WebSocketTestSupport.FastConfig());
        RecordingHealthWriter writer = new RecordingHealthWriter();

        manager.WriteHealth(writer);

        Assert.Equal(new[] { "port=0", "tls=False", "handshakes=0", "droppedUnreliable=0", "droppedVoice=0" }, writer.Fields);
        IBasisTransportTimeouts timeouts = WebSocketTestSupport.FastConfig();
        Assert.Equal(5000, timeouts.DisconnectTimeoutMs);
    }
}
