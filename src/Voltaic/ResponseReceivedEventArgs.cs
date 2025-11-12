namespace Voltaic
{
    using System;

    /// <summary>
    /// Provides data for response received events on the client.
    /// Contains information about the inbound response including the original request, response, and timing information.
    /// </summary>
    public class ResponseReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the original JSON-RPC request.
        /// </summary>
        public JsonRpcRequest Request { get; }

        /// <summary>
        /// Gets the JSON-RPC response.
        /// </summary>
        public JsonRpcResponse Response { get; }

        /// <summary>
        /// Gets the timestamp when the request was sent (UTC).
        /// </summary>
        public DateTime SentUtc { get; }

        /// <summary>
        /// Gets the timestamp when the response was received (UTC).
        /// </summary>
        public DateTime ReceivedUtc { get; }

        /// <summary>
        /// Gets the round-trip duration from request send to response receipt.
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
        /// Initializes a new instance of the <see cref="ResponseReceivedEventArgs"/> class.
        /// </summary>
        /// <param name="request">The original JSON-RPC request.</param>
        /// <param name="response">The JSON-RPC response.</param>
        /// <param name="sentUtc">The timestamp when the request was sent (UTC).</param>
        public ResponseReceivedEventArgs(JsonRpcRequest request, JsonRpcResponse response, DateTime sentUtc)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Response = response ?? throw new ArgumentNullException(nameof(response));
            SentUtc = sentUtc;
            ReceivedUtc = DateTime.UtcNow;
            Duration = ReceivedUtc - SentUtc;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ResponseReceivedEventArgs"/> class from a pending request.
        /// </summary>
        /// <param name="pendingRequest">The pending request state.</param>
        /// <param name="response">The JSON-RPC response.</param>
        internal ResponseReceivedEventArgs(ClientPendingRequest pendingRequest, JsonRpcResponse response)
        {
            if (pendingRequest == null) throw new ArgumentNullException(nameof(pendingRequest));
            if (response == null) throw new ArgumentNullException(nameof(response));

            Request = pendingRequest.Request;
            Response = response;
            SentUtc = pendingRequest.SentUtc;
            ReceivedUtc = DateTime.UtcNow;
            Duration = ReceivedUtc - SentUtc;

            // Update pending request received time
            pendingRequest.ReceivedUtc = ReceivedUtc;
        }
    }
}
