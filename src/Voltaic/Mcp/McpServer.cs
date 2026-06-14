namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// MCP server using stdio transport for subprocess-based operation.
    /// Implements Model Context Protocol stdio transport specification.
    /// </summary>
    public class McpServer : IDisposable
    {
        private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<object>>> _Methods;
        private readonly McpEndpoint _Endpoint;
        private bool _IsDisposed = false;

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
        /// Default is "Voltaic.Mcp.StdioServer".
        /// </summary>
        public string ServerName
        {
            get => _Endpoint.ServerName;
            set => _Endpoint.ServerName = value ?? "Voltaic.Mcp.StdioServer";
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
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpServer"/> class.
        /// </summary>
        /// <param name="includeDefaultMethods">True to include default methods such as echo, ping, and getTime.</param>
        public McpServer(bool includeDefaultMethods = true)
        {
            _Methods = new Dictionary<string, Func<JsonElement?, CancellationToken, Task<object>>>();
            _Endpoint = new McpEndpoint("Voltaic.Mcp.StdioServer")
            {
                SupportsListChangedNotifications = false
            };
            if (includeDefaultMethods) RegisterBuiltInMethods();
        }

        /// <summary>
        /// Registers a custom RPC method with the specified synchronous handler.
        /// The handler is wrapped internally to support async invocation.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The function that handles the method invocation.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<JsonElement?, object> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Methods[name] = (args, _) => Task.FromResult(handler(args));
        }

        /// <summary>
        /// Registers a custom RPC method with the specified asynchronous handler.
        /// Use this overload when the handler needs to perform asynchronous operations such as
        /// database queries, HTTP calls, or file I/O.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<JsonElement?, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Methods[name] = (args, _) => handler(args);
        }

        /// <summary>
        /// Registers a custom RPC method with the specified asynchronous handler that accepts a cancellation token.
        /// Use this overload when the handler needs to perform cancellable asynchronous operations.
        /// The cancellation token provided to the handler is the same token used by the server's request processing.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The async function that handles the method invocation with cancellation support.</param>
        /// <exception cref="ArgumentNullException">Thrown when name or handler is null.</exception>
        public void RegisterMethod(string name, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _Methods[name] = handler;
        }

        /// <summary>
        /// Registers a tool with metadata for MCP protocol tool discovery using a synchronous handler.
        /// This registers both the method handler and the tool definition for tools/list.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="handler">The function that handles the tool invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using a synchronous handler.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="outputSchema">The JSON schema object defining structured output, or null.</param>
        /// <param name="handler">The function that handles the tool invocation.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, object> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a synchronous handler.
        /// </summary>
        /// <param name="definition">The tool definition.</param>
        /// <param name="handler">The function that handles the tool invocation.</param>
        public void RegisterTool(ToolDefinition definition, Func<JsonElement?, object> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Endpoint.RegisterTool(definition, (args, _) => Task.FromResult(handler(args)));
            RegisterMethod(definition.Name, handler);
        }

        /// <summary>
        /// Registers a tool with metadata for MCP protocol tool discovery using an asynchronous handler.
        /// This registers both the method handler and the tool definition for tools/list.
        /// Use this overload when the handler needs to perform asynchronous operations such as
        /// database queries, HTTP calls, or file I/O.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="handler">The async function that handles the tool invocation. Receives optional JSON parameters and returns a result object.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using an asynchronous handler.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="outputSchema">The JSON schema object defining structured output, or null.</param>
        /// <param name="handler">The async function that handles the tool invocation.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using an asynchronous handler.
        /// </summary>
        /// <param name="definition">The tool definition.</param>
        /// <param name="handler">The async function that handles the tool invocation.</param>
        public void RegisterTool(ToolDefinition definition, Func<JsonElement?, Task<object>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _Endpoint.RegisterTool(definition, (args, _) => handler(args));
            RegisterMethod(definition.Name, handler);
        }

        /// <summary>
        /// Registers a tool with metadata for MCP protocol tool discovery using an asynchronous handler that accepts a cancellation token.
        /// This registers both the method handler and the tool definition for tools/list.
        /// Use this overload when the handler needs to perform cancellable asynchronous operations.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="handler">The async function that handles the tool invocation with cancellation support.</param>
        /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
        public void RegisterTool(string name, string description, object inputSchema, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, null), handler);
        }

        /// <summary>
        /// Registers a tool with input and output schema metadata using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="name">The name of the tool.</param>
        /// <param name="description">A description of what the tool does.</param>
        /// <param name="inputSchema">The JSON schema object defining the tool's input parameters.</param>
        /// <param name="outputSchema">The JSON schema object defining structured output, or null.</param>
        /// <param name="handler">The async function that handles the tool invocation with cancellation support.</param>
        public void RegisterTool(string name, string description, object inputSchema, object? outputSchema, Func<JsonElement?, CancellationToken, Task<object>> handler)
        {
            RegisterTool(CreateToolDefinition(name, description, inputSchema, outputSchema), handler);
        }

        /// <summary>
        /// Registers a tool from a full tool definition using a cancellable asynchronous handler.
        /// </summary>
        /// <param name="definition">The tool definition.</param>
        /// <param name="handler">The async function that handles the tool invocation with cancellation support.</param>
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
        /// Runs the MCP server, reading from stdin and writing to stdout.
        /// Blocks until stdin is closed or cancellation is requested.
        /// </summary>
        /// <param name="token">Cancellation token to stop the server.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public async Task RunAsync(CancellationToken token = default)
        {
            using StreamReader stdin = new StreamReader(Console.OpenStandardInput());
            using StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

            LogToStderr("MCP server started");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await stdin.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        LogToStderr("stdin closed, shutting down");
                        break;
                    }

                    await ProcessRequestAsync(stdout, line, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogToStderr($"Fatal error: {ex.Message}");
                throw;
            }
            finally
            {
                LogToStderr("MCP server stopped");
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="McpServer"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="McpServer"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_IsDisposed)
            {
                _IsDisposed = true;

                if (disposing)
                {
                    _Methods.Clear();
                    _Endpoint.Clear();
                }
            }
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

        private static void ValidateToolDefinition(ToolDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (String.IsNullOrEmpty(definition.Name)) throw new ArgumentException("Tool definition must include a name.", nameof(definition));
            if (String.IsNullOrEmpty(definition.Description)) throw new ArgumentException("Tool definition must include a description.", nameof(definition));
            if (definition.InputSchema == null) throw new ArgumentException("Tool definition must include an input schema.", nameof(definition));
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

        private void RegisterBuiltInMethods()
        {
            // MCP Protocol Methods
            RegisterMethod("initialize", (args) =>
            {
                return _Endpoint.Initialize(args);
            });

            RegisterMethod("tools/list", (args) =>
            {
                return _Endpoint.ListTools(args);
            });

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

            // Handle the initialized notification (no response needed, but we should handle it)
            RegisterMethod("notifications/initialized", (args) =>
            {
                LogToStderr("Received initialized notification from client");
                return _Endpoint.Initialized(args);
            });

            // Register built-in tools with proper MCP tool metadata
            RegisterTool("ping",
                "Returns 'pong' to verify server connectivity",
                new
                {
                    type = "object",
                    properties = new { },
                    required = new string[] { }
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
                    required = new string[] { }
                },
                (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private async Task ProcessRequestAsync(StreamWriter stdout, string requestString, CancellationToken token = default)
        {
            try
            {
                LogToStderr($"Received: {requestString}");

                JsonRpcRequest? request = JsonSerializer.Deserialize<JsonRpcRequest>(requestString);
                if (request == null)
                {
                    JsonRpcResponse errorResponse = new JsonRpcResponse
                    {
                        Error = JsonRpcError.InvalidRequest(),
                        Id = null
                    };
                    await SendResponseAsync(stdout, errorResponse, token).ConfigureAwait(false);
                    return;
                }

                // If no ID, it's a notification - process but don't respond
                if (request.Id == null)
                {
                    LogToStderr($"Processing notification: {request.Method}");
                    // Process notification silently
                    if (_Methods.ContainsKey(request.Method))
                    {
                        try
                        {
                            JsonElement? paramsElement = null;
                            if (request.Params is JsonElement jsonElement)
                            {
                                paramsElement = jsonElement;
                            }
                            await _Methods[request.Method](paramsElement, token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogToStderr($"Error processing notification: {ex.Message}");
                        }
                    }
                    return;
                }

                JsonRpcResponse response;

                if (_Methods.ContainsKey(request.Method))
                {
                    try
                    {
                        JsonElement? paramsElement = null;
                        if (request.Params is JsonElement jsonElement)
                        {
                            paramsElement = jsonElement;
                        }

                        object result = await _Methods[request.Method](paramsElement, token).ConfigureAwait(false);
                        response = new JsonRpcResponse
                        {
                            Result = result,
                            Id = request.Id
                        };
                    }
                    catch (Exception ex)
                    {
                        JsonRpcError error = ex is McpProtocolException protocolException
                            ? protocolException.ToJsonRpcError()
                            : new JsonRpcError
                            {
                                Code = -32603,
                                Message = "Internal error",
                                Data = ex.Message
                            };

                        response = new JsonRpcResponse
                        {
                            Error = error,
                            Id = request.Id
                        };
                    }
                }
                else
                {
                    response = new JsonRpcResponse
                    {
                        Error = JsonRpcError.MethodNotFound(),
                        Id = request.Id
                    };
                }

                await SendResponseAsync(stdout, response, token).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                JsonRpcResponse parseError = new JsonRpcResponse
                {
                    Error = JsonRpcError.ParseError(),
                    Id = null
                };
                await SendResponseAsync(stdout, parseError, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogToStderr($"Error processing request: {ex.Message}");
            }
        }

        private async Task SendResponseAsync(StreamWriter stdout, JsonRpcResponse response, CancellationToken token = default)
        {
            try
            {
                string json = JsonSerializer.Serialize(response);
                await stdout.WriteLineAsync(json).ConfigureAwait(false);
                await stdout.FlushAsync().ConfigureAwait(false);
                LogToStderr($"Sent: {json}");
            }
            catch (Exception ex)
            {
                LogToStderr($"Error sending response: {ex.Message}");
            }
        }

        private void LogToStderr(string message)
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fffZ}] {message}");

            // Invoke each handler individually to ensure exception isolation
            if (Log != null)
            {
                foreach (Delegate handler in Log.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<string>)handler)(this, message);
                    }
                    catch
                    {
                        // Swallow exceptions in log handlers to prevent cascading failures
                    }
                }
            }
        }
    }
}
