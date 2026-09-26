namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;

    /// <summary>
    /// A schema document prepared for one validation: its dialect, its resources (the root and every subschema with its
    /// own <c>$id</c>) with their anchors, and the dynamic scope while it is evaluated. References resolve against the
    /// resource they appear in: JSON pointers, plain-name anchors (<c>$anchor</c>, <c>$dynamicAnchor</c>, or a draft-07
    /// <c>$id</c> of the form <c>#name</c>), and embedded resources named by their <c>$id</c>. <c>$dynamicRef</c>
    /// follows the dynamic scope (JSON Schema 2020-12). References that leave the document cannot be resolved; schemas
    /// that use them are rejected rather than treated as permissive. Not thread-safe: each validation builds its own.
    /// </summary>
    internal sealed class McpSchemaDocument
    {
        /// <summary>
        /// The default dialect (JSON Schema 2020-12), used when a schema has no <c>$schema</c>.
        /// </summary>
        internal const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

        private static readonly Uri _PlaceholderBase = new Uri("https://voltaic.invalid/schema/root.json");
        private static readonly string[] _Draft202012 = new[] { "https://json-schema.org/draft/2020-12/schema", "http://json-schema.org/draft/2020-12/schema" };
        private static readonly string[] _Draft07 = new[] { "https://json-schema.org/draft-07/schema", "http://json-schema.org/draft-07/schema" };

        private readonly Dictionary<string, McpSchemaResource> _Resources = new Dictionary<string, McpSchemaResource>(StringComparer.Ordinal);
        private readonly List<McpSchemaReference> _References = new List<McpSchemaReference>();
        private readonly List<McpSchemaResource> _Scope = new List<McpSchemaResource>();
        private readonly McpSchemaResource _RootResource;

        /// <summary>
        /// Indexes a schema.
        /// </summary>
        internal McpSchemaDocument(JsonElement root)
        {
            Root = root;
            string? dialect = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$schema", out JsonElement declared) && declared.ValueKind == JsonValueKind.String
                ? declared.GetString()
                : null;
            Dialect = dialect;
            IsDraft07 = dialect != null && _Draft07.Contains(dialect.TrimEnd('#'), StringComparer.Ordinal);

            Uri rootUri = _PlaceholderBase;
            if (root.ValueKind == JsonValueKind.Object && TryGetResourceId(root, out string? rootId) && Uri.TryCreate(rootId, UriKind.Absolute, out Uri? absolute))
            {
                rootUri = WithoutFragment(absolute);
            }

            _RootResource = new McpSchemaResource(rootUri, root);
            _Resources[rootUri.AbsoluteUri] = _RootResource;
            Index(root, _RootResource, 0, true);
            _Scope.Add(_RootResource);
        }

        /// <summary>
        /// Gets the root schema.
        /// </summary>
        internal JsonElement Root { get; }

        /// <summary>
        /// Gets the root's <c>$schema</c> value, or null when absent (JSON Schema 2020-12).
        /// </summary>
        internal string? Dialect { get; }

        /// <summary>
        /// Gets whether the schema is draft-07: <c>$ref</c> then overrides its sibling keywords, and 2019-09/2020-12
        /// keywords (such as <c>prefixItems</c> or <c>unevaluatedProperties</c>) are not keywords.
        /// </summary>
        internal bool IsDraft07 { get; }

        /// <summary>
        /// Returns true when <paramref name="dialect"/> (a <c>$schema</c> value, or null for the default) is a dialect
        /// the validator implements: JSON Schema 2020-12 (the MCP default) or draft-07.
        /// </summary>
        internal static bool IsSupportedDialect(string? dialect)
        {
            if (dialect == null) return true;
            string normalized = dialect.TrimEnd('#');
            return _Draft202012.Contains(normalized, StringComparer.Ordinal) || _Draft07.Contains(normalized, StringComparer.Ordinal);
        }

        /// <summary>
        /// Returns the first <c>$ref</c> or <c>$dynamicRef</c> that cannot be resolved within the document, or null.
        /// </summary>
        internal string? FindUnresolvedReference()
        {
            foreach (McpSchemaReference reference in _References)
            {
                if (!TryResolveIn(reference.Resource, reference.Reference, out JsonElement _, out McpSchemaResource? _)) return reference.Reference;
            }

            return null;
        }

        /// <summary>
        /// Enters the resource a schema starts (one with its own <c>$id</c>), making it the base for references and
        /// adding it to the dynamic scope. Returns true when a resource was entered; the caller must call
        /// <see cref="Exit"/> afterwards.
        /// </summary>
        internal bool Enter(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object || !TryGetResourceId(schema, out string? id)) return false;
            McpSchemaResource current = _Scope[_Scope.Count - 1];
            if (!Uri.TryCreate(current.BaseUri, id, out Uri? resolved)) return false;
            if (!_Resources.TryGetValue(WithoutFragment(resolved).AbsoluteUri, out McpSchemaResource? resource)) return false;
            _Scope.Add(resource);
            return true;
        }

        /// <summary>
        /// Enters a resource explicitly, for a reference that jumps into it.
        /// </summary>
        internal void Enter(McpSchemaResource resource)
        {
            _Scope.Add(resource);
        }

        /// <summary>
        /// Leaves the most recently entered resource.
        /// </summary>
        internal void Exit()
        {
            if (_Scope.Count > 1) _Scope.RemoveAt(_Scope.Count - 1);
        }

        /// <summary>
        /// Resolves a <c>$ref</c> against the current resource.
        /// </summary>
        internal bool TryResolve(string? reference, out JsonElement target, out McpSchemaResource? resource)
        {
            return TryResolveIn(_Scope[_Scope.Count - 1], reference, out target, out resource);
        }

        /// <summary>
        /// Resolves a <c>$dynamicRef</c>: statically first, and when that target is a <c>$dynamicAnchor</c> of the same
        /// name, the outermost resource in the dynamic scope with such an anchor supplies the target instead.
        /// </summary>
        internal bool TryResolveDynamic(string? reference, out JsonElement target, out McpSchemaResource? resource)
        {
            if (!TryResolve(reference, out target, out resource)) return false;

            int hash = reference!.IndexOf('#');
            string fragment = hash < 0 ? String.Empty : reference.Substring(hash + 1);
            if (fragment.Length == 0 || fragment[0] == '/' || resource == null || !resource.DynamicAnchors.ContainsKey(fragment)) return true;

            foreach (McpSchemaResource scope in _Scope)
            {
                if (scope.DynamicAnchors.TryGetValue(fragment, out JsonElement dynamicTarget))
                {
                    target = dynamicTarget;
                    resource = scope;
                    return true;
                }
            }

            return true;
        }

        private bool TryResolveIn(McpSchemaResource from, string? reference, out JsonElement target, out McpSchemaResource? resource)
        {
            target = from.Element;
            resource = from;
            if (reference == null) return false;

            int hash = reference.IndexOf('#');
            string resourcePart = hash < 0 ? reference : reference.Substring(0, hash);
            string fragment = hash < 0 ? String.Empty : reference.Substring(hash + 1);

            if (resourcePart.Length > 0)
            {
                if (!Uri.TryCreate(from.BaseUri, resourcePart, out Uri? absolute)) return false;
                if (!_Resources.TryGetValue(WithoutFragment(absolute).AbsoluteUri, out resource)) return false;
            }

            if (fragment.Length == 0)
            {
                target = resource!.Element;
                return true;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(fragment);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (decoded.Length > 0 && decoded[0] == '/') return TryResolvePointer(resource!.Element, decoded, out target);
            return resource!.Anchors.TryGetValue(decoded, out target);
        }

        // Records resources, anchors, and references. Keywords that hold instance values (enum, const, default,
        // examples) are not schemas and are skipped.
        private void Index(JsonElement element, McpSchemaResource resource, int depth, bool isResourceRoot)
        {
            if (depth > 256) return;
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Index(item, resource, depth + 1, false);
                return;
            }

            if (element.ValueKind != JsonValueKind.Object) return;

            if (!isResourceRoot && TryGetResourceId(element, out string? id) && Uri.TryCreate(resource.BaseUri, id, out Uri? resolved))
            {
                // A subschema with its own $id is a new resource: its references and anchors are scoped to it.
                McpSchemaResource embedded = new McpSchemaResource(WithoutFragment(resolved), element);
                _Resources[embedded.BaseUri.AbsoluteUri] = embedded;
                resource = embedded;
            }

            foreach (JsonProperty member in element.EnumerateObject())
            {
                switch (member.Name)
                {
                    case "$anchor" when member.Value.ValueKind == JsonValueKind.String && !IsDraft07:
                        resource.Anchors.TryAdd(member.Value.GetString()!, element);
                        continue;
                    case "$dynamicAnchor" when member.Value.ValueKind == JsonValueKind.String && !IsDraft07:
                        resource.Anchors.TryAdd(member.Value.GetString()!, element);
                        resource.DynamicAnchors.TryAdd(member.Value.GetString()!, element);
                        continue;
                    case "$id" when member.Value.ValueKind == JsonValueKind.String && IsDraft07 && member.Value.GetString()!.StartsWith("#", StringComparison.Ordinal):
                        // draft-07: an $id that is only a fragment is a plain-name anchor.
                        resource.Anchors.TryAdd(member.Value.GetString()!.Substring(1), element);
                        continue;
                    case "$ref" when member.Value.ValueKind == JsonValueKind.String:
                        _References.Add(new McpSchemaReference(member.Value.GetString()!, resource, false));
                        continue;
                    case "$dynamicRef" when member.Value.ValueKind == JsonValueKind.String && !IsDraft07:
                        _References.Add(new McpSchemaReference(member.Value.GetString()!, resource, true));
                        continue;
                    case "enum":
                    case "const":
                    case "default":
                    case "examples":
                        continue;
                }

                Index(member.Value, resource, depth + 1, false);
            }
        }

        // The $id that makes a schema a resource (draft-07 fragment-only ids are anchors, not resources).
        private bool TryGetResourceId(JsonElement schema, out string? id)
        {
            id = null;
            if (!schema.TryGetProperty("$id", out JsonElement value) || value.ValueKind != JsonValueKind.String) return false;
            string text = value.GetString() ?? String.Empty;
            if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal)) return false;
            id = text;
            return true;
        }

        private static Uri WithoutFragment(Uri uri)
        {
            if (!uri.IsAbsoluteUri) return uri;
            string text = uri.AbsoluteUri;
            int hash = text.IndexOf('#');
            return hash < 0 ? uri : new Uri(text.Substring(0, hash));
        }

        private static bool TryResolvePointer(JsonElement scope, string pointer, out JsonElement target)
        {
            target = scope;
            JsonElement current = scope;
            foreach (string rawToken in pointer.Substring(1).Split('/'))
            {
                string token = rawToken.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (current.ValueKind == JsonValueKind.Object)
                {
                    if (!current.TryGetProperty(token, out JsonElement next)) return false;
                    current = next;
                }
                else if (current.ValueKind == JsonValueKind.Array)
                {
                    if (!Int32.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= current.GetArrayLength()) return false;
                    current = current[index];
                }
                else
                {
                    return false;
                }
            }

            target = current;
            return true;
        }
    }
}
