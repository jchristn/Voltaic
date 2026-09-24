namespace Voltaic.Mcp
{
    internal sealed class McpStatelessResultStamp
    {
        internal McpResult Result { get; }

        internal bool SetResultType { get; set; }

        internal bool SetTtlMs { get; set; }

        internal bool SetCacheScope { get; set; }

        internal McpStatelessResultStamp(McpResult result)
        {
            Result = result;
        }

        internal bool Any
        {
            get
            {
                return SetResultType || SetTtlMs || SetCacheScope;
            }
        }

        internal void Revert()
        {
            if (SetResultType)
            {
                Result.ResultType = null;
            }

            if (Result is McpPaginatedResult paginated)
            {
                if (SetTtlMs)
                {
                    paginated.TtlMs = null;
                }

                if (SetCacheScope)
                {
                    paginated.CacheScope = null;
                }
            }
            else if (Result is McpReadResourceResult read)
            {
                if (SetTtlMs)
                {
                    read.TtlMs = null;
                }

                if (SetCacheScope)
                {
                    read.CacheScope = null;
                }
            }
            else if (Result is McpDiscoverResult discover)
            {
                if (SetTtlMs)
                {
                    discover.TtlMs = null;
                }

                if (SetCacheScope)
                {
                    discover.CacheScope = null;
                }
            }
        }
    }
}
