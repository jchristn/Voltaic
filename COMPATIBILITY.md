# MCP Compatibility Matrix

This document records how Voltaic conforms to each published MCP specification revision: which transports are supported, the status of every requirement area, the relaxations made deliberately to simplify integration, and the optional features not implemented. It is updated with every release.

- **Voltaic version:** 2.1.7
- **Last reviewed:** 2026-09-26. The v2.1.6 source was reviewed line by line against every MUST, MUST NOT, SHOULD, and SHOULD NOT requirement of each revision; v2.1.7 resolves each finding, and each resolution is covered by tests. A fresh review of v2.1.7 is pending.
- **Revisions covered:** `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, `2026-07-28`
- **Interoperability verified with:** MCP Inspector CLI 2.8.0, the official Python MCP SDK 2.2.0 (as client and server), Claude Code 2.1.281, the official A2A Python SDK 1.1.5

Legend: ✅ conforms · — requirement does not exist in that revision (not applicable) · ❌ optional feature not implemented

## 1. Transport support by revision

| Transport | 2024-11-05 | 2025-03-26 | 2025-06-18 | 2025-11-25 | 2026-07-28 |
|---|:-:|:-:|:-:|:-:|:-:|
| stdio (`McpServer` / `McpClient`) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Streamable HTTP (`McpHttpServer` / `McpHttpClient`) | ✅ ¹ | ✅ | ✅ | ✅ | ✅ stateless |
| HTTP+SSE (the 2024-11-05 HTTP transport) | ❌ optional, deprecated | — | — | — | — |
| TCP, WebSocket (custom transports) | ✅ | ✅ | ✅ | ✅ | ✅ |
| `/rpc` + `/events` (Voltaic-specific custom transport; `EnableLegacyEndpoints`) | ✅ | ✅ | ✅ | ✅ | ✅ |

¹ 2024-11-05 clients are served over Streamable HTTP rather than the HTTP+SSE transport that revision defines.

## 2. Requirement status by revision

Every row that was a violation in v2.1.6 (V1 to V15) is listed with its resolution, followed by the other requirement areas.

| # | Requirement | 2024-11-05 | 2025-03-26 | 2025-06-18 | 2025-11-25 | 2026-07-28 | How v2.1.7 conforms |
|---|---|:-:|:-:|:-:|:-:|:-:|---|
| V1 | Validate JSON Schema in its declared dialect; reject unsupported dialects | — | — | — | ✅ | ✅ | 2020-12 (default) and draft-07 are validated, including `unevaluatedProperties`, `unevaluatedItems`, `$anchor`, `$id`, `$dynamicRef`, and draft-07 `dependencies`; other dialects are rejected at registration |
| V2 | Handle cancellation races; a reused ID is never cancelled by a late notice | ✅ | ✅ | ✅ | ✅ | ✅ | Cancellations for recently finished requests are ignored; early ones still apply |
| V3 | Stateless requests do not depend on earlier requests on the connection | — | — | — | — | ✅ | A stateless request's notifications use its own revision |
| V4 | A closed stateless HTTP stream is treated as cancellation | — | — | — | — | ✅ | Any failed write cancels the handler immediately; keep-alives are optional |
| V5 | Start a new session after a 404 | — | ✅ | ✅ | ✅ | — | `McpHttpClient` recovers on requests, notifications, the GET stream, resumed streams, and answers to server requests |
| V6 | Custom transports keep the lifecycle | ✅ | ✅ | ✅ | ✅ | — | `/rpc` requires `initialize` first; `EnableLegacyEndpoints = false` removes `/rpc` and `/events` |
| V7 | Reject an unsupported `MCP-Protocol-Version` | — | — | ✅ | ✅ | — | The header must be a negotiable version (the cap applies) and, on a session, the negotiated one |
| V8 | `-32021` is HTTP 400; insufficient scope is 403 | — | — | — | ✅ | ✅ | Responses are unstreamed by default, so late errors keep their status; a stream exists only when the client asked for notifications |
| V9 | Challenges name the protected resource metadata URL | — | — | ✅ | ✅ | ✅ | Derived from the configured resource identifier (correct behind a proxy) and served at that path |
| V10 | Reject schemas with unresolved external `$ref` | — | — | — | — | ✅ | Rejected at registration and at validation |
| V11 | Do not use the reserved legacy error codes | ✅ | ✅ | ✅ | ✅ | ✅ | `McpInsufficientScopeException` uses 403, outside the JSON-RPC reserved range |
| V12 | stdio shutdown: close input, then SIGTERM, then SIGKILL | ✅ | ✅ | ✅ | ✅ | ✅ | SIGTERM is sent on Linux and macOS (Windows has none); waits are configurable |
| V13 | Periodic pings, with a configurable frequency | ✅ | ✅ | ✅ | ✅ | — ² | Clients and stdio, TCP, and WebSocket servers ping every `PingIntervalMs` |
| V14 | Sampling context only when the client declares `sampling.context` | — | — | — | ✅ | ✅ | Input requests with `includeContext` require it (`-32021`) |
| V15 | Declare only capabilities the revision defines | ✅ | ✅ | ✅ | ✅ | ✅ | `elicitation` is omitted when requesting 2024-11-05 or 2025-03-26 |
| — | JSON-RPC envelope (IDs string or integer, `params` object, results objects) | ✅ | ✅ | ✅ | ✅ | ✅ | |
| — | Batching (allowed on 2025-03-26, rejected from 2025-06-18; single message on 2026-07-28) | ✅ | ✅ | ✅ | ✅ | ✅ | |
| — | Lifecycle (initialize first and once, version negotiation) | ✅ | ✅ | ✅ | ✅ | — | |
| — | Per-request `_meta`, `resultType`, `server/discover`, removed methods | — | — | — | — | ✅ | |
| — | Cancellation and progress | ✅ | ✅ | ✅ | ✅ | ✅ | |
| — | Tools, resources, prompts, completions, logging, pagination | ✅ | ✅ | ✅ | ✅ | ✅ | |
| — | Change notifications (`list_changed` on registration, `resources/updated` to subscribers) | ✅ | ✅ | ✅ | ✅ | — ³ | |
| — | Result and notification downgrade to the governing revision | ✅ | ✅ | ✅ | ✅ | ✅ | |
| — | Streamable HTTP (sessions, 202, SSE, priming, resumability, Origin) | — | ✅ | ✅ | ✅ | ✅ | |
| — | `x-mcp-header` and routing headers | — | — | — | — | ✅ | |
| — | Multi Round-Trip Requests (`tools/call`) | — | — | — | — | ✅ | |
| — | Authorization (resource server: 401/403 challenges, protected resource metadata) | — | ✅ | ✅ | ✅ | ✅ | |

² 2026-07-28 removed `ping`.
³ 2026-07-28 delivers change notifications through `subscriptions/listen`, an optional feature (section 5).

## 3. Deliberate relaxations

These accept input the specification says a client should not send. None weakens a security check.

| Relaxation | Revisions | Rationale |
|---|---|---|
| A missing `Accept` header counts as `*/*`, and media ranges such as `application/*` match | 2025-03-26 to 2026-07-28 | Many HTTP clients omit it; no security impact |
| `POST /mcp` without `Content-Type` is accepted when there is no `Origin` header | 2025-03-26 to 2026-07-28 | Browser requests without it still get 415 |
| Stateless notification POSTs need no routing headers, and unknown notifications get 202 | 2026-07-28 | The revision defines no header rules for notifications |
| `McpHttpClient` treats a result without `resultType` as complete | 2026-07-28 | Works with servers that predate the field; unknown values are still rejected |
| Tool arguments that fail the input schema produce an `isError` result on every revision | 2024-11-05 to 2025-06-18 | Those revisions allowed either this or `-32602`; the model can correct its arguments |
| The TCP server accepts newline-delimited JSON and Content-Length framing, and closes the connection on a line that is not JSON | Custom transport | Matches stdio framing; drops cross-protocol HTTP requests |
| JSON-RPC batches are accepted on 2024-11-05 sessions as well as 2025-03-26 | 2024-11-05 | JSON-RPC 2.0 allows batches; 2024-11-05 does not mention them |
| Invalid UTF-8 is decoded with replacement characters rather than rejected | All | The message is then validated as usual |
| `/events` accepts `?session=`, and `/rpc` does not check `Accept` or `Content-Type` | Voltaic-specific endpoints | These endpoints are Voltaic's own; the Streamable HTTP endpoint enforces both |

## 4. Documentation status

The README, XML documentation, and CHANGELOG match the behavior above. No known inaccuracies.

## 5. Optional features not implemented

| Feature | Revisions |
|---|---|
| HTTP+SSE transport, and client fallback to it | 2024-11-05 |
| Server-to-client requests (sampling, elicitation, roots) on handshake-era sessions | 2024-11-05 to 2025-11-25 |
| Multi Round-Trip input for `resources/read` and `prompts/get` | 2026-07-28 |
| `subscriptions/listen` | 2026-07-28 |
| Tasks (the server does not run tools as tasks; models only) | 2025-11-25, 2026-07-28 |
| Client-side OAuth flow (`McpHttpClient` does not act on `WWW-Authenticate`) | 2025-03-26 to 2026-07-28 |
| HTTPS listening in `McpHttpServer` (terminate TLS in a proxy) | All HTTP |
