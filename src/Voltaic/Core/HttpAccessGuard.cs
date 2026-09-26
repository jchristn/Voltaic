namespace Voltaic.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Shared request gates for the HttpListener-based servers (MCP HTTP, MCP WebSocket, and A2A HTTP):
    /// loopback-only enforcement, Origin validation, CORS headers that echo an allowed origin, and
    /// authentication-failure responses.
    /// </summary>
    internal static class HttpAccessGuard
    {
        /// <summary>
        /// The request headers MCP and A2A browser clients send, used as the default
        /// <c>Access-Control-Allow-Headers</c> value. A wildcard is not used because it does not cover
        /// <c>Authorization</c>.
        /// </summary>
        internal const string DefaultAllowHeaders = "Accept, Authorization, Content-Type, Last-Event-ID, Mcp-Method, Mcp-Name, MCP-Protocol-Version, Mcp-Session-Id, A2A-Version";

        /// <summary>
        /// Returns false when the server only serves loopback clients and the request came from elsewhere.
        /// </summary>
        internal static bool IsRemoteAllowed(HttpListenerRequest request, bool restrictToLoopbackClients)
        {
            if (!restrictToLoopbackClients) return true;

            IPEndPoint? remote;
            try
            {
                remote = request.RemoteEndPoint;
            }
            catch
            {
                return false;
            }

            return remote != null && LoopbackAddresses.IsLoopback(remote.Address);
        }

        /// <summary>
        /// Returns the request's Origin header, or null when absent.
        /// </summary>
        internal static string? GetOrigin(HttpListenerRequest request)
        {
            return request.Headers["Origin"];
        }

        /// <summary>
        /// Writes CORS headers for an allowed request. Nothing is written when CORS is disabled or the request
        /// carries no Origin (non-browser clients need no CORS grant). The allowed origin is echoed, never
        /// <c>*</c>, together with <c>Vary: Origin</c>; any <c>Access-Control-Allow-Origin</c> entry in
        /// <paramref name="corsHeaders"/> is ignored. When the request is a preflight that lists
        /// <c>Access-Control-Request-Headers</c>, those header names (valid tokens only) are allowed in place of
        /// the configured <c>Access-Control-Allow-Headers</c>.
        /// </summary>
        internal static void ApplyCorsHeaders(HttpListenerContext context, bool enableCors, Dictionary<string, string>? corsHeaders)
        {
            if (!enableCors) return;

            string? origin = GetOrigin(context.Request);
            if (String.IsNullOrEmpty(origin)) return;

            context.Response.AddHeader("Access-Control-Allow-Origin", origin);

            // A preflight names the headers the page wants to send. The origin is already allowed, so those
            // names are allowed too; this lets custom credential headers (X-API-Key), Mcp-Param-* headers, and
            // tracing headers through without listing them in advance.
            string? requestedHeaders = SanitizeHeaderList(context.Request.Headers["Access-Control-Request-Headers"]);
            context.Response.AddHeader("Vary", requestedHeaders != null ? "Origin, Access-Control-Request-Headers" : "Origin");
            if (requestedHeaders != null)
            {
                context.Response.AddHeader("Access-Control-Allow-Headers", requestedHeaders);
            }

            if (corsHeaders == null) return;
            foreach (KeyValuePair<string, string> kvp in corsHeaders)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(kvp.Key, "Access-Control-Allow-Origin")) continue;
                if (StringComparer.OrdinalIgnoreCase.Equals(kvp.Key, "Vary")) continue;
                if (requestedHeaders != null && StringComparer.OrdinalIgnoreCase.Equals(kvp.Key, "Access-Control-Allow-Headers")) continue;
                context.Response.AddHeader(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Returns a comma-separated list of the header names in an Access-Control-Request-Headers value, keeping
        /// only valid HTTP field-name tokens, or null when none remain.
        /// </summary>
        internal static string? SanitizeHeaderList(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;

            List<string> names = new List<string>();
            foreach (string part in value!.Split(','))
            {
                string name = part.Trim();
                if (name.Length == 0 || name.Length > 128) continue;

                bool valid = true;
                foreach (char character in name)
                {
                    bool tchar = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9')
                        || "!#$%&'*+-.^_`|~".IndexOf(character) >= 0;
                    if (!tchar)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }

            return names.Count == 0 ? null : String.Join(", ", names);
        }

        /// <summary>
        /// Rejects a request with 403 and no CORS grant.
        /// </summary>
        internal static async Task RejectAsync(HttpListenerContext context, int statusCode, string message, CancellationToken token)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(message);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Writes an authentication failure: the result's status code, its headers (for example
        /// WWW-Authenticate), CORS for an allowed origin, and the error message as text/plain.
        /// </summary>
        internal static async Task WriteAuthenticationFailureAsync(
            HttpListenerContext context,
            AuthenticationResult result,
            bool enableCors,
            Dictionary<string, string>? corsHeaders,
            CancellationToken token,
            string? resourceMetadataUrl = null)
        {
            context.Response.StatusCode = result.StatusCode;
            ApplyCorsHeaders(context, enableCors, corsHeaders);

            bool hasChallenge = false;
            foreach (KeyValuePair<string, string> header in result.Headers)
            {
                if (String.IsNullOrEmpty(header.Key) || header.Value == null) continue;
                string value = header.Value;
                if (StringComparer.OrdinalIgnoreCase.Equals(header.Key, "WWW-Authenticate"))
                {
                    hasChallenge = true;
                    value = WithResourceMetadata(value, resourceMetadataUrl);
                }

                context.Response.AddHeader(header.Key, value);
            }

            // A 401 must carry a WWW-Authenticate challenge (RFC 9110); MCP clients read resource_metadata from it to
            // discover the authorization server (MCP 2025-06-18 and later).
            if (result.StatusCode == 401 && !hasChallenge)
            {
                context.Response.AddHeader("WWW-Authenticate", WithResourceMetadata("Bearer", resourceMetadataUrl));
            }

            if (!String.IsNullOrEmpty(result.ErrorMessage))
            {
                byte[] errorBytes = Encoding.UTF8.GetBytes(result.ErrorMessage);
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = errorBytes.Length;
                await context.Response.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length, token).ConfigureAwait(false);
            }

            context.Response.Close();
        }

        // Adds resource_metadata to a Bearer challenge that lacks it when the server publishes protected resource
        // metadata, so clients can always discover the authorization server from a 401 or 403.
        internal static string WithResourceMetadata(string challenge, string? resourceMetadataUrl)
        {
            if (String.IsNullOrEmpty(resourceMetadataUrl)) return challenge;
            string trimmed = challenge.Trim();
            if (!trimmed.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase)) return challenge;
            if (trimmed.Length > 6 && !Char.IsWhiteSpace(trimmed[6])) return challenge;
            if (trimmed.IndexOf("resource_metadata=", StringComparison.OrdinalIgnoreCase) >= 0) return challenge;

            string parameter = "resource_metadata=\"" + resourceMetadataUrl!.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return trimmed.Length == 6 ? "Bearer " + parameter : trimmed + ", " + parameter;
        }
    }
}
