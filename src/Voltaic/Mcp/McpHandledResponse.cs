namespace Voltaic.Mcp
{
    using Voltaic.Core;

    /// <summary>
    /// A response produced by <see cref="McpMessageProcessor"/> and the stateless-era version it was served under
    /// (null for the handshake era).
    /// </summary>
    internal sealed class McpHandledResponse
    {
        internal McpHandledResponse(JsonRpcResponse response, string? statelessVersion)
        {
            Response = response;
            StatelessVersion = statelessVersion;
        }

        internal JsonRpcResponse Response { get; }

        internal string? StatelessVersion { get; }

        /// <summary>
        /// Gets or sets the scope a handler reported missing with <see cref="McpInsufficientScopeException"/>, or null.
        /// </summary>
        internal string? InsufficientScope { get; set; }
    }
}
