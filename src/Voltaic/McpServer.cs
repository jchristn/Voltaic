namespace Voltaic
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic;

    /// <summary>
    /// MCP server using stdio transport for subprocess-based operation.
    /// Implements Model Context Protocol stdio transport specification.
    /// </summary>
    public class McpServer : IDisposable
    {
        private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<object>>> _Methods;
        private readonly List<ToolDefinition> _Tools;
        private bool _IsDisposed = false;

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
        /// Default is "Voltaic.Mcp.StdioServer".
        /// </summary>
        public string ServerName
        {
            get => _ServerName;
            set => _ServerName = value ?? "Voltaic.Mcp.StdioServer";
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

        private string _ProtocolVersion = "2025-03-26";
        private string _ServerName = "Voltaic.Mcp.StdioServer";
        private string _ServerVersion = "1.0.0";

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
            _Tools = new List<ToolDefinition>();
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
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(description)) throw new ArgumentNullException(nameof(description));
            if (inputSchema == null) throw new ArgumentNullException(nameof(inputSchema));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            RegisterMethod(name, handler);

            _Tools.Add(new ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema
            });
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
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(description)) throw new ArgumentNullException(nameof(description));
            if (inputSchema == null) throw new ArgumentNullException(nameof(inputSchema));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            RegisterMethod(name, handler);

            _Tools.Add(new ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema
            });
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
            if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (String.IsNullOrEmpty(description)) throw new ArgumentNullException(nameof(description));
            if (inputSchema == null) throw new ArgumentNullException(nameof(inputSchema));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            RegisterMethod(name, handler);

            _Tools.Add(new ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema
            });
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
                    _Tools.Clear();
                }
            }
        }

        private void RegisterBuiltInMethods()
        {
            // MCP Protocol Methods
            RegisterMethod("initialize", (args) =>
            {
                string clientProtocolVersion = _ProtocolVersion;
                if (args.HasValue && args.Value.TryGetProperty("protocolVersion", out JsonElement protocolVersionProp))
                {
                    clientProtocolVersion = protocolVersionProp.GetString() ?? _ProtocolVersion;
                }

                return new
                {
                    protocolVersion = clientProtocolVersion,
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

            RegisterMethod("tools/list", (args) =>
            {
                List<object> toolsList = new List<object>();
                foreach (ToolDefinition tool in _Tools)
                {
                    toolsList.Add(new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        inputSchema = tool.InputSchema
                    });
                }

                return new
                {
                    tools = toolsList
                };
            });

            _Methods["tools/call"] = async (args, token) =>
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

                // Look up and invoke the tool
                if (!_Methods.ContainsKey(toolName))
                {
                    throw new ArgumentException($"Tool '{toolName}' not found");
                }

                object result = await _Methods[toolName](toolArguments, token).ConfigureAwait(false);

                // Return result in MCP format with content array
                return new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = JsonSerializer.Serialize(result)
                        }
                    }
                };
            };

            // Handle the initialized notification (no response needed, but we should handle it)
            RegisterMethod("notifications/initialized", (args) =>
            {
                LogToStderr("Received initialized notification from client");
                return new { };
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
                        response = new JsonRpcResponse
                        {
                            Error = new JsonRpcError
                            {
                                Code = -32603,
                                Message = "Internal error",
                                Data = ex.Message
                            },
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
