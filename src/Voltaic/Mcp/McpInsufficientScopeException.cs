namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// Thrown by a tool or method handler when the caller's access token lacks a scope the operation needs. On
    /// <see cref="McpHttpServer"/> the request is answered with HTTP 403 and an RFC 6750 challenge
    /// (<c>WWW-Authenticate: Bearer error="insufficient_scope", scope="..."</c>, plus <c>resource_metadata</c> when
    /// <see cref="McpHttpServer.ProtectedResourceMetadata"/> is set), as the MCP authorization specification recommends,
    /// so the client can obtain a token with that scope. On other transports, and when the response already started
    /// streaming, it is a JSON-RPC error with code <see cref="ErrorCode"/>.
    /// </summary>
    public class McpInsufficientScopeException : McpProtocolException
    {
        /// <summary>
        /// The JSON-RPC error code used for insufficient scope: <c>-32003</c>, in the implementation-defined server
        /// error range.
        /// </summary>
        public const int ErrorCode = -32003;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpInsufficientScopeException"/> class.
        /// </summary>
        /// <param name="requiredScope">The scope (or space-separated scopes) the operation needs. Must not be null or whitespace.</param>
        /// <param name="message">The error message, or null for a default that names the scope.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="requiredScope"/> is null or whitespace.</exception>
        public McpInsufficientScopeException(string requiredScope, string? message = null)
            : base(ErrorCode, message ?? $"Insufficient scope: this operation requires '{requiredScope}'.", new { scope = requiredScope })
        {
            if (String.IsNullOrWhiteSpace(requiredScope)) throw new ArgumentNullException(nameof(requiredScope));
            RequiredScope = requiredScope;
        }

        /// <summary>
        /// Gets the scope the operation requires. Never null.
        /// </summary>
        public string RequiredScope { get; }
    }
}
