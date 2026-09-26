namespace Voltaic.A2A
{
    using System;
    using Voltaic.Core;

    /// <summary>
    /// Standard A2A JSON-RPC error codes: the A2A-specific codes (<c>-32001</c> to <c>-32009</c>) and the
    /// standard JSON-RPC codes. The numeric value is the JSON-RPC <c>error.code</c>.
    /// </summary>
    public enum A2AErrorCode
    {
        /// <summary>
        /// The task ID does not exist or is not visible to the caller (<c>-32001</c>).
        /// </summary>
        TaskNotFound = -32001,
        /// <summary>
        /// The task cannot be canceled, for example because it is already terminal (<c>-32002</c>).
        /// </summary>
        TaskNotCancelable = -32002,
        /// <summary>
        /// The agent does not support push notifications (<c>-32003</c>).
        /// </summary>
        PushNotificationNotSupported = -32003,
        /// <summary>
        /// The requested operation is not supported by the agent, or not valid in the task's current state (<c>-32004</c>).
        /// </summary>
        UnsupportedOperation = -32004,
        /// <summary>
        /// A message part uses a media type the agent does not accept (<c>-32005</c>).
        /// </summary>
        ContentTypeNotSupported = -32005,
        /// <summary>
        /// The agent produced no response or an invalid one (<c>-32006</c>).
        /// </summary>
        InvalidAgentResponse = -32006,
        /// <summary>
        /// The Agent Card advertises an extended card but none is configured (<c>-32007</c>).
        /// </summary>
        ExtendedAgentCardNotConfigured = -32007,
        /// <summary>
        /// The request requires an A2A extension the client did not declare (<c>-32008</c>).
        /// </summary>
        ExtensionSupportRequired = -32008,
        /// <summary>
        /// The requested A2A protocol version is not supported (<c>-32009</c>).
        /// </summary>
        VersionNotSupported = -32009,
        /// <summary>
        /// Standard JSON-RPC invalid request (<c>-32600</c>).
        /// </summary>
        InvalidRequest = -32600,
        /// <summary>
        /// Standard JSON-RPC method not found (<c>-32601</c>).
        /// </summary>
        MethodNotFound = -32601,
        /// <summary>
        /// Standard JSON-RPC invalid params (<c>-32602</c>).
        /// </summary>
        InvalidParams = -32602,
        /// <summary>
        /// Standard JSON-RPC internal error (<c>-32603</c>).
        /// </summary>
        InternalError = -32603,
        /// <summary>
        /// Standard JSON-RPC parse error (<c>-32700</c>).
        /// </summary>
        ParseError = -32700
    }

    /// <summary>
    /// Represents an A2A protocol error that should be surfaced to the client. The A2A servers convert it to a
    /// JSON-RPC error (or the matching HTTP+JSON or gRPC error), and the A2A clients throw it when the server
    /// returns an error.
    /// </summary>
    public class A2AProtocolException : Exception, IJsonRpcErrorProvider
    {
        /// <summary>
        /// Gets the A2A error code; its numeric value is the JSON-RPC <c>error.code</c>.
        /// </summary>
        public A2AErrorCode ErrorCode { get; }

        /// <summary>
        /// Gets optional structured error data, sent as JSON-RPC <c>error.data</c>. Null when there is none.
        /// </summary>
        public object? ErrorData { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="A2AProtocolException"/> class.
        /// </summary>
        /// <param name="code">The A2A error code.</param>
        /// <param name="message">A human-readable error message, sent as JSON-RPC <c>error.message</c>.</param>
        /// <param name="data">Optional structured error data, sent as JSON-RPC <c>error.data</c>. Default is null.</param>
        public A2AProtocolException(A2AErrorCode code, string message, object? data = null)
            : base(message)
        {
            ErrorCode = code;
            ErrorData = data;
        }

        /// <summary>
        /// Converts the exception to a JSON-RPC error object with the same code, message, and data.
        /// </summary>
        /// <returns>A new <see cref="JsonRpcError"/>. Never null.</returns>
        public JsonRpcError ToJsonRpcError()
        {
            return new JsonRpcError
            {
                Code = (int)ErrorCode,
                Message = Message,
                Data = ErrorData
            };
        }

        /// <summary>
        /// Creates an invalid-params error (<see cref="A2AErrorCode.InvalidParams"/>, <c>-32602</c>).
        /// </summary>
        /// <param name="message">A human-readable description of the invalid parameter.</param>
        /// <param name="data">Optional structured error data. Default is null.</param>
        /// <returns>A new exception. Never null.</returns>
        public static A2AProtocolException InvalidParams(string message, object? data = null)
        {
            return new A2AProtocolException(A2AErrorCode.InvalidParams, message, data);
        }

        /// <summary>
        /// Creates a task-not-found error (<see cref="A2AErrorCode.TaskNotFound"/>, <c>-32001</c>) whose data
        /// carries the task ID.
        /// </summary>
        /// <param name="taskId">The task ID that was not found.</param>
        /// <returns>A new exception. Never null.</returns>
        public static A2AProtocolException TaskNotFound(string taskId)
        {
            return new A2AProtocolException(A2AErrorCode.TaskNotFound, $"Task '{taskId}' was not found.", new { taskId });
        }

        /// <summary>
        /// Creates an unsupported-operation error (<see cref="A2AErrorCode.UnsupportedOperation"/>, <c>-32004</c>).
        /// </summary>
        /// <param name="message">A human-readable description of why the operation is unsupported.</param>
        /// <returns>A new exception. Never null.</returns>
        public static A2AProtocolException UnsupportedOperation(string message)
        {
            return new A2AProtocolException(A2AErrorCode.UnsupportedOperation, message);
        }
    }
}
