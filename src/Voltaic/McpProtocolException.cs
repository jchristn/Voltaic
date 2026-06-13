namespace Voltaic
{
    using System;

    /// <summary>
    /// Represents an MCP protocol error that should be surfaced as a JSON-RPC error response.
    /// </summary>
    public class McpProtocolException : Exception
    {
        /// <summary>
        /// Gets the JSON-RPC error code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets optional JSON-RPC error data.
        /// </summary>
        public object? ErrorData { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpProtocolException"/> class.
        /// </summary>
        /// <param name="code">JSON-RPC error code.</param>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        public McpProtocolException(int code, string message, object? data = null)
            : base(message)
        {
            Code = code;
            ErrorData = data;
        }

        /// <summary>
        /// Creates an invalid params error.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException InvalidParams(string message, object? data = null)
        {
            return new McpProtocolException(-32602, message, data);
        }

        /// <summary>
        /// Creates a validation error.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException ValidationError(string message, object? data = null)
        {
            return InvalidParams(message, data);
        }

        /// <summary>
        /// Creates an unsupported protocol version error.
        /// </summary>
        /// <param name="version">Unsupported version.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException UnsupportedVersion(string version)
        {
            return InvalidParams($"Unsupported MCP protocol version '{version}'.", new { protocolVersion = version });
        }

        /// <summary>
        /// Creates an invalid cursor error.
        /// </summary>
        /// <param name="cursor">Invalid cursor value.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException InvalidCursor(string cursor)
        {
            return InvalidParams($"Invalid cursor '{cursor}'.", new { cursor });
        }

        /// <summary>
        /// Creates an invalid session error.
        /// </summary>
        /// <param name="sessionId">Invalid session identifier.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException InvalidSession(string sessionId)
        {
            return InvalidParams($"Invalid MCP session '{sessionId}'.", new { sessionId });
        }

        /// <summary>
        /// Creates a cancelled request error.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException CancelledRequest(string message = "Request cancelled.", object? data = null)
        {
            return new McpProtocolException(-32800, message, data);
        }

        /// <summary>
        /// Creates a method not found error.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="data">Optional error data.</param>
        /// <returns>Protocol exception.</returns>
        public static McpProtocolException MethodNotFound(string message, object? data = null)
        {
            return new McpProtocolException(-32601, message, data);
        }

        /// <summary>
        /// Converts this exception into a JSON-RPC error object.
        /// </summary>
        /// <returns>JSON-RPC error.</returns>
        public JsonRpcError ToJsonRpcError()
        {
            return new JsonRpcError
            {
                Code = Code,
                Message = Message,
                Data = ErrorData
            };
        }
    }
}
