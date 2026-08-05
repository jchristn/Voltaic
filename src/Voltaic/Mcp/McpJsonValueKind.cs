namespace Voltaic.Mcp
{
    /// <summary>
    /// A DOM-free classification of a JSON value's kind, used by the schema validator instead of
    /// the System.Text.Json <c>JsonValueKind</c>/<c>JsonElement</c> DOM types.
    /// </summary>
    internal enum McpJsonValueKind
    {
        /// <summary>No value was present.</summary>
        Absent = 0,

        /// <summary>A JSON object.</summary>
        Object = 1,

        /// <summary>A JSON array.</summary>
        Array = 2,

        /// <summary>A JSON string.</summary>
        String = 3,

        /// <summary>A JSON number that is not an integer.</summary>
        Number = 4,

        /// <summary>A JSON number that fits in a 64-bit integer.</summary>
        Integer = 5,

        /// <summary>A JSON boolean.</summary>
        Boolean = 6,

        /// <summary>A JSON null.</summary>
        Null = 7
    }
}
