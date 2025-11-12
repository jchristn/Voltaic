namespace Voltaic.Mcp
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.JsonRpc;

    /// <summary>
    /// Provides an HTTP-based MCP (Model Context Protocol) client implementation.
    /// Supports JSON-RPC 2.0 over HTTP with Server-Sent Events (SSE) for server-to-client notifications.
    /// </summary>
    public class McpHttpClient : IDisposable
    {
        /// <summary>
        /// Gets the session ID assigned by the server.
        /// This ID is used to correlate requests and receive notifications via SSE.
        /// </summary>
        public string? SessionId { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the client is currently connected to a server.
        /// A client is considered connected if it has a valid session ID.
        /// </summary>
        public bool IsConnected => !String.IsNullOrEmpty(SessionId);

        /// <summary>
        /// Gets a value indicating whether the SSE (Server-Sent Events) connection is active.
        /// </summary>
        public bool IsSseConnected => _IsSseConnected;

        /// <summary>
        /// Gets or sets the request timeout in milliseconds.
        /// Default is 30000 (30 seconds). Minimum is 1000 (1 second).
        /// </summary>
        public int RequestTimeoutMs
        {
            get => _RequestTimeoutMs;
            set
            {
                if (value < 1000) throw new ArgumentOutOfRangeException(nameof(value), "Request timeout must be at least 1000ms");
                _RequestTimeoutMs = value;
            }
        }

        /// <summary>
        /// Occurs when a log message is generated.
        /// </summary>
        public event EventHandler<string>? Log;

        /// <summary>
        /// Occurs when a notification (request without an ID) is received from the server via SSE.
        /// </summary>
        public event EventHandler<JsonRpcRequest>? NotificationReceived;

        private HttpClient? _HttpClient;
        private string? _BaseUrl;
        private string? _RpcUrl;
        private string? _EventsUrl;
        private CancellationTokenSource? _SseTokenSource;
        private Task? _SseTask;
        private bool _IsSseConnected = false;
        private int _RequestTimeoutMs = 30000;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHttpClient"/> class.
        /// </summary>
        public McpHttpClient()
        {
            _HttpClient = new HttpClient();
        }

        /// <summary>
        /// Asynchronously connects to an HTTP MCP server at the specified base URL.
        /// This method will make an initial request to establish a session.
        /// </summary>
        /// <param name="baseUrl">The base URL of the server (e.g., "http://localhost:8080").</param>
        /// <param name="rpcPath">The RPC endpoint path. Default is "/rpc".</param>
        /// <param name="eventsPath">The SSE events endpoint path. Default is "/events".</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the connection was successful; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException">Thrown when baseUrl is null or empty.</exception>
        public async Task<bool> ConnectAsync(string baseUrl, string rpcPath = "/rpc", string eventsPath = "/events", CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));

            try
            {
                Disconnect();

                _BaseUrl = baseUrl.TrimEnd('/');
                _RpcUrl = $"{_BaseUrl}{rpcPath}";
                _EventsUrl = $"{_BaseUrl}{eventsPath}";

                // Make initial ping request to establish session
                await CallAsync<string>("ping", null, _RequestTimeoutMs, token).ConfigureAwait(false);

                LogMessage($"Connected to {baseUrl}");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Connection failed: {ex.Message}");
                SessionId = null;
                return false;
            }
        }

        /// <summary>
        /// Asynchronously starts the Server-Sent Events (SSE) connection to receive notifications from the server.
        /// A session must be established before calling this method.
        /// </summary>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is true if the SSE connection was established; otherwise, false.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no session has been established.</exception>
        public async Task<bool> StartSseAsync(CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(SessionId))
                throw new InvalidOperationException("No session established. Call ConnectAsync first.");

            try
            {
                StopSse();

                _SseTokenSource = new CancellationTokenSource();
                _SseTask = Task.Run(() => SseLoop(_SseTokenSource.Token));

                // Give SSE connection a moment to establish
                await Task.Delay(100, token).ConfigureAwait(false);

                LogMessage("SSE connection started");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to start SSE: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the Server-Sent Events (SSE) connection.
        /// </summary>
        public void StopSse()
        {
            if (_IsSseConnected)
            {
                _IsSseConnected = false;
                _SseTokenSource?.Cancel();
                LogMessage("SSE connection stopped");
            }
        }

        /// <summary>
        /// Asynchronously invokes a remote method and returns the result as the specified type.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the result into.</typeparam>
        /// <param name="method">The name of the method to invoke.</param>
        /// <param name="parameters">The parameters to pass to the method. Can be null.</param>
        /// <param name="timeoutMs">The timeout in milliseconds to wait for a response. Default is the value of RequestTimeoutMs property.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the method result.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the client has not been initialized.</exception>
        /// <exception cref="Exception">Thrown when the remote method returns an error.</exception>
        public async Task<T> CallAsync<T>(string method, object? parameters = null, int timeoutMs = 0, CancellationToken token = default)
        {
            if (_HttpClient == null || String.IsNullOrEmpty(_RpcUrl))
                throw new InvalidOperationException("Client not initialized. Call ConnectAsync first.");

            if (timeoutMs == 0) timeoutMs = _RequestTimeoutMs;

            JsonRpcRequest request = new JsonRpcRequest
            {
                Method = method,
                Params = parameters,
                Id = Guid.NewGuid().ToString()
            };

            string requestJson = JsonSerializer.Serialize(request);
            LogMessage($"Sending request: {requestJson}");

            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(timeoutMs);

                HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, _RpcUrl);
                httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                if (!String.IsNullOrEmpty(SessionId))
                {
                    httpRequest.Headers.Add("X-Session-Id", SessionId);
                }

                HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
                httpResponse.EnsureSuccessStatusCode();

                // Extract session ID from response
                if (httpResponse.Headers.TryGetValues("X-Session-Id", out System.Collections.Generic.IEnumerable<string>? sessionHeaders))
                {
                    foreach (string sessionHeader in sessionHeaders)
                    {
                        SessionId = sessionHeader;
                        break;
                    }
                }

                string responseJson = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                LogMessage($"Received response: {responseJson}");

                JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);
                if (response == null)
                {
                    throw new Exception("Invalid response from server");
                }

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

        /// <summary>
        /// Disconnects from the server and stops the SSE connection if active.
        /// </summary>
        public void Disconnect()
        {
            StopSse();
            SessionId = null;
            LogMessage("Disconnected");
        }

        /// <summary>
        /// Releases all resources used by the <see cref="McpHttpClient"/>.
        /// </summary>
        public void Dispose()
        {
            Disconnect();
            _SseTokenSource?.Dispose();
            _HttpClient?.Dispose();
        }

        private async Task SseLoop(CancellationToken token)
        {
            try
            {
                if (String.IsNullOrEmpty(_EventsUrl) || String.IsNullOrEmpty(SessionId))
                {
                    LogMessage("Cannot start SSE: missing URL or session ID");
                    return;
                }

                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, _EventsUrl);
                request.Headers.Add("X-Session-Id", SessionId);
                request.Headers.Add("Accept", "text/event-stream");

                HttpResponseMessage response = await _HttpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                _IsSseConnected = true;

                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string? line;
                    StringBuilder dataBuilder = new StringBuilder();

                    while (!token.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (line.StartsWith("data: "))
                        {
                            dataBuilder.Append(line.Substring(6));
                        }
                        else if (String.IsNullOrEmpty(line) && dataBuilder.Length > 0)
                        {
                            // End of message
                            string data = dataBuilder.ToString();
                            dataBuilder.Clear();

                            ProcessNotification(data);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    LogMessage($"SSE error: {ex.Message}");
                }
            }
            finally
            {
                _IsSseConnected = false;
            }
        }

        private void ProcessNotification(string data)
        {
            try
            {
                LogMessage($"Received notification: {data}");

                JsonRpcRequest? notification = JsonSerializer.Deserialize<JsonRpcRequest>(data);
                if (notification != null)
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
            catch (Exception ex)
            {
                LogMessage($"Error processing notification: {ex.Message}");
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
    }
}
