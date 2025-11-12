namespace Voltaic
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// MCP client using stdio transport for subprocess-based MCP servers.
    /// Implements Model Context Protocol stdio transport specification.
    /// </summary>
    public class McpClient : IDisposable
    {
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously

        /// <summary>
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

        /// <summary>
        /// Occurs when a notification (request without an ID) is received from the server.
        /// </summary>
        public event EventHandler<JsonRpcRequest>? NotificationReceived;

        /// <summary>
        /// Occurs when the client successfully connects to a server.
        /// </summary>
        public event EventHandler<ClientConnectedEventArgs>? Connected;

        /// <summary>
        /// Occurs when the client disconnects from the server.
        /// </summary>
        public event EventHandler<ClientDisconnectedEventArgs>? Disconnected;

        /// <summary>
        /// Occurs when a request is sent to the server.
        /// </summary>
        public event EventHandler<RequestSentEventArgs>? RequestSent;

        /// <summary>
        /// Occurs when a response is received from the server.
        /// </summary>
        public event EventHandler<ResponseReceivedEventArgs>? ResponseReceived;

        /// <summary>
        /// Gets a value indicating whether the client is currently connected to a server.
        /// </summary>
        public bool IsConnected => _IsConnected && _ServerProcess != null && !_ServerProcess.HasExited;

        private Process? _ServerProcess;
        private StreamWriter? _StdinWriter;
        private StreamReader? _StdoutReader;
        private StreamReader? _StderrReader;
        private CancellationTokenSource? _CancellationTokenSource;
        private Task? _ReceiveTask;
        private Task? _StderrTask;
        private bool _IsConnected = false;
        private int _RequestIdCounter = 0;
        private readonly ConcurrentDictionary<object, ClientPendingRequest> _PendingRequests;
        private string? _Endpoint;
        private DateTime _ConnectedUtc;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpClient"/> class.
        /// </summary>
        public McpClient()
        {
            _PendingRequests = new ConcurrentDictionary<object, ClientPendingRequest>();
        }

        /// <summary>
        /// Launches an MCP server as a subprocess and connects to it via stdio.
        /// </summary>
        /// <param name="executable">The path to the MCP server executable.</param>
        /// <param name="args">The command-line arguments to pass to the server.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the launch was successful; otherwise, false.</returns>
        public async Task<bool> LaunchServerAsync(string executable, string[] args, CancellationToken token = default)
        {
            try
            {
                Shutdown();

                _ServerProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = string.Join(" ", args),
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                _ServerProcess.Start();

                _StdinWriter = _ServerProcess.StandardInput;
                _StdoutReader = _ServerProcess.StandardOutput;
                _StderrReader = _ServerProcess.StandardError;

                _CancellationTokenSource = new CancellationTokenSource();
                _ReceiveTask = Task.Run(() => ReceiveLoop(_CancellationTokenSource.Token));
                _StderrTask = Task.Run(() => StderrLoop(_CancellationTokenSource.Token));

                _IsConnected = true;
                _Endpoint = $"{executable} {string.Join(" ", args)}";
                _ConnectedUtc = DateTime.UtcNow;
                LogMessage($"Launched MCP server: {executable} {string.Join(" ", args)}");
                RaiseConnected();

                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to launch MCP server: {ex.Message}");
                _IsConnected = false;
                return false;
            }
        }

        /// <summary>
        /// Asynchronously invokes a remote method and returns the result as the specified type.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the result into.</typeparam>
        /// <param name="method">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method. Can be null.</param>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Default is 30000 (30 seconds).</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the method result.</returns>
        public async Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken token = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("MCP client is not connected");

            int id = Interlocked.Increment(ref _RequestIdCounter);
            JsonRpcRequest request = new JsonRpcRequest
            {
                Method = method,
                Params = parameters,
                Id = id
            };

            TaskCompletionSource<JsonRpcResponse> tcs = new TaskCompletionSource<JsonRpcResponse>();
            ClientPendingRequest pendingRequest = new ClientPendingRequest(id, request, tcs);
            _PendingRequests[id] = pendingRequest;

            try
            {
                await SendRequestAsync(request, token).ConfigureAwait(false);
                RaiseRequestSent(pendingRequest);

                using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(timeoutMs);
                    cts.Token.Register(() => tcs.TrySetCanceled());
                    JsonRpcResponse response = await tcs.Task.ConfigureAwait(false);

                    if (response.Error != null)
                    {
                        throw new Exception($"RPC Error {response.Error.Code}: {response.Error.Message}");
                    }

                    if (response.Result is JsonElement jsonElement)
                    {
                        return JsonSerializer.Deserialize<T>(jsonElement.GetRawText())!;
                    }

                    if (response.Result == null)
                    {
                        return default(T)!;
                    }

                    return (T)Convert.ChangeType(response.Result, typeof(T));
                }
            }
            finally
            {
                _PendingRequests.TryRemove(id, out ClientPendingRequest? _);
            }
        }

        /// <summary>
        /// Asynchronously sends a notification to the server.
        /// Notifications are fire-and-forget and do not expect a response.
        /// </summary>
        /// <param name="method">The name of the notification method.</param>
        /// <param name="parameters">The parameters to pass with the notification. Can be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public async Task NotifyAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("MCP client is not connected");

            JsonRpcRequest notification = new JsonRpcRequest
            {
                Method = method,
                Params = parameters
                // No Id for notifications
            };

            await SendRequestAsync(notification, token).ConfigureAwait(false);
            RaiseRequestSent(new RequestSentEventArgs(notification));
        }

        /// <summary>
        /// Shuts down the MCP server gracefully by closing stdin.
        /// </summary>
        public void Shutdown()
        {
            if (_IsConnected)
            {
                _IsConnected = false;
                _CancellationTokenSource?.Cancel();

                // Clear pending requests
                foreach (ClientPendingRequest pending in _PendingRequests.Values)
                {
                    pending.TaskCompletionSource.TrySetCanceled();
                }
                _PendingRequests.Clear();

                // Close stdin to signal shutdown
                _StdinWriter?.Close();

                // Wait for process to exit
                if (_ServerProcess != null && !_ServerProcess.HasExited)
                {
                    bool exited = _ServerProcess.WaitForExit(5000);
                    if (!exited)
                    {
                        LogMessage("Server did not exit gracefully, killing process");
                        _ServerProcess.Kill(entireProcessTree: true);
                    }
                }

                RaiseDisconnected("MCP server shut down");
                LogMessage("MCP server shut down");
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="McpClient"/>.
        /// </summary>
        public void Dispose()
        {
            Shutdown();
            _CancellationTokenSource?.Dispose();
            _StdinWriter?.Dispose();
            _StdoutReader?.Dispose();
            _StderrReader?.Dispose();
            _ServerProcess?.Dispose();
        }

        private async Task SendRequestAsync(JsonRpcRequest request, CancellationToken token = default)
        {
            if (_StdinWriter == null)
                throw new InvalidOperationException("stdin is not initialized");

            string json = JsonSerializer.Serialize(request);
            await _StdinWriter.WriteLineAsync(json).ConfigureAwait(false);
            await _StdinWriter.FlushAsync().ConfigureAwait(false);

            LogMessage($"Sent: {json}");
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _StdoutReader != null)
                {
                    string? line = await _StdoutReader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        LogMessage("MCP server stdout closed");
                        break;
                    }

                    ProcessResponse(line);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    LogMessage($"Receive error: {ex.Message}");
                }
            }
            finally
            {
                _IsConnected = false;
            }
        }

        private async Task StderrLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _StderrReader != null)
                {
                    string? line = await _StderrReader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    LogMessage($"[SERVER STDERR] {line}");
                }
            }
            catch
            {
                // Swallow stderr errors
            }
        }

        private void ProcessResponse(string responseString)
        {
            try
            {
                LogMessage($"Received: {responseString}");

                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseString);
                if (response != null && response.Id != null)
                {
                    object lookupKey = response.Id;
                    if (response.Id is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Number)
                    {
                        lookupKey = jsonElement.GetInt32();
                    }

                    if (_PendingRequests.TryRemove(lookupKey, out ClientPendingRequest? pendingRequest))
                    {
                        RaiseResponseReceived(pendingRequest, response);
                        pendingRequest.TaskCompletionSource.SetResult(response);
                    }
                }
                else
                {
                    JsonRpcRequest? notification = JsonSerializer.Deserialize<JsonRpcRequest>(responseString);
                    if (notification != null && notification.Id == null)
                    {
                        // Invoke each handler individually to ensure exception isolation
                        if (NotificationReceived != null)
                        {
                            foreach (Delegate handler in NotificationReceived.GetInvocationList())
                            {
                                try
                                {
                                    ((EventHandler<JsonRpcRequest>)handler)(this, notification);
                                }
                                catch (Exception ex)
                                {
                                    LogMessage($"Error in NotificationReceived handler: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error processing response: {ex.Message}");
            }
        }

        private void LogMessage(string message)
        {
            string formattedMessage = $"[{DateTime.UtcNow:HH:mm:ss.fffZ}] {message}";

            // Invoke each handler individually to ensure exception isolation
            if (Log != null)
            {
                foreach (Delegate handler in Log.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<string>)handler)(this, formattedMessage);
                    }
                    catch
                    {
                        // Swallow exceptions in log handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseConnected()
        {
            if (Connected != null && _Endpoint != null)
            {
                ClientConnectedEventArgs eventArgs = new ClientConnectedEventArgs(_Endpoint, ClientConnectionTypeEnum.Stdio);
                foreach (Delegate handler in Connected.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<ClientConnectedEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseDisconnected(string reason)
        {
            if (Disconnected != null && _Endpoint != null)
            {
                ClientDisconnectedEventArgs eventArgs = new ClientDisconnectedEventArgs(_ConnectedUtc, _Endpoint, ClientConnectionTypeEnum.Stdio, reason);
                foreach (Delegate handler in Disconnected.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<ClientDisconnectedEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseRequestSent(ClientPendingRequest pendingRequest)
        {
            if (RequestSent != null)
            {
                RequestSentEventArgs eventArgs = new RequestSentEventArgs(pendingRequest);
                foreach (Delegate handler in RequestSent.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<RequestSentEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseRequestSent(RequestSentEventArgs eventArgs)
        {
            if (RequestSent != null)
            {
                foreach (Delegate handler in RequestSent.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<RequestSentEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

        private void RaiseResponseReceived(ClientPendingRequest pendingRequest, JsonRpcResponse response)
        {
            if (ResponseReceived != null)
            {
                ResponseReceivedEventArgs eventArgs = new ResponseReceivedEventArgs(pendingRequest, response);
                foreach (Delegate handler in ResponseReceived.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<ResponseReceivedEventArgs>)handler)(this, eventArgs);
                    }
                    catch
                    {
                        // Swallow exceptions in event handlers to prevent cascading failures
                    }
                }
            }
        }

#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    }
}
