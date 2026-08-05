namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// Resolves the protocol version and era for an incoming MCP request using a deterministic
    /// precedence order. The resolver is transport-agnostic: it accepts the individual signals a
    /// transport can observe rather than a transport-specific request object, which makes it unit
    /// testable in isolation. All members are static and thread-safe.
    /// </summary>
    public static class McpVersionResolver
    {
        /// <summary>
        /// Resolves the protocol version for a request from the available signals.
        /// Precedence, highest first:
        /// <list type="number">
        /// <item><description>The <c>MCP-Protocol-Version</c> header, when present.</description></item>
        /// <item><description>The <c>_meta</c> protocol-version value carried in the request body.</description></item>
        /// <item><description>Structural cues: an <c>initialize</c> method or a session header implies the
        /// handshake era; stateless routing headers imply the newest stateless revision.</description></item>
        /// <item><description>The default handshake version (<see cref="McpProtocol.LatestProtocolVersion"/>).</description></item>
        /// </list>
        /// When both a header and a body <c>_meta</c> version are present and disagree, a
        /// <see cref="McpProtocolException"/> with code <c>-32020</c> (header mismatch) is thrown.
        /// When an explicitly requested version is unknown, a <see cref="McpProtocolException"/> with
        /// code <c>-32022</c> (unsupported protocol version) is thrown.
        /// </summary>
        /// <param name="protocolVersionHeader">Value of the <c>MCP-Protocol-Version</c> header, or null.</param>
        /// <param name="metaProtocolVersion">Protocol version extracted from the request body <c>_meta</c>, or null.</param>
        /// <param name="method">The JSON-RPC method name, or null.</param>
        /// <param name="hasSessionIdHeader">True when the request carries an <c>Mcp-Session-Id</c> header.</param>
        /// <param name="hasRoutingHeaders">True when the request carries stateless routing headers (<c>Mcp-Method</c>/<c>Mcp-Name</c>).</param>
        /// <returns>The resolved version and era. Never null.</returns>
        /// <exception cref="McpProtocolException">
        /// Thrown with code <c>-32020</c> when header and body versions disagree, or with code
        /// <c>-32022</c> when an explicitly requested version is unsupported.
        /// </exception>
        public static McpResolvedVersion Resolve(
            string? protocolVersionHeader,
            string? metaProtocolVersion,
            string? method,
            bool hasSessionIdHeader,
            bool hasRoutingHeaders)
        {
            bool hasHeader = !String.IsNullOrWhiteSpace(protocolVersionHeader);
            bool hasMeta = !String.IsNullOrWhiteSpace(metaProtocolVersion);

            if (hasHeader && hasMeta && !StringComparer.Ordinal.Equals(protocolVersionHeader, metaProtocolVersion))
            {
                throw McpProtocolException.HeaderMismatch(
                    $"Header mismatch: {McpProtocol.ProtocolVersionHeader} header value '{protocolVersionHeader}' does not match request body protocol version '{metaProtocolVersion}'.");
            }

            if (hasHeader)
            {
                return ResolveExplicit(protocolVersionHeader!);
            }

            if (hasMeta)
            {
                return ResolveExplicit(metaProtocolVersion!);
            }

            if (IsHandshakeMethod(method) || hasSessionIdHeader)
            {
                return ForVersion(McpProtocol.LatestProtocolVersion);
            }

            if (hasRoutingHeaders)
            {
                return ForVersion(McpProtocol.NewestProtocolVersion);
            }

            return ForVersion(McpProtocol.LatestProtocolVersion);
        }

        /// <summary>
        /// Builds a resolved-version result for a known, supported version.
        /// </summary>
        /// <param name="version">A supported protocol version string.</param>
        /// <returns>The resolved version and era.</returns>
        /// <exception cref="ArgumentException">Thrown when the version is not in the registry.</exception>
        public static McpResolvedVersion ForVersion(string version)
        {
            McpProtocolVersionInfo? info = McpProtocol.GetVersionInfo(version);
            if (info == null)
            {
                throw new ArgumentException($"Unsupported MCP protocol version '{version}'.", nameof(version));
            }

            return new McpResolvedVersion(info);
        }

        private static McpResolvedVersion ResolveExplicit(string version)
        {
            McpProtocolVersionInfo? info = McpProtocol.GetVersionInfo(version);
            if (info == null)
            {
                throw McpProtocolException.UnsupportedProtocolVersion(version, McpProtocol.SupportedVersionStrings());
            }

            return new McpResolvedVersion(info);
        }

        private static bool IsHandshakeMethod(string? method)
        {
            return StringComparer.Ordinal.Equals(method, "initialize")
                || StringComparer.Ordinal.Equals(method, "notifications/initialized");
        }
    }
}
