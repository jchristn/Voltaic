namespace Voltaic.Mcp
{
    /// <summary>
    /// The parsed value of a JSON Schema <c>additionalProperties</c> keyword: either a boolean or a
    /// schema that undeclared properties must satisfy.
    /// </summary>
    internal sealed class McpAdditionalPropertiesRule
    {
        /// <summary>
        /// Gets or sets whether properties not declared in <c>properties</c> or matched by
        /// <c>patternProperties</c> are allowed. False corresponds to <c>additionalProperties: false</c>.
        /// </summary>
        public bool Allowed { get; set; } = true;

        /// <summary>
        /// Gets or sets the schema that undeclared properties must satisfy, or null when the keyword
        /// was a boolean.
        /// </summary>
        public McpJsonSchema? Schema { get; set; }
    }
}
