# MCP Compatibility Matrix

This document records how Voltaic conforms to each published MCP specification revision: which transports are supported, the status of every requirement area, the relaxations made deliberately to simplify integration, and the optional features not implemented. It is updated with every release.

- **Voltaic version:** 2.1.10
- **Last reviewed:** 2026-09-26. The v2.1.9 source was reviewed line by line against every MUST, MUST NOT, SHOULD, and SHOULD NOT requirement of each revision (four independent reviews: lifecycle and stream transports, Streamable HTTP and authorization, server features and data model, and the stateless revision); v2.1.10 resolves each finding, and each resolution is covered by a test that fails against v2.1.9 (or exercises a new setting). A review of v2.1.10 is pending.
- **Revisions covered:** `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, `2026-07-28`
- **Interoperability verified with:** MCP Inspector CLI 2.8.0, the official Python MCP SDK 2.2.0 (as client and server), Claude Code 2.1.281, the official A2A Python SDK 1.1.5

Legend: ✅ conforms · — requirement does not exist in that revision (not applicable) · ❌ optional feature not implemented

## 1. Transport support by revision

| Transport | 2024-11-05 | 2025-03-26 | 2025-06-18 | 2025-11-25 | 2026-07-28 |
|---|:-:|:-:|:-:|:-:|:-:|
| stdio server (`McpServer`) | ✅ | ✅ | ✅ | ✅ | ✅ |
| stdio client (`McpClient`) | ✅ | ✅ | ✅ | ✅ | ❌ ¹ |
| Streamable HTTP server (`McpHttpServer`) | ✅ ² | ✅ | ✅ | ✅ | ✅ stateless |
| Streamable HTTP client (`McpHttpClient`) | ✅ ² | ✅ | ✅ | ✅ | ✅ stateless |
| HTTP+SSE (the 2024-11-05 HTTP transport) | ❌ optional, deprecated | — | — | — | — |
| TCP, WebSocket servers (custom transports) | ✅ | ✅ | ✅ | ✅ | ✅ |
| TCP, WebSocket clients (`McpTcpClient`, `McpWebsocketsClient`) | ✅ | ✅ | ✅ | ✅ | ❌ ¹ |
| `/rpc` + `/events` (Voltaic-specific custom transport; `EnableLegacyEndpoints`) | ✅ | ✅ | ✅ | ✅ | ✅ |

¹ These clients speak the handshake revisions; a client chooses the revisions it supports. `McpHttpClient` speaks `2026-07-28`.
² 2024-11-05 clients are served over Streamable HTTP rather than the HTTP+SSE transport that revision defines.

## 2. Requirement status by revision

| Requirement | 2024-11-05 | 2025-03-26 | 2025-06-18 | 2025-11-25 | 2026-07-28 | Notes |
|---|:-:|:-:|:-:|:-:|:-:|---|
| JSON-RPC envelope (IDs string or integer, `params` object, results objects, UTF-8) | ✅ | ✅ | ✅ | ✅ | ✅ | The stdio client reads and writes UTF-8 whatever the console code page |
| Batching (2025-03-26 only; single message on 2026-07-28) | ✅ | ✅ | ✅ | ✅ | ✅ | On every transport, HTTP included: a `2026-07-28` request inside a batch is refused, and clients refuse server batches after 2025-03-26 |
| Lifecycle (initialize first and once, version negotiation, shutdown, request IDs, configurable timeouts) | ✅ | ✅ | ✅ | ✅ | — | stdio shutdown: close input, SIGTERM (Linux, macOS), kill; a request method sent without an `id` never runs; `InitializeTimeoutMs` and `InitializeAsync(timeoutMs)` |
| Ping (answer; issue periodically; timeouts are connection failures) | ✅ | ✅ | ✅ | ✅ | — ³ | `PingIntervalMs`, `PingTimeoutMs`, `PingFailureThreshold`; the stdio server stops even while a read is pending |
| Cancellation (races, reused IDs, nothing after cancellation, reasons logged) | ✅ | ✅ | ✅ | ✅ | ✅ | Servers and clients; a cancellation that follows its request always applies (IDs are reserved in message order), also for a reused 2026-07-28 ID; includes rejected and malformed requests and timed-out pings |
| Progress (increasing values, active tokens only, rate limiting) | ✅ | ✅ | ✅ | ✅ | ✅ | Servers send at most one update per `ProgressIntervalMs` (default 20 ms) per request, plus the final one; clients deliver progress only for tokens of requests in flight, at the same bound |
| Per-request `_meta`, statelessness, `resultType`, caching hints, `server/discover`, removed methods | — | — | — | — | ✅ | `_meta` with the client fields but no string version is `-32602` on every transport; every complete result of a cacheable method carries `ttlMs` and `cacheScope` |
| Multi Round-Trip Requests on `tools/call` (capabilities incl. `elicitation.url`, `sampling.tools`, `sampling.context`) | — | — | — | — | ✅ | An elicitation mode other than `form` or `url` is `-32603`; `McpHttpClient` answers input requests with its registered request handlers, matching the capabilities it declares |
| JSON Schema in the declared dialect (2020-12, draft-07), references, ECMA-262 patterns | — | — | — | ✅ | ✅ | Other dialects, malformed keyword values, and unresolvable references are rejected at registration |
| Tools, resources, prompts, completions, logging, pagination | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Output schemas and `structuredContent` shapes per revision | — | — | ✅ | ✅ | ✅ | Tool schemas are sent in each revision's shape (object output schemas and object property subschemas before 2026-07-28) |
| Change notifications (`list_changed` on registration, `resources/updated` to subscribers) | ✅ | ✅ | ✅ | ✅ | — ⁴ | Never ahead of the `initialize` response |
| Result and notification downgrade to the governing revision | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Client capabilities declared per revision | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Streamable HTTP (sessions, 404 recovery, 202 and error statuses, SSE, priming, resumability, Origin, version header) | — | ✅ | ✅ | ✅ | — | 2026-07-28 has no sessions, priming, or resumability; its HTTP rules are the stateless rows below |
| Stateless HTTP headers (`MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name`, `Mcp-Param-*`) and error codes | — | — | — | — | ✅ | `Mcp-Name` is `params.uri` for `resources/read` |
| Disconnect is cancellation (2026-07-28 HTTP) | — | — | — | — | ✅ ⁵ | |
| `-32021` (missing client capability) is HTTP 400 | — | — | — | — | ✅ ⁵ | |
| Insufficient scope is HTTP 403 with an `insufficient_scope` challenge | — | ✅ | ✅ | ✅ ⁵ | ✅ ⁵ | Also for batches |
| Authorization, resource server (401/403 challenges, `resource_metadata`, protected resource metadata at the resource's own URL) | — | ✅ ⁶ | ✅ | ✅ | ✅ | Validating access tokens (signature, expiry, audience) and answering invalid ones with 401 is done in the application's `AuthenticationHandler`; Voltaic runs it before every request and writes the challenge |
| Reserved error codes not emitted | ✅ | ✅ | ✅ | ✅ | ✅ | Insufficient scope uses 403 |

³ 2026-07-28 removed `ping`.
⁴ 2026-07-28 delivers change notifications through `subscriptions/listen`, an optional feature (section 5).
⁵ See section 3, "Status codes once a stream has started" and "Disconnect detection".
⁶ Authorization is optional in 2025-03-26. That revision makes the MCP server its own authorization server (metadata and the `/authorize`, `/token`, and `/register` endpoints); Voltaic provides the resource-server side, and the authorization server is the application's (section 5), as in later revisions.

## 3. Deliberate relaxations and disclosed behavior

These accept input the specification says a client should not send, are stricter than required, or describe behavior the transport cannot avoid (marked in bold; each is a configurable trade-off between two requirements). None weakens a security check.

| Item | Revisions | Rationale |
|---|---|---|
| A missing `Accept` header counts as `*/*`, and media ranges such as `application/*` match | 2025-03-26 to 2026-07-28 | Many HTTP clients omit it; no security impact |
| `POST /mcp` without `Content-Type` is accepted when there is no `Origin` header | 2025-03-26 to 2026-07-28 | Browser requests without it still get 415 |
| Stateless notification POSTs need no routing headers, and unknown notifications get 202 | 2026-07-28 | The revision defines no header rules for notifications; only `notifications/*` methods are notifications, and a request method without an `id` is rejected (400) and never runs |
| Tool arguments that fail the input schema produce an `isError` result on every revision | 2024-11-05 to 2025-06-18 | Those revisions allowed either this or `-32602`; the model can correct its arguments |
| The TCP server accepts newline-delimited JSON and Content-Length framing, and closes the connection on a line that is not JSON | Custom transport | Matches stdio framing; drops cross-protocol HTTP requests |
| JSON-RPC batches are accepted on 2024-11-05 sessions as well as 2025-03-26 | 2024-11-05 | JSON-RPC 2.0 allows batches; 2024-11-05 does not mention them |
| Invalid UTF-8 is decoded with replacement characters rather than rejected | All | The message is then validated as usual |
| An error response to a message whose ID cannot be read carries `"id": null` | All | JSON-RPC 2.0 requires it; the MCP schemas declare no form for such a response (2025-11-25 and later make `id` optional) |
| `/events` accepts `?session=`, and `/rpc` does not check `Accept` or `Content-Type` | Voltaic-specific endpoints | These endpoints are Voltaic's own; the Streamable HTTP endpoint enforces both |
| Unicode property escapes in `pattern` accept ECMA-262 general category names and .NET's own names (such as `\p{IsGreek}` blocks) | 2025-11-25, 2026-07-28 | .NET block names are accepted as written; every ECMA-262 construct keeps its meaning (see "Stricter than required" for the ones rejected) |
| A request carrying `MCP-Protocol-Version: 2026-07-28` is served statelessly even when it also carries a session ID | 2026-07-28 | The header selects the revision, which defines no sessions; the session plays no part in the request |
| Each session keeps its most recent `MaxResumableStreamsPerSession` streams (default 8) for `Last-Event-ID` resumption; older streams cannot be resumed | 2025-03-26 to 2025-11-25 | Resumption is optional; the limit bounds memory and is configurable |
| **Status codes once a stream has started:** a late `-32021` or insufficient-scope error is the final SSE event of a 200 response when the client asked for notifications and the handler sent one before failing, or when a 2026-07-28 handler ran silently for longer than `ResponseKeepAliveMs` (default 15 s) | 2025-11-25, 2026-07-28 | The status is sent with the first byte; the stream is what the client asked for, or is needed to notice a disconnect (next row). Every other response keeps its exact status |
| **Disconnect detection:** a 2026-07-28 client that closes its stream cancels the request at the handler's next write, or within `ResponseKeepAliveMs` (default 15 s) through keep-alives | 2026-07-28 | `HttpListener` detects a closed connection only by writing. Setting `ResponseKeepAliveMs = 0` keeps exact statuses for slow handlers instead, and a handler that writes nothing then runs to completion |
| **Stricter than required:** tools must have a description, and tool names must be 1-128 characters of letters, digits, `_`, `-`, and `.`; `RegisterTool` rejects an `x-mcp-header` integer whose schema bounds exceed the JavaScript safe range; `pattern` rejects the ECMA-262 constructs .NET cannot express (script properties such as `\p{Script=Greek}`, code points above U+FFFF inside a character class) and syntax ECMA-262 does not define that .NET would give its own meaning (`\A`, `\Z`, `\z`, `\G`, `\a`, `\e`, inline options, comments, atomic and conditional groups) | 2025-11-25, 2026-07-28 | The description is optional and the name rules are a SHOULD; enforcing them keeps tools portable. A rejected pattern fails at registration, never by validating differently. `McpHttpClient` applies only the specification's `x-mcp-header` rules to other servers' tools |

## 4. Documentation status

The README, XML documentation, and CHANGELOG match the behavior above as of this release. Earlier CHANGELOG entries describe the behavior of their own releases.

## 5. Optional features not implemented

| Feature | Revisions |
|---|---|
| HTTP+SSE transport, and client fallback to it | 2024-11-05 |
| `2026-07-28` in `McpClient`, `McpTcpClient`, and `McpWebsocketsClient` (use `McpHttpClient`) | 2026-07-28 |
| Server-to-client requests (sampling, elicitation, roots) on handshake-era sessions | 2024-11-05 to 2025-11-25 |
| Multi Round-Trip input for `resources/read` and `prompts/get` | 2026-07-28 |
| `subscriptions/listen` | 2026-07-28 |
| Tasks (the server does not run tools as tasks; models only) | 2025-11-25, 2026-07-28 |
| Authorization server (including the 2025-03-26 metadata and fallback endpoints) and client-side OAuth flow | 2025-03-26 to 2026-07-28 |
| HTTPS listening in `McpHttpServer` (terminate TLS in a proxy) | All HTTP |
