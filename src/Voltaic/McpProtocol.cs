namespace Voltaic
{
    /// <summary>
    /// MCP protocol constants used by Voltaic.
    /// </summary>
    public static class McpProtocol
    {
        /// <summary>
        /// The latest MCP protocol version targeted by Voltaic.
        /// </summary>
        public const string LatestProtocolVersion = "2025-11-25";

        /// <summary>
        /// The previous MCP protocol version supported for compatibility.
        /// </summary>
        public const string ProtocolVersion20250326 = "2025-03-26";

        /// <summary>
        /// JSON-RPC version used by MCP.
        /// </summary>
        public const string JsonRpcVersion = "2.0";

        /// <summary>
        /// Streamable HTTP session header.
        /// </summary>
        public const string SessionIdHeader = "MCP-Session-Id";

        /// <summary>
        /// Legacy streamable HTTP session header spelling accepted for compatibility.
        /// </summary>
        public const string LegacySessionIdHeader = "Mcp-Session-Id";

        /// <summary>
        /// Streamable HTTP protocol version header.
        /// </summary>
        public const string ProtocolVersionHeader = "MCP-Protocol-Version";

        /// <summary>
        /// Determines whether the supplied protocol version is supported by this release.
        /// </summary>
        /// <param name="version">Protocol version to inspect.</param>
        /// <returns>True if supported.</returns>
        public static bool IsSupportedVersion(string? version)
        {
            return version == LatestProtocolVersion || version == ProtocolVersion20250326;
        }

        /// <summary>
        /// Negotiates a protocol version from a client-requested version.
        /// </summary>
        /// <param name="requestedVersion">The client-requested version.</param>
        /// <returns>The version to use for the session.</returns>
        /// <exception cref="ArgumentException">Thrown when the requested version is unsupported.</exception>
        public static string NegotiateVersion(string? requestedVersion)
        {
            if (string.IsNullOrWhiteSpace(requestedVersion))
            {
                return LatestProtocolVersion;
            }

            if (IsSupportedVersion(requestedVersion))
            {
                return requestedVersion;
            }

            throw new ArgumentException($"Unsupported MCP protocol version '{requestedVersion}'.", nameof(requestedVersion));
        }
    }
}
