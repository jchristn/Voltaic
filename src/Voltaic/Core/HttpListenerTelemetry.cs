namespace Voltaic.Core
{
    using System;
    using System.Diagnostics;
    using System.Net;

    /// <summary>
    /// The HTTP layer of Voltaic's <see cref="HttpListener"/>-based servers (MCP HTTP and A2A HTTP), which have no
    /// framework telemetry of their own: one <c>Server</c> span per request named <c>{method} {route}</c> that continues
    /// an inbound <c>traceparent</c>, and the <see cref="VoltaicTelemetryNames.HttpServerRequestDuration"/> histogram
    /// labeled with the method, the route template (a configured endpoint path, never the raw path), and the status code.
    /// Best-effort; never throws. Thread-safe.
    /// </summary>
    internal static class HttpListenerTelemetry
    {
        /// <summary>
        /// Starts the request operation, or returns null when nothing listens.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="protocol">The protocol label (<c>mcp</c> or <c>a2a</c>).</param>
        /// <param name="route">The route template the request matched, or <see cref="VoltaicTelemetryNames.SpanHttpUnmatchedRoute"/>.</param>
        internal static VoltaicOperation? StartRequest(HttpListenerRequest request, string protocol, string route)
        {
            if (!VoltaicTelemetry.Enabled) return null;
            try
            {
                // The accept loop's context never parents a request: only the client's traceparent does.
                ActivityContext parent = VoltaicTraceContext.Extract(name => request.Headers[name]);
                if (parent == default) Activity.Current = null;

                string method = MethodLabel(request.HttpMethod);
                TagList tags = new TagList
                {
                    { VoltaicTelemetryNames.AttrProtocol, protocol },
                    { VoltaicTelemetryNames.AttrHttpMethod, method },
                    { VoltaicTelemetryNames.AttrHttpRoute, route }
                };

                VoltaicOperation? operation = VoltaicOperation.Start(VoltaicInstruments.HttpServerDuration, VoltaicInstruments.HttpServerActive, method + " " + route, ActivityKind.Server, tags, parent);
                if (operation?.Activity != null)
                {
                    operation.SetSpanTag("url.path", request.Url?.AbsolutePath);
                    operation.SetSpanTag("url.scheme", request.Url?.Scheme);
                    operation.SetSpanTag("client.address", request.RemoteEndPoint?.Address.ToString());
                    operation.SetSpanTag("user_agent.original", request.UserAgent);
                }

                return operation;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Records the response status on the operation and ends it. A 5xx status marks the span as an error.
        /// </summary>
        internal static void Finish(VoltaicOperation? operation, HttpListenerResponse response)
        {
            if (operation == null) return;
            try
            {
                int status = response.StatusCode;
                operation.AddMetricTag(VoltaicTelemetryNames.AttrHttpStatusCode, VoltaicInstruments.CodeLabel(status));
                if (status >= 500) operation.SetError(VoltaicInstruments.CodeLabel(status));
            }
            catch (Exception)
            {
                // The response may be disposed; the duration is still recorded.
            }
            finally
            {
                operation.Dispose();
            }
        }

        /// <summary>
        /// The bounded HTTP method label: a standard method, or <c>_OTHER</c>.
        /// </summary>
        internal static string MethodLabel(string? method)
        {
            switch (method)
            {
                case "GET": return "GET";
                case "POST": return "POST";
                case "DELETE": return "DELETE";
                case "OPTIONS": return "OPTIONS";
                case "PUT": return "PUT";
                case "PATCH": return "PATCH";
                case "HEAD": return "HEAD";
                default: return VoltaicTelemetryNames.OtherValue;
            }
        }
    }
}
