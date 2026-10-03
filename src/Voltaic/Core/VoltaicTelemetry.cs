namespace Voltaic.Core
{
    using System;
    using System.Reflection;
    using System.Threading;

    /// <summary>
    /// Process-wide switches for Voltaic's built-in telemetry. Voltaic emits metrics on the
    /// <see cref="System.Diagnostics.Metrics.Meter"/> named <see cref="VoltaicTelemetryNames.MeterName"/> and spans on the
    /// <see cref="System.Diagnostics.ActivitySource"/> named <see cref="VoltaicTelemetryNames.ActivitySourceName"/> (both
    /// <c>Voltaic</c>), using only the .NET base class library: it references no OpenTelemetry SDK or exporter and opens no
    /// connection. A host collects the data by subscribing to those two names (for example with the OpenTelemetry SDK's
    /// <c>AddMeter("Voltaic")</c> and <c>AddSource("Voltaic")</c>, or Radiant's <c>settings.Sources.AddMeter</c> and
    /// <c>AddActivitySource</c>). With nothing subscribed, instrumentation costs a listener check per operation and
    /// allocates nothing. Telemetry is best-effort: a failure while recording never affects request handling.
    /// Thread-safe.
    /// </summary>
    public static class VoltaicTelemetry
    {
        private static int _Enabled = 1;
        private static int _PropagateTraceContext = 1;
        private static readonly string _Version = ReadVersion();

        /// <summary>
        /// Gets or sets whether Voltaic records metrics and starts spans. Default is true. When false, no measurement
        /// is recorded and no span is started, even when a collector is subscribed; it does not affect protocol
        /// behavior. Changes apply to operations that start afterwards. Thread-safe.
        /// </summary>
        public static bool Enabled
        {
            get => Volatile.Read(ref _Enabled) == 1;
            set => Interlocked.Exchange(ref _Enabled, value ? 1 : 0);
        }

        /// <summary>
        /// Gets or sets whether Voltaic propagates W3C trace context across process boundaries. Default is true. When
        /// true and a span is being recorded, Voltaic's HTTP-based clients send the <c>traceparent</c> and
        /// <c>tracestate</c> headers, MCP stream clients (stdio, TCP, WebSocket) add them to the request's
        /// <c>params._meta</c> (as the OpenTelemetry MCP conventions describe), and A2A push notifications carry the
        /// headers to the webhook; servers continue an inbound trace from the same carriers. Plain JSON-RPC clients never
        /// alter request parameters. Thread-safe.
        /// </summary>
        public static bool PropagateTraceContext
        {
            get => Volatile.Read(ref _PropagateTraceContext) == 1;
            set => Interlocked.Exchange(ref _PropagateTraceContext, value ? 1 : 0);
        }

        /// <summary>
        /// Gets the Voltaic version reported as the meter and activity source version and on
        /// <see cref="VoltaicTelemetryNames.BuildInfo"/>, for example <c>2.2.0</c>. Never null.
        /// </summary>
        public static string Version => _Version;

        private static string ReadVersion()
        {
            try
            {
                Assembly assembly = typeof(VoltaicTelemetry).Assembly;
                string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!String.IsNullOrEmpty(informational))
                {
                    int plus = informational!.IndexOf('+');
                    return plus > 0 ? informational.Substring(0, plus) : informational;
                }

                return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            }
            catch (Exception)
            {
                return "0.0.0";
            }
        }
    }
}
