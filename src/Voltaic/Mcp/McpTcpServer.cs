namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides a TCP-based MCP (Model Context Protocol) server implementation for handling remote procedure calls over a network.
    /// This class extends JsonRpcServer with MCP-specific defaults and semantics.
    /// </summary>
    public class McpTcpServer : JsonRpcServer
    {
        private readonly McpEndpoint _Endpoint;

        /// <summary>
        /// Gets or sets the MCP protocol version.
        /// Default is <see cref="McpProtocol.LatestProtocolVersion"/>.
        /// </summary>
        public string ProtocolVersion
        {
            get => _Endpoint.ProtocolVersion;
            set => _Endpoint.ProtocolVersion = value ?? McpProtocol.LatestProtocolVersion;
        }

        /// <summary>
        /// Gets or sets the server name for MCP serverInfo.
        /// Default is "Voltaic.Mcp.TcpServer".
        /// </summary>
        public string ServerName
        {
            get => _Endpoint.ServerName;
            set => _Endpoint.ServerName = value ?? "Voltaic.Mcp.TcpServer";
        }

        /// <summary>
        /// Gets or sets the server version for MCP serverInfo.
        /// Default is "1.0.0".
        /// </summary>
        public string ServerVersion
        {
            get => _Endpoint.ServerVersion;
            set => _Endpoint.ServerVersion = value ?? "1.0.0";
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpTcpServer"/> class.
        /// </summary>
        /// <param name="ip">The IP address to listen on.</param>
        /// <param name="port">The port number to listen on.</param>
        /// <param name="includeDefaultMethods">True to include default MCP methods such as echo, ping, getTime, and getClients.</param>
        public McpTcpServer(IPAddress ip, int port, bool includeDefaultMethods = true)
            : base(ip, port, false)
        {
            _Endpoint = new McpEndpoint("Voltaic.Mcp.TcpServer");

            if (includeDefaultMethods)
            {
                RegisterBuiltInMethods();
            }
        }

        /// <summary>
        /// Registers a tool with metadata for MCP tool discovery using a synchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using a synchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="outputSchema">Output JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a synchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<JsonElement?, object> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, (args, _) => Task.FromResult(handler(args)));
            RegisterMethod(definition.Name, handler);
        }

        /// <summary>
        /// Registers a tool with metadata for MCP tool discovery using an asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using an asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="outputSchema">Output JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using an asynchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<JsonElement?, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, (args, _) => handler(args));
            RegisterMethod(definition.Name, handler);
        }

        /// <summary>
        /// Registers a tool with metadata for MCP tool discovery using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="name">Tool name.</param>
        /// <param name="description">Tool description.</param>
        /// <param name="inputSchema">Input JSON schema.</param>
        /// <param name="outputSchema">Output JSON schema.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="definition">Tool metadata.</param>
        /// <param name="handler">Tool handler.</param>
        public void RegisterTool(ToolDefinition definition, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Endpoint.RegisterTool(definition, handler);
            RegisterMethod(definition.Name, handler);
        }

        /// <summary>
        /// Registers a static MCP resource with a synchronous read handler.
        /// </summary>
        /// <param name="uri">Resource URI.</param>
        /// <param name="name">Resource name.</param>
        /// <param name="mimeType">Resource MIME type.</param>
        /// <param name="readHandler">Read handler.</param>
        public void RegisterResource(string uri, string name, string mimeType, Func<McpReadResourceResult> readHandler)
        {
            if (readHandler == null) throw new ArgumentNullException(nameof(readHandler));
            RegisterResource(CreateResource(uri, name, mimeType), (_, _) => Task.FromResult(readHandler()));
        }

        /// <summary>
        /// Registers a static MCP resource with a cancellable read handler.
        /// </summary>
        /// <param name="resource">Resource metadata.</param>
        /// <param name="readHandler">Read handler that receives the requested URI.</param>
        public void RegisterResource(McpResource resource, Func<string, CancellationToken, Task<McpReadResourceResult>> readHandler)
        {
            _Endpoint.RegisterResource(resource, readHandler);
        }

        /// <summary>
        /// Registers a dynamic MCP resource template with a synchronous read handler.
        /// </summary>
        /// <param name="uriTemplate">URI template.</param>
        /// <param name="name">Template name.</param>
        /// <param name="mimeType">Resource MIME type.</param>
        /// <param name="readHandler">Read handler that receives the matched URI.</param>
        public void RegisterResourceTemplate(string uriTemplate, string name, string mimeType, Func<string, McpReadResourceResult> readHandler)
        {
            if (readHandler == null) throw new ArgumentNullException(nameof(readHandler));
            RegisterResourceTemplate(CreateResourceTemplate(uriTemplate, name, mimeType), (uri, _, _) => Task.FromResult(readHandler(uri)));
        }

        /// <summary>
        /// Registers a dynamic MCP resource template with a cancellable read handler.
        /// </summary>
        /// <param name="template">Template metadata.</param>
        /// <param name="readHandler">Read handler that receives the matched URI and template variables.</param>
        public void RegisterResourceTemplate(
            McpResourceTemplate template,
            Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<McpReadResourceResult>> readHandler)
        {
            _Endpoint.RegisterResourceTemplate(template, readHandler);
        }

        /// <summary>
        /// Registers an MCP prompt with a synchronous handler.
        /// </summary>
        /// <param name="name">Prompt name.</param>
        /// <param name="description">Prompt description.</param>
        /// <param name="arguments">Prompt arguments.</param>
        /// <param name="handler">Prompt handler.</param>
        public void RegisterPrompt(string name, string description, IEnumerable<McpPromptArgument>? arguments, Func<JsonElement?, McpGetPromptResult> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterPrompt(CreatePrompt(name, description, arguments), (args, _) => Task.FromResult(handler(args)));
        }

        /// <summary>
        /// Registers an MCP prompt with a cancellable handler.
        /// </summary>
        /// <param name="prompt">Prompt metadata.</param>
        /// <param name="handler">Prompt handler.</param>
        public void RegisterPrompt(McpPrompt prompt, Func<JsonElement?, CancellationToken, Task<McpGetPromptResult>> handler)
        {
            _Endpoint.RegisterPrompt(prompt, handler);
        }

        /// <summary>
        /// Registers a completion provider for prompt arguments or resource template variables.
        /// </summary>
        /// <param name="referenceType">Reference type, usually <c>ref/prompt</c> or <c>ref/resource</c>.</param>
        /// <param name="referenceId">Prompt name or resource URI template. Use null to match any reference of the type.</param>
        /// <param name="argumentName">Argument name. Use null to match any argument on the reference.</param>
        /// <param name="handler">Completion handler.</param>
        public void RegisterCompletionProvider(
            string referenceType,
            string? referenceId,
            string? argumentName,
            Func<McpCompleteRequest, CancellationToken, Task<McpCompleteResult>> handler)
        {
            _Endpoint.RegisterCompletionProvider(referenceType, referenceId, argumentName, handler);
        }

        /// <summary>
        /// Notifies connected TCP clients that the tool list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyToolsChangedAsync(CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/tools/list_changed", null, token);
        }

        /// <summary>
        /// Notifies connected TCP clients that the resource list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyResourcesChangedAsync(CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/resources/list_changed", null, token);
        }

        /// <summary>
        /// Notifies connected TCP clients that a resource was updated.
        /// </summary>
        /// <param name="uri">Updated resource URI.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyResourceUpdatedAsync(string uri, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(uri)) throw new ArgumentNullException(nameof(uri));
            return BroadcastNotificationAsync("notifications/resources/updated", new { uri }, token);
        }

        /// <summary>
        /// Notifies connected TCP clients that the prompt list changed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyPromptsChangedAsync(CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/prompts/list_changed", null, token);
        }

        /// <summary>
        /// Notifies connected TCP clients about request progress.
        /// </summary>
        /// <param name="progressToken">Progress token from request metadata.</param>
        /// <param name="progress">Current progress value.</param>
        /// <param name="total">Optional total progress value.</param>
        /// <param name="message">Optional human-readable progress text.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyProgressAsync(object progressToken, double progress, double? total = null, string? message = null, CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/progress", new McpProgressNotification
            {
                ProgressToken = progressToken,
                Progress = progress,
                Total = total,
                Message = message
            }, token);
        }

        /// <summary>
        /// Notifies connected TCP clients that a request was cancelled.
        /// </summary>
        /// <param name="requestId">Cancelled request ID.</param>
        /// <param name="reason">Optional cancellation reason.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyCancelledAsync(object requestId, string? reason = null, CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/cancelled", new McpCancelledNotification
            {
                RequestId = requestId,
                Reason = reason
            }, token);
        }

        /// <summary>
        /// Sends an MCP log message notification to connected TCP clients.
        /// </summary>
        /// <param name="level">Syslog-style level.</param>
        /// <param name="data">JSON-serializable log data.</param>
        /// <param name="logger">Optional logger name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task NotifyLogMessageAsync(string level, object? data, string? logger = null, CancellationToken token = default)
        {
            return BroadcastNotificationAsync("notifications/message", new McpLogMessageNotification
            {
                Level = level,
                Logger = logger,
                Data = data
            }, token);
        }

        private static ToolDefinition CreateToolDefinition(string name, string description, object inputSchema, object? outputSchema)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(description)) throw new ArgumentNullException(nameof(description));
            if (inputSchema == null) throw new ArgumentNullException(nameof(inputSchema));

            return new ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema,
                OutputSchema = outputSchema
            };
        }

        private static McpResource CreateResource(string uri, string name, string mimeType)
        {
            if (String.IsNullOrEmpty(uri)) throw new ArgumentNullException(nameof(uri));
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            return new McpResource
            {
                Uri = uri,
                Name = name,
                MimeType = mimeType
            };
        }

        private static McpResourceTemplate CreateResourceTemplate(string uriTemplate, string name, string mimeType)
        {
            if (String.IsNullOrEmpty(uriTemplate)) throw new ArgumentNullException(nameof(uriTemplate));
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            return new McpResourceTemplate
            {
                UriTemplate = uriTemplate,
                Name = name,
                MimeType = mimeType
            };
        }

        private static McpPrompt CreatePrompt(string name, string description, IEnumerable<McpPromptArgument>? arguments)
        {
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            return new McpPrompt
            {
                Name = name,
                Description = description,
                Arguments = arguments?.ToList()
            };
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
                return _Endpoint.Initialize(args);
            });

            RegisterMethod("tools/list", (args) => _Endpoint.ListTools(args));
            RegisterMethod("tools/call", _Endpoint.CallToolAsync);
            RegisterMethod("resources/list", (args) => _Endpoint.ListResources(args));
            RegisterMethod("resources/templates/list", (args) => _Endpoint.ListResourceTemplates(args));
            RegisterMethod("resources/read", _Endpoint.ReadResourceAsync);
            RegisterMethod("resources/subscribe", (args) => _Endpoint.SubscribeResource(args));
            RegisterMethod("resources/unsubscribe", (args) => _Endpoint.UnsubscribeResource(args));
            RegisterMethod("prompts/list", (args) => _Endpoint.ListPrompts(args));
            RegisterMethod("prompts/get", _Endpoint.GetPromptAsync);
            RegisterMethod("completion/complete", _Endpoint.CompleteAsync);
            RegisterMethod("logging/setLevel", (args) => _Endpoint.SetLogLevel(args));
            RegisterMethod("notifications/cancelled", (args) => _Endpoint.Cancelled(args));
            RegisterMethod("notifications/initialized", (args) => _Endpoint.Initialized(args));

            RegisterTool("ping",
                "Returns 'pong' to verify server connectivity",
                new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                },
                (_) => "pong");

            RegisterTool("echo",
                "Echoes back the provided message",
                new
                {
                    type = "object",
                    properties = new
                    {
                        message = new
                        {
                            type = "string",
                            description = "The message to echo back"
                        }
                    },
                    required = new[] { "message" }
                },
                (args) =>
                {
                    if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                        return messageProp.GetString() ?? "empty";
                    return "empty";
                });

            RegisterTool("getTime",
                "Returns the current UTC time in ISO format",
                new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                },
                (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

            RegisterTool("getClients",
                "Returns a list of all connected client IDs",
                new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                },
                (_) => GetConnectedClients());
        }
    }
}
