namespace Voltaic
{
    using System;

    /// <summary>
    /// Provides data for request sent events from the client.
    /// Contains information about the outbound request including the request details and timestamp.
    /// </summary>
    public class RequestSentEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the JSON-RPC request that was sent.
        /// </summary>
        public JsonRpcRequest Request { get; }

        /// <summary>
        /// Gets the timestamp when the request was sent (UTC).
        /// </summary>
        public DateTime SentUtc { get; }

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
        /// Initializes a new instance of the <see cref="RequestSentEventArgs"/> class.
        /// </summary>
        /// <param name="request">The JSON-RPC request that was sent.</param>
        public RequestSentEventArgs(JsonRpcRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            SentUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RequestSentEventArgs"/> class from a pending request.
        /// </summary>
        /// <param name="pendingRequest">The pending request state.</param>
        internal RequestSentEventArgs(ClientPendingRequest pendingRequest)
        {
            if (pendingRequest == null) throw new ArgumentNullException(nameof(pendingRequest));

            Request = pendingRequest.Request;
            SentUtc = pendingRequest.SentUtc;
        }
    }
}
