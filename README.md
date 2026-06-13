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

## MCP Endpoint Requirements

MCP uses JSON-RPC method names for protocol endpoints. For Streamable HTTP, those JSON-RPC messages are sent through the HTTP `/mcp` endpoint; `/rpc` and `/events` are Voltaic compatibility endpoints.

Every MCP connection starts with the lifecycle methods:

- `initialize` - required first request. The client sends its supported protocol version, capabilities, and client info; the server responds with the negotiated protocol version, capabilities, and server info.
- `notifications/initialized` - required client notification after successful initialization. Normal operation starts after this notification.

Base utility methods:

- `ping` - utility request that either side may send to check liveness. A receiver must respond promptly when it receives one.

Streamable HTTP transport requirements:

- `POST /mcp` receives JSON-RPC requests and notifications.
- `GET /mcp` opens the SSE stream when the client wants server notifications.
- Session-aware clients send `MCP-Session-Id` after the server creates a session.
- After initialization, HTTP clients send `MCP-Protocol-Version` on subsequent requests.

Server feature endpoints are capability-driven. If your server advertises a capability, it must support the corresponding methods:

- `tools` capability: `tools/list` and `tools/call`.
- `resources` capability: `resources/list` and `resources/read`. If resource templates are exposed, also support `resources/templates/list`. If `resources.subscribe` is advertised, also support `resources/subscribe`, `resources/unsubscribe`, and `notifications/resources/updated`.
- `prompts` capability: `prompts/list` and `prompts/get`.
- `completions` capability: `completion/complete` for prompt argument or resource-template argument suggestions.
- `logging` capability: `logging/setLevel` from client to server, plus server `notifications/message` when logs are emitted.

Voltaic registers the protocol methods for its MCP server types when default methods are enabled. Your application registers the handlers and data behind those methods with `RegisterTool`, `RegisterResource`, `RegisterResourceTemplate`, `RegisterPrompt`, and `RegisterCompletionProvider`.

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

This example creates a small MCP HTTP server with a tool, resource, resource template, prompt, and completion provider. The client connects with `McpHttpClient`, performs the MCP initialization flow, and calls the endpoint families those handlers power.

Create the server:

```bash
dotnet new console -n CalculatorServer
cd CalculatorServer
dotnet add package Voltaic
```

Replace `Program.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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

server.RegisterResource(
    "voltaic://calculator/status",
    "status",
    "text/plain",
    () => new McpReadResourceResult
    {
        Contents = new List<object>
        {
            new McpTextResourceContents
            {
                Uri = "voltaic://calculator/status",
                MimeType = "text/plain",
                Text = "Calculator server is running."
            }
        }
    });

server.RegisterResourceTemplate(
    "voltaic://calculator/help/{topic}",
    "help-topic",
    "text/plain",
    uri => new McpReadResourceResult
    {
        Contents = new List<object>
        {
            new McpTextResourceContents
            {
                Uri = uri,
                MimeType = "text/plain",
                Text = $"Help content for {uri}."
            }
        }
    });

server.RegisterPrompt(
    "explain",
    "Creates an explanation prompt",
    new[]
    {
        new McpPromptArgument
        {
            Name = "topic",
            Description = "Topic to explain",
            Required = true
        }
    },
    args =>
    {
        string topic = args.HasValue && args.Value.TryGetProperty("topic", out JsonElement topicEl)
            ? topicEl.GetString() ?? "the topic"
            : "the topic";

        return new McpGetPromptResult
        {
            Messages = new List<McpPromptMessage>
            {
                new McpPromptMessage
                {
                    Role = "user",
                    Content = new McpTextContent
                    {
                        Text = $"Explain {topic} with a calculator example."
                    }
                }
            }
        };
    });

server.RegisterCompletionProvider(
    "ref/prompt",
    "explain",
    "topic",
    (request, token) => Task.FromResult(new McpCompleteResult
    {
        Completion = new McpCompletion
        {
            Values = new List<string> { "addition", "subtraction", "multiplication", "division" }
                .Where(value => value.StartsWith(request.Argument.Value, StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .ToList()
        }
    }));

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

JsonRpcResponse resources = await client.CallAsync("resources/list");
Console.WriteLine(resources.Result);

JsonRpcResponse templates = await client.CallAsync("resources/templates/list");
Console.WriteLine(templates.Result);

JsonRpcResponse prompts = await client.CallAsync("prompts/list");
Console.WriteLine(prompts.Result);

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

JsonRpcResponse resource = await client.CallAsync("resources/read", new
{
    uri = "voltaic://calculator/status"
});
Console.WriteLine(resource.Result);

JsonRpcResponse prompt = await client.CallAsync("prompts/get", new
{
    name = "explain",
    arguments = new
    {
        topic = "addition"
    }
});
Console.WriteLine(prompt.Result);

JsonRpcResponse completion = await client.CallAsync("completion/complete", new
{
    @ref = new
    {
        type = "ref/prompt",
        name = "explain"
    },
    argument = new
    {
        name = "topic",
        value = "ad"
    }
});
Console.WriteLine(completion.Result);

await client.CallAsync("logging/setLevel", new { level = "info" });
await client.CallAsync("resources/subscribe", new { uri = "voltaic://calculator/status" });
await client.CallAsync("resources/unsubscribe", new { uri = "voltaic://calculator/status" });
```

Run it:

```bash
dotnet run
```

`McpHttpClient` automatically uses the required Streamable HTTP headers, including `Accept: application/json, text/event-stream` and the `MCP-Session-Id` returned by the server. Call `StartSseAsync()` after `ConnectStreamableAsync()` if the client also needs server-sent notifications.

The server registrations above are the handlers behind the MCP endpoints:

- `RegisterTool(...)` backs `tools/list` and `tools/call`.
- `RegisterResource(...)` backs `resources/list` and `resources/read`.
- `RegisterResourceTemplate(...)` backs `resources/templates/list` and template-based `resources/read`.
- `RegisterPrompt(...)` backs `prompts/list` and `prompts/get`.
- `RegisterCompletionProvider(...)` backs `completion/complete`.
- `initialize`, `notifications/initialized`, `notifications/cancelled`, `resources/subscribe`, `resources/unsubscribe`, `logging/setLevel`, and `ping` are built in when default methods are enabled.

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

### MCP Endpoint Handler Pattern

Use the same registration pattern with `McpServer`, `McpHttpServer`, `McpTcpServer`, and `McpWebsocketsServer`. Voltaic registers the protocol methods described in [MCP Endpoint Requirements](#mcp-endpoint-requirements); your handlers supply the application data returned by the capability-driven endpoint families. The snippet uses `System`, `System.Collections.Generic`, `System.Linq`, `System.Text.Json`, and `System.Threading.Tasks`.

```csharp
// tools/list and tools/call
server.RegisterTool(
    "add",
    "Adds two numbers",
    new
    {
        type = "object",
        properties = new
        {
            a = new { type = "number" },
            b = new { type = "number" }
        },
        required = new[] { "a", "b" }
    },
    args =>
    {
        double a = args?.TryGetProperty("a", out JsonElement aEl) == true ? aEl.GetDouble() : 0;
        double b = args?.TryGetProperty("b", out JsonElement bEl) == true ? bEl.GetDouble() : 0;
        return (object)(a + b);
    });

// resources/list and resources/read
server.RegisterResource("voltaic://example/status", "status", "text/plain",
    () => new McpReadResourceResult
    {
        Contents = new List<object>
        {
            new McpTextResourceContents
            {
                Uri = "voltaic://example/status",
                MimeType = "text/plain",
                Text = "Service is running."
            }
        }
    });

// resources/templates/list and template-based resources/read
server.RegisterResourceTemplate("voltaic://example/{name}", "example-item", "text/plain",
    uri => new McpReadResourceResult
    {
        Contents = new List<object>
        {
            new McpTextResourceContents
            {
                Uri = uri,
                MimeType = "text/plain",
                Text = $"Dynamic resource for {uri}."
            }
        }
    });

// prompts/list and prompts/get
server.RegisterPrompt("summarize", "Creates a summary prompt",
    new[] { new McpPromptArgument { Name = "topic", Required = true } },
    args => new McpGetPromptResult
    {
        Messages = new List<McpPromptMessage>
        {
            new McpPromptMessage
            {
                Role = "user",
                Content = new McpTextContent { Text = "Summarize the requested topic." }
            }
        }
    });

// completion/complete
server.RegisterCompletionProvider("ref/prompt", "summarize", "topic",
    (request, token) => Task.FromResult(new McpCompleteResult
    {
        Completion = new McpCompletion
        {
            Values = new List<string> { "Voltaic", "MCP", "JSON-RPC" }
                .Where(value => value.StartsWith(request.Argument.Value, StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .ToList()
        }
    }));
```

`initialize`, `notifications/initialized`, `notifications/cancelled`, `resources/subscribe`, `resources/unsubscribe`, `logging/setLevel`, and `ping` are built in when default methods are enabled.

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
// - resources/list, resources/templates/list, resources/read
// - resources/subscribe, resources/unsubscribe
// - prompts/list, prompts/get
// - completion/complete
// - logging/setLevel
// - notifications/initialized (handles client init notification)
// - notifications/cancelled
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

## API Surface

All public types are in the `Voltaic` namespace. The main entry points are:

- `JsonRpcServer` / `JsonRpcClient` for TCP JSON-RPC 2.0.
- `McpServer` / `McpClient` for stdio MCP servers and subprocess clients.
- `McpHttpServer` / `McpHttpClient` for HTTP MCP, including Streamable HTTP on `/mcp`.
- `McpTcpServer` / `McpTcpClient` for MCP over TCP framing.
- `McpWebsocketsServer` / `McpWebsocketsClient` for MCP over WebSockets.

The core server pattern is the same across transports: configure identity, register methods/tools/resources/prompts/completions, subscribe to lifecycle events if needed, then start the server. Clients connect, call JSON-RPC methods with `CallAsync`, send notifications with `NotifyAsync`, and dispose when finished.

For exact overloads and model types, use your IDE's IntelliSense, the generated XML documentation in `src/Voltaic/Voltaic.xml`, and the sample/test projects listed above. `src/Test.Shared/API_COVERAGE.md` tracks the public API areas covered by the Touchstone suite.

---

## License

Voltaic is released under the [MIT License](LICENSE.md). Use it freely in your projects, commercial or otherwise.

---

## Support

Need help or found a bug?

- **Issues**: Report bugs or request features at [github.com/jchristn/voltaic/issues](https://github.com/jchristn/voltaic/issues)
- **Discussions**: Ask questions and share ideas at [github.com/jchristn/voltaic/discussions](https://github.com/jchristn/voltaic/discussions)
