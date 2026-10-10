[assembly: Basis.Network.Core.BasisNetworkPackage(typeof(Basis.Network.Core.BasisWebSocketTransport))]

namespace Basis.Network.Core
{
    public sealed class BasisWebSocketTransport : IBasisNetworkPackage
    {
        public const string StackId = "websocket";
        public const string DisplayName = "WebSocket";

        public void Initialize()
        {
            Register();
        }

        public static void Register()
        {
            BasisNetworkStackRegistry.Register(StackId, DisplayName, (listener, configuration) => new BasisWebSocketNetManager(listener, configuration));
            BasisNetworkStackRegistry.RegisterParser(StackId, new BasisWebSocketConnectionTargetParser());
            BasisNetworkStackRegistry.RegisterProbe(StackId, BasisWebSocketProbe.ProbeAsync);
            BasisNetworkStackRegistry.RegisterAddressMatcher(StackId, BasisWebSocketConnectionTargetParser.IsUrl);
            BasisNetworkStackRegistry.RegisterPump(BasisWebSocketPolling.PollAll);
            BasisTransportConfigStore.RegisterType(StackId, typeof(BasisWebSocketTransportConfig));
            BasisConfigXmlDocs.Register(typeof(BasisWebSocketTransportConfig),
                " WebSocket transport (sidecar for the 'websocket' network stack). Only used when NetworkStackId in config.xml includes 'websocket'. This is how web browsers connect, and it also works for native clients on networks that block UDP. Traffic is TCP, so unreliable data cannot overtake reliable data; the server sheds queued position and voice updates for a peer that falls behind instead of letting its latency grow. ",
                new BasisConfigXmlDocs.FieldDoc("ConfigVersion", " Config schema version, managed automatically; new settings are added to this file on load - don't edit by hand. "),
                new BasisConfigXmlDocs.FieldDoc("Port", " TCP port to listen on. 0 = the same number as SetPort in config.xml (UDP and TCP ports are separate, so both can be 4296). Open it as TCP in your firewall and container port mappings. ushort. ", " ===== Listening ===== "),
                new BasisConfigXmlDocs.FieldDoc("Path", " Request path clients must use, for example /basis behind a reverse proxy that routes by path. Empty = accept any path. string. "),
                new BasisConfigXmlDocs.FieldDoc("AllowedOrigins", " Web origins allowed to connect, comma-separated (for example https://play.example.com). * = any origin. Clients that send no Origin header (native clients) are always allowed. string. "),
                new BasisConfigXmlDocs.FieldDoc("TlsEnabled", " Serve wss:// directly. A browser on an https:// page can only open wss:// connections, so either enable this or put a TLS-terminating reverse proxy (Caddy, nginx) in front of the server. true|false. ", " ===== TLS ===== "),
                new BasisConfigXmlDocs.FieldDoc("TlsCertificatePath", " Certificate file: a .pfx/.p12 bundle, or a PEM certificate chain (fullchain.pem) together with TlsKeyPath. Relative paths are looked up next to the server, then in the config folder. The file is checked once a minute and reloaded when it changes, so renewals need no restart. string. "),
                new BasisConfigXmlDocs.FieldDoc("TlsKeyPath", " PEM private key (privkey.pem) for a PEM certificate. Empty when the key is inside the certificate file. string. "),
                new BasisConfigXmlDocs.FieldDoc("TlsCertificatePassword", " Password for a .pfx file or an encrypted PEM key. Empty for none. string. "),
                new BasisConfigXmlDocs.FieldDoc("TrustedProxies", " Reverse proxies whose X-Forwarded-For and X-Real-IP headers are believed, as comma-separated addresses or CIDR ranges. Connections from anywhere else are identified by their own address, so these headers cannot be used to dodge an IP ban. Add your proxy here when it is not on this machine; without it every player behind the proxy shares the proxy's address. string. ", " ===== Reverse proxies ===== "),
                new BasisConfigXmlDocs.FieldDoc("ClientUseTls", " Client side only: connect with wss:// when a server is given as a plain host and port. A full ws:// or wss:// address always wins. true|false. "),
                new BasisConfigXmlDocs.FieldDoc("HandshakeTimeoutMs", " How long a new connection may take to finish its TLS and WebSocket handshake and be answered by the server. int (ms). ", " ===== Timing ===== "),
                new BasisConfigXmlDocs.FieldDoc("MaxPendingHandshakes", " Connections allowed to be mid-handshake at once; more are closed immediately until some finish. Bounds what a flood of half-open connections can cost. int. "),
                new BasisConfigXmlDocs.FieldDoc("PingInterval", " Interval between keep-alive pings. int (ms). "),
                new BasisConfigXmlDocs.FieldDoc("DisconnectTimeout", " Time with nothing received from a peer before it is disconnected. int (ms). "),
                new BasisConfigXmlDocs.FieldDoc("ConnectTimeoutMs", " Client side only: how long to wait for the server to accept a connection. int (ms). "),
                new BasisConfigXmlDocs.FieldDoc("MaxMessageBytes", " Largest single WebSocket message accepted from a peer; a peer sending more is disconnected. int (bytes). ", " ===== Limits ===== "),
                new BasisConfigXmlDocs.FieldDoc("MaxPendingBytesPerPeer", " Reliable data allowed to queue for one peer before it is judged unable to keep up and disconnected. int (bytes). "),
                new BasisConfigXmlDocs.FieldDoc("MaxUnreliableBytesPerPeer", " Queued position and other unreliable data allowed per peer; past this, new updates for that peer are dropped, since a newer one replaces them anyway. Counted in droppedUnreliable on /health. int (bytes). "),
                new BasisConfigXmlDocs.FieldDoc("MaxVoiceBytesPerPeer", " Queued voice allowed per peer, separate from the budget above so a backlog of position updates cannot shed voice. Counted in droppedVoice on /health. int (bytes). "),
                new BasisConfigXmlDocs.FieldDoc("SocketSendBufferBytes", " Kernel send buffer per connection. Smaller keeps queued data in the server, where it can be shed, instead of in the kernel, where it only adds latency. int (bytes); 0 = OS default. "),
                new BasisConfigXmlDocs.FieldDoc("VirtualMtu", " Payload size the server assumes for a WebSocket peer when it packs avatar updates, the same role the UDP MTU plays for LiteNetLib peers. int (bytes); the default matches the largest UDP MTU. "));
        }
    }
}
