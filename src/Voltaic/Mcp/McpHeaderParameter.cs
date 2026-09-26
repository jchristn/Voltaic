namespace Voltaic.Mcp
{
    using System.Collections.Generic;

    /// <summary>
    /// A tool parameter that a 2026-07-28 tool definition mirrors into an <c>Mcp-Param-{Name}</c> HTTP header through
    /// the <c>x-mcp-header</c> annotation.
    /// </summary>
    internal sealed class McpHeaderParameter
    {
        /// <summary>
        /// Gets the <c>x-mcp-header</c> value: the name portion of the header.
        /// </summary>
        internal string Name { get; }

        /// <summary>
        /// Gets the full header name, <c>Mcp-Param-{Name}</c>.
        /// </summary>
        internal string HeaderName => McpProtocol.ParamHeaderPrefix + Name;

        /// <summary>
        /// Gets the chain of <c>properties</c> keys from the schema root to the annotated property.
        /// </summary>
        internal IReadOnlyList<string> Path { get; }

        /// <summary>
        /// Gets the JSON Schema type of the property: <c>string</c>, <c>integer</c>, or <c>boolean</c>.
        /// </summary>
        internal string Type { get; }

        internal McpHeaderParameter(string name, IReadOnlyList<string> path, string type)
        {
            Name = name;
            Path = path;
            Type = type;
        }
    }
}
