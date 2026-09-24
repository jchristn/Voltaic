namespace Voltaic.Mcp
{
    /// <summary>
    /// An MCP result with no payload, returned by requests whose success carries no data, such as
    /// <c>ping</c>, <c>resources/subscribe</c>, <c>resources/unsubscribe</c>, and
    /// <c>logging/setLevel</c>. Under handshake-era revisions it serializes to <c>{}</c>. Under a
    /// stateless-era revision (2026-07-28+) Voltaic servers set <see cref="McpResult.ResultType"/> to
    /// <c>complete</c>, so it serializes to <c>{"resultType":"complete"}</c>.
    /// </summary>
    public class McpEmptyResult : McpResult
    {
    }
}
