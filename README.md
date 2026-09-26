<div align="center">
  <img src="assets/logo.png" alt="Voltaic Logo" width="192" height="192">
</div>

# Voltaic

[![NuGet](https://img.shields.io/nuget/v/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![Downloads](https://img.shields.io/nuget/dt/Voltaic.svg)](https://www.nuget.org/packages/Voltaic/) [![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.md) [![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Modern, lightweight JSON-RPC 2.0, Model Context Protocol (MCP), and Agent2Agent (A2A) implementations for .NET 8.0 and .NET 10.0**

Voltaic gives .NET applications a small, direct way to expose and consume structured agent protocols. Use it when you need JSON-RPC 2.0, MCP tools/resources/prompts, or A2A agents without adopting a larger application framework.

Voltaic v2.1.4 recognizes five MCP protocol revisions (`2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, and the stateless `2026-07-28`) and targets A2A protocol version `1.0`. The public API and source tree are split into `Voltaic.Core`, `Voltaic.Mcp`, and `Voltaic.A2A`.

An `initialize` handshake negotiates at most the newest handshake-era revision, `2025-11-25` (configurable with `MaximumHandshakeProtocolVersion`), because the stateless `2026-07-28` revision defines no `initialize` and no sessions. Clients reach `2026-07-28` through the stateless request path instead: `server/discover` followed by per-request `MCP-Protocol-Version`, `Mcp-Method`, and `_meta` signals. That is how current clients such as Claude Code 2.1.x connect, and Voltaic's stateless responses carry the `resultType`, `ttlMs`, and `cacheScope` fields the revision requires. Version selection is driven by the `McpProtocol` registry and `McpVersionResolver`, and it behaves identically with or without an `AuthenticationHandler`. See [Protocol version negotiation](#protocol-version-negotiation).

> **v2.1.4** closes the remaining MCP specification deviations: every Voltaic client answers requests the server sends it (`ping` included) and can register handlers for others, tool handler exceptions become tool execution errors the model can read, `x-mcp-header` tool parameters are validated by servers and mirrored by `McpHttpClient`, `GET /mcp` streams send a priming event and resume with `Last-Event-ID`, `McpHttpClient` reconnects and resumes SSE streams, and `ping` no longer skips authentication or the session requirement. See [Upgrading to v2.1.4](#upgrading-to-v214).
>
> **v2.1.1** is a spec-conformance patch: JSON-RPC responses POSTed by a client get `202`, header-less requests assume `2025-03-26`, stateless requests must carry their `_meta` protocol version, and `McpHttpServer` can publish OAuth protected resource metadata. See [Specification conformance](#specification-conformance).
>
> **Upgrading to v2.1.0?** v2.1.0 is a security release with safer defaults. HTTP and WebSocket servers validate the browser `Origin` (foreign origins get 403), a server bound to `localhost` serves loopback clients only, Streamable HTTP sessions are created only by a successful `initialize` (sessionless requests get 400, unknown session IDs get 404), `McpWebsocketsServer` gains an `AuthenticationHandler`, and the TCP transports reject anything that is not LSP-style framing. See [Security defaults](#security-defaults) and [Upgrading to v2.1.0](#upgrading-to-v210).
>
> **Upgrading from v1.x?** v2.0.0 is a breaking release. MCP servers no longer publish Voltaic's demo tools (`ping`, `echo`, `getTime`, `getSessions`/`getClients`) unless you opt in, `ping` returns `{}` as the MCP specification requires, tools are callable only through `tools/call`, and tool input schemas enforce `additionalProperties`. See [Upgrading from v1.x](#upgrading-from-v1x) and [MIGRATE_V1_TO_V2.md](MIGRATE_V1_TO_V2.md).

---

## What Is Voltaic?

Voltaic is a protocol library, not an application framework. It provides:

- JSON-RPC 2.0 clients and servers over TCP with LSP-style `Content-Length` framing (MCP over TCP also accepts newline-delimited JSON)
- MCP stdio servers and clients for subprocess-hosted tools
- MCP Streamable HTTP on `/mcp` with `MCP-Session-Id` sessions and SSE notifications
- MCP TCP and WebSocket transports for networked or full-duplex scenarios
- A2A Agent Card discovery, JSON-RPC, HTTP+JSON, gRPC, SSE streaming, task lifecycle, push notification config APIs, and extended Agent Cards
- Shared request/response, notification, lifecycle, and event handling across transports

You bring your business logic. Voltaic handles the protocol surface, message framing, method dispatch, session headers, and transport-specific plumbing.

## What Can It Do?

- Register JSON-RPC methods with synchronous, asynchronous, or cancellation-aware handlers.
- Register MCP tools with input schema metadata, output schema metadata, structured content, annotations, icons, and full `McpToolCallResult` returns.
- Validate common JSON Schema cases (`type`, `required`, nested `properties`, `patternProperties`, and `additionalProperties`) for tool input and structured output.
- Expose MCP resources, resource templates, prompts, and completion providers.
- Handle MCP `initialize`, `tools/list`, `tools/call`, `resources/*`, `prompts/*`, `completion/complete`, `logging/setLevel`, and utility notifications.
- Send list-changed, resource-updated, progress, cancellation, and log-message notifications where the transport supports server-to-client notifications.
- Host Voltaic's own request/response endpoints (`/rpc` and `/events`) alongside the Streamable HTTP endpoint (`/mcp`). They are a Voltaic convenience, not the deprecated 2024-11-05 HTTP+SSE transport.
- Expose and consume A2A agents through dependency-light `A2AClient`, `A2AHttpJsonClient`, `A2AGrpcClient`, `A2AHttpServer`, and `A2AGrpcServer` classes without ASP.NET Core.
- Answer MCP requests from handshake-era and stateless `2026-07-28` clients on the same server, on every transport, including Multi Round-Trip input requests from tool handlers.
- Run the same 572-case Touchstone suite through console, xUnit, and NUnit projects under `src/`.

## MCP Endpoint Requirements

MCP uses JSON-RPC method names for protocol endpoints. For Streamable HTTP, those JSON-RPC messages are sent through the HTTP `/mcp` endpoint. `/rpc` (plain request/response JSON-RPC) and `/events` (an SSE stream for a session opened on `/rpc`) are Voltaic's own endpoints for simple clients and scripts. They are not the 2024-11-05 HTTP+SSE transport: `/events` sends no `endpoint` event, and results come back in the POST response.

Every MCP connection starts with the lifecycle methods:

- `initialize` - required first request for handshake-era revisions. The client sends its supported protocol version, capabilities, and client info; the server responds with the negotiated protocol version, capabilities, and server info. See [Protocol version negotiation](#protocol-version-negotiation) for the version the server answers with.
- `notifications/initialized` - required client notification after successful initialization. Normal operation starts after this notification.

Base utility methods:

- `ping` - utility request that either side may send to check liveness. A receiver must respond promptly when it receives one. Voltaic MCP servers answer with the empty result `{}`. `2026-07-28` removed `ping`; a stateless request for it gets `-32601`.

Streamable HTTP transport requirements:

- `POST /mcp` receives JSON-RPC requests and notifications.
- `GET /mcp` opens the SSE stream when the client wants server notifications.
- A successful `initialize` creates the session and returns `MCP-Session-Id`; clients send it on every later request. A request without it (other than `initialize`) gets 400, and an ID the server did not issue, has expired, or was terminated gets 404, which tells the client to re-initialize. See [Sessions](#sessions).
- After initialization, HTTP clients send `MCP-Protocol-Version` on subsequent requests. When the header is absent and the session has no negotiated version, the server assumes `2025-03-26`, as the specification requires.
- A JSON-RPC response or error POSTed by the client is accepted with `202` and no body.

Server feature endpoints are capability-driven. If your server advertises a capability, it must support the corresponding methods:

- `tools` capability: `tools/list` and `tools/call`.
- `resources` capability: `resources/list` and `resources/read`. If resource templates are exposed, also support `resources/templates/list`. If `resources.subscribe` is advertised, also support `resources/subscribe`, `resources/unsubscribe`, and `notifications/resources/updated`.
- `prompts` capability: `prompts/list` and `prompts/get`.
- `completions` capability: `completion/complete` for prompt argument or resource-template argument suggestions.
- `logging` capability: `logging/setLevel` from client to server, plus server `notifications/message` when logs are emitted.

Voltaic always registers the protocol methods for its MCP server types, including `ping`. Your application registers the handlers and data behind those methods with `RegisterTool`, `RegisterResource`, `RegisterResourceTemplate`, `RegisterPrompt`, and `RegisterCompletionProvider`.

### Tools the server publishes

An MCP server publishes only the tools your application registers. `tools/list` shows exactly those tools, so an AI client's model sees only your product's tools.

- **Diagnostic tools are opt-in.** Pass `includeDiagnosticTools: true` to a server constructor to also publish `echo` and `getTime`. They are useful while developing a server and are off by default. Voltaic no longer ships a `getSessions`/`getClients` tool, because it disclosed other clients' session identifiers.
- **Tools are invoked only through `tools/call`.** `tools/call` validates arguments against the tool's input schema before your handler runs. A tool is not callable as a bare JSON-RPC method, so the schema check cannot be skipped. A tool may share a name with a protocol method (for example, a tool named `ping`) without replacing it.
- **Tools can be removed.** `UnregisterTool(name)` removes a tool and returns `true` if it existed. Registering or unregistering a tool, or registering a resource, resource template, or prompt, sends `notifications/tools/list_changed` (or the resources and prompts equivalent) to clients that completed `initialize`; `NotifyToolsChanged()` (HTTP) and `NotifyToolsChangedAsync()` (stdio, TCP, WebSocket) send it on demand, for example when a tool's behavior changes.

```csharp
McpHttpServer server = new McpHttpServer("localhost", 8080);                               // only your tools
McpHttpServer devServer = new McpHttpServer("localhost", 8081, includeDiagnosticTools: true); // plus echo and getTime

server.RegisterTool("lookup", "Looks up a record", schema, handler);
server.UnregisterTool("lookup");
server.NotifyToolsChanged();
```

Tool input schemas are enforced for `type`, `required`, nested `properties`, `patternProperties`, and `additionalProperties`. With `additionalProperties: false`, a `tools/call` that sends an argument the schema does not declare gets a tool result with `isError: true` and a message naming the property; the handler does not run.

A handler that throws is reported as a tool execution error (a result with `isError: true`), as the specification prescribes for API and business-logic failures, so the model can react. Because tool outputs must be sanitized, the text is a generic `Tool '<name>' failed because of an internal error.` by default, and the exception type and message go to the server's `Log` event. To tell the model what went wrong, throw `McpToolException("City 'Atlantis' was not found.")`: its message is used as the result text. Set `IncludeToolExceptionMessages = true` to send `Tool '<name>' failed: <message>` for every exception (only when messages never contain secrets). To send a JSON-RPC protocol error instead, throw `McpProtocolException` (for example `McpProtocolException.InvalidParams(...)`). A result that violates the tool's own `outputSchema` is a server fault and gets `-32603`. Cancellation of the request still propagates.

#### Header parameters (`x-mcp-header`, 2026-07-28)

A tool can ask stateless HTTP clients to mirror an argument into an `Mcp-Param-{Name}` header, so gateways can route on it without parsing the body. Add `x-mcp-header` to the property's schema:

```csharp
server.RegisterTool("execute_sql", "Runs SQL in a region", JsonDocument.Parse("""
{
  "type": "object",
  "properties": {
    "region": { "type": "string", "x-mcp-header": "Region" },
    "query":  { "type": "string" }
  },
  "required": ["region", "query"]
}
""").RootElement, handler);
```

`RegisterTool` rejects annotations the specification forbids: on anything other than a `string`, `integer`, or `boolean` property (a nullable form such as `["string", "null"]` is allowed); on a property not reachable from the root through `properties` alone (inside `items`, `oneOf`, `$defs`, and so on); duplicated case-insensitively; empty or not an HTTP token; or on an integer whose bounds exceed the JavaScript safe range. On a stateless `tools/call`, `McpHttpServer` requires the header whenever the argument has a value, decodes `=?base64?...?=` values, compares integers numerically, and answers a missing, extra, malformed, or different header, or an integer outside the JavaScript safe range, with HTTP 400 and `-32020` (HeaderMismatch) before the tool runs. An argument of the wrong type is left to input validation (an `isError` result). A malformed `Mcp-Name` value is also rejected with `-32020`. Handshake-era requests and non-HTTP transports ignore the annotation. `McpHttpClient` sends the headers automatically (see [MCP Client (Streamable HTTP)](#mcp-client-streamable-http)).

### Protocol version negotiation

MCP revisions fall into two eras. The handshake era (`2024-11-05` through `2025-11-25`) opens every connection with `initialize` and, on Streamable HTTP, an `MCP-Session-Id`. The stateless era (`2026-07-28`) has no `initialize` and no sessions: each request carries its own version in the `MCP-Protocol-Version` header and `_meta`, plus an `Mcp-Method` routing header, and clients learn what the server offers from `server/discover`.

Because `initialize` belongs to the handshake era, every Voltaic MCP server (HTTP, stdio, TCP, and WebSocket) answers it as follows:

| Client requests in `initialize` | Server answers |
|---|---|
| No version | `ProtocolVersion` (default `2025-11-25`), lowered to the cap if it is newer |
| A handshake-era version at or below the cap | That version |
| A handshake-era version above the cap | The cap |
| A stateless-era version (`2026-07-28`) | The cap |
| An unknown version (older, newer, or malformed) | The cap, as the specification requires ("respond with another protocol version it supports"); the client disconnects if it cannot use it |

The cap is `MaximumHandshakeProtocolVersion`. It defaults to `McpProtocol.NewestHandshakeProtocolVersion` (`2025-11-25`), accepts only handshake-era revisions, and throws `ArgumentException` for anything else. Lower it to pin clients to an older revision:

```csharp
McpHttpServer server = new McpHttpServer("localhost", 8080);
server.MaximumHandshakeProtocolVersion = McpProtocol.ProtocolVersion20250618;
```

Every Voltaic MCP server also serves `2026-07-28` without a handshake. On `McpHttpServer` that is the stateless HTTP path. On stdio, TCP, and WebSocket, a request is treated as `2026-07-28` when its `params._meta["io.modelcontextprotocol/protocolVersion"]` names that revision; `server/discover` is available, and an unknown version in `_meta` gets `-32022` with the supported list. Requests without that `_meta` field, and `initialize`, keep the handshake-era behavior, so one server serves both eras, deciding per request. Under that revision every result carries `resultType`: `complete` for a final result, `input_required` for a Multi Round-Trip result, or `task` for a created task. Cacheable results (`tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`, `resources/read`, and `server/discover`) also carry `ttlMs` and `cacheScope`. Voltaic fills these in on the way out. Values a handler sets itself are kept, as are `ListCacheTtlMs` and `ListCacheScope` when configured. Otherwise the defaults are `ttlMs: 0` and `cacheScope: "private"`, which mean "do not cache; the result is specific to the caller". Handshake-era responses never include these fields.

`server/discover` does not advertise `listChanged` or `resources.subscribe`. Under `2026-07-28` those notifications are delivered through `subscriptions/listen`, which Voltaic does not implement yet. Handshake-era sessions still advertise and receive them.

A method you register yourself with `RegisterMethod` gets the same treatment when it returns an `McpResult` subclass (for example `McpEmptyResult` or `McpToolCallResult`). Any other object result, such as an anonymous type, also gets `resultType` and the server identity in `_meta`. A result must be a JSON object on every revision: a handler that returns a string, number, or array produces `-32603`, and one that returns null answers `{}`.

### Progress and logging from a tool

A tool handler reports progress and log entries through `McpToolCallContext.Current`, and they reach only the client that made the call:

```csharp
server.RegisterTool("import", "Imports files", new { type = "object" }, async (RpcParameters? args, CancellationToken token) =>
{
    McpToolCallContext call = McpToolCallContext.Current!;
    for (int i = 1; i <= 10; i++)
    {
        await ImportBatchAsync(i, token);
        await call.ReportProgressAsync(i, 10, $"batch {i} of 10", token);
    }

    await call.LogAsync("info", new { imported = 10 }, "import", token);
    return "done";
});
```

Progress is sent only when the request carried a `progressToken`, and each value must be larger than the last. Log entries are sent when they meet the level the client set with `logging/setLevel` (all entries until it sets one; or, on `2026-07-28`, the `io.modelcontextprotocol/logLevel` in the request's `_meta`; without it no log entries are sent). Over Streamable HTTP, the POST response becomes an SSE stream when the first notification is sent. A cancelled request (`notifications/cancelled`, or a closed `2026-07-28` response stream) cancels `token`.

### Asking the user for input (Multi Round-Trip Requests)

Under `2026-07-28` a tool can ask the client for more input, such as a confirmation, instead of finishing. The handler returns `McpInputRequiredResult`; the client gathers the answers and calls the tool again with `inputResponses` and the `requestState` the handler returned. `McpToolCallContext.Current` gives the handler the current call:

| Member | Meaning |
|---|---|
| `ToolName` | The tool being called |
| `InputResponses` | The client's answers, keyed like the handler's `InputRequests` (empty on the first call) |
| `RequestState` | The state the handler returned with its input request, echoed by the client (null on the first call) |
| `IsRetry` | True when the call carries input responses or request state |
| `CanRequestInput` | True when the request uses `2026-07-28`, so an `McpInputRequiredResult` can reach the client |

```csharp
server.RegisterTool("delete_file", "Deletes a file after confirmation", schema, args =>
{
    McpToolCallContext call = McpToolCallContext.Current!;
    string path = args?.GetString("path") ?? "";

    if (call.InputResponses.TryGetValue("confirm", out JsonElement answer)
        && answer.GetProperty("action").GetString() == "accept")
    {
        File.Delete(path);
        return McpToolCallResult.FromText($"Deleted {path}.");
    }

    if (!call.CanRequestInput)
    {
        McpToolCallResult unsupported = McpToolCallResult.FromText("Confirmation needs MCP 2026-07-28.");
        unsupported.IsError = true;
        return unsupported;
    }

    return new McpInputRequiredResult
    {
        InputRequests = new Dictionary<string, McpInputRequest>
        {
            ["confirm"] = new McpInputRequest
            {
                Method = "elicitation/create",
                Params = new { mode = "form", message = $"Delete {path}?", requestedSchema = new { type = "object", properties = new { } } }
            }
        },
        RequestState = "delete:" + path
    };
});
```

Handshake-era clients cannot receive an input request. If a handler returns `McpInputRequiredResult` for one anyway, Voltaic answers with a tool result with `isError: true` explaining that the tool needs `2026-07-28`, rather than sending a result the client cannot parse. `McpToolCallContext.Current` is null outside a tool call and is scoped to the call, including its awaited continuations. `McpHttpClient.CallToolStatelessAsync` drives the client side of the exchange; declare the kinds of input it can answer first (for example `client.ClientCapabilities["elicitation"] = new { };`), because a server may request only declared kinds and answers `-32021` otherwise.

## A2A Endpoint Requirements

A2A support lives in the `Voltaic.A2A` namespace. It follows the A2A v1.0 JSON wire shape used by the official `a2a-dotnet` SDK while keeping Voltaic's direct, dependency-light style.

- Public Agent Card discovery: `GET /.well-known/agent-card.json`.
- Version header sent by Voltaic clients: `A2A-Version: 1.0`.
- JSON-RPC endpoint: configurable, default `/a2a`.
- JSON-RPC methods: `SendMessage`, `SendStreamingMessage`, `GetTask`, `ListTasks`, `CancelTask`, `SubscribeToTask`, push notification config CRUD (`CreateTaskPushNotificationConfig`, `GetTaskPushNotificationConfig`, `ListTaskPushNotificationConfigs`, `DeleteTaskPushNotificationConfig`; the older singular `ListTaskPushNotificationConfig` is also accepted), and `GetExtendedAgentCard`.
- HTTP+JSON routes: `POST /message:send`, `POST /message:stream`, `GET /tasks/{id}`, `GET /tasks`, `POST /tasks/{id}:cancel`, `POST /tasks/{id}:subscribe`, `/tasks/{id}/pushNotificationConfigs`, and `GET /extendedAgentCard`.
- gRPC service: `lf.a2a.v1.A2AService` over HTTP/2, with unary and server-streaming RPCs matching the A2A v1 service shape.
- Streaming methods use SSE with `data:` events. JSON-RPC streaming sends JSON-RPC response envelopes; HTTP+JSON streaming sends direct `StreamResponse` payloads.

`A2AHttpServer` is built on `HttpListener`, not ASP.NET Core. It hosts Agent Card discovery, JSON-RPC, HTTP+JSON, SSE streams, task projection, in-memory task storage, push notification delivery, CORS, and an optional authentication hook. Applications provide agent behavior through `IA2AAgentHandler`.

`A2AGrpcServer` is built on the Watson HTTP/2 server package, not ASP.NET Core. It applies the same origin and loopback checks as `A2AHttpServer`, serves only the public Agent Card without authentication, and reports internal errors to clients generically (details go to its `Log` event). Its push notification settings (`PushNotificationUrlValidator`, `PushNotificationTimeoutMs`, `PushNotificationMaxAttempts`) match `A2AHttpServer`. `A2AGrpcClient` uses plain `HttpClient` with gRPC framing and protobuf messages.

A host name of `localhost` makes `A2AGrpcServer` listen on both `127.0.0.1` and `::1` (and `*`/`+` on both `0.0.0.0` and `::`). `HttpClient` and most other clients try `::1` first for `localhost`; with only an IPv4 listener each new connection would wait for the refused IPv6 attempt, about two seconds on Windows. The IPv6 listener is best effort: if it cannot bind, the server logs that and serves IPv4 only. An explicit address such as `127.0.0.1` gets that address only, so point clients at the same address.

JSON-RPC errors are returned with HTTP 200 and the JSON-RPC error body, the JSON-RPC-over-HTTP convention. `A2AClient` reads the JSON-RPC error body whatever the HTTP status, so it also works with servers that send 4xx. HTTP+JSON errors are a `google.rpc.Status` object, as A2A v1.0 specifies, with the A2A status mapping (404 for task not found, 500 for internal errors, 400 otherwise):

```json
{"error":{"code":404,"status":"NOT_FOUND","message":"Task not found","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"TASK_NOT_FOUND","domain":"a2a-protocol.org","metadata":{}}]}}
```

`A2AHttpJsonClient` maps the `ErrorInfo` reason back to the `A2AErrorCode`, and still reads the JSON-RPC-style error body that Voltaic 2.1.2 and earlier servers sent.

### Push notifications

When the Agent Card advertises `Capabilities.PushNotifications = true`, `A2AHttpServer` (and `A2AGrpcServer`, which shares its task engine) delivers every event of a task to each webhook registered for it, either through the push notification config operations or through `SendMessageConfiguration.PushNotificationConfig` (`taskPushNotificationConfig` on the wire). Each delivery is an HTTP `POST` with a `StreamResponse` body (`Content-Type: application/a2a+json`), `Authorization: {scheme} {credentials}` from the config's `authentication`, and `X-A2A-Notification-Token` when the config has a token. Deliveries to one webhook are sent in order, time out after `PushNotificationTimeoutMs` (default 10 seconds), and are retried with exponential backoff up to `PushNotificationMaxAttempts` (default 3).

Push notification configs use the flat A2A v1.0 shape on the wire (`id`, `taskId`, `url`, `token`, `authentication`, and `tenant` when set), and get/delete requests name the configuration with `id`. Voltaic also reads the nested `pushNotificationConfig`/`config` objects and the `configId` field that 2.1.2 and earlier wrote.

Webhook URLs are protected against server-side request forgery, as the A2A specification recommends. By default a URL must be `http` or `https` without user information, must not name `localhost` or a loopback, private, link-local, or carrier-grade NAT address, and is re-checked when the server connects, so a host name that resolves only to such addresses (including through DNS rebinding) is refused. Redirects are not followed and no proxy is used. Replace the policy with `PushNotificationUrlValidator`, for example to allow a list of webhook hosts, or loopback during local development:

```csharp
server.PushNotificationUrlValidator = uri => uri.Host == "hooks.example.com" || uri.IsLoopback;
```

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
    (RpcParameters? args) =>
    {
        double a = args?.GetDouble("a") ?? 0;
        double b = args?.GetDouble("b") ?? 0;

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
        string topic = args?.GetString("topic") ?? "the topic";

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

client.ClientName = "CalculatorClient";
client.ClientVersion = "1.0.0";

// Sends initialize and notifications/initialized, and stores the session.
await client.ConnectStreamableAsync("http://localhost:8080");

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
- `initialize`, `notifications/initialized`, `notifications/cancelled`, `resources/subscribe`, `resources/unsubscribe`, `logging/setLevel`, and `ping` are always registered.

---

## More Quick Starts

### JSON-RPC Server (TCP)

```csharp
using System.Net;
using System.Text.Json;
using Voltaic.Core;
using Voltaic.Mcp;

// Exposes only the methods you register. Pass includeDiagnosticMethods: true to also
// register the ping, echo, getTime, and add diagnostic methods.
JsonRpcServer server = new JsonRpcServer(IPAddress.Any, 8080);

// Subscribe to events
server.ClientConnected += (sender, client) =>
    Console.WriteLine($"Client connected: {client.SessionId}");

server.RequestReceived += (sender, e) =>
    Console.WriteLine($"Request: {e.Method} from {e.Client.SessionId}");

server.ResponseSent += (sender, e) =>
    Console.WriteLine($"Response: {e.Method} took {e.Duration.TotalMilliseconds}ms");

// Register a synchronous method
server.RegisterMethod("greet", (RpcParameters? args) =>
{
    string? name = args?.GetString("name") ?? "World";
    return $"Hello, {name}!";
});

// Register an asynchronous method (for I/O-bound work like DB queries, HTTP calls, etc.)
server.RegisterMethod("fetchData", async (RpcParameters? args) =>
{
    // Async handlers avoid blocking the thread pool
    await Task.Delay(100); // Simulate async work
    return (object)"async result";
});

// Register an async method with cancellation support
server.RegisterMethod("longRunningTask", async (RpcParameters? args, CancellationToken token) =>
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
        double a = args?.GetDouble("a") ?? 0;
        double b = args?.GetDouble("b") ?? 0;
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

`initialize`, `notifications/initialized`, `notifications/cancelled`, `resources/subscribe`, `resources/unsubscribe`, `logging/setLevel`, and `ping` are always registered.

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
    (RpcParameters? args) =>
    {
        double a = args?.GetDouble("a") ?? 0;
        double b = args?.GetDouble("b") ?? 0;
        return (object)(a + b);
    });

// Protocol methods are always registered:
// - initialize (returns capabilities and serverInfo)
// - ping (returns {})
// - tools/list (returns all registered tools)
// - tools/call (invokes a tool by name)
// - resources/list, resources/templates/list, resources/read
// - resources/subscribe, resources/unsubscribe
// - prompts/list, prompts/get
// - completion/complete
// - logging/setLevel
// - notifications/initialized (handles client init notification)
// - notifications/cancelled
//
// tools/list returns only the tools registered above. Construct with
// new McpServer(includeDiagnosticTools: true) to also publish echo and getTime.

// Run the server (reads from stdin, writes to stdout)
await server.RunAsync();
```

### MCP Client (stdio)

```csharp
using Voltaic.Core;
using Voltaic.Mcp;

McpClient client = new McpClient();

// Launch an MCP server as a subprocess; the client sends initialize and notifications/initialized
// (set AutoInitialize = false to do it yourself with InitializeAsync)
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
    (RpcParameters? args) =>
    {
        double a = args?.GetDouble("a") ?? 0;
        double b = args?.GetDouble("b") ?? 0;
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

// Connect to the TCP server; the client sends initialize and notifications/initialized.
// Messages are newline-delimited JSON; set NewlineDelimited = false for Voltaic servers before 2.1.5.
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
    (RpcParameters? args) =>
    {
        double a = args?.GetDouble("a") ?? 0;
        double b = args?.GetDouble("b") ?? 0;
        return (object)(a + b);
    });

// Start the server
await server.StartAsync();
Console.WriteLine("MCP HTTP server running on http://localhost:8080");
await Task.Delay(Timeout.Infinite, server.TokenSource.Token);
```

The default `McpHttpServer` listens on all three HTTP endpoints:
- `/mcp` for MCP Streamable HTTP (the specification's transport; use this for MCP clients)
- `/rpc` for Voltaic's plain request/response JSON-RPC (scripts, `curl`, `McpHttpClient.ConnectAsync`)
- `/events` for a Voltaic SSE notification stream tied to a session opened with `initialize` on `/rpc`

`/rpc` and `/events` are not the deprecated 2024-11-05 HTTP+SSE transport, so clients built for that transport cannot connect to them. Clients speaking `2024-11-05` connect over Streamable HTTP (`/mcp`), stdio, TCP, or WebSocket.

Set `mcpPath: null` in the constructor if you want to disable the Streamable HTTP endpoint.

The same `/mcp` endpoint serves both handshake-era clients (`initialize` plus a session) and stateless `2026-07-28` clients (`server/discover` plus per-request headers), such as Claude Code 2.1.x. No configuration is needed for either; see [Protocol version negotiation](#protocol-version-negotiation).

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

`ConnectStreamableAsync()` performs the MCP handshake: it sends `initialize` (requesting `client.ProtocolVersion` and reporting `ClientName`/`ClientVersion`), stores the session ID and the negotiated protocol version, and sends `notifications/initialized`. `ConnectAsync()` does the same on `/rpc`, and the session it receives is the one `/events` streams to. Call `StartSseAsync()` when you want the SSE notification stream to become active. Connecting fails (returns `false`) when `initialize` returns an error.

A Streamable HTTP server may answer a POST with `application/json` or with an SSE stream (`text/event-stream`), and the specification requires clients to accept both. `McpHttpClient` reads SSE responses as they arrive: notifications on the stream are raised through `NotificationReceived`, and the call completes with the response whose `id` matches the request. Server-to-client requests on the stream are logged and ignored.

On the handshake-era transport, `McpHttpClient` keeps SSE streams going the way the specification describes: it skips empty priming events, remembers each event ID, and when the server closes a GET stream (or the connection drops) it reopens it with `Last-Event-ID` after the server's `retry` interval, or `SseReconnectDelayMs` (default 1000) when the server sent none. An HTTP 4xx answer, such as 404 for an expired session or 405 from a server without a GET stream, stops reconnecting, as does `SseMaxReconnectAttempts` (default 5) consecutive failures. A POST response stream that ends after an event ID but before the response is resumed with a GET carrying `Last-Event-ID`. Set `AutoReconnectSse = false` to turn this off.

In stateless mode, `McpHttpClient` also handles `x-mcp-header` tool parameters: each `tools/list` result is checked, tool definitions with invalid annotations are removed from the result (a warning naming the tool goes to `Log`), and `tools/call` mirrors the annotated arguments into `Mcp-Param-{Name}` headers, Base64-encoding values that are not plain ASCII or have leading or trailing whitespace. If a server rejects a call with `-32020`, the client lists the tools again and retries once. `Mcp-Name` is taken from `params.name` or `params.uri` when no name is passed.

When `McpHttpClient` talks to a server over the stateless `2026-07-28` path, it treats a result with `resultType: "input_required"` as a Multi Round-Trip request and any other result as final. A result with no `resultType` is also treated as final, so the client keeps working against servers that predate the field.

### Answering requests from the server

MCP servers may send requests to clients: `ping` at any time, and, on the handshake-era revisions, `roots/list`, `sampling/createMessage`, and `elicitation/create`. Every Voltaic client (`McpClient`, `McpTcpClient`, `McpWebsocketsClient`, and `McpHttpClient`) answers them, as JSON-RPC requires: `ping` with `{}`, methods you register with `RegisterRequestHandler` with the handler's result, and anything else with `-32601` (method not found).

```csharp
McpHttpClient client = new McpHttpClient();
client.RegisterRequestHandler("roots/list", (args, token) =>
    Task.FromResult<object?>(new { roots = new[] { new { uri = "file:///home/me/project", name = "project" } } }));

await client.ConnectStreamableAsync("http://localhost:8080"); // declares the roots capability
await client.StartSseAsync();                                   // server requests arrive on the GET stream
```

Registering `roots/list`, `sampling/createMessage`, or `elicitation/create` on `McpHttpClient` declares the matching capability in `initialize` and in the `clientCapabilities` of every stateless request, so register handlers before connecting. A handler that throws `McpProtocolException` sends that error; any other exception sends `-32603` without its message. `ping` is reserved and always answered by the client. `McpHttpClient` sends each answer as a POST on the session. In stateless `2026-07-28` mode, where servers must use Multi Round-Trip input requests instead, requests on a stream are ignored. The plain `JsonRpcClient` answers server requests the same way but has no built-in `ping`.

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
    (RpcParameters? args) =>
    {
        double a = args?.GetDouble("a") ?? 0;
        double b = args?.GetDouble("b") ?? 0;
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

// Optional: credentials for a server with an AuthenticationHandler (sent on the upgrade request)
client.SetRequestHeader("Authorization", "Bearer " + token);

// Connect to the WebSocket server; the client sends initialize and notifications/initialized
await client.ConnectAsync("ws://localhost:8080/mcp");

// Call methods on the server
object? result = await client.CallAsync<object>("tools/list");
Console.WriteLine(result);
```

---

## Security defaults

Voltaic servers usually run on a developer workstation or next to data they expose, often without authentication. Since v2.1.0 the defaults assume a web page in the user's browser, or another host on the network, may try to reach them.

| Protection | Applies to | Default | How to change it |
|---|---|---|---|
| Browser `Origin` validation (MCP spec requirement; blocks cross-site calls and DNS rebinding) | `McpHttpServer`, `McpWebsocketsServer`, `A2AHttpServer` | Requests without `Origin` and loopback origins (`http(s)://localhost`, `127.0.0.0/8`, `[::1]`, any port) are allowed; every other origin gets 403 before preflight or authentication | `server.OriginPolicy.AllowedOrigins.Add("https://app.example.com")`, `AllowLoopbackOrigins`, or a custom `OriginValidator` |
| Browser `Origin` validation | `A2AGrpcServer` | Same rules; only the public Agent Card skips authentication | `OriginPolicy` |
| CORS | `McpHttpServer`, `A2AHttpServer` | The allowed origin is echoed (never `*`) with `Vary: Origin`. A preflight gets back the header names it asks for (valid tokens only), so custom headers such as `X-API-Key` or `Mcp-Param-*` work; otherwise an explicit `Access-Control-Allow-Headers` list that includes `Authorization` is sent. `WWW-Authenticate` is exposed so browser clients can read an OAuth challenge | `EnableCors`, `CorsHeaders` |
| Loopback-only clients for loopback binds | `McpHttpServer`, `McpWebsocketsServer`, `A2AHttpServer`, `A2AGrpcServer` | On when the host name is `localhost`, `127.x.x.x`, or `::1`: a request from any other address gets 403. On Windows, `HttpListener` serves a `localhost` prefix on every interface and routes by the spoofable `Host` header, so this check is what keeps it local | Bind to `+`/`*` or a specific address, or set `RestrictToLoopbackClients = false` |
| JSON bodies on `POST /mcp` | `McpHttpServer` | A `Content-Type` other than `application/json` gets 415. A request with no `Content-Type` is accepted only without an `Origin` header (a non-browser client), because a browser can send such a body without a CORS preflight. A missing `Accept` header counts as `*/*` | None needed |
| Webhook targets | `A2AHttpServer`, `A2AGrpcServer` | Push notification URLs must not target loopback, private, or link-local addresses; checked when the config is created and again when connecting | `PushNotificationUrlValidator` |
| Initialize-only sessions, principal binding | `McpHttpServer` | See [Sessions](#sessions) | None (since v2.1.4 `RequireInitializedSessions` is obsolete and ignored) |
| Strict framing | `JsonRpcServer`, `McpTcpServer`, `JsonRpcClient`, `McpTcpClient` | Only `Content-Length` and `Content-Type` header lines (1024 bytes at most) are accepted, and `McpTcpServer` also accepts newline-delimited JSON but closes the connection on a line that is not JSON, so an HTTP request from a browser `fetch()` is dropped before anything runs | None; Voltaic, LSP-style, and newline-delimited MCP clients send only those |

```csharp
McpHttpServer server = new McpHttpServer("localhost", 8080);

// Allow a browser app served from another origin (loopback origins are already allowed)
server.OriginPolicy.AllowedOrigins.Add("https://tools.example.com");

// Serve other machines: bind to all interfaces (requires a URL ACL or admin rights on Windows)
McpHttpServer lanServer = new McpHttpServer("+", 8080);
```

The TCP and stdio transports stay unauthenticated by design; strict framing is what stops browsers from talking to them.

### Sessions

On the handshake-era Streamable HTTP path, session IDs are always generated by the server:

- A successful `initialize` (on `/mcp` or `/rpc`) creates the session, raises `ClientConnected`, and returns `MCP-Session-Id`. A rejected `initialize` creates nothing and returns no header.
- `POST /mcp` without `MCP-Session-Id` gets 400 (`-32600`), except `initialize`. This includes `ping` (since v2.1.4); use the health check (`GET /`) to probe connectivity without a session. Stateless `2026-07-28` requests have no sessions and are unaffected.
- `POST /rpc` without a session runs on a temporary connection and returns no session header, so plain request/response callers (scripts, `curl`) keep working without leaving sessions behind.
- An `MCP-Session-Id` the server did not issue, has expired, or was terminated gets 404 (`-32001`) on every endpoint; it is never adopted. The client should send `initialize` again.
- With an `AuthenticationHandler`, a session belongs to the principal that created it. Another principal presenting that ID gets 404.
- Every request on a session counts as activity, so `SessionTimeoutSeconds` expires only idle sessions.
- The `?session=` query parameter is accepted only on the Voltaic-specific `/events` stream, for browser `EventSource` clients that cannot set headers; `GET /mcp` reads the `MCP-Session-Id` header only.
- `GET /mcp` streams are resumable. Each starts with a priming event (an event ID and an empty `data` field, plus `retry: SseRetryIntervalMs`, default 1000), and every message carries an ID of the form `{streamId}-{n}`. A client that reconnects with `Last-Event-ID` gets the messages it missed on that stream (the last `SseReplayBufferSize` per stream, default 100; the last 8 streams per session), and the old connection stops taking messages. IDs from another session or an unknown stream open a new stream, and messages beyond the replay buffer are not recovered. Streams send `X-Accel-Buffering: no` so reverse proxies do not buffer them. The Voltaic-specific `/events` stream is not resumable.

Clients built on Voltaic 2.0.0 or earlier opened their session with `ping` instead of `initialize` and must be upgraded: since v2.1.4 `RequireInitializedSessions` is obsolete, always reads `true`, and setting it has no effect.

## Authentication

`McpHttpServer` supports an optional async authentication handler that runs before request processing. When set, every request that passes the loopback and origin checks (see [Security defaults](#security-defaults)) is passed through the handler, except the exceptions listed below. The handler receives the full `HttpListenerRequest` and returns an `AuthenticationResult`. If authentication fails, the server returns the result's status code, headers, and error message without processing the request. When no handler is set, requests are not authenticated, but the loopback and origin checks still apply.

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

The following requests are served without calling the handler, so infrastructure can validate connectivity and clients can discover how to authenticate. Origin validation and the loopback check still apply to them:

- **Health check** (`GET /`) - returns `{"status":"Ok"}` for load balancer probes. The handler is not called.
- **Protected resource metadata** (`GET /.well-known/oauth-protected-resource`, and the same path followed by the MCP endpoint path) - served when `ProtectedResourceMetadata` is set. The handler is not called.
- **CORS preflight** (`OPTIONS` requests) - returns `204` with CORS headers for an allowed origin. The handler is not called.

Every other request must authenticate, including the MCP `ping`: the MCP authorization specification requires a `401` for a missing or invalid token on every request. Before v2.1.4 a `ping` that failed authentication was answered anyway.

To shape the rejection, add headers to the failed result. `AuthenticationResult.BearerChallenge()` builds the RFC 6750 challenge MCP clients use to discover your authorization server:

```csharp
server.AuthenticationHandler = request =>
{
    if (request.Headers["Authorization"] != "Bearer " + expectedToken)
    {
        // 401 with WWW-Authenticate: Bearer resource_metadata="...", error="invalid_token"
        return Task.FromResult(AuthenticationResult.BearerChallenge(
            "https://api.example.com/.well-known/oauth-protected-resource", "invalid_token"));
    }

    return Task.FromResult(new AuthenticationResult { IsAuthenticated = true, Principal = "user" });
};

// Any other header works too, for example Retry-After on a throttled rejection:
// result.Headers["Retry-After"] = "30";
```

`McpHttpServer`, `McpWebsocketsServer`, and `A2AHttpServer` write `AuthenticationResult.Headers` on every rejection.

Setting an `AuthenticationHandler` never changes protocol behavior. Once a request is authenticated, it runs through exactly the same MCP pipeline as on a server without a handler: version resolution, stateless `2026-07-28` routing, the batching rules, session tracking, and the `resultType`/cache fields on stateless results. Before v1.1.0, authenticated requests took a separate path that skipped most of these, and stateless clients such as Claude Code could not list tools on an authenticated server.

### OAuth and protected resource metadata

The MCP authorization specification makes the MCP server an OAuth 2.1 resource server: it must publish OAuth 2.0 Protected Resource Metadata (RFC 9728) naming its authorization server, answer unauthenticated requests with `401` and a `WWW-Authenticate` challenge, and validate every access token, including that the token was issued for this server (its audience). Voltaic covers the resource-server plumbing:

```csharp
McpHttpServer server = new McpHttpServer("+", 8443);

// Served without authentication at /.well-known/oauth-protected-resource and /.well-known/oauth-protected-resource/mcp
server.ProtectedResourceMetadata = new McpProtectedResourceMetadata
{
    Resource = "https://mcp.example.com/mcp",
    AuthorizationServers = new List<string> { "https://auth.example.com" },
    ScopesSupported = new List<string> { "tools:read", "tools:write" }
};

server.AuthenticationHandler = async request =>
{
    string? header = request.Headers["Authorization"];
    AccessToken? token = header != null && header.StartsWith("Bearer ")
        ? await ValidateJwtAsync(header.Substring(7), expectedAudience: "https://mcp.example.com/mcp")
        : null;

    if (token == null)
    {
        return AuthenticationResult.BearerChallenge(
            "https://mcp.example.com" + McpProtocol.ProtectedResourceMetadataPath, "invalid_token");
    }

    return new AuthenticationResult { IsAuthenticated = true, Principal = token.Subject };
};
```

The authorization server itself, token issuance, and token validation (signature, expiry, audience, scopes) remain the application's responsibility; `ValidateJwtAsync` above stands for your validator. Serve OAuth-protected servers over HTTPS in production, for example behind a TLS-terminating reverse proxy.

### Sending authentication from the client

`McpHttpClient.SetRequestHeader(name, value)` attaches a header to every request the client sends — the JSON-RPC POST requests and the SSE GET stream, including the initial connection handshake. Use it to send a bearer token or a custom API-key header to an authenticated server. Set the header before connecting so it is present on the handshake. Passing a null or empty value removes a previously set header, and header names are matched case-insensitively.

```csharp
using Voltaic.Mcp;

using McpHttpClient client = new McpHttpClient();

// Bearer token: Authorization: Bearer <token>
client.SetRequestHeader("Authorization", "Bearer " + token);

// Or a custom API-key header instead:
// client.SetRequestHeader("X-API-Key", apiKey);

await client.ConnectStreamableAsync("http://localhost:8080");
```

This pairs with the server-side `AuthenticationHandler` above: the client sends the credential and the handler validates it. The header is present on the `initialize` handshake, so the session the server creates belongs to the authenticated principal.

### WebSocket authentication

`McpWebsocketsServer.AuthenticationHandler` takes the same delegate as `McpHttpServer` and runs on the upgrade request, after the origin and loopback checks. A failure refuses the upgrade with the result's status code, headers, and message. On success, the caller is stored in `ClientConnection.Caller` (visible to `ClientConnected` subscribers) and is the ambient `RpcCallContext.Current` for every request on that socket. On the client, `McpWebsocketsClient.SetRequestHeader` sends credentials on the upgrade request.

```csharp
McpWebsocketsServer server = new McpWebsocketsServer("localhost", 8080);
server.AuthenticationHandler = request => Task.FromResult(
    request.Headers["Authorization"] == "Bearer " + expectedToken
        ? new AuthenticationResult { IsAuthenticated = true, Principal = "user" }
        : AuthenticationResult.BearerChallenge());

McpWebsocketsClient client = new McpWebsocketsClient();
client.SetRequestHeader("Authorization", "Bearer " + expectedToken);
await client.ConnectAsync("ws://localhost:8080/mcp");
```

### Authorizing inside a handler

`AuthenticationHandler` decides whether a request is allowed in; to make per-caller authorization decisions *inside* a tool or method handler (scope reads to a tenant, gate writes by role, and so on) the handler needs the caller's identity. Voltaic carries it there for you.

After a successful `AuthenticationResult`, `McpHttpServer` (per request) and `McpWebsocketsServer` (per socket) publish the caller's `Principal` and `Claims` as an ambient `Voltaic.Core.RpcCallContext`. Because it flows on the request's async call chain, any handler can read `RpcCallContext.Current` without changing its signature. `Current` is `null` when no `AuthenticationHandler` is configured, on transports that do not authenticate (TCP and stdio), and for a `ping` that failed authentication.

```csharp
using Voltaic.Core;
using Voltaic.Mcp;

server.RegisterTool("list_orders", "List the caller's orders", inputSchema, async (RpcParameters? args, CancellationToken token) =>
{
    RpcCallContext? caller = RpcCallContext.Current;
    if (caller == null) throw new InvalidOperationException("Unauthenticated.");

    string tenantId = caller.Claims.TryGetValue("tenantId", out string? t) ? t : throw new InvalidOperationException("No tenant.");
    bool isAdmin = caller.Claims.TryGetValue("isAdmin", out string? a) && a == "true";

    return await LoadOrdersAsync(tenantId, caller.Principal, isAdmin, token);
});
```

If you prefer the caller passed in explicitly rather than read from an ambient, every `RegisterMethod`/`RegisterTool` surface also offers an overload that receives an `RpcCallContext?` parameter (it simply forwards `RpcCallContext.Current`):

```csharp
server.RegisterTool("list_orders", "List the caller's orders", inputSchema,
    async (RpcParameters? args, RpcCallContext? caller, CancellationToken token) =>
    {
        // caller == RpcCallContext.Current
        return await LoadOrdersAsync(caller, token);
    });
```

Both forms are additive: existing handlers that ignore the caller compile and behave exactly as before.

### Authorizing inside an A2A agent

The A2A servers carry the caller the same way, but through the context object the handler already receives rather than an ambient. When `A2AHttpServer` or `A2AGrpcServer` authenticates a request, it copies the `AuthenticationResult`'s `Principal` and `Claims` onto the `A2ARequestContext` before your `IA2AAgentHandler` runs — so the identity is available even though A2A agents execute on a background task and may stream results after the request returns. `Principal`/`Claims` are `null` when no `AuthenticationHandler` is configured or for public Agent Card requests.

```csharp
public async Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
{
    string? principal = context.Principal;                 // who is calling
    string? tenantId = context.Claims != null && context.Claims.TryGetValue("tenantId", out string? t) ? t : null;

    // ... scope the agent's work to the authenticated caller ...
}
```

---

## Specification conformance

Voltaic implements the MCP revisions `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, and `2026-07-28`, and meets their MUST, MUST NOT, SHOULD, and SHOULD NOT requirements on every transport (stdio, TCP, WebSocket, and Streamable HTTP). [COMPATIBILITY.md](COMPATIBILITY.md) has the full per-revision matrix, updated with every release. This section lists the optional features Voltaic does not implement, the places where it deliberately accepts input the specification says a client should not send, and the places where it is stricter than required.

**Optional features not implemented.**

| Area | Voltaic behavior | Specification |
|---|---|---|
| HTTP+SSE transport (2024-11-05) | Not implemented, and `McpHttpClient` does not fall back to it; `/rpc` and `/events` are different, Voltaic-specific endpoints (`EnableLegacyEndpoints = false` turns them off) | Deprecated since 2025-03-26. `2024-11-05` itself is supported over Streamable HTTP, stdio, TCP, and WebSocket. |
| `2026-07-28` in the stdio, TCP, and WebSocket clients | `McpClient`, `McpTcpClient`, and `McpWebsocketsClient` speak the handshake revisions; `McpHttpClient` speaks `2026-07-28` (`ConnectStatelessAsync`). All servers serve `2026-07-28` on every transport. | A client chooses the revisions it supports. |
| Server-to-client requests (sampling, elicitation, roots) on handshake-era sessions | Not sent by Voltaic servers (only `ping`) | Optional. On `2026-07-28`, tool handlers can return `McpInputRequiredResult` (see [Multi Round-Trip Requests](#asking-the-user-for-input-multi-round-trip-requests)); a handshake-era caller gets an `isError` result instead. Voltaic clients answer such requests from other servers through `RegisterRequestHandler`. |
| Multi Round-Trip input for `resources/read` and `prompts/get` (2026-07-28) | Only `tools/call` handlers can request input | Optional. |
| `subscriptions/listen` (2026-07-28) | Not implemented; `server/discover` does not advertise `listChanged` or `subscribe` | Optional, capability-driven. Handshake-era sessions receive change notifications. |
| Tasks (2025-11-25 and the 2026-07-28 extension) | Models only; the server does not run tools as tasks | Optional. |
| OAuth authorization | Resource-server plumbing only: `ProtectedResourceMetadata`, `BearerChallenge`, `InsufficientScope`, `McpInsufficientScopeException`, and `AuthenticationHandler`; `McpHttpClient` sends the credentials you give it but runs no OAuth flow | Authorization is optional. The authorization server (including the `2025-03-26` metadata and fallback endpoints `/authorize`, `/token`, and `/register`), token validation, and audience checks are application responsibilities. See [OAuth and protected resource metadata](#oauth-and-protected-resource-metadata). |
| HTTPS listening | `McpHttpServer` listens on HTTP; terminate TLS in a reverse proxy | The transport requires HTTPS in production deployments, which a proxy provides. |

**Deliberate leniency.** Each of these accepts input the specification says a client should not send, without weakening any security check:

- A missing `Accept` header counts as `*/*`, and media ranges such as `application/*` match.
- `POST /mcp` without `Content-Type` is accepted when there is no `Origin` header (a non-browser client). Browser requests without it get 415.
- On `2026-07-28`, notification POSTs need no routing headers, and unknown notifications get 202.
- `McpHttpClient` treats a `2026-07-28` result without `resultType` as complete, for servers that predate the field. Any other value it does not recognize, including a non-string one, is rejected.
- Tool arguments that fail the input schema produce a tool result with `isError: true` on every revision (the model can correct them); older revisions allowed either this or `-32602`.
- The TCP server accepts newline-delimited JSON (the stdio framing) as well as `Content-Length` framing, and closes a connection that sends a line that is not JSON (a cross-protocol request).
- JSON-RPC batches are accepted on `2024-11-05` sessions as well as `2025-03-26` (JSON-RPC 2.0 allows them; `2024-11-05` does not mention them).
- Invalid UTF-8 in a message is decoded with replacement characters rather than rejected.
- The Voltaic-specific `/rpc` and `/events` endpoints accept the session in `?session=` (on `/events`) and do not check `Accept` or `Content-Type`; the Streamable HTTP endpoint does.
- A JSON Schema `pattern` that .NET's ECMAScript mode cannot compile (for example one with `\p{...}` Unicode property escapes) runs with .NET regular expression semantics.

**Stricter than required.** `RegisterTool` requires a description (optional in the specification) and rejects tool names outside 1-128 characters of letters, digits, `_`, `-`, and `.` (a SHOULD in the specification), so every published tool is portable.

**Status codes and disconnects on HTTP.** The HTTP status is sent with the first byte of a response. By default (`ResponseKeepAliveMs = 0`) a POST response is a single JSON body with its exact status, including 400 for a `-32021` and 403 for an `McpInsufficientScopeException` raised late in a tool. The one exception is a request whose client asked for notifications (a progress token, or a `2026-07-28` log level) and whose handler sent one before failing: that response is already the SSE stream the client asked for (status 200), so the error is its final event. A `2026-07-28` client that closes its stream cancels the request as soon as the server writes to it (a notification or the response); a handler that sends nothing runs to completion. Setting `ResponseKeepAliveMs` above 0 sends keep-alives so a disconnect is noticed while a silent handler runs, at the cost of committing status 200 once the first keep-alive is sent.

**Behavior that follows the specification**, for reference:

- Lifecycle: `initialize` must come first (other requests get `-32600`, except `ping`), on every transport including `/rpc`; it must carry `protocolVersion`, `capabilities`, and `clientInfo` (`-32602`) and runs once per session, even when two arrive together. A version the server does not know is answered with `MaximumHandshakeProtocolVersion`. Every Voltaic client initializes on connect (`AutoInitialize`), declares only the capabilities the requested revision defines, and requests only handshake versions. The stdio client reads and writes UTF-8 whatever the console code page, and shuts the server down by closing its input, then SIGTERM (Linux, macOS), then killing it.
- JSON-RPC: `id` must be a string or an integer (`null` and fractions are `-32600`), `jsonrpc` must be `"2.0"`, and MCP `params` must be an object (members of the wrong type are `-32602`). A notification method sent with an `id` is `-32601`. Every result is a JSON object (null answers `{}`, anything else is `-32603`). Batches are rejected on `2025-06-18` and later (an empty batch is `-32600`, and `initialize` or a `2026-07-28` request may not be batched); every client accepts batches from the server.
- Concurrency, cancellation, and health: requests on one connection run concurrently, so `ping` is answered while a tool runs. `notifications/cancelled` cancels the handler's token and suppresses its response and any further progress or log messages, including when it arrives before its request; a cancellation for a request that was already answered or rejected is ignored, so a reused ID is never cancelled by it. Both sides log cancellation reasons. Clients send `notifications/cancelled` when a call times out or is cancelled, and honor it for requests the server sent them. Clients, and stdio, TCP, and WebSocket servers, ping the other side every `PingIntervalMs` (default 30 seconds) after `initialize`, and treat an unanswered ping as a connection failure (`PingFailureThreshold`, default 1).
- Notifications: `list_changed` is sent automatically when tools are registered or unregistered and when resources, templates, or prompts are registered, and goes only to initialized sessions; `resources/updated` only to sessions subscribed to that URI; `notifications/message` only at or above the session's `logging/setLevel` level (or the `2026-07-28` `_meta` log level); progress only for a request in flight that carries the token, with increasing values, on the request's own response stream. `BroadcastNotificationAsync` reaches initialized sessions only. Notifications and results are downgraded to the revision that governs them (the negotiated one, or `2026-07-28` for a stateless request on any connection).
- Schemas: tool schemas are validated in their declared dialect with that dialect's keywords only: JSON Schema 2020-12 (the default; `prefixItems`, `dependentRequired`, `dependentSchemas`, `unevaluatedProperties`, `unevaluatedItems`, `$anchor`, and `$dynamicRef` with dynamic scope) or draft-07 (array `items` with `additionalItems`, `dependencies`, fragment `$id` anchors, and `$ref` overriding its siblings). References resolve within the resource they appear in. `pattern` uses ECMA-262 semantics. Another dialect, a keyword form the dialect does not allow, or a reference that cannot be resolved within the schema is rejected when the tool is registered.
- Features: input schemas are objects; an output schema may have any shape, and older sessions (whose schema requires an object) receive it as an object schema or not at all; a tool with an `outputSchema` must return `structuredContent` (JSON `null` included, on `2026-07-28`), and `structuredContent` that is not an object is removed before `2026-07-28` (the text block keeps it); resource-not-found is `-32002` on handshake-era revisions and `-32602` on `2026-07-28`; resource templates follow RFC 6570; completions return at most 100 values with the full `total`; cursors are opaque and stable across changes.
- HTTP: invalid `Origin` gets 403; sessions come only from a successful `initialize`; a missing session gets 400 and an unknown or terminated one 404, and a terminated session's streams end; `DELETE` ends a session and only its owner may send it; `MCP-Protocol-Version` must name a version the server negotiates and, on a session, the session's version; a notification the server cannot accept gets 400; `McpHttpClient` starts a new session after any 404 (requests, notifications, its GET stream, resumed streams) and sends `DELETE` when it disconnects. POST response streams on a session carry event IDs (with a priming event from `2025-11-25`, as on GET streams) and can be resumed with `GET` and `Last-Event-ID`. A 401 always carries `WWW-Authenticate`, every Bearer challenge names `resource_metadata` (derived from the configured resource identifier, so it is correct behind a proxy), and missing scopes get 403 `insufficient_scope`, also for a batch.
- `2026-07-28`: the body is a single JSON-RPC request or notification (not JSON is 400 `-32700`; batches and client responses are 400 `-32600`); `_meta` must carry the protocol version and `clientCapabilities` (`-32602`); results carry `resultType` and `serverInfo` in `_meta`; `ping`, `logging/setLevel`, and `resources/subscribe` are not served; input requests need the matching client capability, including `elicitation.url`, `sampling.tools`, and `sampling.context` (`-32021`, whose data always carries `requiredCapabilities`); `server/discover` requires the `_meta` protocol version; `MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name` (from `params.name` for tools and prompts, `params.uri` for `resources/read`), and `Mcp-Param-{Name}` headers are validated (400 `-32020`), whatever the argument's JSON type; an unsupported version gets 400 `-32022` with the versions this server accepts. `McpHttpClient` in stateless mode sends the version header and `_meta` on every request and notification (including `CallAsync` and `NotifyAsync`) and retries any request after `-32022` with a listed version. No error code from the reserved legacy range is used (`McpInsufficientScopeException` uses 403).

## Upgrading to v2.1.8

v2.1.8 closes the gaps a review of v2.1.7 found. These cases need attention:

| Change | Who is affected | What to do |
|---|---|---|
| Schemas are validated with their dialect's keywords only: a 2020-12 schema no longer enforces `dependencies` or the array form of `items` (rejected at registration), and a draft-07 schema no longer enforces `prefixItems`, `unevaluated*`, `dependentRequired`, or `$ref` siblings | Tools whose schemas mixed dialects | Use the keyword of the declared dialect (`dependentRequired`, `prefixItems`, ...), or declare draft-07 |
| A typeless output schema is kept as written; older sessions receive it with `"type": "object"` only when it describes objects, otherwise without it | Tools whose typeless output schema relied on the implied object type for 2026-07-28 clients | Add `"type": "object"` when the output is an object |
| For stateless `resources/read`, `Mcp-Name` must equal `params.uri` | Clients that sent the resource name | Send the URI (`McpHttpClient` does) |
| A server closes a connection whose client leaves a ping unanswered, and clients disconnect likewise (`PingFailureThreshold`, default 1) | Clients or servers that do not answer ping | Answer ping (every MCP implementation must), or set `PingFailureThreshold = 0` |
| A notification the server cannot accept gets 400; insufficient scope in a batch makes the batch response 403; a non-JSON stateless body is `-32700` | Clients that relied on 202 or 200 | Handle the error status |
| JSON Schema `pattern` uses ECMA-262 semantics (`\d` and `\w` are ASCII) | Patterns that relied on .NET's Unicode digit and word classes | Use explicit character classes |
| `MissingRequiredClientCapability` always carries `data.requiredCapabilities` | Code that checked for null data | Read `requiredCapabilities` |

## Upgrading to v2.1.7

v2.1.7 closes every remaining MUST and SHOULD gap. These cases need attention:

| Change | Who is affected | What to do |
|---|---|---|
| `RegisterTool` rejects schemas with an unsupported `$schema` dialect (anything but 2020-12 and draft-07) or a `$ref` that cannot be resolved within the schema, and enforces `unevaluatedProperties`/`unevaluatedItems` | Tools with draft-04 schemas or external references | Use 2020-12 or draft-07 and inline the referenced schemas in `$defs` |
| Sessionless `/rpc` requests other than `initialize` and `ping` get `-32600` | Code that called tools on `/rpc` without `initialize` | Connect with `McpHttpClient.ConnectAsync`, or `initialize` first and send the session ID |
| A handler result that is not a JSON object is `-32603` on every revision | `RegisterMethod` handlers returning strings, numbers, or arrays | Return an object (for example `new { value = ... }`) |
| Request IDs must be strings or integers; a notification method sent with an `id` is `-32601` | Clients sending fractional IDs | Use integer or string IDs |
| `MCP-Protocol-Version` on a session must be the negotiated version, and at most `MaximumHandshakeProtocolVersion` | Clients that send another version after `initialize` | Send the negotiated version (`McpHttpClient` does) |
| `ResponseKeepAliveMs` defaults to 0 | Servers relying on keep-alives for disconnect detection | Set it above 0; see "Status codes and disconnects on HTTP" |
| `McpInsufficientScopeException.ErrorCode` is 403 (was -32003) | Code that checked for -32003 | Check for 403, or catch the HTTP 403 |
| Registering or removing tools, resources, templates, and prompts sends `list_changed` automatically | Code that also called `Notify*Changed` after registering | Remove the extra call, or keep it (clients handle duplicates) |
| Clients and stream servers ping every 30 seconds after `initialize` | Tests that read every message on a raw connection | Answer the ping, or set `PingIntervalMs = 0` |
| `BroadcastNotificationAsync` on `McpTcpServer` and `McpWebsocketsServer` reaches initialized sessions only | Code that broadcast to connections before `initialize` | Send after `initialize` |
| Stream clients do not declare `elicitation` when requesting 2024-11-05 or 2025-03-26 | None usually | None |

## Upgrading to v2.1.6

v2.1.6 closes the remaining gaps found by a second review against the specification. These cases need attention:

| Change | Who is affected | What to do |
|---|---|---|
| `ProtocolVersion` on `McpClient`, `McpTcpClient`, and `McpWebsocketsClient` throws `ArgumentException` for a version `initialize` cannot negotiate (for example `2026-07-28`) | Code that set an unsupported version | Use a handshake-era version; use `McpHttpClient.ConnectStatelessAsync` for `2026-07-28` |
| `server/discover` without the `_meta` protocol version gets `-32602` | Handshake-era callers of `server/discover` | Send the stateless `_meta` (`McpHttpClient.DiscoverAsync` does) |
| A stateless `tools/call` whose `x-mcp-header` argument has another JSON type than declared needs the header too (400 `-32020` without it) | Stateless clients that skipped the header for such values | Mirror the value as its own type (`McpHttpClient` does) |
| POST response streams on a session carry `id:` fields and, from `2025-11-25`, start with a priming event (empty `data`) | Code that parsed the raw stream | Skip events with empty data |
| Server-level `NotifyProgressAsync` outside a tool handler sends nothing when several clients use the same token | Servers that relied on the first match | Report progress from the handler with `McpToolCallContext.ReportProgressAsync` |
| Params members of the wrong JSON type are `-32602` instead of `-32603`; a handler that returns null answers `{}` | Code that checked for `-32603` | Check for `-32602` |
| `structuredContent` that is not an object is removed for `2025-06-18` and `2025-11-25` sessions | Tools returning arrays or scalars through `FromStructured` | Return an object, or rely on the text block |

## Upgrading to v2.1.5

v2.1.5 enforces MCP requirements that earlier versions did not. Code that follows the specification needs no changes; these are the cases that do:

| Change | Who is affected | What to do |
|---|---|---|
| stdio, TCP, and WebSocket servers answer requests before `initialize` with `-32600` (except `ping`), and a second `initialize` too | Clients that skipped `initialize`, or Voltaic stream clients whose code sends `initialize` after connecting | Voltaic clients now initialize on connect: remove your own `initialize` call, or set `AutoInitialize = false` and call `InitializeAsync` |
| `initialize` without `protocolVersion`, `capabilities`, or `clientInfo` gets `-32602` | Hand-written clients that omitted them | Send all three; the servers' `ProtocolVersion` property (the old fallback) is obsolete |
| `McpTcpClient` sends newline-delimited JSON | Clients of Voltaic TCP servers before 2.1.5 | Upgrade the server, or set `NewlineDelimited = false` |
| Resource not found is `-32002` on handshake-era sessions | Code that checked for `-32602` | Check for `-32002` (still `-32602` on `2026-07-28`) |
| `RegisterTool` throws `ArgumentException` for names outside `[A-Za-z0-9_.-]{1,128}` and for schemas that are not objects | Tools named with spaces, slashes, or other characters | Rename the tool; use an object schema (a schema without `type` gets `type: "object"`) |
| A tool with an `outputSchema` that returns no `structuredContent` gets `-32603` | Tools that return only text despite declaring an output schema | Return `McpToolCallResult.FromStructured(...)`, or drop the output schema |
| Batches are answered only on `2025-03-26` sessions | Clients that batched on other versions | Send requests one at a time |
| `NotifyProgress*` sends only for a request in flight that carries the token, and progress must increase; log notifications honor `logging/setLevel`; `resources/updated` goes only to subscribers; `list_changed` only to initialized sessions | Servers that broadcast progress, or relied on every client receiving every notification | Report progress from the tool with `McpToolCallContext.ReportProgressAsync`; clients subscribe to the resources they want |
| `NotifyCancelledAsync` and `NotifyCancelled` are obsolete and do nothing | Servers that called them (compiler warning CS0618) | Remove the calls; a server may cancel only requests it sent |
| Results are downgraded for older sessions (for example no `structuredContent` or audio content for `2024-11-05`) | Tests that compared full results on old versions | Expect the fields the negotiated revision defines |
| On `2026-07-28`: `_meta` without `clientCapabilities` or the protocol version gets 400 `-32602`; `ping`, `logging/setLevel`, and `resources/subscribe` get 404 `-32601`; a `RegisterMethod` handler that returns something other than an object gets `-32603` | Stateless clients that omitted `_meta` fields or used removed methods | Send the required `_meta` (`McpHttpClient` does); return objects from handlers |
| `DELETE /mcp` from a principal other than the session's owner gets 404; `/mcp?session=...` is no longer accepted | Clients that shared sessions across credentials or passed the session in the query | Send `MCP-Session-Id` with the credentials that created the session |
| `McpHttpClient.Disconnect` sends `DELETE` for the session | Code that reused a session ID after disconnecting | Connect again for a new session |
| Pagination cursors are opaque base64 values | Code that built cursors itself | Pass back the `nextCursor` you received |
| `McpResourceLinkContent.Name` is a non-nullable string | Code that assigned null | Assign a name (null becomes empty) |
| On `2026-07-28`, a tool that requests input the client did not declare a capability for gets `-32021` | `CallToolStatelessAsync` callers that answer input requests without declaring the capability | Add the capability, for example `client.ClientCapabilities["elicitation"] = new { };` |

## Upgrading to v2.1.4

v2.1.4 changes behavior where Voltaic deviated from the MCP specification:

| Change | Who is affected | What to do |
|---|---|---|
| A tool handler exception returns a result with `isError: true` and a generic message instead of JSON-RPC `-32603` with the exception message | Code that checked for `-32603` after `tools/call`; tools that relied on their exception text reaching the client | Check `isError`; throw `McpToolException` for text meant for the model, or set `IncludeToolExceptionMessages = true`; throw `McpProtocolException` when a protocol error is wanted |
| An output-schema violation is `-32603` instead of `-32602` | Code that checked for `-32602` | Treat it as a server error |
| `POST /mcp` `ping` without `MCP-Session-Id` gets 400 | Health checks that pinged `/mcp` without a session | Use `GET /` (health check), or `initialize` first |
| `ping` no longer skips the `AuthenticationHandler` (on `/mcp` and `/rpc`) | Unauthenticated liveness probes | Use `GET /`, which needs no credentials |
| `RequireInitializedSessions` is obsolete and ignored (always `true`) | Servers that set it `false` for Voltaic 2.0.0 clients (compiler warning CS0618) | Upgrade those clients; remove the setting |
| `GET /mcp` streams start with a priming event (`id`, `retry`, empty `data`) instead of the `: connected` comment, and every event has an `id` | Code that parsed the raw stream expecting the comment | Skip events with empty data |
| `RegisterTool` throws `ArgumentException` for invalid `x-mcp-header` annotations, and stateless `tools/call` needs matching `Mcp-Param-*` headers | Tools that used the annotation (it had no effect before) | Fix the annotation; stateless HTTP clients must send the headers (`McpHttpClient` does) |
| Clients answer server requests instead of dropping them; `ping` can no longer be registered with `RegisterRequestHandler` on MCP clients | Code that relied on requests being ignored | None usually; register handlers for methods you support |

## Upgrading to v2.1.3

v2.1.3 is additive for MCP and aligns the A2A wire format with A2A v1.0:

| Change | Who is affected | What to do |
|---|---|---|
| Push notification configs are written in the flat A2A v1.0 shape (`url`, `token`, `authentication` at the top level) and get/delete requests send `id` instead of `configId` | Non-Voltaic code that parsed the nested `pushNotificationConfig` object from Voltaic | Read the flat fields. Voltaic reads both shapes, so Voltaic clients and servers of any 2.x version interoperate |
| `A2AClient` lists push configs with `ListTaskPushNotificationConfigs` (the v1.0 method name) | Servers that accept only the singular name | Voltaic servers accept both names; upgrade other servers or rename the method |
| HTTP+JSON errors are `google.rpc.Status` objects with an `ErrorInfo` reason | Code that parsed Voltaic's REST error body as a JSON-RPC error | Read `error.details[].reason`; `A2AHttpJsonClient` reads both formats |
| stdio, TCP, and WebSocket servers answer requests whose `_meta` names `2026-07-28` with stateless results (`resultType`, `ttlMs`, `cacheScope`), and reject an unknown `_meta` version with `-32022` | Clients that sent `_meta` protocol versions to these transports and expected handshake-era results | Omit the `_meta` protocol version, or handle stateless results |
| A tool returning `McpInputRequiredResult` to a handshake-era caller gets an `isError` result | Handlers that returned input requests unconditionally | Check `McpToolCallContext.Current.CanRequestInput` and provide a fallback |
| `A2AGrpcServer` bound to `localhost` also listens on `::1` | Other processes that use the same port on `::1` | Bind to `127.0.0.1` to keep IPv4 only |

## Upgrading to v2.1.2

v2.1.2 changes a few behaviors to match the specifications:

| Change | Who is affected | What to do |
|---|---|---|
| Invalid tool arguments return a result with `isError: true` instead of JSON-RPC `-32602` | Code that checked for `-32602` after `tools/call` | Check `isError` on the result |
| `initialize` with an unknown version negotiates `MaximumHandshakeProtocolVersion` instead of failing | Tests that expected an error | Expect the negotiated version |
| A2A JSON-RPC errors use HTTP 200 (the error is in the body) | Clients that relied on 4xx statuses for JSON-RPC errors | Read the JSON-RPC `error` object; `A2AClient` already does |
| A2A push notification configs require an existing task and an allowed webhook URL | Callers that registered configs for unknown tasks or local URLs | Create the task first; set `PushNotificationUrlValidator` to allow local webhooks during development |
| `A2AGrpcServer` requires authentication for `GET /extendedAgentCard`, validates `Origin`, and serves loopback clients only when bound to `localhost` | Unauthenticated callers of the extended card; remote clients of a localhost-bound gRPC server | Authenticate, or bind to `*`/`+`, or set `RestrictToLoopbackClients = false` |
| `SendMessageConfiguration.PushNotificationConfig` is written as `taskPushNotificationConfig` (the A2A v1.0 name); both names are read | Servers built on Voltaic 2.1.1 or earlier ignore the new name | Upgrade servers; they never delivered pushes before 2.1.2 |

## Upgrading to v2.1.0

v2.1.0 changes defaults for security. Most applications need no code changes; check this list if browsers, remote machines, or older clients call your server.

| Symptom after upgrading | Cause | Fix |
|---|---|---|
| A browser app on another origin gets 403 | `Origin` validation | `server.OriginPolicy.AllowedOrigins.Add("https://your.app")` |
| Another machine gets 403 from a server bound to `localhost` | Loopback-only clients | Bind to `+`/`*` or a specific address, or set `RestrictToLoopbackClients = false` |
| A client gets 400 "Missing MCP-Session-Id" on `/mcp` | Sessions come only from `initialize` | Send `initialize` first (Voltaic 2.1.0 clients do). In v2.1.0 to v2.1.3, `RequireInitializedSessions = false` served Voltaic 2.0.0 clients; since v2.1.4 it is ignored |
| A client gets 404 for a session ID it chose or that expired | Unknown IDs are no longer adopted | Re-initialize and use the server-issued ID |
| A sessionless `/rpc` caller no longer receives `MCP-Session-Id` | Sessionless `/rpc` requests run without a session | Send `initialize` to `/rpc` when you need a session (for example for `/events`) |
| A `text/plain` POST to `/mcp` gets 415 | Streamable HTTP requires JSON | Send `Content-Type: application/json` |
| A TCP client is disconnected | Strict LSP framing | Send only `Content-Length` and `Content-Type` header lines |
| A browser expects `Access-Control-Allow-Origin: *` | CORS echoes the allowed origin | Allow the origin in `OriginPolicy` |

`McpHttpClient.ConnectAsync` and `ConnectStreamableAsync` now perform the `initialize` handshake instead of a `ping`, so upgrade clients alongside servers.

## Upgrading from v1.x

v2.0.0 changes what an MCP server publishes by default and tightens tool invocation. Most applications need one or two edits:

| v1.x | v2.0.0 |
|---|---|
| `includeDefaultMethods` constructor parameter, default `true`, controls protocol methods and demo tools together | MCP servers: `includeDiagnosticTools`, default `false`, controls only `echo` and `getTime`; protocol methods are always registered. `JsonRpcServer`: `includeDiagnosticMethods`, default `false` |
| Demo tools `ping`, `echo`, `getTime`, `getSessions`/`getClients` published in `tools/list` | Nothing is published unless you register it; `getSessions`/`getClients` removed |
| `ping` returns `"pong"` | `ping` returns `{}` (with `resultType` under `2026-07-28`) |
| Every tool is also a bare JSON-RPC method | Tools are invoked only through `tools/call` |
| No way to remove a tool | `UnregisterTool(name)` on every MCP server |
| `additionalProperties` ignored | `additionalProperties` and `patternProperties` enforced |
| `RegisterBuiltInMethods()` (protected virtual) | `RegisterProtocolMethods()` and `RegisterDiagnosticTools()`; `JsonRpcServer.RegisterDiagnosticMethods()` |

Clients: use the new `PingAsync()` on `McpHttpClient`, `McpClient`, and `McpWebsocketsClient` instead of `CallAsync<string>("ping")`. It accepts both `{}` and a v1.x server's `"pong"`. A v1.x `McpHttpClient` cannot connect to a v2.0.0 server, because its connection probe expects `"pong"`; upgrade clients and servers together.

[MIGRATE_V1_TO_V2.md](MIGRATE_V1_TO_V2.md) lists every change with before-and-after code.

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
- **Test.Shared**: Shared Touchstone descriptors and the central 572-case API/protocol matrix
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

# The shared suite currently projects 572 cases through the console, xUnit, and NUnit runners

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

- `Voltaic.Core`: `JsonRpcServer`, `JsonRpcClient`, JSON-RPC request/response/error models, TCP framing, connection models, shared authentication/error helpers, and the `OriginPolicy` and `LoopbackAddresses` access helpers.
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
