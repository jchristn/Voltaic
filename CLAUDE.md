# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Voltaic is a JSON-RPC 2.0, Model Context Protocol (MCP), and Agent2Agent (A2A) implementation for .NET 8.0 and .NET 10.0. The library provides TCP JSON-RPC, MCP stdio/Streamable HTTP/TCP/WebSocket transports, and dependency-light A2A JSON-RPC/HTTP+JSON/gRPC/SSE hosting. Voltaic v2.1.5 recognizes five MCP protocol revisions (`2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, and the stateless `2026-07-28`) via the `McpProtocol` version registry and `McpVersionResolver`. `McpProtocol.LatestProtocolVersion` remains `2025-11-25` as the default handshake version; `McpProtocol.NewestProtocolVersion` is `2026-07-28`. `initialize` negotiates at most `McpProtocol.NewestHandshakeProtocolVersion` (`2025-11-25`, configurable per server with `MaximumHandshakeProtocolVersion`); `2026-07-28` is reached without a handshake: on `McpHttpServer` through the stateless HTTP path, and on stdio/TCP/WebSocket when a request's `_meta` protocol version names it (`McpMessageProcessor` resolves the version per request through `McpStatelessDispatcher`; `server/discover` is registered on every server). Stateless results carry `resultType` and cacheable results carry `ttlMs`/`cacheScope` (see `McpStatelessResultStamper`). Authenticated and unauthenticated `McpHttpServer` requests share one pipeline. A2A protocol version is `1.0`. The multi-version work is tracked in `archive/UPDATED_MCP_VERSION.md`.

Since v2.0.0, every MCP server always registers the MCP protocol methods (`RegisterProtocolMethods()`), including a protocol `ping` that returns `McpEmptyResult` (`{}`). The diagnostic tools `echo` and `getTime` are opt-in through the `includeDiagnosticTools` constructor parameter (default `false`; `RegisterDiagnosticTools()`), and `getSessions`/`getClients` no longer exist. `RegisterTool` registers a tool only in the `McpEndpoint` registry, so tools are reachable only through `tools/call` (never as bare JSON-RPC methods); `UnregisterTool` removes one. `McpSchemaValidator` enforces `additionalProperties` and `patternProperties`. `JsonRpcServer` registers its diagnostic methods (`ping`, `echo`, `getTime`, `add`) only with `includeDiagnosticMethods: true`. See `MIGRATE_V1_TO_V2.md` and `archive/DEFAULT_TOOL_FIX.md`.

Since v2.1.0 (security release), the HttpListener-based servers (`McpHttpServer`, `McpWebsocketsServer`, `A2AHttpServer`) share request gates in the internal `HttpAccessGuard`: loopback-only clients when bound to a loopback name (`RestrictToLoopbackClients`, via `LoopbackAddresses`), browser `Origin` validation (`OriginPolicy`; 403 before preflight and auth), CORS that echoes the allowed origin (never `*`), and auth-failure responses that write `AuthenticationResult.Headers` (`BearerChallenge` builds `WWW-Authenticate`). `McpHttpServer` creates handshake sessions only on a successful `initialize`: sessionless `POST /mcp` gets 400 (except `initialize`/`ping`), unknown/expired/terminated session IDs get 404 (`McpProtocolException.SessionNotFound()`, `-32001`) and are never adopted, sessionless `POST /rpc` runs on an unregistered connection, sessions are bound to the creating principal (`ClientConnection.Caller`), and `RequireInitializedSessions = false` restores issuance for ping-first clients. `POST /mcp` requires `application/json`. `McpWebsocketsServer` has an `AuthenticationHandler` on the upgrade request and pushes `ClientConnection.Caller` as `RpcCallContext` per message; `McpWebsocketsClient.SetRequestHeader` sends upgrade headers. `MessageFraming` accepts only `Content-Length`/`Content-Type` header lines. `McpHttpClient.ConnectAsync`/`ConnectStreamableAsync` perform `initialize` + `notifications/initialized`. Since v2.1.1: client-sent JSON-RPC responses get 202, header-less handshake requests assume `McpProtocol.HeaderlessProtocolVersion` (`2025-03-26`), stateless requests must carry the `_meta` protocol version (else 400 `-32020`), and `McpHttpServer.ProtectedResourceMetadata` is served unauthenticated at `/.well-known/oauth-protected-resource`. Deliberate spec differences are listed in the README "Specification conformance" section. Since v2.1.2: invalid tool arguments return an `isError` tool result (not `-32602`); `initialize` with an unknown version negotiates the cap; `A2AHttpServer` delivers push notifications (`PushNotificationUrlValidator` guards webhook URLs against SSRF, using `A2AWebhookAddressPolicy`); A2A JSON-RPC errors use HTTP 200; `A2AGrpcServer` has `OriginPolicy`/`RestrictToLoopbackClients` and authenticates the extended card. Every public member must have XML docs: `CS1591` is a build error in `Voltaic.csproj`. Test fixtures call `StartAsync` directly (not via `Task.Run`) so readiness probes never hit a closed port; a refused loopback connect costs about 2 s on Windows. Since v2.1.3: stdio/TCP/WebSocket serve `2026-07-28` per request (`McpStatelessDispatcher`); tool handlers see the Multi Round-Trip state through the ambient `McpToolCallContext` (`InputResponses`, `RequestState`, `IsRetry`, `CanRequestInput`), and an `McpInputRequiredResult` returned to a handshake-era caller becomes an `isError` result; `McpHttpClient` reads SSE responses to POST requests; A2A push configs are the flat v1.0 shape (`TaskPushNotificationConfigJsonConverter`, `CreateTaskPushNotificationConfigRequestJsonConverter`; legacy nested shapes and `configId` are still read); `A2AProtocol.ListTaskPushNotificationConfig` is `ListTaskPushNotificationConfigs` (the singular name is still accepted); HTTP+JSON errors are `google.rpc.Status` with an `ErrorInfo` reason (`A2ARestErrors`); `A2AGrpcServer` exposes the push settings, listens on `127.0.0.1` and `::1` for `localhost` (and `0.0.0.0` and `::` for `*`/`+`), and stops Watson through `Stop()` rather than a cancelled start token (which left the socket bound). Since v2.1.4: every client answers server requests through the internal `Core/ClientRequestDispatcher` (`ping` -> `{}` on MCP clients, `RegisterRequestHandler`, else `-32601`; `McpTcpClient` calls `AnswerPingRequests()`), with a send lock per client; `McpEndpoint.CallToolAsync` turns handler exceptions into `isError` results (except `McpProtocolException` and cancellation) with a generic text unless the exception is `McpToolException` or `IncludeToolExceptionMessages` is set (details go to `Log` via `McpEndpoint.ErrorLog`), and output-schema violations are `-32603`; `McpHttpServer` has no `ping` auth bypass, rejects sessionless handshake-era `ping` on `/mcp`, treats `RequireInitializedSessions` as an obsolete no-op, and makes `GET /mcp` resumable (`SseStreamLog`, `SseSessionStreams`, priming event, `Last-Event-ID` replay, `Claim` cancels the abandoned writer); `x-mcp-header` rules live in `McpHeaderParameters` (validated at `RegisterTool`, enforced by `ValidateParamHeaders` on stateless `tools/call`, mirrored and filtered by `McpHttpClient`); `McpHttpClient` parses SSE with `Core/SseEventReader` and reconnects/resumes streams. Since v2.1.5 (full MUST/SHOULD conformance): stdio, TCP, and WebSocket servers and the HTTP server share the internal `McpMessageProcessor` (envelope validation via `McpEnvelope`, initialize-first and initialize-once, batches only on `2025-03-26`, concurrent dispatch, `notifications/cancelled`, stateless `_meta` checks, `McpVersionCompatibility` downgrades) with per-connection `McpSessionState` in `ClientConnection.ProtocolState` (negotiated version, capabilities, log level, subscriptions, in-flight `McpInFlightRequest`s); `McpRequestScope` is the ambient per-request state behind `McpToolCallContext.ReportProgressAsync`/`LogAsync`; `McpServerNotifications` fans out list_changed (initialized sessions), resources/updated (subscribers), log (level-filtered), and progress (in-flight token owner, increasing). `JsonRpcServer` has `private protected` `ProcessMessageAsync`/`OnClientConnected`/`OnClientDisconnected`/`WriteToClientAsync` hooks and `McpTcpServer` accepts newline-delimited JSON (`AcceptNewlineFraming`). `McpHttpServer` writes POST responses through `McpHttpResponseWriter` (JSON, or SSE once a notification is sent), cancels stateless requests when a keep-alive write fails (`ResponseKeepAliveMs`), always sends a `WWW-Authenticate` challenge on 401, maps `McpInsufficientScopeException`/`AuthenticationResult.InsufficientScope` to 403 `insufficient_scope`, and checks the session owner on DELETE. Stream clients auto-initialize (`McpClientHandshake`, `AutoInitialize`), send `notifications/cancelled`, and accept batches; `McpTcpClient.NewlineDelimited` defaults to true. The servers' `ProtocolVersion` property is obsolete.

## Solution Structure

- **Voltaic** (src/Voltaic/): Core library containing JSON-RPC 2.0, MCP, and A2A protocol implementation
  - `JsonRpcRequest.cs`: Request message model
  - `JsonRpcResponse.cs`: Response message model
  - `JsonRpcError.cs`: Error object with standard JSON-RPC error codes
  - `JsonRpcServer.cs`: TCP-based server implementation with JsonRpcServer class
  - `JsonRpcClient.cs`: TCP-based client implementation with JsonRpcClient class
  - `MessageFraming.cs`: LSP-style message framing utilities (internal static class)
- `McpEndpoint.cs`: Shared MCP dispatcher for tools, resources, prompts, completions, capabilities, schema validation, and utility methods
- `Mcp*Models.cs`: Typed MCP protocol models for capabilities, content, resources, prompts, completions, and utilities
- `A2A*`: A2A Agent Cards, models, JSON-RPC/HTTP+JSON/gRPC clients and servers, task store/projection, SSE helpers, protobuf wire helpers, and protocol errors
- **Test.Shared** (src/Test.Shared/): Touchstone descriptors and public API coverage matrix
- **Test.Automated** (src/Test.Automated/): Touchstone console runner
- **Test.Xunit** / **Test.Nunit**: Touchstone adapter projects for `dotnet test`
- **Sample.McpServer**: Copyable MCP sample with tools, resources, prompts, and completions
- **Sample.A2AServer**: Copyable A2A sample with Agent Card, JSON-RPC, HTTP+JSON, gRPC, streaming, push config, and extended card support
- **Test.JsonRpc***, **Test.Mcp***, **Test.A2AServer**, **Test.A2AClient**: Manual and transport-specific sample/test applications

Namespace layout:
- `Voltaic.Core`: JSON-RPC, TCP framing, connection, shared auth/error helpers
- `Voltaic.Mcp`: MCP clients, servers, protocol models, and endpoint helpers
- `Voltaic.A2A`: A2A clients, servers, protocol models, task helpers, and Agent Card discovery

## Build Commands

```bash
# Build entire solution
dotnet build src/Voltaic.sln

# Build specific project
dotnet build src/Voltaic/Voltaic.csproj
dotnet build src/Sample.McpServer/Sample.McpServer.csproj
dotnet build src/Sample.A2AServer/Sample.A2AServer.csproj
dotnet build src/Test.A2AServer/Test.A2AServer.csproj

# Build for release
dotnet build src/Voltaic.sln -c Release

# Release package
dotnet pack src/Voltaic/Voltaic.csproj -c Release
```

## Running Tests

```bash
# Run Touchstone console suites
dotnet run --project src/Test.Automated/Test.Automated.csproj -c Release --framework net8.0
dotnet run --project src/Test.Automated/Test.Automated.csproj -c Release --framework net10.0

# Export JSON results
dotnet run --project src/Test.Automated/Test.Automated.csproj -c Release --framework net8.0 -- --results artifacts/test-results/voltaic-touchstone.json

# Run adapter-backed tests
dotnet test src/Test.Xunit/Test.Xunit.csproj -c Release --framework net8.0
dotnet test src/Test.Xunit/Test.Xunit.csproj -c Release --framework net10.0
dotnet test src/Test.Nunit/Test.Nunit.csproj -c Release --framework net8.0
dotnet test src/Test.Nunit/Test.Nunit.csproj -c Release --framework net10.0

# The shared suite currently projects 644 deterministic cases through the console, xUnit, and NUnit runners.
```

## Running Interactive Test Applications

```bash
# Start JSON-RPC TCP server/client examples
dotnet run --project src/Test.JsonRpcServer/Test.JsonRpcServer.csproj -- 8080
dotnet run --project src/Test.JsonRpcClient/Test.JsonRpcClient.csproj -- 8080

# Start MCP HTTP examples
dotnet run --project src/Test.McpHttpServer/Test.McpHttpServer.csproj -- 8080
dotnet run --project src/Test.McpHttpClient/Test.McpHttpClient.csproj -- 8080

# Start the combined MCP sample server
dotnet run --project src/Sample.McpServer/Sample.McpServer.csproj

# Start the A2A sample server and manual client
# A2A servers use the selected port for JSON-RPC/HTTP+JSON and port + 1 for gRPC.
dotnet run --project src/Sample.A2AServer/Sample.A2AServer.csproj -- 8080
dotnet run --project src/Test.A2AServer/Test.A2AServer.csproj -- 8080
dotnet run --project src/Test.A2AClient/Test.A2AClient.csproj -- http://localhost:8080 "hello from A2A"
```

## Architecture Notes

### JSON-RPC Protocol Implementation

The implementation follows JSON-RPC 2.0 specification:
- **Requests** have an optional `id` field - when omitted, they become notifications (no response expected)
- **Responses** must include the matching `id` from the request
- **Built-in error codes** are provided via static factory methods in `JsonRpcError`

### Server Architecture (JsonRpcServer.cs)

- Implements `IDisposable` for proper resource cleanup
- Uses `TcpListener` to accept connections on a specified port
- Maintains concurrent connections in a `ConcurrentDictionary<string, ClientConnection>`
- Each client is assigned a unique ID (`client_1`, `client_2`, etc.)
- Methods are registered via `RegisterMethod(name, handler)` with both sync (`Func<RpcParameters?, object>`) and async (`Func<RpcParameters?, Task<object>>`) overloads. Sync handlers are wrapped internally via `Task.FromResult()`.
- Optional diagnostic methods (`includeDiagnosticMethods: true`, default `false`): `ping` (returns `"pong"`), `echo`, `getTime`, `add`
- Supports broadcasting notifications to all connected clients via `BroadcastNotificationAsync()`
- Responses are only sent for requests with an `id` (not for notifications)
- Server can be stopped gracefully with `Stop()` method which disconnects all clients

### Client Architecture (JsonRpcClient.cs)

- Implements `IDisposable` for proper resource cleanup
- Uses `TcpClient` to connect to server via `ConnectAsync(host, port)`
- Maintains pending requests in `ConcurrentDictionary<object, TaskCompletionSource<JsonRpcResponse>>`
- Request IDs are integers generated by `Interlocked.Increment` (thread-safe)
- Response matching handles `JsonElement` wrapping of numeric IDs
- Background receive loop processes responses and notifications
- Two call methods: `CallAsync<T>()` for typed responses and `CallAsync()` for object responses
- `NotifyAsync()` sends notifications without expecting responses
- Notifications from server trigger `NotificationReceived` event
- Connection status available via `IsConnected` property
- Supports configurable timeouts for requests (default 30 seconds)
- Use `Disconnect()` or `Dispose()` to cleanly close connection

### Key Implementation Details

1. **ID Handling**: The client uses integer IDs but must handle them as `JsonElement` when deserializing responses. The `ProcessResponse` method (JsonRpcClient.cs:89-133) extracts the integer value from `JsonElement` for dictionary lookups.

2. **Namespace Usage**: When working with the library:
   - Use `using Voltaic.Core;` for JSON-RPC and shared core types
   - Use `using Voltaic.Mcp;` for MCP types
   - Use `using Voltaic.A2A;` for A2A types

3. **Connection Management**:
   - Server handles disconnections in `HandleClientAsync` finally block (JsonRpcServer.cs:137-142)
   - Client maintains `isConnected` flag synchronized with connection state
   - Pending requests are cancelled on disconnect

4. **Method Registration**: Server methods receive `JsonElement?` parameters and must use `TryGetProperty()` to extract typed values safely.

5. **Message Framing**: The `MessageFraming` class (internal static) provides LSP-style message framing:
   - Format: `Content-Length: {size}\r\n\r\n{json_body}`
   - `ReadMessageAsync()` handles partial reads, buffering, and multiple messages
   - `WriteMessageAsync()` frames messages with Content-Length header
   - Supports optional Content-Type header
   - Note: Currently uses tuples for return values (should be refactored per coding standards)

## Package Information

The Voltaic library is configured for NuGet package generation:
- Author: Joel Christner
- Target frameworks: .NET 8.0 and .NET 10.0
- `GeneratePackageOnBuild` is enabled

## Coding Standards and Style Guidelines

**THESE RULES MUST BE FOLLOWED STRICTLY**

### Namespace and Using Statements

- Namespace declaration must always be at the top of the file
- Using statements must be contained INSIDE the namespace block
- All Microsoft and standard system library usings must be first, in alphabetical order
- Other using statements follow, also in alphabetical order

Example:
```csharp
namespace Voltaic.Core
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using ThirdPartyLib;
}
```

### Documentation Requirements

- All public members, constructors, and public methods MUST have XML documentation comments
- NO code documentation should be applied to private members or private methods
- Document nullability in XML comments
- Document thread safety guarantees in XML comments
- Document which exceptions public methods can throw using `/// <exception>` tags
- Document default values, minimum values, and maximum values where appropriate
- Specify what different values mean or what effect they may have

### Member Naming and Declaration

- Private class member variable names MUST start with an underscore and then be Pascal cased
  - Correct: `_FooBar`, `_TcpClient`, `_IsConnected`
  - Incorrect: `_fooBar`, `fooBar`, `tcpClient`
- All public members should have explicit getters and setters using backing variables when value requires range or null validation
- Do NOT use `var` when defining variables - use the actual type explicitly
  - Correct: `List<string> items = new List<string>();`
  - Incorrect: `var items = new List<string>();`
- Avoid using constant values for things that developers may later want to configure
  - Instead use a public member with a backing private member set to a reasonable default

### Type Usage and Patterns

- **Do NOT use tuples unless absolutely necessary** - they are strongly discouraged
- Use specific exception types rather than generic `Exception`
- Always include meaningful error messages with context
- Consider using custom exception types for domain-specific errors
- Use exception filters when appropriate: `catch (SqlException ex) when (ex.Number == 2601)`
- Use nullable reference types (ensure `<Nullable>enable</Nullable>` in project files)
- Validate input parameters with guard clauses at method start
- Use `ArgumentNullException.ThrowIfNull()` for .NET 6+ or manual null checks
- Consider using the Result pattern or Option/Maybe types for methods that can fail
- Proactively identify and eliminate any situations in code where null might cause exceptions

### Asynchronous Programming

- Async calls should use `.ConfigureAwait(false)` where appropriate
- Every async method should accept a `CancellationToken` as an input parameter, unless:
  - The class has a `CancellationToken` as a class member, OR
  - The class has a `CancellationTokenSource` as a class member
- Async calls should check whether cancellation has been requested at appropriate places using `token.ThrowIfCancellationRequested()` or checking `token.IsCancellationRequested`
- When implementing a method that returns an `IEnumerable`, also create an async variant that includes a `CancellationToken`

### Resource Management

- Implement `IDisposable`/`IAsyncDisposable` when holding unmanaged resources or disposable objects
- Use `using` statements or `using` declarations for `IDisposable` objects
- Follow the full Dispose pattern with `protected virtual void Dispose(bool disposing)`
- Always call `base.Dispose()` in derived classes

### Thread Safety

- Use `Interlocked` operations for simple atomic operations
- Prefer `ReaderWriterLockSlim` over `lock` for read-heavy scenarios

### LINQ and Collections

- Prefer LINQ methods over manual loops when readability is not compromised
- Use `.Any()` instead of `.Count() > 0` for existence checks
- Be aware of multiple enumeration issues - consider `.ToList()` when needed
- Use `.FirstOrDefault()` with null checks rather than `.First()` when element might not exist

### File Organization

- Limit each file to containing exactly one class OR exactly one enum
- Do NOT nest multiple classes or multiple enums in a single file
- Regions for Public-Members, Private-Members, Constructors-and-Factories, Public-Methods, and Private-Methods are NOT required for small files under 500 lines

### Library Code Restrictions

- **Ensure NO `Console.WriteLine` statements are added to library code**
- Use logging abstractions, events, or delegates instead for library diagnostic output
- Sample and test applications under `src/Test.*` and `src/Sample.*` may use console output freely

### SQL and External Dependencies

- If manual SQL string construction is present, there is likely a good reason - assume it's intentional
- Do not make assumptions about class members or methods on opaque classes - ask for implementation details

### Documentation and Maintenance

- If a README exists, analyze it and ensure it is accurate
- Compile the code and ensure it is free of errors and warnings to the best of your ability

## Current Code Status

**Note**: Some existing code in the repository does not yet fully conform to these standards:
- Private member names in `JsonRpcClient.cs` and `JsonRpcServer.cs` use camelCase (e.g., `tcpClient`, `stream`) instead of PascalCase with underscore prefix (e.g., `_TcpClient`, `_Stream`)
- `MessageFraming.cs` uses tuples extensively for return values, which should be refactored to custom types or classes
- Some uses of `var` exist in the codebase that should be replaced with explicit types

When modifying existing code, update it to follow these standards. When creating new code, strictly adhere to all standards from the start.
