namespace Voltaic
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Tracks the state of an in-flight JSON-RPC request on the client side.
    /// </summary>
    internal class ClientPendingRequest
    {
        /// <summary>
        /// Gets the unique request identifier.
        /// </summary>
        public object RequestId { get; }

        /// <summary>
        /// Gets the JSON-RPC request.
        /// </summary>
        public JsonRpcRequest Request { get; }

        /// <summary>
        /// Gets the timestamp when the request was sent (UTC).
        /// </summary>
        public DateTime SentUtc { get; }

        /// <summary>
        /// Gets the task completion source for awaiting the response.
        /// </summary>
        public TaskCompletionSource<JsonRpcResponse> TaskCompletionSource { get; }

        /// <summary>
        /// Gets or sets the timestamp when the response was received (UTC).
        /// </summary>
        public DateTime? ReceivedUtc { get; set; }

        /// <summary>
        /// Gets the round-trip duration, if the response has been received.
        /// </summary>
        public TimeSpan? Duration => ReceivedUtc.HasValue
            ? ReceivedUtc.Value - SentUtc
            : null;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientPendingRequest"/> class.
        /// </summary>
        /// <param name="requestId">The unique request identifier.</param>
        /// <param name="request">The JSON-RPC request.</param>
        /// <param name="taskCompletionSource">The task completion source for awaiting the response.</param>
        public ClientPendingRequest(object requestId, JsonRpcRequest request, TaskCompletionSource<JsonRpcResponse> taskCompletionSource)
        {
            RequestId = requestId;
            Request = request;
            TaskCompletionSource = taskCompletionSource;
            SentUtc = DateTime.UtcNow;
        }
    }
}
