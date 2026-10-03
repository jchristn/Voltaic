# Voltaic Telemetry

Voltaic measures itself. Since v2.2.0 every server, client, and endpoint emits metrics and traces through the .NET base class library: a `Meter` and an `ActivitySource`, both named **`Voltaic`**. Voltaic takes no dependency on OpenTelemetry, Radiant, or any exporter, and it never opens a connection to a telemetry backend. It emits; your host collects. With nothing subscribed, each operation costs one listener check and allocates nothing.

The goal is operational: an on-call engineer looking only at dashboards and traces can tell where the time went and what failed, without reading Voltaic's source or attaching a debugger.

- [Subscribing](#subscribing)
- [Configuration](#configuration)
- [What is instrumented](#what-is-instrumented)
- [Metrics catalog](#metrics-catalog)
- [Labels](#labels)
- [Spans catalog](#spans-catalog)
- [Trace context propagation](#trace-context-propagation)
- [Watson (A2A gRPC)](#watson-a2a-grpc)
- [Recommended alerts](#recommended-alerts)
- [Suggested dashboards](#suggested-dashboards)
- [Cardinality, privacy, and cost](#cardinality-privacy-and-cost)
- [Runtime metrics and logs](#runtime-metrics-and-logs)

## Subscribing

Two strings are the whole contract: subscribe a collector to the meter `Voltaic` and the activity source `Voltaic`. Both are also available as `VoltaicTelemetryNames.MeterName` and `VoltaicTelemetryNames.ActivitySourceName`. If your process hosts `A2AGrpcServer`, also subscribe to `Watson` (see [Watson](#watson-a2a-grpc)).

With the OpenTelemetry SDK:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Voltaic.Core;

using MeterProvider meters = Sdk.CreateMeterProviderBuilder()
    .AddMeter(VoltaicTelemetryNames.MeterName)          // "Voltaic"
    .AddMeter("Watson")                                 // only if you host A2AGrpcServer
    .AddOtlpExporter()                                  // or AddPrometheusHttpListener()
    .Build();

using TracerProvider traces = Sdk.CreateTracerProviderBuilder()
    .AddSource(VoltaicTelemetryNames.ActivitySourceName) // "Voltaic"
    .AddSource("Watson")
    .AddOtlpExporter()
    .Build();
```

With [Radiant](https://github.com/jchristn/Radiant), one settings object at the composition root:

```csharp
RadiantSettings settings = new RadiantSettings("my-agent-service");
settings.Sources.AddMeter("Voltaic");
settings.Sources.AddActivitySource("Voltaic");
settings.Sources.AddMeter("Watson");             // only if you host A2AGrpcServer
settings.Sources.AddActivitySource("Watson");
settings.Otlp.Endpoint = "http://127.0.0.1:4317";
settings.Prometheus.Enable = true;               // optional in-process /metrics

using (RadiantHost host = RadiantHost.Start(settings))
{
    // start Voltaic servers and clients
}
```

Histogram bucket boundaries are a collector concern. Voltaic records raw durations in seconds; choose buckets with an OpenTelemetry view (or a Radiant latency preset). Derive p50, p95, and p99 in Grafana from the buckets. Voltaic computes no quantiles in-process.

The meter and its instruments are created when the first Voltaic server, client, or endpoint is constructed, so a collector sees `voltaic.build.info` from startup.

## Configuration

Telemetry is on by default. Everything is controlled in code; there are no environment variables or files.

| Setting | Default | Effect |
|---|---|---|
| `VoltaicTelemetry.Enabled` | `true` | Process-wide switch. When `false`, Voltaic records no measurement and starts no span, even when a collector is subscribed. Protocol behavior is unchanged. |
| `VoltaicTelemetry.PropagateTraceContext` | `true` | When `true` and a span is being recorded, clients send W3C `traceparent`/`tracestate` (HTTP headers, or MCP `params._meta` on stdio, TCP, and WebSocket) and servers continue an inbound trace. See [propagation](#trace-context-propagation). |
| `VoltaicTelemetry.Version` | assembly version | Read-only. The meter and source version, and the `voltaic.version` label on `voltaic.build.info`. |
| `A2AGrpcServer` Watson settings | on | `A2AGrpcServer` sets Watson's `Settings.Telemetry.Enable`, `EnableMetrics`, `EnableTraces`, and `PropagateContext` to `true` explicitly. |

Both switches are thread-safe and apply to operations that start after the change.

## What is instrumented

Voltaic is a library: it has no process, port, or background job of its own beyond what its servers and clients run. Every inbound unit of work, outbound call, pipeline stage, pool, limiter, and failure path is covered:

| Area | Code | Metrics | Spans |
|---|---|---|---|
| Inbound requests, every protocol and transport (JSON-RPC TCP, MCP stdio/TCP/WebSocket/HTTP, A2A JSON-RPC/HTTP+JSON/gRPC, and server requests answered by clients) | `JsonRpcServer`, `McpMessageProcessor` (shared by every MCP server), `A2AHttpServer`, `A2AGrpcServer`, `ClientRequestDispatcher` | `voltaic.rpc.server.duration`, `voltaic.rpc.server.active_requests`, `voltaic.rpc.server.rejected_messages` | `{method}` (Server), `a2a {method}` |
| Outbound calls by every client | `JsonRpcClient`, `McpTcpClient`, `McpClient`, `McpWebsocketsClient`, `McpHttpClient`, `A2AClient`, `A2AHttpJsonClient`, `A2AGrpcClient`, `A2ACardResolver` | `voltaic.rpc.client.duration`, `voltaic.rpc.client.active_requests` | `{method}` (Client) |
| Client connect and MCP handshake, stdio process launch, connection loss, HTTP session recovery | the clients | `voltaic.client.connect.duration`, `voltaic.mcp.client.process.starts`, `voltaic.client.connection_losses`, `voltaic.client.session_recoveries` | `{protocol}.{transport} connect`, `mcp.stdio launch_server`, `mcp.http recover_session` |
| HTTP layer of the HttpListener servers | `McpHttpServer`, `A2AHttpServer` | `voltaic.http.server.request.duration`, `voltaic.http.server.active_requests` | `{METHOD} {route}` (Server) |
| Access gates (loopback, origin, authentication, scope) | `HttpAccessGuard` call sites, WebSocket upgrade, gRPC | `voltaic.http.access.denials` | (on the HTTP span) |
| Connections and sessions | TCP and WebSocket connections, the stdio session, MCP HTTP sessions | `voltaic.server.sessions.active/opened/closed/rejected`, `voltaic.server.session.duration`, `voltaic.server.sessions.limit` | |
| MCP tool workflow: validate input, execute, validate output | `McpEndpoint.CallToolAsync` | `voltaic.mcp.tool.duration`, `voltaic.mcp.tool.stage.duration`, `voltaic.mcp.schema.validations` | `tools/call {tool}`, `stage:validate_input`, `stage:execute`, `stage:validate_output` |
| Limiters, queues, streams | `McpRateLimiter`, `ClientConnection` queue, SSE streams, `subscriptions/listen` | `voltaic.mcp.rate_limit.decisions`, `voltaic.connection.queue.depth/dropped`, `voltaic.mcp.sse.streams.active`, `voltaic.mcp.sse.replayed_events`, `voltaic.mcp.subscriptions.active` | |
| Server notifications and keep-alive | `McpServerNotifications`, `McpSubscription`, `McpPinger` | `voltaic.mcp.notifications.sent`, `voltaic.mcp.pings`, `voltaic.mcp.ping.connection_failures` | |
| A2A agent execution and tasks | `A2AHttpServer` (shared by `A2AGrpcServer`) | `voltaic.a2a.agent.duration`, `voltaic.a2a.task.transitions` | `a2a.agent execute` |
| A2A push notification pipeline: queued, deliver, attempts | `A2AHttpServer` push delivery | `voltaic.a2a.push.deliveries`, `voltaic.a2a.push.stage.duration`, `voltaic.a2a.push.attempt.duration`, `voltaic.a2a.push.pending`, `voltaic.a2a.push.last_success`, `voltaic.a2a.push.url_rejections` | `a2a.push deliver` (Consumer), `a2a.push POST` (Client) |
| Build information | | `voltaic.build.info` | |

## Metrics catalog

Instrument names are dotted. A Prometheus exporter rewrites them to snake case, adds `_seconds` for unit `s`, `_total` for counters, and `_bucket`/`_sum`/`_count` for histograms; label keys have their dots replaced with underscores (`voltaic.outcome` becomes `voltaic_outcome`). Every histogram has an outcome, so its `_count` series doubles as the request counter.

| Instrument | Type | Unit | Labels | Prometheus name | Description |
|---|---|---|---|---|---|
| `voltaic.rpc.server.duration` | Histogram | s | `voltaic.protocol`, `voltaic.transport`, `rpc.method`, `voltaic.outcome`, `error.type` | `voltaic_rpc_server_duration_seconds` | One inbound request or notification, from dispatch to response. |
| `voltaic.rpc.server.active_requests` | UpDownCounter | {request} | `voltaic.protocol`, `voltaic.transport` | `voltaic_rpc_server_active_requests` | Inbound requests in flight. |
| `voltaic.rpc.server.rejected_messages` | Counter | {message} | `voltaic.protocol`, `voltaic.transport`, `voltaic.reason` | `voltaic_rpc_server_rejected_messages_total` | Messages rejected before dispatch: `parse_error`, `invalid_request`, `too_large`, `invalid_framing`, `binary_message`, `method_not_found` (A2A JSON-RPC). |
| `voltaic.rpc.client.duration` | Histogram | s | `voltaic.protocol`, `voltaic.transport`, `rpc.method`, `voltaic.outcome`, `error.type` | `voltaic_rpc_client_duration_seconds` | One outbound call, including retries the client makes on its own (session recovery, version fallback). |
| `voltaic.rpc.client.active_requests` | UpDownCounter | {request} | `voltaic.protocol`, `voltaic.transport` | `voltaic_rpc_client_active_requests` | Outbound calls waiting for a response. |
| `voltaic.client.connect.duration` | Histogram | s | `voltaic.protocol`, `voltaic.transport`, `voltaic.outcome`, `error.type` | `voltaic_client_connect_duration_seconds` | Connect, including the MCP `initialize` handshake (and process launch for stdio). |
| `voltaic.client.connection_losses` | Counter | {connection} | `voltaic.protocol`, `voltaic.transport` | `voltaic_client_connection_losses_total` | Stream connections that dropped (not closed by the client). Waiting calls fail at once with `IOException`. |
| `voltaic.client.session_recoveries` | Counter | {recovery} | `voltaic.outcome` | `voltaic_client_session_recoveries_total` | `McpHttpClient` new sessions after a 404. |
| `voltaic.server.sessions.active` | UpDownCounter | {session} | `voltaic.protocol`, `voltaic.transport` | `voltaic_server_sessions_active` | Open TCP and WebSocket connections, the stdio session, and MCP HTTP sessions. |
| `voltaic.server.sessions.opened` | Counter | {session} | `voltaic.protocol`, `voltaic.transport` | `voltaic_server_sessions_opened_total` | Connections and sessions opened. |
| `voltaic.server.sessions.closed` | Counter | {session} | `voltaic.protocol`, `voltaic.transport`, `voltaic.reason` | `voltaic_server_sessions_closed_total` | Closed, by reason: `client_closed`, `server_stopped`, `error`, `protocol_error`, `ping_failure` (stdio), `deleted`, `expired`, `evicted`, `kicked`, `removed` (HTTP). |
| `voltaic.server.sessions.rejected` | Counter | {session} | `voltaic.protocol`, `voltaic.transport`, `voltaic.reason` | `voltaic_server_sessions_rejected_total` | MCP HTTP session refusals: `not_found` (404), `session_required` (400), `max_sessions` (503). |
| `voltaic.server.session.duration` | Histogram | s | `voltaic.protocol`, `voltaic.transport`, `voltaic.reason` | `voltaic_server_session_duration_seconds` | Lifetime of a connection or session, by close reason. |
| `voltaic.server.sessions.limit` | ObservableGauge | {session} | `voltaic.protocol`, `voltaic.transport` | `voltaic_server_sessions_limit` | `MaxSessions` summed over running `McpHttpServer` instances (capacity for `sessions.active`). |
| `voltaic.connection.queue.depth` | UpDownCounter | {message} | `voltaic.transport` | `voltaic_connection_queue_depth` | Notifications queued on server connections (HTTP sessions) and not yet delivered. |
| `voltaic.connection.queue.dropped` | Counter | {message} | `voltaic.transport` | `voltaic_connection_queue_dropped_total` | Notifications discarded because a queue reached `MaxQueueSize`. |
| `voltaic.http.server.request.duration` | Histogram | s | `voltaic.protocol`, `http.request.method`, `http.route`, `http.response.status_code`, `voltaic.outcome`, `error.type` | `voltaic_http_server_request_duration_seconds` | One request on `McpHttpServer` or `A2AHttpServer`. Long-lived SSE GETs count their whole stream. |
| `voltaic.http.server.active_requests` | UpDownCounter | {request} | `voltaic.protocol`, `http.request.method`, `http.route` | `voltaic_http_server_active_requests` | HTTP requests in flight (includes open SSE streams). |
| `voltaic.http.access.denials` | Counter | {request} | `voltaic.protocol`, `voltaic.transport`, `voltaic.reason` | `voltaic_http_access_denials_total` | Refused by the gates: `loopback_only`, `origin`, `authentication`, `insufficient_scope`. |
| `voltaic.mcp.tool.duration` | Histogram | s | `gen_ai.tool.name`, `voltaic.outcome`, `error.type` | `voltaic_mcp_tool_duration_seconds` | One `tools/call`. Outcomes: `success`, `tool_error` (result with `isError`), `invalid_arguments`, `rate_limited`, `handler_exception`, `input_required`, `invalid_output`, `protocol_error`, `cancelled`. |
| `voltaic.mcp.tool.stage.duration` | Histogram | s | `gen_ai.tool.name`, `voltaic.stage`, `voltaic.outcome`, `error.type` | `voltaic_mcp_tool_stage_duration_seconds` | Stages `validate_input`, `execute`, `validate_output`. |
| `voltaic.mcp.schema.validations` | Counter | {validation} | `voltaic.kind` (`input`, `output`), `voltaic.outcome` (`valid`, `invalid`) | `voltaic_mcp_schema_validations_total` | Tool input and output JSON Schema checks. |
| `voltaic.mcp.rate_limit.decisions` | Counter | {decision} | `voltaic.kind` (`tools`, `completions`, `logs`), `voltaic.outcome` (`allowed`, `rejected`) | `voltaic_mcp_rate_limit_decisions_total` | `McpRateLimits` decisions (only when a limit is set). |
| `voltaic.mcp.notifications.sent` | Counter | {notification} | `rpc.method`, `voltaic.outcome` (`sent`, `failed`) | `voltaic_mcp_notifications_sent_total` | Server-to-client notifications: list changes, resource updates, log messages, progress, subscription events. |
| `voltaic.mcp.pings` | Counter | {ping} | `voltaic.role` (`server`, `client`), `voltaic.outcome` (`answered`, `unanswered`, `failed`) | `voltaic_mcp_pings_total` | Keep-alive pings (`PingIntervalMs`). |
| `voltaic.mcp.ping.connection_failures` | Counter | {connection} | `voltaic.role` | `voltaic_mcp_ping_connection_failures_total` | Connections closed after `PingFailureThreshold` unanswered pings. |
| `voltaic.mcp.sse.streams.active` | UpDownCounter | {stream} | `voltaic.kind` (`get`, `events`) | `voltaic_mcp_sse_streams_active` | Open `GET /mcp` and legacy `/events` streams. |
| `voltaic.mcp.sse.replayed_events` | Counter | {event} | | `voltaic_mcp_sse_replayed_events_total` | Events replayed to clients resuming with `Last-Event-ID`. |
| `voltaic.mcp.subscriptions.active` | UpDownCounter | {subscription} | | `voltaic_mcp_subscriptions_active` | Open `subscriptions/listen` streams (2026-07-28). |
| `voltaic.mcp.client.process.starts` | Counter | {process} | `voltaic.outcome` | `voltaic_mcp_client_process_starts_total` | stdio server processes launched by `McpClient`. |
| `voltaic.a2a.agent.duration` | Histogram | s | `voltaic.kind` (`blocking`, `background`, `streaming`), `voltaic.outcome`, `error.type` | `voltaic_a2a_agent_duration_seconds` | One `IA2AAgentHandler.ExecuteAsync`. |
| `voltaic.a2a.task.transitions` | Counter | {transition} | `a2a.task.state` | `voltaic_a2a_task_transitions_total` | Task status updates applied (`Submitted`, `Working`, `Completed`, `Failed`, `Canceled`, `Rejected`, `InputRequired`, `AuthRequired`). |
| `voltaic.a2a.push.deliveries` | Counter | {notification} | `voltaic.outcome` (`success`, `error`, `cancelled`) | `voltaic_a2a_push_deliveries_total` | Final outcome of a push notification after all attempts. |
| `voltaic.a2a.push.stage.duration` | Histogram | s | `voltaic.stage` (`queued`, `deliver`) | `voltaic_a2a_push_stage_duration_seconds` | Time waiting behind earlier deliveries to the same webhook, then delivering (all attempts and backoff). |
| `voltaic.a2a.push.attempt.duration` | Histogram | s | `voltaic.outcome` (`success`, `http_error`, `exception`), `error.type` | `voltaic_a2a_push_attempt_duration_seconds` | One HTTP POST to a webhook. `error.type` is the status code, `timeout`, or the exception type. |
| `voltaic.a2a.push.pending` | UpDownCounter | {notification} | | `voltaic_a2a_push_pending` | Push notifications queued or being delivered. |
| `voltaic.a2a.push.url_rejections` | Counter | {url} | `voltaic.reason` (`invalid_url`, `validator`, `address_policy`) | `voltaic_a2a_push_url_rejections_total` | Webhook URLs or resolved addresses refused (SSRF protection). |
| `voltaic.a2a.push.last_success` | ObservableGauge | s | | `voltaic_a2a_push_last_success_seconds` | Unix time of the last successful delivery in this process. |
| `voltaic.build.info` | ObservableGauge | (none) | `voltaic.version`, `voltaic.runtime` | `voltaic_build_info` | Always 1. |

## Labels

| Key | Values |
|---|---|
| `voltaic.protocol` | `jsonrpc`, `mcp`, `a2a` |
| `voltaic.transport` | `tcp`, `stdio`, `websocket`, `http` (MCP and JSON-RPC); `jsonrpc`, `http_json`, `grpc` (the A2A bindings) |
| `rpc.method` | The JSON-RPC, MCP, or A2A method. On servers, only methods the specification defines or the server registered; anything else is `_OTHER`. On clients, the method the calling code passed (`_OTHER` if over 128 characters). |
| `voltaic.outcome` | `success`, `error`, `cancelled`, `timeout`, plus the domain outcomes listed per metric |
| `error.type` | A JSON-RPC or A2A error code (`-32601`, `-32001`), an HTTP status code (`503`), `timeout`, or an exception type name (`InvalidOperationException`). Absent on success. |
| `gen_ai.tool.name` | A registered tool name; `_OTHER` for unknown tools |
| `http.route` | The configured endpoint path (`/mcp`, `/rpc`, `/events`, `/a2a`), `/`, the Agent Card or protected-resource metadata path, or an A2A REST template (`/tasks/{id}`, `/tasks/{id}:cancel`, `/tasks/{id}/pushNotificationConfigs/{configId}`); `_OTHER` for unmatched paths. Never a raw path. |
| `http.response.status_code` | The HTTP status code |
| `voltaic.reason`, `voltaic.kind`, `voltaic.stage`, `voltaic.role` | The fixed sets listed per metric |
| `a2a.task.state` | The A2A `TaskState` name |

## Spans catalog

All spans are on the `Voltaic` activity source. Each sets its status explicitly: `Ok` on success, `Error` with the `error.type` as description on failure. A failure caused by an exception adds an OpenTelemetry `exception` event with `exception.type` and `exception.stacktrace`. The exception message is never recorded, because it may contain caller data.

| Span name | Kind | Started by | Key attributes |
|---|---|---|---|
| `{method}`, e.g. `tools/list`, `initialize` | Server | Every inbound JSON-RPC and MCP request or notification (also server requests answered by a client) | `rpc.system`=`jsonrpc`, `rpc.method`, `mcp.method.name`, `mcp.protocol.version`, `rpc.jsonrpc.request_id`, `rpc.jsonrpc.error_code`, `voltaic.protocol`, `voltaic.transport`, `voltaic.outcome` |
| `tools/call {tool}`, `prompts/get {prompt}` | Server | The same span, renamed once the registered tool or prompt is known | `gen_ai.tool.name` or `gen_ai.prompt.name` |
| `stage:validate_input`, `stage:execute`, `stage:validate_output` | Internal | Each stage of a tool call | `gen_ai.tool.name`, `voltaic.stage` |
| `{method}` | Client | Every outbound call by a Voltaic client | `rpc.method`, `rpc.jsonrpc.request_id`, `rpc.jsonrpc.error_code`, `voltaic.protocol`, `voltaic.transport` |
| `{protocol}.{transport} connect`, e.g. `mcp.websocket connect` | Client | `ConnectAsync`, `ConnectStreamableAsync`, `ConnectStatelessAsync`, `LaunchServerAsync` (includes `initialize`) | `server.address` |
| `mcp.stdio launch_server` | Internal | Starting the stdio server process | |
| `mcp.http recover_session` | Internal | `McpHttpClient` replacing a lost session | |
| `{METHOD} {route}`, e.g. `POST /mcp`, `GET /tasks/{id}` | Server | Every request on `McpHttpServer` and `A2AHttpServer` | `http.request.method`, `http.route`, `http.response.status_code`, `url.path`, `url.scheme`, `client.address`, `user_agent.original` |
| `a2a {method}`, e.g. `a2a SendMessage` | Internal | Each A2A operation on any binding (under the HTTP span, or under Watson's span for gRPC) | `rpc.method`, `voltaic.transport` |
| `a2a.agent execute` | Internal | Each agent handler run (also background runs for `returnImmediately`) | `a2a.task.id`, `voltaic.kind` |
| `a2a.push deliver` | Consumer | Each push notification delivery, parented to the span that produced the task event (a background hand-off) | `a2a.task.id`, `voltaic.outcome` |
| `a2a.push POST` | Client | Each HTTP attempt to a webhook | `server.address`, `voltaic.attempt`, `http.response.status_code` |

A typical MCP tool call over Streamable HTTP is one trace:

```
tools/call                       Client  (McpHttpClient)
  POST /mcp                      Server  (McpHttpServer HTTP layer, continues traceparent)
    tools/call search_docs       Server  (MCP dispatch)
      stage:validate_input
      stage:execute              <- your handler's own spans nest here
      stage:validate_output
```

## Trace context propagation

With `VoltaicTelemetry.PropagateTraceContext` on (the default) and a span being recorded:

- **HTTP clients** (`McpHttpClient`, `A2AClient`, `A2AHttpJsonClient`, `A2AGrpcClient`, `A2ACardResolver`, A2A push delivery) add `traceparent` and `tracestate` headers unless the request already has them.
- **MCP stream clients** (`McpClient`, `McpTcpClient`, `McpWebsocketsClient`) add `traceparent` and `tracestate` to the request's `params._meta`, as the OpenTelemetry MCP semantic conventions describe. An existing `_meta.traceparent` is never replaced. Plain `JsonRpcClient` calls never alter parameters.
- **Servers** continue the trace: `McpHttpServer` and `A2AHttpServer` from the `traceparent` header; MCP stdio, TCP, and WebSocket servers (and clients answering server requests) from `params._meta.traceparent`. A request without a context starts a new trace; it never inherits whatever span was current when the server started.
- **Background hand-offs** keep the trace: tool handlers run under the request span, A2A background agent runs nest under the request that started them, and push deliveries are `Consumer` spans parented to the producing span.

The propagator is `DistributedContextPropagator.Current`, so a host that changes it (for example to B3) changes Voltaic too.

## Watson (A2A gRPC)

`A2AGrpcServer` is hosted on Watson 7.1, which measures its HTTP/2 layer itself on a meter and activity source named `Watson`: `http.server.request.duration`, connection and route metrics, and one `Server` span per request that adopts an inbound `traceparent`. Voltaic turns Watson's telemetry on explicitly (`Enable`, `EnableMetrics`, `EnableTraces`, `PropagateContext`) and does not duplicate it: the per-method `a2a {method}` span and `voltaic.rpc.server.duration{voltaic_transport="grpc"}` nest under Watson's span. Subscribe to `Watson` to see that layer. See Watson's own `TELEMETRY.md` for its catalog.

## Recommended alerts

PromQL, assuming the Prometheus names above. Tune thresholds to your traffic.

```promql
# Inbound error ratio above 5% for 10 minutes, per protocol and transport
sum by (voltaic_protocol, voltaic_transport) (rate(voltaic_rpc_server_duration_seconds_count{voltaic_outcome="error"}[5m]))
  / sum by (voltaic_protocol, voltaic_transport) (rate(voltaic_rpc_server_duration_seconds_count[5m])) > 0.05

# p95 latency of a method above 2 s
histogram_quantile(0.95, sum by (le, rpc_method) (rate(voltaic_rpc_server_duration_seconds_bucket[5m]))) > 2

# Tool handlers throwing
sum by (gen_ai_tool_name) (rate(voltaic_mcp_tool_duration_seconds_count{voltaic_outcome="handler_exception"}[5m])) > 0

# Slow tool execution stage (p95 above 10 s)
histogram_quantile(0.95, sum by (le, gen_ai_tool_name) (rate(voltaic_mcp_tool_stage_duration_seconds_bucket{voltaic_stage="execute"}[5m]))) > 10

# MCP HTTP sessions above 80% of MaxSessions, or refusals at the cap
sum(voltaic_server_sessions_active{voltaic_transport="http"}) / sum(voltaic_server_sessions_limit) > 0.8
increase(voltaic_server_sessions_rejected_total{voltaic_reason="max_sessions"}[5m]) > 0

# Clients being rate limited
sum by (voltaic_kind) (rate(voltaic_mcp_rate_limit_decisions_total{voltaic_outcome="rejected"}[5m])) > 0

# Burst of access denials (possible probing or misconfigured client)
sum by (voltaic_protocol, voltaic_reason) (rate(voltaic_http_access_denials_total[5m])) > 1

# Peers that stop answering keep-alive pings
increase(voltaic_mcp_ping_connection_failures_total[15m]) > 0

# Notifications dropped because a session queue was full
increase(voltaic_connection_queue_dropped_total[15m]) > 0

# Outbound calls failing, per protocol and method
sum by (voltaic_protocol, rpc_method) (rate(voltaic_rpc_client_duration_seconds_count{voltaic_outcome=~"error|timeout"}[5m])) > 0.1

# A2A agent failures
sum(rate(voltaic_a2a_agent_duration_seconds_count{voltaic_outcome="error"}[5m])) > 0

# A2A push notifications failing, backing up, or stale (only where pushes are expected)
increase(voltaic_a2a_push_deliveries_total{voltaic_outcome="error"}[15m]) > 0
voltaic_a2a_push_pending > 100
time() - voltaic_a2a_push_last_success_seconds > 3600

# Instance gone (no build info scraped)
absent(voltaic_build_info)
```

## Suggested dashboards

Voltaic is a library, so it ships no compose stack or dashboard JSON: the host that embeds it owns its Prometheus, Tempo, and Grafana. Split the host's Voltaic panels by domain, following the metric domains above:

| Dashboard | Panels |
|---|---|
| Overview | `voltaic_build_info` by version; request rate and error ratio from `voltaic_rpc_server_duration_seconds_count` by protocol and transport; active requests; open sessions against `voltaic_server_sessions_limit`. |
| HTTP | `voltaic_http_server_request_duration_seconds` rate by route and status class, p95 by route; access denials by reason; open SSE streams. Add Watson's `http_server_request_duration_seconds` for gRPC. |
| MCP | Request rate and p95 by `rpc_method`; tool rate and p95 by `gen_ai_tool_name` and outcome; per-stage p95 (`validate_input`, `execute`, `validate_output`); schema validation failures; rate-limit rejections; notifications by method and outcome; pings and ping failures; sessions opened and closed by reason; replayed SSE events; queue depth and drops. |
| A2A | Operation rate and p95 by method and binding; agent p95 and failures by mode; task transitions by state; push pipeline: deliveries by outcome, queued and deliver stage p95, attempt outcomes, pending, time since last success, URL rejections. |
| Clients and integrations | Outbound call rate, error ratio, and p95 by protocol, transport, and method; connect duration and failures; connection losses; session recoveries; stdio process starts. |

Link the error and latency panels to Tempo with a query on the service and span name (`tools/call {tool}`, `a2a {method}`) so an operator can move from a spike to a representative trace.

## Cardinality, privacy, and cost

- **Bounded labels only.** Metric labels never carry IDs, session IDs, task IDs, URLs, raw paths, principals, or free-form text. Server-side method and tool labels admit only names the specification defines or the server registered (`_OTHER` otherwise). HTTP routes are configured paths or templates.
- **Spans carry detail, not secrets.** Spans may carry request IDs, task IDs, the raw URL path, the client address, the user agent, and the webhook host. They never carry request or response payloads, tool arguments, exception messages, headers, tokens, credentials, or MCP session IDs (which can act as bearer credentials).
- **Best-effort.** A failing listener or exporter never affects request handling: every recording is guarded, and spans are optional (`null` when nothing samples).
- **Near-zero cost when unobserved.** Each operation checks `Instrument.Enabled` and `ActivitySource.HasListeners()` first and allocates nothing when both are false. `VoltaicTelemetry.Enabled = false` skips even that work.
- **Listeners attached later.** In-flight and session counters (`*.active_requests`, `voltaic.server.sessions.active`) count only work that started while a listener was attached, so they never go negative. The queue, stream, subscription, and push-pending counters are plain up/down counters: if a collector attaches while items are already queued or open, those series can read below their true value until the items drain.

## Runtime metrics and logs

- **Runtime metrics** (GC, thread pool, allocations, CPU) are the host's job. On .NET 9 and later subscribe to the built-in `System.Runtime` meter; on .NET 8 add `OpenTelemetry.Instrumentation.Runtime`, or let Radiant add its baseline process and runtime metrics.
- **Logs.** Voltaic keeps its existing `Log` events and callbacks and does not ship logs to Loki itself. Because Voltaic sets `Activity.Current` for every request, tool handler, and agent run, any `ILogger` the host uses inside them is stamped with the trace and span ID, so logs and traces correlate in Grafana once the host exports both.
