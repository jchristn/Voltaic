# Bug: `McpHttpServer` creates handshake sessions for requests that never initialized

**Status:** Fixed in Voltaic 2.1.0 (see `CHANGELOG.md` and the `McpHttp.Sessions` Touchstone suite).
**Affects:** Voltaic 2.0.0; also present in 1.1.0.
**Component:** `src/Voltaic/Mcp/McpHttpServer.cs` — `HandleMcpRequestAsync` (the `/mcp` Streamable HTTP endpoint) and `HandleRpcRequestAsync` (the `/rpc` JSON-RPC endpoint).
**Found by:** a downstream MCP test suite (a case asserting that `initialize` rejects an unknown version), then reproduced directly against Voltaic 2.0.0.

## Summary

On the handshake-era path (protocol versions `2024-11-05` through `2025-11-25`), `McpHttpServer` registers a session **before** it looks at the request. It then echoes that session ID back in `MCP-Session-Id`. So a session gets created, registered in `_Sessions`, raised through `ClientConnected`, and made usable in each of these cases:

1. **A rejected `initialize`.** An `initialize` that fails with JSON-RPC `-32602` (for example an unsupported protocol version) still returns an `MCP-Session-Id`. That session works for later requests.
2. **Any non-`initialize` request with no session.** A bare `tools/list` with no `MCP-Session-Id` and no prior `initialize` gets a fresh session and a normal result.
3. **An unknown, client-chosen session ID.** A request carrying an `MCP-Session-Id` the server never issued is not rejected. The server **adopts that exact ID** as a new session and serves the request.

The stateless `2026-07-28` path is not affected. It uses a throwaway `ClientConnection("stateless")` and never registers or echoes a session.

## Why it matters

- **Spec conformance (Streamable HTTP, 2025-03-26 and later).** A server issues the session ID in the response to a *successful* `initialize`. Servers that require sessions SHOULD answer requests without an `MCP-Session-Id` (other than `initialize`) with **400 Bad Request**. Requests carrying a session ID the server does not recognize are answered with **404 Not Found**, which is also the signal that tells a client to re-initialize. Voltaic currently does neither.
- **The negotiated version is skipped.** A session that never completed `initialize` has no entry in `_SessionVersions`. Version-keyed policy (batching gates, header policy) then falls back to headers or defaults for a session that never negotiated anything.
- **Unbounded session creation.** Each sessionless POST allocates a `ClientConnection` (and its queue) and fires `ClientConnected`. The idle sweep (`CleanupSessionsLoop`, `SessionTimeoutSeconds` = 300 by default) eventually reclaims them, but inside that window any caller can make the server allocate sessions at request rate. On a server without an `AuthenticationHandler`, that means anyone who can reach the port.
- **Client-chosen session IDs.** Adopting an ID the client made up means session IDs are no longer guaranteed to be server-generated random values. Hosts that key per-session state, logs, or authorization decisions on the session ID should be able to trust that it came from the server.
- **Misleading events.** Hosts that subscribe to `ClientConnected` see "connections" for traffic that never initialized, including failed handshakes.

## Reproduction (Voltaic 2.0.0)

Any `McpHttpServer` with default endpoints. Below, a Voltaic 2.0.0 MCP server is listening on `127.0.0.1:18190`, with `H` set to `-H "Content-Type: application/json" -H "Accept: application/json, text/event-stream"`.

```bash
# 1. initialize with an unsupported version -> error, but a session is issued
curl -si $H -X POST http://127.0.0.1:18190/mcp \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01","capabilities":{},"clientInfo":{"name":"repro","version":"1"}}}'
# HTTP/1.1 200 OK
# MCP-Session-Id: bbd893cf-dd97-4bbc-9c74-06f50fef5b32
# {"jsonrpc":"2.0","error":{"code":-32602,"message":"Unsupported MCP protocol version '1999-01-01'.",...},"id":1}

# 1b. that session is fully usable
curl -s $H -H "MCP-Session-Id: bbd893cf-..." -H "MCP-Protocol-Version: 2025-11-25" \
  -X POST http://127.0.0.1:18190/mcp -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
# HTTP 200, full tools/list result
# DELETE /mcp with that session id -> HTTP 200 (it is registered)

# 2. tools/list with no session and no initialize -> a new session is issued
curl -si $H -H "MCP-Protocol-Version: 2025-11-25" -X POST http://127.0.0.1:18190/mcp \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/list"}'
# HTTP 200, MCP-Session-Id: f82e5a5d-46be-4203-a64e-83531d6684cf

# 3. a made-up session id is adopted instead of rejected
curl -si $H -H "MCP-Session-Id: made-up-session-123" -H "MCP-Protocol-Version: 2025-11-25" \
  -X POST http://127.0.0.1:18190/mcp -d '{"jsonrpc":"2.0","id":4,"method":"tools/list"}'
# HTTP 200, MCP-Session-Id: made-up-session-123, full tools/list result
# DELETE /mcp with "made-up-session-123" -> HTTP 200 (registered)
# Control: DELETE /mcp with a never-used id "never-used-456" -> HTTP 404

# 4. /rpc behaves the same way for a rejected initialize
curl -si -H "Content-Type: application/json" -X POST http://127.0.0.1:18190/rpc \
  -d '{"jsonrpc":"2.0","id":5,"method":"initialize","params":{"protocolVersion":"1999-01-01","capabilities":{},"clientInfo":{"name":"repro","version":"1"}}}'
# HTTP 200, -32602 error body, MCP-Session-Id: ada06879-0824-41eb-adfa-6f64dcd2e2b7
```

The control in step 3 matters. `DELETE` on an ID the server never saw returns 404, so the 200s in steps 1b and 3 show those IDs really were registered.

## Root cause

`HandleMcpRequestAsync`, around line 1298:

```csharp
// Get or create session (handshake era)
string sessionId = GetSessionId(context) ?? Guid.NewGuid().ToString();
if (_TerminatedSessions.ContainsKey(sessionId)) { ... 404 ... }

bool isNewSession = !_Sessions.ContainsKey(sessionId);
ClientConnection connection = _Sessions.GetOrAdd(sessionId, (id) => new ClientConnection(id) { ... });
if (isNewSession && _Sessions.ContainsKey(sessionId)) RaiseClientConnected(connection);
...
JsonRpcResponse response = await ProcessRpcRequestAsync(connection, requestBody, token);
...
SetSessionIdHeaders(context.Response, sessionId);   // echoed whether or not initialize succeeded
```

`HandleRpcRequestAsync`, around line 1962, has the same unconditional `GetOrAdd` + `RaiseClientConnected`, and the same unconditional `SetSessionIdHeaders` on the way out.

Specifically:
- The only session-ID check is against `_TerminatedSessions`. An ID that was never issued falls through to `GetOrAdd`, which creates it.
- The session is created and the header written regardless of the method, and regardless of whether `initialize` produced a result or an error. `_SessionVersions` is correctly gated on `response.Result != null`, but session creation and the header are not.

## Suggested fix

### `/mcp` (Streamable HTTP), strict and spec-aligned

1. **Session ID present:**
   - Known (in `_Sessions`) → proceed as today.
   - In `_TerminatedSessions` → 404 (as today).
   - Otherwise (never issued, or expired and swept) → **404 Not Found** with a JSON-RPC error body. Do **not** create it. A client that sees 404 should re-initialize, which is also the correct recovery after the idle sweep removes a session.
2. **No session ID:**
   - `initialize` → process it on a **provisional** connection that is not in `_Sessions`. Only if the response has a `Result` (not an `Error`): generate the ID, add the connection to `_Sessions`, record `_SessionVersions`, raise `ClientConnected`, and write `MCP-Session-Id`. On error, write no header and register nothing.
   - A notification (no `id`) without a session → 400.
   - Any other request → **400 Bad Request** ("Missing MCP-Session-Id; send initialize first"). This matches what the GET/SSE branch already does ("Missing session ID. Send a POST to initialize first.").
3. **Batch bodies:** apply the same rule. A batch without a session must not create one, unless the batch contains a successful `initialize` in an era where batching is allowed.

### `/rpc` (compatibility JSON-RPC), keep it lenient without leaking

`/rpc` is documented as request/response JSON-RPC. Existing callers (test harnesses, scripts, `curl`) POST `tools/list` / `tools/call` there with no `initialize` and no session, and that has to keep working. Recommended:
- A request **with no session ID** (other than a successful `initialize`) runs on an ephemeral, unregistered connection, the way the stateless path does. No `_Sessions` entry, no `ClientConnected`, no `MCP-Session-Id` header.
- A request **with a session ID** is served only if the session is known. An unknown ID → 404, not adoption.
- `initialize` on `/rpc`: register a session and echo it only on success, same as `/mcp`.

If silently tightening `/mcp` is a concern for existing hosts, the strict 400/404 behavior could sit behind an option (for example `RequireInitializedSessions`, default `true` from the next major). Given 2.0.0 just shipped breaking changes, making it the default in 2.0.1 as a spec-conformance fix also seems defensible. That's your call.

### Related, low priority

`GetSessionId` also accepts the session ID from the `?session=` query string. Session IDs in URLs end up in access logs and proxies. Consider dropping that fallback, or limiting it to the GET/SSE path if it exists for `EventSource` clients that can't set headers.

## Tests to add (Voltaic `Test.Shared`)

Positive:
- A successful `initialize` on `/mcp` returns `MCP-Session-Id`. That ID works for `tools/list`, `DELETE` of it returns 200, and `ClientConnected` fires exactly once.
- A successful `initialize` on `/rpc` returns a session that works on `/rpc`.
- A sessionless `tools/list` / `tools/call` on `/rpc` still succeeds (compatibility).
- The stateless `2026-07-28` path still creates no session on either endpoint (regression guard).

Negative:
- `initialize` with an unknown version on `/mcp` and `/rpc` → `-32602`, **no** `MCP-Session-Id` header, `ClientConnected` not raised, and the server's session count is unchanged.
- `tools/list` on `/mcp` with no session → 400, no header, no session created.
- `tools/list` on `/mcp` with a made-up `MCP-Session-Id` → 404, no header echoed, and a follow-up `DELETE` of that ID → 404.
- The same made-up-ID request on `/rpc` → 404.
- A session removed by the idle sweep → later requests with that ID → 404 (the client must re-initialize).
- A sessionless notification on `/mcp` → 400.
