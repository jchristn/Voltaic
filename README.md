<div align="center">
  <img src="assets/logo.png" alt="Voltaic Logo" width="192" height="192">
</div>

# Voltaic

[![NuGet](https://img.shields.io/nuget/v/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![Downloads](https://img.shields.io/nuget/dt/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.md) [![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Modern, lightweight JSON-RPC 2.0 and Model Context Protocol (MCP) implementations for .NET 8.0 and .NET 10.0**

Voltaic gives .NET applications a small, direct way to expose and consume structured RPC APIs. Use it when you need JSON-RPC 2.0, MCP tools/resources/prompts, or multiple MCP transports without adopting a larger application framework.

Voltaic v0.3.0 targets MCP protocol version `2025-11-25` and keeps compatibility helpers for `2025-03-26`.

---

## What Is Voltaic?

Voltaic is a protocol library, not an application framework. It provides:

- JSON-RPC 2.0 clients and servers over TCP with LSP-style `Content-Length` framing
- MCP stdio servers and clients for subprocess-hosted tools
- MCP Streamable HTTP on `/mcp` with `MCP-Session-Id` sessions and SSE notifications
- MCP TCP and WebSocket transports for networked or full-duplex scenarios
- Shared request/response, notification, lifecycle, and event handling across transports

You bring your business logic. Voltaic handles the protocol surface, message framing, method dispatch, session headers, and transport-specific plumbing.

## What Can It Do?

- Register JSON-RPC methods with synchronous, asynchronous, or cancellation-aware handlers.
- Register MCP tools with input schema metadata, output schema metadata, structured content, annotations, icons, and full `McpToolCallResult` returns.
- Validate common JSON Schema object/type/required cases for tool input and structured output.
- Expose MCP resources, resource templates, prompts, and completion providers.
- Handle MCP `initialize`, `tools/list`, `tools/call`, `resources/*`, `prompts/*`, `completion/complete`, `logging/setLevel`, and utility notifications.
- Send list-changed, resource-updated, progress, cancellation, and log-message notifications where the transport supports server-to-client notifications.
- Host HTTP compatibility endpoints (`/rpc` and `/events`) alongside the current Streamable HTTP endpoint (`/mcp`).
- Run the same 253-case Touchstone suite through console, xUnit, and NUnit projects under `src/`.

## Why Use Voltaic?

- **Small API surface**: Register handlers and start a transport; avoid framework-level ceremony.
- **Current MCP coverage**: Tools, resources, prompts, completions, Streamable HTTP, sessions, and utility notifications are first-class.
- **Transport choice**: Use stdio for local MCP servers, Streamable HTTP for MCP clients and inspectors, TCP for service-to-service RPC, or WebSockets for full-duplex web-facing systems.
- **Plain .NET**: Works with normal C# delegates, `System.Text.Json`, `Task`, `CancellationToken`, and `IDisposable`.
- **Testable behavior**: Protocol behavior is covered by shared Touchstone descriptors and adapter-backed test projects.

## Who Is This For?

Voltaic is designed for developers building:

- AI assistant integrations that need to expose MCP tools, resources, prompts, or completions from .NET.
- Services that need structured JSON-RPC calls without REST resource modeling or gRPC contracts.
- Local agents, CLIs, and desktop tools that launch MCP subprocesses over stdio.
- Language-server-style protocols that use `Content-Length` message framing.
- Web integrations that need Streamable HTTP, SSE notifications, or WebSocket communication.
- Libraries and products that need protocol primitives without handing control to a large host framework.

---

## Getting Started

### Installation

```bash
dotnet add package Voltaic
```

### End-to-End MCP Example (Streamable HTTP)

This example creates a small MCP HTTP server with one `add` tool, then connects with `McpHttpClient`, performs the MCP initialization flow, lists tools, and calls the tool.

Create the server:

```bash
dotnet new console -n CalculatorServer
cd CalculatorServer
dotnet add package Voltaic
```

Replace `Program.cs`:

```csharp
using System.Text.Json;
using Voltaic;

using McpHttpServer server = new McpHttpServer("localhost", 8080)
{
    ServerName = "CalculatorServer",
    ServerVersion = "1.0.0"
};

server.RegisterTool(
    "add",
    "Adds two numbers",
    new
    {
        type = "object",
        properties = new
        {
            a = new { type = "number", description = "First number" },
            b = new { type = "number", description = "Second number" }
        },
        required = new[] { "a", "b" }
    },
    (JsonElement? args) =>
    {
        double a = args.HasValue && args.Value.TryGetProperty("a", out JsonElement aEl)
            ? aEl.GetDouble()
            : 0;
        double b = args.HasValue && args.Value.TryGetProperty("b", out JsonElement bEl)
            ? bEl.GetDouble()
            : 0;

        return (object)(a + b);
    });

await server.StartAsync();
Console.WriteLine("MCP server listening at http://localhost:8080/mcp");
await Task.Delay(Timeout.Infinite);
```

Run it:

```bash
dotnet run
```

Create the client in a second terminal:

```bash
dotnet new console -n CalculatorClient
cd CalculatorClient
dotnet add package Voltaic
```

Replace `Program.cs`:

```csharp
using Voltaic;

using McpHttpClient client = new McpHttpClient();

await client.ConnectStreamableAsync("http://localhost:8080");

await client.CallAsync("initialize", new
{
    protocolVersion = "2025-11-25",
    capabilities = new { },
    clientInfo = new
    {
        name = "CalculatorClient",
        version = "1.0.0"
    }
});
await client.NotifyAsync("notifications/initialized");

JsonRpcResponse tools = await client.CallAsync("tools/list");
Console.WriteLine(tools.Result);

JsonRpcResponse sum = await client.CallAsync("tools/call", new
{
    name = "add",
    arguments = new
    {
        a = 2,
        b = 3
    }
});
Console.WriteLine(sum.Result);
```

Run it:

```bash
dotnet run
```

`McpHttpClient` automatically uses the required Streamable HTTP headers, including `Accept: application/json, text/event-stream` and the `MCP-Session-Id` returned by the server. Call `StartSseAsync()` after `ConnectStreamableAsync()` if the client also needs server-sent notifications.

---

## More Quick Starts

### JSON-RPC Server (TCP)

```csharp
using System.Net;
using System.Text.Json;
using Voltaic;

JsonRpcServer server = new JsonRpcServer(IPAddress.Any, 8080);

// Subscribe to events
server.ClientConnected += (sender, client) =>
    Console.WriteLine($"Client connected: {client.SessionId}");

server.RequestReceived += (sender, e) =>
    Console.WriteLine($"Request: {e.Method} from {e.Client.SessionId}");

server.ResponseSent += (sender, e) =>
    Console.WriteLine($"Response: {e.Method} took {e.Duration.TotalMilliseconds}ms");

// Register a synchronous method
server.RegisterMethod("greet", (JsonElement? args) =>
{
    string? name = args?.TryGetProperty("name", out JsonElement nameEl) == true
        ? nameEl.GetString()
        : "World";
    return $"Hello, {name}!";
});

// Register an asynchronous method (for I/O-bound work like DB queries, HTTP calls, etc.)
server.RegisterMethod("fetchData", async (JsonElement? args) =>
{
    // Async handlers avoid blocking the thread pool
    await Task.Delay(100); // Simulate async work
    return (object)"async result";
});

// Register an async method with cancellation support
server.RegisterMethod("longRunningTask", async (JsonElement? args, CancellationToken token) =>
{
    // The token is the server's connection processing token
    await Task.Delay(5000, token); // Cancels if client disconnects
    return (object)"completed";
});

// Start the server
await server.StartAsync();
Console.WriteLine("Server running on port 8080");

// Keep it running
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

### JSON-RPC Client (TCP)

```csharp
using Voltaic;

JsonRpcClient client = new JsonRpcClient();

// Subscribe to notification events from server
client.NotificationReceived += (sender, request) =>
    Console.WriteLine($"Server notification: {request.Method}");

await client.ConnectAsync("localhost", 8080);

// Call a method with typed response
string greeting = await client.CallAsync<string>("greet", new { name = "Developer" });
Console.WriteLine(greeting); // "Hello, Developer!"

// Send a notification (no response expected)
await client.NotifyAsync("logEvent", new { level = "info", message = "User logged in" });
```

### MCP Server (stdio)

```csharp
using System.Text.Json;
using Voltaic;

McpServer server = new McpServer();

// Customize server identity (optional)
server.ServerName = "MyMcpServer";
server.ServerVersion = "2.0.0";

// Register a tool with metadata for MCP tool discovery
server.RegisterTool("add",
    "Adds two numbers",
    new
    {
        type = "object",
        properties = new
        {
            a = new { type = "number", description = "First number" },
            b = new { type = "number", description = "Second number" }
        },
        required = new[] { "a", "b" }
    },
    (JsonElement? args) =>
    {
        double a = args?.TryGetProperty("a", out JsonElement aEl) == true ? aEl.GetDouble() : 0;
        double b = args?.TryGetProperty("b", out JsonElement bEl) == true ? bEl.GetDouble() : 0;
        return (object)(a + b);
    });

// Built-in methods are registered automatically:
// - initialize (returns capabilities and serverInfo)
// - tools/list (returns all registered tools)
// - tools/call (invokes a tool by name)
// - notifications/initialized (handles client init notification)
// - ping, echo, getTime (utility tools)

// Run the server (reads from stdin, writes to stdout)
await server.RunAsync();
```

### MCP Client (stdio)

```csharp
using Voltaic;

McpClient client = new McpClient();

// Launch an MCP server as a subprocess
await client.LaunchServerAsync("dotnet", new[] { "run", "--project", "MyMcpServer" });

// Call methods on the server
JsonRpcResponse response = await client.CallAsync("tools/list");
Console.WriteLine(response.Result);
```

### MCP Server (TCP)

```csharp
using System.Net;
using System.Text.Json;
using Voltaic;

McpTcpServer server = new McpTcpServer(IPAddress.Any, 8080);

// Subscribe to events
server.ClientConnected += (sender, client) =>
    Console.WriteLine($"Client connected: {client.SessionId}");

server.ClientDisconnected += (sender, client) =>
    Console.WriteLine($"Client disconnected: {client.SessionId}");

server.RegisterTool(
    "add",
    "Adds two numbers",
    new
    {
        type = "object",
        properties = new
        {
            a = new { type = "number", description = "First number" },
            b = new { type = "number", description = "Second number" }
        },
        required = new[] { "a", "b" }
    },
    (JsonElement? args) =>
    {
        double a = args?.TryGetProperty("a", out JsonElement aEl) == true ? aEl.GetDouble() : 0;
        double b = args?.TryGetProperty("b", out JsonElement bEl) == true ? bEl.GetDouble() : 0;
        return (object)(a + b);
    });

// Start the server
await server.StartAsync();
Console.WriteLine("MCP server running on port 8080");
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

### MCP Client (TCP)

```csharp
using Voltaic;

McpTcpClient client = new McpTcpClient();

// Subscribe to server notifications
client.NotificationReceived += (sender, request) =>
    Console.WriteLine($"Server notification: {request.Method}");

// Connect to the TCP server
await client.ConnectAsync("localhost", 8080);

// Call methods on the server
object? tools = await client.CallAsync<object>("tools/list");
Console.WriteLine(tools);
```

### MCP Server (HTTP)

```csharp
using System.Text.Json;
using Voltaic;

McpHttpServer server = new McpHttpServer("localhost", 8080);

// Subscribe to events
server.ClientConnected += (sender, client) =>
    Console.WriteLine($"Session started: {client.SessionId}");

server.RequestReceived += (sender, e) =>
    Console.WriteLine($"Request: {e.Method} from session {e.Client.SessionId}");

// Register a tool (automatically added to tools/list and tools/call)
server.RegisterTool("add",
    "Adds two numbers",
    new
    {
        type = "object",
        properties = new
        {
            a = new { type = "number", description = "First number" },
            b = new { type = "number", description = "Second number" }
        },
        required = new[] { "a", "b" }
    },
    (JsonElement? args) =>
    {
        double a = args?.TryGetProperty("a", out JsonElement aEl) == true ? aEl.GetDouble() : 0;
        double b = args?.TryGetProperty("b", out JsonElement bEl) == true ? bEl.GetDouble() : 0;
        return (object)(a + b);
    });

// Start the server
await server.StartAsync();
Console.WriteLine("MCP HTTP server running on http://localhost:8080");
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

The default `McpHttpServer` listens on all three HTTP endpoints:
- `/rpc` for request/response JSON-RPC
- `/events` for classic SSE notifications
- `/mcp` for MCP Streamable HTTP

Set `mcpPath: null` in the constructor if you want to disable the Streamable HTTP endpoint.

### MCP Client (HTTP)

```csharp
using Voltaic;

McpHttpClient client = new McpHttpClient();

// Connect to the HTTP server
await client.ConnectAsync("http://localhost:8080");

// Start SSE connection for server notifications
await client.StartSseAsync();

// Call methods on the server
object? result = await client.CallAsync<object>("tools/list");
Console.WriteLine(result);
```

### MCP Client (Streamable HTTP)

```csharp
using Voltaic;

McpHttpClient client = new McpHttpClient();

// Establish the RPC/session side on POST /mcp
await client.ConnectStreamableAsync("http://localhost:8080");

// Open the SSE side on GET /mcp for notifications
await client.StartSseAsync();

// Call methods on the same session
object? result = await client.CallAsync<object>("tools/list");
Console.WriteLine(result);
```

`ConnectStreamableAsync()` establishes the session and POST endpoint. Call `StartSseAsync()` when you want the SSE notification stream to become active on the same `/mcp` endpoint.

### MCP Server (WebSocket)

```csharp
using System.Text.Json;
using Voltaic;

McpWebsocketsServer server = new McpWebsocketsServer("localhost", 8080);

// Subscribe to events
server.ClientConnected += (sender, client) =>
    Console.WriteLine($"WebSocket client connected: {client.SessionId}");

server.ResponseSent += (sender, e) =>
    Console.WriteLine($"Sent response for {e.Method} in {e.Duration.TotalMilliseconds}ms");

server.RegisterTool(
    "add",
    "Adds two numbers",
    new
    {
        type = "object",
        properties = new
        {
            a = new { type = "number", description = "First number" },
            b = new { type = "number", description = "Second number" }
        },
        required = new[] { "a", "b" }
    },
    (JsonElement? args) =>
    {
        double a = args?.TryGetProperty("a", out JsonElement aEl) == true ? aEl.GetDouble() : 0;
        double b = args?.TryGetProperty("b", out JsonElement bEl) == true ? bEl.GetDouble() : 0;
        return (object)(a + b);
    });

// Start the server
await server.StartAsync();
Console.WriteLine("MCP WebSocket server running on ws://localhost:8080/mcp");
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

### MCP Client (WebSocket)

```csharp
using Voltaic;

McpWebsocketsClient client = new McpWebsocketsClient();

// Subscribe to server notifications
client.NotificationReceived += (sender, request) =>
    Console.WriteLine($"Server notification: {request.Method}");

// Connect to the WebSocket server
await client.ConnectAsync("ws://localhost:8080/mcp");

// Call methods on the server
object? result = await client.CallAsync<object>("tools/list");
Console.WriteLine(result);

// Send a notification
await client.NotifyAsync("log", new { message = "Hello from WebSocket client" });
```

---

## Authentication

`McpHttpServer` supports an optional async authentication handler that runs before request processing. When set, every incoming HTTP request is passed through the handler, which receives the full `HttpListenerRequest` and returns an `AuthenticationResult`. If authentication fails, the server returns the configured HTTP status code and error message without processing the request. When not set, all requests are accepted.

```csharp
using System.Net;
using Voltaic;

McpHttpServer server = new McpHttpServer("localhost", 8080);

server.AuthenticationHandler = async (HttpListenerRequest request) =>
{
    string? token = request.Headers["Authorization"];
    if (string.IsNullOrEmpty(token) || !token.StartsWith("Bearer "))
    {
        return new AuthenticationResult
        {
            IsAuthenticated = false,
            StatusCode = 401,
            ErrorMessage = "Missing or invalid Authorization header"
        };
    }

    // Validate the token with your JWT validator, database, identity provider, etc.
    bool isValid = await ValidateTokenAsync(token.Substring("Bearer ".Length));

    return new AuthenticationResult
    {
        IsAuthenticated = isValid,
        Principal = "my-user",
        Claims = new Dictionary<string, string> { { "role", "admin" } }
    };
};

await server.StartAsync();
```

The following requests bypass authentication so infrastructure can validate connectivity:

- **Health check** (`GET /`) - returns `{"status":"Ok"}` for load balancer probes.
- **Ping** (`ping` JSON-RPC method via any RPC endpoint) - returns `"pong"` for application-layer connectivity checks.
- **CORS preflight** (`OPTIONS` requests) - returns `204` with CORS headers.

Full authorization flows, such as OAuth 2.1 from the MCP specification, remain the responsibility of the application. `AuthenticationHandler` is the hook for plugging in the scheme your product already uses.

---

## When NOT to Use This

Voltaic might not be the right fit if you need:

- **gRPC Features**: If you need streaming, advanced load balancing, or language-agnostic service definitions, use gRPC
- **REST Conventions**: If you need resource-oriented APIs with standard HTTP verbs, use web APIs or a REST microservice
- **High-level Abstractions**: Voltaic is a protocol library, not a framework; you'll write your own business logic

---

## Resource Management

All server and client classes implement `IDisposable` using the full Dispose pattern (`protected virtual void Dispose(bool disposing)`) with double-disposal protection. Use `using` statements or call `Dispose()` to ensure proper resource cleanup:

```csharp
// Recommended: using statement ensures cleanup
using McpHttpServer server = new McpHttpServer("localhost", 8080);
await server.StartAsync();

// Or manually dispose
McpHttpServer server2 = new McpHttpServer("localhost", 8081);
try
{
    await server2.StartAsync();
}
finally
{
    server2.Dispose();
}
```

**Key points:**
- `Dispose()` is safe to call multiple times; subsequent calls are no-ops
- For servers, `Dispose()` calls `Stop()` internally, disconnecting all clients and releasing the listening port
- For clients, `Dispose()` calls `Disconnect()` internally, cancelling pending requests
- `Disconnect()`/`Stop()` manage connection state only; `Dispose()` releases underlying resources (sockets, listeners, cancellation tokens)
- All classes support the `protected virtual void Dispose(bool disposing)` pattern for subclass extensibility

---

## Example Projects

Check out the `src/Test.*` projects for working examples:

- **Test.JsonRpcServer** / **Test.JsonRpcClient**: Interactive JSON-RPC demos over TCP
- **Test.McpServer** / **Test.McpClient**: MCP stdio examples
- **Test.McpHttpServer** / **Test.McpHttpClient**: MCP HTTP with SSE examples
- **Test.McpWebsocketsServer** / **Test.McpWebsocketsClient**: MCP WebSocket examples
- **Sample.McpServer**: MCP tool, structured-output, resource, template, and prompt sample
- **Test.Shared**: Shared Touchstone descriptors and the central 253-case API/protocol matrix
- **Test.Automated**: Touchstone console runner
- **Test.Xunit** / **Test.Nunit**: Touchstone adapter projects for `dotnet test`

Run examples:
```bash
# JSON-RPC Server (TCP)
dotnet run --project src/Test.JsonRpcServer/Test.JsonRpcServer.csproj -- 8080

# JSON-RPC Client (TCP)
dotnet run --project src/Test.JsonRpcClient/Test.JsonRpcClient.csproj -- 8080

# MCP Stdio Client (launches server subprocess)
dotnet run --project src/Test.McpClient/Test.McpClient.csproj

# MCP HTTP Server
dotnet run --project src/Test.McpHttpServer/Test.McpHttpServer.csproj -- 8080

# MCP HTTP Client
dotnet run --project src/Test.McpHttpClient/Test.McpHttpClient.csproj -- 8080

# MCP WebSocket Server
dotnet run --project src/Test.McpWebsocketsServer/Test.McpWebsocketsServer.csproj -- 8080

# MCP WebSocket Client
dotnet run --project src/Test.McpWebsocketsClient/Test.McpWebsocketsClient.csproj -- 8080
```

### Connecting with MCP Inspector

The [MCP Inspector](https://github.com/modelcontextprotocol/inspector) is a visual tool for testing and debugging MCP servers. To connect MCP Inspector to a Voltaic MCP HTTP server:

1. **Start your MCP HTTP server**:
   ```bash
   dotnet run --project src/Test.McpHttpServer/Test.McpHttpServer.csproj -- 8080
   ```

2. **Open MCP Inspector** in your web browser

3. **Configure the connection**:
   - **Transport Type**: Select `Streamable HTTP`
   - **URL**: Enter `http://{hostname}:{port}/mcp`
     - For example: `http://localhost:8080/mcp`
     - If you specified a custom `mcpPath` when creating the server, use that instead of `/mcp`

4. **Click Connect**

5. **Verify the connection**: The inspector should display the list of registered tools and allow you to call them interactively

**Note**: Use the `Streamable HTTP` transport in MCP Inspector for Voltaic's `/mcp` endpoint. For other Voltaic transports (TCP, WebSocket, stdio), use the corresponding client implementations or command-line tools.

---

## Building

```bash
# Build everything
dotnet build src/Voltaic.sln

# Build the library
dotnet build src/Voltaic/Voltaic.csproj

# Run Touchstone console tests
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net8.0

# The shared suite currently projects 253 cases through the console, xUnit, and NUnit runners

# Export Touchstone JSON results
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net8.0 -- --results artifacts/test-results/voltaic-touchstone.json

# Filter by descriptor tag
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net8.0 -- --tag mcp

# Run adapter-backed tests
dotnet test src/Test.Xunit/Test.Xunit.csproj --framework net8.0
dotnet test src/Test.Nunit/Test.Nunit.csproj --framework net8.0

# Cross-target the console runner
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net10.0
```

---

## API Reference

All classes and methods are available in the `Voltaic` namespace.

### JSON-RPC Server and Client

**Server API (JsonRpcServer):**

*Constructor:*
- `JsonRpcServer(IPAddress ip, int port, bool includeDefaultMethods = true)` - Create a server listening on the specified IP address and port

*Methods:*
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register a synchronous RPC method
- `void RegisterMethod(string name, Func<JsonElement?, Task<object>> handler)` - Register an asynchronous RPC method
- `void RegisterMethod(string name, Func<JsonElement?, CancellationToken, Task<object>> handler)` - Register an async RPC method with cancellation support
- `Task StartAsync(CancellationToken token = default)` - Start accepting connections
- `Task BroadcastNotificationAsync(string method, object? parameters, CancellationToken token = default)` - Send notifications to all clients
- `List<string> GetConnectedClients()` - Get list of connected client IDs
- `bool KickClient(string clientId)` - Disconnect a specific client by ID
- `void Stop()` - Gracefully shut down the server
- `void Dispose()` - Release all resources (calls Stop() internally, safe to call multiple times)

*Properties:*
- `int MaxQueueSize { get; set; }` - Maximum queued notifications per client (default: 100, min: 1)
- `CancellationTokenSource? TokenSource { get; }` - Cancellation token source for the server
- `string DefaultContentType { get; set; }` - Content-Type header for messages (default: "application/json; charset=utf-8")

*Events:*
- `event EventHandler<ClientConnection> ClientConnected` - Fires when a client connects
- `event EventHandler<ClientConnection> ClientDisconnected` - Fires when a client disconnects
- `event EventHandler<JsonRpcRequestEventArgs> RequestReceived` - Fires when a request is received
- `event EventHandler<JsonRpcResponseEventArgs> ResponseSent` - Fires when a response is sent
- `event EventHandler<string> Log` - Fires when a log message is generated

**Client API (JsonRpcClient):**

*Methods:*
- `Task<bool> ConnectAsync(string host, int port, CancellationToken token = default)` - Connect to a server
- `Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Make an RPC call and await typed response
- `Task<JsonRpcResponse> CallAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Make an RPC call and await response
- `Task NotifyAsync(string method, object? parameters = null, CancellationToken token = default)` - Send a notification (no response)
- `void Disconnect()` - Close the connection
- `void Dispose()` - Release all resources (calls Disconnect() internally, safe to call multiple times)

*Properties:*
- `bool IsConnected { get; }` - Whether the client is currently connected
- `TcpClient? TcpClient { get; }` - Underlying TCP client
- `CancellationTokenSource? TokenSource { get; }` - Cancellation token source
- `string DefaultContentType { get; set; }` - Content-Type header for messages

*Events:*
- `event EventHandler<JsonRpcRequest> NotificationReceived` - Fires when a notification is received from the server
- `event EventHandler<string> Log` - Fires when a log message is generated

### MCP Servers and Clients

**McpServer (stdio):**

*Methods:*
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register a synchronous MCP method
- `void RegisterMethod(string name, Func<JsonElement?, Task<object>> handler)` - Register an asynchronous MCP method
- `void RegisterMethod(string name, Func<JsonElement?, CancellationToken, Task<object>> handler)` - Register an async MCP method with cancellation support
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)` - Register tool with synchronous handler
- `void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, object> handler)` - Register tool with output schema metadata
- `void RegisterTool(ToolDefinition definition, Func<JsonElement?, object> handler)` - Register tool with full MCP metadata
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, Task<object>> handler)` - Register tool with asynchronous handler
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, CancellationToken, Task<object>> handler)` - Register tool with async cancellable handler
- `void RegisterResource(string uri, string name, string mimeType, Func<McpReadResourceResult> readHandler)` - Register a static resource
- `void RegisterResourceTemplate(string uriTemplate, string name, string mimeType, Func<string, McpReadResourceResult> readHandler)` - Register a URI-template resource
- `void RegisterPrompt(string name, string description, IEnumerable<McpPromptArgument>? arguments, Func<JsonElement?, McpGetPromptResult> handler)` - Register a prompt
- `void RegisterCompletionProvider(string referenceType, string? referenceId, string? argumentName, Func<McpCompleteRequest, CancellationToken, Task<McpCompleteResult>> handler)` - Register completions for prompt arguments or resource template variables
- `Task RunAsync(CancellationToken token = default)` - Run the server (blocks until stdin closes)
- `void Dispose()` - Release all resources (safe to call multiple times)

*Properties:*
- `string ProtocolVersion { get; set; }` - MCP protocol version (default: "2025-11-25")
- `string ServerName { get; set; }` - Server name for MCP serverInfo (default: "Voltaic.Mcp.StdioServer")
- `string ServerVersion { get; set; }` - Server version for MCP serverInfo (default: "1.0.0")

*Built-in Methods:*
- `initialize` - MCP protocol initialization (returns capabilities and serverInfo)
- `tools/list` - List registered tools
- `tools/call` - Invoke a tool by name
- `resources/list`, `resources/templates/list`, `resources/read`, `resources/subscribe`, `resources/unsubscribe`
- `prompts/list`, `prompts/get`
- `completion/complete`
- `logging/setLevel`
- `notifications/initialized`, `notifications/cancelled`
- `ping`, `echo`, `getTime` - Utility tools

*Events:*
- `event EventHandler<string> Log` - Fires when a log message is generated

**McpClient (stdio):**

*Methods:*
- `Task<bool> LaunchServerAsync(string executable, string[] args, CancellationToken token = default)` - Launch subprocess server
- `Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call server method with typed response
- `Task<JsonRpcResponse> CallAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call server method
- `Task NotifyAsync(string method, object? parameters = null, CancellationToken token = default)` - Send notification
- `void StopServer()` - Stop the subprocess server
- `void Dispose()` - Release all resources (calls Shutdown() internally, safe to call multiple times)

*Events:*
- `event EventHandler<JsonRpcRequest> NotificationReceived` - Handle server notifications
- `event EventHandler<string> Log` - Fires when a log message is generated

**McpTcpServer (TCP-based MCP):**

Inherits from `JsonRpcServer` with additional MCP-specific built-in methods. All JsonRpcServer APIs apply, plus:

*Additional Methods:*
- `RegisterTool(...)` overloads matching `McpServer`
- `RegisterResource(...)`, `RegisterResourceTemplate(...)`, and `RegisterPrompt(...)`
- `RegisterCompletionProvider(...)`
- `Task NotifyToolsChangedAsync(CancellationToken token = default)`
- `Task NotifyResourcesChangedAsync(CancellationToken token = default)`
- `Task NotifyResourceUpdatedAsync(string uri, CancellationToken token = default)`
- `Task NotifyPromptsChangedAsync(CancellationToken token = default)`
- `Task NotifyProgressAsync(object progressToken, double progress, double? total = null, string? message = null, CancellationToken token = default)`
- `Task NotifyCancelledAsync(object requestId, string? reason = null, CancellationToken token = default)`
- `Task NotifyLogMessageAsync(string level, object? data, string? logger = null, CancellationToken token = default)`

*Additional Built-in Methods:*
- `initialize` - MCP protocol initialization
- `tools/list` - List registered tools
- `tools/call` - Invoke a tool by name
- `resources/list`, `resources/templates/list`, `resources/read`, `resources/subscribe`, `resources/unsubscribe`
- `prompts/list`, `prompts/get`
- `completion/complete`, `logging/setLevel`, `notifications/cancelled`

**McpTcpClient (TCP-based MCP):**

*Methods:*
- Same as `JsonRpcClient`
- `Task<bool> ConnectAsync(string host, int port, CancellationToken token = default)` - Connect to TCP server
- `Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call with typed response

**McpHttpServer (HTTP-based MCP with SSE):**

*Constructor:*
- `McpHttpServer(string hostname, int port, string rpcPath = "/rpc", string eventsPath = "/events", bool includeDefaultMethods = true, string? mcpPath = "/mcp")`

*Methods:*
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register a synchronous RPC method
- `void RegisterMethod(string name, Func<JsonElement?, Task<object>> handler)` - Register an asynchronous RPC method
- `void RegisterMethod(string name, Func<JsonElement?, CancellationToken, Task<object>> handler)` - Register an async RPC method with cancellation support
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)` - Register tool with synchronous handler
- `void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, object> handler)` - Register tool with output schema metadata
- `void RegisterTool(ToolDefinition definition, Func<JsonElement?, object> handler)` - Register tool with full MCP metadata
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, Task<object>> handler)` - Register tool with asynchronous handler
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, CancellationToken, Task<object>> handler)` - Register tool with async cancellable handler
- `void RegisterResource(string uri, string name, string mimeType, Func<McpReadResourceResult> readHandler)` - Register a static resource
- `void RegisterResourceTemplate(string uriTemplate, string name, string mimeType, Func<string, McpReadResourceResult> readHandler)` - Register a URI-template resource
- `void RegisterPrompt(string name, string description, IEnumerable<McpPromptArgument>? arguments, Func<JsonElement?, McpGetPromptResult> handler)` - Register a prompt
- `void RegisterCompletionProvider(string referenceType, string? referenceId, string? argumentName, Func<McpCompleteRequest, CancellationToken, Task<McpCompleteResult>> handler)` - Register completions
- `void NotifyToolsChanged()` - Queue `notifications/tools/list_changed` for active sessions
- `void NotifyResourcesChanged()` - Queue `notifications/resources/list_changed` for active sessions
- `void NotifyResourceUpdated(string uri)` - Queue `notifications/resources/updated` for active sessions
- `void NotifyPromptsChanged()` - Queue `notifications/prompts/list_changed` for active sessions
- `bool NotifyProgress(string sessionId, object progressToken, double progress, double? total = null, string? message = null)` - Queue `notifications/progress`
- `bool NotifyCancelled(string sessionId, object requestId, string? reason = null)` - Queue `notifications/cancelled`
- `bool NotifyLogMessage(string sessionId, string level, object? data, string? logger = null)` - Queue `notifications/message`
- `Task StartAsync(CancellationToken token = default)` - Start the HTTP server
- `bool SendNotificationToSession(string sessionId, string method, object? parameters = null)` - Send notification to specific session
- `void BroadcastNotification(string method, object? parameters = null)` - Broadcast to all sessions
- `List<string> GetActiveSessions()` - Get list of active session IDs
- `List<string> GetConnectedClients()` - Get list of connected client IDs
- `bool KickClient(string clientId)` - Disconnect a specific client
- `bool RemoveSession(string sessionId)` - Remove a session
- `void Stop()` - Stop the server
- `void Dispose()` - Release all resources (calls Stop() internally, safe to call multiple times)

*Properties:*
- `Func<HttpListenerRequest, Task<AuthenticationResult>>? AuthenticationHandler { get; set; }` - Optional async authentication handler. When set, every request is authenticated before processing. When null (default), all requests are accepted
- `int SessionTimeoutSeconds { get; set; }` - Session timeout (default: 300, min: 10)
- `int MaxQueueSize { get; set; }` - Max queued notifications per client (default: 100, min: 1)
- `bool EnableCors { get; set; }` - Enable CORS support (default: true)
- `Dictionary<string, string> CorsHeaders { get; set; }` - CORS headers configuration
- `string ProtocolVersion { get; set; }` - MCP protocol version (default: "2025-11-25")
- `string ServerName { get; set; }` - Server name for MCP serverInfo
- `string ServerVersion { get; set; }` - Server version for MCP serverInfo

*Events:*
- `event EventHandler<ClientConnection> ClientConnected` - Fires when a session is created
- `event EventHandler<ClientConnection> ClientDisconnected` - Fires when a session is removed
- `event EventHandler<JsonRpcRequestEventArgs> RequestReceived` - Fires when a request is received
- `event EventHandler<JsonRpcResponseEventArgs> ResponseSent` - Fires when a response is sent
- `event EventHandler<string> Log` - Fires when a log message is generated

**McpHttpClient (HTTP-based MCP):**

*Methods:*
- `Task<bool> ConnectAsync(string baseUrl, CancellationToken token = default)` - Connect to HTTP server
- `Task<bool> ConnectStreamableAsync(string baseUrl, string mcpPath = "/mcp", CancellationToken token = default)` - Establish a Streamable HTTP session on `/mcp`
- `Task<bool> StartSseAsync(CancellationToken token = default)` - Start Server-Sent Events for notifications on `/events` or `/mcp`
- `void StopSse()` - Stop SSE connection
- `Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call with typed response
- `Task<JsonRpcResponse> CallAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call method
- `Task NotifyAsync(string method, object? parameters = null, CancellationToken token = default)` - Send notification
- `void Disconnect()` - Disconnect from the server
- `void Dispose()` - Release all resources (calls Disconnect() internally, safe to call multiple times)

*Properties:*
- `string? SessionId { get; }` - Session ID assigned by server
- `bool IsConnected { get; }` - Connection status
- `bool IsSseConnected { get; }` - SSE connection status
- `string ProtocolVersion { get; set; }` - MCP protocol version header sent after a session is established

*Events:*
- `event EventHandler<JsonRpcRequest> NotificationReceived` - Handle server notifications
- `event EventHandler<string> Log` - Fires when a log message is generated

**McpWebsocketsServer (WebSocket-based MCP):**

*Constructor:*
- `McpWebsocketsServer(string hostname, int port, string path = "/mcp", bool includeDefaultMethods = true)`

*Methods:*
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register a synchronous RPC method
- `void RegisterMethod(string name, Func<JsonElement?, Task<object>> handler)` - Register an asynchronous RPC method
- `void RegisterMethod(string name, Func<JsonElement?, CancellationToken, Task<object>> handler)` - Register an async RPC method with cancellation support
- `RegisterTool(...)` overloads matching `McpServer`
- `RegisterResource(...)`, `RegisterResourceTemplate(...)`, and `RegisterPrompt(...)`
- `RegisterCompletionProvider(...)`
- `Task NotifyToolsChangedAsync(CancellationToken token = default)`
- `Task NotifyResourcesChangedAsync(CancellationToken token = default)`
- `Task NotifyResourceUpdatedAsync(string uri, CancellationToken token = default)`
- `Task NotifyPromptsChangedAsync(CancellationToken token = default)`
- `Task NotifyProgressAsync(object progressToken, double progress, double? total = null, string? message = null, CancellationToken token = default)`
- `Task NotifyCancelledAsync(object requestId, string? reason = null, CancellationToken token = default)`
- `Task NotifyLogMessageAsync(string level, object? data, string? logger = null, CancellationToken token = default)`
- `Task StartAsync(CancellationToken token = default)` - Start the WebSocket server
- `Task BroadcastNotificationAsync(string method, object? parameters = null, CancellationToken token = default)` - Broadcast to all clients
- `List<string> GetConnectedClients()` - Get list of connected client IDs
- `bool KickClient(string clientId)` - Disconnect a specific client
- `void Stop()` - Stop the server
- `void Dispose()` - Release all resources (calls Stop() internally, safe to call multiple times)

*Properties:*
- `int MaxMessageSize { get; set; }` - Maximum message size in bytes (default: 1MB, min: 4096)
- `int KeepAliveIntervalSeconds { get; set; }` - WebSocket keep-alive interval (default: 30, 0 to disable)
- `int MaxQueueSize { get; set; }` - Max queued notifications per client (default: 100, min: 1)
- `string ProtocolVersion { get; set; }` - MCP protocol version
- `string ServerName { get; set; }` - Server name for MCP serverInfo
- `string ServerVersion { get; set; }` - Server version for MCP serverInfo

*Events:*
- `event EventHandler<ClientConnection> ClientConnected` - Fires when a client connects
- `event EventHandler<ClientConnection> ClientDisconnected` - Fires when a client disconnects
- `event EventHandler<JsonRpcRequestEventArgs> RequestReceived` - Fires when a request is received
- `event EventHandler<JsonRpcResponseEventArgs> ResponseSent` - Fires when a response is sent
- `event EventHandler<string> Log` - Fires when a log message is generated

**McpWebsocketsClient (WebSocket-based MCP):**

*Methods:*
- `Task<bool> ConnectAsync(string url, CancellationToken token = default)` - Connect to WebSocket server
- `Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call with typed response
- `Task<JsonRpcResponse> CallAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call method
- `Task NotifyAsync(string method, object? parameters = null, CancellationToken token = default)` - Send notification
- `void Disconnect()` - Close connection
- `void Dispose()` - Release all resources (calls Disconnect() internally, safe to call multiple times)

*Properties:*
- `bool IsConnected { get; }` - Connection status
- `int MaxMessageSize { get; set; }` - Maximum message size

*Events:*
- `event EventHandler<JsonRpcRequest> NotificationReceived` - Handle server notifications
- `event EventHandler<string> Log` - Fires when a log message is generated

### Event Handler Examples

All server types support event handlers for monitoring connection lifecycle and request/response activity:

**Monitoring Client Connections:**

```csharp
using System.Net;
using Voltaic;

JsonRpcServer server = new JsonRpcServer(IPAddress.Any, 8080);

server.ClientConnected += (sender, client) =>
{
    Console.WriteLine($"New client: {client.SessionId}");
    Console.WriteLine($"Connection type: {client.Type}");
    Console.WriteLine($"Connected at: {client.LastActivity:yyyy-MM-dd HH:mm:ss}");
};

server.ClientDisconnected += (sender, client) =>
{
    Console.WriteLine($"Client {client.SessionId} disconnected");
    Console.WriteLine($"Queued notifications: {client.Count()}");
};

await server.StartAsync();
```

**Tracking Requests and Responses:**

```csharp
using System.Net;
using Voltaic;

JsonRpcServer server = new JsonRpcServer(IPAddress.Any, 8080);

server.RequestReceived += (sender, e) =>
{
    Console.WriteLine($"[{e.ReceivedUtc:HH:mm:ss.fff}] Request from {e.Client.SessionId}");
    Console.WriteLine($"  Method: {e.Method}");
    Console.WriteLine($"  Request ID: {e.RequestId}");
    Console.WriteLine($"  Is Notification: {e.IsNotification}");
};

server.ResponseSent += (sender, e) =>
{
    string status = e.IsSuccess ? "OK" : "ERR";
    Console.WriteLine($"[{e.SentUtc:HH:mm:ss.fff}] {status} Response to {e.Client.SessionId}");
    Console.WriteLine($"  Method: {e.Method}");
    Console.WriteLine($"  Duration: {e.Duration.TotalMilliseconds:F2}ms");
    Console.WriteLine($"  Success: {e.IsSuccess}");

    if (e.IsError)
    {
        Console.WriteLine($"  Error: {e.Response.Error?.Message}");
    }
};

await server.StartAsync();
```

**Managing Client Queues:**

```csharp
using System.Net;
using Voltaic;

JsonRpcServer server = new JsonRpcServer(IPAddress.Any, 8080);

// Configure queue size
server.MaxQueueSize = 50; // Max 50 notifications per client

server.ClientConnected += (sender, client) =>
{
    // Per-client queue configuration
    client.MaxQueueSize = 100; // Override for this specific client
    Console.WriteLine($"Client {client.SessionId} queue size: {client.MaxQueueSize}");
};

// Monitor queue activity
server.ResponseSent += (sender, e) =>
{
    int queuedCount = e.Client.Count();
    if (queuedCount > 40)
    {
        Console.WriteLine($"WARNING: Client {e.Client.SessionId} queue is {queuedCount}/50");
    }
};

await server.StartAsync();
```

**Building Request Metrics:**

```csharp
using System.Collections.Concurrent;
using System.Net;
using Voltaic;

JsonRpcServer server = new JsonRpcServer(IPAddress.Any, 8080);

ConcurrentDictionary<string, int> requestCounts = new();
ConcurrentDictionary<string, List<double>> responseTimes = new();

server.RequestReceived += (sender, e) =>
{
    requestCounts.AddOrUpdate(e.Method, 1, (key, count) => count + 1);
};

server.ResponseSent += (sender, e) =>
{
    double ms = e.Duration.TotalMilliseconds;
    responseTimes.AddOrUpdate(
        e.Method,
        new List<double> { ms },
        (key, list) => { list.Add(ms); return list; }
    );
};

// Print stats every 10 seconds
Timer statsTimer = new Timer(_ =>
{
    Console.WriteLine("\n=== Request Statistics ===");
    foreach (var kvp in requestCounts.OrderByDescending(x => x.Value))
    {
        var times = responseTimes.GetValueOrDefault(kvp.Key, new List<double>());
        double avgMs = times.Count > 0 ? times.Average() : 0;
        Console.WriteLine($"{kvp.Key}: {kvp.Value} requests, avg {avgMs:F2}ms");
    }
}, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

await server.StartAsync();
```

**Handling Client Notifications:**

```csharp
using Voltaic;

JsonRpcClient client = new JsonRpcClient();

client.NotificationReceived += (sender, request) =>
{
    Console.WriteLine($"Server notification: {request.Method}");

    // Handle specific notification types
    switch (request.Method)
    {
        case "server/shutdown":
            Console.WriteLine("Server is shutting down!");
            break;

        case "broadcast":
            var message = request.Params?.ToString();
            Console.WriteLine($"Broadcast: {message}");
            break;

        default:
            Console.WriteLine($"Unknown notification: {request.Method}");
            break;
    }
};

await client.ConnectAsync("localhost", 8080);
```

**Event-Driven Client Management:**

```csharp
using System.Collections.Concurrent;
using Voltaic;

McpWebsocketsServer server = new McpWebsocketsServer("localhost", 8080);

ConcurrentDictionary<string, ClientConnection> activeClients = new();

server.ClientConnected += (sender, client) =>
{
    activeClients[client.SessionId] = client;

    // Send welcome notification to the new client
    JsonRpcRequest welcome = new JsonRpcRequest
    {
        Method = "welcome",
        Params = new { message = $"Welcome {client.SessionId}!" }
    };
    client.Enqueue(welcome);
};

server.ClientDisconnected += (sender, client) =>
{
    activeClients.TryRemove(client.SessionId, out _);

    // Notify other clients
    foreach (var otherClient in activeClients.Values)
    {
        JsonRpcRequest notification = new JsonRpcRequest
        {
            Method = "client_left",
            Params = new { clientId = client.SessionId }
        };
        otherClient.Enqueue(notification);
    }
};

await server.StartAsync();
```

---

## License

Voltaic is released under the [MIT License](LICENSE.md). Use it freely in your projects, commercial or otherwise.

---

## Support

Need help or found a bug?

- **Issues**: Report bugs or request features at [github.com/jchristn/voltaic/issues](https://github.com/jchristn/voltaic/issues)
- **Discussions**: Ask questions and share ideas at [github.com/jchristn/voltaic/discussions](https://github.com/jchristn/voltaic/discussions)
