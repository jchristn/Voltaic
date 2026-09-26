# MCP Compatibility Matrix

This document records how Voltaic conforms to each published MCP specification revision: which transports are supported, the status of every requirement area, the relaxations made deliberately to simplify integration, and the optional features not implemented. It is updated with every release.

- **Voltaic version:** 2.1.8
- **Last reviewed:** 2026-09-26. The v2.1.7 source was reviewed line by line against every MUST, MUST NOT, SHOULD, and SHOULD NOT requirement of each revision (four independent reviews: lifecycle and stream transports, Streamable HTTP and authorization, server features and data model, and the stateless revision); v2.1.8 resolves each finding, and each resolution is covered by tests. A review of v2.1.8 is pending.
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
| Batching (2025-03-26 only; single message on 2026-07-28) | ✅ | ✅ | ✅ | ✅ | ✅ | A `2026-07-28` request inside a batch is refused |
| Lifecycle (initialize first and once, version negotiation, shutdown) | ✅ | ✅ | ✅ | ✅ | — | stdio shutdown: close input, SIGTERM (Linux, macOS), kill |
| Ping (answer; issue periodically; timeouts are connection failures) | ✅ | ✅ | ✅ | ✅ | — ³ | `PingIntervalMs`, `PingTimeoutMs`, `PingFailureThreshold` |
| Cancellation (races, reused IDs, nothing after cancellation, reasons logged) | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Progress | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Per-request `_meta`, statelessness, `resultType`, `server/discover`, removed methods | — | — | — | — | ✅ | |
| Multi Round-Trip Requests on `tools/call` (capabilities incl. `elicitation.url`, `sampling.tools`, `sampling.context`) | — | — | — | — | ✅ | |
| JSON Schema in the declared dialect (2020-12, draft-07), references, ECMA-262 patterns | — | — | — | ✅ | ✅ | Other dialects and unresolvable references are rejected at registration |
| Tools, resources, prompts, completions, logging, pagination | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Output schemas and `structuredContent` shapes per revision | — | — | ✅ | ✅ | ✅ | |
| Change notifications (`list_changed` on registration, `resources/updated` to subscribers) | ✅ | ✅ | ✅ | ✅ | — ⁴ | |
| Result and notification downgrade to the governing revision | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Client capabilities declared per revision | ✅ | ✅ | ✅ | ✅ | ✅ | |
| Streamable HTTP (sessions, 404 recovery, 202 and error statuses, SSE, priming, resumability, Origin, version header) | — | ✅ | ✅ | ✅ | ✅ | |
| Stateless HTTP headers (`MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name`, `Mcp-Param-*`) and error codes | — | — | — | — | ✅ | `Mcp-Name` is `params.uri` for `resources/read` |
| Disconnect is cancellation (2026-07-28 HTTP) | — | — | — | — | ✅ ⁵ | |
| `-32021` is HTTP 400; insufficient scope is HTTP 403 | — | — | — | ✅ ⁵ | ✅ ⁵ | Also for batches |
| Authorization, resource server (401/403 challenges, `resource_metadata`, protected resource metadata) | — | — ⁶ | ✅ | ✅ | ✅ | |
| Reserved error codes not emitted | ✅ | ✅ | ✅ | ✅ | ✅ | Insufficient scope uses 403 |

³ 2026-07-28 removed `ping`.
⁴ 2026-07-28 delivers change notifications through `subscriptions/listen`, an optional feature (section 5).
⁵ See section 3, "Status codes once a stream has started" and "Silent handlers".
⁶ 2025-03-26 authorization makes the MCP server its own authorization server (metadata and the `/authorize`, `/token`, and `/register` endpoints); Voltaic leaves the authorization server to the application.

## 3. Deliberate relaxations and disclosed behavior

These accept input the specification says a client should not send, or describe behavior the transport cannot avoid. None weakens a security check.

| Item | Revisions | Rationale |
|---|---|---|
| A missing `Accept` header counts as `*/*`, and media ranges such as `application/*` match | 2025-03-26 to 2026-07-28 | Many HTTP clients omit it; no security impact |
| `POST /mcp` without `Content-Type` is accepted when there is no `Origin` header | 2025-03-26 to 2026-07-28 | Browser requests without it still get 415 |
| Stateless notification POSTs need no routing headers, and unknown notifications get 202 | 2026-07-28 | The revision defines no header rules for notifications |
| `McpHttpClient` treats a result without `resultType` as complete | 2026-07-28 | Works with servers that predate the field; any other unrecognized value is rejected |
| Tool arguments that fail the input schema produce an `isError` result on every revision | 2024-11-05 to 2025-06-18 | Those revisions allowed either this or `-32602`; the model can correct its arguments |
| The TCP server accepts newline-delimited JSON and Content-Length framing, and closes the connection on a line that is not JSON | Custom transport | Matches stdio framing; drops cross-protocol HTTP requests |
| JSON-RPC batches are accepted on 2024-11-05 sessions as well as 2025-03-26 | 2024-11-05 | JSON-RPC 2.0 allows batches; 2024-11-05 does not mention them |
| Invalid UTF-8 is decoded with replacement characters rather than rejected | All | The message is then validated as usual |
| `/events` accepts `?session=`, and `/rpc` does not check `Accept` or `Content-Type` | Voltaic-specific endpoints | These endpoints are Voltaic's own; the Streamable HTTP endpoint enforces both |
| A `pattern` that .NET's ECMAScript mode cannot compile (such as `\p{...}`) runs with .NET semantics | 2025-11-25, 2026-07-28 | Such patterns would otherwise be unusable |
| **Status codes once a stream has started:** when the client asked for notifications and the handler sent one before failing, a late `-32021` or insufficient-scope error is the final SSE event of a 200 response | 2025-11-25, 2026-07-28 | The status is sent with the first byte; the stream is what the client asked for. Without that, responses keep their exact status (`ResponseKeepAliveMs = 0`, the default) |
| **Silent handlers:** a 2026-07-28 client that closes its stream cancels the request at the next write; a handler that writes nothing runs to completion | 2026-07-28 | `HttpListener` detects a closed connection only by writing; `ResponseKeepAliveMs` above 0 detects it sooner at the cost of the exact status |
| **Stricter than required:** tools must have a description, and tool names must be 1-128 characters of letters, digits, `_`, `-`, and `.` | 2025-11-25, 2026-07-28 | The description is optional and the name rules are a SHOULD; enforcing them keeps tools portable |

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
