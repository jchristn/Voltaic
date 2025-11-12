<div align="center">
  <img src="assets/logo.png" alt="Voltaic Logo" width="192" height="192">
</div>

# Voltaic

[![NuGet](https://img.shields.io/nuget/v/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![Downloads](https://img.shields.io/nuget/dt/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.md) [![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Modern, lightweight JSON-RPC 2.0 and MCP implementations for .NET 8.0**

Voltaic provides client and server implementations for JSON-RPC 2.0 and the Model Context Protocol (MCP). Whether you're building microservices, AI integrations, or distributed systems, Voltaic gives you the tools to communicate clearly and reliably.

---

## What's Inside

Voltaic is split into two focused libraries:

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
Client and server implementations for Anthropic's Model Context Protocol (MCP), supporting both stdio and TCP transports.

**Features:**
- Stdio transport for subprocess-based MCP servers (standard MCP pattern)
- TCP transport for network-based MCP communication
- Full JSON-RPC 2.0 foundation (built on Voltaic.JsonRpc)
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
- **Custom RPC APIs**: Implement your own remote procedure call interfaces without the overhead of HTTP
- **Subprocess Orchestration**: Launch and communicate with child processes using stdio transport
- **Language Server Protocols**: Build LSP-style applications that use Content-Length framing
- **Real-time Systems**: Low-latency RPC communication over TCP sockets

If you're building .NET applications that need structured, bidirectional communication, Voltaic has you covered.

---

## Getting Started

### Installation

```bash
# Install the full Voltaic package (includes both JsonRpc and Mcp)
dotnet add package Voltaic
```

### Quick Start: JSON-RPC Server

```csharp
using Voltaic.JsonRpc;

JsonRpcServer server = new JsonRpcServer(8080);

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

### Quick Start: JSON-RPC Client

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

---

## When NOT to Use This

Voltaic might not be the right fit if you need:

- **HTTP-based RPC**: Use gRPC, REST APIs, or ASP.NET Core Web APIs instead
- **Language Interop**: If you need to communicate across languages, consider gRPC or plain HTTP
- **Browser Clients**: JSON-RPC over TCP doesn't work in browsers; use WebSockets or HTTP
- **Legacy .NET**: Voltaic targets .NET 8.0+; older frameworks aren't supported
- **High-level Abstractions**: Voltaic is a protocol library, not a framework—you'll write your own business logic

---

## Documentation

### Voltaic.JsonRpc

**Server API:**
- `JsonRpcServer(int port)` - Create a server listening on the specified port
- `RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register an RPC method
- `StartAsync()` - Start accepting connections
- `BroadcastNotificationAsync(string method, object? params)` - Send notifications to all clients
- `Stop()` - Gracefully shut down the server

**Client API:**
- `ConnectAsync(string host, int port)` - Connect to a server
- `CallAsync(string method, object? params)` - Make an RPC call and await response
- `NotifyAsync(string method, object? params)` - Send a notification (no response)
- `NotificationReceived` event - Handle notifications from the server
- `Disconnect()` - Close the connection

### Voltaic.Mcp

**McpServer (stdio):**
- `RegisterMethod(string name, Func<JsonElement?, object> handler)` - Register an MCP method
- `RunAsync(CancellationToken token)` - Run the server (blocks until stdin closes)

**McpClient (stdio):**
- `LaunchServerAsync(string executable, string[] args)` - Launch subprocess server
- `CallAsync(string method, object? params)` - Call server method
- `NotificationReceived` event - Handle server notifications

**TCP Variants:**
- `McpTcpServer` and `McpTcpClient` provide network-based MCP communication

---

## Examples

Check out the `src/Test.*` projects for working examples:

- **Test.JsonRpcServer** / **Test.JsonRpcClient**: Interactive JSON-RPC demos
- **Test.McpServer** / **Test.McpClient**: MCP stdio examples
- **Test.Automated**: Comprehensive test suite showing various usage patterns

Run examples:
```bash
# JSON-RPC Server
dotnet run --project src/Test.JsonRpcServer -- 8080

# JSON-RPC Client
dotnet run --project src/Test.JsonRpcClient -- 8080

# MCP Client (launches server subprocess)
dotnet run --project src/Test.McpClient
```

---

## Building

```bash
# Build everything
dotnet build src/Voltaic.sln

# Build specific library
dotnet build src/Voltaic.JsonRpc/Voltaic.JsonRpc.csproj

# Run automated tests
dotnet run --project src/Test.Automated/Test.Automated.csproj
```

---

## License

Voltaic is released under the [MIT License](LICENSE.md). Use it freely in your projects, commercial or otherwise.

---

## Support

Need help or found a bug?

- **Issues**: Report bugs or request features at [github.com/jchristn/voltaic/issues](https://github.com/jchristn/voltaic/issues)
- **Discussions**: Ask questions and share ideas at [github.com/jchristn/voltaic/discussions](https://github.com/jchristn/voltaic/discussions)
