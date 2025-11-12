namespace Voltaic
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Represents a JSON-RPC 2.0 response message.
    /// </summary>
    public class JsonRpcResponse
    {
        /// <summary>
        /// Gets or sets the JSON-RPC protocol version. Always "2.0".
        /// </summary>
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        /// <summary>
        /// Gets or sets the result of the method invocation.
        /// This property is required on success and will be omitted from serialization if null.
        /// Must not be present if there was an error.
        /// </summary>
        [JsonPropertyName("result")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Result { get; set; }

        /// <summary>
        /// Gets or sets the error object if there was an error invoking the method.
        /// This property is required on error and will be omitted from serialization if null.
        /// Must not be present if the method succeeded.
        /// </summary>
        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonRpcError? Error { get; set; }

        /// <summary>
        /// Gets or sets the request identifier that this response corresponds to.
        /// This must match the Id of the request being replied to.
        /// </summary>
        [JsonPropertyName("id")]
        public object? Id { get; set; }
    }
}
