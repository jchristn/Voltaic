namespace Voltaic.Mcp
{
    using System;

    /// <summary>
    /// The outcome of resolving the protocol version and era for an incoming MCP request.
    /// Instances are immutable and therefore thread-safe.
    /// </summary>
    public sealed class McpResolvedVersion
    {
        /// <summary>
        /// Gets the resolved protocol version string. Never null or empty.
        /// </summary>
        public string Version { get; }

        /// <summary>
        /// Gets the architectural era of the resolved version.
        /// </summary>
        public McpProtocolEra Era { get; }

        /// <summary>
        /// Gets the registry entry for the resolved version. Never null.
        /// </summary>
        public McpProtocolVersionInfo Info { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpResolvedVersion"/> class.
        /// </summary>
        /// <param name="info">Registry entry for the resolved version. May not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="info"/> is null.</exception>
        public McpResolvedVersion(McpProtocolVersionInfo info)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            Version = info.Version;
            Era = info.Era;
        }
    }
}
