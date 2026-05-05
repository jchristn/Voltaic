# Changelog

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
- Fixed listener disposal in JsonRpcServer, McpHttpServer, and McpWebsocketsServer — now calls `Dispose()` instead of `Close()`/`Stop()`
- Fixed `TcpClient?.Close()` to `TcpClient?.Dispose()` in ClientConnection
- Removed resource disposal from `Disconnect()`/`Shutdown()` methods to prevent double-disposal — these methods now only manage connection state

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
