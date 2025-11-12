namespace Voltaic
{
    using System;

    /// <summary>
    /// Provides data for JSON-RPC response events.
    /// Contains information about the client connection, the original request, the response, and timing information.
    /// </summary>
    public class JsonRpcResponseEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the client connection that received the response.
        /// </summary>
        public ClientConnection Client { get; }

        /// <summary>
        /// Gets the original JSON-RPC request.
        /// </summary>
        public JsonRpcRequest Request { get; }

        /// <summary>
        /// Gets the JSON-RPC response.
        /// </summary>
        public JsonRpcResponse Response { get; }

        /// <summary>
        /// Gets the timestamp when the request was received (UTC).
        /// </summary>
        public DateTime ReceivedUtc { get; }

        /// <summary>
        /// Gets the timestamp when the response was sent (UTC).
        /// </summary>
        public DateTime SentUtc { get; }

        /// <summary>
        /// Gets the processing duration from request receipt to response send.
        /// </summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Gets the unique request identifier, if present.
        /// </summary>
        public object? RequestId => Request.Id;

        /// <summary>
        /// Gets the method name that was invoked.
        /// </summary>
        public string Method => Request.Method;

        /// <summary>
        /// Gets whether the response contains an error.
        /// </summary>
        public bool IsError => Response.Error != null;

        /// <summary>
        /// Gets whether the response was successful (contains a result, not an error).
        /// </summary>
        public bool IsSuccess => Response.Error == null;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcResponseEventArgs"/> class.
        /// </summary>
        /// <param name="client">The client connection that received the response.</param>
        /// <param name="request">The original JSON-RPC request.</param>
        /// <param name="response">The JSON-RPC response.</param>
        /// <param name="receivedUtc">The timestamp when the request was received (UTC).</param>
        public JsonRpcResponseEventArgs(ClientConnection client, JsonRpcRequest request, JsonRpcResponse response, DateTime receivedUtc)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Response = response ?? throw new ArgumentNullException(nameof(response));
            ReceivedUtc = receivedUtc;
            SentUtc = DateTime.UtcNow;
            Duration = SentUtc - ReceivedUtc;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcResponseEventArgs"/> class from a pending request.
        /// </summary>
        /// <param name="pendingRequest">The pending request state.</param>
        /// <param name="response">The JSON-RPC response.</param>
        internal JsonRpcResponseEventArgs(ServerPendingRequest pendingRequest, JsonRpcResponse response)
        {
            if (pendingRequest == null) throw new ArgumentNullException(nameof(pendingRequest));
            if (response == null) throw new ArgumentNullException(nameof(response));

            Client = pendingRequest.Client;
            Request = pendingRequest.Request;
            Response = response;
            ReceivedUtc = pendingRequest.ReceivedUtc;
            SentUtc = DateTime.UtcNow;
            Duration = SentUtc - ReceivedUtc;

            // Update pending request completion time
            pendingRequest.CompletedUtc = SentUtc;
        }
    }
}
