<div align="center">
  <img src="assets/logo.png" alt="Voltaic Logo" width="192" height="192">
</div>

# Voltaic

[![NuGet](https://img.shields.io/nuget/v/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![Downloads](https://img.shields.io/nuget/dt/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.md) [![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Modern, lightweight JSON-RPC 2.0 and MCP implementations for .NET 8.0**

Voltaic provides client and server implementations for JSON-RPC 2.0 and the Model Context Protocol (MCP). Whether you're building microservices, AI integrations, or distributed systems—Voltaic gives you the tools to communicate clearly and reliably.

---

## What's Inside

Voltaic consists of two focused libraries, available separately or together:

### Voltaic.JsonRpc
A complete JSON-RPC 2.0 implementation with TCP-based client and server. Perfect for building RPC-based APIs, microservices, and distributed applications.

**Features:**
- Full JSON-RPC 2.0 specification compliance
- TCP-based transport with LSP-style message framing (`Content-Length` headers)
- Async/await throughout for modern .NET performance
- Support for requests, responses, notifications, and broadcasts
- Type-safe method registration and invocation
- Connection management with graceful shutdown
- Thread-safe concurrent request handling

### Voltaic.Mcp
Client and server implementations for Anthropic's Model Context Protocol (MCP), supporting multiple transport options.

**Features:**
- **Stdio transport**: Subprocess-based MCP servers (standard MCP pattern)
- **TCP transport**: Network-based MCP communication with LSP-style framing
- **HTTP transport**: HTTP-based MCP communication with Server-Sent Events (SSE) for notifications
- **WebSocket transport**: Full-duplex bidirectional communication
- Implements JSON-RPC 2.0 protocol across all transports
- Process lifecycle management for subprocess servers
- Event-driven notification handling
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
# Install the full Voltaic package (includes both JsonRpc and Mcp)
dotnet add package Voltaic
```

### Quick Start: JSON-RPC Server (TCP)

```csharp
using System.Net;
using System.Text.Json;
using Voltaic.JsonRpc;

JsonRpcServer server = new JsonRpcServer(IPAddress.Any, 8080);

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
using Voltaic.JsonRpc;

JsonRpcClient client = new JsonRpcClient();
await client.ConnectAsync("localhost", 8080);

// Call a method with typed response
JsonRpcResponse response = await client.CallAsync("greet", new { name = "Developer" });
Console.WriteLine(response.Result); // "Hello, Developer!"

// Send a notification (no response expected)
await client.NotifyAsync("logEvent", new { level = "info", message = "User logged in" });
```

### Quick Start: MCP Server (stdio)

```csharp
using System.Text.Json;
using Voltaic.Mcp;

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
using Voltaic.Mcp;

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
using Voltaic.Mcp;

McpTcpServer server = new McpTcpServer(IPAddress.Any, 8080);

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
using Voltaic.Mcp;

McpTcpClient client = new McpTcpClient();

// Connect to the TCP server
await client.ConnectAsync("localhost", 8080);

// Call methods on the server
JsonRpcResponse response = await client.CallAsync("tools/list");
Console.WriteLine(response.Result);
```

### Quick Start: MCP Server (HTTP)

```csharp
using System.Text.Json;
using Voltaic.Mcp;

McpHttpServer server = new McpHttpServer("localhost", 8080);

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
using Voltaic.Mcp;

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
using Voltaic.Mcp;

McpWebsocketsServer server = new McpWebsocketsServer("localhost", 8080);

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
using Voltaic.Mcp;

McpWebsocketsClient client = new McpWebsocketsClient();

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
- **REST Conventions**: If you need resource-oriented APIs with standard HTTP verbs, use ASP.NET Core Web APIs
- **Legacy .NET**: Voltaic targets .NET 8.0+; older frameworks aren't supported
- **High-level Abstractions**: Voltaic is a protocol library, not a framework—you'll write your own business logic

---

## Documentation

### Voltaic.JsonRpc

**Server API:**
- `JsonRpcServer(IPAddress ip, int port, bool includeDefaultMethods = true)` - Create a server listening on the specified IP address and port
- `RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register an RPC method
- `Task StartAsync(CancellationToken token = default)` - Start accepting connections
- `Task BroadcastNotificationAsync(string method, object? parameters, CancellationToken token = default)` - Send notifications to all clients
- `void Stop()` - Gracefully shut down the server

**Client API:**
- `Task<bool> ConnectAsync(string host, int port)` - Connect to a server
- `Task<JsonRpcResponse> CallAsync(string method, object? parameters)` - Make an RPC call and await response
- `Task NotifyAsync(string method, object? parameters)` - Send a notification (no response)
- `NotificationReceived` event - Handle notifications from the server
- `void Disconnect()` - Close the connection

### Voltaic.Mcp

**McpServer (stdio):**
- `void RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register an MCP method
- `Task RunAsync(CancellationToken token = default)` - Run the server (blocks until stdin closes)

**McpClient (stdio):**
- `Task<bool> LaunchServerAsync(string executable, string[] args)` - Launch subprocess server
- `Task<JsonRpcResponse> CallAsync(string method, object? parameters)` - Call server method
- `NotificationReceived` event - Handle server notifications

**TCP Variants:**
- `McpTcpServer(IPAddress ip, int port, bool includeDefaultMethods = true)` - TCP-based MCP server
- `McpTcpClient()` - TCP-based MCP client
- Same API as JsonRpcServer/JsonRpcClient

**HTTP Variants:**
- `McpHttpServer(string hostname, int port, string rpcPath = "/rpc", string eventsPath = "/events", bool includeDefaultMethods = true)` - HTTP-based MCP server with SSE support
- `McpHttpClient()` - HTTP-based MCP client
- Additional methods:
  - `Task<bool> ConnectAsync(string baseUrl)` - Connect to HTTP server
  - `Task<bool> StartSseAsync()` - Start Server-Sent Events connection for notifications
  - `void StopSse()` - Stop SSE connection
  - `string? SessionId` property - Get session ID assigned by server

**WebSocket Variants:**
- `McpWebsocketsServer(string hostname, int port, string path = "/mcp", bool includeDefaultMethods = true)` - WebSocket-based MCP server
- `McpWebsocketsClient()` - WebSocket-based MCP client
- Same API as JsonRpcClient with bidirectional messaging

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

# Build specific library
dotnet build src/Voltaic.JsonRpc/Voltaic.JsonRpc.csproj
dotnet build src/Voltaic.Mcp/Voltaic.Mcp.csproj

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
