namespace Voltaic.Mcp
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Typed view of <c>initialize</c> request parameters, used by the dispatcher to avoid
    /// System.Text.Json DOM access.
    /// </summary>
    internal sealed class McpInitializeParams
    {
        [JsonPropertyName("protocolVersion")]
        public string? ProtocolVersion { get; set; }
    }

    /// <summary>
    /// Typed view of request parameters that carry a <c>name</c> and optional <c>arguments</c>
    /// (used by <c>tools/call</c> and <c>prompts/get</c>). The arguments are captured as a CLR value
    /// so they can be re-serialized into an <see cref="McpParameters"/> for the handler without DOM.
    /// </summary>
    internal sealed class McpNamedArgumentsParams
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public object? Arguments { get; set; }
    }

    /// <summary>
    /// Typed view of request parameters that carry a <c>uri</c> (used by resource methods).
    /// </summary>
    internal sealed class McpUriParams
    {
        [JsonPropertyName("uri")]
        public string? Uri { get; set; }
    }

    /// <summary>
    /// Typed view of request parameters that carry a logging <c>level</c>.
    /// </summary>
    internal sealed class McpLevelParams
    {
        [JsonPropertyName("level")]
        public string? Level { get; set; }
    }

    /// <summary>
    /// Typed view of request parameters that carry a pagination <c>cursor</c>.
    /// </summary>
    internal sealed class McpCursorParams
    {
        [JsonPropertyName("cursor")]
        public string? Cursor { get; set; }
    }

    /// <summary>
    /// Typed view of the built-in <c>echo</c> tool arguments.
    /// </summary>
    internal sealed class McpEchoArguments
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>
    /// Typed, recursive view of the subset of JSON Schema that Voltaic validates
    /// (<c>type</c>, <c>required</c>, and <c>properties</c>). Other keywords are ignored.
    /// </summary>
    internal sealed class McpJsonSchema
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("required")]
        public List<string>? Required { get; set; }

        [JsonPropertyName("properties")]
        public Dictionary<string, McpJsonSchema>? Properties { get; set; }
    }
}
