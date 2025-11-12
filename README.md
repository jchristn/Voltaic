<div align="center">
  <img src="assets/logo.png" alt="Voltaic Logo" width="192" height="192">
</div>

# Voltaic

[![NuGet](https://img.shields.io/nuget/v/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![Downloads](https://img.shields.io/nuget/dt/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.md) [![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Modern, lightweight JSON-RPC 2.0 and MCP implementations for .NET 8.0**

Voltaic provides client and server implementations for JSON-RPC 2.0 and the Model Context Protocol (MCP). Whether you're building microservices, AI integrations, or distributed systems—Voltaic gives you the tools to communicate clearly and reliably.

---

## What's Inside

### JSON-RPC 2.0
A complete JSON-RPC 2.0 implementation with TCP-based client and server. Perfect for building RPC-based APIs, microservices, and distributed applications.

**Features:**
- Full JSON-RPC 2.0 specification compliance
- TCP-based transport with LSP-style message framing (`Content-Length` headers)
- Async/await throughout for modern .NET performance
- Support for requests, responses, notifications, and broadcasts
- Type-safe method registration and invocation
- Connection management with graceful shutdown
- Thread-safe concurrent request handling
- Event-driven architecture with connection and request/response events
- Per-client notification queue management with configurable limits

### Model Context Protocol (MCP)
Client and server implementations for Anthropic's Model Context Protocol, supporting multiple transport options.

**Features:**
- **Stdio transport**: Subprocess-based MCP servers (standard MCP pattern)
- **TCP transport**: Network-based MCP communication with LSP-style framing
- **HTTP transport**: HTTP-based MCP communication with Server-Sent Events (SSE) for notifications
- **WebSocket transport**: Full-duplex bidirectional communication
- Implements JSON-RPC 2.0 protocol across all transports
- Process lifecycle management for subprocess servers
- Event-driven notification handling with connection lifecycle events
- Request/response event tracking with timing information
- Compatible with MCP server ecosystem

### Important

Voltaic currently does not support authorization, which is listed as OPTIONAL in the [spec](https://modelcontextprotocol.io/specification/draft/basic/authorization).

---

## Who Is This For?

Voltaic is designed for developers who need:

- **Microservice Communication**: Build services that talk to each other using a standard RPC protocol
- **AI Tool Integration**: Connect to MCP servers for AI assistant integrations (Claude, etc.)
- **Custom RPC APIs**: Implement your own remote procedure call interfaces
- **Subprocess Orchestration**: Launch and communicate with child processes using stdio transport
- **Language Server Protocols**: Build LSP-style applications that use Content-Length framing
- **Real-time Systems**: Low-latency RPC communication over TCP sockets
- **Web-based Integration**: HTTP and WebSocket transports for browser-compatible communication
- **Flexible Transport Options**: Choose the right transport for your architecture

If you're building .NET applications that need structured, bidirectional communication, Voltaic has you covered.

---

## Getting Started

### Installation

```bash
# Install the Voltaic package
dotnet add package Voltaic
```

### Quick Start: JSON-RPC Server (TCP)

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

// Register custom methods
server.RegisterMethod("greet", (JsonElement? args) =>
{
    string? name = args?.TryGetProperty("name", out JsonElement nameEl) == true
        ? nameEl.GetString()
        : "World";
    return $"Hello, {name}!";
});

// Start the server
await server.StartAsync();
Console.WriteLine("Server running on port 8080");

// Keep it running
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

### Quick Start: JSON-RPC Client (TCP)

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

### Quick Start: MCP Server (stdio)

```csharp
using System.Text.Json;
using Voltaic;

McpServer server = new McpServer();

// Register MCP methods
server.RegisterMethod("tools/list", (JsonElement? args) =>
{
    return new
    {
        tools = new[]
        {
            new { name = "calculator", description = "Performs calculations" }
        }
    };
});

// Run the server (reads from stdin, writes to stdout)
await server.RunAsync();
```

### Quick Start: MCP Client (stdio)

```csharp
using Voltaic;

McpClient client = new McpClient();

// Launch an MCP server as a subprocess
await client.LaunchServerAsync("dotnet", new[] { "run", "--project", "MyMcpServer" });

// Call methods on the server
JsonRpcResponse response = await client.CallAsync("tools/list");
Console.WriteLine(response.Result);
```

### Quick Start: MCP Server (TCP)

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

// Register MCP methods
server.RegisterMethod("tools/list", (JsonElement? args) =>
{
    return new
    {
        tools = new[]
        {
            new { name = "calculator", description = "Performs calculations" }
        }
    };
});

// Start the server
await server.StartAsync();
Console.WriteLine("MCP server running on port 8080");
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

### Quick Start: MCP Client (TCP)

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

### Quick Start: MCP Server (HTTP)

```csharp
using System.Text.Json;
using Voltaic;

McpHttpServer server = new McpHttpServer("localhost", 8080);

// Subscribe to events
server.ClientConnected += (sender, client) =>
    Console.WriteLine($"Session started: {client.SessionId}");

server.RequestReceived += (sender, e) =>
    Console.WriteLine($"Request: {e.Method} from session {e.Client.SessionId}");

// Register MCP methods
server.RegisterMethod("tools/list", (JsonElement? args) =>
{
    return new
    {
        tools = new[]
        {
            new { name = "calculator", description = "Performs calculations" }
        }
    };
});

// Start the server
await server.StartAsync();
Console.WriteLine("MCP HTTP server running on http://localhost:8080");
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

### Quick Start: MCP Client (HTTP)

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

### Quick Start: MCP Server (WebSocket)

```csharp
using System.Text.Json;
using Voltaic;

McpWebsocketsServer server = new McpWebsocketsServer("localhost", 8080);

// Subscribe to events
server.ClientConnected += (sender, client) =>
    Console.WriteLine($"WebSocket client connected: {client.SessionId}");

server.ResponseSent += (sender, e) =>
    Console.WriteLine($"Sent response for {e.Method} in {e.Duration.TotalMilliseconds}ms");

// Register MCP methods
server.RegisterMethod("tools/list", (JsonElement? args) =>
{
    return new
    {
        tools = new[]
        {
            new { name = "calculator", description = "Performs calculations" }
        }
    };
});

// Start the server
await server.StartAsync();
Console.WriteLine("MCP WebSocket server running on ws://localhost:8080");
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

### Quick Start: MCP Client (WebSocket)

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

## When NOT to Use This

Voltaic might not be the right fit if you need:

- **gRPC Features**: If you need streaming, advanced load balancing, or language-agnostic service definitions, use gRPC
- **REST Conventions**: If you need resource-oriented APIs with standard HTTP verbs, use web APIs or a REST microservice
- **High-level Abstractions**: Voltaic is a protocol library, not a framework — you'll write your own business logic

---

## Documentation

All classes and methods are available in the `Voltaic` namespace.

### JSON-RPC Server and Client

**Server API (JsonRpcServer):**

*Constructor:*
- `JsonRpcServer(IPAddress ip, int port, bool includeDefaultMethods = true)` - Create a server listening on the specified IP address and port

*Methods:*
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register an RPC method
- `Task StartAsync(CancellationToken token = default)` - Start accepting connections
- `Task BroadcastNotificationAsync(string method, object? parameters, CancellationToken token = default)` - Send notifications to all clients
- `List<string> GetConnectedClients()` - Get list of connected client IDs
- `bool KickClient(string clientId)` - Disconnect a specific client by ID
- `void Stop()` - Gracefully shut down the server

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
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register an MCP method
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)` - Register tool with metadata
- `Task RunAsync(CancellationToken token = default)` - Run the server (blocks until stdin closes)

*Events:*
- `event EventHandler<string> Log` - Fires when a log message is generated

**McpClient (stdio):**

*Methods:*
- `Task<bool> LaunchServerAsync(string executable, string[] args, CancellationToken token = default)` - Launch subprocess server
- `Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call server method with typed response
- `Task<JsonRpcResponse> CallAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call server method
- `Task NotifyAsync(string method, object? parameters = null, CancellationToken token = default)` - Send notification
- `void StopServer()` - Stop the subprocess server

*Events:*
- `event EventHandler<JsonRpcRequest> NotificationReceived` - Handle server notifications
- `event EventHandler<string> Log` - Fires when a log message is generated

**McpTcpServer (TCP-based MCP):**

Inherits from `JsonRpcServer` with additional MCP-specific built-in methods. All JsonRpcServer APIs apply, plus:

*Additional Built-in Methods:*
- `initialize` - MCP protocol initialization
- `tools/list` - List registered tools
- `tools/call` - Invoke a tool by name

**McpTcpClient (TCP-based MCP):**

*Methods:*
- Same as `JsonRpcClient`
- `Task<bool> ConnectAsync(string host, int port, CancellationToken token = default)` - Connect to TCP server
- `Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call with typed response

**McpHttpServer (HTTP-based MCP with SSE):**

*Constructor:*
- `McpHttpServer(string hostname, int port, string rpcPath = "/rpc", string eventsPath = "/events", bool includeDefaultMethods = true)`

*Methods:*
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register an RPC method
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)` - Register tool with metadata
- `Task StartAsync(CancellationToken token = default)` - Start the HTTP server
- `bool SendNotificationToSession(string sessionId, string method, object? parameters = null)` - Send notification to specific session
- `void BroadcastNotification(string method, object? parameters = null)` - Broadcast to all sessions
- `List<string> GetActiveSessions()` - Get list of active session IDs
- `List<string> GetConnectedClients()` - Get list of connected client IDs
- `bool KickClient(string clientId)` - Disconnect a specific client
- `bool RemoveSession(string sessionId)` - Remove a session
- `void Stop()` - Stop the server

*Properties:*
- `int SessionTimeoutSeconds { get; set; }` - Session timeout (default: 300, min: 10)
- `int MaxQueueSize { get; set; }` - Max queued notifications per client (default: 100, min: 1)
- `bool EnableCors { get; set; }` - Enable CORS support (default: true)
- `Dictionary<string, string> CorsHeaders { get; set; }` - CORS headers configuration
- `string ProtocolVersion { get; set; }` - MCP protocol version (default: "2025-03-26")
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
- `Task<bool> StartSseAsync(CancellationToken token = default)` - Start Server-Sent Events for notifications
- `void StopSse()` - Stop SSE connection
- `Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call with typed response
- `Task<JsonRpcResponse> CallAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)` - Call method
- `Task NotifyAsync(string method, object? parameters = null, CancellationToken token = default)` - Send notification

*Properties:*
- `string? SessionId { get; }` - Session ID assigned by server
- `bool IsConnected { get; }` - Connection status

*Events:*
- `event EventHandler<JsonRpcRequest> NotificationReceived` - Handle server notifications
- `event EventHandler<string> Log` - Fires when a log message is generated

**McpWebsocketsServer (WebSocket-based MCP):**

*Constructor:*
- `McpWebsocketsServer(string hostname, int port, string path = "/mcp", bool includeDefaultMethods = true)`

*Methods:*
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register an RPC method
- `void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)` - Register tool with metadata
- `Task StartAsync(CancellationToken token = default)` - Start the WebSocket server
- `Task BroadcastNotificationAsync(string method, object? parameters = null, CancellationToken token = default)` - Broadcast to all clients
- `List<string> GetConnectedClients()` - Get list of connected client IDs
- `bool KickClient(string clientId)` - Disconnect a specific client
- `void Stop()` - Stop the server

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
    string status = e.IsSuccess ? "✓" : "✗";
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

## Examples

Check out the `src/Test.*` projects for working examples:

- **Test.JsonRpcServer** / **Test.JsonRpcClient**: Interactive JSON-RPC demos over TCP
- **Test.McpServer** / **Test.McpClient**: MCP stdio examples
- **Test.McpHttpServer** / **Test.McpHttpClient**: MCP HTTP with SSE examples
- **Test.McpWebsocketsServer** / **Test.McpWebsocketsClient**: MCP WebSocket examples
- **Test.Automated**: Comprehensive test suite showing various usage patterns

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
   - **URL**: Enter `http://{hostname}:{port}/rpc`
     - For example: `http://localhost:8080/rpc`
     - If you specified a custom `rpcPath` when creating the server, use that instead of `/rpc`

4. **Click Connect**

5. **Verify the connection**: The inspector should display the list of registered tools and allow you to call them interactively

**Note**: MCP Inspector currently supports HTTP transport via Streamable HTTP. For other transports (TCP, WebSocket, stdio), use the corresponding client implementations or command-line tools.

---

## Building

```bash
# Build everything
dotnet build src/Voltaic.sln

# Build the library
dotnet build src/Voltaic/Voltaic.csproj

# Run automated tests (all transports)
dotnet run --project src/Test.Automated/Test.Automated.csproj

# Run automated tests for specific transport
dotnet run --project src/Test.Automated/Test.Automated.csproj -- -stdio
dotnet run --project src/Test.Automated/Test.Automated.csproj -- -tcp
dotnet run --project src/Test.Automated/Test.Automated.csproj -- -http
dotnet run --project src/Test.Automated/Test.Automated.csproj -- -ws

# Run automated tests for multiple transports
dotnet run --project src/Test.Automated/Test.Automated.csproj -- -tcp -http -ws
```

---

## License

Voltaic is released under the [MIT License](LICENSE.md). Use it freely in your projects, commercial or otherwise.

---

## Support

Need help or found a bug?

- **Issues**: Report bugs or request features at [github.com/jchristn/voltaic/issues](https://github.com/jchristn/voltaic/issues)
- **Discussions**: Ask questions and share ideas at [github.com/jchristn/voltaic/discussions](https://github.com/jchristn/voltaic/discussions)
