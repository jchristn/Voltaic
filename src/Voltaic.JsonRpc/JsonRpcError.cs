namespace Voltaic.JsonRpc
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Represents a JSON-RPC 2.0 error object.
    /// </summary>
    public class JsonRpcError
    {
        /// <summary>
        /// Gets or sets the error code.
        /// Standard error codes are defined by the JSON-RPC 2.0 specification.
        /// </summary>
        [JsonPropertyName("code")]
        public int Code { get; set; }

        /// <summary>
        /// Gets or sets a short description of the error.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets additional information about the error.
        /// This property is optional and will be omitted from serialization if null.
        /// </summary>
        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Data { get; set; }

        /// <summary>
        /// Creates a parse error (-32700) indicating that invalid JSON was received by the server.
        /// </summary>
        /// <returns>A new JsonRpcError instance representing a parse error.</returns>
        public static JsonRpcError ParseError() => new JsonRpcError { Code = -32700, Message = "Parse error" };

        /// <summary>
        /// Creates an invalid request error (-32600) indicating that the JSON sent is not a valid request object.
        /// </summary>
        /// <returns>A new JsonRpcError instance representing an invalid request error.</returns>
        public static JsonRpcError InvalidRequest() => new JsonRpcError { Code = -32600, Message = "Invalid request" };

        /// <summary>
        /// Creates a method not found error (-32601) indicating that the method does not exist or is not available.
        /// </summary>
        /// <returns>A new JsonRpcError instance representing a method not found error.</returns>
        public static JsonRpcError MethodNotFound() => new JsonRpcError { Code = -32601, Message = "Method not found" };

        /// <summary>
        /// Creates an invalid params error (-32602) indicating that the method parameters are invalid.
        /// </summary>
        /// <returns>A new JsonRpcError instance representing an invalid params error.</returns>
        public static JsonRpcError InvalidParams() => new JsonRpcError { Code = -32602, Message = "Invalid params" };

        /// <summary>
        /// Creates an internal error (-32603) indicating an internal JSON-RPC error.
        /// </summary>
        /// <returns>A new JsonRpcError instance representing an internal error.</returns>
        public static JsonRpcError InternalError() => new JsonRpcError { Code = -32603, Message = "Internal error" };

        /// <summary>
        /// Represents a JSON-RPC 2.0 error object.
        /// </summary>
        public JsonRpcError()
        {

        }
    }
}
