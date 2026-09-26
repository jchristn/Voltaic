# Migrating from Voltaic v1.x to v2.0.0

This guide is written for developers and coding agents that upgrade an application from Voltaic 1.x (1.1.0 or earlier) to 2.0.0. Each change lists how to find affected code, what to change, and how to verify it. Work through the sections in order; most applications need only sections 1 and 2.

## Summary of breaking changes

| # | Change | Affects | Compile error? |
|---|---|---|---|
| 1 | Constructor parameter `includeDefaultMethods` renamed and its default changed | Every server construction that names the parameter or relies on the demo tools | Yes, when the parameter is named |
| 2 | Demo tools no longer published by default; `getSessions` and `getClients` removed | Code or clients that call `ping`/`echo`/`getTime`/`getSessions`/`getClients` tools | No |
| 3 | Protocol `ping` returns `{}` instead of `"pong"` | Clients that read the `ping` result | No |
| 4 | Tools are no longer callable as bare JSON-RPC methods | Clients that call a tool by its name instead of through `tools/call` | No |
| 5 | `RegisterBuiltInMethods()` replaced | Subclasses of `McpHttpServer`, `McpTcpServer`, `McpWebsocketsServer`, or `JsonRpcServer` | Yes |
| 6 | Tool input schemas enforce `additionalProperties` and `patternProperties` | Tools whose schema sets `additionalProperties: false` or `patternProperties` | No |
| 7 | `JsonRpcServer` no longer registers diagnostic methods by default and never registers `getClients` | Plain JSON-RPC servers whose clients call `ping`, `echo`, `getTime`, `add`, or `getClients` | No |

New APIs: `UnregisterTool(string)` on every MCP server, `PingAsync(...)` on `McpHttpClient`, `McpClient`, and `McpWebsocketsClient`, and the protected `RegisterProtocolMethods()` / `RegisterDiagnosticTools()` extension points.

Update the package reference first:

```xml
<PackageReference Include="Voltaic" Version="2.0.0" />
```

---

## 1. Server constructor parameter

### What changed

In v1.x one boolean, `includeDefaultMethods` (default `true`), controlled both the MCP protocol methods (`initialize`, `tools/list`, `tools/call`, ...) and the demo tools. Setting it to `false` produced a server that could not speak MCP.

In v2.0.0 the protocol methods are always registered, and the parameter controls only the optional diagnostic tools:

| Class | v1.x parameter | v2.0.0 parameter | v2.0.0 default | What it controls in v2.0.0 |
|---|---|---|---|---|
| `McpHttpServer` | `includeDefaultMethods = true` | `includeDiagnosticTools = false` | `false` | `echo` and `getTime` tools |
| `McpTcpServer` | `includeDefaultMethods = true` | `includeDiagnosticTools = false` | `false` | `echo` and `getTime` tools |
| `McpServer` (stdio) | `includeDefaultMethods = true` | `includeDiagnosticTools = false` | `false` | `echo` and `getTime` tools |
| `McpWebsocketsServer` | `includeDefaultMethods = true` | `includeDiagnosticTools = false` | `false` | `echo` and `getTime` tools |
| `JsonRpcServer` | `includeDefaultMethods = true` | `includeDiagnosticMethods = false` | `false` | `ping`, `echo`, `getTime`, `add` methods |

The parameter keeps its position, so positional calls still compile.

### Find

```bash
grep -rn "includeDefaultMethods" --include=*.cs .
grep -rnE "new (McpHttpServer|McpTcpServer|McpServer|McpWebsocketsServer|JsonRpcServer)\(" --include=*.cs .
```

### Change

| v1.x code | v2.0.0 code |
|---|---|
| `new McpHttpServer(host, port, includeDefaultMethods: false)` (tried to hide demo tools) | `new McpHttpServer(host, port)` |
| `new McpHttpServer(host, port)` or `includeDefaultMethods: true` | `new McpHttpServer(host, port)`; add `includeDiagnosticTools: true` only if you want `echo` and `getTime` |
| `new McpServer(includeDefaultMethods: false)` | `new McpServer()` |
| `new JsonRpcServer(ip, port)` whose clients call `ping`/`echo`/`getTime`/`add` | `new JsonRpcServer(ip, port, includeDiagnosticMethods: true)` |
| `new JsonRpcServer(ip, port, includeDefaultMethods: false)` | `new JsonRpcServer(ip, port)` |
| A positional `false` in the `includeDefaultMethods` slot | Leave as is (it now means "no diagnostic tools") or delete it |
| A positional `true` in the `includeDefaultMethods` slot | Delete it unless you want the diagnostic tools |

If a v1.x application passed `includeDefaultMethods: false` and then re-registered protocol methods by hand with `RegisterMethod("initialize", ...)` and similar, delete those registrations. They now override the built-in handlers.

### Verify

`tools/list` returns exactly the tools the application registers.

---

## 2. Demo tools removed from `tools/list`

### What changed

v1.x published these tools on every MCP server:

| Tool | v1.x servers | v2.0.0 |
|---|---|---|
| `ping` | HTTP, TCP, stdio | Removed as a tool. `ping` is a protocol method on every server (see section 3). |
| `echo` | HTTP, TCP, stdio (bare method on WebSocket) | Opt-in diagnostic tool (`includeDiagnosticTools: true`) on every MCP server |
| `getTime` | HTTP, TCP, stdio (bare method on WebSocket) | Opt-in diagnostic tool (`includeDiagnosticTools: true`) on every MCP server |
| `getSessions` | HTTP | Removed. It disclosed every client's `Mcp-Session-Id`, which was enough to end or read another client's session. |
| `getClients` | TCP (bare method on WebSocket and `JsonRpcServer`) | Removed |

### Find

```bash
grep -rnE "\"(getSessions|getClients|echo|getTime)\"" --include=*.cs .
```

Also search client code, test scripts, and prompts or documentation that tell an AI model to call these tools.

### Change

- If the application needs `echo` or `getTime`, pass `includeDiagnosticTools: true`, or register its own tool with `RegisterTool`.
- If host code needs the session or client list, call it in-process: `McpHttpServer.GetActiveSessions()` and `GetConnectedClients()` are still public. Do not expose them as a tool to remote callers.
- If the application registered its own `ping`, `echo`, or `getTime` tool to replace Voltaic's, keep it. It no longer collides with anything, and an application tool named `ping` no longer replaces the protocol `ping`.
- Remove documentation that says "Voltaic also registers its own diagnostics" or lists the four demo tools.

### Verify

An AI client (for example Claude Code) lists only the application's tools.

---

## 3. Protocol `ping` returns `{}`

### What changed

In v1.x the `ping` request was answered by the demo `ping` tool's handler and returned the string `"pong"`. The MCP specification requires an empty result. In v2.0.0 every MCP server answers `ping` with `{}`, and under the stateless `2026-07-28` revision with `{"resultType":"complete"}`.

`McpHttpServer` still lets `ping` bypass the `AuthenticationHandler`. The bypass now reaches only the protocol handler, which runs no application code.

`JsonRpcServer` (plain JSON-RPC, not MCP) still returns `"pong"` from its diagnostic `ping` method when `includeDiagnosticMethods: true`.

### Find

```bash
grep -rnE "CallAsync<string>\(\"ping\"|\"pong\"" --include=*.cs .
```

### Change

| v1.x code | v2.0.0 code |
|---|---|
| `string pong = await client.CallAsync<string>("ping");` | `await client.PingAsync();` |
| `Assert.Equal("pong", result)` | Assert that the call succeeded, or that the result is an empty object |
| `JsonRpcResponse r = await httpClient.CallAsync("ping"); r.Result == "pong"` | `r.Error == null` |

`PingAsync` exists on `McpHttpClient`, `McpClient`, and `McpWebsocketsClient`. It accepts any successful result, so it works against both v2.0.0 (`{}`) and v1.x (`"pong"`) servers. It throws `InvalidOperationException` (`McpHttpClient`) or `Exception` (`McpClient`, `McpWebsocketsClient`) when the server answers with a JSON-RPC error.

`McpTcpClient` inherits `JsonRpcClient`; use `await client.CallAsync<object?>("ping")` there.

If an application replaced `ping` with `RegisterMethod("ping", _ => "pong")`, remove that registration. It breaks specification-conformant clients, and on `McpHttpServer` it runs application code without authentication.

### Mixed-version deployments

- v2.0.0 `McpHttpClient` to v1.x server: works (`PingAsync` accepts `"pong"`).
- v1.x `McpHttpClient` to v2.0.0 server: **fails**. `ConnectAsync` and `ConnectStreamableAsync` in v1.x call `CallAsync<string>("ping")`, which cannot read `{}`, and return `false`. Upgrade Voltaic clients before or together with servers.
- Third-party MCP clients (Claude Code, MCP Inspector, the official SDKs) expect `{}` and are unaffected or improved.
- v2.1.0 note: from v2.1.0, `McpHttpClient` connects with the `initialize` handshake instead of `ping`, and `McpHttpServer` creates sessions only on a successful `initialize`. A v2.0.0 `McpHttpClient` talking to a v2.1.0 server needs `RequireInitializedSessions = false` on the server. See "Upgrading to v2.1.0" in the README.

---

## 4. Tools are callable only through `tools/call`

### What changed

In v1.x `RegisterTool(name, ...)` also registered `name` as a bare JSON-RPC method, so a client could send `{"method":"my_tool","params":{...}}` directly. That path skipped `tools/call` and its input and output schema validation.

In v2.0.0 a tool is reachable only through `tools/call`. A bare call to a tool name returns `-32601` (method not found).

`RegisterMethod` is unchanged. Methods registered with it are still bare JSON-RPC methods and do not appear in `tools/list`.

### Find

Search client code for calls whose method name is a tool name rather than a protocol method:

```bash
grep -rnE "CallAsync(<[^>]+>)?\(\"[^\"/]+\"" --include=*.cs .
```

Any hit whose method is one of your `RegisterTool` names needs to change. Hits for `RegisterMethod` names, `ping`, and `initialize` are fine.

### Change

```csharp
// v1.x
double sum = await client.CallAsync<double>("add", new { a = 5, b = 3 });

// v2.0.0: read the tools/call result through a small typed view
TextToolResult result = await client.CallAsync<TextToolResult>(
    "tools/call",
    new { name = "add", arguments = new { a = 5, b = 3 } });
string? text = result.Content[0].Text; // "8"

// Typed views (one class per file in projects that follow Voltaic's style)
public sealed class TextToolResult
{
    [JsonPropertyName("content")]
    public List<TextBlock> Content { get; set; } = new List<TextBlock>();
}

public sealed class TextBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
```

`McpToolCallResult.Content` is a `List<object>`, so deserializing into `McpToolCallResult` on the client yields untyped content items; a typed view like the one above is simpler to read.

A tool's result is always wrapped in `McpToolCallResult` (`content`, and `structuredContent` when the tool declares an output schema). If a caller needs the raw value, either read `structuredContent`/`content`, or register a separate bare method with `RegisterMethod`.

### Verify

Calling a tool by its bare name returns `-32601`; calling it through `tools/call` succeeds.

---

## 5. `RegisterBuiltInMethods()` replaced

### What changed

| Class | v1.x | v2.0.0 |
|---|---|---|
| `McpHttpServer` | `protected virtual void RegisterBuiltInMethods()` | `protected virtual void RegisterProtocolMethods()` and `protected virtual void RegisterDiagnosticTools()` |
| `McpWebsocketsServer` | `protected virtual void RegisterBuiltInMethods()` | `protected virtual void RegisterProtocolMethods()` and `protected virtual void RegisterDiagnosticTools()` |
| `McpTcpServer` | `protected override void RegisterBuiltInMethods()` | `protected virtual void RegisterProtocolMethods()`, `protected virtual void RegisterDiagnosticTools()`, and `protected override void RegisterDiagnosticMethods()` (calls `RegisterDiagnosticTools()`) |
| `JsonRpcServer` | `protected virtual void RegisterBuiltInMethods()` | `protected virtual void RegisterDiagnosticMethods()` |
| `McpServer` (stdio) | private | private |

The constructor calls `RegisterProtocolMethods()` always and `RegisterDiagnosticTools()` only when `includeDiagnosticTools` is true.

### Find

```bash
grep -rn "RegisterBuiltInMethods" --include=*.cs .
```

### Change

- An override that existed only to drop the demo tools: delete it. The default already publishes none.
- An override that customized protocol methods: override `RegisterProtocolMethods()`, call `base.RegisterProtocolMethods()` first, then add or replace registrations. Without the base call the server does not speak MCP.
- An override that published a different demo set: override `RegisterDiagnosticTools()` and construct the server with `includeDiagnosticTools: true`.
- A `JsonRpcServer` subclass: rename the override to `RegisterDiagnosticMethods()` and pass `includeDiagnosticMethods: true`.

---

## 6. Schema validation enforces `additionalProperties`

### What changed

v1.x validated only `type`, `required`, and nested `properties`, and silently ignored `additionalProperties`. v2.0.0 also enforces:

- `additionalProperties: false`: an argument not declared in `properties` (and not matched by `patternProperties`) is rejected with `-32602` and the message `... has unexpected property '<name>'; the schema does not allow additional properties.`
- `additionalProperties: { <schema> }`: undeclared arguments must satisfy that schema.
- `patternProperties`: names matching a pattern are allowed and validated against the pattern's schema.

Schemas without these keywords behave exactly as before. The rules apply at every nesting level.

### Find

```bash
grep -rnE "additionalProperties|patternProperties" --include=*.cs --include=*.json .
```

### Change

For each tool whose schema sets `additionalProperties: false`, confirm that every argument its callers send is declared. Typical fixes:

- Declare the missing property in `properties`.
- Fix the caller that sends a misspelled or legacy argument name (for example `data` instead of `dataBase64`).
- Remove any application-side workaround that re-checked undeclared arguments; Voltaic now does it.

---

## 7. `JsonRpcServer` diagnostic methods

### What changed

`JsonRpcServer` (plain JSON-RPC over TCP) registered `ping`, `echo`, `getTime`, `add`, and `getClients` by default. In v2.0.0 it registers nothing by default. With `includeDiagnosticMethods: true` it registers `ping` (returns `"pong"`), `echo`, `getTime`, and `add`. `getClients` is gone; call `GetConnectedClients()` in-process.

### Change

Pass `includeDiagnosticMethods: true` if clients depend on those methods, or register your own with `RegisterMethod`.

---

## 8. New APIs you may want

### `UnregisterTool`

```csharp
bool removed = server.UnregisterTool("legacy_tool"); // true if it existed
server.NotifyToolsChanged();                          // McpHttpServer
await tcpServer.NotifyToolsChangedAsync();            // McpTcpServer, McpWebsocketsServer
```

Available on `McpHttpServer`, `McpTcpServer`, `McpServer`, and `McpWebsocketsServer`. Throws `ArgumentNullException` for a null or empty name. It does not notify clients by itself, matching `RegisterTool`.

### `PingAsync`

```csharp
await httpClient.PingAsync();                 // McpHttpClient; timeoutMs 0 uses RequestTimeoutMs
await stdioClient.PingAsync(timeoutMs: 5000); // McpClient
await wsClient.PingAsync();                   // McpWebsocketsClient
```

---

## Checklist for an agent

1. Bump the `Voltaic` package reference to `2.0.0`.
2. `grep -rn "includeDefaultMethods"`: rename or delete each hit (section 1).
3. `grep -rn "RegisterBuiltInMethods"`: replace each override (section 5).
4. Build. Fix remaining compile errors.
5. `grep -rnE "\"(getSessions|getClients)\""`: remove tool calls; use `GetActiveSessions()`/`GetConnectedClients()` in-process (section 2).
6. `grep -rnE "\"pong\"|CallAsync<string>\(\"ping\""`: switch to `PingAsync()` or accept `{}` (section 3).
7. Find client calls that invoke a `RegisterTool` name directly; route them through `tools/call` (section 4).
8. `grep -rn "additionalProperties"`: confirm callers send only declared arguments (section 6).
9. If the application relied on `echo`/`getTime`, add `includeDiagnosticTools: true` (section 2). If a plain `JsonRpcServer` relied on `ping`/`echo`/`getTime`/`add`, add `includeDiagnosticMethods: true` (section 7).
10. Remove documentation that lists Voltaic's demo tools, and any workaround for them.
11. Run the application's tests. Then connect a real MCP client and confirm `tools/list` shows only the application's tools and `ping` returns `{}`.
