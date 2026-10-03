namespace Voltaic.A2A
{
    using System;
    using Voltaic.Core;

    /// <summary>
    /// Maps A2A HTTP+JSON routes and gRPC paths to bounded telemetry labels: the route template (never the raw path,
    /// which carries task and configuration IDs) and the A2A method it serves. Thread-safe (stateless).
    /// </summary>
    internal static class A2ATelemetry
    {
        /// <summary>
        /// Returns the route template of an HTTP+JSON task or message path and sets <paramref name="method"/> to the A2A
        /// method it serves; <see cref="VoltaicTelemetryNames.SpanHttpUnmatchedRoute"/> and null when it is not one.
        /// </summary>
        internal static string RestRouteOf(string httpMethod, string path, out string? method)
        {
            method = null;
            if (path == "/message:send") { method = A2AProtocol.SendMessage; return path; }
            if (path == "/message:stream") { method = A2AProtocol.SendStreamingMessage; return path; }

            string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments[0] != "tasks") return VoltaicTelemetryNames.SpanHttpUnmatchedRoute;
            if (segments.Length == 1) { method = A2AProtocol.ListTasks; return "/tasks"; }
            if (segments.Length == 2 && segments[1].EndsWith(":cancel", StringComparison.Ordinal)) { method = A2AProtocol.CancelTask; return "/tasks/{id}:cancel"; }
            if (segments.Length == 2 && segments[1].EndsWith(":subscribe", StringComparison.Ordinal)) { method = A2AProtocol.SubscribeToTask; return "/tasks/{id}:subscribe"; }
            if (segments.Length == 2) { method = A2AProtocol.GetTask; return "/tasks/{id}"; }
            if (segments[2] != "pushNotificationConfigs") return VoltaicTelemetryNames.SpanHttpUnmatchedRoute;
            if (segments.Length == 3)
            {
                method = httpMethod == "POST" ? A2AProtocol.CreateTaskPushNotificationConfig : A2AProtocol.ListTaskPushNotificationConfig;
                return "/tasks/{id}/pushNotificationConfigs";
            }

            method = httpMethod == "DELETE" ? A2AProtocol.DeleteTaskPushNotificationConfig : A2AProtocol.GetTaskPushNotificationConfig;
            return "/tasks/{id}/pushNotificationConfigs/{configId}";
        }

        /// <summary>
        /// Returns the A2A method an HTTP+JSON request path (with or without a query) calls, or <c>_OTHER</c>.
        /// </summary>
        internal static string RestMethodOf(string httpMethod, string pathAndQuery)
        {
            string path = pathAndQuery.Split('?')[0];
            if (StringComparer.OrdinalIgnoreCase.Equals(path, A2AProtocol.ExtendedAgentCardPath)) return A2AProtocol.GetExtendedAgentCard;
            RestRouteOf(httpMethod, path, out string? method);
            return method ?? VoltaicTelemetryNames.OtherValue;
        }

        /// <summary>
        /// Returns the A2A method of a gRPC path (<c>/lf.a2a.v1.A2AService/SendMessage</c> is <c>SendMessage</c>), or
        /// <c>_OTHER</c>.
        /// </summary>
        internal static string GrpcMethodOf(string path)
        {
            string trimmed = path.Split('?')[0];
            int slash = trimmed.LastIndexOf('/');
            string method = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            return A2AProtocol.IsValidMethod(method) ? method : VoltaicTelemetryNames.OtherValue;
        }
    }
}
