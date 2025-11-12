namespace Voltaic.Mcp
{
    using System;
    using System.Net;
    using System.Text.Json;
    using Voltaic.JsonRpc;

    /// <summary>
    /// Provides a TCP-based MCP (Model Context Protocol) server implementation for handling remote procedure calls over a network.
    /// This class extends JsonRpcServer with MCP-specific defaults and semantics.
    /// </summary>
    public class McpTcpServer : JsonRpcServer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="McpTcpServer"/> class.
        /// </summary>
        /// <param name="ip">The IP address to listen on.</param>
        /// <param name="port">The port number to listen on.</param>
        /// <param name="includeDefaultMethods">True to include default MCP methods such as echo, ping, getTime, and getClients.</param>
        public McpTcpServer(IPAddress ip, int port, bool includeDefaultMethods = true)
            : base(ip, port, includeDefaultMethods)
        {
        }

        /// <summary>
        /// Registers the built-in MCP methods: ping, echo, getTime, and getClients.
        /// Note: Unlike JsonRpcServer, this does not include the 'add' method.
        /// </summary>
        protected override void RegisterBuiltInMethods()
        {
            RegisterMethod("ping", (_) => "pong");
            RegisterMethod("echo", (args) =>
            {
                if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                    return messageProp.GetString() ?? "empty";
                return "empty";
            });
            RegisterMethod("getTime", (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            RegisterMethod("getClients", (_) => GetConnectedClients());
        }
    }
}
