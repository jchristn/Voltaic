namespace Voltaic
{
    using System;

    /// <summary>
    /// Provides data for client disconnection events.
    /// Contains information about the disconnection including timestamp, reason, and connection duration.
    /// </summary>
    public class ClientDisconnectedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the timestamp when the connection was established (UTC).
        /// </summary>
        public DateTime ConnectedUtc { get; }

        /// <summary>
        /// Gets the timestamp when the disconnection occurred (UTC).
        /// </summary>
        public DateTime DisconnectedUtc { get; }

        /// <summary>
        /// Gets the total duration the connection was active.
        /// </summary>
        public TimeSpan ConnectionDuration { get; }

        /// <summary>
        /// Gets the reason for disconnection.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Gets the connection endpoint (e.g., "localhost:8080" for TCP, "http://localhost:8080" for HTTP, "ws://localhost:8080" for WebSocket).
        /// </summary>
        public string Endpoint { get; }

        /// <summary>
        /// Gets the connection type identifier (e.g., "TCP", "HTTP", "WebSocket", "Stdio").
        /// </summary>
        public ClientConnectionTypeEnum ConnectionType { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientDisconnectedEventArgs"/> class.
        /// </summary>
        /// <param name="connectedUtc">The timestamp when the connection was established (UTC).</param>
        /// <param name="endpoint">The connection endpoint.</param>
        /// <param name="connectionType">The connection type.</param>
        /// <param name="reason">The reason for disconnection.</param>
        public ClientDisconnectedEventArgs(DateTime connectedUtc, string endpoint, ClientConnectionTypeEnum connectionType, string reason)
        {
            ConnectedUtc = connectedUtc;
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            ConnectionType = connectionType;
            Reason = reason ?? "Unknown";
            DisconnectedUtc = DateTime.UtcNow;
            ConnectionDuration = DisconnectedUtc - ConnectedUtc;
        }
    }
}
