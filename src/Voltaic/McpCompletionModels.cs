namespace Voltaic.Mcp
{
    using Voltaic.Core;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Parameters supplied to an MCP completion request.
    /// </summary>
    public class McpCompleteRequest
    {
        /// <summary>
        /// Gets or sets the prompt or resource reference being completed.
        /// </summary>
        [JsonPropertyName("ref")]
        public McpCompletionReference Ref { get; set; } = new McpCompletionReference();

        /// <summary>
        /// Gets or sets the argument being completed.
        /// </summary>
        [JsonPropertyName("argument")]
        public McpCompletionArgument Argument { get; set; } = new McpCompletionArgument();

        /// <summary>
        /// Gets or sets optional completion context.
        /// </summary>
        [JsonPropertyName("context")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public McpCompletionContext? Context { get; set; }
    }

    /// <summary>
    /// Identifies a prompt or resource template for completion.
    /// </summary>
    public class McpCompletionReference
    {
        /// <summary>
        /// Gets or sets the reference type, such as ref/prompt or ref/resource.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the referenced prompt name when Type is ref/prompt.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the referenced resource URI template when Type is ref/resource.
        /// </summary>
        [JsonPropertyName("uri")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Uri { get; set; }
    }

    /// <summary>
    /// Describes the argument currently being completed.
    /// </summary>
    public class McpCompletionArgument
    {
        /// <summary>
        /// Gets or sets the argument name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the current partial value.
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Carries previously resolved completion arguments.
    /// </summary>
    public class McpCompletionContext
    {
        /// <summary>
        /// Gets or sets already resolved argument values.
        /// </summary>
        [JsonPropertyName("arguments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Arguments { get; set; }
    }

    /// <summary>
    /// Result returned by completion/complete.
    /// </summary>
    public class McpCompleteResult : McpResult
    {
        /// <summary>
        /// Gets or sets completion values and metadata.
        /// </summary>
        [JsonPropertyName("completion")]
        public McpCompletion Completion { get; set; } = new McpCompletion();
    }

    /// <summary>
    /// Completion values and optional paging metadata.
    /// </summary>
    public class McpCompletion
    {
        /// <summary>
        /// Gets or sets ranked completion values. MCP responses are bounded to 100 values.
        /// </summary>
        [JsonPropertyName("values")]
        public List<string> Values { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the total number of available matches.
        /// </summary>
        [JsonPropertyName("total")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Total { get; set; }

        /// <summary>
        /// Gets or sets whether more values are available.
        /// </summary>
        [JsonPropertyName("hasMore")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? HasMore { get; set; }
    }
}
