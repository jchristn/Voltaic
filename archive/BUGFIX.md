# BUGFIX: let callers supply the HTTP handler for `McpHttpClient`

Requested by Garrison (P15.22, security review finding SR-07). Target release: **v2.4.0**, a minor release that adds API only. **Status: shipped in v2.4.0.** Nothing existing changes behavior.

## Problem

`McpHttpClient` builds its own `HttpClient` in its only constructor:

```csharp
public McpHttpClient()
{
    VoltaicInstruments.EnsureCreated();
    _HttpClient = new HttpClient();
    _RequestDispatcher.Log = LogMessage;
}
```

A default `HttpClient` follows redirects and connects to whatever address DNS returns at connect time. An application that points `McpHttpClient` at a URL chosen by someone it does not fully trust can be steered to an internal address, such as a cloud metadata service at 169.254.169.254:

- **Redirects.** The MCP server answers `302 Location: http://169.254.169.254/...` and the client follows it.
- **DNS rebinding.** The application resolves the name, checks the address, and allows it. The name then resolves to a blocked address when the client connects.

The application cannot fix either, because it cannot reach the handler. It can check the URL's address before connecting, which Garrison does today, but that check runs before the connection is made, so it closes neither gap.

`A2AClient` and `A2AGrpcClient` already accept an `HttpClient`. `McpHttpClient` is the only Voltaic HTTP client without that option.

## Change

### 1. New constructor that takes a handler

```csharp
/// <summary>
/// Initializes a new instance of the <see cref="McpHttpClient"/> class that sends every request (JSON-RPC POSTs,
/// the SSE GET stream, session DELETE, and reconnects) through the given handler. Use it to control redirects,
/// proxies, certificates, or the connection itself, for example a SocketsHttpHandler whose ConnectCallback refuses
/// some addresses.
/// </summary>
/// <param name="handler">Handler for every request this client sends.</param>
/// <param name="disposeHandler">Whether disposing this client also disposes the handler. Default true.</param>
/// <exception cref="ArgumentNullException">Thrown when handler is null.</exception>
public McpHttpClient(HttpMessageHandler handler, bool disposeHandler = true)
{
    if (handler == null) throw new ArgumentNullException(nameof(handler));
    VoltaicInstruments.EnsureCreated();
    _HttpClient = new HttpClient(handler, disposeHandler);
    _RequestDispatcher.Log = LogMessage;
}
```

The parameterless constructor stays as it is.

Use a handler rather than an `HttpClient` on purpose. `SetRequestHeader` writes to `_HttpClient.DefaultRequestHeaders`, and the client sets other per-session state on its `HttpClient`. With a handler, Voltaic still owns the `HttpClient`, so none of that can leak into a client the caller shares elsewhere. The caller controls the transport, which is all the security fix needs.

### 2. Every request goes through `_HttpClient`

Check that no code path creates a second `HttpClient` or calls a static helper that bypasses `_HttpClient`. Today every send uses `_HttpClient`:

- `SendAsync` in the POST path (around line 677);
- the stateless path (around line 953);
- notifications and retries (around lines 1171 and 1185);
- the SSE loop (around line 1262);
- session termination (around line 1352);
- `ListenAsync` (around line 1690);
- input-request replies (around line 1807).

Add a test that fails if that changes (see below).

### 3. Redirect behavior stays the caller's choice

Do not set `AllowAutoRedirect` in Voltaic. With the new constructor the caller decides. The default constructor keeps today's behavior, redirects included, so existing applications see no change.

### 4. The same constructor on `McpWebsocketsClient`

`ClientWebSocket` follows no redirects, but it connects to whatever address DNS returns, so DNS rebinding applies to the WebSocket client too. Add `McpWebsocketsClient(HttpMessageHandler handler, bool disposeHandler = true)`. It wraps the handler in an `HttpMessageInvoker` that every `ConnectAsync` passes to `ClientWebSocket.ConnectAsync(Uri, HttpMessageInvoker, CancellationToken)` (.NET 7 and later, so both targets have it), and disposes the invoker when `disposeHandler` is true. The handler must be a `SocketsHttpHandler` or delegating handlers over one.

### 5. Scope across the library

Every Voltaic client that opens outbound connections was reviewed:

| Client | Redirects | DNS rebinding | Change |
|---|---|---|---|
| `McpHttpClient` | Followed by the default `HttpClient` | Exposed | New handler constructor (items 1-3) |
| `McpWebsocketsClient` | Not followed by `ClientWebSocket` | Exposed | New handler constructor (item 4) |
| `A2AClient`, `A2AHttpJsonClient`, `A2AGrpcClient`, `A2ACardResolver` | Caller's `HttpClient` | Caller's `HttpClient` | None: they already take an optional `HttpClient` |
| `A2AHttpServer`/`A2AGrpcServer` push delivery | `AllowAutoRedirect = false` | `ConnectCallback` resolves and checks (`A2AWebhookAddressPolicy`) | None: already guarded |
| `JsonRpcClient`, `McpTcpClient` | None on raw TCP | The caller resolves and passes the checked IP address to `ConnectAsync` | Documented in the README |
| `McpClient` (stdio) | Not a network client | Not applicable | None |

## Tests

Add to the HTTP client suite. Run on net8.0 and net10.0, through the console, xUnit, and NUnit runners.

1. **Handler is used for every request type.** A counting `DelegatingHandler` around a `SocketsHttpHandler`. Run `ConnectStreamableAsync`, `CallAsync`, `NotifyAsync`, the SSE GET, a stateless (`2026-07-28`) call, and `Disconnect` against the in-process `McpHttpServer`. Assert that the handler saw each request.
2. **Redirects are not followed when the handler says so.** A tiny `HttpListener` answers `POST /mcp` with `302 Location:` pointing at a second listener. With `new SocketsHttpHandler { AllowAutoRedirect = false }`, assert that the second listener receives nothing and the connect fails.
3. **The connect callback can refuse addresses.** A handler whose `ConnectCallback` throws for 127.0.0.2. Point the client at `127.0.0.2` directly (a hosts entry would need machine setup and breaks determinism; the callback runs before any socket connects, so the address need not be configured). Assert that the connect fails with the callback's exception as the inner exception.
4. **Ownership.** With `disposeHandler: false`, disposing the client leaves the handler usable. With `true`, it disposes the handler.
5. **WebSocket.** The upgrade goes through the handler, the connect callback refuses `127.0.0.2`, a `302` to the upgrade fails and the target is never contacted, and ownership follows `disposeHandler` across reconnects.
6. **Default behavior unchanged.** The parameterless `McpHttpClient` still follows the redirect.
7. **Native AOT.** Add one call through each new constructor to `src/Test.Aot`.

## Documentation

- README: in the `McpHttpClient` section, a short "Supplying the HTTP handler" example that uses `SocketsHttpHandler` with `AllowAutoRedirect = false` and a `ConnectCallback`.
- CHANGELOG `v2.4.0`, under "Added": `McpHttpClient(HttpMessageHandler handler, bool disposeHandler = true)`.
- UPGRADING: one line saying that no change is needed.
- Bump `<Version>` to 2.4.0 in `src/Voltaic/Voltaic.csproj`, and update the version mentioned in CLAUDE.md, when releasing.

## Compatibility

The release adds a constructor and nothing else. Source and binary compatibility with v2.3.x is unchanged, and `new McpHttpClient()` behaves exactly as before.

## What Garrison does after the release

1. Bump the `Voltaic` package to 2.4.0 in `Garrison.Server`, `Garrison.Harnesses`, and `Garrison.FakeHarness`.
2. Use `new McpHttpClient(OutboundAddressPolicy.CreateHandler(), true)` wherever the URL comes from a tenant:
   - the HTTP MCP server test in `src/Garrison.Server/Routes/McpServerRoutes.cs`;
   - MCP preflight on workers in `src/Garrison.Harnesses/Preflight/McpPreflight.cs`;
   - the provider harness MCP bridge in `src/Garrison.ProviderHarness/ProviderMcpBridge.cs`.

   The handler never follows redirects, and its `ConnectCallback` resolves the host and connects only to an address the policy allows.
3. Keep the pre-connect check in the MCP server test. It still gives a fast, readable error for literal metadata addresses.
4. Mark the Voltaic part of SR-07 as fixed in `docs/SECURITY_REVIEW.md`, with a test that points an MCP server test at a local listener answering `302` to 169.254.169.254, and at a name that resolves to a blocked address.
