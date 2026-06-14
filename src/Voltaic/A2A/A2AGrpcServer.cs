#pragma warning disable CS1591
namespace Voltaic.A2A
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Google.Protobuf;
    using Google.Protobuf.WellKnownTypes;
    using Voltaic.Core;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using GrpcWire = Voltaic.A2A.Grpc;
    using WatsonHttpMethod = WatsonWebserver.Core.HttpMethod;

    /// <summary>
    /// Direct A2A gRPC server implemented with Watson HTTP/2 and protobuf framing.
    /// </summary>
    public sealed class A2AGrpcServer : IDisposable
    {
        private readonly string _Hostname;
        private readonly int _Port;
        private readonly A2AHttpServer _Endpoint;
        private Webserver? _Server;
        private CancellationTokenSource? _TokenSource;
        private bool _IsDisposed;

        public A2AGrpcServer(string hostname, int port, AgentCard agentCard, IA2AAgentHandler? handler = null, IA2ATaskStore? taskStore = null)
        {
            if (String.IsNullOrWhiteSpace(hostname)) throw new ArgumentNullException(nameof(hostname));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Hostname = hostname;
            _Port = port;
            _Endpoint = new A2AHttpServer(hostname, port, agentCard, handler, taskStore);
        }

        public AgentCard AgentCard
        {
            get => _Endpoint.AgentCard;
            set => _Endpoint.AgentCard = value ?? throw new ArgumentNullException(nameof(value));
        }

        public AgentCard? ExtendedAgentCard
        {
            get => _Endpoint.ExtendedAgentCard;
            set => _Endpoint.ExtendedAgentCard = value;
        }

        public IA2AAgentHandler? Handler => _Endpoint.Handler;

        public Func<HttpContextBase, Task<AuthenticationResult>>? AuthenticationHandler { get; set; }

        public bool EnableAgentCardEndpoints { get; set; } = true;

        public event EventHandler<string>? Log;

        public void RegisterHandler(IA2AAgentHandler handler)
        {
            _Endpoint.RegisterHandler(handler);
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, bool streaming, CancellationToken token = default)
        {
            return _Endpoint.SendMessageAsync(request, streaming, token);
        }

        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return _Endpoint.SendStreamingMessageAsync(request, token);
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            return _Endpoint.GetTaskAsync(request, token);
        }

        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            return _Endpoint.ListTasksAsync(request, token);
        }

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            return _Endpoint.CancelTaskAsync(request, token);
        }

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken token = default)
        {
            return _Endpoint.SubscribeToTaskAsync(request, token);
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return _Endpoint.CreateTaskPushNotificationConfigAsync(request, token);
        }

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return _Endpoint.GetTaskPushNotificationConfigAsync(request, token);
        }

        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return _Endpoint.ListTaskPushNotificationConfigAsync(request, token);
        }

        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return _Endpoint.DeleteTaskPushNotificationConfigAsync(request, token);
        }

        public async Task StartAsync(CancellationToken token = default)
        {
            if (_Server != null)
            {
                throw new InvalidOperationException("Server is already started.");
            }

            _TokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            WebserverSettings settings = new WebserverSettings(NormalizeListenHostname(_Hostname), _Port, false);
            settings.Protocols.EnableHttp1 = true;
            settings.Protocols.EnableHttp2 = true;
            settings.Protocols.EnableHttp2Cleartext = true;
            settings.Protocols.EnableHttp3 = false;

            _Server = new Webserver(settings, HandleRequestAsync);
            _Server.Start(_TokenSource.Token);
            await Task.Yield();
        }

        public void Stop()
        {
            _TokenSource?.Cancel();
            try
            {
                _Server?.Stop();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_IsDisposed)
            {
                return;
            }

            _IsDisposed = true;
            Stop();
            _Server?.Dispose();
            _TokenSource?.Dispose();
            _Endpoint.Dispose();
        }

        private async Task HandleRequestAsync(HttpContextBase context)
        {
            try
            {
                string path = context.Request.Url.RawWithoutQuery ?? "/";

                if (AuthenticationHandler != null && !IsPublicAgentCardRequest(context, path))
                {
                    AuthenticationResult auth = await AuthenticationHandler(context).ConfigureAwait(false);
                    if (!auth.IsAuthenticated)
                    {
                        await SendTextAsync(context, auth.StatusCode, auth.ErrorMessage ?? "Unauthorized", context.Token).ConfigureAwait(false);
                        return;
                    }
                }

                if (EnableAgentCardEndpoints &&
                    context.Request.Method == WatsonHttpMethod.GET &&
                    StringComparer.OrdinalIgnoreCase.Equals(path, A2AProtocol.AgentCardPath))
                {
                    await SendJsonAsync(context, AgentCard, context.Token).ConfigureAwait(false);
                    return;
                }

                if (EnableAgentCardEndpoints &&
                    context.Request.Method == WatsonHttpMethod.GET &&
                    StringComparer.OrdinalIgnoreCase.Equals(path, A2AProtocol.ExtendedAgentCardPath))
                {
                    await SendJsonAsync(context, GetExtendedAgentCard(), context.Token).ConfigureAwait(false);
                    return;
                }

                if (context.Request.Method != WatsonHttpMethod.POST || !path.StartsWith(A2AGrpcWire.ServicePrefix, StringComparison.Ordinal))
                {
                    await SendTextAsync(context, 404, "Not found", context.Token).ConfigureAwait(false);
                    return;
                }

                if (context.Protocol != HttpProtocol.Http2 && context.Protocol != HttpProtocol.Http3)
                {
                    await SendTextAsync(context, 426, "A2A gRPC requires HTTP/2.", context.Token).ConfigureAwait(false);
                    return;
                }

                byte[] body = await context.Request.ReadBodyAsync(context.Token).ConfigureAwait(false);
                byte[] payload = A2AGrpcWire.DecodeSinglePayload(body);

                switch (path)
                {
                    case A2AGrpcWire.SendMessagePath:
                        await HandleUnaryAsync(
                            context,
                            GrpcWire.SendMessageRequest.Parser.ParseFrom(payload),
                            request => _Endpoint.SendMessageAsync(A2AGrpcWire.FromGrpc(request), false, context.Token),
                            response => A2AGrpcWire.ToGrpc(response),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.SendStreamingMessagePath:
                        await SendStreamAsync(
                            context,
                            _Endpoint.SendStreamingMessageAsync(A2AGrpcWire.FromGrpc(GrpcWire.SendMessageRequest.Parser.ParseFrom(payload)), context.Token),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.GetTaskPath:
                        await HandleUnaryAsync(
                            context,
                            GrpcWire.GetTaskRequest.Parser.ParseFrom(payload),
                            request => _Endpoint.GetTaskAsync(A2AGrpcWire.FromGrpc(request), context.Token),
                            response => A2AGrpcWire.ToGrpc(response),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.ListTasksPath:
                        await HandleUnaryAsync(
                            context,
                            GrpcWire.ListTasksRequest.Parser.ParseFrom(payload),
                            request => _Endpoint.ListTasksAsync(A2AGrpcWire.FromGrpc(request), context.Token),
                            response => A2AGrpcWire.ToGrpc(response),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.CancelTaskPath:
                        await HandleUnaryAsync(
                            context,
                            GrpcWire.CancelTaskRequest.Parser.ParseFrom(payload),
                            request => _Endpoint.CancelTaskAsync(A2AGrpcWire.FromGrpc(request), context.Token),
                            response => A2AGrpcWire.ToGrpc(response),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.SubscribeToTaskPath:
                        await SendStreamAsync(
                            context,
                            _Endpoint.SubscribeToTaskAsync(A2AGrpcWire.FromGrpc(GrpcWire.SubscribeToTaskRequest.Parser.ParseFrom(payload)), context.Token),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.CreateTaskPushNotificationConfigPath:
                        await HandleUnaryAsync(
                            context,
                            GrpcWire.TaskPushNotificationConfig.Parser.ParseFrom(payload),
                            request => _Endpoint.CreateTaskPushNotificationConfigAsync(A2AGrpcWire.ToCreateRequest(request), context.Token),
                            response => A2AGrpcWire.ToGrpc(response),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.GetTaskPushNotificationConfigPath:
                        await HandleUnaryAsync(
                            context,
                            GrpcWire.GetTaskPushNotificationConfigRequest.Parser.ParseFrom(payload),
                            request => _Endpoint.GetTaskPushNotificationConfigAsync(A2AGrpcWire.FromGrpc(request), context.Token),
                            response => A2AGrpcWire.ToGrpc(response),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.ListTaskPushNotificationConfigsPath:
                        await HandleUnaryAsync(
                            context,
                            GrpcWire.ListTaskPushNotificationConfigsRequest.Parser.ParseFrom(payload),
                            request => _Endpoint.ListTaskPushNotificationConfigAsync(A2AGrpcWire.FromGrpc(request), context.Token),
                            response => A2AGrpcWire.ToGrpc(response),
                            context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.GetExtendedAgentCardPath:
                        await SendUnaryAsync(context, A2AGrpcWire.ToGrpc(GetExtendedAgentCard()), context.Token).ConfigureAwait(false);
                        break;

                    case A2AGrpcWire.DeleteTaskPushNotificationConfigPath:
                        await HandleUnaryAsync(
                            context,
                            GrpcWire.DeleteTaskPushNotificationConfigRequest.Parser.ParseFrom(payload),
                            async request =>
                            {
                                await _Endpoint.DeleteTaskPushNotificationConfigAsync(A2AGrpcWire.FromGrpc(request), context.Token).ConfigureAwait(false);
                                return new Empty();
                            },
                            response => response,
                            context.Token).ConfigureAwait(false);
                        break;

                    default:
                        await SendGrpcErrorAsync(
                            context,
                            new A2AProtocolException(A2AErrorCode.MethodNotFound, $"gRPC method path '{path}' was not found."),
                            context.Token).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"A2A gRPC request error: {ex.Message}");
                await SendGrpcErrorAsync(context, ex, context.Token).ConfigureAwait(false);
            }
        }

        private async Task HandleUnaryAsync<TWireRequest, TResult>(
            HttpContextBase context,
            TWireRequest request,
            Func<TWireRequest, Task<TResult>> invoke,
            Func<TResult, IMessage> map,
            CancellationToken token)
        {
            TResult result = await invoke(request).ConfigureAwait(false);
            await SendUnaryAsync(context, map(result), token).ConfigureAwait(false);
        }

        private async Task SendUnaryAsync(HttpContextBase context, IMessage response, CancellationToken token)
        {
            PrepareGrpcResponse(context);
            SetGrpcStatus(context, 0, null);
            await context.Response.Send(A2AGrpcWire.EncodeMessage(response), token).ConfigureAwait(false);
        }

        private async Task SendStreamAsync(HttpContextBase context, IAsyncEnumerable<StreamResponse> responses, CancellationToken token)
        {
            PrepareGrpcResponse(context);
            context.Response.ChunkedTransfer = true;

            try
            {
                await foreach (StreamResponse item in responses.ConfigureAwait(false))
                {
                    await context.Response.SendChunk(A2AGrpcWire.EncodeMessage(A2AGrpcWire.ToGrpc(item)), false, token).ConfigureAwait(false);
                }

                SetGrpcStatus(context, 0, null);
            }
            catch (Exception ex)
            {
                SetGrpcStatus(context, A2AGrpcWire.ToGrpcStatus(ex), ex.Message);
            }

            await context.Response.SendChunk(Array.Empty<byte>(), true, token).ConfigureAwait(false);
        }

        private async Task SendGrpcErrorAsync(HttpContextBase context, Exception ex, CancellationToken token)
        {
            if (context.Response.ResponseStarted)
            {
                return;
            }

            PrepareGrpcResponse(context);
            SetGrpcStatus(context, A2AGrpcWire.ToGrpcStatus(ex), ex.Message);
            await context.Response.Send(Array.Empty<byte>(), token).ConfigureAwait(false);
        }

        private AgentCard GetExtendedAgentCard()
        {
            if (AgentCard.Capabilities.ExtendedAgentCard != true)
            {
                throw A2AProtocolException.UnsupportedOperation("Extended Agent Card is not supported.");
            }

            return ExtendedAgentCard ?? throw new A2AProtocolException(A2AErrorCode.ExtendedAgentCardNotConfigured, "Extended Agent Card is not configured.");
        }

        private static void PrepareGrpcResponse(HttpContextBase context)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = A2AGrpcWire.ContentType;
            context.Response.Headers.Set("grpc-encoding", "identity");
            context.Response.Headers.Set("grpc-accept-encoding", "identity");
        }

        private static void SetGrpcStatus(HttpContextBase context, int status, string? message)
        {
            context.Response.Trailers.Set("grpc-status", status.ToString());
            if (!String.IsNullOrEmpty(message))
            {
                context.Response.Trailers.Set("grpc-message", Uri.EscapeDataString(message));
            }
        }

        private static async Task SendJsonAsync(HttpContextBase context, object value, CancellationToken token)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.Headers.Set(A2AProtocol.VersionHeader, A2AProtocol.ProtocolVersion);
            string json = JsonSerializer.Serialize(value, A2AJson.DefaultOptions);
            await context.Response.Send(json, token).ConfigureAwait(false);
        }

        private static async Task SendTextAsync(HttpContextBase context, int statusCode, string text, CancellationToken token)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain";
            byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
            await context.Response.Send(data, token).ConfigureAwait(false);
        }

        private static bool IsPublicAgentCardRequest(HttpContextBase context, string path)
        {
            return context.Request.Method == WatsonHttpMethod.GET &&
                (StringComparer.OrdinalIgnoreCase.Equals(path, A2AProtocol.AgentCardPath) ||
                 StringComparer.OrdinalIgnoreCase.Equals(path, A2AProtocol.ExtendedAgentCardPath));
        }

        private static string NormalizeListenHostname(string hostname)
        {
            return hostname == "*" || hostname == "+" ? "0.0.0.0" : hostname;
        }

        private void LogMessage(string message)
        {
            EventHandler<string>? handler = Log;
            if (handler != null)
            {
                Task.Run(() =>
                {
                    try
                    {
                        handler(this, message);
                    }
                    catch
                    {
                    }
                });
            }
        }
    }
}
