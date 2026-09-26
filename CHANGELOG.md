# Changelog

## v2.1.5
Brings Voltaic to full conformance with the MUST and SHOULD requirements of the five MCP revisions, on every transport. A review against v2.1.4 found lifecycle, JSON-RPC, notification, cancellation, and HTTP requirements that were not enforced or not implemented; all are fixed here. The only remaining differences are optional features and a short list of deliberate leniencies (README "Specification conformance"). stdio, TCP, and WebSocket servers now share one message processor with per-connection session state, so every rule below applies to all of them alike.

### Lifecycle and JSON-RPC (all transports)
- **`initialize` comes first and only once.** Requests other than `ping` (and `server/discover`) before `initialize` get `-32600`, as does a second `initialize`. `initialize` must carry `protocolVersion`, `capabilities`, and `clientInfo` (`-32602` otherwise); the result includes `instructions` when `ServerInstructions` is set.
- **Clients initialize on connect.** `McpClient`, `McpTcpClient`, and `McpWebsocketsClient` send `initialize` and `notifications/initialized` after connecting (new `AutoInitialize`, default true; `InitializeAsync`, `InitializeResult`, `ProtocolVersion`, `ClientName`, `ClientVersion`, `ClientCapabilities`). A failed handshake disconnects and `ConnectAsync`/`LaunchServerAsync` return false.
- **Envelope rules.** A `null` id, a missing or wrong `jsonrpc`, a non-string `method`, or a message that is not an object gets `-32600`; MCP `params` that are not an object get `-32602`.
- **Batches only on 2025-03-26.** Batches are answered with an array on `2025-03-26` sessions and rejected otherwise; an empty batch is `-32600`, and `initialize` may not be batched. Every client now accepts batches from the server.
- **Concurrent requests and cancellation.** Requests on one connection run concurrently (a `ping` is answered while a tool runs), writes are serialized, and `notifications/cancelled` cancels the handler's token and suppresses its response (`initialize` cannot be cancelled). Clients send `notifications/cancelled` when a call times out or its token is cancelled.
- **TCP framing.** `McpTcpServer` accepts newline-delimited JSON (the stdio framing) and Content-Length framing, detected per connection; a line that is not JSON closes the connection. `McpTcpClient` sends newline-delimited JSON by default (new `NewlineDelimited`; set false for Voltaic TCP servers before 2.1.5).
- **stdio keeps stdout clean.** `McpServer.RunAsync` redirects `Console.Out` to stderr while it runs, so a tool that writes to the console cannot corrupt the protocol stream.

### Notifications
- `list_changed` notifications go only to sessions that completed `initialize`; `notifications/resources/updated` only to sessions subscribed to that URI (subscriptions are per session and removed by `resources/unsubscribe`).
- `logging/setLevel` is applied per session, and log notifications below the level are not sent; an unknown level is `-32602`.
- Progress goes only to the client whose in-flight request carries the token, must increase (`ArgumentOutOfRangeException` otherwise), and stops when the request ends. New `McpToolCallContext.ProgressToken`, `ReportProgressAsync`, `LogAsync`, and `ClientSupports`.
- `McpServer` (stdio) gains `NotifyToolsChangedAsync`, `NotifyResourcesChangedAsync`, `NotifyPromptsChangedAsync`, `NotifyResourceUpdatedAsync`, `NotifyLogMessageAsync`, and `NotifyProgressAsync`, matching TCP and WebSocket. `NotifyCancelledAsync`/`NotifyCancelled` are obsolete no-ops: a server may only cancel requests it sent.
- Results and notifications are downgraded to the negotiated revision: fields a revision does not define are removed, and content types it lacks (audio before 2025-03-26, `resource_link` before 2025-06-18) become text.

### Features
- Tool names must be 1-128 characters of letters, digits, `_`, `-`, and `.` (`ArgumentException` at registration). Input and output schemas must be objects (`type: "object"` is added when missing; another type is rejected). A tool with an `outputSchema` must return `structuredContent` (`-32603`). `tools/call` arguments must be an object, and prompt arguments must be strings (`-32602`).
- Resource not found is `-32002` on handshake-era revisions (`-32602` on 2026-07-28), with the URI in `data`. Resource templates are matched with full RFC 6570 support (reserved, fragment, label, path, and query expressions).
- Completions return at most 100 values with `total` counting all of them, and references to unknown prompts or resources are `-32602`.
- `initialize` always advertises `tools`, `resources` (with `subscribe`), `prompts`, `completions`, and `logging`, which all work. Cursors are opaque and stay valid when the list changes between pages.
- Tool input schemas are validated with the full JSON Schema 2020-12 vocabulary.
- `McpAnnotations` gains `Audience`, `Priority`, and `LastModified`; `McpResourceLinkContent.Name` is required (never null) and gains `Title`, `Description`, `Size`, `Annotations`, `Icons`, and `Meta`.
- New `ServerInstructions` and `PageSize` on every MCP server (they were HTTP-only or not configurable). The servers' `ProtocolVersion` property is obsolete: `initialize` must name a version, so it had no effect.

### 2026-07-28
- `_meta` must carry the protocol version and `clientCapabilities` (400 `-32602`); an optional `io.modelcontextprotocol/logLevel` (`McpProtocol.MetaLogLevelKey`) turns on log notifications for that request. Every result carries `resultType` (a handler returning something that is not an object is `-32603`) and `serverInfo` in `_meta`. `ping`, `logging/setLevel`, `resources/subscribe`, and `resources/unsubscribe` are not served (404 `-32601`). A tool asking for input the client declared no capability for gets `-32021`.
- On HTTP, a POST response switches to SSE only when progress or log notifications are sent, and closing a stateless response stream cancels the request (keep-alives detect it; new `McpHttpServer.ResponseKeepAliveMs`, default 2000).
- `McpHttpClient` retries with a server-listed version after `-32022`, re-issues a request once with a new ID when its SSE stream ends without a response, rejects unknown `resultType` values, and merges caller `_meta` with its own.

### HTTP
- A 401 always carries `WWW-Authenticate` (`Bearer`, with `resource_metadata` when `ProtectedResourceMetadata` is set). New `AuthenticationResult.InsufficientScope` and `McpInsufficientScopeException` (code `-32003`) produce 403 with `error="insufficient_scope"` and the scope; `BearerChallenge` gains a `scope` parameter.
- `DELETE /mcp` requires the session's owner (404 otherwise). Endpoint paths match exactly, and a session ID in the query string is accepted only on `/events`. Unknown-session errors on GET and DELETE have JSON-RPC bodies.
- `McpHttpClient` initializes a new session and retries once after a 404, sends `DELETE` on `Disconnect`, sends `notifications/cancelled`, and sends `ClientCapabilities` in `initialize`.

### Tests
- New suites `McpStreams.Conformance`, `McpStreams.Notifications`, `McpStreams.Features`, `McpHttp.Conformance`, `McpHttp.ClientConformance`, and `Mcp.SchemaValidation`; existing tests were updated for the stricter lifecycle (644 in total). `Test.McpServer` gained a `chatty` tool that writes to the console.

## v2.1.4
Closes MCP specification deviations found by a full review of Voltaic against the five published revisions. Verified end to end with the MCP Inspector CLI 2.8.0, the official Python MCP SDK 2.2.0 (as a client of Voltaic over HTTP and stdio in automatic, legacy, and 2026-07-28 modes, and as a server for Voltaic's clients), Claude Code 2.1.281 (HTTP, authenticated HTTP, stdio), and the official A2A Python SDK 1.1.5.

### Clients answer server requests
- **Every client now answers requests the server sends it.** `McpClient`, `McpWebsocketsClient`, `McpTcpClient`, `JsonRpcClient`, and `McpHttpClient` treated a server request (a message with `method` and `id`) as a stray response and dropped it, so a server that pinged its clients (the specification requires the receiver to answer `ping` promptly) never got an answer. MCP clients now answer `ping` with `{}`; other methods are answered by handlers registered with the new `RegisterRequestHandler`/`UnregisterRequestHandler`, and anything else with `-32601`. A handler's `McpProtocolException` is sent as its error, and other exceptions as `-32603` without details. `McpHttpClient` POSTs each answer on the session, declares the `roots`, `sampling`, and `elicitation` capabilities in `initialize` when handlers for them are registered, and ignores stream requests in stateless 2026-07-28 mode, where they are forbidden. Writes are now serialized on every stream transport.

### Server fixes
- **Tool handler exceptions are tool execution errors.** A handler that throws now produces a result with `isError: true` instead of JSON-RPC `-32603`, as the specification prescribes for API and business-logic failures, so the model can react. Tool outputs must be sanitized, so the text is generic by default and the exception goes to `Log` (the `-32603` error used to carry the message in `data`). New `McpToolException` sends its message to the model as written, and new `IncludeToolExceptionMessages` (on every MCP server) shows every message. `McpProtocolException` still produces a protocol error, and cancellation still propagates. An output-schema violation is now `-32603` (a server fault) instead of `-32602`.
- **`ping` follows the session and authentication rules.** A handshake-era `ping` on `/mcp` without `MCP-Session-Id` now gets 400 like every request other than `initialize`, and `ping` no longer skips the `AuthenticationHandler` (the MCP authorization specification requires 401 for a missing or invalid token on every request). Use `GET /` as an unauthenticated health check.
- **`RequireInitializedSessions` is obsolete.** It always reads `true` and setting it has no effect; sessions come only from a successful `initialize`.
- **`AuthenticationHandler` may read the request body**; the body is buffered before the handler runs.
- **Resumable GET streams.** `GET /mcp` now starts with a priming event (an event ID, `retry`, and empty `data`) as MCP 2025-11-25 recommends, every message carries an event ID (`{streamId}-{n}`), and a client that reconnects with `Last-Event-ID` receives the messages it missed on that stream, never another stream's. New `SseReplayBufferSize` (default 100) and `SseRetryIntervalMs` (default 1000). A resumed stream takes over from the abandoned connection, which previously kept consuming the session's messages until a write failed; a message that connection had already taken is handed to the new one. Streams send `X-Accel-Buffering: no`.
- **`x-mcp-header` (2026-07-28).** `RegisterTool` rejects annotations the specification forbids, and a stateless `tools/call` whose `Mcp-Param-{Name}` headers are missing, extra, malformed, or different from the arguments gets 400 `-32020` before the tool runs. Base64 sentinel values are decoded strictly (for `Mcp-Name` too), integers are compared numerically and must be within the JavaScript safe range, an argument of the wrong type is left to input validation, and nullable primitive types (`["string","null"]`) may be annotated. New constants `McpProtocol.ParamHeaderPrefix` and `McpProtocol.HeaderAnnotationKeyword`.

### McpHttpClient
- Parses SSE per the event stream rules (optional space after `data:`, `id`, `retry`, comments, priming events). It reopens a closed GET stream with `Last-Event-ID` after the server's `retry` (or `SseReconnectDelayMs`), stops on HTTP 4xx or after `SseMaxReconnectAttempts` failures, and resumes a POST response stream that ends before its response. New `AutoReconnectSse`, `SseReconnectDelayMs`, `SseMaxReconnectAttempts`. `StopSse` now also stops a stream that is waiting to reconnect.
- Sends `MCP-Protocol-Version` on every request after `initialize`, also when the server issues no session, and declares the capabilities of registered handlers in the `clientCapabilities` of stateless requests.
- In stateless mode it removes tool definitions with invalid `x-mcp-header` annotations from `tools/list` results (logging a warning), mirrors annotated arguments into `Mcp-Param-{Name}` headers, and on `-32020` lists the tools again and retries once. `Mcp-Name` is taken from `params.name`/`params.uri` when no name is passed, and values with leading or trailing whitespace are Base64-encoded as required.

### Tests
- New suites `McpClients.ServerRequests` (9), `McpHttp.SseResumability` (4), and `Mcp.HeaderParameters` (7), and new `Mcp.SpecConformance` and `McpHttp.Advanced` cases; tests for the changed session, `ping`, and tool-error behavior were rewritten (572 in total).
- The README "Specification conformance" section now lists the known gaps that remain. `Test.McpServer` gained a `--probe-client` mode that sends requests to the client.

## v2.1.3
Closes the remaining MCP gaps and aligns A2A with the v1.0 wire format. Every MCP transport now serves `2026-07-28`, tool handlers can ask the user for input, `McpHttpClient` reads SSE responses, and A2A push configs and HTTP+JSON errors match the specification. Verified end to end with the MCP Inspector CLI 2.8.0, the official Python MCP SDK 2.2.0 (automatic, legacy, and 2026-07-28 modes over HTTP and stdio, including a Multi Round-Trip elicitation), Claude Code 2.1.281 (HTTP, authenticated HTTP, stdio), and the official A2A Python SDK 1.1.5 as a client of Voltaic (JSON-RPC, HTTP+JSON, gRPC; 36 checks) and as a server for Voltaic's clients (JSON-RPC, HTTP+JSON; 11 checks).

### MCP
- **`2026-07-28` on stdio, TCP, and WebSocket.** These servers served only the handshake-era revisions. A request whose `params._meta["io.modelcontextprotocol/protocolVersion"]` names `2026-07-28` is now served statelessly: `server/discover` is registered, results carry `resultType` and, where cacheable, `ttlMs`/`cacheScope`, and an unknown `_meta` version gets `-32022` with the supported list. Requests without that field and `initialize` are unchanged, so one server serves both eras. The official Python SDK's stdio `auto` mode, which probes with `server/discover`, now negotiates `2026-07-28`.
- **Multi Round-Trip Requests reach tool handlers.** New `McpToolCallContext` (ambient, like `RpcCallContext`) gives a handler the client's `InputResponses`, the echoed `RequestState`, `IsRetry`, and `CanRequestInput`. Before, a handler returning `McpInputRequiredResult` could never see the answers, so every retry asked again. Works on every transport.
- **Input requests never reach handshake-era clients.** A handler that returns `McpInputRequiredResult` to a handshake-era caller now produces a tool result with `isError: true` explaining that the tool needs `2026-07-28`; the client previously received a result it could not parse.
- **`McpHttpClient` reads SSE responses to POST.** The Streamable HTTP specification lets servers answer a POST with `text/event-stream` and requires clients to accept it; `McpHttpClient` failed against such servers (for example the official Python SDK). It now reads the stream as it arrives, raises notifications through `NotificationReceived`, and completes with the response whose `id` matches.

### A2A
- **Push notification configs use the flat v1.0 shape.** `TaskPushNotificationConfig` and `CreateTaskPushNotificationConfigRequest` are written with `url`, `token`, and `authentication` at the top level (plus `id`, `taskId`, and `tenant`), as A2A v1.0 defines; they were nested under `pushNotificationConfig`/`config`, which the official SDKs rejected. Get and delete requests name the configuration with `id` instead of `configId`. The older shapes are still read.
- **`ListTaskPushNotificationConfigs`.** `A2AProtocol.ListTaskPushNotificationConfig` is now the v1.0 method name (plural). Servers accept both names (`A2AProtocol.ListTaskPushNotificationConfigLegacy`).
- **HTTP+JSON errors are `google.rpc.Status`.** Errors carry `code`, `status`, `message`, and an `ErrorInfo` detail whose `reason` names the A2A error (for example `TASK_NOT_FOUND`, domain `a2a-protocol.org`). `A2AHttpJsonClient` maps the reason back to `A2AErrorCode`, so a missing task from the official SDK server is `TaskNotFound` rather than a generic error; it still reads the body that Voltaic 2.1.2 and earlier sent.
- **`A2AGrpcServer` push settings.** `PushNotificationUrlValidator`, `PushNotificationTimeoutMs`, and `PushNotificationMaxAttempts` are now exposed, as on `A2AHttpServer`; before, a gRPC server could not allow a webhook host or change delivery settings.
- **`localhost` gRPC connects without a two-second delay.** Watson binds one address, so a server on `localhost` listened on `127.0.0.1` only, and clients that try `::1` first (including `HttpClient`) waited about two seconds on Windows for every new connection. A `localhost` host name now also listens on `::1`, and `*`/`+` also on `::`; the IPv6 listener is best effort and is logged when it cannot bind.
- **`A2AGrpcServer.Stop` releases the port.** Stopping cancelled the start token before stopping Watson; Watson's accept loop then marked itself stopped, its `Stop` threw, and the listening socket stayed bound, so a restart on the same port could fail. Watson is now stopped directly, and cancelling the `StartAsync` token calls `Stop`. `StartAsync` with an already-cancelled token throws `OperationCanceledException`.

### Tests
- New `McpStreams.Stateless` suite (stdio, TCP, WebSocket), and new `Mcp.SpecConformance` and `A2A.Hardening` cases for everything above (549 in total). The A2A suite runs in about 7 seconds instead of 60, now that gRPC cases no longer wait for the IPv6 fallback.

## v2.1.2
Fixes the remaining deviations from the MCP and A2A specifications, relaxes checks that were stricter than the specifications where that costs no safety, completes A2A push notifications, and hardens `A2AGrpcServer`. Every public type and member is now documented, and missing documentation fails the build. Verified end to end with the MCP Inspector CLI 2.8.0, the official Python MCP SDK 2.2.0 (automatic, legacy, and 2026-07-28 modes over HTTP; stdio), and Claude Code 2.1.281 (HTTP, authenticated HTTP, stdio).

### MCP fixes
- **Invalid tool arguments are tool execution errors.** A `tools/call` whose arguments fail the input schema now returns a result with `isError: true` and a message naming the problem, as the 2025-11-25 and 2026-07-28 specifications require, so the model can correct itself. Before, it was a JSON-RPC `-32602` protocol error. The handler still never runs. Unknown tools and malformed requests remain protocol errors. Found by testing with the official Python SDK.
- **`initialize` with an unknown version negotiates.** The specification requires a server to answer with a version it supports; Voltaic answered `-32602`. It now answers with `MaximumHandshakeProtocolVersion`, on every transport.

### MCP relaxations (no loss of safety)
- A missing `Accept` header counts as `*/*`, and media ranges such as `application/*` match.
- `POST /mcp` without `Content-Type` is accepted when there is no `Origin` header (a non-browser client). Browser requests without it still get 415.
- On `2026-07-28`, notification POSTs no longer require `MCP-Protocol-Version` or `Mcp-Method`; the revision defines no header rules for notifications. Headers that are present must still match.
- CORS preflights from allowed origins get back the header names they request (valid tokens only), so custom headers such as `X-API-Key`, `Mcp-Param-*`, and tracing headers work. `WWW-Authenticate` is added to `Access-Control-Expose-Headers` (`McpHttpServer` and `A2AHttpServer`) so browser clients can read OAuth challenges.

### A2A fixes
- **Push notifications are delivered.** `A2AHttpServer` (and `A2AGrpcServer`, which shares its engine) now POSTs every task event, as a `StreamResponse` with `Content-Type: application/a2a+json`, to each webhook registered for the task, with `Authorization: {scheme} {credentials}` and `X-A2A-Notification-Token`. Deliveries are ordered per webhook, time out after `PushNotificationTimeoutMs` (default 10 s), and are retried with exponential backoff up to `PushNotificationMaxAttempts` (default 3). A config in `SendMessageConfiguration` is registered before the handler runs. Before, configs were stored but nothing was ever sent.
- **Webhook SSRF protection** (A2A security guidance): webhook URLs must be `http`/`https` without user information and must not target `localhost` or loopback, private, link-local, carrier-grade NAT, or multicast addresses. The check is repeated at connect time against the resolved addresses, which defeats DNS rebinding; redirects are not followed and no proxy is used. `PushNotificationUrlValidator` replaces the policy.
- Push config operations on a task that does not exist return `TaskNotFound`, as the specification lists; a missing configuration returns `TaskNotFound` naming the configuration (it named the task before). A missing URL or task ID is `InvalidParams`.
- JSON-RPC `DeleteTaskPushNotificationConfig` swallowed every error; errors now reach the caller.
- `SendMessageConfiguration.PushNotificationConfig` is serialized as `taskPushNotificationConfig`, the A2A v1.0 name; the older `pushNotificationConfig` is still read.
- JSON-RPC errors are returned with HTTP 200 and the JSON-RPC error body (the JSON-RPC-over-HTTP convention); they were 400/404, which made Voltaic's own `A2AClient` throw `HttpRequestException` instead of `A2AProtocolException`. `A2AClient` now reads a JSON-RPC error body whatever the status. HTTP+JSON errors follow the A2A mapping (404, 500 for internal errors, 400).
- Internal errors (exceptions other than `A2AProtocolException`) are reported to clients as a generic "Internal error" on the JSON-RPC, HTTP+JSON, and gRPC bindings; the details go to the `Log` event. Before, the exception message was sent to the caller.

### A2A gRPC server hardening
- **Security:** `GET /extendedAgentCard` required no authentication on `A2AGrpcServer`, handing the authenticated extended card to anyone. Only the public card is now exempt, as on `A2AHttpServer`.
- `OriginPolicy` and `RestrictToLoopbackClients` (default on for loopback host names), as on the other HTTP servers.
- `AuthenticationResult.Headers` (for example `WWW-Authenticate`) are written on authentication failures.
- Protobuf parse errors map to `INVALID_ARGUMENT` and cancellations to `CANCELLED` instead of `INTERNAL`.

### Lifecycle and robustness
- `A2AHttpServer` and `A2AGrpcServer` can be started again after `Stop()`; `Stop()` waits for the accept loop; starting a disposed server throws `ObjectDisposedException`.
- `InMemoryA2ATaskStore.SaveTaskAsync` throws `ArgumentNullException` for a null task (it threw `NullReferenceException`), and `ListTasksAsync(null)` lists everything.

### Added
- `A2AHttpServer.PushNotificationUrlValidator`, `PushNotificationTimeoutMs`, `PushNotificationMaxAttempts`; `A2AGrpcServer.OriginPolicy`, `RestrictToLoopbackClients`; `A2AProtocol.NotificationTokenHeader`.
- XML documentation for every public A2A type and member (409 members had none), and `CS1591` (missing documentation) is now a build error for the library.
- Touchstone cases for all of the above: the `A2A.Hardening` suite (13 cases) and 6 more `Mcp.SpecConformance` cases (521 in total).

## v2.1.1
A specification-conformance patch, found by reviewing Voltaic against the published MCP `2025-11-25` and `2026-07-28` specifications.

### Fixed
- **Client-sent JSON-RPC responses get `202 Accepted`.** A JSON-RPC response or error POSTed to `/mcp` or `/rpc` was treated as a request and answered with `200` and a `-32601` error; the Streamable HTTP specification requires `202` with no body (or an HTTP error status). Such messages are now accepted and dropped, since Voltaic sends no requests to clients. On `/mcp` without a session they still get 400. In a batch, response entries are skipped, and a batch of only responses and notifications gets `202`. A malformed batch now gets a single parse error instead of an empty array.
- **Header-less requests assume `2025-03-26`.** When a handshake-era request has no `MCP-Protocol-Version` header and no negotiated session version, the server now assumes `2025-03-26`, as the specification requires, instead of `2025-11-25`. This affects the batching rule for such requests and the no-signal result of `McpVersionResolver.Resolve`. A session's negotiated version still takes precedence.
- **Stateless requests must carry the `_meta` protocol version.** A `2026-07-28` request whose body has no `params._meta["io.modelcontextprotocol/protocolVersion"]` is now rejected with `400` and `-32020` (HeaderMismatch), because the specification requires the `MCP-Protocol-Version` header to match it. Notifications are exempt. `McpHttpClient` and Claude Code already send it.

### Added
- `McpHttpServer.ProtectedResourceMetadata` and `McpProtectedResourceMetadata`: OAuth 2.0 Protected Resource Metadata (RFC 9728), served without authentication at `/.well-known/oauth-protected-resource` and at that path followed by the MCP endpoint path. The MCP authorization specification requires OAuth-protected servers to publish it. The setter rejects metadata without a resource or an authorization server.
- `McpProtocol.HeaderlessProtocolVersion` (`2025-03-26`) and `McpProtocol.ProtectedResourceMetadataPath`.
- The `Mcp.SpecConformance` Touchstone suite (10 cases; 503 in total).

### Documentation
- The README has a new "Specification conformance" section listing deliberate differences and unimplemented optional features, and a new "OAuth and protected resource metadata" section.
- Corrected the `AuthenticationHandler` documentation (README and XML): the handler runs for `ping`, and requests without a handler are still subject to the origin and loopback checks.
- The README now states that `/rpc` and `/events` are Voltaic-specific endpoints, not the deprecated 2024-11-05 HTTP+SSE transport.

## v2.1.0
A security release (reports: `archive/BUG_TO_FIX.md`, `archive/AUTH_BUGS.md`). It fixes handshake sessions being created for requests that never initialized, and closes the ways a web page in the user's browser, or another host on the network, could reach a Voltaic server the developer believed was local-only. Defaults change as a result; see "Upgrading to v2.1.0" in the README for the one-line fixes.

### Security fixes
- **Browser origins are validated** on `McpHttpServer`, `McpWebsocketsServer`, and `A2AHttpServer`, as the MCP Streamable HTTP specification requires. Before, any page could call tools with a preflight-free `text/plain` POST and read the result, because every response carried `Access-Control-Allow-Origin: *`, and any page could open a WebSocket (browsers do not apply CORS to WebSockets). Requests without an `Origin` header (non-browser clients) and loopback origins (`http(s)://localhost`, `127.0.0.0/8`, `[::1]`, any port) are allowed; every other origin gets 403 with no CORS headers, before preflight handling and authentication. Configure with the new `OriginPolicy` property (`AllowedOrigins`, `AllowLoopbackOrigins`, `OriginValidator`).
- **CORS echoes the allowed origin** instead of `*`, adds `Vary: Origin`, and uses an explicit `Access-Control-Allow-Headers` list (which, unlike `*`, covers `Authorization`). `/events` no longer hard-codes `*`. An `Access-Control-Allow-Origin` entry in `CorsHeaders` is ignored.
- **Servers bound to a loopback name serve loopback clients only.** On Windows, `HttpListener` (http.sys) serves a `localhost` prefix on every interface and routes by the `Host` header, so a LAN client could reach a "localhost" server by sending `Host: localhost`. The new `RestrictToLoopbackClients` property (default true for `localhost`, `127.x.x.x`, and `::1`) rejects non-loopback remote addresses with 403 on `McpHttpServer`, `McpWebsocketsServer`, and `A2AHttpServer`.
- **Handshake sessions are created only by a successful `initialize`** (`McpHttpServer`, `/mcp` and `/rpc`). Before, a session was registered and returned for a rejected `initialize`, for any request without a session, and for any client-chosen `MCP-Session-Id`, which was adopted as-is. Now: a rejected `initialize` creates nothing; `POST /mcp` without a session gets 400 (`-32600`) except `initialize` and `ping` (a sessionless `ping` is answered without creating a session); `POST /rpc` without a session runs on a temporary connection with no session header; an unknown, expired, or terminated session ID gets 404 (`-32001`, via the new `McpProtocolException.SessionNotFound()`) on every endpoint.
- **Sessions are bound to the authenticated principal** that created them. Another principal presenting the ID gets 404.
- **`McpWebsocketsServer` supports authentication.** The new `AuthenticationHandler` (same delegate as `McpHttpServer`) runs on the upgrade request; the caller is stored in the new `ClientConnection.Caller` and is the ambient `RpcCallContext.Current` for every request on the socket. Before, the caller was always null on WebSocket and the transport could not be authenticated at all.
- **The TCP framing parser is strict.** `MessageFraming` accepts only `Content-Length` (once, digits only) and `Content-Type` header lines, enforces the 1024-byte header limit on complete headers, and fails fast on anything else. Before, it skipped unknown lines, so an HTTP request from a browser `fetch()` with a matching `Content-Length` ran methods on `JsonRpcServer` and `McpTcpServer`.
- **`POST /mcp` requires `Content-Type: application/json`** (415 otherwise), removing the preflight-free `text/plain` path even for allowed origins.

### Added
- `OriginPolicy` and `LoopbackAddresses` in `Voltaic.Core`.
- `OriginPolicy` and `RestrictToLoopbackClients` on `McpHttpServer`, `McpWebsocketsServer`, and `A2AHttpServer`.
- `McpHttpServer.RequireInitializedSessions` (default true). Set it to false to serve clients that open a session with a request other than `initialize`, such as `McpHttpClient` from Voltaic 2.0.0 and earlier: any successful sessionless request is then issued a new session. Unknown IDs are still rejected in both modes.
- `McpWebsocketsServer.AuthenticationHandler`, `ClientConnection.Caller`, and `McpWebsocketsClient.SetRequestHeader(name, value)`.
- `AuthenticationResult.Headers`, written on rejections by `McpHttpServer`, `McpWebsocketsServer`, and `A2AHttpServer`, and `AuthenticationResult.BearerChallenge(resourceMetadataUrl, error, errorDescription, errorMessage)`, which builds a 401 with an RFC 6750 `WWW-Authenticate: Bearer` challenge.
- `ClientConnection.MarkActivity()`. HTTP sessions are marked active on every request and on SSE keep-alives, so `SessionTimeoutSeconds` now expires only idle sessions (before, only notification traffic counted).
- `McpProtocolException.SessionNotFound()` and `McpProtocolException.SessionRequired()`.
- Touchstone suites `McpHttp.Sessions`, `Security.Policies`, `Security.HttpServers`, `Security.WebSocket`, and `Security.Framing` (65 new cases; 493 in total). Every negative security case was confirmed to fail with its protection disabled.

### Changed
- `McpHttpClient.ConnectAsync` and `ConnectStreamableAsync` perform the MCP handshake (`initialize`, then `notifications/initialized`) instead of a `ping`, report `ClientName`/`ClientVersion` in `clientInfo`, and adopt the negotiated protocol version in `ProtocolVersion`. Connecting fails when `initialize` returns an error.
- On an authenticated `McpHttpServer`, the `AuthenticationHandler` now also runs for `ping`: an authenticated ping carries its caller (and can use that caller's session); a ping that fails authentication is still answered, without a caller.
- The `?session=` query parameter is accepted only on GET streams (`/events`, `GET /mcp`), keeping session IDs out of URLs and logs for POST and DELETE.
- `/events` distinguishes a missing session (400) from an unknown one (404).
- Negotiated session versions are released when a session is removed.

### Tests
- The test fixtures start servers without `Task.Run`, so readiness probes no longer hit a closed port. A refused loopback connection costs about 500ms on Windows; the suite now runs in about 37 seconds instead of about 130.

## v2.0.0
A breaking release. MCP servers built on Voltaic now publish only the tools the host application registers, answer `ping` the way the MCP specification requires, and invoke tools only through `tools/call`. See [MIGRATE_V1_TO_V2.md](MIGRATE_V1_TO_V2.md) for step-by-step upgrade instructions. The design notes are in `archive/DEFAULT_TOOL_FIX.md`.

### Breaking changes
- **Protocol methods and demo tools are separated.** Every MCP server (`McpHttpServer`, `McpTcpServer`, `McpServer`, `McpWebsocketsServer`) now always registers the MCP protocol methods. The constructor parameter `includeDefaultMethods` (default `true`), which controlled protocol methods and demo tools together, is replaced by `includeDiagnosticTools` (default `false`), which controls only the `echo` and `getTime` diagnostic tools. In v1.x, turning off the demo tools also turned off `initialize`, `tools/list`, and every other protocol method.
- **Demo tools are no longer published by default.** `tools/list` returns only the application's tools. The `ping` tool is gone (`ping` is a protocol method), and `getSessions` (HTTP) and `getClients` (TCP, WebSocket) are removed. `getSessions` returned every active `Mcp-Session-Id`, which was enough for one caller to end or read another caller's session, including across tenants on an authenticated server.
- **`ping` returns `{}`.** The protocol `ping` is now served by its own handler, returns `McpEmptyResult` (`{}`, plus `resultType: "complete"` under `2026-07-28`), and can no longer be replaced by a tool. In v1.x it returned the demo tool's `"pong"`, which is not a valid MCP result. A v1.x `McpHttpClient` cannot connect to a v2.0.0 server, because its connection probe requires `"pong"`.
- **The `ping` authentication bypass covers only the protocol handler.** In v1.x an application tool named `ping` was also reachable without authentication on `McpHttpServer`. Tools are now always authenticated.
- **Tools are invoked only through `tools/call`.** `RegisterTool` no longer also registers the tool as a bare JSON-RPC method, so a client can no longer skip `tools/call` and its input and output schema validation. A bare call to a tool name returns `-32601`.
- **Tool input schemas enforce `additionalProperties` and `patternProperties`.** `additionalProperties: false` rejects undeclared arguments with `-32602` and names the property; an `additionalProperties` schema validates undeclared arguments; `patternProperties` names are allowed and validated. v1.x ignored these keywords, so a misspelled argument was dropped silently. Schemas without them behave as before.
- **`RegisterBuiltInMethods()` is replaced** by `RegisterProtocolMethods()` and `RegisterDiagnosticTools()` on `McpHttpServer`, `McpTcpServer`, and `McpWebsocketsServer`, and by `RegisterDiagnosticMethods()` on `JsonRpcServer`.
- **`JsonRpcServer` diagnostic methods are opt-in.** The `includeDefaultMethods` parameter (default `true`) is replaced by `includeDiagnosticMethods` (default `false`), which registers `ping` (still `"pong"` on plain JSON-RPC), `echo`, `getTime`, and `add`. `getClients` is removed.

### Added
- `UnregisterTool(string name)` on `McpHttpServer`, `McpTcpServer`, `McpServer`, and `McpWebsocketsServer`. Returns `true` when a tool was removed. Like `RegisterTool`, it does not notify clients; call `NotifyToolsChanged()`/`NotifyToolsChangedAsync()` afterwards.
- `PingAsync(...)` on `McpHttpClient`, `McpClient`, and `McpWebsocketsClient`. It accepts any successful result, so it works against v2.0.0 servers (`{}`) and v1.x servers (`"pong"`). `McpHttpClient.ConnectAsync` and `ConnectStreamableAsync` use it.
- The `Mcp.DiagnosticTools` and `Mcp.SchemaValidation` Touchstone suites (19 cases), covering both directions: protocol methods without diagnostic tools on every transport, opt-in diagnostics, `ping` result shape under handshake and stateless revisions, the authentication bypass, a tool named `ping`, bare tool calls, `UnregisterTool`, the client against a `"pong"` server, and `additionalProperties`/`patternProperties`. The Claude Code 2.1.x stateless replay now also asserts that `tools/list` contains exactly the application's tools (428 cases in total).

### Fixed
- Fixed the `McpHttpServer` JSON-RPC endpoint (`/rpc` by default) ignoring the stateless `2026-07-28` revision. `server/discover` advertises that revision on every endpoint, so a client such as Claude Code that chose it and sent requests to `/rpc` received results without `resultType` and rejected `tools/list`. The JSON-RPC endpoint now resolves the protocol version the same way as the MCP endpoint and serves stateless-era requests without a session. Requests without a protocol-version header or stateless routing headers keep the existing JSON-RPC behavior.
- Added `McpVersion.StatelessResults` cases that replay the Claude Code 2.1.x sequence against the JSON-RPC endpoint with and without authentication, reject a stateless request missing `Mcp-Method` there, and confirm plain JSON-RPC requests are unchanged.

### Samples and test applications
- `Sample.McpServer` publishes only its own tools and no longer overrides `ping`.
- `Test.McpServer`, `Test.McpHttpServer`, and `Test.McpWebsocketsServer` enable the diagnostic tools explicitly; `Test.JsonRpcServer` enables the diagnostic methods. Their help text reflects the v2.0.0 surface.

## v1.1.0
Fixes MCP clients on the stateless `2026-07-28` revision, such as Claude Code 2.1.x, seeing zero tools from Voltaic MCP servers. Confirmed end to end against Claude Code 2.1.281 over Streamable HTTP, with and without an `AuthenticationHandler`.

v1.0.0 was published to NuGet by mistake and is superseded by this release. Upgrade from 0.7.x or 1.0.0 directly to v1.1.0. The alpha label no longer applies to Voltaic.

- Fixed stateless (`2026-07-28`) results missing the required `resultType`. Clients such as Claude Code open with `server/discover`, choose `2026-07-28`, and reject any result without it, so `tools/list` failed and no tools appeared. Every built-in result served under that revision now carries `resultType: "complete"`. Handler-supplied `input_required` and `task` values are never overwritten.
- Fixed stateless cacheable results missing the required `ttlMs` and `cacheScope`. `tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`, `resources/read`, and `server/discover` now carry them. Configured `ListCacheTtlMs`/`ListCacheScope` values are used when set; otherwise the conservative defaults `ttlMs: 0` and `cacheScope: "private"` apply.
- Fixed `McpHttpServer` sending authenticated requests (`AuthenticationHandler` set) down a separate path that skipped version resolution, stateless routing, the batching gate, and session-version tracking. Authenticated and unauthenticated requests now share one pipeline. This also fixes a leak: on authenticated servers every stateless request used to create a server session that was never used again.
- Fixed `initialize` agreeing to the stateless `2026-07-28` revision, which defines no `initialize` and no sessions. `initialize` now negotiates at most the newest handshake-era revision (`2025-11-25`) on every transport (HTTP, stdio, TCP, WebSocket), including when a `2026-07-28` protocol header accompanies it or the server's `ProtocolVersion` default is set to `2026-07-28`.
- `server/discover` no longer advertises `listChanged` or `resources.subscribe`. Under `2026-07-28` these are delivered through `subscriptions/listen`, which Voltaic does not implement yet, so advertising them made clients poll a method that returned "Method not found". Handshake-era `initialize` still advertises and delivers them.
- Added `McpProtocol.NegotiateHandshakeVersion(requested, maximum)`, `McpProtocol.NewestHandshakeProtocolVersion`, and `McpProtocol.IsHandshakeVersion(version)`. `McpProtocol.NegotiateVersion` is unchanged and documented as era-agnostic.
- Added `MaximumHandshakeProtocolVersion` to `McpHttpServer`, `McpServer`, `McpTcpServer`, and `McpWebsocketsServer`. It defaults to `2025-11-25`, and the setter throws `ArgumentException` for a stateless-era or unknown revision; null restores the default.
- Added `McpResult.ResultType` (omitted from the wire when null) and the `McpResult.ResultTypeComplete`/`ResultTypeInputRequired`/`ResultTypeTask` constants. `McpDiscoverResult`, `McpInputRequiredResult`, `McpCreateTaskResult`, and `McpTaskAck` now inherit the property and set their value in the constructor. Their `ResultType` changed from `string` to `string?`.
- Added `McpEmptyResult`, which replaces the anonymous `{}` results of `ping`, `resources/subscribe`, `resources/unsubscribe`, `logging/setLevel`, and the notification handlers. It still serializes to `{}` under handshake-era revisions.
- Behavior change: a custom `RegisterMethod` handler that returns an `McpResult` subclass is stamped under `2026-07-28`. Plain objects are serialized unmodified, as documented on `RegisterMethod`.
- Added the `McpHttp.Server.Negotiation`, `McpVersion.StatelessResults`, and `McpHttp.Server.AuthParity` Touchstone suites, plus transport-parity cases for stdio, TCP, and WebSocket (405 cases in total, all passing on the console, xUnit, and NUnit runners under `net8.0` and `net10.0`). The regression cases replay the exact Claude Code 2.1.x request sequence with and without authentication.

## v0.7.1
- Extended authenticated-caller propagation to the A2A servers, mirroring the MCP work in v0.7.0
- `A2ARequestContext` now exposes `Principal` and `Claims`, copied from the server's `AuthenticationHandler` result before the `IA2AAgentHandler` runs, so agents can authorize per-caller (scope reads, gate writes) using the identity that authenticated the request
- Populated over both A2A transports: `A2AHttpServer` (JSON-RPC and HTTP+JSON) and `A2AGrpcServer` (which delegates to the HTTP endpoint). Because the caller cannot be threaded through the shared public methods without changing their signatures, an internal request-scoped bridge carries it from each auth site to context construction, where it is copied onto the context so it survives the background handler task and streaming loop
- Additive and backward compatible: `Principal`/`Claims` are null when no `AuthenticationHandler` is configured and for public Agent Card requests; existing `IA2AAgentHandler` implementations are unaffected
- Added positive and negative Touchstone cases: the caller reaches the agent context over A2A HTTP and gRPC, and the context carries no identity when unauthenticated

## v0.7.0
- Added `Voltaic.Core.RpcCallContext`, an ambient (`AsyncLocal`), request-scoped context that carries the authenticated caller's `Principal` and read-only `Claims` into MCP/JSON-RPC method and tool handlers
- `McpHttpServer` now populates `RpcCallContext.Current` immediately after a successful `AuthenticationHandler` result and restores the prior value when the request ends (via an `IDisposable` scope), so the identity flows untouched down the awaited dispatch chain into `tool.Handler` without any handler-signature change
- `RpcCallContext.Current` is `null` when no `AuthenticationHandler` is configured, on transports that do not authenticate, and for requests that bypass authentication (for example `ping`); concurrent requests on independent async flows never observe one another's context
- Added optional explicit-context registration overloads on `JsonRpcServer`, `McpServer`, `McpHttpServer`, `McpTcpServer`, and `McpWebsocketsServer`: `RegisterMethod`/`RegisterTool` variants whose handler receives `Func<RpcParameters?, RpcCallContext?, CancellationToken, Task<object>>` and forward `RpcCallContext.Current`
- Fully additive and backward compatible: all existing `RegisterMethod`/`RegisterTool` overloads compile and behave unchanged, and handlers that ignore the caller are unaffected
- Added the `McpHttp.Server.CallContext` Touchstone suite with positive and negative cases (context reflects the auth result inside a `tools/call` handler, is null without an `AuthenticationHandler`, is null on the pre-auth `ping` bypass, isolates concurrent callers, and flows through the explicit-context overload)

## v0.6.0
- Began multi-version MCP support spanning all five published protocol revisions: `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, and the stateless `2026-07-28`
- Added a version registry and era model (`McpProtocol` registry, `McpProtocolVersionInfo`, `McpProtocolEra`) that records each revision's era (handshake or stateless) and transport traits (sessions, batching, required protocol-version header, header routing)
- Added `McpProtocol.NewestProtocolVersion` (`2026-07-28`) alongside `LatestProtocolVersion`, which remains `2025-11-25` as the default handshake version so existing servers and clients are unaffected
- Added a deterministic version/era resolver (`McpVersionResolver`, `McpResolvedVersion`) that selects a revision from the `MCP-Protocol-Version` header, the request-body `_meta`, or structural cues, and rejects header/body disagreements
- Added stateless-era protocol error factories with the specification's codes: `HeaderMismatch` (`-32020`), `MissingRequiredClientCapability` (`-32021`), and `UnsupportedProtocolVersion` (`-32022`, carrying the supported-version list)
- Added additive protocol models: `server/discover` result (`McpDiscoverResult`), Multi Round-Trip Requests (`McpInputRequest`, `McpInputRequiredResult`), cacheable list/read results (`ttlMs`/`cacheScope`), the `2026-07-28` tasks extension (`McpTask`, `McpCreateTaskResult`, `McpUpdateTaskParams`, `McpTaskAck`, `McpTaskStatus`), and the `2025-11-25` experimental in-core tasks (`McpInCoreTask`, `McpCreateInCoreTaskResult`, `McpTaskAugmentation`, `McpListTasksResult`)
- Added `extensions` to client and server capability models for extension negotiation
- Implemented the stateless `2026-07-28` Streamable HTTP transport alongside the handshake transport, selected by the resolver: single POST endpoint, `Mcp-Method`/`Mcp-Name`/`Mcp-Param-*` routing-header validation (Base64 sentinel decoding), header/body version-match enforcement, `server/discover`, Multi Round-Trip Requests emission on `tools/call`, cacheable list/read results (`ttlMs`/`cacheScope`), notification `202`, and unknown-method `404`/`-32601`
- Added the stateless client surface to `McpHttpClient` (`ConnectStatelessAsync`, `DiscoverAsync`, `SendStatelessAsync`, `CallStatelessAsync<T>`, `CallToolStatelessAsync` with the MRTR retry loop, and `ClientName`/`ClientVersion`/`IsStateless`)
- Enforced version-gated JSON-RPC batching: batches are accepted for `2024-11-05` and `2025-03-26` and rejected (`-32600`) for `2025-06-18` and later, keyed on the session's negotiated version
- **Breaking:** removed all `System.Text.Json` document-object-model types (`JsonElement`/`JsonDocument`/`JsonNode`/`JsonObject`/`JsonArray`) from the entire codebase. MCP/JSON-RPC handlers now receive `Voltaic.Core.RpcParameters` (raw JSON + `Deserialize<T>()` + `Utf8JsonReader`-based scalar accessors) instead of `JsonElement?`; the `JsonRpcServer` value tuple became `RpcMethodInvocation`; schema validation, A2A models/wire, and all response parsing were rewritten DOM-free. `var` and tuples remain zero across the codebase
- Added discrete positive and negative end-to-end Touchstone suites for every revision (`Mcp.Version.2024-11-05` … `Mcp.Version.2026-07-28`) plus `McpVersion.Resolver`/`Models`/`Discovery`/`Stateless`/`StatelessClient`, covering negotiation, core operations, batching policy, routing/header validation, MRTR, and discovery, through the console, xUnit, and NUnit runners
- Green on `net8.0` and `net10.0` across all three runners

## v0.5.1
- Documentation fixes; no code changes from v0.5.0
- Corrected the README to reference the current version (v0.5.1) instead of v0.4.0

## v0.5.0
- Added `McpHttpClient.SetRequestHeader(name, value)` so HTTP and Streamable-HTTP MCP clients can attach authentication headers — a bearer `Authorization` header or a custom API-key header (for example `X-API-Key`) — applied to every request the client sends, including the connection handshake (ping) and the SSE GET stream
- Passing a null or empty value removes a previously set header; header names are matched case-insensitively
- Additive and backward compatible: clients that set no header behave exactly as before
- Added `McpHttp.Client.Auth` Touchstone suite proving configured headers reach the server (verified through the server's `AuthenticationHandler`) and that removed or omitted headers stay off the request

## v0.4.0
- Breaking pre-1.0 namespace change: moved shared JSON-RPC/core APIs to `Voltaic.Core` and MCP APIs to `Voltaic.Mcp`
- Added `Voltaic.A2A` for A2A v1.0 Agent Card models, task/message/artifact models, security declarations, push notification config models, and protocol errors
- Added dependency-light `A2AClient` for JSON-RPC over HTTP with `A2A-Version: 1.0` and SSE streaming support
- Added dependency-light `A2AHttpJsonClient` for the A2A HTTP+JSON binding without depending on ASP.NET Core or `System.Net.ServerSentEvents`
- Added dependency-light `A2AGrpcClient` and Watson-backed `A2AGrpcServer` for A2A gRPC over HTTP/2 without ASP.NET Core
- Added `A2ACardResolver` for `/.well-known/agent-card.json` discovery
- Added `A2AHttpServer` on `HttpListener` with public and extended Agent Cards, JSON-RPC, HTTP+JSON REST routes, SSE streaming, task projection, in-memory task storage, live task subscriptions, CORS, optional auth, push notification config storage, and return-immediately handling
- Added `IA2AAgentHandler`, `A2AAgentEventQueue`, `A2ATaskUpdater`, `IA2ATaskStore`, and `InMemoryA2ATaskStore` for direct-style agent implementation
- Added official `a2a-dotnet` compatibility coverage based on inspected source commit `8fe65cfaa65a72b2d63bc9bef2e2d32fddc12a18`, including JSON-RPC method/envelope/header checks, HTTP+JSON route/body checks, official-style server request acceptance, and SSE parsing
- Added A2A Touchstone suites covering serialization, Agent Card discovery, JSON-RPC, HTTP+JSON, gRPC, streaming, task lifecycle, push notification config CRUD, extended Agent Cards, return-immediately behavior, and compatibility oracle checks
- Added `Sample.A2AServer`, `Test.A2AServer`, and `Test.A2AClient`
- Reorganized library source under `src/Voltaic/Core`, `src/Voltaic/Mcp`, and `src/Voltaic/A2A`, including A2A protobuf definitions under `src/Voltaic/A2A/Protos`
- Updated README, package metadata, API coverage documentation, and source-layout tests for the new namespace layout and A2A support

## v0.3.0
- Updated package version and MCP default protocol version to `2025-11-25`, while retaining `2025-03-26` negotiation support
- Added shared MCP endpoint infrastructure for tools, resources, prompts, capability reporting, pagination, and protocol validation errors
- Expanded `ToolDefinition` metadata and added `McpToolCallResult` support for structured content and full tool-call result returns
- Added resource models and server registration APIs for static resources, resource templates, `resources/list`, `resources/templates/list`, and `resources/read`
- Added prompt models and server registration APIs for `prompts/list`, `prompts/get`, and required prompt argument validation
- Added completion provider APIs and `completion/complete` handling for prompt and resource completions
- Added MCP utility models and handlers for `logging/setLevel`, `notifications/cancelled`, `notifications/progress`, and `notifications/message`
- Added lightweight JSON Schema validation for common tool input and structured-output schema cases
- Tightened Streamable HTTP behavior for required `Accept` headers, `MCP-Protocol-Version`, notification `202 Accepted` responses, and terminated-session `404` responses
- Added MCP resource/prompt/tool notification helpers for HTTP, TCP, and WebSocket transports where server-to-client notifications are available
- Added Touchstone-based shared test descriptors plus console, xUnit, and NUnit runners under `src/`
- Expanded `Test.Shared` into a 253-case matrix covering public API validation, JSON-RPC TCP integration, Streamable HTTP behavior, MCP registry operations, model serialization, framing edge cases, client connection lifecycle, auth/CORS/session behavior, stdio subprocess integration, and TCP/WebSocket MCP parity
- Added protocol-level HTTP tests covering initialize, unsupported versions, tools, resources, templates, prompts, SSE notification delivery, and JSON result export
- Updated `Sample.McpServer` and README with structured-output, resource, template, prompt, Streamable HTTP, authentication, and Touchstone testing examples

## v0.2.0
- Fixed Streamable HTTP `/mcp` and legacy `/events` SSE connections so they emit an immediate `: connected` prelude and flush as soon as the stream is established
- Fixed idle SSE heartbeat behavior by restoring keep-alive comments (`: keep-alive`) when no notifications are queued
- Added raw `HttpClient` regression tests that verify immediate SSE liveness and keep-alive delivery on the wire
- Breaking change: `ClientConnection.DequeueAsync(CancellationToken)` now throws `OperationCanceledException` when cancelled instead of returning `null`
- Clarified Streamable HTTP client usage and endpoint documentation in the README

## v0.1.11
- Fixed IDisposable implementation across all 9 disposable classes to follow the full Dispose pattern
- All classes now implement `protected virtual void Dispose(bool disposing)` with `GC.SuppressFinalize(this)`
- Added `_IsDisposed` guard flags to prevent double-disposal in all classes
- Fixed double-disposal bugs in JsonRpcClient, McpClient, and McpWebsocketsClient where `Disconnect()`/`Shutdown()` previously disposed resources that `Dispose()` also disposed
- Fixed listener disposal in JsonRpcServer, McpHttpServer, and McpWebsocketsServer; now calls `Dispose()` instead of `Close()`/`Stop()`
- Fixed `TcpClient?.Close()` to `TcpClient?.Dispose()` in ClientConnection
- Removed resource disposal from `Disconnect()`/`Shutdown()` methods to prevent double-disposal; these methods now only manage connection state

## v0.1.10
- Added `AuthenticationHandler` property to `McpHttpServer` for optional async request authentication
- Added `AuthenticationResult` class with `IsAuthenticated`, `Principal`, `Claims`, `StatusCode`, and `ErrorMessage` properties
- Health check (`/`) and `ping` JSON-RPC method bypass authentication to allow connectivity validation without credentials
- CORS preflight (`OPTIONS`) requests bypass authentication
- When `AuthenticationHandler` is not set, behavior is unchanged from previous versions

## v0.1.x
- Initial release

## Previous Versions

Notes from previous versions will be pasted here.
