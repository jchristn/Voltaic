namespace Voltaic.Mcp
{
    using System;
    using System.Net.WebSockets;
    using System.Threading;

    /// <summary>
    /// Represents a WebSocket client connection on the server side.
    /// Encapsulates the WebSocket instance and client metadata.
    /// </summary>
    public class WebSocketConnection : IDisposable
    {
        /// <summary>
        /// Gets the unique identifier for this client connection.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the underlying WebSocket instance.
        /// </summary>
        public WebSocket WebSocket { get; }

        /// <summary>
        /// Gets the cancellation token source for this connection.
        /// </summary>
        public CancellationTokenSource CancellationTokenSource { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketConnection"/> class.
        /// </summary>
        /// <param name="id">The unique identifier for this connection.</param>
        /// <param name="webSocket">The WebSocket instance.</param>
        public WebSocketConnection(string id, WebSocket webSocket)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            Id = id;
            WebSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
            CancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Releases all resources used by the <see cref="WebSocketConnection"/>.
        /// </summary>
        public void Dispose()
        {
            CancellationTokenSource.Cancel();
            CancellationTokenSource.Dispose();
            WebSocket.Dispose();
        }
    }
}
