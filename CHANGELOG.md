# Changelog

## v0.7.1
- Extended authenticated-caller propagation to the A2A servers, mirroring the MCP work in v0.7.0
- `A2ARequestContext` now exposes `Principal` and `Claims`, copied from the server's `AuthenticationHandler` result before the `IA2AAgentHandler` runs, so agents can authorize per-caller (scope reads, gate writes) using the identity that authenticated the request
- Populated over both A2A transports: `A2AHttpServer` (JSON-RPC and HTTP+JSON) and `A2AGrpcServer` (which delegates to the HTTP endpoint). Because the caller cannot be threaded through the shared public methods without changing their signatures, an internal request-scoped bridge carries it from each auth site to context construction, where it is copied onto the context so it survives the background handler task and streaming loop
- Additive and backward compatible: `Principal`/`Claims` are null when no `AuthenticationHandler` is configured and for public Agent Card requests; existing `IA2AAgentHandler` implementations are unaffected
- Added positive and negative Touchstone cases: the caller reaches the agent context over A2A HTTP and gRPC, and the context carries no identity when unauthenticated

## v0.7.0
- Added `Voltaic.Core.RpcCallContext`, an ambient (`AsyncLocal`), request-scoped context that carries the authenticated caller's `Principal` and read-only `Claims` into MCP/JSON-RPC method and tool handlers
- `McpHttpServer` now populates `RpcCallContext.Current` immediately after a successful `AuthenticationHandler` result and restores the prior value when the request ends (via an `IDisposable` scope), so the identity flows untouched down the awaited dispatch chain into `tool.Handler` without any handler-signature change
- `RpcCallContext.Current` is `null` when no `AuthenticationHandler` is configured, on transports that do not authenticate, and for requests that bypass authentication (for example `ping`); concurrent requests on independent async flows never observe one another's context
- Added optional explicit-context registration overloads on `JsonRpcServer`, `McpServer`, `McpHttpServer`, `McpTcpServer`, and `McpWebsocketsServer`: `RegisterMethod`/`RegisterTool` variants whose handler receives `Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>>` and forward `RpcCallContext.Current`
- Fully additive and backward compatible: all existing `RegisterMethod`/`RegisterTool` overloads compile and behave unchanged, and handlers that ignore the caller are unaffected
- Added the `McpHttp.Server.CallContext` Touchstone suite with positive and negative cases (context reflects the auth result inside a `tools/call` handler, is null without an `AuthenticationHandler`, is null on the pre-auth `ping` bypass, isolates concurrent callers, and flows through the explicit-context overload)

## v0.6.0
- Began multi-version MCP support spanning all five published protocol revisions: `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, and the stateless `2026-07-28`
- Added a version registry and era model (`McpProtocol` registry, `McpProtocolVersionInfo`, `McpProtocolEra`) that records each revision's era (handshake or stateless) and transport traits (sessions, batching, required protocol-version header, header routing)
- Added `McpProtocol.NewestProtocolVersion` (`2026-07-28`) alongside `LatestProtocolVersion`, which remains `2025-11-25` as the default handshake version so existing servers and clients are unaffected
- Added a deterministic version/era resolver (`McpVersionResolver`, `McpResolvedVersion`) that selects a revision from the `MCP-Protocol-Version` header, the request-body `_meta`, or structural cues, and rejects header/body disagreements
- Added stateless-era protocol error factories with the specification's codes: `HeaderMismatch` (`-32020`), `MissingRequiredClientCapability` (`-32021`), and `UnsupportedProtocolVersion` (`-32022`, carrying the supported-version list)
- Added additive protocol models: `server/discover` result (`McpDiscoverResult`), Multi Round-Trip Requests (`McpInputRequest`, `McpInputRequiredResult`), cacheable list/read results (`ttlMs`/`cacheScope`), the `2026-07-28` tasks extension (`McpTask`, `McpCreateTaskResult`, `McpUpdateTaskParams`, `McpTaskAck`, `McpTaskStatus`), and the `2025-11-25` experimental in-core tasks (`McpInCoreTask`, `McpCreateInCoreTaskResult`, `McpTaskAugmentation`, `McpListTasksResult`)
- Added `extensions` to client and server capability models for extension negotiation
- Implemented the stateless `2026-07-28` Streamable HTTP transport alongside the handshake transport, selected by the resolver: single POST endpoint, `Mcp-Method`/`Mcp-Name`/`Mcp-Param-*` routing-header validation (Base64 sentinel decoding), header/body version-match enforcement, `server/discover`, Multi Round-Trip Requests emission on `tools/call`, cacheable list/read results (`ttlMs`/`cacheScope`), notification `202`, and unknown-method `404`/`-32601`
- Added the stateless client surface to `McpHttpClient` (`ConnectStatelessAsync`, `DiscoverAsync`, `SendStatelessAsync`, `CallStatelessAsync<T>`, `CallToolStatelessAsync` with the MRTR retry loop, and `ClientName`/`ClientVersion`/`IsStateless`)
- Enforced version-gated JSON-RPC batching: batches are accepted for `2024-11-05` and `2025-03-26` and rejected (`-32600`) for `2025-06-18` and later, keyed on the session's negotiated version
- **Breaking (pre-1.0 ALPHA):** removed all `System.Text.Json` document-object-model types (`JsonElement`/`JsonDocument`/`JsonNode`/`JsonObject`/`JsonArray`) from the entire codebase. MCP/JSON-RPC handlers now receive `Voltaic.Core.RpcParameters` (raw JSON + `Deserialize<T>()` + `Utf8JsonReader`-based scalar accessors) instead of `JsonElement?`; the `JsonRpcServer` value tuple became `RpcMethodInvocation`; schema validation, A2A models/wire, and all response parsing were rewritten DOM-free. `var` and tuples remain zero across the codebase
- Added discrete positive and negative end-to-end Touchstone suites for every revision (`Mcp.Version.2024-11-05` … `Mcp.Version.2026-07-28`) plus `McpVersion.Resolver`/`Models`/`Discovery`/`Stateless`/`StatelessClient`, covering negotiation, core operations, batching policy, routing/header validation, MRTR, and discovery, through the console, xUnit, and NUnit runners
- Green on `net8.0` and `net10.0` across all three runners

## v0.5.1
- Documentation fixes; no code changes from v0.5.0
- Corrected the README to reference the current version (v0.5.1) instead of v0.4.0

## v0.5.0
- Added `McpHttpClient.SetRequestHeader(name, value)` so HTTP and Streamable-HTTP MCP clients can attach authentication headers — a bearer `Authorization` header or a custom API-key header (for example `X-API-Key`) — applied to every request the client sends, including the connection handshake (ping) and the SSE GET stream
- Passing a null or empty value removes a previously set header; header names are matched case-insensitively
- Additive and backward compatible: clients that set no header behave exactly as before
- Added `McpHttp.Client.Auth` Touchstone suite proving configured headers reach the server (verified through the server's `AuthenticationHandler`) and that removed or omitted headers stay off the request

## v0.4.0
- Breaking pre-1.0 namespace change: moved shared JSON-RPC/core APIs to `Voltaic.Core` and MCP APIs to `Voltaic.Mcp`
- Added `Voltaic.A2A` for A2A v1.0 Agent Card models, task/message/artifact models, security declarations, push notification config models, and protocol errors
- Added dependency-light `A2AClient` for JSON-RPC over HTTP with `A2A-Version: 1.0` and SSE streaming support
- Added dependency-light `A2AHttpJsonClient` for the A2A HTTP+JSON binding without depending on ASP.NET Core or `System.Net.ServerSentEvents`
- Added dependency-light `A2AGrpcClient` and Watson-backed `A2AGrpcServer` for A2A gRPC over HTTP/2 without ASP.NET Core
- Added `A2ACardResolver` for `/.well-known/agent-card.json` discovery
- Added `A2AHttpServer` on `HttpListener` with public and extended Agent Cards, JSON-RPC, HTTP+JSON REST routes, SSE streaming, task projection, in-memory task storage, live task subscriptions, CORS, optional auth, push notification config storage, and return-immediately handling
- Added `IA2AAgentHandler`, `A2AAgentEventQueue`, `A2ATaskUpdater`, `IA2ATaskStore`, and `InMemoryA2ATaskStore` for direct-style agent implementation
- Added official `a2a-dotnet` compatibility coverage based on inspected source commit `8fe65cfaa65a72b2d63bc9bef2e2d32fddc12a18`, including JSON-RPC method/envelope/header checks, HTTP+JSON route/body checks, official-style server request acceptance, and SSE parsing
- Added A2A Touchstone suites covering serialization, Agent Card discovery, JSON-RPC, HTTP+JSON, gRPC, streaming, task lifecycle, push notification config CRUD, extended Agent Cards, return-immediately behavior, and compatibility oracle checks
- Added `Sample.A2AServer`, `Test.A2AServer`, and `Test.A2AClient`
- Reorganized library source under `src/Voltaic/Core`, `src/Voltaic/Mcp`, and `src/Voltaic/A2A`, including A2A protobuf definitions under `src/Voltaic/A2A/Protos`
- Updated README, package metadata, API coverage documentation, and source-layout tests for the new namespace layout and A2A support

## v0.3.0
- Updated package version and MCP default protocol version to `2025-11-25`, while retaining `2025-03-26` negotiation support
- Added shared MCP endpoint infrastructure for tools, resources, prompts, capability reporting, pagination, and protocol validation errors
- Expanded `ToolDefinition` metadata and added `McpToolCallResult` support for structured content and full tool-call result returns
- Added resource models and server registration APIs for static resources, resource templates, `resources/list`, `resources/templates/list`, and `resources/read`
- Added prompt models and server registration APIs for `prompts/list`, `prompts/get`, and required prompt argument validation
- Added completion provider APIs and `completion/complete` handling for prompt and resource completions
- Added MCP utility models and handlers for `logging/setLevel`, `notifications/cancelled`, `notifications/progress`, and `notifications/message`
- Added lightweight JSON Schema validation for common tool input and structured-output schema cases
- Tightened Streamable HTTP behavior for required `Accept` headers, `MCP-Protocol-Version`, notification `202 Accepted` responses, and terminated-session `404` responses
- Added MCP resource/prompt/tool notification helpers for HTTP, TCP, and WebSocket transports where server-to-client notifications are available
- Added Touchstone-based shared test descriptors plus console, xUnit, and NUnit runners under `src/`
- Expanded `Test.Shared` into a 253-case matrix covering public API validation, JSON-RPC TCP integration, Streamable HTTP behavior, MCP registry operations, model serialization, framing edge cases, client connection lifecycle, auth/CORS/session behavior, stdio subprocess integration, and TCP/WebSocket MCP parity
- Added protocol-level HTTP tests covering initialize, unsupported versions, tools, resources, templates, prompts, SSE notification delivery, and JSON result export
- Updated `Sample.McpServer` and README with structured-output, resource, template, prompt, Streamable HTTP, authentication, and Touchstone testing examples

## v0.2.0
- Fixed Streamable HTTP `/mcp` and legacy `/events` SSE connections so they emit an immediate `: connected` prelude and flush as soon as the stream is established
- Fixed idle SSE heartbeat behavior by restoring keep-alive comments (`: keep-alive`) when no notifications are queued
- Added raw `HttpClient` regression tests that verify immediate SSE liveness and keep-alive delivery on the wire
- Breaking change: `ClientConnection.DequeueAsync(CancellationToken)` now throws `OperationCanceledException` when cancelled instead of returning `null`
- Clarified Streamable HTTP client usage and endpoint documentation in the README

## v0.1.11
- Fixed IDisposable implementation across all 9 disposable classes to follow the full Dispose pattern
- All classes now implement `protected virtual void Dispose(bool disposing)` with `GC.SuppressFinalize(this)`
- Added `_IsDisposed` guard flags to prevent double-disposal in all classes
- Fixed double-disposal bugs in JsonRpcClient, McpClient, and McpWebsocketsClient where `Disconnect()`/`Shutdown()` previously disposed resources that `Dispose()` also disposed
- Fixed listener disposal in JsonRpcServer, McpHttpServer, and McpWebsocketsServer; now calls `Dispose()` instead of `Close()`/`Stop()`
- Fixed `TcpClient?.Close()` to `TcpClient?.Dispose()` in ClientConnection
- Removed resource disposal from `Disconnect()`/`Shutdown()` methods to prevent double-disposal; these methods now only manage connection state

## v0.1.10
- Added `AuthenticationHandler` property to `McpHttpServer` for optional async request authentication
- Added `AuthenticationResult` class with `IsAuthenticated`, `Principal`, `Claims`, `StatusCode`, and `ErrorMessage` properties
- Health check (`/`) and `ping` JSON-RPC method bypass authentication to allow connectivity validation without credentials
- CORS preflight (`OPTIONS`) requests bypass authentication
- When `AuthenticationHandler` is not set, behavior is unchanged from previous versions

## v0.1.x
- Initial release

## Previous Versions

Notes from previous versions will be pasted here.
