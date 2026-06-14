#pragma warning disable CS1591
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
    /// Resolves A2A Agent Cards from the well-known discovery endpoint.
    /// </summary>
    public sealed class A2ACardResolver : IDisposable
    {
        private readonly HttpClient _HttpClient;
        private readonly bool _OwnsHttpClient;

        public A2ACardResolver(HttpClient? httpClient = null)
        {
            _HttpClient = httpClient ?? new HttpClient();
            _OwnsHttpClient = httpClient == null;
        }

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

        public void Dispose()
        {
            if (_OwnsHttpClient)
            {
                _HttpClient.Dispose();
            }
        }
    }

    /// <summary>
    /// Direct A2A JSON-RPC-over-HTTP client with SSE streaming support.
    /// </summary>
    public sealed class A2AClient : IDisposable
    {
        private readonly HttpClient _HttpClient;
        private readonly bool _OwnsHttpClient;
        private readonly string _Url;
        private bool _IsDisposed;

        public A2AClient(string endpointUrl, HttpClient? httpClient = null)
            : this(new Uri(endpointUrl ?? throw new ArgumentNullException(nameof(endpointUrl))), httpClient)
        {
        }

        public A2AClient(Uri endpointUrl, HttpClient? httpClient = null)
        {
            if (endpointUrl == null) throw new ArgumentNullException(nameof(endpointUrl));

            _Url = endpointUrl.ToString();
            _HttpClient = httpClient ?? new HttpClient();
            _OwnsHttpClient = httpClient == null;
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<SendMessageResponse>(A2AProtocol.SendMessage, request, token);
        }

        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return SendStreamingJsonRpcRequestAsync<StreamResponse>(A2AProtocol.SendStreamingMessage, request, token);
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<AgentTask>(A2AProtocol.GetTask, request, token);
        }

        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<ListTasksResponse>(A2AProtocol.ListTasks, request, token);
        }

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<AgentTask>(A2AProtocol.CancelTask, request, token);
        }

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken token = default)
        {
            return SendStreamingJsonRpcRequestAsync<StreamResponse>(A2AProtocol.SubscribeToTask, request, token);
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<TaskPushNotificationConfig>(A2AProtocol.CreateTaskPushNotificationConfig, request, token);
        }

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<TaskPushNotificationConfig>(A2AProtocol.GetTaskPushNotificationConfig, request, token);
        }

        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<ListTaskPushNotificationConfigResponse>(A2AProtocol.ListTaskPushNotificationConfig, request, token);
        }

        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<object>(A2AProtocol.DeleteTaskPushNotificationConfig, request, token);
        }

        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken token = default)
        {
            return SendJsonRpcRequestAsync<AgentCard>(A2AProtocol.GetExtendedAgentCard, request, token);
        }

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
            httpResponse.EnsureSuccessStatusCode();

            string responseJson = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
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
            httpResponse.EnsureSuccessStatusCode();

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

            if (result is JsonElement element)
            {
                return JsonSerializer.Deserialize<TResult>(element.GetRawText(), A2AJson.DefaultOptions)
                    ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "JSON-RPC result could not be deserialized.");
            }

            string json = JsonSerializer.Serialize(result, A2AJson.DefaultOptions);
            return JsonSerializer.Deserialize<TResult>(json, A2AJson.DefaultOptions)
                ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "JSON-RPC result could not be deserialized.");
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
    /// Direct A2A HTTP+JSON client with SSE streaming support.
    /// </summary>
    public sealed class A2AHttpJsonClient : IDisposable
    {
        private readonly HttpClient _HttpClient;
        private readonly bool _OwnsHttpClient;
        private readonly string _BaseUrl;
        private bool _IsDisposed;

        public A2AHttpJsonClient(string baseUrl, HttpClient? httpClient = null)
            : this(new Uri(baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))), httpClient)
        {
        }

        public A2AHttpJsonClient(Uri baseUrl, HttpClient? httpClient = null)
        {
            if (baseUrl == null) throw new ArgumentNullException(nameof(baseUrl));

            _BaseUrl = baseUrl.ToString().TrimEnd('/');
            _HttpClient = httpClient ?? new HttpClient();
            _OwnsHttpClient = httpClient == null;
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return PostJsonAsync<SendMessageRequest, SendMessageResponse>("/message:send", request, token);
        }

        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return PostStreamingAsync("/message:stream", request, token);
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string query = BuildQuery(("historyLength", request.HistoryLength?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return GetJsonAsync<AgentTask>($"/tasks/{Uri.EscapeDataString(request.Id)}{query}", token);
        }

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

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return PostJsonAsync<object, AgentTask>($"/tasks/{Uri.EscapeDataString(request.Id)}:cancel", new { metadata = request.Metadata }, token);
        }

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return PostStreamingAsync($"/tasks/{Uri.EscapeDataString(request.Id)}:subscribe", new { }, token);
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return PostJsonAsync<PushNotificationConfig, TaskPushNotificationConfig>(
                $"/tasks/{Uri.EscapeDataString(request.TaskId)}/pushNotificationConfigs",
                request.Config,
                token);
        }

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return GetJsonAsync<TaskPushNotificationConfig>(
                $"/tasks/{Uri.EscapeDataString(request.TaskId)}/pushNotificationConfigs/{Uri.EscapeDataString(request.ConfigId)}",
                token);
        }

        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return GetJsonAsync<ListTaskPushNotificationConfigResponse>(
                $"/tasks/{Uri.EscapeDataString(request.TaskId)}/pushNotificationConfigs",
                token);
        }

        public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            using HttpRequestMessage httpRequest = CreateRequest(HttpMethod.Delete, $"/tasks/{Uri.EscapeDataString(request.TaskId)}/pushNotificationConfigs/{Uri.EscapeDataString(request.ConfigId)}");
            using HttpResponseMessage response = await _HttpClient.SendAsync(httpRequest, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        }

        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken token = default)
        {
            return GetJsonAsync<AgentCard>(A2AProtocol.ExtendedAgentCardPath, token);
        }

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

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(body))
            {
                try
                {
                    JsonRpcResponse? rpc = JsonSerializer.Deserialize<JsonRpcResponse>(body, A2AJson.DefaultOptions);
                    if (rpc?.Error != null)
                    {
                        A2AErrorCode code = Enum.IsDefined(typeof(A2AErrorCode), rpc.Error.Code)
                            ? (A2AErrorCode)rpc.Error.Code
                            : A2AErrorCode.InternalError;
                        throw new A2AProtocolException(code, rpc.Error.Message, rpc.Error.Data);
                    }
                }
                catch (JsonException)
                {
                }
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
