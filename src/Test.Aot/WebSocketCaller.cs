namespace Test.Aot
{
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    /// <summary>
    /// Calls through <see cref="McpWebsocketsClient"/>.
    /// </summary>
    internal sealed class WebSocketCaller : McpCaller
    {
        private readonly McpWebsocketsClient _Client;

        /// <summary>
        /// Creates the caller.
        /// </summary>
        internal WebSocketCaller(McpWebsocketsClient client)
        {
            _Client = client;
        }

        /// <inheritdoc />
        internal override Task<T> CallAsync<T>(string method, object? parameters)
        {
            return _Client.CallAsync<T>(method, parameters);
        }
    }
}
