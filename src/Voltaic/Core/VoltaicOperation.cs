namespace Voltaic.Core
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.Metrics;
    using System.Threading;

    /// <summary>
    /// One instrumented unit of work: a span on the Voltaic activity source, a duration histogram measurement with an
    /// outcome, and an optional in-flight counter. <see cref="Start"/> returns null when nothing listens, so callers use
    /// the null-conditional operator and pay only the listener check. The outcome is <c>success</c> unless
    /// <see cref="Fail(Exception)"/> or <see cref="SetError"/> changes it. Recording is best-effort and never throws.
    /// Not thread-safe: one operation belongs to one logical flow.
    /// </summary>
    internal sealed class VoltaicOperation : IDisposable
    {
        private readonly Histogram<double> _Duration;
        private readonly UpDownCounter<long>? _Active;
        private readonly TagList _ActiveTags;
        private TagList _Tags;
        private readonly long _Start;
        private readonly bool _CountedActive;
        private string _Outcome = VoltaicTelemetryNames.OutcomeSuccess;
        private string? _ErrorType;
        private bool _Disposed;

        private VoltaicOperation(Histogram<double> duration, UpDownCounter<long>? active, in TagList tags, Activity? activity)
        {
            _Duration = duration;
            _Active = active;
            _Tags = tags;
            _ActiveTags = tags;
            Activity = activity;
            _Start = Stopwatch.GetTimestamp();
            if (_Active != null && VoltaicInstruments.IsOn(_Active))
            {
                VoltaicInstruments.Add(_Active, 1, _ActiveTags);
                _CountedActive = true;
            }
        }

        /// <summary>
        /// Gets the span, or null when no listener samples it.
        /// </summary>
        internal Activity? Activity { get; }

        /// <summary>
        /// Gets the outcome recorded so far.
        /// </summary>
        internal string Outcome => _Outcome;

        /// <summary>
        /// Starts an operation, or returns null when telemetry is off or neither the histogram nor the activity source
        /// has a listener. The metric labels are <paramref name="tags"/> (bounded values only); they are also set on the
        /// span. A <paramref name="parent"/> of default continues <see cref="System.Diagnostics.Activity.Current"/>.
        /// </summary>
        internal static VoltaicOperation? Start(
            Histogram<double> duration,
            UpDownCounter<long>? active,
            string spanName,
            ActivityKind kind,
            in TagList tags,
            ActivityContext parent = default)
        {
            if (!VoltaicTelemetry.Enabled) return null;
            bool metrics = duration.Enabled || (active != null && active.Enabled);
            if (!metrics && !VoltaicInstruments.Source.HasListeners()) return null;

            Activity? activity = null;
            try
            {
                activity = VoltaicInstruments.Source.StartActivity(spanName, kind, parent);
                if (activity != null && activity.IsAllDataRequested)
                {
                    foreach (System.Collections.Generic.KeyValuePair<string, object?> tag in tags)
                    {
                        activity.SetTag(tag.Key, tag.Value);
                    }
                }
            }
            catch (Exception)
            {
                activity = null;
            }

            if (!metrics && activity == null) return null;
            return new VoltaicOperation(duration, active, tags, activity);
        }

        /// <summary>
        /// Adds a bounded label to the duration measurement and the span (not to the in-flight counter).
        /// </summary>
        internal void AddMetricTag(string key, string? value)
        {
            if (value == null) return;
            _Tags.Add(key, value);
            SetSpanTag(key, value);
        }

        /// <summary>
        /// Sets a span attribute only; use for high-cardinality detail such as IDs.
        /// </summary>
        internal void SetSpanTag(string key, object? value)
        {
            if (Activity == null || value == null) return;
            try
            {
                if (Activity.IsAllDataRequested) Activity.SetTag(key, value);
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        /// <summary>
        /// Renames the span, for example once the tool name of a <c>tools/call</c> is known.
        /// </summary>
        internal void SetSpanName(string name)
        {
            if (Activity == null) return;
            try
            {
                Activity.DisplayName = name;
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        /// <summary>
        /// Sets the outcome without marking an error (for example <c>tool_error</c> for a tool result with isError, or
        /// <c>rejected</c>).
        /// </summary>
        internal void SetOutcome(string outcome)
        {
            _Outcome = outcome;
        }

        /// <summary>
        /// Marks the operation failed with an error class (a JSON-RPC error code, an HTTP status code, or an exception
        /// type name) and an outcome (default <c>error</c>); the span status becomes Error.
        /// </summary>
        internal void SetError(string errorType, string outcome = VoltaicTelemetryNames.OutcomeError)
        {
            _Outcome = outcome;
            _ErrorType = errorType;
            if (Activity == null) return;
            try
            {
                Activity.SetStatus(ActivityStatusCode.Error, errorType);
                if (Activity.IsAllDataRequested) Activity.SetTag(VoltaicTelemetryNames.AttrErrorType, errorType);
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        /// <summary>
        /// Marks the operation failed by an exception: <c>cancelled</c> for a cancellation, otherwise <c>error</c>, with
        /// <c>error.type</c> from <see cref="VoltaicInstruments.ErrorTypeOf"/>. The span gets an <c>exception</c> event
        /// carrying the exception type and stack trace; the message is not recorded, since it may contain caller data.
        /// </summary>
        internal void Fail(Exception exception)
        {
            if (exception == null) return;
            if (_ErrorType != null)
            {
                // The first recorded error class wins (for example a JSON-RPC code set before the call threw).
                RecordException(Activity, exception);
                return;
            }

            bool cancelled = exception is OperationCanceledException;
            SetError(cancelled ? exception.GetType().Name : VoltaicInstruments.ErrorTypeOf(exception), cancelled ? VoltaicTelemetryNames.OutcomeCancelled : VoltaicTelemetryNames.OutcomeError);
            RecordException(Activity, exception);
        }

        /// <summary>
        /// Marks the operation timed out.
        /// </summary>
        internal void Timeout()
        {
            SetError("timeout", VoltaicTelemetryNames.OutcomeTimeout);
        }

        /// <summary>
        /// Records the duration measurement, releases the in-flight count, and stops the span. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (_Disposed) return;
            _Disposed = true;

            if (_CountedActive && _Active != null) VoltaicInstruments.Add(_Active, -1, _ActiveTags);

            if (VoltaicInstruments.IsOn(_Duration))
            {
                TagList tags = _Tags;
                tags.Add(VoltaicTelemetryNames.AttrOutcome, _Outcome);
                if (_ErrorType != null) tags.Add(VoltaicTelemetryNames.AttrErrorType, _ErrorType);
                VoltaicInstruments.Record(_Duration, VoltaicInstruments.SecondsSince(_Start), tags);
            }

            if (Activity != null)
            {
                try
                {
                    if (Activity.IsAllDataRequested) Activity.SetTag(VoltaicTelemetryNames.AttrOutcome, _Outcome);
                    if (_ErrorType == null && Activity.Status == ActivityStatusCode.Unset) Activity.SetStatus(ActivityStatusCode.Ok);
                    Activity.Dispose();
                }
                catch (Exception)
                {
                    // Best-effort.
                }
            }
        }

        /// <summary>
        /// Adds an OpenTelemetry <c>exception</c> event (type and stack trace, no message) to a span.
        /// </summary>
        internal static void RecordException(Activity? activity, Exception exception)
        {
            if (activity == null || exception == null || !activity.IsAllDataRequested) return;
            try
            {
                ActivityTagsCollection tags = new ActivityTagsCollection
                {
                    { "exception.type", exception.GetType().FullName },
                    { "exception.stacktrace", exception.StackTrace }
                };
                activity.AddEvent(new ActivityEvent("exception", DateTimeOffset.UtcNow, tags));
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        /// <summary>
        /// Marks a failed client call: a cancellation the caller did not request is a timeout; anything else as
        /// <see cref="Fail(Exception)"/>.
        /// </summary>
        internal void FailCall(Exception exception, CancellationToken callerToken)
        {
            if (exception is OperationCanceledException && !callerToken.IsCancellationRequested && _ErrorType == null)
            {
                Timeout();
                return;
            }

            Fail(exception);
        }

        /// <summary>
        /// Starts an outbound call operation (span kind Client named after the method, the client duration histogram,
        /// and the client in-flight counter), or null when nothing listens.
        /// </summary>
        internal static VoltaicOperation? StartClientCall(string protocol, string transport, string? method)
        {
            if (!VoltaicTelemetry.Enabled) return null;
            string label = VoltaicInstruments.ClientMethodLabel(method);
            TagList tags = new TagList
            {
                { VoltaicTelemetryNames.AttrProtocol, protocol },
                { VoltaicTelemetryNames.AttrTransport, transport },
                { VoltaicTelemetryNames.AttrRpcMethod, label }
            };

            VoltaicOperation? operation = Start(VoltaicInstruments.RpcClientDuration, VoltaicInstruments.RpcClientActive, label, ActivityKind.Client, tags);
            operation?.SetSpanTag(VoltaicTelemetryNames.AttrRpcSystem, "jsonrpc");
            return operation;
        }

        /// <summary>
        /// Starts a client connect operation (span <c>{protocol}.{transport} connect</c> and the connect duration
        /// histogram), or null when nothing listens.
        /// </summary>
        internal static VoltaicOperation? StartConnect(string protocol, string transport)
        {
            if (!VoltaicTelemetry.Enabled) return null;
            TagList tags = new TagList
            {
                { VoltaicTelemetryNames.AttrProtocol, protocol },
                { VoltaicTelemetryNames.AttrTransport, transport }
            };

            return Start(VoltaicInstruments.ClientConnectDuration, null, protocol + "." + transport + VoltaicTelemetryNames.SpanConnectSuffix, ActivityKind.Client, tags);
        }

        /// <summary>
        /// Starts a plain internal span (no metrics) for a workflow stage, or returns null when nothing samples it.
        /// </summary>
        internal static Activity? StartSpan(string name, ActivityKind kind = ActivityKind.Internal, ActivityContext parent = default)
        {
            if (!VoltaicTelemetry.Enabled || !VoltaicInstruments.Source.HasListeners()) return null;
            try
            {
                return VoltaicInstruments.Source.StartActivity(name, kind, parent);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
