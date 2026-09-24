namespace Voltaic.Mcp
{
    using Voltaic.Core;

    internal static class McpStatelessResultStamper
    {
        internal const long DefaultStatelessCacheTtlMs = 0;

        internal const string DefaultStatelessCacheScope = "private";

        internal static McpStatelessResultStamp? ApplyForVersion(JsonRpcResponse response, string? negotiatedVersion)
        {
            if (response == null || response.Error != null)
            {
                return null;
            }

            McpProtocolVersionInfo? info = McpProtocol.GetVersionInfo(negotiatedVersion);
            if (info == null || info.Era != McpProtocolEra.Stateless)
            {
                return null;
            }

            if (!(response.Result is McpResult result))
            {
                return null;
            }

            McpStatelessResultStamp stamp = new McpStatelessResultStamp(result);

            // Every stateless-era result carries resultType. A handler-supplied value such as
            // input_required or task is never overwritten.
            if (result.ResultType == null)
            {
                result.ResultType = McpResult.ResultTypeComplete;
                stamp.SetResultType = true;
            }

            // Cacheable results (the list results, resources/read, and server/discover) must carry ttlMs
            // and cacheScope under the stateless era. When the server configured no caching guidance, the
            // conservative default is "do not cache, per caller": ttlMs 0 and cacheScope private.
            if (result is McpPaginatedResult paginated)
            {
                if (paginated.TtlMs == null)
                {
                    paginated.TtlMs = DefaultStatelessCacheTtlMs;
                    stamp.SetTtlMs = true;
                }

                if (paginated.CacheScope == null)
                {
                    paginated.CacheScope = DefaultStatelessCacheScope;
                    stamp.SetCacheScope = true;
                }
            }
            else if (result is McpReadResourceResult read)
            {
                if (read.TtlMs == null)
                {
                    read.TtlMs = DefaultStatelessCacheTtlMs;
                    stamp.SetTtlMs = true;
                }

                if (read.CacheScope == null)
                {
                    read.CacheScope = DefaultStatelessCacheScope;
                    stamp.SetCacheScope = true;
                }
            }
            else if (result is McpDiscoverResult discover)
            {
                if (discover.TtlMs == null)
                {
                    discover.TtlMs = DefaultStatelessCacheTtlMs;
                    stamp.SetTtlMs = true;
                }

                if (discover.CacheScope == null)
                {
                    discover.CacheScope = DefaultStatelessCacheScope;
                    stamp.SetCacheScope = true;
                }
            }

            return stamp.Any ? stamp : null;
        }
    }
}
