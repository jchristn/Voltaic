namespace Voltaic
{
    using System;

    /// <summary>
    /// Provides data for client connection events.
    /// Contains information about the connection establishment including the connection endpoint and timestamp.
    /// </summary>
    public class ClientConnectedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the timestamp when the connection was established (UTC).
        /// </summary>
        public DateTime ConnectedUtc { get; }

        /// <summary>
        /// Gets the connection endpoint (e.g., "localhost:8080" for TCP, "http://localhost:8080" for HTTP, "ws://localhost:8080" for WebSocket).
        /// </summary>
        public string Endpoint { get; }

        /// <summary>
        /// Gets the connection type identifier (e.g., "TCP", "HTTP", "WebSocket", "Stdio").
        /// </summary>
        public ClientConnectionTypeEnum ConnectionType { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientConnectedEventArgs"/> class.
        /// </summary>
        /// <param name="endpoint">The connection endpoint.</param>
        /// <param name="connectionType">The connection type.</param>
        public ClientConnectedEventArgs(string endpoint, ClientConnectionTypeEnum connectionType)
        {
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            ConnectionType = connectionType;
            ConnectedUtc = DateTime.UtcNow;
        }
    }
}
