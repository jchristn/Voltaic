<div align="center">
  <img src="assets/logo.png" alt="Voltaic Logo" width="192" height="192">
</div>

# Voltaic

[![NuGet](https://img.shields.io/nuget/v/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![Downloads](https://img.shields.io/nuget/dt/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.md) [![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Modern, lightweight JSON-RPC 2.0, Model Context Protocol (MCP), and Agent2Agent (A2A) implementations for .NET 8.0 and .NET 10.0**

Voltaic gives .NET applications a small, direct way to expose and consume structured agent protocols. Use it when you need JSON-RPC 2.0, MCP tools/resources/prompts, or A2A agents without adopting a larger application framework.

Voltaic v0.4.0 targets MCP protocol version `2025-11-25` and A2A protocol version `1.0`. The public API and source tree are split into `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A`.

---

## What Is Voltaic?

Voltaic is a protocol library, not an application framework. It provides:

- JSON-RPC 2.0 clients and servers over TCP with LSP-style `Content-Length` framing
- MCP stdio servers and clients for subprocess-hosted tools
- MCP Streamable HTTP on `/mcp` with `MCP-Session-Id` sessions and SSE notifications
- MCP TCP and WebSocket transports for networked or full-duplex scenarios
- A2A Agent Card discovery, JSON-RPC, HTTP+JSON, gRPC, SSE streaming, task lifecycle, push notification config APIs, and extended Agent Cards
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
- Expose and consume A2A agents through dependency-light `A2AClient`, `A2AHttpJsonClient`, `A2AGrpcClient`, `A2AHttpServer`, and `A2AGrpcServer` classes without ASP.NET Core.
- Run the same 274-case Touchstone suite through console, xUnit, and NUnit projects under `src/`.

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

## A2A Endpoint Requirements

A2A support lives in the `Voltaic.A2A` namespace. It follows the A2A v1.0 JSON wire shape used by the official `a2a-dotnet` SDK while keeping Voltaic's direct, dependency-light style.

- Public Agent Card discovery: `GET /.well-known/agent-card.json`.
- Version header sent by Voltaic clients: `A2A-Version: 1.0`.
- JSON-RPC endpoint: configurable, default `/a2a`.
- JSON-RPC methods: `SendMessage`, `SendStreamingMessage`, `GetTask`, `ListTasks`, `CancelTask`, `SubscribeToTask`, push notification config CRUD, and `GetExtendedAgentCard`.
- HTTP+JSON routes: `POST /message:send`, `POST /message:stream`, `GET /tasks/{id}`, `GET /tasks`, `POST /tasks/{id}:cancel`, `POST /tasks/{id}:subscribe`, `/tasks/{id}/pushNotificationConfigs`, and `GET /extendedAgentCard`.
- gRPC service: `lf.a2a.v1.A2AService` over HTTP/2, with unary and server-streaming RPCs matching the A2A v1 service shape.
- Streaming methods use SSE with `data:` events. JSON-RPC streaming sends JSON-RPC response envelopes; HTTP+JSON streaming sends direct `StreamResponse` payloads.

`A2AHttpServer` is built on `HttpListener`, not ASP.NET Core. It hosts Agent Card discovery, JSON-RPC, HTTP+JSON, SSE streams, task projection, in-memory task storage, push notification configuration storage, CORS, and an optional authentication hook. Applications provide agent behavior through `IA2AAgentHandler`.

`A2AGrpcServer` is built on the Watson HTTP/2 server package, not ASP.NET Core. `A2AGrpcClient` uses plain `HttpClient` with gRPC framing and protobuf messages.

## Why Use Voltaic?

- **Small API surface**: Register handlers and start a transport; avoid framework-level ceremony.
- **Current MCP coverage**: Tools, resources, prompts, completions, Streamable HTTP, sessions, and utility notifications are first-class.
- **A2A coverage**: Agent Cards, JSON-RPC, HTTP+JSON, gRPC, task lifecycle, streaming, push config APIs, and extended Agent Cards are first-class.
- **Transport choice**: Use stdio for local MCP servers, Streamable HTTP for MCP clients and inspectors, TCP for service-to-service RPC, WebSockets for full-duplex web-facing systems, or HTTP/SSE/gRPC for A2A agents.
- **Plain .NET**: Works with normal C# delegates, `System.Text.Json`, `Task`, `CancellationToken`, and `IDisposable`.
- **Testable behavior**: Protocol behavior is covered by shared Touchstone descriptors and adapter-backed test projects.

## Who Is This For?

Voltaic is designed for developers building:

- AI assistant integrations that need to expose MCP tools, resources, prompts, or completions from .NET.
- Services that need structured JSON-RPC calls, REST-style HTTP+JSON routes, or a small gRPC binding without adopting a large hosting framework.
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

### A2A Server Example

```csharp
using Voltaic.A2A;

string baseUrl = "http://localhost:8080";

AgentCard card = new AgentCard
{
    Name = "Echo Agent",
    Description = "A simple Voltaic A2A agent.",
    Version = "1.0.0",
    SupportedInterfaces = new List<AgentInterface>
    {
        new AgentInterface { Url = baseUrl + "/a2a", ProtocolBinding = "JSONRPC" },
        new AgentInterface { Url = baseUrl, ProtocolBinding = "HTTP+JSON" },
        new AgentInterface { Url = "http://localhost:8081", ProtocolBinding = "GRPC" }
    },
    Capabilities = new AgentCapabilities
    {
        Streaming = true,
        PushNotifications = true,
        StateTransitionHistory = true,
        ExtendedAgentCard = true
    },
    Skills = new List<AgentSkill>
    {
        new AgentSkill { Id = "echo", Name = "Echo", Description = "Echoes text." }
    },
    DefaultInputModes = new List<string> { "text/plain" },
    DefaultOutputModes = new List<string> { "text/plain" }
};

using A2AHttpServer server = new A2AHttpServer("localhost", 8080, card, new EchoAgent())
{
    ExtendedAgentCard = card
};
using A2AGrpcServer grpcServer = new A2AGrpcServer("localhost", 8081, card, new EchoAgent())
{
    ExtendedAgentCard = card
};

await server.StartAsync();
await grpcServer.StartAsync();
Console.WriteLine("A2A server listening on http://localhost:8080");
Console.WriteLine("A2A gRPC listening on http://localhost:8081");
await Task.Delay(Timeout.Infinite);

sealed class EchoAgent : IA2AAgentHandler
{
    public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
    {
        A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.SubmitAsync(token: token);
        await updater.StartAsync(token: token);

        string text = context.Message.Parts.FirstOrDefault()?.Text ?? string.Empty;
        Message response = new Message
        {
            Role = Role.Agent,
            MessageId = Guid.NewGuid().ToString("N"),
            TaskId = context.TaskId,
            ContextId = context.ContextId,
            Parts = new List<Part> { Part.FromText("echo: " + text) }
        };

        await updater.CompleteAsync(response, token);
    }
}
```

### A2A Client Example

```csharp
using Voltaic.A2A;

using HttpClient http = new HttpClient();
using A2ACardResolver resolver = new A2ACardResolver(http);
AgentCard card = await resolver.GetAgentCardAsync("http://localhost:8080");

AgentInterface jsonRpc = card.SupportedInterfaces.First(item => item.ProtocolBinding == "JSONRPC");
using A2AClient client = new A2AClient(jsonRpc.Url, http);

SendMessageRequest request = new SendMessageRequest
{
    Message = new Message
    {
        Role = Role.User,
        MessageId = Guid.NewGuid().ToString("N"),
        Parts = new List<Part> { Part.FromText("hello") }
    }
};

SendMessageResponse response = await client.SendMessageAsync(request);
Console.WriteLine(response.Task?.Status.State);

await foreach (StreamResponse item in client.SendStreamingMessageAsync(request))
{
    Console.WriteLine(item.StatusUpdate?.Status.State);
}

AgentInterface httpJson = card.SupportedInterfaces.First(item => item.ProtocolBinding == "HTTP+JSON");
using A2AHttpJsonClient restClient = new A2AHttpJsonClient(httpJson.Url, http);
SendMessageResponse restResponse = await restClient.SendMessageAsync(request);
Console.WriteLine(restResponse.Task?.Id);

AgentInterface? grpc = card.SupportedInterfaces.FirstOrDefault(item => item.ProtocolBinding == "GRPC");
if (grpc != null)
{
    AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    using HttpClient grpcHttp = new HttpClient(new SocketsHttpHandler { EnableMultipleHttp2Connections = true });
    using A2AGrpcClient grpcClient = new A2AGrpcClient(grpc.Url, grpcHttp);
    SendMessageResponse grpcResponse = await grpcClient.SendMessageAsync(request);
    Console.WriteLine(grpcResponse.Task?.Id);
}
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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
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

### MCP Client (Streamable HTTP)

```csharp
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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
using Voltaic.Core;
using Voltaic.Mcp;

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

- **Advanced gRPC Ecosystem Features**: If you need code-first service hosting, interceptors, advanced load balancing, or broad gRPC framework integration, use a dedicated gRPC stack
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
- **Sample.A2AServer**: A2A Agent Card, JSON-RPC, HTTP+JSON, gRPC, streaming, push config, and extended-card sample
- **Test.A2AServer**: Manual A2A server harness with JSON-RPC, HTTP+JSON, gRPC, task inspection, and push config commands
- **Test.A2AClient**: Manual A2A client for Agent Card discovery, JSON-RPC, HTTP+JSON, gRPC, streaming, and push config calls
- **Test.Shared**: Shared Touchstone descriptors and the central 274-case API/protocol matrix
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

# A2A sample server
# Starts JSON-RPC and HTTP+JSON on the selected port, and gRPC on port + 1
dotnet run --project src/Sample.A2AServer/Sample.A2AServer.csproj -- 8080

# A2A manual server
# Starts JSON-RPC and HTTP+JSON on the selected port, and gRPC on port + 1
dotnet run --project src/Test.A2AServer/Test.A2AServer.csproj -- 8080

# A2A manual client
dotnet run --project src/Test.A2AClient/Test.A2AClient.csproj -- http://localhost:8080 "hello from A2A"
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

# The shared suite currently projects 274 cases through the console, xUnit, and NUnit runners

# Export Touchstone JSON results
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net8.0 -- --results artifacts/test-results/voltaic-touchstone.json

# Filter by descriptor tag
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net8.0 -- --tag mcp
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net8.0 -- --tag a2a
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net8.0 -- --tag compatibility

# Run adapter-backed tests
dotnet test src/Test.Xunit/Test.Xunit.csproj --framework net8.0
dotnet test src/Test.Nunit/Test.Nunit.csproj --framework net8.0

# Cross-target the console runner
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net10.0
```

---

## API Surface

Public types are grouped by protocol namespace:

- `Voltaic.Core`: `JsonRpcServer`, `JsonRpcClient`, JSON-RPC request/response/error models, TCP framing, connection models, and shared authentication/error helpers.
- `Voltaic.Mcp`: MCP stdio, HTTP, TCP, and WebSocket clients/servers plus MCP tools, resources, prompts, completions, capabilities, and utility models.
- `Voltaic.A2A`: A2A Agent Cards, task/message/artifact models, `A2AClient`, `A2AHttpJsonClient`, `A2ACardResolver`, `A2AHttpServer`, task storage, event queue, updater, and protocol errors.

The library source mirrors those namespaces:

- `src/Voltaic/Core`: shared JSON-RPC, framing, connection, authentication, and event types.
- `src/Voltaic/Mcp`: MCP protocol models, endpoint infrastructure, clients, and servers.
- `src/Voltaic/A2A`: A2A protocol models, clients, servers, task infrastructure, JSON helpers, and gRPC wire support.
- `src/Voltaic/A2A/Protos`: the A2A protobuf contract used by the internal gRPC binding.

The core server pattern is the same across protocols: configure identity, register handlers or capabilities, subscribe to lifecycle events if needed, then start the server. Clients connect, call protocol methods, stream SSE events where supported, and dispose when finished.

For exact overloads and model types, use your IDE's IntelliSense, the generated XML documentation in `src/Voltaic/Voltaic.xml`, and the sample/test projects listed above. `src/Test.Shared/API_COVERAGE.md` tracks the public API areas covered by the Touchstone suite.

---

## License

Voltaic is released under the [MIT License](LICENSE.md). Use it freely in your projects, commercial or otherwise.

---

## Support

Need help or found a bug?

- **Issues**: Report bugs or request features at [github.com/jchristn/voltaic/issues](https://github.com/jchristn/voltaic/issues)
- **Discussions**: Ask questions and share ideas at [github.com/jchristn/voltaic/discussions](https://github.com/jchristn/voltaic/discussions)
