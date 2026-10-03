namespace Voltaic.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Metrics;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// The meter, activity source, and every instrument Voltaic records on, with best-effort recording helpers. A
    /// listener that throws never reaches the caller. Labels passed here must be bounded (see
    /// <see cref="VoltaicTelemetryNames"/>). Thread-safe.
    /// </summary>
    internal static class VoltaicInstruments
    {
        internal static readonly Meter Meter = new Meter(VoltaicTelemetryNames.MeterName, VoltaicTelemetry.Version);

        internal static readonly ActivitySource Source = new ActivitySource(VoltaicTelemetryNames.ActivitySourceName, VoltaicTelemetry.Version);

        internal static readonly Histogram<double> RpcServerDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.RpcServerDuration, "s", "Duration of inbound JSON-RPC, MCP, and A2A requests and notifications.");
        internal static readonly UpDownCounter<long> RpcServerActive = Meter.CreateUpDownCounter<long>(VoltaicTelemetryNames.RpcServerActiveRequests, "{request}", "Inbound requests currently being handled.");
        internal static readonly Counter<long> RpcServerRejected = Meter.CreateCounter<long>(VoltaicTelemetryNames.RpcServerRejectedMessages, "{message}", "Inbound messages rejected before dispatch.");
        internal static readonly Histogram<double> RpcClientDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.RpcClientDuration, "s", "Duration of outbound calls made by Voltaic clients.");
        internal static readonly UpDownCounter<long> RpcClientActive = Meter.CreateUpDownCounter<long>(VoltaicTelemetryNames.RpcClientActiveRequests, "{request}", "Outbound calls waiting for a response.");
        internal static readonly Histogram<double> ClientConnectDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.ClientConnectDuration, "s", "Duration of client connects and MCP handshakes.");
        internal static readonly Counter<long> ClientConnectionLosses = Meter.CreateCounter<long>(VoltaicTelemetryNames.ClientConnectionLosses, "{connection}", "Client connections lost.");
        internal static readonly Counter<long> ClientSessionRecoveries = Meter.CreateCounter<long>(VoltaicTelemetryNames.ClientSessionRecoveries, "{recovery}", "MCP HTTP client session recoveries.");
        internal static readonly UpDownCounter<long> ServerSessionsActive = Meter.CreateUpDownCounter<long>(VoltaicTelemetryNames.ServerSessionsActive, "{session}", "Open server connections and sessions.");
        internal static readonly Counter<long> ServerSessionsOpened = Meter.CreateCounter<long>(VoltaicTelemetryNames.ServerSessionsOpened, "{session}", "Server connections and sessions opened.");
        internal static readonly Counter<long> ServerSessionsClosed = Meter.CreateCounter<long>(VoltaicTelemetryNames.ServerSessionsClosed, "{session}", "Server connections and sessions closed.");
        internal static readonly Counter<long> ServerSessionsRejected = Meter.CreateCounter<long>(VoltaicTelemetryNames.ServerSessionsRejected, "{session}", "Session requests refused.");
        internal static readonly Histogram<double> ServerSessionDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.ServerSessionDuration, "s", "Lifetime of server connections and sessions.");
        internal static readonly UpDownCounter<long> ConnectionQueueDepth = Meter.CreateUpDownCounter<long>(VoltaicTelemetryNames.ConnectionQueueDepth, "{message}", "Notifications queued on server connections.");
        internal static readonly Counter<long> ConnectionQueueDropped = Meter.CreateCounter<long>(VoltaicTelemetryNames.ConnectionQueueDropped, "{message}", "Queued notifications discarded because a queue was full.");
        internal static readonly Histogram<double> HttpServerDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.HttpServerRequestDuration, "s", "Duration of requests on HttpListener-based servers.");
        internal static readonly UpDownCounter<long> HttpServerActive = Meter.CreateUpDownCounter<long>(VoltaicTelemetryNames.HttpServerActiveRequests, "{request}", "HTTP requests being handled.");
        internal static readonly Counter<long> HttpAccessDenials = Meter.CreateCounter<long>(VoltaicTelemetryNames.HttpAccessDenials, "{request}", "Requests refused by the HTTP access gates.");
        internal static readonly Histogram<double> McpToolDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.McpToolDuration, "s", "Duration of MCP tool calls.");
        internal static readonly Histogram<double> McpToolStageDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.McpToolStageDuration, "s", "Duration of MCP tool call stages.");
        internal static readonly Counter<long> McpSchemaValidations = Meter.CreateCounter<long>(VoltaicTelemetryNames.McpSchemaValidations, "{validation}", "Tool input and output schema validations.");
        internal static readonly Counter<long> McpRateLimitDecisions = Meter.CreateCounter<long>(VoltaicTelemetryNames.McpRateLimitDecisions, "{decision}", "MCP rate-limit decisions.");
        internal static readonly Counter<long> McpNotificationsSent = Meter.CreateCounter<long>(VoltaicTelemetryNames.McpNotificationsSent, "{notification}", "Server-to-client MCP notifications.");
        internal static readonly Counter<long> McpPings = Meter.CreateCounter<long>(VoltaicTelemetryNames.McpPings, "{ping}", "MCP keep-alive pings.");
        internal static readonly Counter<long> McpPingConnectionFailures = Meter.CreateCounter<long>(VoltaicTelemetryNames.McpPingConnectionFailures, "{connection}", "Connections closed after unanswered pings.");
        internal static readonly UpDownCounter<long> McpSseStreamsActive = Meter.CreateUpDownCounter<long>(VoltaicTelemetryNames.McpSseStreamsActive, "{stream}", "Open MCP server-sent event streams.");
        internal static readonly Counter<long> McpSseReplayedEvents = Meter.CreateCounter<long>(VoltaicTelemetryNames.McpSseReplayedEvents, "{event}", "SSE events replayed on resumption.");
        internal static readonly UpDownCounter<long> McpSubscriptionsActive = Meter.CreateUpDownCounter<long>(VoltaicTelemetryNames.McpSubscriptionsActive, "{subscription}", "Open MCP subscriptions/listen streams.");
        internal static readonly Counter<long> McpClientProcessStarts = Meter.CreateCounter<long>(VoltaicTelemetryNames.McpClientProcessStarts, "{process}", "MCP stdio server processes launched.");
        internal static readonly Histogram<double> A2AAgentDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.A2AAgentDuration, "s", "Duration of A2A agent handler executions.");
        internal static readonly Counter<long> A2ATaskTransitions = Meter.CreateCounter<long>(VoltaicTelemetryNames.A2ATaskTransitions, "{transition}", "A2A task status updates applied.");
        internal static readonly Counter<long> A2APushDeliveries = Meter.CreateCounter<long>(VoltaicTelemetryNames.A2APushDeliveries, "{notification}", "A2A push notification deliveries.");
        internal static readonly Histogram<double> A2APushStageDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.A2APushStageDuration, "s", "Duration of A2A push notification stages.");
        internal static readonly Histogram<double> A2APushAttemptDuration = Meter.CreateHistogram<double>(VoltaicTelemetryNames.A2APushAttemptDuration, "s", "Duration of A2A push notification HTTP attempts.");
        internal static readonly UpDownCounter<long> A2APushPending = Meter.CreateUpDownCounter<long>(VoltaicTelemetryNames.A2APushPending, "{notification}", "A2A push notifications queued or in delivery.");
        internal static readonly Counter<long> A2APushUrlRejections = Meter.CreateCounter<long>(VoltaicTelemetryNames.A2APushUrlRejections, "{url}", "A2A webhook URLs and addresses refused.");

        private static readonly ConcurrentDictionary<object, Func<int>> _SessionCapacity = new ConcurrentDictionary<object, Func<int>>();
        private static long _PushLastSuccessUnixMs;

        static VoltaicInstruments()
        {
            Meter.CreateObservableGauge(VoltaicTelemetryNames.BuildInfo, ObserveBuildInfo, null, "Voltaic build information; always 1.");
            Meter.CreateObservableGauge(VoltaicTelemetryNames.ServerSessionsLimit, ObserveSessionCapacity, "{session}", "Configured MCP HTTP session capacity.");
            Meter.CreateObservableGauge(VoltaicTelemetryNames.A2APushLastSuccess, ObservePushLastSuccess, "s", "Unix time of the last successful A2A push delivery.");
        }

        // Creates the meter and every instrument (this type's static constructor), so a collector sees them, and the
        // build-info gauge, from the moment the first Voltaic server, client, or endpoint is constructed.
        internal static void EnsureCreated()
        {
        }

        // True when the instrument has a listener and telemetry is on; callers skip building labels otherwise.
        internal static bool IsOn(Instrument instrument)
        {
            return instrument.Enabled && VoltaicTelemetry.Enabled;
        }

        internal static void Add(Counter<long> counter, in TagList tags, long value = 1)
        {
            if (!IsOn(counter)) return;
            try
            {
                counter.Add(value, tags);
            }
            catch (Exception)
            {
                // Telemetry is best-effort: a failing listener never affects the caller.
            }
        }

        internal static void Add(UpDownCounter<long> counter, long delta, in TagList tags)
        {
            if (!IsOn(counter)) return;
            try
            {
                counter.Add(delta, tags);
            }
            catch (Exception)
            {
                // Telemetry is best-effort: a failing listener never affects the caller.
            }
        }

        internal static void Record(Histogram<double> histogram, double value, in TagList tags)
        {
            if (!IsOn(histogram)) return;
            try
            {
                histogram.Record(value, tags);
            }
            catch (Exception)
            {
                // Telemetry is best-effort: a failing listener never affects the caller.
            }
        }

        internal static double SecondsSince(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
        }

        #region Domain-Helpers

        internal static void RejectedMessage(string protocol, string transport, string reason)
        {
            if (!IsOn(RpcServerRejected)) return;
            Add(RpcServerRejected, new TagList { { VoltaicTelemetryNames.AttrProtocol, protocol }, { VoltaicTelemetryNames.AttrTransport, transport }, { VoltaicTelemetryNames.AttrReason, reason } });
        }

        // Returns the start timestamp the caller passes to SessionClosed, or 0 when nothing listened (the close is then
        // not recorded either, so the active count never goes negative for a listener that attached in between).
        internal static long SessionOpened(string protocol, string transport)
        {
            if (!IsOn(ServerSessionsOpened) && !IsOn(ServerSessionsActive) && !IsOn(ServerSessionsClosed) && !IsOn(ServerSessionDuration)) return 0;
            TagList tags = new TagList { { VoltaicTelemetryNames.AttrProtocol, protocol }, { VoltaicTelemetryNames.AttrTransport, transport } };
            Add(ServerSessionsOpened, tags);
            Add(ServerSessionsActive, 1, tags);
            return Stopwatch.GetTimestamp();
        }

        internal static void SessionClosed(string protocol, string transport, string reason, long startTimestamp)
        {
            if (startTimestamp == 0) return;
            TagList active = new TagList { { VoltaicTelemetryNames.AttrProtocol, protocol }, { VoltaicTelemetryNames.AttrTransport, transport } };
            Add(ServerSessionsActive, -1, active);
            TagList closed = new TagList { { VoltaicTelemetryNames.AttrProtocol, protocol }, { VoltaicTelemetryNames.AttrTransport, transport }, { VoltaicTelemetryNames.AttrReason, reason } };
            Add(ServerSessionsClosed, closed);
            Record(ServerSessionDuration, SecondsSince(startTimestamp), closed);
        }

        internal static void SessionRejected(string protocol, string transport, string reason)
        {
            if (!IsOn(ServerSessionsRejected)) return;
            Add(ServerSessionsRejected, new TagList { { VoltaicTelemetryNames.AttrProtocol, protocol }, { VoltaicTelemetryNames.AttrTransport, transport }, { VoltaicTelemetryNames.AttrReason, reason } });
        }

        internal static void AccessDenied(string protocol, string transport, string reason)
        {
            if (!IsOn(HttpAccessDenials)) return;
            Add(HttpAccessDenials, new TagList { { VoltaicTelemetryNames.AttrProtocol, protocol }, { VoltaicTelemetryNames.AttrTransport, transport }, { VoltaicTelemetryNames.AttrReason, reason } });
        }

        internal static void ConnectionLost(string protocol, string transport)
        {
            if (!IsOn(ClientConnectionLosses)) return;
            Add(ClientConnectionLosses, new TagList { { VoltaicTelemetryNames.AttrProtocol, protocol }, { VoltaicTelemetryNames.AttrTransport, transport } });
        }

        internal static void SessionRecovery(bool recovered)
        {
            if (!IsOn(ClientSessionRecoveries)) return;
            Add(ClientSessionRecoveries, new TagList { { VoltaicTelemetryNames.AttrOutcome, recovered ? VoltaicTelemetryNames.OutcomeSuccess : VoltaicTelemetryNames.OutcomeError } });
        }

        internal static void QueueChanged(ClientConnectionTypeEnum type, long delta)
        {
            if (delta == 0 || !IsOn(ConnectionQueueDepth)) return;
            Add(ConnectionQueueDepth, delta, new TagList { { VoltaicTelemetryNames.AttrTransport, TransportOf(type) } });
        }

        internal static void QueueDropped(ClientConnectionTypeEnum type)
        {
            if (!IsOn(ConnectionQueueDropped)) return;
            Add(ConnectionQueueDropped, new TagList { { VoltaicTelemetryNames.AttrTransport, TransportOf(type) } });
        }

        internal static void RateLimitDecision(string kind, bool allowed)
        {
            if (!IsOn(McpRateLimitDecisions)) return;
            Add(McpRateLimitDecisions, new TagList { { VoltaicTelemetryNames.AttrKind, kind }, { VoltaicTelemetryNames.AttrOutcome, allowed ? "allowed" : "rejected" } });
        }

        internal static void SchemaValidation(string kind, bool valid)
        {
            if (!IsOn(McpSchemaValidations)) return;
            Add(McpSchemaValidations, new TagList { { VoltaicTelemetryNames.AttrKind, kind }, { VoltaicTelemetryNames.AttrOutcome, valid ? "valid" : "invalid" } });
        }

        internal static void NotificationSent(string? method, bool sent)
        {
            if (!IsOn(McpNotificationsSent)) return;
            Add(McpNotificationsSent, new TagList { { VoltaicTelemetryNames.AttrRpcMethod, McpNotificationLabel(method) }, { VoltaicTelemetryNames.AttrOutcome, sent ? "sent" : "failed" } });
        }

        internal static void Ping(string role, string outcome)
        {
            if (!IsOn(McpPings)) return;
            Add(McpPings, new TagList { { VoltaicTelemetryNames.AttrRole, role }, { VoltaicTelemetryNames.AttrOutcome, outcome } });
        }

        internal static void PingConnectionFailure(string role)
        {
            if (!IsOn(McpPingConnectionFailures)) return;
            Add(McpPingConnectionFailures, new TagList { { VoltaicTelemetryNames.AttrRole, role } });
        }

        internal static void SseStream(string kind, long delta)
        {
            if (!IsOn(McpSseStreamsActive)) return;
            Add(McpSseStreamsActive, delta, new TagList { { VoltaicTelemetryNames.AttrKind, kind } });
        }

        internal static void SseReplayed(long count)
        {
            if (count <= 0 || !IsOn(McpSseReplayedEvents)) return;
            Add(McpSseReplayedEvents, new TagList(), count);
        }

        internal static void Subscription(long delta)
        {
            if (!IsOn(McpSubscriptionsActive)) return;
            Add(McpSubscriptionsActive, delta, new TagList());
        }

        internal static void ProcessStart(bool started)
        {
            if (!IsOn(McpClientProcessStarts)) return;
            Add(McpClientProcessStarts, new TagList { { VoltaicTelemetryNames.AttrOutcome, started ? VoltaicTelemetryNames.OutcomeSuccess : VoltaicTelemetryNames.OutcomeError } });
        }

        internal static void TaskTransition(string state)
        {
            if (!IsOn(A2ATaskTransitions)) return;
            Add(A2ATaskTransitions, new TagList { { VoltaicTelemetryNames.AttrA2ATaskState, state } });
        }

        internal static void PushPending(long delta)
        {
            if (!IsOn(A2APushPending)) return;
            Add(A2APushPending, delta, new TagList());
        }

        internal static void PushDelivery(string outcome)
        {
            if (outcome == VoltaicTelemetryNames.OutcomeSuccess) Interlocked.Exchange(ref _PushLastSuccessUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!IsOn(A2APushDeliveries)) return;
            Add(A2APushDeliveries, new TagList { { VoltaicTelemetryNames.AttrOutcome, outcome } });
        }

        internal static void PushStage(string stage, double seconds)
        {
            if (!IsOn(A2APushStageDuration)) return;
            Record(A2APushStageDuration, seconds, new TagList { { VoltaicTelemetryNames.AttrStage, stage } });
        }

        internal static void PushAttempt(string outcome, string? errorType, double seconds)
        {
            if (!IsOn(A2APushAttemptDuration)) return;
            TagList tags = new TagList { { VoltaicTelemetryNames.AttrOutcome, outcome } };
            if (errorType != null) tags.Add(VoltaicTelemetryNames.AttrErrorType, errorType);
            Record(A2APushAttemptDuration, seconds, tags);
        }

        internal static void PushUrlRejected(string reason)
        {
            if (!IsOn(A2APushUrlRejections)) return;
            Add(A2APushUrlRejections, new TagList { { VoltaicTelemetryNames.AttrReason, reason } });
        }

        // Registers (or, with null, removes) a running server's session capacity for voltaic.server.sessions.limit.
        internal static void TrackSessionCapacity(object owner, Func<int>? capacity)
        {
            if (capacity == null) _SessionCapacity.TryRemove(owner, out Func<int>? _);
            else _SessionCapacity[owner] = capacity;
        }

        #endregion Domain-Helpers

        #region Labels

        // MCP methods and notifications defined by the specification revisions Voltaic serves; anything else is _OTHER
        // unless the server registered it.
        private static readonly HashSet<string> _KnownMcpMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "initialize", "ping", "server/discover", "tools/list", "tools/call", "resources/list", "resources/read",
            "resources/templates/list", "resources/subscribe", "resources/unsubscribe", "prompts/list", "prompts/get",
            "completion/complete", "logging/setLevel", "subscriptions/listen", "sampling/createMessage", "roots/list",
            "elicitation/create", "notifications/initialized", "notifications/cancelled", "notifications/progress",
            "notifications/message", "notifications/roots/list_changed", "notifications/tools/list_changed",
            "notifications/prompts/list_changed", "notifications/resources/list_changed", "notifications/resources/updated",
            "notifications/subscriptions/acknowledged", "notifications/elicitation/complete"
        };

        // A server-side method label: the name when the specification defines it or the server registered it.
        internal static string ServerMethodLabel(string? method, Func<string, bool>? isRegistered)
        {
            if (String.IsNullOrEmpty(method)) return VoltaicTelemetryNames.OtherValue;
            if (_KnownMcpMethods.Contains(method!)) return method!;
            if (isRegistered != null && method!.Length <= 128 && isRegistered(method)) return method;
            return VoltaicTelemetryNames.OtherValue;
        }

        // A client-side method label: chosen by the calling code, so used as is unless it is unreasonably long.
        internal static string ClientMethodLabel(string? method)
        {
            if (String.IsNullOrEmpty(method) || method!.Length > 128) return VoltaicTelemetryNames.OtherValue;
            return method;
        }

        internal static string McpNotificationLabel(string? method)
        {
            return method != null && _KnownMcpMethods.Contains(method) ? method : VoltaicTelemetryNames.OtherValue;
        }

        private static readonly ConcurrentDictionary<int, string> _CodeLabels = new ConcurrentDictionary<int, string>();

        internal static string CodeLabel(int code)
        {
            if (_CodeLabels.Count > 256) return code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return _CodeLabels.GetOrAdd(code, value => value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // error.type for an exception: the JSON-RPC error code it carries, else its type name.
        internal static string ErrorTypeOf(Exception exception)
        {
            if (exception is IJsonRpcErrorProvider provider)
            {
                try
                {
                    return CodeLabel(provider.ToJsonRpcError().Code);
                }
                catch (Exception)
                {
                    // Fall back to the type name.
                }
            }

            return exception.GetType().Name;
        }

        internal static string TransportOf(ClientConnectionTypeEnum type)
        {
            switch (type)
            {
                case ClientConnectionTypeEnum.Tcp: return VoltaicTelemetryNames.TransportTcp;
                case ClientConnectionTypeEnum.Websockets: return VoltaicTelemetryNames.TransportWebSocket;
                case ClientConnectionTypeEnum.Http: return VoltaicTelemetryNames.TransportHttp;
                default: return VoltaicTelemetryNames.OtherValue;
            }
        }

        #endregion Labels

        #region Observers

        private static IEnumerable<Measurement<int>> ObserveBuildInfo()
        {
            if (!VoltaicTelemetry.Enabled) yield break;
            yield return new Measurement<int>(1, new KeyValuePair<string, object?>(VoltaicTelemetryNames.AttrVersion, VoltaicTelemetry.Version), new KeyValuePair<string, object?>(VoltaicTelemetryNames.AttrRuntime, RuntimeInformation.FrameworkDescription));
        }

        private static IEnumerable<Measurement<long>> ObserveSessionCapacity()
        {
            if (!VoltaicTelemetry.Enabled) yield break;
            long total = 0;
            int servers = 0;
            foreach (Func<int> capacity in _SessionCapacity.Values)
            {
                try
                {
                    total += capacity();
                    servers++;
                }
                catch (Exception)
                {
                    // A server stopping concurrently; skip it.
                }
            }

            if (servers == 0) yield break;
            yield return new Measurement<long>(total, new KeyValuePair<string, object?>(VoltaicTelemetryNames.AttrProtocol, VoltaicTelemetryNames.ProtocolMcp), new KeyValuePair<string, object?>(VoltaicTelemetryNames.AttrTransport, VoltaicTelemetryNames.TransportHttp));
        }

        private static IEnumerable<Measurement<double>> ObservePushLastSuccess()
        {
            if (!VoltaicTelemetry.Enabled) yield break;
            long last = Interlocked.Read(ref _PushLastSuccessUnixMs);
            if (last == 0) yield break;
            yield return new Measurement<double>(last / 1000.0);
        }

        #endregion Observers
    }
}
