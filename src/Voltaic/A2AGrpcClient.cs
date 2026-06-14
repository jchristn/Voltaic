#pragma warning disable CS1591
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
    /// Direct A2A gRPC client implemented with HttpClient and protobuf framing.
    /// </summary>
    public sealed class A2AGrpcClient : IDisposable
    {
        private readonly Uri _BaseUrl;
        private readonly HttpClient _HttpClient;
        private readonly bool _OwnsHttpClient;
        private bool _IsDisposed;

        public A2AGrpcClient(string endpointUrl, HttpClient? httpClient = null)
            : this(NormalizeEndpointUrl(endpointUrl), httpClient)
        {
        }

        public A2AGrpcClient(Uri endpointUrl, HttpClient? httpClient = null)
        {
            _BaseUrl = endpointUrl ?? throw new ArgumentNullException(nameof(endpointUrl));
            _HttpClient = httpClient ?? new HttpClient();
            _OwnsHttpClient = httpClient == null;
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.SendMessagePath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.SendMessageResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return StreamingAsync(
                A2AGrpcWire.SendStreamingMessagePath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.StreamResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.GetTaskPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.Task.Parser,
                response => A2AGrpcWire.FromGrpc(response),
                token);
        }

        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.ListTasksPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.ListTasksResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.CancelTaskPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.Task.Parser,
                response => A2AGrpcWire.FromGrpc(response),
                token);
        }

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken token = default)
        {
            return StreamingAsync(
                A2AGrpcWire.SubscribeToTaskPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.StreamResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.CreateTaskPushNotificationConfigPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.TaskPushNotificationConfig.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.GetTaskPushNotificationConfigPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.TaskPushNotificationConfig.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.ListTaskPushNotificationConfigsPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.ListTaskPushNotificationConfigsResponse.Parser,
                A2AGrpcWire.FromGrpc,
                token);
        }

        public async Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            await UnaryAsync(
                A2AGrpcWire.DeleteTaskPushNotificationConfigPath,
                A2AGrpcWire.ToGrpc(request),
                Empty.Parser,
                _ => true,
                token).ConfigureAwait(false);
        }

        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken token = default)
        {
            return UnaryAsync(
                A2AGrpcWire.GetExtendedAgentCardPath,
                A2AGrpcWire.ToGrpc(request),
                GrpcWire.AgentCard.Parser,
                A2AGrpcWire.FromGrpc,
                token);
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
