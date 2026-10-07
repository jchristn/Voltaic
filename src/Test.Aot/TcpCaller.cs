namespace Test.Aot
{
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    /// <summary>
    /// Calls through <see cref="McpTcpClient"/>.
    /// </summary>
    internal sealed class TcpCaller : McpCaller
    {
        private readonly McpTcpClient _Client;

        /// <summary>
        /// Creates the caller.
        /// </summary>
        internal TcpCaller(McpTcpClient client)
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
