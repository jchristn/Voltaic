namespace Voltaic.Mcp
{
    /// <summary>
    /// The kinds of node in a parsed ECMA-262 regular expression.
    /// </summary>
    internal enum McpRegexNodeKind
    {
        /// <summary>Matches the empty string.</summary>
        Empty,

        /// <summary>Matches one code point in <see cref="McpRegexNode.Set"/>.</summary>
        CharSet,

        /// <summary>Matches its children in order.</summary>
        Sequence,

        /// <summary>Matches the first child that leads to an overall match.</summary>
        Alternation,

        /// <summary>A group; capturing when <see cref="McpRegexNode.CaptureIndex"/> is 1 or more.</summary>
        Group,

        /// <summary>Repeats its child from <see cref="McpRegexNode.Min"/> to <see cref="McpRegexNode.Max"/> times.</summary>
        Quantifier,

        /// <summary>The start of the input (<c>^</c>).</summary>
        Start,

        /// <summary>The end of the input (<c>$</c>).</summary>
        End,

        /// <summary>A word boundary (<c>\b</c>).</summary>
        WordBoundary,

        /// <summary>Not a word boundary (<c>\B</c>).</summary>
        NotWordBoundary,

        /// <summary>A lookahead or lookbehind assertion.</summary>
        Lookaround,

        /// <summary>A backreference to capture group <see cref="McpRegexNode.CaptureIndex"/>.</summary>
        Backreference,
    }
}
