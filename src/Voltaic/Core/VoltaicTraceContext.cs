namespace Voltaic.Core
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>
    /// W3C trace context propagation for Voltaic's transports: HTTP headers for HTTP-based clients and servers, and
    /// <c>params._meta.traceparent</c>/<c>tracestate</c> for MCP stream transports (the OpenTelemetry MCP convention).
    /// Uses <see cref="DistributedContextPropagator.Current"/>, so a host's propagator choice applies. Every method is
    /// best-effort and never throws. Thread-safe.
    /// </summary>
    internal static class VoltaicTraceContext
    {
        internal const string TraceParent = "traceparent";
        internal const string TraceState = "tracestate";

        /// <summary>
        /// Reads a parent context from carrier fields (for example HTTP request headers); default when absent or invalid.
        /// </summary>
        internal static ActivityContext Extract(Func<string, string?> getField)
        {
            if (!VoltaicTelemetry.Enabled || !VoltaicTelemetry.PropagateTraceContext) return default;
            try
            {
                DistributedContextPropagator.Current.ExtractTraceIdAndState(
                    getField,
                    static (object? carrier, string name, out string? value, out IEnumerable<string>? values) =>
                    {
                        values = null;
                        value = carrier is Func<string, string?> get ? get(name) : null;
                    },
                    out string? traceParent,
                    out string? traceState);
                return Parse(traceParent, traceState);
            }
            catch (Exception)
            {
                return default;
            }
        }

        /// <summary>
        /// Reads a parent context from an MCP request's <c>params._meta</c> object; default when absent or invalid.
        /// </summary>
        internal static ActivityContext ExtractFromMeta(JsonElement? meta)
        {
            if (!VoltaicTelemetry.Enabled || !VoltaicTelemetry.PropagateTraceContext) return default;
            if (!meta.HasValue || meta.Value.ValueKind != JsonValueKind.Object) return default;
            try
            {
                string? traceParent = meta.Value.TryGetProperty(TraceParent, out JsonElement parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null;
                string? traceState = meta.Value.TryGetProperty(TraceState, out JsonElement state) && state.ValueKind == JsonValueKind.String ? state.GetString() : null;
                return Parse(traceParent, traceState);
            }
            catch (Exception)
            {
                return default;
            }
        }

        /// <summary>
        /// Adds <c>traceparent</c>/<c>tracestate</c> for <paramref name="activity"/> (or the current activity) to an
        /// outbound HTTP request, unless the request already carries them.
        /// </summary>
        internal static void Inject(HttpRequestMessage request, Activity? activity = null)
        {
            if (request == null || !VoltaicTelemetry.Enabled || !VoltaicTelemetry.PropagateTraceContext) return;
            Activity? source = activity ?? Activity.Current;
            if (source == null) return;
            try
            {
                DistributedContextPropagator.Current.Inject(source, request, static (object? carrier, string name, string value) =>
                {
                    if (carrier is HttpRequestMessage message && !message.Headers.Contains(name))
                    {
                        message.Headers.TryAddWithoutValidation(name, value);
                    }
                });
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        /// <summary>
        /// Returns MCP request parameters that carry <paramref name="activity"/>'s context in <c>_meta</c>, or the
        /// parameters unchanged when there is no span, propagation is off, or they are not a JSON object. Existing
        /// <c>_meta</c> fields are kept, and a <c>traceparent</c> the caller set is never replaced.
        /// </summary>
        internal static object? InjectIntoMeta(object? parameters, Activity? activity)
        {
            if (activity == null || !VoltaicTelemetry.Enabled || !VoltaicTelemetry.PropagateTraceContext) return parameters;
            try
            {
                string? traceParent = null;
                string? traceState = null;
                DistributedContextPropagator.Current.Inject(activity, null, (object? carrier, string name, string value) =>
                {
                    if (name == TraceParent) traceParent = value;
                    else if (name == TraceState) traceState = value;
                });
                if (String.IsNullOrEmpty(traceParent)) return parameters;

                JsonNode? node = parameters == null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters, JsonLimits.Serializer);
                if (node is not JsonObject root) return parameters;

                JsonNode? existingMeta = root["_meta"];
                JsonObject meta;
                if (existingMeta == null)
                {
                    meta = new JsonObject();
                    root["_meta"] = meta;
                }
                else if (existingMeta is JsonObject found)
                {
                    meta = found;
                }
                else
                {
                    return parameters;
                }

                if (meta.ContainsKey(TraceParent)) return parameters;
                meta[TraceParent] = traceParent;
                if (!String.IsNullOrEmpty(traceState) && !meta.ContainsKey(TraceState)) meta[TraceState] = traceState;
                return root;
            }
            catch (Exception)
            {
                return parameters;
            }
        }

        /// <summary>
        /// Starts long-running background work (a receive loop, a cleanup loop) without the current span, so the work
        /// it does later is never attributed to whatever operation happened to start it.
        /// </summary>
        internal static System.Threading.Tasks.Task RunDetached(Func<System.Threading.Tasks.Task> work)
        {
            Activity? previous = Activity.Current;
            Activity.Current = null;
            try
            {
                return System.Threading.Tasks.Task.Run(work);
            }
            finally
            {
                Activity.Current = previous;
            }
        }

        private static ActivityContext Parse(string? traceParent, string? traceState)
        {
            if (String.IsNullOrEmpty(traceParent)) return default;
            return ActivityContext.TryParse(traceParent, traceState, true, out ActivityContext context) ? context : default;
        }
    }
}
