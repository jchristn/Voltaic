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
    /// Direct A2A gRPC server for the <c>lf.a2a.v1.A2AService</c> service, hosted on WatsonWebserver with HTTP/1.1,
    /// HTTP/2, and cleartext HTTP/2 (h2c) enabled, using gRPC length-prefixed protobuf framing.
    /// <para>
    /// gRPC calls are <c>POST</c> requests under <c>/lf.a2a.v1.A2AService/</c> and require HTTP/2 (other protocols get
    /// HTTP 426). When <see cref="EnableAgentCardEndpoints"/> is true, <c>GET</c> <see cref="A2AProtocol.AgentCardPath"/>
    /// and <c>GET</c> <see cref="A2AProtocol.ExtendedAgentCardPath"/> are also served as JSON. Task state, push
    /// notification configurations, and handler execution are shared with an internal <see cref="A2AHttpServer"/>
    /// (which is never started), so results match the JSON-RPC and HTTP+JSON bindings.
    /// </para>
    /// <para>
    /// Security: requests are gated in order by <see cref="RestrictToLoopbackClients"/> (on by default for a loopback
    /// host name), <see cref="OriginPolicy"/>, and <see cref="AuthenticationHandler"/>, as on <see cref="A2AHttpServer"/>.
    /// Only the public Agent Card is served without authentication. Errors other than A2A protocol errors are reported
    /// to clients as a generic <c>grpc-message</c>, and the details are logged through <see cref="Log"/>. A host name
    /// of <c>*</c> or <c>+</c> listens on all interfaces (<c>0.0.0.0</c>, plus <c>::</c> when IPv6 is available).
    /// </para>
    /// <para>
    /// Loopback: a host name of <c>localhost</c> listens on both <c>127.0.0.1</c> and <c>::1</c> (the IPv6 listener is
    /// best effort and skipped when it cannot bind). Clients such as <see cref="System.Net.Http.HttpClient"/> resolve
    /// <c>localhost</c> to <c>::1</c> first; without an IPv6 listener each new connection would wait for the refused
    /// IPv6 attempt (about two seconds on Windows) before falling back to IPv4. An explicit address such as
    /// <c>127.0.0.1</c> listens on that address only, so clients should connect to that same address.
    /// </para>
    /// Configure properties and register the handler before calling <see cref="StartAsync"/>. Requests are handled
    /// concurrently; the public members are not synchronized against concurrent reconfiguration.
    /// </summary>
    public sealed class A2AGrpcServer : IDisposable
    {
        private readonly string _Hostname;
        private readonly int _Port;
        private readonly A2AHttpServer _Endpoint;
        private readonly List<Webserver> _Servers = new List<Webserver>();
        private CancellationTokenRegistration _StopRegistration;
        private bool _IsDisposed;
        private OriginPolicy _OriginPolicy = new OriginPolicy();
        private readonly object _LifecycleLock = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="A2AGrpcServer"/> class. The server does not listen until
        /// <see cref="StartAsync"/> is called.
        /// </summary>
        /// <param name="hostname">The host name or address to listen on. <c>*</c> or <c>+</c> listens on all interfaces. Must not be null, empty, or whitespace.</param>
        /// <param name="port">The TCP port to listen on. Minimum 0, maximum 65535.</param>
        /// <param name="agentCard">The public Agent Card served at <see cref="A2AProtocol.AgentCardPath"/>. Must not be null.</param>
        /// <param name="handler">The agent handler that executes messages. Null (the default) means no handler yet; register one with <see cref="RegisterHandler"/> before messages are sent.</param>
        /// <param name="taskStore">The task store. Null (the default) uses a new <see cref="InMemoryA2ATaskStore"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="hostname"/> is null, empty, or whitespace, or <paramref name="agentCard"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="port"/> is outside 0 to 65535.</exception>
        public A2AGrpcServer(string hostname, int port, AgentCard agentCard, IA2AAgentHandler? handler = null, IA2ATaskStore? taskStore = null)
        {
            if (String.IsNullOrWhiteSpace(hostname)) throw new ArgumentNullException(nameof(hostname));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            _Hostname = hostname;
            RestrictToLoopbackClients = LoopbackAddresses.IsLoopbackHostname(hostname);
            _Port = port;
            _Endpoint = new A2AHttpServer(hostname, port, agentCard, handler, taskStore);
        }

        /// <summary>
        /// Gets or sets the public Agent Card, served without authentication at <see cref="A2AProtocol.AgentCardPath"/>
        /// when <see cref="EnableAgentCardEndpoints"/> is true. Its <c>Capabilities.ExtendedAgentCard</c> value controls
        /// whether <see cref="ExtendedAgentCard"/> is offered.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when set to null.</exception>
        public AgentCard AgentCard
        {
            get => _Endpoint.AgentCard;
            set => _Endpoint.AgentCard = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Gets or sets the extended Agent Card returned by <c>GetExtendedAgentCard</c> and <c>GET</c>
        /// <see cref="A2AProtocol.ExtendedAgentCardPath"/>. It is returned only when <see cref="AgentCard"/> advertises
        /// <c>Capabilities.ExtendedAgentCard = true</c>; otherwise callers get an unsupported-operation error, and when
        /// advertised but null they get <see cref="A2AErrorCode.ExtendedAgentCardNotConfigured"/>. Default is null.
        /// Both <c>GET</c> <see cref="A2AProtocol.ExtendedAgentCardPath"/> and the gRPC <c>GetExtendedAgentCard</c> call
        /// require <see cref="AuthenticationHandler"/> to succeed when one is configured.
        /// </summary>
        public AgentCard? ExtendedAgentCard
        {
            get => _Endpoint.ExtendedAgentCard;
            set => _Endpoint.ExtendedAgentCard = value;
        }

        /// <summary>
        /// Gets the registered agent handler, or null when none is registered.
        /// </summary>
        public IA2AAgentHandler? Handler => _Endpoint.Handler;

        /// <summary>
        /// Gets or sets an optional asynchronous authentication handler. It receives the WatsonWebserver
        /// <see cref="HttpContextBase"/> for every request that passes the loopback and origin checks, except
        /// <c>GET</c> <see cref="A2AProtocol.AgentCardPath"/> (the public card). When it returns
        /// <see cref="AuthenticationResult.IsAuthenticated"/> false, the server responds with
        /// <see cref="AuthenticationResult.StatusCode"/> and <see cref="AuthenticationResult.ErrorMessage"/> (or
        /// <c>Unauthorized</c>) as <c>text/plain</c>, plus <see cref="AuthenticationResult.Headers"/> (for example
        /// <c>WWW-Authenticate</c>). On success, <see cref="AuthenticationResult.Principal"/> and <see cref="AuthenticationResult.Claims"/>
        /// are copied onto the <see cref="A2ARequestContext"/> the agent handler receives. Default is null, which
        /// performs no authentication.
        /// </summary>
        public Func<HttpContextBase, Task<AuthenticationResult>>? AuthenticationHandler { get; set; }

        /// <summary>
        /// Gets or sets the policy that decides which browser origins may call the server. A request whose
        /// <c>Origin</c> header the policy rejects receives HTTP 403 before authentication, including requests for the
        /// Agent Card endpoints. Requests without an <c>Origin</c> header (gRPC clients, SDKs) and loopback origins
        /// are allowed by default. Setting null restores the default policy. Configure before <see cref="StartAsync"/>.
        /// </summary>
        public OriginPolicy OriginPolicy
        {
            get => _OriginPolicy;
            set => _OriginPolicy = value ?? new OriginPolicy();
        }

        /// <summary>
        /// Gets or sets whether only loopback clients are served. When true, a request whose source address is not
        /// loopback (<c>127.0.0.0/8</c>, <c>::1</c>, or an IPv4-mapped loopback address) receives HTTP 403. Default is
        /// true when the server is constructed with a loopback host name (<c>localhost</c>, <c>127.x.x.x</c>, or
        /// <c>::1</c>) and false otherwise. Bind to <c>*</c>, <c>+</c>, or a specific address to serve remote clients,
        /// or set this to false explicitly.
        /// </summary>
        public bool RestrictToLoopbackClients { get; set; }

        /// <summary>
        /// Gets or sets the push notification webhook URL validator. See <see cref="A2AHttpServer.PushNotificationUrlValidator"/>;
        /// push notifications are delivered by the task engine this server shares with an internal <see cref="A2AHttpServer"/>.
        /// Default is null, which applies the built-in protection against loopback, private, and link-local targets.
        /// </summary>
        public Func<Uri, bool>? PushNotificationUrlValidator
        {
            get => _Endpoint.PushNotificationUrlValidator;
            set => _Endpoint.PushNotificationUrlValidator = value;
        }

        /// <summary>
        /// Gets or sets the timeout for each push notification request, in milliseconds. See
        /// <see cref="A2AHttpServer.PushNotificationTimeoutMs"/>. Default is 10000; minimum 1000, maximum 300000.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside 1000 to 300000.</exception>
        public int PushNotificationTimeoutMs
        {
            get => _Endpoint.PushNotificationTimeoutMs;
            set => _Endpoint.PushNotificationTimeoutMs = value;
        }

        /// <summary>
        /// Gets or sets how many times a push notification is attempted. See <see cref="A2AHttpServer.PushNotificationMaxAttempts"/>.
        /// Default is 3; minimum 1, maximum 10.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside 1 to 10.</exception>
        public int PushNotificationMaxAttempts
        {
            get => _Endpoint.PushNotificationMaxAttempts;
            set => _Endpoint.PushNotificationMaxAttempts = value;
        }

        /// <summary>
        /// Gets or sets whether the JSON Agent Card endpoints (<c>GET</c> <see cref="A2AProtocol.AgentCardPath"/> and
        /// <c>GET</c> <see cref="A2AProtocol.ExtendedAgentCardPath"/>) are served alongside gRPC. Default is true. When false,
        /// those paths return HTTP 404.
        /// </summary>
        public bool EnableAgentCardEndpoints { get; set; } = true;

        /// <summary>
        /// Occurs when the server logs a diagnostic message, such as a request error. Handlers are invoked on a thread-pool
        /// thread, and exceptions they throw are ignored.
        /// </summary>
        public event EventHandler<string>? Log;

        /// <summary>
        /// Registers or replaces the agent handler that executes messages.
        /// </summary>
        /// <param name="handler">The agent handler. Must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> is null.</exception>
        public void RegisterHandler(IA2AAgentHandler handler)
        {
            _Endpoint.RegisterHandler(handler);
        }

        /// <summary>
        /// Sends a message to the agent handler and waits for the result. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <param name="request">The message and optional configuration to send.</param>
        /// <param name="streaming">True to process the message as the streaming variant would; false for <c>SendMessage</c> semantics.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The agent's response: either a task or a direct message.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, bool streaming, CancellationToken token = default)
        {
            return _Endpoint.SendMessageAsync(request, streaming, token);
        }

        /// <summary>
        /// Sends a message to the agent handler and streams the resulting events. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <remarks>The operation starts when enumeration starts; events are yielded as the agent handler produces them.</remarks>
        /// <param name="request">The message and optional configuration to send.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of stream events.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken token = default)
        {
            return _Endpoint.SendStreamingMessageAsync(request, token);
        }

        /// <summary>
        /// Gets a task by ID. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <param name="request">The task ID and optional history length.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The task.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken token = default)
        {
            return _Endpoint.GetTaskAsync(request, token);
        }

        /// <summary>
        /// Lists tasks, optionally filtered and paged. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <param name="request">The filters and paging options.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A page of tasks and the token for the next page.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken token = default)
        {
            return _Endpoint.ListTasksAsync(request, token);
        }

        /// <summary>
        /// Requests cancellation of a task. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <param name="request">The task ID and optional metadata.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The task in its updated state.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken token = default)
        {
            return _Endpoint.CancelTaskAsync(request, token);
        }

        /// <summary>
        /// Subscribes to updates for an existing task and streams its events. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <remarks>The operation starts when enumeration starts; events are yielded as the agent handler produces them.</remarks>
        /// <param name="request">The ID of the task to subscribe to.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of stream events.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken token = default)
        {
            return _Endpoint.SubscribeToTaskAsync(request, token);
        }

        /// <summary>
        /// Creates a push notification configuration for a task. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <param name="request">The task ID and the configuration to create.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The stored configuration, including its assigned ID.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return _Endpoint.CreateTaskPushNotificationConfigAsync(request, token);
        }

        /// <summary>
        /// Gets a task's push notification configuration. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <param name="request">The task ID and configuration ID.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The configuration.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return _Endpoint.GetTaskPushNotificationConfigAsync(request, token);
        }

        /// <summary>
        /// Lists a task's push notification configurations. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <param name="request">The task ID (and optional tenant).</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>The configurations.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return _Endpoint.ListTaskPushNotificationConfigAsync(request, token);
        }

        /// <summary>
        /// Deletes a task's push notification configuration. This is an in-process call: it runs the same logic a gRPC client reaches, without the network,
        /// the <see cref="AuthenticationHandler"/>, or a caller context.
        /// </summary>
        /// <param name="request">The task ID and configuration ID.</param>
        /// <param name="token">Cancellation token for the operation.</param>
        /// <returns>A task that completes when the configuration is deleted.</returns>
        /// <exception cref="A2AProtocolException">Thrown for A2A protocol failures, for example an unknown task (<see cref="A2AErrorCode.TaskNotFound"/>), an operation the task's state does not allow, or no registered handler (<see cref="A2AErrorCode.InternalError"/>).</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is cancelled.</exception>
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken token = default)
        {
            return _Endpoint.DeleteTaskPushNotificationConfigAsync(request, token);
        }

        /// <summary>
        /// Starts listening. The method returns once the listener has started; requests are then served in the background
        /// until <see cref="Stop"/>, <see cref="Dispose"/>, or cancellation of <paramref name="token"/>.
        /// </summary>
        /// <param name="token">Cancellation token that stops the server when cancelled.</param>
        /// <returns>A task that completes when the listener has started.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the server is already running.</exception>
        /// <exception cref="ObjectDisposedException">Thrown when the server has been disposed.</exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="token"/> is already cancelled.</exception>
        public async Task StartAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (_LifecycleLock)
            {
                if (_IsDisposed) throw new ObjectDisposedException(nameof(A2AGrpcServer));
                if (_Servers.Count > 0)
                {
                    throw new InvalidOperationException("Server is already started.");
                }

                Webserver primary = new Webserver(CreateSettings(NormalizeListenHostname(_Hostname)), HandleRequestAsync);
                primary.Start();
                _Servers.Add(primary);

                string? ipv6Hostname = SecondaryIpv6Hostname(_Hostname, _Port);
                if (ipv6Hostname != null)
                {
                    Webserver? secondary = null;
                    try
                    {
                        secondary = new Webserver(CreateSettings(ipv6Hostname), HandleRequestAsync);
                        secondary.Start();
                        _Servers.Add(secondary);
                    }
                    catch (Exception e)
                    {
                        // IPv6 may be disabled or the port taken on ::1; IPv4 still serves the host name.
                        try
                        {
                            secondary?.Dispose();
                        }
                        catch
                        {
                        }

                        LogMessage($"IPv6 listener on [{ipv6Hostname}]:{_Port} not started: {e.Message}");
                    }
                }

                // Watson is stopped through its own Stop rather than a cancelled start token: when the token cancels
                // first, its accept loop exits and marks itself stopped, and the listening socket is never closed.
                _StopRegistration = token.CanBeCanceled ? token.Register(Stop) : default;
            }

            await Task.Yield();
        }

        /// <summary>
        /// Stops listening and cancels in-flight requests. Errors raised while stopping are ignored. The server can be
        /// started again with <see cref="StartAsync"/>. Safe to call more than once.
        /// </summary>
        public void Stop()
        {
            List<Webserver> servers;
            CancellationTokenRegistration registration;
            lock (_LifecycleLock)
            {
                servers = new List<Webserver>(_Servers);
                registration = _StopRegistration;
                _Servers.Clear();
                _StopRegistration = default;
            }

            registration.Unregister();

            foreach (Webserver server in servers)
            {
                try
                {
                    server.Stop();
                }
                catch
                {
                }

                try
                {
                    server.Dispose();
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Stops the server and releases its resources, including the internal endpoint. Calling this more than once has
        /// no effect.
        /// </summary>
        public void Dispose()
        {
            if (_IsDisposed)
            {
                return;
            }

            Stop();
            _IsDisposed = true;
            _Endpoint.Dispose();
        }

        private async Task HandleRequestAsync(HttpContextBase context)
        {
            try
            {
                string path = context.Request.Url.RawWithoutQuery ?? "/";

                if (RestrictToLoopbackClients && !IsLoopbackSource(context))
                {
                    LogMessage($"Rejected remote client {context.Request.Source?.IpAddress}: the server accepts loopback clients only");
                    await SendTextAsync(context, 403, "Remote connections are not allowed.", context.Token).ConfigureAwait(false);
                    return;
                }

                string? origin = context.Request.RetrieveHeaderValue("Origin");
                if (!_OriginPolicy.IsAllowed(String.IsNullOrEmpty(origin) ? null : origin))
                {
                    LogMessage($"Rejected request from disallowed origin '{origin}'");
                    await SendTextAsync(context, 403, "Origin not allowed.", context.Token).ConfigureAwait(false);
                    return;
                }

                AuthenticationResult? caller = null;
                if (AuthenticationHandler != null && !IsPublicAgentCardRequest(context, path))
                {
                    AuthenticationResult auth = await AuthenticationHandler(context).ConfigureAwait(false);
                    if (!auth.IsAuthenticated)
                    {
                        foreach (KeyValuePair<string, string> header in auth.Headers)
                        {
                            if (String.IsNullOrEmpty(header.Key) || header.Value == null) continue;
                            context.Response.Headers.Set(header.Key, header.Value);
                        }

                        await SendTextAsync(context, auth.StatusCode, auth.ErrorMessage ?? "Unauthorized", context.Token).ConfigureAwait(false);
                        return;
                    }

                    caller = auth;
                }

                // Make the authenticated caller ambient so the A2AHttpServer endpoint this server delegates
                // to copies it onto the A2ARequestContext handed to the agent handler.
                using (A2ACallerContext.Push(caller))
                {
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
            }
            catch (Exception ex)
            {
                LogMessage($"A2A gRPC request error: {ex}");
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
                LogMessage($"A2A gRPC stream error: {ex}");
                SetGrpcStatus(context, A2AGrpcWire.ToGrpcStatus(ex), ClientErrorMessage(ex));
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
            SetGrpcStatus(context, A2AGrpcWire.ToGrpcStatus(ex), ClientErrorMessage(ex));
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
            // Only the public card is exempt. The extended card is for authenticated clients, as on A2AHttpServer.
            return context.Request.Method == WatsonHttpMethod.GET &&
                StringComparer.OrdinalIgnoreCase.Equals(path, A2AProtocol.AgentCardPath);
        }

        private static bool IsLoopbackSource(HttpContextBase context)
        {
            string? address = context.Request.Source?.IpAddress;
            return !String.IsNullOrEmpty(address)
                && System.Net.IPAddress.TryParse(address, out System.Net.IPAddress? parsed)
                && LoopbackAddresses.IsLoopback(parsed);
        }

        /// <summary>
        /// Returns the message sent to the client in <c>grpc-message</c>. Protocol errors carry their own message;
        /// anything else (handler exceptions, internal failures) is reduced to a generic message so internal details
        /// never reach the caller. The full exception is logged.
        /// </summary>
        private static string ClientErrorMessage(Exception ex)
        {
            if (ex is A2AProtocolException) return ex.Message;
            if (ex is InvalidProtocolBufferException) return "Invalid request message.";
            if (ex is OperationCanceledException) return "Request cancelled.";
            return "Internal error.";
        }

        private static string NormalizeListenHostname(string hostname)
        {
            if (hostname == "*" || hostname == "+") return "0.0.0.0";
            return String.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : hostname;
        }

        // The IPv6 companion listener for host names that clients resolve to both address families. Port 0 is skipped
        // because each listener would pick a different ephemeral port.
        private static string? SecondaryIpv6Hostname(string hostname, int port)
        {
            if (port == 0) return null;
            if (hostname == "*" || hostname == "+") return "::";
            return String.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase) ? "::1" : null;
        }

        private WebserverSettings CreateSettings(string hostname)
        {
            WebserverSettings settings = new WebserverSettings(hostname, _Port, false);
            settings.Protocols.EnableHttp1 = true;
            settings.Protocols.EnableHttp2 = true;
            settings.Protocols.EnableHttp2Cleartext = true;
            settings.Protocols.EnableHttp3 = false;
            return settings;
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
