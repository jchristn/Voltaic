# Upgrading Voltaic

Behavior changes that may need action when upgrading, newest first. Each table lists the change, who is affected, and what to do. [CHANGELOG.md](CHANGELOG.md) has the full release history, [COMPATIBILITY.md](COMPATIBILITY.md) the per-revision MCP conformance matrix, and [MIGRATE_V1_TO_V2.md](MIGRATE_V1_TO_V2.md) before-and-after code for v1.x to v2.0.0.

v2.1.1 needs no action (see the README's [Specification conformance](README.md#specification-conformance) section).

## Upgrading to v2.2.0

v2.2.0 adds telemetry and needs no code changes. These cases may need attention:

| Change | Who is affected | What to do |
|---|---|---|
| Voltaic records metrics and spans on the `Voltaic` meter and activity source | Hosts that subscribe to every meter or source (for example `AddMeter("*")`) | Expect the new series and spans; see [TELEMETRY.md](TELEMETRY.md). Set `VoltaicTelemetry.Enabled = false` to turn them off. |
| MCP stdio, TCP, and WebSocket clients add `traceparent`/`tracestate` to `params._meta` while a span is recorded | Servers that reject unknown `_meta` keys, or tests that compare request JSON while a tracer listens to `Voltaic` | Set `VoltaicTelemetry.PropagateTraceContext = false`. |
| HTTP clients add `traceparent`/`tracestate` headers while a span is recorded | Servers or proxies that reject unknown headers | Set `VoltaicTelemetry.PropagateTraceContext = false`. |

## Upgrading to v2.1.13

v2.1.13 closes the gaps a review of v2.1.12 found. These cases need attention:

| Change | Who is affected | What to do |
|---|---|---|
| A cancellation for an ID the server has not seen is ignored; it no longer cancels a request that arrives later with that ID | Clients that sent the cancellation before the request | Send `notifications/cancelled` after the request |
| Rate limits apply per client (principal and remote address, or remote address on TCP) across all its sessions and connections | Clients that opened several sessions or connections to get more calls per second | Raise `RateLimits` on the server, or send fewer calls |
| Queued server notifications no longer keep an idle HTTP session alive | Clients that relied on notifications to keep a session open | Keep a GET stream open or send requests |
| WebSocket `MaxMessageSize` is enforced: a larger message closes the connection with status 1009 | Peers that send messages over 1 MB (the default) | Raise `MaxMessageSize` on both sides |
| `InitializeResult` is null until the handshake of the current connection completes | Code that read it after reconnecting without `initialize` | Read it after `InitializeAsync` |
| `inputResponses` values must be input results (`-32602` otherwise), and only `tools/call`, `resources/read`, and `prompts/get` may return an input-required result | Clients that sent other values; servers whose own methods returned `McpInputRequiredResult` | Send elicitation, sampling, or roots results; return input-required results only from those methods |
| A tool argument string with an unpaired UTF-16 surrogate fails validation wherever it appears | Clients that send such strings | Send valid Unicode |
| A resource template with both a prefix and `*` (`{v:2*}`) is rejected at registration, and `:n` counts decoded characters | Templates with such variables | Use a prefix or `*`, not both |
| Every transport limits one message to `MaxMessageSize` (16 MiB on stdio, TCP, and HTTP) | Applications that exchange larger messages | Raise `MaxMessageSize` on both sides |
| `McpHttpServer` limits sessions (`MaxSessions` 10,000, `MaxSessionsPerClient` 100) | Deployments behind a proxy, where every client shares the proxy's address | Set `ClientIdentifier`, or raise the limits |
| Unreadable request IDs are null in errors before 2025-11-25 and omitted from then on | Clients that parsed these errors | Accept both forms |
| `server/discover` advertises `listChanged` and `subscribe`, and `subscriptions/listen` is served | Stateless clients | Use `McpHttpClient.ListenAsync` to receive change notifications |
| A tool result's `resultType` must be `complete`, `input_required` (with `inputRequests` or `requestState`), or `task` with the tasks extension | Handlers that set `ResultType` themselves | Return `McpInputRequiredResult` to request input |
| Numbers with exponents of more than 1,000 digits fail validation; schemas with such bounds or with a `multipleOf` over 1,000 digits are rejected | Schemas or clients using astronomically large numbers | Use numbers within range |

## Upgrading to v2.1.12

v2.1.12 closes the gaps a review of v2.1.11 found. These cases need attention:

| Change | Who is affected | What to do |
|---|---|---|
| A call waiting when its connection drops fails at once with `IOException` (it used to wait for its timeout) | Code that caught only `TaskCanceledException` for lost connections | Catch `IOException` too |
| A failed explicit `InitializeAsync` disconnects and throws `InvalidOperationException` | Code that kept the connection after a failed handshake | Reconnect before retrying |
| A string argument with an unpaired UTF-16 surrogate fails input validation | Clients that send such strings | Send valid Unicode |
| `pattern` rejects `{n}` with nothing to repeat, and groups nested more than 500 deep | Schemas with such patterns | Escape the brace (`\{`) or simplify the pattern |

## Upgrading to v2.1.11

v2.1.11 closes the gaps a review of v2.1.10 found. These cases need attention:

| Change | Who is affected | What to do |
|---|---|---|
| Tool calls, completions, and log messages are rate-limited per client (`RateLimits`: 100, 100, and 200 per second) | Servers that expect a single client to make more calls per second | Raise the limits, or set them to 0 to disable |
| `pattern` uses Voltaic's own ECMA-262 engine with `u`-flag syntax: .NET-only syntax, invalid escapes of letters (`\q`), and script or binary properties are rejected at registration | Schemas that relied on .NET regular expression behavior | Write the pattern in ECMA-262 syntax |
| A long-running request keeps its session alive; sessions are checked every half `SessionTimeoutSeconds` (between 5 and 60 seconds) | Servers that relied on sessions expiring mid-request | None expected |
| An embedded resource with its own `$schema` is validated in that dialect | Schemas that mixed dialects | Declare the dialect you mean |

## Upgrading to v2.1.10

v2.1.10 closes the gaps a review of v2.1.9 found. These cases need attention:

| Change | Who is affected | What to do |
|---|---|---|
| A request method sent without an `id` (for example `tools/call`) is never run; HTTP answers 400 | Clients that invoked methods as JSON-RPC notifications | Send requests with an `id` |
| `ResponseKeepAliveMs` defaults to 15000: a `2026-07-28` handler that runs silently for more than 15 seconds gets an SSE response (status 200) with keep-alives, so a disconnect cancels it | Clients that relied on an exact late status after a slow stateless tool | Handle the error as the final SSE event, or set `ResponseKeepAliveMs = 0` |
| Progress is rate-limited: servers send at most one update per `ProgressIntervalMs` (20 ms) per request, plus the final one; clients drop progress for tokens their requests did not carry | Handlers or applications that relied on every rapid update | Set `ProgressIntervalMs = 0` on the server or client |
| `pattern` rejects syntax ECMA-262 does not define (`\A`, `\Z`, `\z`, `\G`, `\a`, `\e`, inline options, comments, atomic and conditional groups) and script properties | Schemas that used .NET regular expression syntax | Use ECMA-262 syntax |
| `ProtectedResourceMetadata.Resource` must be an absolute http(s) URL without a fragment | Servers that used another identifier | Use the server's URL |
| Stateless results get `ttlMs` and `cacheScope` by method (the six cacheable methods), not by result type | Custom methods that returned list results | Set the fields yourself if a custom method needs them |
| `CallToolStatelessAsync` without a callback answers input requests with registered request handlers instead of returning the input-required result | Callers that relied on getting the interim result while handlers were registered | Pass a callback, or unregister the handler |

## Upgrading to v2.1.9

v2.1.9 closes the gaps a review of v2.1.8 found. These cases need attention:

| Change | Who is affected | What to do |
|---|---|---|
| Schemas with malformed keyword values (an unknown `type` name, a non-numeric `minimum`, a negative `maxLength`, an invalid `pattern`, a non-schema property value, and so on) are rejected when the tool is registered; they used to be ignored keyword by keyword | Tools with such schemas | Fix the keyword value; unknown keywords are still ignored |
| `pattern` follows ECMA-262 fully: `$` matches only at the end (not before a final newline), `.` does not match `\r`, `\u2028`, or `\u2029`, and `\s` includes Unicode spaces such as `\u00a0` | Patterns that relied on .NET's `$`, `.`, or `\s` | Adjust the pattern |
| Protected resource metadata is served only at the well-known URL derived from `ProtectedResourceMetadata.Resource` (for `https://host/mcp`, `/.well-known/oauth-protected-resource/mcp`) | Authentication handlers whose `BearerChallenge` names the root form (as the earlier README example did), and clients that fetched the root form for a resource with a path | Pass null as the challenge URL, so Voltaic adds the derived one; clients should use the URL from the `resource_metadata` challenge parameter |
| On handshake sessions, a tool whose typeless output schema only describes objects must return an object (`-32603` otherwise) | Tools that returned arrays or scalars under such a schema | Return an object, or give the schema a non-object type (it is then sent to `2026-07-28` clients only) |
| A draft-07 `$ref` hides every sibling keyword, including `$id` | draft-07 schemas that placed an anchor `$id` beside a `$ref` | Move the `$id` to a schema without `$ref` |
| Clients refuse server batches on `2025-06-18` and later, answer a `null` request ID with `-32600`, and answer a handler result that is not an object with `-32603` | `RegisterRequestHandler` handlers that return strings or arrays | Return an object |

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
| `ResponseKeepAliveMs` defaults to 0 (15000 from v2.1.10) | Servers relying on keep-alives for disconnect detection | Set it above 0; see [Status codes and disconnects on HTTP](README.md#specification-conformance) |
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
