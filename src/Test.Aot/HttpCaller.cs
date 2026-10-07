namespace Test.Aot
{
    using System.Threading.Tasks;
    using Voltaic.Mcp;

    /// <summary>
    /// Calls through <see cref="McpHttpClient"/>.
    /// </summary>
    internal sealed class HttpCaller : McpCaller
    {
        private readonly McpHttpClient _Client;

        /// <summary>
        /// Creates the caller.
        /// </summary>
        internal HttpCaller(McpHttpClient client)
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
