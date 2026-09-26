namespace Voltaic.Mcp
{
    /// <summary>
    /// One side of a character class range while an ECMA-262 pattern is parsed: a single code point, or the set of a
    /// class escape such as <c>\w</c>.
    /// </summary>
    internal readonly struct McpRegexClassAtom
    {
        internal McpRegexClassAtom(int codePoint, McpRegexCharSet? set)
        {
            CodePoint = codePoint;
            Set = set;
        }

        internal int CodePoint { get; }

        internal McpRegexCharSet? Set { get; }

        internal McpRegexCharSet ToSet()
        {
            return Set ?? McpRegexCharSet.Single(CodePoint);
        }
    }
}
