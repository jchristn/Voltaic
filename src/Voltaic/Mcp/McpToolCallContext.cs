namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;

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
    ///     if (call == null || !call.CanRequestInput) return McpToolCallResult.FromText("Pass confirm=true to delete.");
    ///     if (call.InputResponses.TryGetValue("confirm", out JsonElement answer)
    ///         &amp;&amp; answer.TryGetProperty("action", out JsonElement action) &amp;&amp; action.GetString() == "accept")
    ///     {
    ///         return McpToolCallResult.FromText("Deleted.");
    ///     }
    ///
    ///     return new McpInputRequiredResult
    ///     {
    ///         InputRequests = new Dictionary&lt;string, McpInputRequest&gt;
    ///         {
    ///             { "confirm", new McpInputRequest { Method = "elicitation/create", Params = new { mode = "form", message = "Delete the file?", requestedSchema = new { type = "object" } } } }
    ///         },
    ///         RequestState = "delete-1"
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
        {
            if (String.IsNullOrEmpty(toolName)) throw new ArgumentNullException(nameof(toolName));
            ToolName = toolName;
            InputResponses = inputResponses ?? _EmptyResponses;
            RequestState = requestState;
            CanRequestInput = canRequestInput;
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
