# Changelog

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
