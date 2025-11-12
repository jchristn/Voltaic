namespace Voltaic.JsonRpc
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Represents a JSON-RPC 2.0 request message.
    /// </summary>
    public class JsonRpcRequest
    {
        /// <summary>
        /// Gets or sets the JSON-RPC protocol version. Always "2.0".
        /// </summary>
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        /// <summary>
        /// Gets or sets the name of the method to be invoked.
        /// </summary>
        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the parameters to be passed to the method.
        /// This property is optional and will be omitted from serialization if null.
        /// </summary>
        [JsonPropertyName("params")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Params { get; set; }

        /// <summary>
        /// Gets or sets the request identifier.
        /// If null, this is a notification and no response is expected.
        /// This property will be omitted from serialization if null.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Id { get; set; }

        /// <summary>
        /// Represents a JSON-RPC 2.0 request message.
        /// </summary>
        public JsonRpcRequest()
        {

        }
    }
}
