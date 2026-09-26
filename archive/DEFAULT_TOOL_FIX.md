# Default Tool Fix

Plan for letting MCP servers built on Voltaic ship without Voltaic's built-in demo tools, while keeping
the MCP protocol methods they need.

> **Status: shipped in v2.0.0.** See `CHANGELOG.md` and `MIGRATE_V1_TO_V2.md`. Where the release differs
> from this plan:
>
> - The diagnostic-tool default is `false` (Option B), released as a major version.
> - `getSessions` and `getClients` were removed outright rather than scoped to the caller. Host code
>   can still call `GetActiveSessions()`/`GetConnectedClients()` in-process.
> - No `ping` diagnostic tool is kept. The protocol `ping` always returns `McpEmptyResult` (`{}`, plus
>   `resultType` under `2026-07-28`); there is no `"pong"` compatibility shim, so v1.x `McpHttpClient`
>   instances cannot connect to v2.0.0 servers.
> - Tools are no longer registered as bare methods at all (the "better approach" in step 2), which also
>   closes related finding 1. Related finding 2 (`additionalProperties`) is fixed in the same release,
>   together with `patternProperties`.
> - `UnregisterTool` does not send `notifications/tools/list_changed` by itself, matching `RegisterTool`;
>   applications call `NotifyToolsChanged()`/`NotifyToolsChangedAsync()`.
> - `JsonRpcServer` got the same treatment: `includeDiagnosticMethods` (default `false`) and no `getClients`.
> - `PingAsync` was added to `McpHttpClient`, `McpClient`, and `McpWebsocketsClient`.

Line numbers refer to commit `f646623` (v1.1.0 plus the unreleased JSON-RPC endpoint fix).

---

## The problem

Every Voltaic MCP server adds its own demo tools to the host application's tools. A server built on
`McpHttpServer` shows these in `tools/list` next to the application's tools:

| Tool | What it does |
|---|---|
| `ping` | Returns `"pong"` |
| `echo` | Returns its `message` argument |
| `getTime` | Returns the server's UTC time |
| `getSessions` | Returns the ID of every active MCP session on the server |

The TCP server adds `getClients` in place of `getSessions`. The stdio server adds `ping`, `echo`, and
`getTime`.

The host application has no way to remove them:

- **The constructor flag removes too much.** `includeDefaultMethods: false` skips the demo tools, but
  it also skips `initialize`, `tools/list`, `tools/call`, `server/discover`, and every other protocol
  method. The result is not a working MCP server.
- **Subclasses cannot rebuild the protocol methods.** `RegisterBuiltInMethods()` is `protected virtual`
  on the HTTP, TCP, and WebSocket servers, so a subclass can override it. But the protocol handlers call
  `_Endpoint`, a private field of the internal type `McpEndpoint`, so a subclass cannot register them
  itself. On `McpServer` (stdio) the method is `private`.
- **There is no unregister API.** No `UnregisterTool` or `RemoveTool` method exists, and the tool
  registry is inside the internal `McpEndpoint`.

This was found while upgrading PepperX to Voltaic 1.1.0. Claude Code 2.1.281, connected as a live
client, listed 20 tools: PepperX's 16 plus these 4.

---

## Why it matters

1. **`getSessions` exposes other clients' session IDs.** On the handshake-era Streamable HTTP
   transport, the `Mcp-Session-Id` header is the only thing that identifies a session. A caller who
   knows another client's session ID can end that session with `DELETE /mcp` (`McpHttpServer.cs:1479`).
   It can also open that session's SSE stream with `GET` (`McpHttpServer.cs:1391`) and receive that
   client's notifications. When an `AuthenticationHandler` is set, every authenticated caller still
   sees every session, so one tenant can see another's. `getClients` on TCP is lower risk but also
   reveals other connections.

2. **Agents see tools that are not part of the product.** MCP clients show every tool in
   `tools/list` to the model. `echo`, `getTime`, and `ping` take up the model's context, can be chosen
   instead of the application's own tools, and make the server look unfinished. PepperX had to document
   them ("Voltaic also registers its own … diagnostics") because it could not remove them.

3. **Their names can clash with application tools.** An application that registers its own `ping` or
   `echo` silently replaces Voltaic's version. For `ping`, that also replaces the protocol `ping`
   handler (see below).

4. **Nobody chose to publish them.** The default is `true` on every server, so any Voltaic MCP server
   publishes these tools unless its author reads the source.

---

## Where it originates

### The constructor flag controls both groups

Each MCP server passes one boolean to one method. That method registers the protocol methods and the
demo tools together:

| Server | Constructor | Registration method | Demo tools |
|---|---|---|---|
| `McpHttpServer` | `Mcp/McpHttpServer.cs:271`, flag checked at `:286` | `protected virtual RegisterBuiltInMethods()` at `:991` | `RegisterTool` calls at `:1029` (`ping`), `:1039` (`echo`), `:1060` (`getTime`), `:1070` (`getSessions`, calls `GetActiveSessions()` at `:819`) |
| `McpTcpServer` | `Mcp/McpTcpServer.cs:87`, flag checked at `:92` | `protected override RegisterBuiltInMethods()` at `:490` | `:512` (`ping`), `:522` (`echo`), `:543` (`getTime`), `:553` (`getClients`) |
| `McpServer` (stdio) | `Mcp/McpServer.cs:92`, flag checked at `:99` | `private RegisterBuiltInMethods()` at `:537` | `:570` (`ping`), `:580` (`echo`), `:601` (`getTime`) |
| `McpWebsocketsServer` | `Mcp/McpWebsocketsServer.cs:180`, flag checked at `:192` | `protected virtual RegisterBuiltInMethods()` at `:794` | Registered as plain methods, not tools: `ping`, `echo`, `getTime`, `getClients` (about `:816`–`:823`) |
| `JsonRpcServer` (core) | `Core/JsonRpcServer.cs:98`, flag checked at `:108` | `protected virtual RegisterBuiltInMethods()` at `:323` | Plain methods: `echo`, `getTime`, `add`, `getClients`, `ping` |

`McpTcpServer` inherits from `JsonRpcServer` but calls `base(ip, port, false)`. The core JSON-RPC
demo methods are therefore not registered twice, and only the MCP override at `:490` applies.

On WebSocket and plain JSON-RPC the demos are bare methods, so they do not appear in `tools/list`.
They are still callable. The problem that shows up in `tools/list` is limited to HTTP, TCP, and stdio.

### The registry cannot be reached

- `McpEndpoint` is `internal sealed` (`Mcp/McpEndpoint.cs:13`). It holds the tool list and every
  protocol handler (`Initialize`, `Discover`, `ListTools`, `CallToolAsync`, and so on).
- Each server keeps its endpoint in a private `_Endpoint` field.
- `McpEndpoint.RegisterTool` (`:135`) replaces a tool with the same name, but nothing removes one.

---

## Coupling to resolve first

The demo `ping` tool currently also serves the MCP protocol's `ping` request. Removing it without
changing anything else would break `ping`.

- **Each tool is registered as a method too.** `RegisterTool` puts the tool in the endpoint, then calls
  `RegisterMethod(definition.Name, handler)`: `McpHttpServer.cs:397/439/480`, `McpTcpServer.cs:206`,
  `McpServer.cs:263`, `McpWebsocketsServer.cs:276/313/350`. The demo `ping` tool therefore becomes the
  handler for the JSON-RPC method `ping`.
- **The protocol `ping` handler is never registered.** `McpEndpoint.Ping()` (`McpEndpoint.cs:130`)
  returns `McpEmptyResult`, which is the `{}` the specification requires. Nothing calls it. As a result,
  `ping` answers with the string `"pong"`, not `{}`. A string is not an `McpResult`, so under
  `2026-07-28` it would also not be stamped with `resultType`. That breaks the spec shape, and would
  matter to any stateless client that sends `ping`.
- **`McpHttpClient` depends on the string.** `McpHttpClient.cs:192` and `:229` call
  `CallAsync<string>("ping", ...)` while connecting. They need to accept `{}`, or any result, before
  `ping` can return the correct shape.
- **The HTTP auth bypass is keyed on the method name.** `McpHttpServer.cs:1157` lets requests whose
  method is `ping` skip `AuthenticationHandler`. The bypass must stay attached to the protocol `ping`,
  not to a tool. Note that, today, an application that registers its own `ping` tool gets an
  unauthenticated tool.
- **Tests and samples use the demo tools.** `Test.Shared/McpHttpAdvancedProtocolSuites.cs` (from `:53`)
  uses `ping` as a probe. `Test.Shared/JsonRpcTcpIntegrationSuites.cs:23-24` calls `ping` and `echo`.
  The interactive test hosts under `src/Test.*` and `README.md:720` document `getSessions`,
  `getClients`, `echo`, and `getTime`.

---

## Proposed fix

### 1. Separate protocol methods from demo tools (every MCP server)

Split each `RegisterBuiltInMethods()` into two methods:

- **`RegisterProtocolMethods()`** always runs. It registers `initialize`, `server/discover`,
  `tools/*`, `resources/*`, `prompts/*`, `completion/complete`, `logging/setLevel`, the notification
  handlers, and the protocol `ping`, wired to `_Endpoint.Ping`.
- **`RegisterDiagnosticTools()`** runs only when a new option is on. It registers `echo`, `getTime`,
  and `getSessions`/`getClients`, plus a `ping` tool if one is still wanted.

Suggested option: a constructor parameter `includeDiagnosticTools`, or an
`IncludeDiagnosticTools` property that must be set before `StartAsync`.

Keep `includeDefaultMethods` so existing callers compile. Its current meaning is "no protocol methods
either". Either keep that meaning and document it, or mark it `[Obsolete]` and point to the new option.

### 2. Stop demo tools from replacing protocol methods

Register the protocol `ping` separately so that no tool can replace it. The simplest approach is to
have `RegisterTool` skip `RegisterMethod` for names that are reserved protocol methods (`ping`,
`initialize`, anything containing `/`). The better approach is to stop registering tools as bare
methods at all; see "Related findings".

### 3. Add `UnregisterTool(string name)`

Add it to `McpEndpoint` and expose it on each MCP server. It should remove the tool from the registry
and its method from `_Methods`, and send a `tools/list_changed` notification where the transport
supports one. This lets applications remove individual tools without subclassing. The same pattern can
cover resources and prompts later.

### 4. Fix the client side of `ping`

Change `McpHttpClient`'s connection probe (`:192`, `:229`) to accept any successful result, for example
`CallAsync<object>` or a check that there is no error. It must work against both old servers
(`"pong"`) and fixed servers (`{}`).

### 5. Choose a default and version it

| Option | Default for diagnostic tools | Version |
|---|---|---|
| A (conservative) | `true`. Existing behavior is kept; applications opt out. | 1.2.0 |
| B (recommended) | `false`. Servers publish only what their author registered. | 2.0.0, or 1.2.0 with a prominent CHANGELOG note |

Option B is recommended because of the `getSessions` exposure. It is still a behavior change for
anyone who calls `echo` or `getTime` today. Either way, the protocol `ping` returning `{}` in place of
`"pong"` is a wire change and must be listed under breaking changes.

---

## Test plan

Add Touchstone cases to `Test.Shared`, run on the console, xUnit, and NUnit runners under `net8.0` and
`net10.0`.

**Should succeed**
- With diagnostic tools off, `tools/list` on HTTP, TCP, and stdio returns only the application's
  tools. `initialize`, `server/discover`, `tools/call`, and the protocol `ping` still work on every
  transport.
- The protocol `ping` returns `{}` under handshake revisions, and `{}` with `resultType: "complete"`
  under `2026-07-28`.
- With diagnostic tools on, the current four tools appear and behave as today.
- `ping` still bypasses `AuthenticationHandler` with diagnostic tools off.
- `McpHttpClient` connects to a server with diagnostic tools off, and to one running Voltaic 1.1.0.
- `UnregisterTool` removes a tool from `tools/list`, makes `tools/call` for it return "not found", and
  sends `tools/list_changed` to subscribed sessions.

**Should fail**
- With diagnostic tools off, `tools/call` for `getSessions`, `echo`, or `getTime` returns `-32602`
  ("not found"). Calling them as bare methods returns `-32601`.
- An application tool named `ping` does not replace the protocol `ping`, and does not inherit its auth
  bypass. A protocol `ping` still returns `{}`.
- `UnregisterTool` on an unknown name is a no-op or throws, whichever is chosen and documented; the
  test asserts that choice.

**Regression**
- Replay the Claude Code 2.1.x stateless sequence (`McpVersion.StatelessResults`) with diagnostic
  tools off, and confirm the tool count equals the tools the application registered.

---

## Related findings (separate fixes)

These came up in the same investigation. They are recorded here so they are not lost.

1. **Tools can be called as bare JSON-RPC methods, which skips schema validation.** Because
   `RegisterTool` also calls `RegisterMethod(name, handler)`, a client can send
   `{"method":"pepperx_object_write", ...}` directly. That bypasses `tools/call`, and with it
   `McpSchemaValidator.Validate` (`McpEndpoint.cs`, inside `CallToolAsync`) and the output-schema
   checks. Fixing the "Stop demo tools from replacing protocol methods" step properly (no bare-method
   registration for tools) closes this too.
2. **`McpSchemaValidator` ignores `additionalProperties: false`.** It checks only `type`, `required`,
   and nested `properties` (`Mcp/McpSchemaValidator.cs`). Arguments a tool does not declare are dropped
   silently. In PepperX, a client that sent `data` in place of `dataBase64` wrote an empty object with
   no error. PepperX now works around this itself; Voltaic should enforce `additionalProperties: false`
   so every host gets the check.
