namespace Voltaic.Mcp
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// Checks whether a client declared a capability, including nested ones named by a dotted path such as
    /// <c>elicitation.url</c> or <c>sampling.tools</c>. Thread-safe.
    /// </summary>
    internal static class McpClientCapabilityPath
    {
        /// <summary>
        /// Returns true when <paramref name="path"/> names an object member present in <paramref name="capabilities"/>.
        /// </summary>
        internal static bool IsDeclared(JsonElement? capabilities, string path)
        {
            if (!capabilities.HasValue || String.IsNullOrEmpty(path)) return false;
            JsonElement current = capabilities.Value;
            foreach (string segment in path.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out JsonElement next)) return false;
                current = next;
            }

            return true;
        }
    }
}
