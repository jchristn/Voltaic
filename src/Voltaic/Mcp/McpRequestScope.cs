namespace Voltaic.Mcp
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Voltaic.Core;

    /// <summary>
    /// The ambient context of the MCP request being processed: its session, its in-flight entry, the stateless-era
    /// version and per-request log level when it has them, its client capabilities, and the channel for
    /// notifications related to it (progress, log messages). Flows with the async call.
    /// </summary>
    internal sealed class McpRequestScope
    {
        private static readonly AsyncLocal<McpRequestScope?> _Current = new AsyncLocal<McpRequestScope?>();

        internal McpRequestScope(
            McpSessionState session,
            McpInFlightRequest? request,
            string? statelessVersion,
            string? requestLogLevel,
            JsonElement? statelessCapabilities,
            Func<JsonRpcRequest, CancellationToken, Task>? notify)
        {
            Session = session;
            Request = request;
            StatelessVersion = statelessVersion;
            RequestLogLevel = requestLogLevel;
            StatelessCapabilities = statelessCapabilities;
            Notify = notify;
        }

        /// <summary>
        /// Gets the current scope, or null outside MCP request processing.
        /// </summary>
        internal static McpRequestScope? Current => _Current.Value;

        internal McpSessionState Session { get; }

        internal McpInFlightRequest? Request { get; }

        /// <summary>
        /// Gets the stateless-era version the request named in <c>_meta</c>, or null for a handshake-era request.
        /// </summary>
        internal string? StatelessVersion { get; }

        /// <summary>
        /// Gets the <c>io.modelcontextprotocol/logLevel</c> of a stateless-era request, or null.
        /// </summary>
        internal string? RequestLogLevel { get; }

        /// <summary>
        /// Gets the client capabilities a stateless-era request declared in <c>_meta</c>.
        /// </summary>
        internal JsonElement? StatelessCapabilities { get; }

        /// <summary>
        /// Gets the channel for notifications related to this request, or null when there is none.
        /// </summary>
        internal Func<JsonRpcRequest, CancellationToken, Task>? Notify { get; }

        /// <summary>
        /// Gets or sets the scope a handler reported missing, or null.
        /// </summary>
        internal string? InsufficientScope { get; set; }

        /// <summary>
        /// Gets the protocol version governing this request: the stateless version, or the session's negotiated one.
        /// </summary>
        internal string? ProtocolVersion => StatelessVersion ?? Session.NegotiatedVersion;

        /// <summary>
        /// Gets the client capabilities in effect for this request.
        /// </summary>
        internal JsonElement? ClientCapabilities => StatelessVersion != null ? StatelessCapabilities : Session.ClientCapabilities;

        /// <summary>
        /// Makes <paramref name="scope"/> current until the returned value is disposed.
        /// </summary>
        internal static IDisposable Push(McpRequestScope? scope)
        {
            McpRequestScope? previous = _Current.Value;
            _Current.Value = scope;
            return new Restore(previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly McpRequestScope? _Previous;
            private bool _Disposed;

            internal Restore(McpRequestScope? previous)
            {
                _Previous = previous;
            }

            public void Dispose()
            {
                if (_Disposed) return;
                _Disposed = true;
                _Current.Value = _Previous;
            }
        }
    }
}
