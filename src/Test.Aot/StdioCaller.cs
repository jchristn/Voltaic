namespace Test.Aot
{
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    /// <summary>
    /// Calls through <see cref="McpClient"/>.
    /// </summary>
    internal sealed class StdioCaller : McpCaller
    {
        private readonly McpClient _Client;

        /// <summary>
        /// Creates the caller.
        /// </summary>
        internal StdioCaller(McpClient client)
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
