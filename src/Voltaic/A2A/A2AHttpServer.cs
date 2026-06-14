#pragma warning disable CS1591
namespace Voltaic.A2A
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using Voltaic.Core;

    public interface IA2AAgentHandler
    {
        Task ExecuteAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token);

        async Task CancelAsync(A2ARequestContext context, A2AAgentEventQueue eventQueue, CancellationToken token)
        {
            A2ATaskUpdater updater = new A2ATaskUpdater(eventQueue, context.TaskId, context.ContextId);
            await updater.CancelAsync(token: token).ConfigureAwait(false);
        }
    }

    public sealed class A2ARequestContext
    {
        public Message Message { get; set; } = new Message();
        public SendMessageRequest? SendMessageRequest { get; set; }
        public AgentTask? ExistingTask { get; set; }
        public string TaskId { get; set; } = string.Empty;
        public string ContextId { get; set; } = string.Empty;
        public bool IsContinuation => ExistingTask != null;
        public bool StreamingResponse { get; set; }
        public SendMessageConfiguration? Configuration { get; set; }
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }

    public sealed class A2AAgentEventQueue
    {
        private readonly Channel<StreamResponse> _Channel = Channel.CreateUnbounded<StreamResponse>();

        public ValueTask WriteAsync(StreamResponse response, CancellationToken token = default)
        {
            return _Channel.Writer.WriteAsync(response, token);
        }

        public void Complete(Exception? exception = null)
        {
            _Channel.Writer.TryComplete(exception);
        }

        public IAsyncEnumerable<StreamResponse> ReadAllAsync(CancellationToken token = default)
        {
            return _Channel.Reader.ReadAllAsync(token);
        }
    }

    public sealed class A2ATaskUpdater
    {
        private readonly A2AAgentEventQueue _EventQueue;
        private readonly string _TaskId;
        private readonly string _ContextId;

        public A2ATaskUpdater(A2AAgentEventQueue eventQueue, string taskId, string contextId)
        {
            _EventQueue = eventQueue ?? throw new ArgumentNullException(nameof(eventQueue));
            _TaskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
            _ContextId = contextId ?? throw new ArgumentNullException(nameof(contextId));
        }

        public ValueTask SubmitAsync(Message? message = null, CancellationToken token = default)
        {
            return UpdateStatusAsync(TaskState.Submitted, message, token);
        }

        public ValueTask StartAsync(Message? message = null, CancellationToken token = default)
        {
            return UpdateStatusAsync(TaskState.Working, message, token);
        }

        public ValueTask CompleteAsync(Message? message = null, CancellationToken token = default)
        {
            return UpdateStatusAsync(TaskState.Completed, message, token);
        }

        public ValueTask FailAsync(Message? message = null, CancellationToken token = default)
        {
            return UpdateStatusAsync(TaskState.Failed, message, token);
        }

        public ValueTask CancelAsync(Message? message = null, CancellationToken token = default)
        {
            return UpdateStatusAsync(TaskState.Canceled, message, token);
        }

        public ValueTask UpdateStatusAsync(TaskState state, Message? message = null, CancellationToken token = default)
        {
            return _EventQueue.WriteAsync(new StreamResponse
            {
                StatusUpdate = new TaskStatusUpdateEvent
                {
                    TaskId = _TaskId,
                    ContextId = _ContextId,
                    Status = new TaskStatus
                    {
                        State = state,
                        Message = message,
                        Timestamp = DateTimeOffset.UtcNow
                    }
                }
            }, token);
        }

        public ValueTask AddArtifactAsync(Artifact artifact, bool append = false, bool lastChunk = true, CancellationToken token = default)
        {
            return _EventQueue.WriteAsync(new StreamResponse
            {
                ArtifactUpdate = new TaskArtifactUpdateEvent
                {
                    TaskId = _TaskId,
                    ContextId = _ContextId,
                    Artifact = artifact,
                    Append = append,
                    LastChunk = lastChunk
                }
            }, token);
        }

        public ValueTask ReturnMessageAsync(Message message, CancellationToken token = default)
        {
            return _EventQueue.WriteAsync(new StreamResponse { Message = message }, token);
        }
    }

    public interface IA2ATaskStore
    {
        Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken token = default);
        Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken token = default);
        Task DeleteTaskAsync(string taskId, CancellationToken token = default);
        Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default);
    }

    public sealed class InMemoryA2ATaskStore : IA2ATaskStore
    {
        private readonly ConcurrentDictionary<string, AgentTask> _Tasks = new ConcurrentDictionary<string, AgentTask>(StringComparer.Ordinal);

        public Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken token = default)
        {
            if (_Tasks.TryGetValue(taskId, out AgentTask? task))
            {
                return Task.FromResult<AgentTask?>(task.Clone());
            }

            return Task.FromResult<AgentTask?>(null);
        }

        public Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken token = default)
        {
            _Tasks[taskId] = task.Clone();
            return Task.CompletedTask;
        }

        public Task DeleteTaskAsync(string taskId, CancellationToken token = default)
        {
            _Tasks.TryRemove(taskId, out _);
            return Task.CompletedTask;
        }

        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            IEnumerable<AgentTask> query = _Tasks.Values.Select(task => task.Clone());
            if (!String.IsNullOrEmpty(request.ContextId))
            {
                query = query.Where(task => StringComparer.Ordinal.Equals(task.ContextId, request.ContextId));
            }

            if (request.Status != null)
            {
                query = query.Where(task => task.Status.State == request.Status.Value);
            }

            if (request.StatusTimestampAfter != null)
            {
                query = query.Where(task => task.Status.Timestamp != null && task.Status.Timestamp > request.StatusTimestampAfter);
            }

            List<AgentTask> all = query.OrderBy(task => task.Id, StringComparer.Ordinal).ToList();
            int offset = 0;
            if (!String.IsNullOrEmpty(request.PageToken) && (!Int32.TryParse(request.PageToken, out offset) || offset < 0))
            {
                throw A2AProtocolException.InvalidParams($"Invalid pageToken '{request.PageToken}'.");
            }

            int pageSize = Math.Max(1, request.PageSize ?? 100);
            List<AgentTask> page = all.Skip(offset).Take(pageSize).Select(task =>
            {
                AgentTask trimmed = task.WithHistoryTrimmedTo(request.HistoryLength);
                if (request.IncludeArtifacts == false)
                {
                    trimmed.Artifacts = null;
                }

                return trimmed;
            }).ToList();

            int nextOffset = offset + page.Count;
            return Task.FromResult(new ListTasksResponse
            {
                Tasks = page,
                PageSize = page.Count,
                TotalSize = all.Count,
                NextPageToken = nextOffset < all.Count ? nextOffset.ToString() : string.Empty
            });
        }
    }

    public sealed class A2AHttpServer : IDisposable
    {
        private static readonly byte[] _SsePrelude = Encoding.UTF8.GetBytes(": connected\n\n");
        private readonly string _Hostname;
        private readonly int _Port;
        private readonly string _RpcPath;
        private readonly IA2ATaskStore _TaskStore;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TaskPushNotificationConfig>> _PushConfigs = new ConcurrentDictionary<string, ConcurrentDictionary<string, TaskPushNotificationConfig>>(StringComparer.Ordinal);
        private readonly A2ATaskEventNotifier _Notifier = new A2ATaskEventNotifier();
        private HttpListener? _Listener;
        private CancellationTokenSource? _TokenSource;
        private Task? _ListenTask;
        private bool _IsDisposed;

        public AgentCard AgentCard { get; set; }

        public AgentCard? ExtendedAgentCard { get; set; }

        public IA2AAgentHandler? Handler { get; private set; }

        public Func<HttpListenerRequest, Task<AuthenticationResult>>? AuthenticationHandler { get; set; }

        public bool EnableCors { get; set; } = true;

        public Dictionary<string, string> CorsHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS" },
            { "Access-Control-Allow-Headers", "*" },
            { "Access-Control-Max-Age", "86400" }
        };

        public event EventHandler<string>? Log;

        public A2AHttpServer(string hostname, int port, AgentCard agentCard, IA2AAgentHandler? handler = null, IA2ATaskStore? taskStore = null, string rpcPath = "/a2a")
        {
            if (String.IsNullOrWhiteSpace(hostname)) throw new ArgumentNullException(nameof(hostname));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Hostname = hostname;
            _Port = port;
            _RpcPath = NormalizePath(rpcPath);
            AgentCard = agentCard ?? throw new ArgumentNullException(nameof(agentCard));
            Handler = handler;
            _TaskStore = taskStore ?? new InMemoryA2ATaskStore();
        }

        public void RegisterHandler(IA2AAgentHandler handler)
        {
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public async Task StartAsync(CancellationToken token = default)
        {
            if (_Listener != null)
            {
                throw new InvalidOperationException("Server is already started.");
            }

            _TokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            _Listener = new HttpListener();
            string prefixHost = _Hostname == "*" ? "+" : _Hostname;
            _Listener.Prefixes.Add($"http://{prefixHost}:{_Port}/");
            _Listener.Start();
            _ListenTask = Task.Run(() => ListenLoopAsync(_TokenSource.Token));
            await Task.Yield();
        }

        public void Stop()
        {
            _TokenSource?.Cancel();
            try
            {
                _Listener?.Stop();
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
            _Listener?.Close();
            _TokenSource?.Dispose();
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _Listener != null && _Listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await _Listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(context, token));
                }
                catch (Exception ex) when (token.IsCancellationRequested || ex is HttpListenerException || ex is ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                AddCors(context.Response);
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    return;
                }

                if (AuthenticationHandler != null && !IsPublicAgentCardRequest(context.Request))
                {
                    AuthenticationResult auth = await AuthenticationHandler(context.Request).ConfigureAwait(false);
                    if (!auth.IsAuthenticated)
                    {
                        await SendTextAsync(context, auth.StatusCode, auth.ErrorMessage ?? "Unauthorized", token).ConfigureAwait(false);
                        return;
                    }
                }

                string path = context.Request.Url?.AbsolutePath ?? "/";

                if (context.Request.HttpMethod == "GET" && StringComparer.OrdinalIgnoreCase.Equals(path, A2AProtocol.AgentCardPath))
                {
                    await SendJsonAsync(context, AgentCard, token).ConfigureAwait(false);
                    return;
                }

                if (context.Request.HttpMethod == "GET" && StringComparer.OrdinalIgnoreCase.Equals(path, A2AProtocol.ExtendedAgentCardPath))
                {
                    AgentCard extended = GetExtendedAgentCard();
                    await SendJsonAsync(context, extended, token).ConfigureAwait(false);
                    return;
                }

                if (StringComparer.OrdinalIgnoreCase.Equals(path, _RpcPath))
                {
                    await HandleJsonRpcAsync(context, token).ConfigureAwait(false);
                    return;
                }

                if (await TryHandleRestAsync(context, path, token).ConfigureAwait(false))
                {
                    return;
                }

                await SendTextAsync(context, 404, "Not found", token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMessage($"A2A request error: {ex.Message}");
                if (context.Response.OutputStream.CanWrite)
                {
                    await SendJsonErrorAsync(context, ex, null, token).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleJsonRpcAsync(HttpListenerContext context, CancellationToken token)
        {
            if (context.Request.HttpMethod != "POST")
            {
                await SendTextAsync(context, 405, "Method not allowed", token).ConfigureAwait(false);
                return;
            }

            string body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            JsonRpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<JsonRpcRequest>(body, A2AJson.DefaultOptions);
            }
            catch (JsonException)
            {
                await SendJsonErrorAsync(context, new A2AProtocolException(A2AErrorCode.ParseError, "Parse error"), null, token).ConfigureAwait(false);
                return;
            }

            if (request == null || String.IsNullOrWhiteSpace(request.Method))
            {
                await SendJsonErrorAsync(context, new A2AProtocolException(A2AErrorCode.InvalidRequest, "Invalid JSON-RPC request."), request?.Id, token).ConfigureAwait(false);
                return;
            }

            if (!A2AProtocol.IsValidMethod(request.Method))
            {
                await SendJsonErrorAsync(context, new A2AProtocolException(A2AErrorCode.MethodNotFound, $"Method '{request.Method}' was not found."), request.Id, token).ConfigureAwait(false);
                return;
            }

            if (A2AProtocol.IsStreamingMethod(request.Method))
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                await context.Response.OutputStream.WriteAsync(_SsePrelude, 0, _SsePrelude.Length, token).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(token).ConfigureAwait(false);

                try
                {
                    await foreach (StreamResponse item in InvokeStreamingMethodAsync(request.Method, request.Params, token).ConfigureAwait(false))
                    {
                        JsonRpcResponse response = new JsonRpcResponse { Id = request.Id, Result = item };
                        await SendSseJsonAsync(context.Response, response, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    JsonRpcResponse response = BuildErrorResponse(ex, request.Id);
                    await SendSseJsonAsync(context.Response, response, token).ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Close();
                }

                return;
            }

            try
            {
                object? result = await InvokeMethodAsync(request.Method, request.Params, token).ConfigureAwait(false);
                JsonRpcResponse response = new JsonRpcResponse { Id = request.Id, Result = result };
                await SendJsonAsync(context, response, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await SendJsonErrorAsync(context, ex, request.Id, token).ConfigureAwait(false);
            }
        }

        private async Task<bool> TryHandleRestAsync(HttpListenerContext context, string path, CancellationToken token)
        {
            string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (context.Request.HttpMethod == "POST" && path == "/message:send")
            {
                SendMessageRequest request = await ReadJsonAsync<SendMessageRequest>(context.Request).ConfigureAwait(false);
                await SendJsonAsync(context, await SendMessageAsync(request, false, token).ConfigureAwait(false), token).ConfigureAwait(false);
                return true;
            }

            if (context.Request.HttpMethod == "POST" && path == "/message:stream")
            {
                SendMessageRequest request = await ReadJsonAsync<SendMessageRequest>(context.Request).ConfigureAwait(false);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                await context.Response.OutputStream.WriteAsync(_SsePrelude, 0, _SsePrelude.Length, token).ConfigureAwait(false);
                await foreach (StreamResponse item in SendStreamingMessageAsync(request, token).ConfigureAwait(false))
                {
                    await SendSseJsonAsync(context.Response, item, token).ConfigureAwait(false);
                }
                context.Response.Close();
                return true;
            }

            if (segments.Length == 1 && segments[0] == "tasks" && context.Request.HttpMethod == "GET")
            {
                ListTasksRequest request = new ListTasksRequest
                {
                    ContextId = context.Request.QueryString["contextId"],
                    Status = TryParseTaskState(context.Request.QueryString["status"]),
                    PageToken = context.Request.QueryString["pageToken"],
                    PageSize = TryParseInt(context.Request.QueryString["pageSize"]),
                    HistoryLength = TryParseInt(context.Request.QueryString["historyLength"]),
                    StatusTimestampAfter = TryParseDateTimeOffset(context.Request.QueryString["statusTimestampAfter"]),
                    IncludeArtifacts = TryParseBool(context.Request.QueryString["includeArtifacts"])
                };
                await SendJsonAsync(context, await ListTasksAsync(request, token).ConfigureAwait(false), token).ConfigureAwait(false);
                return true;
            }

            if (segments.Length >= 2 && segments[0] == "tasks")
            {
                string taskId = Uri.UnescapeDataString(segments[1]);
                if (segments.Length == 2 && context.Request.HttpMethod == "GET")
                {
                    GetTaskRequest request = new GetTaskRequest
                    {
                        Id = taskId,
                        HistoryLength = TryParseInt(context.Request.QueryString["historyLength"])
                    };
                    await SendJsonAsync(context, await GetTaskAsync(request, token).ConfigureAwait(false), token).ConfigureAwait(false);
                    return true;
                }

                if (segments.Length == 2 && segments[1].EndsWith(":cancel", StringComparison.Ordinal))
                {
                    string id = Uri.UnescapeDataString(segments[1].Substring(0, segments[1].Length - ":cancel".Length));
                    await SendJsonAsync(context, await CancelTaskAsync(new CancelTaskRequest { Id = id }, token).ConfigureAwait(false), token).ConfigureAwait(false);
                    return true;
                }

                if (segments.Length == 2 && segments[1].EndsWith(":subscribe", StringComparison.Ordinal))
                {
                    string id = Uri.UnescapeDataString(segments[1].Substring(0, segments[1].Length - ":subscribe".Length));
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/event-stream";
                    context.Response.SendChunked = true;
                    await context.Response.OutputStream.WriteAsync(_SsePrelude, 0, _SsePrelude.Length, token).ConfigureAwait(false);
                    await foreach (StreamResponse item in SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = id }, token).ConfigureAwait(false))
                    {
                        await SendSseJsonAsync(context.Response, item, token).ConfigureAwait(false);
                    }
                    context.Response.Close();
                    return true;
                }

                if (segments.Length >= 3 && segments[2] == "pushNotificationConfigs")
                {
                    if (segments.Length == 3 && context.Request.HttpMethod == "POST")
                    {
                        CreateTaskPushNotificationConfigRequest request = await ReadPushNotificationConfigRequestAsync(context.Request).ConfigureAwait(false);
                        request.TaskId = taskId;
                        await SendJsonAsync(context, await CreateTaskPushNotificationConfigAsync(request, token).ConfigureAwait(false), token).ConfigureAwait(false);
                        return true;
                    }

                    if (segments.Length == 3 && context.Request.HttpMethod == "GET")
                    {
                        await SendJsonAsync(context, await ListTaskPushNotificationConfigAsync(new ListTaskPushNotificationConfigRequest { TaskId = taskId }, token).ConfigureAwait(false), token).ConfigureAwait(false);
                        return true;
                    }

                    if (segments.Length == 4)
                    {
                        string configId = Uri.UnescapeDataString(segments[3]);
                        if (context.Request.HttpMethod == "GET")
                        {
                            await SendJsonAsync(context, await GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = taskId, ConfigId = configId }, token).ConfigureAwait(false), token).ConfigureAwait(false);
                            return true;
                        }

                        if (context.Request.HttpMethod == "DELETE")
                        {
                            await DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = taskId, ConfigId = configId }, token).ConfigureAwait(false);
                            await SendJsonAsync(context, new { }, token).ConfigureAwait(false);
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private async Task<object?> InvokeMethodAsync(string method, object? parameters, CancellationToken token)
        {
            return method switch
            {
                A2AProtocol.SendMessage => await SendMessageAsync(DeserializeParams<SendMessageRequest>(parameters), false, token).ConfigureAwait(false),
                A2AProtocol.GetTask => await GetTaskAsync(DeserializeParams<GetTaskRequest>(parameters), token).ConfigureAwait(false),
                A2AProtocol.ListTasks => await ListTasksAsync(DeserializeParams<ListTasksRequest>(parameters), token).ConfigureAwait(false),
                A2AProtocol.CancelTask => await CancelTaskAsync(DeserializeParams<CancelTaskRequest>(parameters), token).ConfigureAwait(false),
                A2AProtocol.CreateTaskPushNotificationConfig => await CreateTaskPushNotificationConfigAsync(DeserializeParams<CreateTaskPushNotificationConfigRequest>(parameters), token).ConfigureAwait(false),
                A2AProtocol.GetTaskPushNotificationConfig => await GetTaskPushNotificationConfigAsync(DeserializeParams<GetTaskPushNotificationConfigRequest>(parameters), token).ConfigureAwait(false),
                A2AProtocol.ListTaskPushNotificationConfig => await ListTaskPushNotificationConfigAsync(DeserializeParams<ListTaskPushNotificationConfigRequest>(parameters), token).ConfigureAwait(false),
                A2AProtocol.DeleteTaskPushNotificationConfig => await DeleteTaskPushNotificationConfigAsync(DeserializeParams<DeleteTaskPushNotificationConfigRequest>(parameters), token).ContinueWith(_ => (object?)new { }, token).ConfigureAwait(false),
                A2AProtocol.GetExtendedAgentCard => GetExtendedAgentCard(),
                _ => throw new A2AProtocolException(A2AErrorCode.MethodNotFound, $"Method '{method}' was not found.")
            };
        }

        private async IAsyncEnumerable<StreamResponse> InvokeStreamingMethodAsync(string method, object? parameters, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            if (method == A2AProtocol.SendStreamingMessage)
            {
                await foreach (StreamResponse item in SendStreamingMessageAsync(DeserializeParams<SendMessageRequest>(parameters), token).ConfigureAwait(false))
                {
                    yield return item;
                }
                yield break;
            }

            if (method == A2AProtocol.SubscribeToTask)
            {
                await foreach (StreamResponse item in SubscribeToTaskAsync(DeserializeParams<SubscribeToTaskRequest>(parameters), token).ConfigureAwait(false))
                {
                    yield return item;
                }
                yield break;
            }

            throw new A2AProtocolException(A2AErrorCode.MethodNotFound, $"Method '{method}' was not found.");
        }

        public async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, bool streaming, CancellationToken token = default)
        {
            A2ARequestContext context = await ResolveContextAsync(request, streaming, token).ConfigureAwait(false);
            IA2AAgentHandler handler = Handler ?? throw new A2AProtocolException(A2AErrorCode.InternalError, "No A2A handler is registered.");

            if (!streaming && context.Configuration?.ReturnImmediately == true)
            {
                AgentTask accepted = await SaveAcceptedTaskAsync(context, token).ConfigureAwait(false);
                CancellationToken backgroundToken = _TokenSource?.Token ?? token;
                _ = Task.Run(() => ExecuteHandlerAndApplyEventsAsync(handler, context, backgroundToken), backgroundToken);
                return new SendMessageResponse { Task = accepted };
            }

            A2AAgentEventQueue queue = new A2AAgentEventQueue();

            Task handlerTask = Task.Run(async () =>
            {
                try
                {
                    await handler.ExecuteAsync(context, queue, token).ConfigureAwait(false);
                }
                finally
                {
                    queue.Complete();
                }
            }, token);

            SendMessageResponse? first = null;
            await foreach (StreamResponse item in queue.ReadAllAsync(token).ConfigureAwait(false))
            {
                await ApplyEventAsync(item, context, token).ConfigureAwait(false);
                if (first == null)
                {
                    if (item.Task != null) first = new SendMessageResponse { Task = item.Task };
                    else if (item.Message != null) first = new SendMessageResponse { Message = item.Message };
                    else if (item.StatusUpdate != null || item.ArtifactUpdate != null)
                    {
                        AgentTask? projected = await _TaskStore.GetTaskAsync(context.TaskId, token).ConfigureAwait(false);
                        if (projected != null)
                        {
                            first = new SendMessageResponse { Task = projected };
                        }
                    }
                }
            }

            await handlerTask.ConfigureAwait(false);

            if (first?.Task != null)
            {
                first.Task = await _TaskStore.GetTaskAsync(context.TaskId, token).ConfigureAwait(false) ?? first.Task;
            }

            return first ?? throw new A2AProtocolException(A2AErrorCode.InvalidAgentResponse, "Agent handler did not produce a response.");
        }

        private async Task ExecuteHandlerAndApplyEventsAsync(IA2AAgentHandler handler, A2ARequestContext context, CancellationToken token)
        {
            A2AAgentEventQueue queue = new A2AAgentEventQueue();
            Task handlerTask = Task.Run(async () =>
            {
                try
                {
                    await handler.ExecuteAsync(context, queue, token).ConfigureAwait(false);
                }
                finally
                {
                    queue.Complete();
                }
            }, token);

            try
            {
                await foreach (StreamResponse item in queue.ReadAllAsync(token).ConfigureAwait(false))
                {
                    await ApplyEventAsync(item, context, token).ConfigureAwait(false);
                }

                await handlerTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                LogMessage($"A2A background handler error: {ex.Message}");
                await ApplyEventAsync(new StreamResponse
                {
                    StatusUpdate = new TaskStatusUpdateEvent
                    {
                        TaskId = context.TaskId,
                        ContextId = context.ContextId,
                        Status = new TaskStatus
                        {
                            State = TaskState.Failed,
                            Timestamp = DateTimeOffset.UtcNow,
                            Message = new Message
                            {
                                Role = Role.Agent,
                                MessageId = Guid.NewGuid().ToString("N"),
                                TaskId = context.TaskId,
                                ContextId = context.ContextId,
                                Parts = new List<Part> { Part.FromText("Agent handler failed.") }
                            }
                        }
                    }
                }, context, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task<AgentTask> SaveAcceptedTaskAsync(A2ARequestContext context, CancellationToken token)
        {
            AgentTask task = context.ExistingTask?.Clone() ?? new AgentTask
            {
                Id = context.TaskId,
                ContextId = context.ContextId,
                History = new List<Message>(),
                Artifacts = new List<Artifact>()
            };

            task.Status = new TaskStatus
            {
                State = TaskState.Submitted,
                Timestamp = DateTimeOffset.UtcNow
            };
            task.History ??= new List<Message>();
            if (!task.History.Any(message => message.MessageId == context.Message.MessageId))
            {
                task.History.Add(context.Message);
            }

            await _TaskStore.SaveTaskAsync(task.Id, task, token).ConfigureAwait(false);
            return task.WithHistoryTrimmedTo(context.Configuration?.HistoryLength);
        }

        public async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            A2ARequestContext context = await ResolveContextAsync(request, true, token).ConfigureAwait(false);
            A2AAgentEventQueue queue = new A2AAgentEventQueue();
            IA2AAgentHandler handler = Handler ?? throw new A2AProtocolException(A2AErrorCode.InternalError, "No A2A handler is registered.");

            Task handlerTask = Task.Run(async () =>
            {
                try
                {
                    await handler.ExecuteAsync(context, queue, token).ConfigureAwait(false);
                }
                finally
                {
                    queue.Complete();
                }
            }, token);

            await foreach (StreamResponse item in queue.ReadAllAsync(token).ConfigureAwait(false))
            {
                await ApplyEventAsync(item, context, token).ConfigureAwait(false);
                yield return item;
            }

            await handlerTask.ConfigureAwait(false);
        }

        public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(request.Id)) throw A2AProtocolException.InvalidParams("GetTask requires id.");
            if (request.HistoryLength < 0) throw A2AProtocolException.InvalidParams("historyLength must be non-negative.");

            AgentTask task = await _TaskStore.GetTaskAsync(request.Id, token).ConfigureAwait(false) ?? throw A2AProtocolException.TaskNotFound(request.Id);
            return task.WithHistoryTrimmedTo(request.HistoryLength);
        }

        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            return _TaskStore.ListTasksAsync(request ?? new ListTasksRequest(), token);
        }

        public async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(request.Id)) throw A2AProtocolException.InvalidParams("CancelTask requires id.");
            AgentTask task = await _TaskStore.GetTaskAsync(request.Id, token).ConfigureAwait(false) ?? throw A2AProtocolException.TaskNotFound(request.Id);
            if (IsTerminal(task.Status.State))
            {
                throw new A2AProtocolException(A2AErrorCode.TaskNotCancelable, "Task is already terminal.");
            }

            A2ARequestContext context = new A2ARequestContext
            {
                ExistingTask = task,
                TaskId = task.Id,
                ContextId = task.ContextId,
                Message = task.History?.LastOrDefault() ?? new Message { Role = Role.User, MessageId = string.Empty },
                Metadata = request.Metadata
            };
            A2AAgentEventQueue queue = new A2AAgentEventQueue();
            IA2AAgentHandler handler = Handler ?? throw new A2AProtocolException(A2AErrorCode.InternalError, "No A2A handler is registered.");
            await handler.CancelAsync(context, queue, token).ConfigureAwait(false);
            queue.Complete();
            await foreach (StreamResponse item in queue.ReadAllAsync(token).ConfigureAwait(false))
            {
                await ApplyEventAsync(item, context, token).ConfigureAwait(false);
            }

            return await _TaskStore.GetTaskAsync(request.Id, token).ConfigureAwait(false) ?? throw A2AProtocolException.TaskNotFound(request.Id);
        }

        public async IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(request.Id)) throw A2AProtocolException.InvalidParams("SubscribeToTask requires id.");
            AgentTask task = await _TaskStore.GetTaskAsync(request.Id, token).ConfigureAwait(false) ?? throw A2AProtocolException.TaskNotFound(request.Id);
            if (IsTerminal(task.Status.State))
            {
                throw A2AProtocolException.UnsupportedOperation("Cannot subscribe to a terminal task.");
            }

            Channel<StreamResponse> channel = _Notifier.CreateChannel(request.Id);
            try
            {
                yield return new StreamResponse { Task = task };
                await foreach (StreamResponse item in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    yield return item;
                }
            }
            finally
            {
                _Notifier.RemoveChannel(request.Id, channel);
            }
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            EnsurePushNotificationsSupported();
            if (String.IsNullOrWhiteSpace(request.TaskId)) throw A2AProtocolException.InvalidParams("TaskId is required.");
            string configId = String.IsNullOrWhiteSpace(request.ConfigId) ? request.Config.Id ?? Guid.NewGuid().ToString("N") : request.ConfigId;
            TaskPushNotificationConfig config = new TaskPushNotificationConfig
            {
                Id = configId,
                TaskId = request.TaskId,
                Tenant = request.Tenant,
                PushNotificationConfig = request.Config
            };
            _PushConfigs.GetOrAdd(request.TaskId, _ => new ConcurrentDictionary<string, TaskPushNotificationConfig>(StringComparer.Ordinal))[configId] = config;
            return Task.FromResult(config);
        }

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            EnsurePushNotificationsSupported();
            if (_PushConfigs.TryGetValue(request.TaskId, out ConcurrentDictionary<string, TaskPushNotificationConfig>? configs) &&
                configs.TryGetValue(request.ConfigId, out TaskPushNotificationConfig? config))
            {
                return Task.FromResult(config);
            }

            throw A2AProtocolException.TaskNotFound(request.TaskId);
        }

        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            EnsurePushNotificationsSupported();
            List<TaskPushNotificationConfig> configs = _PushConfigs.TryGetValue(request.TaskId, out ConcurrentDictionary<string, TaskPushNotificationConfig>? taskConfigs)
                ? taskConfigs.Values.OrderBy(config => config.Id, StringComparer.Ordinal).ToList()
                : new List<TaskPushNotificationConfig>();
            return Task.FromResult(new ListTaskPushNotificationConfigResponse { Configs = configs });
        }

        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            EnsurePushNotificationsSupported();
            if (_PushConfigs.TryGetValue(request.TaskId, out ConcurrentDictionary<string, TaskPushNotificationConfig>? configs))
            {
                configs.TryRemove(request.ConfigId, out _);
            }

            return Task.CompletedTask;
        }

        private AgentCard GetExtendedAgentCard()
        {
            if (AgentCard.Capabilities.ExtendedAgentCard != true)
            {
                throw A2AProtocolException.UnsupportedOperation("Extended Agent Card is not supported.");
            }

            return ExtendedAgentCard ?? throw new A2AProtocolException(A2AErrorCode.ExtendedAgentCardNotConfigured, "Extended Agent Card is not configured.");
        }

        private async Task<A2ARequestContext> ResolveContextAsync(SendMessageRequest request, bool streaming, CancellationToken token)
        {
            if (request == null) throw A2AProtocolException.InvalidParams("SendMessage requires params.");
            if (request.Message == null) throw A2AProtocolException.InvalidParams("SendMessage requires message.");

            AgentTask? existing = null;
            string? taskId = request.Message.TaskId;
            if (!String.IsNullOrWhiteSpace(taskId))
            {
                existing = await _TaskStore.GetTaskAsync(taskId, token).ConfigureAwait(false) ?? throw A2AProtocolException.TaskNotFound(taskId);
                if (IsTerminal(existing.Status.State))
                {
                    throw A2AProtocolException.UnsupportedOperation("Task is terminal and cannot accept new messages.");
                }
            }

            string resolvedTaskId = taskId ?? Guid.NewGuid().ToString("N");
            string resolvedContextId = request.Message.ContextId ?? existing?.ContextId ?? Guid.NewGuid().ToString("N");
            request.Message.TaskId = resolvedTaskId;
            request.Message.ContextId = resolvedContextId;

            return new A2ARequestContext
            {
                SendMessageRequest = request,
                Message = request.Message,
                ExistingTask = existing,
                TaskId = resolvedTaskId,
                ContextId = resolvedContextId,
                StreamingResponse = streaming,
                Configuration = request.Configuration,
                Metadata = request.Metadata
            };
        }

        private async Task ApplyEventAsync(StreamResponse response, A2ARequestContext context, CancellationToken token)
        {
            AgentTask? current = await _TaskStore.GetTaskAsync(context.TaskId, token).ConfigureAwait(false);
            AgentTask? updated = A2ATaskProjection.Apply(current, response, context);
            if (updated != null)
            {
                await _TaskStore.SaveTaskAsync(updated.Id, updated, token).ConfigureAwait(false);
            }

            _Notifier.Notify(context.TaskId, response);
        }

        private void EnsurePushNotificationsSupported()
        {
            if (AgentCard.Capabilities.PushNotifications != true)
            {
                throw new A2AProtocolException(A2AErrorCode.PushNotificationNotSupported, "Push notifications are not supported.");
            }
        }

        private static T DeserializeParams<T>(object? parameters) where T : new()
        {
            if (parameters == null)
            {
                return new T();
            }

            if (parameters is JsonElement element)
            {
                return JsonSerializer.Deserialize<T>(element.GetRawText(), A2AJson.DefaultOptions) ?? new T();
            }

            string json = JsonSerializer.Serialize(parameters, A2AJson.DefaultOptions);
            return JsonSerializer.Deserialize<T>(json, A2AJson.DefaultOptions) ?? new T();
        }

        private static async Task<T> ReadJsonAsync<T>(HttpListenerRequest request) where T : new()
        {
            string body = await ReadBodyAsync(request).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(body))
            {
                return new T();
            }

            return JsonSerializer.Deserialize<T>(body, A2AJson.DefaultOptions) ?? new T();
        }

        private static async Task<CreateTaskPushNotificationConfigRequest> ReadPushNotificationConfigRequestAsync(HttpListenerRequest request)
        {
            string body = await ReadBodyAsync(request).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(body))
            {
                return new CreateTaskPushNotificationConfigRequest();
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("config", out _))
            {
                return JsonSerializer.Deserialize<CreateTaskPushNotificationConfigRequest>(body, A2AJson.DefaultOptions)
                    ?? new CreateTaskPushNotificationConfigRequest();
            }

            PushNotificationConfig? config = JsonSerializer.Deserialize<PushNotificationConfig>(body, A2AJson.DefaultOptions);
            return new CreateTaskPushNotificationConfigRequest
            {
                ConfigId = config?.Id ?? string.Empty,
                Config = config ?? new PushNotificationConfig()
            };
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
        {
            using StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private async Task SendJsonErrorAsync(HttpListenerContext context, Exception ex, object? id, CancellationToken token)
        {
            context.Response.StatusCode = ex is A2AProtocolException protocolException && protocolException.ErrorCode == A2AErrorCode.TaskNotFound ? 404 : 400;
            await SendJsonAsync(context, BuildErrorResponse(ex, id), token).ConfigureAwait(false);
        }

        private static JsonRpcResponse BuildErrorResponse(Exception ex, object? id)
        {
            JsonRpcError error = ex is IJsonRpcErrorProvider provider
                ? provider.ToJsonRpcError()
                : new JsonRpcError { Code = (int)A2AErrorCode.InternalError, Message = "Internal error", Data = ex.Message };

            return new JsonRpcResponse { Id = id, Error = error };
        }

        private async Task SendJsonAsync(HttpListenerContext context, object value, CancellationToken token)
        {
            string json = JsonSerializer.Serialize(value, A2AJson.DefaultOptions);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            context.Response.Close();
        }

        private async Task SendTextAsync(HttpListenerContext context, int statusCode, string text, CancellationToken token)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            context.Response.Close();
        }

        private static async Task SendSseJsonAsync(HttpListenerResponse response, object value, CancellationToken token)
        {
            string json = JsonSerializer.Serialize(value, A2AJson.DefaultOptions);
            byte[] buffer = Encoding.UTF8.GetBytes($"data: {json}\n\n");
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }

        private void AddCors(HttpListenerResponse response)
        {
            if (!EnableCors)
            {
                return;
            }

            foreach (KeyValuePair<string, string> header in CorsHeaders)
            {
                response.Headers[header.Key] = header.Value;
            }
        }

        private static bool IsPublicAgentCardRequest(HttpListenerRequest request)
        {
            return request.HttpMethod == "GET" && StringComparer.OrdinalIgnoreCase.Equals(request.Url?.AbsolutePath, A2AProtocol.AgentCardPath);
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "/";
            return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        }

        private static int? TryParseInt(string? value)
        {
            return Int32.TryParse(value, out int parsed) ? parsed : null;
        }

        private static bool? TryParseBool(string? value)
        {
            return Boolean.TryParse(value, out bool parsed) ? parsed : null;
        }

        private static DateTimeOffset? TryParseDateTimeOffset(string? value)
        {
            return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;
        }

        private static TaskState? TryParseTaskState(string? value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<TaskState>($"\"{value}\"", A2AJson.DefaultOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsTerminal(TaskState state)
        {
            return state == TaskState.Completed ||
                state == TaskState.Failed ||
                state == TaskState.Canceled ||
                state == TaskState.Rejected;
        }

        private void LogMessage(string message)
        {
            Log?.Invoke(this, $"[{DateTime.UtcNow:HH:mm:ss.fffZ}] {message}");
        }
    }

    internal static class A2ATaskProjection
    {
        public static AgentTask? Apply(AgentTask? current, StreamResponse response, A2ARequestContext context)
        {
            if (response.Task != null)
            {
                return response.Task.Clone();
            }

            if (response.Message != null)
            {
                return current;
            }

            if (response.StatusUpdate != null)
            {
                AgentTask task = current ?? new AgentTask
                {
                    Id = response.StatusUpdate.TaskId,
                    ContextId = response.StatusUpdate.ContextId,
                    History = new List<Message>(),
                    Artifacts = new List<Artifact>()
                };

                task.Status = response.StatusUpdate.Status;
                task.Metadata = Merge(task.Metadata, response.StatusUpdate.Metadata);
                if (context.Message != null && (task.History == null || !task.History.Any(message => message.MessageId == context.Message.MessageId)))
                {
                    task.History ??= new List<Message>();
                    task.History.Add(context.Message);
                }

                if (response.StatusUpdate.Status.Message != null)
                {
                    task.History ??= new List<Message>();
                    task.History.Add(response.StatusUpdate.Status.Message);
                }

                return task;
            }

            if (response.ArtifactUpdate != null)
            {
                TaskArtifactUpdateEvent update = response.ArtifactUpdate;
                AgentTask task = current ?? new AgentTask
                {
                    Id = update.TaskId,
                    ContextId = update.ContextId,
                    Status = new TaskStatus { State = TaskState.Working, Timestamp = DateTimeOffset.UtcNow },
                    History = new List<Message>(),
                    Artifacts = new List<Artifact>()
                };

                task.Artifacts ??= new List<Artifact>();
                Artifact? existing = task.Artifacts.FirstOrDefault(artifact => artifact.ArtifactId == update.Artifact.ArtifactId);
                if (existing != null && update.Append)
                {
                    existing.Parts.AddRange(update.Artifact.Parts);
                    existing.Metadata = Merge(existing.Metadata, update.Artifact.Metadata);
                }
                else
                {
                    task.Artifacts.RemoveAll(artifact => artifact.ArtifactId == update.Artifact.ArtifactId);
                    task.Artifacts.Add(update.Artifact);
                }

                task.Metadata = Merge(task.Metadata, update.Metadata);
                return task;
            }

            return current;
        }

        private static Dictionary<string, JsonElement>? Merge(Dictionary<string, JsonElement>? left, Dictionary<string, JsonElement>? right)
        {
            if (right == null || right.Count == 0)
            {
                return left;
            }

            Dictionary<string, JsonElement> merged = left != null
                ? new Dictionary<string, JsonElement>(left, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> item in right)
            {
                merged[item.Key] = item.Value;
            }

            return merged;
        }
    }

    internal sealed class A2ATaskEventNotifier
    {
        private readonly object _Lock = new object();
        private readonly Dictionary<string, List<Channel<StreamResponse>>> _Channels = new Dictionary<string, List<Channel<StreamResponse>>>(StringComparer.Ordinal);

        public Channel<StreamResponse> CreateChannel(string taskId)
        {
            Channel<StreamResponse> channel = Channel.CreateUnbounded<StreamResponse>();
            lock (_Lock)
            {
                if (!_Channels.TryGetValue(taskId, out List<Channel<StreamResponse>>? channels))
                {
                    channels = new List<Channel<StreamResponse>>();
                    _Channels[taskId] = channels;
                }

                channels.Add(channel);
            }

            return channel;
        }

        public void RemoveChannel(string taskId, Channel<StreamResponse> channel)
        {
            lock (_Lock)
            {
                if (_Channels.TryGetValue(taskId, out List<Channel<StreamResponse>>? channels))
                {
                    channels.Remove(channel);
                    if (channels.Count == 0)
                    {
                        _Channels.Remove(taskId);
                    }
                }
            }
        }

        public void Notify(string taskId, StreamResponse response)
        {
            List<Channel<StreamResponse>> channels;
            lock (_Lock)
            {
                if (!_Channels.TryGetValue(taskId, out List<Channel<StreamResponse>>? existing))
                {
                    return;
                }

                channels = existing.ToList();
            }

            foreach (Channel<StreamResponse> channel in channels)
            {
                channel.Writer.TryWrite(response);
            }
        }
    }
}
