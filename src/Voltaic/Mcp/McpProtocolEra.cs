namespace Voltaic.Mcp
{
    /// <summary>
    /// Identifies the architectural era of an MCP protocol revision.
    /// The era determines how a request negotiates its version and whether the
    /// transport is session-oriented or stateless.
    /// </summary>
    public enum McpProtocolEra
    {
        /// <summary>
        /// Initialization-based revisions (<c>2024-11-05</c> through <c>2025-11-25</c>).
        /// These negotiate once via the <c>initialize</c>/<c>initialized</c> handshake and,
        /// on Streamable HTTP, may mint a session identified by <c>Mcp-Session-Id</c>.
        /// </summary>
        Handshake = 0,

        /// <summary>
        /// Stateless revisions (<c>2026-07-28</c> and later). Every request is self-describing
        /// via the <c>_meta</c> field, there is no handshake, and capabilities may be discovered
        /// up front through the optional <c>server/discover</c> RPC.
        /// </summary>
        Stateless = 1
    }
}
