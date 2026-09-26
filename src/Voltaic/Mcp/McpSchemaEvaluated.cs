namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The object properties and array items of one instance that keywords of a schema evaluated successfully, used
    /// by <c>unevaluatedProperties</c> and <c>unevaluatedItems</c> (JSON Schema 2020-12 annotation collection). Not
    /// thread-safe; each evaluation uses its own instances.
    /// </summary>
    internal sealed class McpSchemaEvaluated
    {
        /// <summary>
        /// Gets the names of evaluated properties.
        /// </summary>
        internal HashSet<string> Properties { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the indexes of evaluated items.
        /// </summary>
        internal HashSet<int> Items { get; } = new HashSet<int>();

        /// <summary>
        /// Adds the annotations of a subschema that validated the same instance.
        /// </summary>
        internal void Merge(McpSchemaEvaluated other)
        {
            Properties.UnionWith(other.Properties);
            Items.UnionWith(other.Items);
        }
    }
}
