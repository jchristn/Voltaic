namespace Voltaic.Mcp
{
    using Voltaic.Core;

    /// <summary>
    /// Provides a TCP-based MCP (Model Context Protocol) client implementation for making remote procedure calls over a network.
    /// This class extends JsonRpcClient with MCP-specific semantics: a <c>ping</c> request from the server is always
    /// answered with an empty result (<c>{}</c>), as the MCP specification requires of both parties, and other server
    /// requests are answered by handlers registered with <see cref="JsonRpcClient.RegisterRequestHandler"/> or with
    /// <c>-32601</c> (method not found).
    /// </summary>
    public class McpTcpClient : JsonRpcClient
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="McpTcpClient"/> class.
        /// </summary>
        public McpTcpClient()
            : base()
        {
            AnswerPingRequests();
        }
    }
}
