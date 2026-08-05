namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// MCP protocol constants and the version registry used by Voltaic.
    /// </summary>
    public static class McpProtocol
    {
        /// <summary>
        /// The default handshake protocol version negotiated by Voltaic servers when a client
        /// does not request a specific version. This remains <c>2025-11-25</c> for backward
        /// compatibility; the newest overall revision is <see cref="NewestProtocolVersion"/>.
        /// </summary>
        public const string LatestProtocolVersion = "2025-11-25";

        /// <summary>
        /// The newest MCP protocol revision recognized by Voltaic (<c>2026-07-28</c>, stateless era).
        /// </summary>
        public const string NewestProtocolVersion = "2026-07-28";

        /// <summary>
        /// The original MCP protocol revision (<c>2024-11-05</c>, HTTP+SSE transport, now Deprecated).
        /// </summary>
        public const string ProtocolVersion20241105 = "2024-11-05";

        /// <summary>
        /// The <c>2025-03-26</c> revision that introduced the Streamable HTTP transport.
        /// </summary>
        public const string ProtocolVersion20250326 = "2025-03-26";

        /// <summary>
        /// The <c>2025-06-18</c> revision (structured output, elicitation, required protocol-version
        /// header, batching removed).
        /// </summary>
        public const string ProtocolVersion20250618 = "2025-06-18";

        /// <summary>
        /// The <c>2025-11-25</c> revision (icons, experimental in-core tasks, OIDC discovery).
        /// </summary>
        public const string ProtocolVersion20251125 = "2025-11-25";

        /// <summary>
        /// The <c>2026-07-28</c> stateless revision.
        /// </summary>
        public const string ProtocolVersion20260728 = "2026-07-28";

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
        /// Stateless header carrying the JSON-RPC <c>method</c> value (2026-07-28+).
        /// </summary>
        public const string MethodHeader = "Mcp-Method";

        /// <summary>
        /// Stateless header carrying the <c>params.name</c> or <c>params.uri</c> value (2026-07-28+).
        /// </summary>
        public const string NameHeader = "Mcp-Name";

        /// <summary>
        /// The <c>_meta</c> key carrying the per-request protocol version (2026-07-28+).
        /// </summary>
        public const string MetaProtocolVersionKey = "io.modelcontextprotocol/protocolVersion";

        /// <summary>
        /// The <c>_meta</c> key carrying client identity (2026-07-28+).
        /// </summary>
        public const string MetaClientInfoKey = "io.modelcontextprotocol/clientInfo";

        /// <summary>
        /// The <c>_meta</c> key carrying per-request client capabilities (2026-07-28+).
        /// </summary>
        public const string MetaClientCapabilitiesKey = "io.modelcontextprotocol/clientCapabilities";

        /// <summary>
        /// The <c>_meta</c> key carrying server identity in discovery results (2026-07-28+).
        /// </summary>
        public const string MetaServerInfoKey = "io.modelcontextprotocol/serverInfo";

        /// <summary>
        /// The <c>_meta</c> key associating a message with a task (2025-11-25 in-core tasks).
        /// </summary>
        public const string MetaRelatedTaskKey = "io.modelcontextprotocol/related-task";

        /// <summary>
        /// The extension identifier for the 2026-07-28 tasks extension.
        /// </summary>
        public const string TasksExtensionId = "io.modelcontextprotocol/tasks";

        private static readonly IReadOnlyList<McpProtocolVersionInfo> _Registry = new List<McpProtocolVersionInfo>
        {
            new McpProtocolVersionInfo(ProtocolVersion20241105, McpProtocolEra.Handshake, supportsSessions: false, supportsBatching: true, requiresProtocolVersionHeader: false, usesHeaderRouting: false),
            new McpProtocolVersionInfo(ProtocolVersion20250326, McpProtocolEra.Handshake, supportsSessions: true, supportsBatching: true, requiresProtocolVersionHeader: false, usesHeaderRouting: false),
            new McpProtocolVersionInfo(ProtocolVersion20250618, McpProtocolEra.Handshake, supportsSessions: true, supportsBatching: false, requiresProtocolVersionHeader: true, usesHeaderRouting: false),
            new McpProtocolVersionInfo(ProtocolVersion20251125, McpProtocolEra.Handshake, supportsSessions: true, supportsBatching: false, requiresProtocolVersionHeader: true, usesHeaderRouting: false),
            new McpProtocolVersionInfo(ProtocolVersion20260728, McpProtocolEra.Stateless, supportsSessions: false, supportsBatching: false, requiresProtocolVersionHeader: true, usesHeaderRouting: true)
        };

        /// <summary>
        /// Gets every MCP protocol revision supported by this release, oldest first.
        /// </summary>
        public static IReadOnlyList<McpProtocolVersionInfo> SupportedVersions => _Registry;

        /// <summary>
        /// Gets the protocol version strings supported by this release, oldest first.
        /// </summary>
        /// <returns>An ordered list of version strings.</returns>
        public static IReadOnlyList<string> SupportedVersionStrings()
        {
            return _Registry.Select(info => info.Version).ToList();
        }

        /// <summary>
        /// Determines whether the supplied protocol version is supported by this release.
        /// </summary>
        /// <param name="version">Protocol version to inspect. A null or blank value is not supported.</param>
        /// <returns>True if supported.</returns>
        public static bool IsSupportedVersion(string? version)
        {
            if (String.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            return _Registry.Any(info => StringComparer.Ordinal.Equals(info.Version, version));
        }

        /// <summary>
        /// Gets the registry entry for a version, or null when the version is unknown.
        /// </summary>
        /// <param name="version">Protocol version to look up. May be null.</param>
        /// <returns>The registry entry, or null.</returns>
        public static McpProtocolVersionInfo? GetVersionInfo(string? version)
        {
            if (String.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            return _Registry.FirstOrDefault(info => StringComparer.Ordinal.Equals(info.Version, version));
        }

        /// <summary>
        /// Gets the architectural era for a supported version.
        /// </summary>
        /// <param name="version">Protocol version to inspect.</param>
        /// <returns>The era of the version.</returns>
        /// <exception cref="ArgumentException">Thrown when the version is unsupported.</exception>
        public static McpProtocolEra GetEra(string? version)
        {
            McpProtocolVersionInfo? info = GetVersionInfo(version);
            if (info == null)
            {
                throw new ArgumentException($"Unsupported MCP protocol version '{version}'.", nameof(version));
            }

            return info.Era;
        }

        /// <summary>
        /// Negotiates a protocol version from a client-requested version.
        /// When no version is requested, the default handshake version
        /// (<see cref="LatestProtocolVersion"/>) is returned for backward compatibility.
        /// </summary>
        /// <param name="requestedVersion">The client-requested version. May be null or blank.</param>
        /// <returns>The version to use for the session.</returns>
        /// <exception cref="ArgumentException">Thrown when the requested version is unsupported.</exception>
        public static string NegotiateVersion(string? requestedVersion)
        {
            if (String.IsNullOrWhiteSpace(requestedVersion))
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
