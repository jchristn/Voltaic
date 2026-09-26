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
        /// The protocol version a server assumes for a handshake-era HTTP request that carries no
        /// <c>MCP-Protocol-Version</c> header and has no other way to identify its version (such as a session's
        /// negotiated version): <c>2025-03-26</c>, as the Streamable HTTP specification requires for backward
        /// compatibility with clients that predate the header.
        /// </summary>
        public const string HeaderlessProtocolVersion = "2025-03-26";

        /// <summary>
        /// The well-known path (RFC 9728) at which <see cref="McpHttpServer"/> serves
        /// <see cref="McpHttpServer.ProtectedResourceMetadata"/>: <c>/.well-known/oauth-protected-resource</c>.
        /// </summary>
        public const string ProtectedResourceMetadataPath = "/.well-known/oauth-protected-resource";

        /// <summary>
        /// Stateless header carrying the JSON-RPC <c>method</c> value (2026-07-28+).
        /// </summary>
        public const string MethodHeader = "Mcp-Method";

        /// <summary>
        /// Stateless header carrying the <c>params.name</c> or <c>params.uri</c> value (2026-07-28+).
        /// </summary>
        public const string NameHeader = "Mcp-Name";

        /// <summary>
        /// Prefix of the stateless headers that mirror tool parameters annotated with <see cref="HeaderAnnotationKeyword"/>
        /// (<c>Mcp-Param-{Name}</c>, 2026-07-28+).
        /// </summary>
        public const string ParamHeaderPrefix = "Mcp-Param-";

        /// <summary>
        /// The JSON Schema extension keyword that designates a tool parameter to mirror into an
        /// <c>Mcp-Param-{Name}</c> header (2026-07-28+). Its value is the header name portion. It may annotate only
        /// <c>string</c>, <c>integer</c>, and <c>boolean</c> properties reachable from the schema root through
        /// <c>properties</c> keys alone, and must be a case-insensitively unique HTTP token within the schema.
        /// </summary>
        public const string HeaderAnnotationKeyword = "x-mcp-header";

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
        /// Gets the newest handshake-era revision in the registry (currently <c>2025-11-25</c>). This is
        /// the newest revision an <c>initialize</c> handshake can agree to, because stateless-era revisions
        /// define no <c>initialize</c> request and no sessions. The value is derived from
        /// <see cref="SupportedVersions"/>, so it moves automatically when a newer handshake-era revision
        /// is registered. Never null.
        /// </summary>
        public static string NewestHandshakeProtocolVersion
        {
            get
            {
                return _Registry.Last(info => info.Era == McpProtocolEra.Handshake).Version;
            }
        }

        /// <summary>
        /// Determines whether the supplied version is a supported handshake-era revision, meaning it
        /// can be negotiated through an <c>initialize</c> request.
        /// </summary>
        /// <param name="version">Protocol version to inspect. A null or blank value returns false.</param>
        /// <returns>True when the version is supported and belongs to <see cref="McpProtocolEra.Handshake"/>.</returns>
        public static bool IsHandshakeVersion(string? version)
        {
            McpProtocolVersionInfo? info = GetVersionInfo(version);
            return info != null && info.Era == McpProtocolEra.Handshake;
        }

        /// <summary>
        /// Negotiates a protocol version from a client-requested version.
        /// When no version is requested, the default handshake version
        /// (<see cref="LatestProtocolVersion"/>) is returned for backward compatibility.
        /// This method is era-agnostic: it returns any supported version unchanged, including the
        /// stateless <c>2026-07-28</c> revision. Callers answering an <c>initialize</c> request should use
        /// <see cref="NegotiateHandshakeVersion(string?, string)"/> instead, which never agrees to a
        /// stateless-era revision.
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

        /// <summary>
        /// Negotiates the protocol version for a session-based handshake (the <c>initialize</c> request).
        /// A stateless-era revision cannot be negotiated through <c>initialize</c>, so a request for one is
        /// answered with <paramref name="maximumHandshakeVersion"/>, the newest handshake-era revision the
        /// server will speak. The rules are:
        /// <list type="bullet">
        /// <item><description>No version requested (null or blank): <see cref="LatestProtocolVersion"/>, lowered to the maximum when the maximum is older.</description></item>
        /// <item><description>A handshake-era version at or below the maximum: that version.</description></item>
        /// <item><description>A handshake-era version above the maximum: the maximum.</description></item>
        /// <item><description>A stateless-era version (for example <c>2026-07-28</c>): the maximum.</description></item>
        /// <item><description>An unknown version: <see cref="ArgumentException"/>.</description></item>
        /// </list>
        /// This method is stateless and thread-safe.
        /// </summary>
        /// <param name="requestedVersion">The client-requested version. May be null or blank.</param>
        /// <param name="maximumHandshakeVersion">The newest handshake-era version to agree to. Must be a supported handshake-era revision; see <see cref="NewestHandshakeProtocolVersion"/> for the usual value.</param>
        /// <returns>The handshake-era version to use for the session. Never null.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="requestedVersion"/> is not a supported version, or when <paramref name="maximumHandshakeVersion"/> is not a supported handshake-era revision.</exception>
        public static string NegotiateHandshakeVersion(string? requestedVersion, string maximumHandshakeVersion)
        {
            if (!IsHandshakeVersion(maximumHandshakeVersion))
            {
                throw new ArgumentException(
                    $"The maximum handshake protocol version '{maximumHandshakeVersion}' is not a supported handshake-era revision.",
                    nameof(maximumHandshakeVersion));
            }

            if (String.IsNullOrWhiteSpace(requestedVersion))
            {
                return Older(LatestProtocolVersion, maximumHandshakeVersion);
            }

            McpProtocolVersionInfo? requested = GetVersionInfo(requestedVersion);
            if (requested == null)
            {
                throw new ArgumentException($"Unsupported MCP protocol version '{requestedVersion}'.", nameof(requestedVersion));
            }

            if (requested.Era != McpProtocolEra.Handshake)
            {
                return maximumHandshakeVersion;
            }

            return Older(requested.Version, maximumHandshakeVersion);
        }

        private static string Older(string first, string second)
        {
            int firstIndex = IndexOf(first);
            int secondIndex = IndexOf(second);
            return firstIndex <= secondIndex ? first : second;
        }

        private static int IndexOf(string version)
        {
            for (int index = 0; index < _Registry.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(_Registry[index].Version, version))
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
