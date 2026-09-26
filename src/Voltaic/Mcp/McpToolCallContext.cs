namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// Ambient context for the <c>tools/call</c> request currently being handled on this asynchronous flow. It carries
    /// the Multi Round-Trip Request (MRTR) state a client sends when it retries a tool call after the tool returned an
    /// <see cref="McpInputRequiredResult"/>: the <see cref="InputResponses"/> it gathered (keyed like
    /// <see cref="McpInputRequiredResult.InputRequests"/>) and the <see cref="RequestState"/> the tool asked it to echo.
    /// <para>
    /// Every MCP server sets <see cref="Current"/> for the duration of a tool handler and restores the prior value when
    /// the handler returns, on every transport. A handler reads it to decide whether it has the input it asked for:
    /// </para>
    /// <code>
    /// server.RegisterTool("delete_file", "Deletes a file after confirmation", schema, args =>
    /// {
    ///     McpToolCallContext? call = McpToolCallContext.Current;
    ///     string path = args?.GetString("path") ?? "";
    ///     if (call == null || !call.CanRequestInput) return McpToolCallResult.FromText("Confirmation needs MCP 2026-07-28.");
    ///
    ///     // requestState and inputResponses come from the client: act only on an answer to the request this call made,
    ///     // and protect the state's integrity (for example with an HMAC) when more than a match check depends on it.
    ///     if (call.RequestState == "delete:" + path
    ///         &amp;&amp; call.InputResponses.TryGetValue("confirm", out JsonElement answer)
    ///         &amp;&amp; answer.ValueKind == JsonValueKind.Object
    ///         &amp;&amp; answer.TryGetProperty("action", out JsonElement action) &amp;&amp; action.GetString() == "accept")
    ///     {
    ///         File.Delete(path);
    ///         return McpToolCallResult.FromText("Deleted " + path + ".");
    ///     }
    ///
    ///     return new McpInputRequiredResult
    ///     {
    ///         InputRequests = new Dictionary&lt;string, McpInputRequest&gt;
    ///         {
    ///             { "confirm", new McpInputRequest { Method = "elicitation/create", Params = new { mode = "form", message = "Delete " + path + "?", requestedSchema = new { type = "object" } } } }
    ///         },
    ///         RequestState = "delete:" + path
    ///     };
    /// });
    /// </code>
    /// </summary>
    /// <remarks>
    /// Instances are immutable and therefore thread-safe. The ambient value is isolated per asynchronous flow by
    /// <see cref="AsyncLocal{T}"/>, so concurrent tool calls never observe each other's context.
    /// </remarks>
    public sealed class McpToolCallContext
    {
        private static readonly AsyncLocal<McpToolCallContext?> _Current = new AsyncLocal<McpToolCallContext?>();
        private static readonly IReadOnlyDictionary<string, JsonElement> _EmptyResponses = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        private readonly McpRequestScope? _Scope;

        /// <summary>
        /// Gets the context of the tool call being handled on this asynchronous flow, or null outside a tool handler.
        /// </summary>
        public static McpToolCallContext? Current => _Current.Value;

        /// <summary>
        /// Gets the name of the tool being called. Never null or empty.
        /// </summary>
        public string ToolName { get; }

        /// <summary>
        /// Gets the input responses the client sent with this call (<c>params.inputResponses</c>), keyed by the input
        /// request name the tool used. Each value is the raw response JSON, for example an elicitation result such as
        /// <c>{"action":"accept","content":{...}}</c>. Never null; empty on a first call.
        /// </summary>
        public IReadOnlyDictionary<string, JsonElement> InputResponses { get; }

        /// <summary>
        /// Gets the opaque state the client echoed from the tool's <see cref="McpInputRequiredResult.RequestState"/>
        /// (<c>params.requestState</c>), or null when the call carried none. Treat it as untrusted client input.
        /// </summary>
        public string? RequestState { get; }

        /// <summary>
        /// Gets a value indicating whether this call is an MRTR retry: true when it carries input responses or a
        /// request state.
        /// </summary>
        public bool IsRetry => InputResponses.Count > 0 || RequestState != null;

        /// <summary>
        /// Gets a value indicating whether the tool may return an <see cref="McpInputRequiredResult"/> to ask the client
        /// for input: true when the request uses the stateless revision (<c>2026-07-28</c>), where Multi Round-Trip
        /// Requests exist. Handshake-era clients cannot answer input requests; if a handler returns
        /// <see cref="McpInputRequiredResult"/> anyway, the server turns it into a tool result with <c>isError</c> true
        /// that explains the input could not be requested. Check this flag to choose a fallback instead (for example,
        /// accept an explicit argument).
        /// </summary>
        public bool CanRequestInput { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpToolCallContext"/> class. Servers create instances; tool
        /// handlers read <see cref="Current"/>.
        /// </summary>
        /// <param name="toolName">The tool name. Must not be null or empty.</param>
        /// <param name="inputResponses">The input responses, or null for none.</param>
        /// <param name="requestState">The echoed request state, or null.</param>
        /// <param name="canRequestInput">True when the request uses the stateless revision, where the tool may return <see cref="McpInputRequiredResult"/>. Default is false.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="toolName"/> is null or empty.</exception>
        public McpToolCallContext(string toolName, IReadOnlyDictionary<string, JsonElement>? inputResponses, string? requestState, bool canRequestInput = false)
            : this(toolName, inputResponses, requestState, canRequestInput, null)
        {
        }

        internal McpToolCallContext(string toolName, IReadOnlyDictionary<string, JsonElement>? inputResponses, string? requestState, bool canRequestInput, McpRequestScope? scope)
        {
            if (String.IsNullOrEmpty(toolName)) throw new ArgumentNullException(nameof(toolName));
            ToolName = toolName;
            InputResponses = inputResponses ?? _EmptyResponses;
            RequestState = requestState;
            CanRequestInput = canRequestInput;
            _Scope = scope;
        }

        /// <summary>
        /// Gets the progress token the client sent with this call (<c>params._meta.progressToken</c>), or null when it
        /// asked for no progress notifications. <see cref="ReportProgressAsync"/> does nothing without one.
        /// </summary>
        public JsonElement? ProgressToken => _Scope?.Request?.ProgressToken;

        /// <summary>
        /// Sends a <c>notifications/progress</c> for this call to the client that made it, on the connection or
        /// response stream of the call (on HTTP the response becomes an SSE stream). Does nothing when the client sent
        /// no progress token or the transport cannot deliver notifications for the call. Progress must increase with
        /// every notification. Updates are rate-limited by the server's <c>ProgressIntervalMs</c>: one that follows the
        /// previous one sooner is not sent, except the final one (progress equal to the total).
        /// </summary>
        /// <param name="progress">The progress so far. Must be greater than the previous value.</param>
        /// <param name="total">The total, when known, or null.</param>
        /// <param name="message">A human-readable progress message, or null. Clients on 2024-11-05 do not receive it.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the notification was written.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="progress"/> does not increase.</exception>
        public Task ReportProgressAsync(double progress, double? total = null, string? message = null, CancellationToken token = default)
        {
            McpInFlightRequest? request = _Scope?.Request;
            if (request == null || !request.ProgressToken.HasValue || _Scope?.Notify == null) return Task.CompletedTask;

            // Nothing may be sent for a request that was cancelled or has already been answered.
            if (!request.IsActive) return Task.CompletedTask;
            if (!request.TryRecordProgress(progress))
            {
                throw new ArgumentOutOfRangeException(nameof(progress), "Progress must increase with each notification.");
            }

            if (!request.ShouldSendProgress(progress, total)) return Task.CompletedTask;

            JsonRpcRequest notification = new JsonRpcRequest
            {
                Method = "notifications/progress",
                Params = new McpProgressNotification
                {
                    ProgressToken = request.ProgressToken.Value,
                    Progress = progress,
                    Total = total,
                    Message = message
                }
            };

            return _Scope.Notify(notification, token);
        }

        /// <summary>
        /// Sends a <c>notifications/message</c> log entry related to this call. On handshake-era sessions it is sent
        /// when <paramref name="level"/> meets the level the client set with <c>logging/setLevel</c> (everything when it
        /// set none). On <c>2026-07-28</c> requests it is sent only when the request carried
        /// <c>io.modelcontextprotocol/logLevel</c> and the level meets it, as that revision requires. Does nothing when
        /// the transport cannot deliver notifications for the call.
        /// </summary>
        /// <param name="level">One of debug, info, notice, warning, error, critical, alert, emergency.</param>
        /// <param name="data">The JSON-serializable log data.</param>
        /// <param name="logger">An optional logger name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the notification was written or skipped.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="level"/> is not an MCP log level.</exception>
        public Task LogAsync(string level, object? data, string? logger = null, CancellationToken token = default)
        {
            if (!McpLogLevels.IsValid(level)) throw new ArgumentException($"'{level}' is not an MCP log level.", nameof(level));
            if (_Scope?.Notify == null) return Task.CompletedTask;
            if (_Scope.Request != null && !_Scope.Request.IsActive) return Task.CompletedTask;

            string? minimum = _Scope.StatelessVersion != null ? _Scope.RequestLogLevel : _Scope.Session.LogLevel;
            if (_Scope.StatelessVersion != null && minimum == null) return Task.CompletedTask;
            if (!McpLogLevels.Passes(level, minimum)) return Task.CompletedTask;

            JsonRpcRequest notification = new JsonRpcRequest
            {
                Method = "notifications/message",
                Params = new McpLogMessageNotification { Level = level, Logger = logger, Data = data }
            };

            return _Scope.Notify(notification, token);
        }

        /// <summary>
        /// Returns true when the client declared <paramref name="capability"/> (for example <c>elicitation</c>,
        /// <c>sampling</c>, or <c>roots</c>): in <c>initialize</c> for handshake-era sessions, or in the request's
        /// <c>_meta</c> for <c>2026-07-28</c> requests. A dotted path checks a nested capability, for example
        /// <c>elicitation.url</c> or <c>sampling.tools</c>. Tools must not rely on capabilities the client did not declare.
        /// </summary>
        /// <param name="capability">The capability name or dotted path. Must not be null or empty.</param>
        /// <returns>True when declared.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="capability"/> is null or empty.</exception>
        public bool ClientSupports(string capability)
        {
            if (String.IsNullOrEmpty(capability)) throw new ArgumentNullException(nameof(capability));
            return McpClientCapabilityPath.IsDeclared(_Scope?.ClientCapabilities, capability);
        }

        /// <summary>
        /// Sets the ambient context for the current asynchronous flow and returns a token that restores the previous
        /// value when disposed. Servers call this around tool handlers.
        /// </summary>
        /// <param name="context">The context to make ambient, or null to clear it.</param>
        /// <returns>A token that restores the previous context when disposed.</returns>
        public static IDisposable Push(McpToolCallContext? context)
        {
            McpToolCallContext? prior = _Current.Value;
            _Current.Value = context;
            return new Scope(prior);
        }

        private sealed class Scope : IDisposable
        {
            private readonly McpToolCallContext? _Prior;
            private bool _Disposed;

            public Scope(McpToolCallContext? prior)
            {
                _Prior = prior;
            }

            public void Dispose()
            {
                if (_Disposed) return;
                _Disposed = true;
                _Current.Value = _Prior;
            }
        }
    }
}
