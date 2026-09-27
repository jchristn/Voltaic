namespace Test.Shared
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A message-level connection to an MCP server on one transport: JSON-RPC text goes out as written and every
    /// message the server sends (responses, notifications, requests) comes back in order of arrival, so one scenario
    /// runs unchanged on stdio, TCP, WebSocket, and Streamable HTTP.
    /// </summary>
    internal interface IMcpRawChannel : IAsyncDisposable
    {
        /// <summary>
        /// Gets the transport name, for messages.
        /// </summary>
        string Transport { get; }

        /// <summary>
        /// Gets or sets the protocol version the channel announces where the transport carries it (the HTTP
        /// <c>MCP-Protocol-Version</c> header), or null to send none.
        /// </summary>
        string? ProtocolVersion { get; set; }

        /// <summary>
        /// Gets or sets whether requests are stateless (2026-07-28): on HTTP, the stateless headers are added.
        /// </summary>
        bool Stateless { get; set; }

        /// <summary>
        /// Sends one message (or batch) without waiting for its answer.
        /// </summary>
        Task SendAsync(string json, CancellationToken token);

        /// <summary>
        /// Returns the next message from the server, or null when none arrives in time.
        /// </summary>
        string? Receive(TimeSpan timeout);
    }
}
