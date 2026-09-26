namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// A schema resource within a schema document: the root, or a subschema with its own <c>$id</c>. References
    /// without a resource part resolve against the resource they appear in, and anchors are scoped to their resource.
    /// Not thread-safe; built once per document.
    /// </summary>
    internal sealed class McpSchemaResource
    {
        /// <summary>
        /// Initializes a resource.
        /// </summary>
        internal McpSchemaResource(Uri baseUri, JsonElement element)
        {
            BaseUri = baseUri;
            Element = element;
        }

        /// <summary>
        /// Gets the resource's absolute URI (its <c>$id</c> resolved against the enclosing resource, or a placeholder
        /// for a root without one), used to resolve relative references.
        /// </summary>
        internal Uri BaseUri { get; }

        /// <summary>
        /// Gets the schema that is the resource.
        /// </summary>
        internal JsonElement Element { get; }

        /// <summary>
        /// Gets the plain-name anchors in the resource (<c>$anchor</c>, <c>$dynamicAnchor</c>, or a draft-07
        /// <c>$id</c> of the form <c>#name</c>).
        /// </summary>
        internal Dictionary<string, JsonElement> Anchors { get; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the <c>$dynamicAnchor</c> names in the resource.
        /// </summary>
        internal Dictionary<string, JsonElement> DynamicAnchors { get; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }
}
