namespace Voltaic
{
    /// <summary>
    /// Provides a TCP-based MCP (Model Context Protocol) client implementation for making remote procedure calls over a network.
    /// This class extends JsonRpcClient with MCP-specific semantics and branding.
    /// </summary>
    public class McpTcpClient : JsonRpcClient
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="McpTcpClient"/> class.
        /// </summary>
        public McpTcpClient()
            : base()
        {
        }
    }
}
