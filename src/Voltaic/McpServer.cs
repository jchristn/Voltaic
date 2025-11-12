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
        private readonly Dictionary<string, Func<JsonElement?, object>> _Methods;

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
            _Methods = new Dictionary<string, Func<JsonElement?, object>>();
            if (includeDefaultMethods) RegisterBuiltInMethods();
        }

        /// <summary>
        /// Registers a custom RPC method with the specified handler.
        /// </summary>
        /// <param name="name">The name of the method to register.</param>
        /// <param name="handler">The function that handles the method invocation.</param>
        public void RegisterMethod(string name, Func<JsonElement?, object> handler)
        {
            _Methods[name] = handler;
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
            _Methods.Clear();
        }

        private void RegisterBuiltInMethods()
        {
            _Methods["tools/call"] = (args) =>
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

                object result = _Methods[toolName](toolArguments);

                // Return result in MCP format with content array
                return new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = result.ToString()
                        }
                    }
                };
            };

            _Methods["ping"] = (_) => "pong";
            _Methods["echo"] = (args) =>
            {
                if (args.HasValue && args.Value.TryGetProperty("message", out JsonElement messageProp))
                    return messageProp.GetString() ?? "empty";
                return "empty";
            };
            _Methods["getTime"] = (_) => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
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
                            _Methods[request.Method](paramsElement);
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

                        object result = _Methods[request.Method](paramsElement);
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
