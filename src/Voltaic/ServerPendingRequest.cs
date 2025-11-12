namespace Voltaic
{
    using System;

    /// <summary>
    /// Tracks the state of an in-flight JSON-RPC request on the server side.
    /// </summary>
    internal class ServerPendingRequest
    {
        /// <summary>
        /// Gets the unique request identifier.
        /// </summary>
        public object? RequestId { get; }

        /// <summary>
        /// Gets the client connection for this request.
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
        /// Gets or sets the timestamp when processing started (UTC).
        /// </summary>
        public DateTime? ProcessingStartedUtc { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the response was sent (UTC).
        /// </summary>
        public DateTime? CompletedUtc { get; set; }

        /// <summary>
        /// Gets the processing duration, if completed.
        /// </summary>
        public TimeSpan? Duration => CompletedUtc.HasValue
            ? CompletedUtc.Value - ReceivedUtc
            : null;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServerPendingRequest"/> class.
        /// </summary>
        /// <param name="requestId">The unique request identifier.</param>
        /// <param name="client">The client connection.</param>
        /// <param name="request">The JSON-RPC request.</param>
        public ServerPendingRequest(object? requestId, ClientConnection client, JsonRpcRequest request)
        {
            RequestId = requestId;
            Client = client;
            Request = request;
            ReceivedUtc = DateTime.UtcNow;
        }
    }
}
