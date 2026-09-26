namespace Test.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;
    using Voltaic.Mcp;

    /// <summary>
    /// Raw Streamable HTTP request helpers for tests that need full control over MCP headers:
    /// authorization, session, protocol version, and the stateless routing headers.
    /// </summary>
    internal static class McpHttpTestRequests
    {
        /// <summary>
        /// The loopback address the negotiation, stateless-result, and auth-parity suites bind to.
        /// </summary>
        public const string Loopback = "127.0.0.1";

        /// <summary>
        /// The Authorization header value the auth-enabled test servers accept.
        /// </summary>
        public const string ValidToken = "Bearer test-token";

        /// <summary>
        /// Builds the per-request <c>_meta</c> block a stateless (2026-07-28) client sends, matching the
        /// shape Claude Code 2.1.x sends.
        /// </summary>
        public static Dictionary<string, object?> StatelessMeta(string version = McpProtocol.ProtocolVersion20260728)
        {
            return new Dictionary<string, object?>
            {
                { McpProtocol.MetaProtocolVersionKey, version },
                { McpProtocol.MetaClientInfoKey, new { name = "claude-code", version = "2.1.281" } },
                { McpProtocol.MetaClientCapabilitiesKey, new { elicitation = new { } } }
            };
        }

        /// <summary>
        /// Sends a stateless 2026-07-28 request with the protocol-version and <c>Mcp-Method</c> headers,
        /// and <c>Mcp-Name</c> when <paramref name="nameHeader"/> is supplied. The <c>_meta</c> block is
        /// added to <paramref name="parameters"/>. The request goes to <c>/mcp/</c> unless
        /// <paramref name="path"/> names another endpoint path.
        /// </summary>
        public static Task<RpcResult> SendStatelessAsync(
            HttpMcpTestServerFixture fixture,
            string method,
            object? id,
            Dictionary<string, object?>? parameters,
            string? nameHeader,
            string? authorization,
            CancellationToken token,
            string path = "/mcp/")
        {
            Dictionary<string, object?> withMeta = parameters != null
                ? new Dictionary<string, object?>(parameters)
                : new Dictionary<string, object?>();
            withMeta["_meta"] = StatelessMeta();

            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { McpProtocol.ProtocolVersionHeader, McpProtocol.ProtocolVersion20260728 },
                { McpProtocol.MethodHeader, method }
            };

            if (nameHeader != null)
            {
                headers[McpProtocol.NameHeader] = nameHeader;
            }

            if (authorization != null)
            {
                headers["Authorization"] = authorization;
            }

            return SendAsync(fixture, BuildBody(method, id, withMeta), headers, true, token, path);
        }

        /// <summary>
        /// Sends a single JSON-RPC request to <c>/mcp/</c> with optional session, protocol-version, and
        /// authorization headers.
        /// </summary>
        public static Task<RpcResult> SendAsync(
            HttpMcpTestServerFixture fixture,
            string method,
            object? id,
            object? parameters,
            string? sessionId,
            string? protocolVersion,
            string? authorization,
            CancellationToken token)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            if (sessionId != null)
            {
                headers[McpProtocol.SessionIdHeader] = sessionId;
            }

            if (protocolVersion != null)
            {
                headers[McpProtocol.ProtocolVersionHeader] = protocolVersion;
            }

            if (authorization != null)
            {
                headers["Authorization"] = authorization;
            }

            return SendAsync(fixture, BuildBody(method, id, parameters), headers, true, token);
        }

        /// <summary>
        /// Sends a raw body to <c>/mcp/</c>, or to <paramref name="path"/> when supplied, with the supplied
        /// headers. When <paramref name="mcpAccept"/> is true the Streamable HTTP <c>Accept</c> header is added.
        /// </summary>
        public static async Task<RpcResult> SendAsync(
            HttpMcpTestServerFixture fixture,
            string body,
            IDictionary<string, string> headers,
            bool mcpAccept,
            CancellationToken token,
            string path = "/mcp/")
        {
            using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}{path}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            if (mcpAccept)
            {
                message.Headers.Accept.ParseAdd("application/json");
                message.Headers.Accept.ParseAdd("text/event-stream");
            }

            foreach (KeyValuePair<string, string> header in headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return await ToResultAsync(fixture, message, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends a DELETE to <c>/mcp/</c> to terminate a session.
        /// </summary>
        public static async Task<RpcResult> DeleteSessionAsync(HttpMcpTestServerFixture fixture, string sessionId, string? authorization, CancellationToken token)
        {
            using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Delete, $"{fixture.BaseUrl}/mcp/");
            message.Headers.TryAddWithoutValidation(McpProtocol.SessionIdHeader, sessionId);
            if (authorization != null)
            {
                message.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            return await ToResultAsync(fixture, message, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs <c>initialize</c> requesting <paramref name="version"/> (omitted when null). The
        /// response's <see cref="RpcResult.SessionId"/> carries the new session.
        /// </summary>
        public static Task<RpcResult> InitializeAsync(HttpMcpTestServerFixture fixture, string? version, string? authorization, CancellationToken token)
        {
            Dictionary<string, object?> parameters = new Dictionary<string, object?>
            {
                { "capabilities", new { } },
                { "clientInfo", new { name = "voltaic-test", version = "1.0.0" } }
            };

            parameters["protocolVersion"] = version ?? McpProtocol.LatestProtocolVersion;

            return SendAsync(fixture, "initialize", 1, parameters, null, null, authorization, token);
        }

        /// <summary>
        /// Builds a JSON-RPC request body. A null <paramref name="id"/> produces a notification.
        /// </summary>
        public static string BuildBody(string method, object? id, object? parameters)
        {
            Dictionary<string, object?> body = new Dictionary<string, object?>
            {
                { "jsonrpc", "2.0" },
                { "method", method }
            };

            if (parameters != null)
            {
                body["params"] = parameters;
            }

            if (id != null)
            {
                body["id"] = id;
            }

            return JsonSerializer.Serialize(body);
        }

        /// <summary>
        /// Builds an authentication handler that accepts only <see cref="ValidToken"/> and reports
        /// <paramref name="principal"/> as the caller.
        /// </summary>
        public static Func<HttpListenerRequest, Task<AuthenticationResult>> BearerAuth(string principal = "test-user")
        {
            return request =>
            {
                bool valid = StringComparer.Ordinal.Equals(request.Headers["Authorization"], ValidToken);
                return Task.FromResult(new AuthenticationResult
                {
                    IsAuthenticated = valid,
                    Principal = valid ? principal : null,
                    StatusCode = valid ? 200 : 401,
                    ErrorMessage = valid ? null : "invalid token"
                });
            };
        }

        /// <summary>
        /// Starts an HTTP MCP test server bound to <see cref="Loopback"/>, with a bearer-token
        /// authentication handler when <paramref name="withAuth"/> is true.
        /// </summary>
        public static Task<HttpMcpTestServerFixture> StartAsync(bool withAuth, Action<McpHttpServer>? configure, CancellationToken token)
        {
            return HttpMcpTestServerFixture.StartAsync(
                token,
                server =>
                {
                    if (withAuth)
                    {
                        server.AuthenticationHandler = BearerAuth();
                    }

                    configure?.Invoke(server);
                },
                hostname: Loopback);
        }

        private static async Task<RpcResult> ToResultAsync(HttpMcpTestServerFixture fixture, HttpRequestMessage message, CancellationToken token)
        {
            using HttpResponseMessage response = await fixture.Client.SendAsync(message, token).ConfigureAwait(false);
            string responseBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            string? sessionId = response.Headers.TryGetValues(McpProtocol.SessionIdHeader, out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : null;

            return new RpcResult(response.StatusCode, responseBody, sessionId, response.Content.Headers.ContentType?.MediaType);
        }
    }
}
