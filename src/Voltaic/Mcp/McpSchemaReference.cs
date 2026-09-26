namespace Voltaic.Mcp
{
    /// <summary>
    /// A <c>$ref</c> or <c>$dynamicRef</c> found in a schema, with the resource it appears in, so it can be checked
    /// for resolvability when the schema is registered. Immutable.
    /// </summary>
    internal sealed class McpSchemaReference
    {
        /// <summary>
        /// Initializes a reference.
        /// </summary>
        internal McpSchemaReference(string reference, McpSchemaResource resource, bool dynamic)
        {
            Reference = reference;
            Resource = resource;
            Dynamic = dynamic;
        }

        /// <summary>
        /// Gets the reference text.
        /// </summary>
        internal string Reference { get; }

        /// <summary>
        /// Gets the resource the reference appears in.
        /// </summary>
        internal McpSchemaResource Resource { get; }

        /// <summary>
        /// Gets whether the reference is a <c>$dynamicRef</c>.
        /// </summary>
        internal bool Dynamic { get; }
    }
}
