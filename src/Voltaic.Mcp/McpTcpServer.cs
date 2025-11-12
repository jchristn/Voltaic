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
        private string _ProtocolVersion = "2025-03-26";
        private string _ServerName = "Voltaic.Mcp.TcpServer";
        private string _ServerVersion = "1.0.0";

        /// <summary>
        /// Gets or sets the MCP protocol version.
        /// Default is "2025-03-26".
        /// </summary>
        public string ProtocolVersion
        {
            get => _ProtocolVersion;
            set => _ProtocolVersion = value ?? "2025-03-26";
        }

        /// <summary>
        /// Gets or sets the server name for MCP serverInfo.
        /// Default is "Voltaic.Mcp.TcpServer".
        /// </summary>
        public string ServerName
        {
            get => _ServerName;
            set => _ServerName = value ?? "Voltaic.Mcp.TcpServer";
        }

        /// <summary>
        /// Gets or sets the server version for MCP serverInfo.
        /// Default is "1.0.0".
        /// </summary>
        public string ServerVersion
        {
            get => _ServerVersion;
            set => _ServerVersion = value ?? "1.0.0";
        }

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
        /// Registers the built-in MCP methods: initialize, ping, echo, getTime, and getClients.
        /// Note: Unlike JsonRpcServer, this does not include the 'add' method.
        /// </summary>
        protected override void RegisterBuiltInMethods()
        {
            // MCP Protocol Methods
            RegisterMethod("initialize", (args) =>
            {
                // Read the client's requested protocol version from params
                string ClientProtocolVersion = _ProtocolVersion;
                if (args.HasValue && args.Value.TryGetProperty("protocolVersion", out JsonElement protocolVersionProp))
                {
                    ClientProtocolVersion = protocolVersionProp.GetString() ?? _ProtocolVersion;
                }

                // The initialize method returns server capabilities and info
                return new
                {
                    protocolVersion = ClientProtocolVersion,
                    capabilities = new
                    {
                        tools = new
                        {
                            listChanged = true
                        }
                    },
                    serverInfo = new
                    {
                        name = _ServerName,
                        version = _ServerVersion
                    }
                };
            });

            RegisterMethod("tools/call", (args) =>
            {
                // MCP tools/call handler - invokes a tool by name with arguments
                if (!args.HasValue)
                {
                    throw new ArgumentException("tools/call requires params with 'name' and 'arguments'");
                }

                // Extract tool name from params.name
                if (!args.Value.TryGetProperty("name", out JsonElement nameElement))
                {
                    throw new ArgumentException("tools/call requires 'name' parameter");
                }

                string toolName = nameElement.GetString() ?? throw new ArgumentException("Tool name cannot be null");

                // Extract arguments from params.arguments
                JsonElement? toolArguments = null;
                if (args.Value.TryGetProperty("arguments", out JsonElement argsElement))
                {
                    toolArguments = argsElement;
                }

                // Invoke the method directly (tools are registered as methods in this server)
                if (!TryInvokeMethod(toolName, toolArguments, out object? result))
                {
                    throw new ArgumentException($"Tool '{toolName}' not found");
                }

                // Return result in MCP format with content array
                return new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = result?.ToString() ?? string.Empty
                        }
                    }
                };
            });

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
