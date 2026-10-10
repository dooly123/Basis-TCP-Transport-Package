# Basis WebSocket Transport

Adds the `websocket` network stack to Basis. Web builds connect through it (browsers cannot use
UDP), and native clients can use it on networks that block UDP. Players on WebSocket and on
LiteNetLib share one player id space, so they see each other normally.

Basis does not include this package. Install it into the Unity project (web builds need it) and
into every Basis Server that web players should join. It is a Basis server package, so the same
folder is compiled into the Unity client and into the server.

| Folder | Built into |
|---|---|
| `Runtime/` | `BasisNetworkCore` (the client and the server), through `BasisNetworkCore.asmref` |
| `Unity/` | its own Unity assembly: the browser WebSocket channel and `BasisWebSocket.jslib` for web builds |
| `Server~/BasisServerTests/` | the server test project, so its tests run with `dotnet test` |

## Installing

The Basis Package Manager lists it as Basis WebSocket Transport; installing it from there adds it to
the Unity project and to the Basis Server.

By hand, Unity: add it to `Basis/Packages/manifest.json` by its git URL, or from a local copy with
a `file:` path (relative to `Basis/Packages`):

```json
"com.basis.transport.websocket": "https://github.com/dooly123/Basis-TCP-Transport-Package.git"
```

Server: install it with the Basis Package Manager (the Server tab, or the command line):

```
basispm --project <Basis repository> server-install https://github.com/dooly123/Basis-TCP-Transport-Package.git
```

A Docker image build only sees `Basis Server/`, so install it by git URL on servers built as images:
the build restores it from git at the commit recorded in `packages-lock.props`. To take it out
again, run `basispm server-remove com.basis.transport.websocket` and delete the Unity manifest line.

## Turning it on for a server

Installing the package only makes the stack available. A server listens on it when `NetworkStackId`
in `config/config.xml` (or the `NetworkStackId` environment variable) includes `websocket`:

```xml
<NetworkStackId>litenetlib,websocket</NetworkStackId>
```

The WebSocket listener uses the same port number as `SetPort`, over TCP, unless
`config/transports/websocket.xml` sets its own `Port`. That file is written on first start and
documents every setting (TLS certificates, allowed origins, trusted reverse proxies, queue limits).
`/health` reports the listener under `transports`, with its port, TLS state, pending handshakes and
shed updates. See "Web Clients (WebSocket)" in `Basis Server/Docker/README.md` for a deployment
walkthrough.

## Clients

- Server addresses that start with `ws://`, `wss://`, `http://` or `https://` are routed to this
  stack, in the Servers panel and in web join links (`?connection=wss://host/path`).
- In a web build the package also takes over the LiteNetLib stack, so saved `host:port` servers and
  `?connection=host:port` links connect over WebSocket to the same port.
- `ClientUseTls` in `websocket.xml` makes plain `host:port` addresses use `wss://`.

## License

MIT licensed; see `LICENSE`.
