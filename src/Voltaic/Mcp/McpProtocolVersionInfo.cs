namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// Describes the capabilities and transport shape of a single MCP protocol revision.
    /// Instances are immutable and are held in the <see cref="McpProtocol"/> registry.
    /// This type is thread-safe because all members are read-only.
    /// </summary>
    public sealed class McpProtocolVersionInfo
    {
        /// <summary>
        /// Gets the protocol version string in <c>YYYY-MM-DD</c> form. Never null or empty.
        /// </summary>
        public string Version { get; }

        /// <summary>
        /// Gets the architectural era of the revision.
        /// </summary>
        public McpProtocolEra Era { get; }

        /// <summary>
        /// Gets a value indicating whether the revision assigns Streamable HTTP sessions
        /// via the <c>Mcp-Session-Id</c> header. False for stateless revisions and for the
        /// original <c>2024-11-05</c> HTTP+SSE transport.
        /// </summary>
        public bool SupportsSessions { get; }

        /// <summary>
        /// Gets a value indicating whether JSON-RPC batching is permitted. Batching was removed
        /// in <c>2025-06-18</c>; it is permitted only on <c>2024-11-05</c> and <c>2025-03-26</c>.
        /// </summary>
        public bool SupportsBatching { get; }

        /// <summary>
        /// Gets a value indicating whether the <c>MCP-Protocol-Version</c> HTTP header is required
        /// on requests subsequent to initialization. Required from <c>2025-06-18</c> onward.
        /// </summary>
        public bool RequiresProtocolVersionHeader { get; }

        /// <summary>
        /// Gets a value indicating whether the revision uses the stateless header-routing model
        /// (<c>Mcp-Method</c>, <c>Mcp-Name</c>, <c>Mcp-Param-*</c>). True only for stateless revisions.
        /// </summary>
        public bool UsesHeaderRouting { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpProtocolVersionInfo"/> class.
        /// </summary>
        /// <param name="version">Protocol version string. May not be null or empty.</param>
        /// <param name="era">Architectural era of the revision.</param>
        /// <param name="supportsSessions">Whether Streamable HTTP sessions are used.</param>
        /// <param name="supportsBatching">Whether JSON-RPC batching is permitted.</param>
        /// <param name="requiresProtocolVersionHeader">Whether the protocol-version header is required.</param>
        /// <param name="usesHeaderRouting">Whether the stateless header-routing model applies.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="version"/> is null or empty.</exception>
        public McpProtocolVersionInfo(
            string version,
            McpProtocolEra era,
            bool supportsSessions,
            bool supportsBatching,
            bool requiresProtocolVersionHeader,
            bool usesHeaderRouting)
        {
            if (String.IsNullOrEmpty(version)) throw new ArgumentNullException(nameof(version));

            Version = version;
            Era = era;
            SupportsSessions = supportsSessions;
            SupportsBatching = supportsBatching;
            RequiresProtocolVersionHeader = requiresProtocolVersionHeader;
            UsesHeaderRouting = usesHeaderRouting;
        }
    }
}
