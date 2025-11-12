namespace Voltaic
{
    using System;

    /// <summary>
    /// Provides data for JSON-RPC request events.
    /// Contains information about the client connection, the request, and timing information.
    /// </summary>
    public class JsonRpcRequestEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the client connection that sent the request.
        /// </summary>
        public ClientConnection Client { get; }

        /// <summary>
        /// Gets the JSON-RPC request.
        /// </summary>
        public JsonRpcRequest Request { get; }

        /// <summary>
        /// Gets the timestamp when the request was received (UTC).
        /// </summary>
        public DateTime ReceivedUtc { get; }

        /// <summary>
        /// Gets the unique request identifier, if present.
        /// Will be null for notifications (requests without an ID).
        /// </summary>
        public object? RequestId => Request.Id;

        /// <summary>
        /// Gets the method name being invoked.
        /// </summary>
        public string Method => Request.Method;

        /// <summary>
        /// Gets whether this request is a notification (has no ID and expects no response).
        /// </summary>
        public bool IsNotification => Request.Id == null;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcRequestEventArgs"/> class.
        /// </summary>
        /// <param name="client">The client connection that sent the request.</param>
        /// <param name="request">The JSON-RPC request.</param>
        public JsonRpcRequestEventArgs(ClientConnection client, JsonRpcRequest request)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Request = request ?? throw new ArgumentNullException(nameof(request));
            ReceivedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcRequestEventArgs"/> class from a pending request.
        /// </summary>
        /// <param name="pendingRequest">The pending request state.</param>
        internal JsonRpcRequestEventArgs(ServerPendingRequest pendingRequest)
        {
            if (pendingRequest == null) throw new ArgumentNullException(nameof(pendingRequest));

            Client = pendingRequest.Client;
            Request = pendingRequest.Request;
            ReceivedUtc = pendingRequest.ReceivedUtc;
        }
    }
}
