namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.Json;

    /// <summary>
    /// A schema document prepared for validation: its dialect, and the targets of <c>$ref</c> and
    /// <c>$dynamicRef</c> within it (JSON pointers, <c>$anchor</c> and <c>$dynamicAnchor</c> names, and embedded
    /// resources identified by <c>$id</c>). References that leave the document cannot be resolved; schemas that use
    /// them are rejected rather than treated as permissive. Thread-safe after construction.
    /// </summary>
    internal sealed class McpSchemaDocument
    {
        /// <summary>
        /// The default dialect (JSON Schema 2020-12), used when a schema has no <c>$schema</c>.
        /// </summary>
        internal const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

        private static readonly string[] _SupportedDialects = new[]
        {
            "https://json-schema.org/draft/2020-12/schema",
            "http://json-schema.org/draft/2020-12/schema",
            "https://json-schema.org/draft-07/schema",
            "http://json-schema.org/draft-07/schema"
        };

        private readonly Dictionary<string, JsonElement> _Anchors = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _DynamicAnchors = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _Resources = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        private readonly List<string> _References = new List<string>();
        private readonly Uri? _BaseUri;

        /// <summary>
        /// Indexes a schema.
        /// </summary>
        internal McpSchemaDocument(JsonElement root)
        {
            Root = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                && Uri.TryCreate(id.GetString(), UriKind.Absolute, out Uri? baseUri))
            {
                _BaseUri = baseUri;
            }

            Index(root, 0);
        }

        /// <summary>
        /// Gets the root schema.
        /// </summary>
        internal JsonElement Root { get; }

        /// <summary>
        /// Returns true when <paramref name="dialect"/> (a <c>$schema</c> value, or null for the default) is a dialect
        /// the validator implements: JSON Schema 2020-12 (the MCP default) or draft-07.
        /// </summary>
        internal static bool IsSupportedDialect(string? dialect)
        {
            if (dialect == null) return true;
            string normalized = dialect.TrimEnd('#');
            foreach (string supported in _SupportedDialects)
            {
                if (StringComparer.Ordinal.Equals(normalized, supported)) return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the root's <c>$schema</c> value, or null when absent.
        /// </summary>
        internal string? Dialect
        {
            get
            {
                return Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty("$schema", out JsonElement dialect) && dialect.ValueKind == JsonValueKind.String
                    ? dialect.GetString()
                    : null;
            }
        }

        /// <summary>
        /// Returns the first <c>$ref</c> or <c>$dynamicRef</c> that cannot be resolved within the document, or null.
        /// </summary>
        internal string? FindUnresolvedReference()
        {
            foreach (string reference in _References)
            {
                if (!TryResolve(reference, out JsonElement _)) return reference;
            }

            return null;
        }

        /// <summary>
        /// Resolves a <c>$ref</c>: a JSON pointer (<c>#/...</c>), an anchor (<c>#name</c>), or an embedded resource
        /// named by its <c>$id</c> (absolute or relative to the root <c>$id</c>), optionally followed by a fragment.
        /// </summary>
        internal bool TryResolve(string? reference, out JsonElement target)
        {
            target = Root;
            if (String.IsNullOrEmpty(reference)) return false;

            int hash = reference!.IndexOf('#');
            string resource = hash < 0 ? reference : reference.Substring(0, hash);
            string fragment = hash < 0 ? String.Empty : reference.Substring(hash + 1);

            JsonElement scope = Root;
            if (resource.Length > 0 && !TryFindResource(resource, out scope)) return false;
            if (fragment.Length == 0)
            {
                target = scope;
                return true;
            }

            if (fragment[0] == '/') return TryResolvePointer(scope, fragment, out target);
            return _Anchors.TryGetValue(fragment, out target) || _DynamicAnchors.TryGetValue(fragment, out target);
        }

        /// <summary>
        /// Resolves a <c>$dynamicRef</c>. Within one document the outermost <c>$dynamicAnchor</c> of that name is the
        /// dynamic scope's target; a reference without a matching dynamic anchor resolves like <c>$ref</c>.
        /// </summary>
        internal bool TryResolveDynamic(string? reference, out JsonElement target)
        {
            target = Root;
            if (!String.IsNullOrEmpty(reference) && reference!.StartsWith("#", StringComparison.Ordinal)
                && reference.Length > 1 && reference[1] != '/' && _DynamicAnchors.TryGetValue(reference.Substring(1), out target))
            {
                return true;
            }

            return TryResolve(reference, out target);
        }

        private bool TryFindResource(string resource, out JsonElement target)
        {
            if (_Resources.TryGetValue(resource, out target)) return true;
            if (_BaseUri != null && Uri.TryCreate(_BaseUri, resource, out Uri? absolute) && _Resources.TryGetValue(absolute.ToString(), out target)) return true;
            if (_BaseUri != null && StringComparer.Ordinal.Equals(resource, _BaseUri.ToString()))
            {
                target = Root;
                return true;
            }

            return false;
        }

        private void Index(JsonElement element, int depth)
        {
            if (depth > 256) return;
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Index(item, depth + 1);
                return;
            }

            if (element.ValueKind != JsonValueKind.Object) return;

            foreach (JsonProperty member in element.EnumerateObject())
            {
                switch (member.Name)
                {
                    case "$anchor" when member.Value.ValueKind == JsonValueKind.String:
                        _Anchors.TryAdd(member.Value.GetString()!, element);
                        break;
                    case "$dynamicAnchor" when member.Value.ValueKind == JsonValueKind.String:
                        _DynamicAnchors.TryAdd(member.Value.GetString()!, element);
                        break;
                    case "$id" when member.Value.ValueKind == JsonValueKind.String:
                        string id = member.Value.GetString()!.TrimEnd('#');
                        _Resources.TryAdd(id, element);
                        if (_BaseUri != null && Uri.TryCreate(_BaseUri, id, out Uri? absolute)) _Resources.TryAdd(absolute.ToString(), element);
                        break;
                    case "$ref" when member.Value.ValueKind == JsonValueKind.String:
                    case "$dynamicRef" when member.Value.ValueKind == JsonValueKind.String:
                        _References.Add(member.Value.GetString()!);
                        break;
                    case "enum":
                    case "const":
                    case "default":
                    case "examples":
                        // Instance values, not schemas.
                        continue;
                }

                Index(member.Value, depth + 1);
            }
        }

        private static bool TryResolvePointer(JsonElement scope, string fragment, out JsonElement target)
        {
            target = scope;
            string pointer;
            try
            {
                pointer = Uri.UnescapeDataString(fragment.Substring(1));
            }
            catch (UriFormatException)
            {
                return false;
            }


            JsonElement current = scope;
            foreach (string rawToken in pointer.Split('/'))
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
