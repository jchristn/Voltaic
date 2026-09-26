namespace Voltaic.Mcp
{
    using System.Threading;

    /// <summary>
    /// Records, for the request being dispatched on this asynchronous flow, the stateless-era protocol version it
    /// selected, or null for a handshake-era request. Transports set it before dispatch; tools/call reads it to tell
    /// handlers whether Multi Round-Trip Requests are available.
    /// </summary>
    internal static class McpRequestProtocol
    {
        private static readonly AsyncLocal<string?> _StatelessVersion = new AsyncLocal<string?>();

        /// <summary>
        /// Gets the stateless-era version of the current request, or null when the request is handshake-era.
        /// </summary>
        internal static string? StatelessVersion => _StatelessVersion.Value;

        /// <summary>
        /// Sets the protocol era for the current request. Call it from the (async) request-processing method before
        /// dispatch: the value then flows into the handler and is discarded when that method returns.
        /// </summary>
        internal static void Set(string? statelessVersion)
        {
            _StatelessVersion.Value = statelessVersion;
        }
    }
}
