# MCP Compatibility Matrix

This document records how Voltaic conforms to each published MCP specification revision: which transports are supported, the status of every requirement area, the relaxations made deliberately to simplify integration, and the optional features not implemented. It is updated with every release.

- **Voltaic version:** 2.1.13
- **Last reviewed:** 2026-09-26. The v2.1.12 source was reviewed line by line against every MUST, MUST NOT, SHOULD, and SHOULD NOT requirement of each revision (four independent reviews: lifecycle and stream transports, Streamable HTTP and authorization, server features and data model, and the stateless revision); v2.1.13 resolves each finding, and each resolution is covered by a test. The unreleased v2.1.13 was then reviewed the same way, and its findings are resolved in this release too.
- **Revisions covered:** `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, `2026-07-28`
- **Interoperability verified with:** MCP Inspector CLI 2.8.0, the official Python MCP SDK 2.2.0 (as client and server; as client on each handshake revision, `2024-11-05` through `2025-11-25`, over HTTP and stdio, and on `2026-07-28`, including `subscriptions/listen`), Claude Code 2.1.281, the official A2A Python SDK 1.1.5

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

The last column names the test suites that verify each row; every suite runs on .NET 8 and .NET 10 in each release. `Mcp.RevisionMatrix` runs the same core scenario for every revision on every transport (stdio, TCP, WebSocket, and Streamable HTTP): negotiation, ping, the revision's shape of tool listings and results, invalid arguments, unknown methods, resources, prompts, log levels, progress, cancellation, and batching for the handshake revisions, and discovery, result types, caching hints, the `_meta` rule, removed methods, and cancellation for `2026-07-28`.

| Requirement | 2024-11-05 | 2025-03-26 | 2025-06-18 | 2025-11-25 | 2026-07-28 | Notes | Verified by |
|---|:-:|:-:|:-:|:-:|:-:|---|---|
| JSON-RPC envelope (IDs string or integer, `params` object, results objects, UTF-8) | ✅ | ✅ | ✅ | ✅ | ✅ | The stdio client reads and writes UTF-8 whatever the console code page | `Mcp.RevisionMatrix`, `McpStreams.Strict`, `McpStreams.Rigor`, `MessageFraming.Edge`, `Mcp.Limits`, `Mcp.Closeout` |
| Batching (2025-03-26 only; single message on 2026-07-28) | ✅ | ✅ | ✅ | ✅ | ✅ | On every transport, HTTP included: a `2026-07-28` request inside a batch is refused, and clients refuse server batches after 2025-03-26 and answer empty batches and invalid elements with `-32600` | `Mcp.RevisionMatrix`, `Mcp.SpecConformance`, `McpStreams.Conformance`, `Mcp.Settle`, `Mcp.Closeout` |
| Lifecycle (initialize first and once, version negotiation, shutdown, request IDs, configurable timeouts) | ✅ | ✅ | ✅ | ✅ | — | stdio shutdown: close input, SIGTERM (Linux, macOS), kill; a request method sent without an `id` never runs; `InitializeTimeoutMs` and `InitializeAsync(timeoutMs)` | `Mcp.RevisionMatrix`, `McpStreams.Conformance`, `McpHttp.Server.Negotiation`, `McpStdio.Integration` |
| Ping (answer; issue periodically; timeouts are connection failures) | ✅ | ✅ | ✅ | ✅ | — ³ | `PingIntervalMs`, `PingTimeoutMs`, `PingFailureThreshold`; the stdio server stops even while a read is pending | `Mcp.RevisionMatrix`, `McpClients.ServerRequests`, `McpStreams.Rigor` |
| Cancellation (races, reused IDs, nothing after cancellation, reasons logged) | ✅ | ✅ | ✅ | ✅ | ✅ | Servers and clients; a cancellation that follows its request always applies (IDs are reserved in message order), also for a reused 2026-07-28 ID and for batch elements whose handlers had not finished (refused elements count as unfinished until the batch response is assembled); a cancellation for an unknown or finished ID is ignored; timed-out pings are cancelled | `Mcp.RevisionMatrix`, `Mcp.Settle`, `McpStreams.EdgeCases`, `McpStreams.Rigor` |
| Progress (increasing values, active tokens only, rate limiting) | ✅ | ✅ | ✅ | ✅ | ✅ | Servers send at most one update per `ProgressIntervalMs` (default 20 ms) per request; clients deliver progress only for tokens of requests in flight, at the same bound; the latest held-back update goes out just before the response | `Mcp.RevisionMatrix`, `McpStreams.Notifications`, `McpStreams.EdgeCases` |
| Per-request `_meta`, statelessness, `resultType`, caching hints, `server/discover`, removed methods | — | — | — | — | ✅ | a request whose `_meta` has the client fields but no string version is `-32602` on stdio, TCP, and WebSocket (on HTTP the version header decides the revision); every complete result of a cacheable method carries `ttlMs` and `cacheScope`; a handler's `resultType` must be a supported type; application errors get their required data, and reserved codes become `-32603` | `Mcp.RevisionMatrix`, `McpVersion.StatelessResults`, `McpVersion.Stateless`, `McpStreams.Stateless`, `Mcp.Closeout` |
| Multi Round-Trip Requests on `tools/call` (capabilities incl. `elicitation.url`, `sampling.tools`, `sampling.context`) | — | — | — | — | ✅ | An elicitation mode other than `form` or `url` is `-32603`; recognizable `inputResponses` values are validated against their result schemas (`-32602`) and unrecognized ones ignored; only `tools/call`, `resources/read`, and `prompts/get` may return an input-required result, which must carry `inputRequests` or `requestState`; `McpHttpClient` answers input requests with its registered request handlers, matching the capabilities it declares | `McpStreams.Closure`, `McpStreams.Compliance`, `McpVersion.StatelessClient`, `Mcp.Settle`, `Mcp.Closeout` |
| JSON Schema in the declared dialect (2020-12, draft-07, per embedded resource), references, ECMA-262 patterns (code points, `u` flag), exact numbers | — | — | — | ✅ | ✅ | Other dialects, malformed keyword values, and unresolvable references are rejected at registration | `Mcp.SchemaKeywords`, `Mcp.SchemaDialectsExact`, `Mcp.SchemaRigor`, `Mcp.RegexEngine`, `Mcp.Closeout`, `Mcp.RegexModern` |
| Tools, resources, prompts, completions, logging, pagination, rate limiting | ✅ | ✅ | ✅ | ✅ | ✅ | `RateLimits` limits tool calls, completions, and log messages per client: its principal and remote address across all its HTTP sessions and WebSocket connections, its address on TCP, the process on stdio | `Mcp.RevisionMatrix`, `McpHttp.Registry.Matrix`, `McpStreams.Features`, `Mcp.Settle` |
| Output schemas and `structuredContent` shapes per revision | — | — | ✅ | ✅ | ✅ | Tool schemas are sent in each revision's shape (object output schemas and object property subschemas before 2026-07-28) | `Mcp.RevisionMatrix`, `McpStreams.Features`, `McpStreams.EdgeCases` |
| Change notifications (`list_changed` on registration, `resources/updated` to subscribers) | ✅ | ✅ | ✅ | ✅ | ✅ ⁴ | Never ahead of the `initialize` response | `McpStreams.Notifications`, `McpStreams.Compliance`, `Mcp.Closeout` |
| Result and notification downgrade to the governing revision | ✅ | ✅ | ✅ | ✅ | ✅ | | `Mcp.RevisionMatrix`, `McpStreams.Compliance`, `McpHttp.Server.Negotiation` |
| Client capabilities declared per revision | ✅ | ✅ | ✅ | ✅ | ✅ | | `McpClients.ServerRequests`, `McpHttp.ClientConformance` |
| Streamable HTTP (sessions, 404 recovery, 202 and error statuses, SSE, priming, resumability, Origin, version header) | — | ✅ | ✅ | ✅ | — | 2026-07-28 has no sessions, priming, or resumability; its HTTP rules are the stateless rows below | `Mcp.RevisionMatrix`, `McpHttp.Sessions`, `McpHttp.SseResumability`, `McpHttp.Streamable.Matrix`, `Mcp.Closeout`, `Mcp.Limits` |
| Subscribe and notify (`subscriptions/listen`: acknowledgment first, only requested types, subscription ID on every message, graceful closure) | — | — | — | — | ✅ | Every server and transport; `McpHttpClient.ListenAsync` on the client side | `Mcp.Closeout` |
| Stateless HTTP headers (`MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name`, `Mcp-Param-*`) and error codes | — | — | — | — | ✅ | `Mcp-Name` is `params.uri` for `resources/read`; `Mcp-Param-*` numbers compare exactly | `Mcp.RevisionMatrix`, `Mcp.HeaderParameters`, `McpVersion.Resolver`, `Mcp.Settle`, `Mcp.Closeout` |
| Disconnect is cancellation (2026-07-28 HTTP) | — | — | — | — | ✅ ⁵ | | `McpHttp.Conformance`, `McpHttp.Rigor` |
| `-32021` (missing client capability) is HTTP 400 | — | — | — | — | ✅ ⁵ | | `McpHttp.Compliance`, `Mcp.Settle` |
| Insufficient scope is HTTP 403 with an `insufficient_scope` challenge | — | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | Also for batches | `Security.HttpServers`, `McpHttp.Server.CallContext` |
| Authorization, resource server (401/403 challenges, `resource_metadata`, protected resource metadata at the resource's own URL) | — | ✅ ⁶ | ✅ | ✅ | ✅ | Validating access tokens (signature, expiry, audience) and answering invalid ones with 401 is done in the application's `AuthenticationHandler`; Voltaic runs it before every request and writes the challenge | `Security.HttpServers`, `McpHttp.Client.Auth`, `McpHttp.Server.AuthParity` |
| Reserved error codes not emitted | ✅ | ✅ | ✅ | ✅ | ✅ | Insufficient scope uses 403; under 2026-07-28, reserved codes an application raises become `-32603` | `McpStreams.Features`, `McpHttp.Sessions` |

³ 2026-07-28 removed `ping`.
⁴ 2026-07-28 delivers change notifications through `subscriptions/listen` (next rows).
⁵ See section 3, "Status codes once a stream has started" and "Disconnect detection".
⁶ Authorization is optional in 2025-03-26. That revision makes the MCP server its own authorization server (metadata and the `/authorize`, `/token`, and `/register` endpoints); Voltaic provides the resource-server side, and the authorization server is the application's (section 5), as in later revisions.

## 3. Deliberate relaxations and disclosed behavior

These accept input the specification says a client should not send, are stricter than required, or describe behavior the transport cannot avoid (marked in bold; each is a trade-off between two requirements that the transport cannot meet together). None weakens a security check.

| Item | Revisions | Rationale |
|---|---|---|
| A missing `Accept` header counts as `*/*`, and media ranges such as `application/*` match | 2025-03-26 to 2026-07-28 | Many HTTP clients omit it; no security impact |
| `POST /mcp` without `Content-Type` is accepted when there is no `Origin` header | 2025-03-26 to 2026-07-28 | Browser requests without it still get 415 |
| Stateless notification POSTs need no routing headers, and unknown notifications get 202 | 2026-07-28 | The revision defines no header rules for notifications; only `notifications/*` methods are notifications, and a request method without an `id` is rejected (400) and never runs |
| Tool arguments that fail the input schema produce an `isError` result on every revision | 2024-11-05 to 2025-06-18 | Those revisions allowed either this or `-32602`; the model can correct its arguments |
| The TCP server accepts newline-delimited JSON and Content-Length framing, and closes the connection on a line that is not JSON | Custom transport | Matches stdio framing; drops cross-protocol HTTP requests |
| JSON-RPC batches are accepted on 2024-11-05 sessions as well as 2025-03-26 | 2024-11-05 | JSON-RPC 2.0 allows batches; 2024-11-05 does not mention them |
| Invalid UTF-8 is decoded with replacement characters rather than rejected | All | The message is then validated as usual |
| An error response to a message whose ID cannot be read, or is not a string or integer, carries `"id": null` before 2025-11-25 and omits `id` from 2025-11-25 | All | JSON-RPC 2.0 requires null, which the 2024-11-05 to 2025-06-18 schemas do not describe; 2025-11-25 and later make `id` optional and do not allow null |
| `/events` accepts `?session=`, and `/rpc` does not check `Accept` or `Content-Type` | Voltaic-specific endpoints | These endpoints are Voltaic's own; the Streamable HTTP endpoint enforces both |
| `pattern` also accepts three Annex B forms: identity escapes of non-alphanumeric characters (`\_`), a literal `{`, `}`, or `]` that does not form a quantifier, and a class escape at a range end (`[\w-.]`) | 2025-11-25, 2026-07-28 | Common and unambiguous; every pattern valid with the `u` flag keeps its exact meaning |
| `pattern` general categories and group-name characters use the running .NET version's Unicode data (Unicode 15.0 on .NET 8, 16.0 on .NET 10); case folding for the `i` modifier is Unicode 16.0 on both | 2025-11-25, 2026-07-28 | ECMA-262 follows the latest Unicode version; the .NET 10 build matches it |
| Schema evaluation has a step and depth budget, and all the `pattern` matches of one value share a step budget (10,000,000 steps, plus 200 per character of the value, plus 4 per character squared up to 200,000,000); exceeding either rejects the value | 2025-11-25, 2026-07-28 | Bounds the work a hostile schema or input can cause: the most expensive patterns measured used up the budget in about 3 seconds on a 1 MB value (up to about 11 seconds for case-insensitive Unicode classes over supplementary characters). Simple patterns always fit, and quadratic work on values up to about 7,000 characters |
| A request carrying `MCP-Protocol-Version: 2026-07-28` is served statelessly even when it also carries a session ID | 2026-07-28 | The header selects the revision, which defines no sessions; the session plays no part in the request |
| Each session keeps its most recent `MaxResumableStreamsPerSession` streams (default 8) for `Last-Event-ID` resumption; older streams cannot be resumed | 2025-03-26 to 2025-11-25 | Resumption is optional; the limit bounds memory and is configurable |
| **Status codes once a stream has started:** a late `-32021` or insufficient-scope error is the final SSE event of a 200 response when the handler sent a notification on the response stream before failing (progress for the request's token, or a log message the client's log level allows; every level passes on a handshake-era session until the client sets one), or when a 2026-07-28 handler ran silently for longer than `ResponseKeepAliveMs` (default 15 s) | 2025-03-26 to 2026-07-28 (`-32021`: 2026-07-28 only) | The status is sent with the first byte; the stream is what the client asked for, or is needed to notice a disconnect (next row). Every other response keeps its exact status |
| Every transport limits one received message (`MaxMessageSize`: 16 MiB on stdio, TCP, and HTTP, 1 MB on WebSocket); `McpHttpServer` limits sessions (`MaxSessions`, 10,000, and `MaxSessionsPerClient`, 100); messages may nest up to 256 levels | All | Bounds the memory a peer can claim; every limit is configurable. An oversized newline-delimited message gets `-32600`, an HTTP body 413, a WebSocket message close 1009 |
| **Disconnect detection:** a 2026-07-28 client that closes its stream cancels the request at the handler's next write, or within `ResponseKeepAliveMs` (default 15 s) through keep-alives | 2026-07-28 | `HttpListener` detects a closed connection only by writing. Setting `ResponseKeepAliveMs = 0` keeps exact statuses for slow handlers instead, and a handler that writes nothing then runs to completion |
| **Stricter than required:** tools must have a description, and tool names must be 1-128 characters of letters, digits, `_`, `-`, and `.`; `RegisterTool` rejects an `x-mcp-header` integer whose schema bounds exceed the JavaScript safe range; `pattern` rejects Unicode properties other than general categories, `Any`, `ASCII`, and `Assigned` (script and binary properties such as `\p{Script=Greek}` or `\p{Alphabetic}`) and groups nested more than 500 deep; a number whose written exponent has more than 1,000 digits fails validation, and a schema bound with one, or a `multipleOf` with more than 1,000 significant digits, is rejected at registration; a tool argument string or property name with an unpaired UTF-16 surrogate (not valid Unicode; I-JSON forbids it) fails input validation wherever it appears, and such a string in other parameters is `-32602` | All | The description is optional and the name rules are a SHOULD; enforcing them keeps tools portable. A rejected pattern fails at registration, never by validating differently. `McpHttpClient` applies only the specification's `x-mcp-header` rules to other servers' tools |

## 4. Documentation status

The README, XML documentation, and CHANGELOG match the behavior above as of this release. Earlier CHANGELOG entries describe the behavior of their own releases.

## 5. Optional features not implemented

| Feature | Revisions |
|---|---|
| HTTP+SSE transport, and client fallback to it | 2024-11-05 |
| `2026-07-28` in `McpClient`, `McpTcpClient`, and `McpWebsocketsClient` (use `McpHttpClient`) | 2026-07-28 |
| Server-to-client requests (sampling, elicitation, roots) on handshake-era sessions | 2024-11-05 to 2025-11-25 |
| Multi Round-Trip input for `resources/read` and `prompts/get` | 2026-07-28, `Mcp.Closeout` |
| Tasks (the server does not run tools as tasks; models only) | 2025-11-25, 2026-07-28 |
| Authorization server (including the 2025-03-26 metadata and fallback endpoints) and client-side OAuth flow | 2025-03-26 to 2026-07-28 |
| HTTPS listening in `McpHttpServer` (terminate TLS in a proxy) | All HTTP |
