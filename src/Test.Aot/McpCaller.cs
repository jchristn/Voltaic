namespace Test.Aot
{
    using System.Threading.Tasks;

    /// <summary>
    /// The one call the MCP checks need, implemented by each client.
    /// </summary>
    internal abstract class McpCaller
    {
        /// <summary>
        /// Sends a request and deserializes its result.
        /// </summary>
        internal abstract Task<T> CallAsync<T>(string method, object? parameters);
    }
}
