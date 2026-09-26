namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Adds the fields every 2026-07-28 result carries to a serialized result: <c>resultType</c> (<c>complete</c> unless
    /// the handler set one) and, on complete results of the cacheable methods, the caching hints <c>ttlMs</c> and
    /// <c>cacheScope</c>. It works on the per-response JSON, so results a handler shares between requests are never
    /// modified.
    /// </summary>
    internal static class McpStatelessResultStamper
    {
        internal const long DefaultStatelessCacheTtlMs = 0;

        internal const string DefaultStatelessCacheScope = "private";

        private static readonly HashSet<string> _CacheableMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "server/discover", "tools/list", "prompts/list", "resources/list", "resources/templates/list", "resources/read"
        };

        /// <summary>
        /// Stamps the result object of a stateless response for the method that produced it.
        /// </summary>
        internal static void Stamp(JsonObject result, string? method)
        {
            if (!result.ContainsKey("resultType")) result["resultType"] = McpResult.ResultTypeComplete;

            // Cacheable complete results must carry caching hints (2026-07-28 caching utility); interim results
            // (input_required) carry none.
            bool complete = result["resultType"] is JsonValue type && type.TryGetValue(out string? typeName) && typeName == McpResult.ResultTypeComplete;
            if (!complete || method == null || !_CacheableMethods.Contains(method)) return;

            if (result["ttlMs"] is not JsonValue ttl || !ttl.TryGetValue(out long ttlValue) || ttlValue < 0) result["ttlMs"] = DefaultStatelessCacheTtlMs;
            if (result["cacheScope"] is not JsonValue scope || !scope.TryGetValue(out string? scopeValue) || (scopeValue != "public" && scopeValue != "private"))
            {
                result["cacheScope"] = DefaultStatelessCacheScope;
            }
        }
    }
}
