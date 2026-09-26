namespace Voltaic.A2A
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// Resolves A2A Agent Cards from the well-known discovery endpoint (<see cref="A2AProtocol.AgentCardPath"/>).
    /// Instances are safe to use from multiple threads concurrently, as long as the supplied
    /// <see cref="HttpClient"/> is (the default one is).
    /// </summary>
    public sealed class A2ACardResolver : IDisposable
    {
        private readonly HttpClient _HttpClient;
        private readonly bool _OwnsHttpClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="A2ACardResolver"/> class.
        /// </summary>
        /// <param name="httpClient">
        /// Optional <see cref="HttpClient"/> to send requests with. When null (the default), the resolver creates its own
        /// client and disposes it in <see cref="Dispose"/>; a supplied client is never disposed by the resolver.
        /// </param>
        public A2ACardResolver(HttpClient? httpClient = null)
        {
            _HttpClient = httpClient ?? new HttpClient();
            _OwnsHttpClient = httpClient == null;
        }

        /// <summary>
        /// Fetches and deserializes the public Agent Card from <paramref name="baseUrl"/> followed by
        /// <see cref="A2AProtocol.AgentCardPath"/>, sending the <see cref="A2AProtocol.VersionHeader"/> header.
        /// </summary>
        /// <param name="baseUrl">The agent's base URL, for example <c>http://localhost:8080</c>. A trailing slash is ignored. Must not be null, empty, or whitespace.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The Agent Card. Never null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseUrl"/> is null, empty, or whitespace.</exception>
        /// <exception cref="A2AProtocolException">Thrown with <see cref="A2AErrorCode.InvalidAgentResponse"/> when the response body is empty or not an Agent Card.</exception>
        /// <exception cref="System.Text.Json.JsonException">Thrown when the response body is not valid JSON.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails or the server returns a non-success status code.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the resolver has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public async Task<AgentCard> GetAgentCardAsync(string baseUrl, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));

            string url = baseUrl.TrimEnd('/') + A2AProtocol.AgentCardPath;
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(A2AProtocol.VersionHeader, A2AProtocol.ProtocolVersion);
            using HttpResponseMessage response = await _HttpClient.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            AgentCard? card = await JsonSerializer.DeserializeAsync<AgentCard>(stream, A2AJson.DefaultOptions, token).ConfigureAwait(false);
            return card ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "Agent Card response was empty or invalid.");
        }

        /// <summary>
        /// Releases the <see cref="HttpClient"/> the resolver created. A client supplied to the constructor is not disposed.
        /// </summary>
        public void Dispose()
        {
            if (_OwnsHttpClient)
            {
                _HttpClient.Dispose();
            }
        }
    }

    /// <summary>
    /// Direct A2A JSON-RPC-over-HTTP client with Server-Sent Events (SSE) streaming support. Every request is a
    /// JSON-RPC 2.0 POST to the endpoint URL and carries the <see cref="A2AProtocol.VersionHeader"/> header.
    /// Instances hold no per-request state and are safe to use from multiple threads concurrently, as long as the
    /// supplied <see cref="HttpClient"/> is (the default one is). To send authentication, configure
    /// <see cref="HttpClient.DefaultRequestHeaders"/> on a client you pass in.
    /// </summary>
    public sealed class A2AClient : IDisposable
    {
        private readonly HttpClient _HttpClient;
        private readonly bool _OwnsHttpClient;
        private readonly string _Url;
        private bool _IsDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="A2AClient"/> class.
        /// </summary>
        /// <param name="endpointUrl">The absolute URL of the agent's JSON-RPC endpoint, for example <c>http://localhost:8080/a2a</c>. Must not be null.</param>
        /// <param name="httpClient">Optional <see cref="HttpClient"/> to send requests with. When null (the default), the client creates its own and disposes it in <see cref="Dispose"/>; a supplied client is never disposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpointUrl"/> is null.</exception>
        /// <exception cref="UriFormatException">Thrown when <paramref name="endpointUrl"/> is not a valid absolute URI.</exception>
        public A2AClient(string endpointUrl, HttpClient? httpClient = null)
            : this(new Uri(endpointUrl ?? throw new ArgumentNullException(nameof(endpointUrl))), httpClient)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="A2AClient"/> class.
        /// </summary>
        /// <param name="endpointUrl">The absolute URI of the agent's JSON-RPC endpoint. Must not be null.</param>
        /// <param name="httpClient">Optional <see cref="HttpClient"/> to send requests with. When null (the default), the client creates its own and disposes it in <see cref="Dispose"/>; a supplied client is never disposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpointUrl"/> is null.</exception>
        public A2AClient(Uri endpointUrl, HttpClient? httpClient = null)
        {
            if (endpointUrl == null) throw new ArgumentNullException(nameof(endpointUrl));

            _Url = endpointUrl.ToString();
            _HttpClient = httpClient ?? new HttpClient();
            _OwnsHttpClient = httpClient == null;
        }

        /// <summary>
        /// Sends a message to the agent (<c>SendMessage</c>) and waits for the result.
        /// </summary>
        /// <param name="request">The message and optional configuration to send.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The agent's response: either a task or a direct message.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<SendMessageResponse>(A2AProtocol.SendMessage, request, token);
        }

        /// <summary>
        /// Sends a message to the agent (<c>SendStreamingMessage</c>) and streams task, message, status-update, and artifact-update events as they arrive.
        /// </summary>
        /// <remarks>
        /// The request is sent when enumeration starts. Each Server-Sent Event carries one JSON-RPC response; an error
        /// event throws <see cref="A2AProtocolException"/> from the enumerator. Enumeration ends when the server closes the
        /// stream or <paramref name="token"/> is cancelled.
        /// </remarks>
        /// <param name="request">The message and optional configuration to send.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of stream events.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return SendStreamingJsonRpcRequestAsync<StreamResponse>(A2AProtocol.SendStreamingMessage, request, token);
        }

        /// <summary>
        /// Gets a task by ID (<c>GetTask</c>).
        /// </summary>
        /// <param name="request">The task ID and optional history length.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The task. Throws <see cref="A2AProtocolException"/> with <see cref="A2AErrorCode.TaskNotFound"/> when the task does not exist.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<AgentTask>(A2AProtocol.GetTask, request, token);
        }

        /// <summary>
        /// Lists tasks (<c>ListTasks</c>), optionally filtered and paged.
        /// </summary>
        /// <param name="request">The filters and paging options.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A page of tasks and the token for the next page.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<ListTasksResponse>(A2AProtocol.ListTasks, request, token);
        }

        /// <summary>
        /// Requests cancellation of a task (<c>CancelTask</c>).
        /// </summary>
        /// <param name="request">The task ID and optional metadata.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The task in its updated state.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<AgentTask>(A2AProtocol.CancelTask, request, token);
        }

        /// <summary>
        /// Subscribes to updates for an existing task (<c>SubscribeToTask</c>) and streams its events.
        /// </summary>
        /// <remarks>
        /// The request is sent when enumeration starts. Each Server-Sent Event carries one JSON-RPC response; an error
        /// event throws <see cref="A2AProtocolException"/> from the enumerator. Enumeration ends when the server closes the
        /// stream or <paramref name="token"/> is cancelled.
        /// </remarks>
        /// <param name="request">The ID of the task to subscribe to.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of stream events.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken token = default)
        {
            return SendStreamingJsonRpcRequestAsync<StreamResponse>(A2AProtocol.SubscribeToTask, request, token);
        }

        /// <summary>
        /// Creates a push notification configuration for a task (<c>CreateTaskPushNotificationConfig</c>).
        /// </summary>
        /// <param name="request">The task ID and the configuration to create.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The stored configuration, including its assigned ID.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<TaskPushNotificationConfig>(A2AProtocol.CreateTaskPushNotificationConfig, request, token);
        }

        /// <summary>
        /// Gets a task's push notification configuration (<c>GetTaskPushNotificationConfig</c>).
        /// </summary>
        /// <param name="request">The task ID and configuration ID.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The configuration.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<TaskPushNotificationConfig>(A2AProtocol.GetTaskPushNotificationConfig, request, token);
        }

        /// <summary>
        /// Lists a task's push notification configurations (<c>ListTaskPushNotificationConfig</c>).
        /// </summary>
        /// <param name="request">The task ID (and optional tenant).</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The configurations.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<ListTaskPushNotificationConfigResponse>(A2AProtocol.ListTaskPushNotificationConfig, request, token);
        }

        /// <summary>
        /// Deletes a task's push notification configuration (<c>DeleteTaskPushNotificationConfig</c>).
        /// </summary>
        /// <param name="request">The task ID and configuration ID.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that completes when the agent confirms the deletion.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<object>(A2AProtocol.DeleteTaskPushNotificationConfig, request, token);
        }

        /// <summary>
        /// Gets the authenticated extended Agent Card over JSON-RPC (<c>GetExtendedAgentCard</c>).
        /// </summary>
        /// <param name="request">The request (optional tenant). May carry no values.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The extended Agent Card. Agents that do not offer one return an error.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns a JSON-RPC error (mapped to <see cref="A2AErrorCode"/>; unknown codes map to <see cref="A2AErrorCode.InternalError"/>) or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<AgentCard>(A2AProtocol.GetExtendedAgentCard, request, token);
        }

        /// <summary>
        /// Gets the authenticated extended Agent Card with a REST GET to <paramref name="baseUrl"/> followed by
        /// <see cref="A2AProtocol.ExtendedAgentCardPath"/>.
        /// </summary>
        /// <param name="baseUrl">The agent's base URL. A trailing slash is ignored. Must not be null, empty, or whitespace.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The extended Agent Card. Never null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseUrl"/> is null, empty, or whitespace.</exception>
        /// <exception cref="A2AProtocolException">Thrown with <see cref="A2AErrorCode.InvalidAgentResponse"/> when the response body is empty or not an Agent Card.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or when the server returns a non-success status code without a JSON-RPC error body (a JSON-RPC error body is raised as <see cref="A2AProtocolException"/> whatever the status).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public async Task<AgentCard> GetExtendedAgentCardRestAsync(string baseUrl, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + A2AProtocol.ExtendedAgentCardPath);
            request.Headers.TryAddWithoutValidation(A2AProtocol.VersionHeader, A2AProtocol.ProtocolVersion);
            using HttpResponseMessage response = await _HttpClient.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<AgentCard>(stream, A2AJson.DefaultOptions, token).ConfigureAwait(false)
                ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "Extended Agent Card response was empty or invalid.");
        }

        /// <summary>
        /// Releases the <see cref="HttpClient"/> the client created. A client supplied to the constructor is not disposed.
        /// Calling this more than once has no effect.
        /// </summary>
        public void Dispose()
        {
            if (_IsDisposed)
            {
                return;
            }

            _IsDisposed = true;
            if (_OwnsHttpClient)
            {
                _HttpClient.Dispose();
            }
        }

        private async Task<TResult> SendJsonRpcRequestAsync<TResult>(string method, object? parameters, CancellationToken token)
        {
            JsonRpcRequest request = CreateRequest(method, parameters);
            string json = JsonSerializer.Serialize(request, A2AJson.DefaultOptions);

            using HttpRequestMessage httpRequest = CreatePostRequest(json);
            using HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, token).ConfigureAwait(false);

            // Read the JSON-RPC error body even when the status is not 2xx: some servers (including Voltaic before
            // 2.1.2) send JSON-RPC errors with 4xx statuses, and the error code is what callers need.
            string responseJson = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                ThrowIfJsonRpcErrorBody(responseJson);
                httpResponse.EnsureSuccessStatusCode();
            }

            JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson, A2AJson.DefaultOptions);
            if (response == null)
            {
                throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "Invalid JSON-RPC response.");
            }

            ThrowIfError(response);
            if (typeof(TResult) == typeof(object))
            {
                return default!;
            }

            return DeserializeResult<TResult>(response.Result);
        }

        private async IAsyncEnumerable<TResult> SendStreamingJsonRpcRequestAsync<TResult>(string method, object? parameters, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            JsonRpcRequest request = CreateRequest(method, parameters);
            string json = JsonSerializer.Serialize(request, A2AJson.DefaultOptions);

            using HttpRequestMessage httpRequest = CreatePostRequest(json);
            httpRequest.Headers.Accept.ParseAdd("text/event-stream");

            using HttpResponseMessage httpResponse = await _HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                ThrowIfJsonRpcErrorBody(await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                httpResponse.EnsureSuccessStatusCode();
            }

            using Stream stream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            StringBuilder data = new StringBuilder();

            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    yield break;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    string value = line.Length > 5 && line[5] == ' ' ? line.Substring(6) : line.Substring(5);
                    data.Append(value);
                    continue;
                }

                if (line.Length == 0 && data.Length > 0)
                {
                    JsonRpcResponse? response = JsonSerializer.Deserialize<JsonRpcResponse>(data.ToString(), A2AJson.DefaultOptions);
                    data.Clear();
                    if (response == null)
                    {
                        throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "Invalid JSON-RPC SSE event.");
                    }

                    ThrowIfError(response);
                    yield return DeserializeResult<TResult>(response.Result);
                }
            }
        }

        private static JsonRpcRequest CreateRequest(string method, object? parameters)
        {
            return new JsonRpcRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Method = method,
                Params = parameters
            };
        }

        private HttpRequestMessage CreatePostRequest(string json)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _Url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation(A2AProtocol.VersionHeader, A2AProtocol.ProtocolVersion);
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        }

        private static TResult DeserializeResult<TResult>(object? result)
        {
            if (result == null)
            {
                return default!;
            }

            string json = JsonSerializer.Serialize(result, A2AJson.DefaultOptions);
            return JsonSerializer.Deserialize<TResult>(json, A2AJson.DefaultOptions)
                ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "JSON-RPC result could not be deserialized.");
        }

        private static void ThrowIfJsonRpcErrorBody(string body)
        {
            if (String.IsNullOrWhiteSpace(body)) return;

            JsonRpcResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<JsonRpcResponse>(body, A2AJson.DefaultOptions);
            }
            catch (JsonException)
            {
                return;
            }

            if (response?.Error != null)
            {
                ThrowIfError(response);
            }
        }

        private static void ThrowIfError(JsonRpcResponse response)
        {
            if (response.Error == null)
            {
                return;
            }

            A2AErrorCode code = Enum.IsDefined(typeof(A2AErrorCode), response.Error.Code)
                ? (A2AErrorCode)response.Error.Code
                : A2AErrorCode.InternalError;
            throw new A2AProtocolException(code, response.Error.Message, response.Error.Data);
        }
    }

    /// <summary>
    /// Direct A2A HTTP+JSON (REST) client with Server-Sent Events (SSE) streaming support. Requests use the A2A
    /// REST routes under the base URL (for example <c>POST /message:send</c> and <c>GET /tasks/{id}</c>) and carry
    /// the <see cref="A2AProtocol.VersionHeader"/> header. Instances hold no per-request state and are safe to use
    /// from multiple threads concurrently, as long as the supplied <see cref="HttpClient"/> is (the default one is).
    /// To send authentication, configure <see cref="HttpClient.DefaultRequestHeaders"/> on a client you pass in.
    /// </summary>
    public sealed class A2AHttpJsonClient : IDisposable
    {
        private readonly HttpClient _HttpClient;
        private readonly bool _OwnsHttpClient;
        private readonly string _BaseUrl;
        private bool _IsDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="A2AHttpJsonClient"/> class.
        /// </summary>
        /// <param name="baseUrl">The absolute base URL of the agent's HTTP+JSON binding, for example <c>http://localhost:8080</c>. A trailing slash is ignored. Must not be null.</param>
        /// <param name="httpClient">Optional <see cref="HttpClient"/> to send requests with. When null (the default), the client creates its own and disposes it in <see cref="Dispose"/>; a supplied client is never disposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseUrl"/> is null.</exception>
        /// <exception cref="UriFormatException">Thrown when <paramref name="baseUrl"/> is not a valid absolute URI.</exception>
        public A2AHttpJsonClient(string baseUrl, HttpClient? httpClient = null)
            : this(new Uri(baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))), httpClient)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="A2AHttpJsonClient"/> class.
        /// </summary>
        /// <param name="baseUrl">The absolute base URI of the agent's HTTP+JSON binding. A trailing slash is ignored. Must not be null.</param>
        /// <param name="httpClient">Optional <see cref="HttpClient"/> to send requests with. When null (the default), the client creates its own and disposes it in <see cref="Dispose"/>; a supplied client is never disposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseUrl"/> is null.</exception>
        public A2AHttpJsonClient(Uri baseUrl, HttpClient? httpClient = null)
        {
            if (baseUrl == null) throw new ArgumentNullException(nameof(baseUrl));

            _BaseUrl = baseUrl.ToString().TrimEnd('/');
            _HttpClient = httpClient ?? new HttpClient();
            _OwnsHttpClient = httpClient == null;
        }

        /// <summary>
        /// Sends a message to the agent with <c>POST /message:send</c> and waits for the result.
        /// </summary>
        /// <param name="request">The message and optional configuration to send.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The agent's response: either a task or a direct message.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return PostJsonAsync<SendMessageRequest, SendMessageResponse>("/message:send", request, token);
        }

        /// <summary>
        /// Sends a message to the agent with <c>POST /message:stream</c> and streams the resulting events.
        /// </summary>
        /// <remarks>
        /// The request is sent when enumeration starts. Each Server-Sent Event carries one <see cref="StreamResponse"/>.
        /// Enumeration ends when the server closes the stream or <paramref name="token"/> is cancelled.
        /// </remarks>
        /// <param name="request">The message and optional configuration to send.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of stream events.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return PostStreamingAsync("/message:stream", request, token);
        }

        /// <summary>
        /// Gets a task with <c>GET /tasks/{id}</c>, passing <c>historyLength</c> when set.
        /// </summary>
        /// <param name="request">The task ID and optional history length. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The task.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string query = BuildQuery(("historyLength", request.HistoryLength?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return GetJsonAsync<AgentTask>($"/tasks/{Uri.EscapeDataString(request.Id)}{query}", token);
        }

        /// <summary>
        /// Lists tasks with <c>GET /tasks</c>, passing the set filters and paging options as query parameters.
        /// </summary>
        /// <param name="request">The filters and paging options. Null lists tasks without filters.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A page of tasks and the token for the next page.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            request ??= new ListTasksRequest();
            string query = BuildQuery(
                ("contextId", request.ContextId),
                ("status", SerializeTaskState(request.Status)),
                ("pageSize", request.PageSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("pageToken", request.PageToken),
                ("historyLength", request.HistoryLength?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("statusTimestampAfter", request.StatusTimestampAfter?.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                ("includeArtifacts", request.IncludeArtifacts?.ToString().ToLowerInvariant()));
            return GetJsonAsync<ListTasksResponse>($"/tasks{query}", token);
        }

        /// <summary>
        /// Requests cancellation of a task with <c>POST /tasks/{id}:cancel</c>.
        /// </summary>
        /// <param name="request">The task ID and optional metadata. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The task in its updated state.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return PostJsonAsync<object, AgentTask>($"/tasks/{Uri.EscapeDataString(request.Id)}:cancel", new { metadata = request.Metadata }, token);
        }

        /// <summary>
        /// Subscribes to updates for an existing task with <c>POST /tasks/{id}:subscribe</c> and streams its events.
        /// </summary>
        /// <remarks>
        /// The request is sent when enumeration starts. Each Server-Sent Event carries one <see cref="StreamResponse"/>.
        /// Enumeration ends when the server closes the stream or <paramref name="token"/> is cancelled.
        /// </remarks>
        /// <param name="request">The ID of the task to subscribe to. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of stream events.</returns>
        /// <exception cref="ArgumentNullException">Thrown immediately (not on enumeration) when <paramref name="request"/> is null.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return PostStreamingAsync($"/tasks/{Uri.EscapeDataString(request.Id)}:subscribe", new { }, token);
        }

        /// <summary>
        /// Creates a push notification configuration with <c>POST /tasks/{taskId}/pushNotificationConfigs</c>.
        /// </summary>
        /// <param name="request">The task ID and the configuration to create. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The stored configuration, including its assigned ID.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return PostJsonAsync<PushNotificationConfig, TaskPushNotificationConfig>(
                $"/tasks/{Uri.EscapeDataString(request.TaskId)}/pushNotificationConfigs",
                request.Config,
                token);
        }

        /// <summary>
        /// Gets a push notification configuration with <c>GET /tasks/{taskId}/pushNotificationConfigs/{configId}</c>.
        /// </summary>
        /// <param name="request">The task ID and configuration ID. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The configuration.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return GetJsonAsync<TaskPushNotificationConfig>(
                $"/tasks/{Uri.EscapeDataString(request.TaskId)}/pushNotificationConfigs/{Uri.EscapeDataString(request.ConfigId)}",
                token);
        }

        /// <summary>
        /// Lists a task's push notification configurations with <c>GET /tasks/{taskId}/pushNotificationConfigs</c>.
        /// </summary>
        /// <param name="request">The task ID. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The configurations.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return GetJsonAsync<ListTaskPushNotificationConfigResponse>(
                $"/tasks/{Uri.EscapeDataString(request.TaskId)}/pushNotificationConfigs",
                token);
        }

        /// <summary>
        /// Deletes a push notification configuration with <c>DELETE /tasks/{taskId}/pushNotificationConfigs/{configId}</c>.
        /// </summary>
        /// <param name="request">The task ID and configuration ID. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that completes when the agent confirms the deletion.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            using HttpRequestMessage httpRequest = CreateRequest(HttpMethod.Delete, $"/tasks/{Uri.EscapeDataString(request.TaskId)}/pushNotificationConfigs/{Uri.EscapeDataString(request.ConfigId)}");
            using HttpResponseMessage response = await _HttpClient.SendAsync(httpRequest, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the authenticated extended Agent Card with <c>GET</c> <see cref="A2AProtocol.ExtendedAgentCardPath"/>.
        /// </summary>
        /// <param name="request">The request. Its tenant is not used by this binding, and it may be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The extended Agent Card.</returns>
        /// <exception cref="A2AProtocolException">Thrown when the agent returns an error body carrying a JSON-RPC error object, or an empty or malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request fails, or the server returns a non-success status code without a recognizable error body.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken token = default)
        {
            return GetJsonAsync<AgentCard>(A2AProtocol.ExtendedAgentCardPath, token);
        }

        /// <summary>
        /// Releases the <see cref="HttpClient"/> the client created. A client supplied to the constructor is not disposed.
        /// Calling this more than once has no effect.
        /// </summary>
        public void Dispose()
        {
            if (_IsDisposed)
            {
                return;
            }

            _IsDisposed = true;
            if (_OwnsHttpClient)
            {
                _HttpClient.Dispose();
            }
        }

        private async Task<TResult> GetJsonAsync<TResult>(string path, CancellationToken token)
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
            using HttpResponseMessage response = await _HttpClient.SendAsync(request, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<TResult>(stream, A2AJson.DefaultOptions, token).ConfigureAwait(false)
                ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "REST response was empty or invalid.");
        }

        private async Task<TResult> PostJsonAsync<TBody, TResult>(string path, TBody body, CancellationToken token)
        {
            string json = JsonSerializer.Serialize(body, A2AJson.DefaultOptions);
            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _HttpClient.SendAsync(request, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<TResult>(stream, A2AJson.DefaultOptions, token).ConfigureAwait(false)
                ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "REST response was empty or invalid.");
        }

        private async IAsyncEnumerable<StreamResponse> PostStreamingAsync<TBody>(string path, TBody body, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            string json = JsonSerializer.Serialize(body, A2AJson.DefaultOptions);
            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");

            using HttpResponseMessage response = await _HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);

            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            StringBuilder data = new StringBuilder();

            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    yield break;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    string value = line.Length > 5 && line[5] == ' ' ? line.Substring(6) : line.Substring(5);
                    data.Append(value);
                    continue;
                }

                if (line.Length == 0 && data.Length > 0)
                {
                    StreamResponse? item = JsonSerializer.Deserialize<StreamResponse>(data.ToString(), A2AJson.DefaultOptions);
                    data.Clear();
                    yield return item ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "Invalid REST SSE event.");
                }
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            HttpRequestMessage request = new HttpRequestMessage(method, _BaseUrl + path);
            request.Headers.TryAddWithoutValidation(A2AProtocol.VersionHeader, A2AProtocol.ProtocolVersion);
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // HTTP+JSON errors are google.rpc.Status objects whose ErrorInfo reason names the A2A error (A2A v1.0
            // section 11.6); older Voltaic servers sent a JSON-RPC-style error body. Both become A2AProtocolException.
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            A2AProtocolException? protocolError = A2ARestErrors.Parse(body, (int)response.StatusCode);
            if (protocolError != null)
            {
                throw protocolError;
            }

            throw new HttpRequestException($"A2A REST request failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
        }

        private static string BuildQuery(params (string Name, string? Value)[] values)
        {
            List<string> parts = new List<string>();
            foreach ((string name, string? value) in values)
            {
                if (!String.IsNullOrEmpty(value))
                {
                    parts.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
                }
            }

            return parts.Count == 0 ? string.Empty : "?" + String.Join("&", parts);
        }

        private static string? SerializeTaskState(TaskState? state)
        {
            if (state == null)
            {
                return null;
            }

            string json = JsonSerializer.Serialize(state.Value, A2AJson.DefaultOptions);
            return json.Trim('"');
        }
    }
}
