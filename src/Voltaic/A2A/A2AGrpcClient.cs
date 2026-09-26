namespace Voltaic.A2A
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Google.Protobuf;
    using Google.Protobuf.WellKnownTypes;
    using GrpcWire = Voltaic.A2A.Grpc;

    /// <summary>
    /// Direct A2A gRPC client implemented with <see cref="HttpClient"/> over HTTP/2 and gRPC length-prefixed
    /// protobuf framing, targeting the <c>lf.a2a.v1.A2AService</c> service. No Grpc.Net.Client dependency is used.
    /// Instances hold no per-call state and are safe to use from multiple threads concurrently, as long as the
    /// supplied <see cref="HttpClient"/> is (the default one is). To send authentication, configure
    /// <see cref="HttpClient.DefaultRequestHeaders"/> on a client you pass in.
    /// </summary>
    public sealed class A2AGrpcClient : IDisposable
    {
        private readonly Uri _BaseUrl;
        private readonly HttpClient _HttpClient;
        private readonly bool _OwnsHttpClient;
        private bool _IsDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="A2AGrpcClient"/> class.
        /// </summary>
        /// <param name="endpointUrl">
        /// The gRPC endpoint, for example <c>http://localhost:8081</c> or <c>localhost:8081</c>. Surrounding whitespace is
        /// trimmed, <c>http://</c> is assumed when no scheme is given, and a trailing slash is added. Must not be null,
        /// empty, or whitespace.
        /// </param>
        /// <param name="httpClient">Optional <see cref="HttpClient"/> to send requests with. When null (the default), the client creates its own and disposes it in <see cref="Dispose"/>; a supplied client is never disposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpointUrl"/> is null, empty, or whitespace.</exception>
        /// <exception cref="UriFormatException">Thrown when <paramref name="endpointUrl"/> is not a valid URI.</exception>
        public A2AGrpcClient(string endpointUrl, HttpClient? httpClient = null)
            : this(NormalizeEndpointUrl(endpointUrl), httpClient)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="A2AGrpcClient"/> class.
        /// </summary>
        /// <param name="endpointUrl">The absolute base URI of the gRPC endpoint. gRPC method paths are resolved relative to it, so it should end with a slash. Must not be null.</param>
        /// <param name="httpClient">Optional <see cref="HttpClient"/> to send requests with. When null (the default), the client creates its own and disposes it in <see cref="Dispose"/>; a supplied client is never disposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpointUrl"/> is null.</exception>
        public A2AGrpcClient(Uri endpointUrl, HttpClient? httpClient = null)
        {
            _BaseUrl = endpointUrl ?? throw new ArgumentNullException(nameof(endpointUrl));
            _HttpClient = httpClient ?? new HttpClient();
            _OwnsHttpClient = httpClient == null;
        }

        /// <summary>
        /// Sends a message to the agent (<c>SendMessage</c>) and waits for the result.
        /// </summary>
        /// <param name="request">The message and optional configuration to send. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The agent's response: either a task or a direct message.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.SendMessagePath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.SendMessageResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        /// <summary>
        /// Sends a message to the agent (<c>SendStreamingMessage</c>, server streaming) and streams the resulting events.
        /// </summary>
        /// <remarks>
        /// The call starts when enumeration starts. Each gRPC message frame is yielded as it arrives; the final
        /// <c>grpc-status</c> is checked after the last frame, so a server-side failure throws
        /// <see cref="A2AProtocolException"/> from the enumerator after any frames already received.
        /// </remarks>
        /// <param name="request">The message and optional configuration to send. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of stream events.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return StreamingAsync(
                A2AGrpcWire.SendStreamingMessagePath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.StreamResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        /// <summary>
        /// Gets a task by ID (<c>GetTask</c>).
        /// </summary>
        /// <param name="request">The task ID and optional history length. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The task.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.GetTaskPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.Task.Parser,
                response => A2AGrpcWire.FromGrpc(response),
                token);
        }

        /// <summary>
        /// Lists tasks (<c>ListTasks</c>), optionally filtered and paged.
        /// </summary>
        /// <param name="request">The filters and paging options. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A page of tasks and the token for the next page.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.ListTasksPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.ListTasksResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        /// <summary>
        /// Requests cancellation of a task (<c>CancelTask</c>).
        /// </summary>
        /// <param name="request">The task ID and optional metadata. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The task in its updated state.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.CancelTaskPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.Task.Parser,
                response => A2AGrpcWire.FromGrpc(response),
                token);
        }

        /// <summary>
        /// Subscribes to updates for an existing task (<c>SubscribeToTask</c>, server streaming) and streams its events.
        /// </summary>
        /// <remarks>
        /// The call starts when enumeration starts. Each gRPC message frame is yielded as it arrives; the final
        /// <c>grpc-status</c> is checked after the last frame, so a server-side failure throws
        /// <see cref="A2AProtocolException"/> from the enumerator after any frames already received.
        /// </remarks>
        /// <param name="request">The ID of the task to subscribe to. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of stream events.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken token = default)
        {
            return StreamingAsync(
                A2AGrpcWire.SubscribeToTaskPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.StreamResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        /// <summary>
        /// Creates a push notification configuration for a task (<c>CreateTaskPushNotificationConfig</c>).
        /// </summary>
        /// <param name="request">The task ID and the configuration to create. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The stored configuration, including its assigned ID.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.CreateTaskPushNotificationConfigPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.TaskPushNotificationConfig.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        /// <summary>
        /// Gets a task's push notification configuration (<c>GetTaskPushNotificationConfig</c>).
        /// </summary>
        /// <param name="request">The task ID and configuration ID. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The configuration.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.GetTaskPushNotificationConfigPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.TaskPushNotificationConfig.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        /// <summary>
        /// Lists a task's push notification configurations (<c>ListTaskPushNotificationConfigs</c>).
        /// </summary>
        /// <param name="request">The task ID (and optional tenant). Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The configurations.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.ListTaskPushNotificationConfigsPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.ListTaskPushNotificationConfigsResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        /// <summary>
        /// Deletes a task's push notification configuration (<c>DeleteTaskPushNotificationConfig</c>).
        /// </summary>
        /// <param name="request">The task ID and configuration ID. Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that completes when the agent confirms the deletion.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            await UnaryAsync(
                A2AGrpcWire.DeleteTaskPushNotificationConfigPath,
                A2AGrpcWire.ToGrpc(request),
                Empty.Parser,
                _ => true,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the authenticated extended Agent Card (<c>GetExtendedAgentCard</c>).
        /// </summary>
        /// <param name="request">The request (optional tenant). Must not be null.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The extended Agent Card. Agents that do not offer one return an error.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null, including for streaming calls, where it is thrown immediately rather than on enumeration.</exception>
        /// <exception cref="A2AProtocolException">Thrown when the server returns a non-zero <c>grpc-status</c> (mapped to <see cref="A2AErrorCode"/>) or a malformed response.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP/2 request fails or the server returns a non-success HTTP status code.</exception>
        /// <exception cref="InvalidProtocolBufferException">Thrown when a response frame is not a valid protobuf message.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the client has been disposed and it owns its <see cref="HttpClient"/>.</exception>
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.GetExtendedAgentCardPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.AgentCard.Parser,
                A2AGrpcWire.FromGrpc,
                token);
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

        private async Task<TResult> UnaryAsync<TWireRequest, TWireResponse, TResult>(
            string path,
            TWireRequest request,
            MessageParser<TWireResponse> parser,
            Func<TWireResponse, TResult> map,
            CancellationToken token)
            where TWireRequest : IMessage
            where TWireResponse : IMessage<TWireResponse>
        {
            using HttpRequestMessage httpRequest = CreateRequest(path, request);
            using HttpResponseMessage response = await _HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            byte[] body = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            A2AGrpcWire.EnsureGrpcSuccess(response);

            List<byte[]> frames = A2AGrpcWire.DecodePayloads(body);
            if (frames.Count != 1)
            {
                throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, $"Expected one gRPC response frame, found {frames.Count}.");
            }

            TWireResponse wireResponse = parser.ParseFrom(frames[0]);
            return map(wireResponse);
        }

        private async IAsyncEnumerable<TResult> StreamingAsync<TWireRequest, TWireResponse, TResult>(
            string path,
            TWireRequest request,
            MessageParser<TWireResponse> parser,
            Func<TWireResponse, TResult> map,
            [EnumeratorCancellation] CancellationToken token)
            where TWireRequest : IMessage
            where TWireResponse : IMessage<TWireResponse>
        {
            using HttpRequestMessage httpRequest = CreateRequest(path, request);
            using HttpResponseMessage response = await _HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await foreach (byte[] payload in A2AGrpcWire.ReadPayloadsAsync(stream, token).ConfigureAwait(false))
            {
                yield return map(parser.ParseFrom(payload));
            }

            A2AGrpcWire.EnsureGrpcSuccess(response);
        }

        private HttpRequestMessage CreateRequest(string path, IMessage message)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, new Uri(_BaseUrl, path));
            request.Content = new ByteArrayContent(A2AGrpcWire.EncodeMessage(message));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(A2AGrpcWire.ContentType);
            A2AGrpcWire.ConfigureGrpcRequest(request);
            return request;
        }

        private static Uri NormalizeEndpointUrl(string endpointUrl)
        {
            if (String.IsNullOrWhiteSpace(endpointUrl)) throw new ArgumentNullException(nameof(endpointUrl));

            string value = endpointUrl.Trim();
            if (!value.Contains("://", StringComparison.Ordinal))
            {
                value = "http://" + value;
            }

            if (!value.EndsWith("/", StringComparison.Ordinal))
            {
                value += "/";
            }

            return new Uri(value, UriKind.Absolute);
        }
    }
}
