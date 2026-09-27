namespace Voltaic.Mcp
{
    using System.Collections.Generic;

    /// <summary>
    /// A node of a parsed ECMA-262 regular expression.
    /// </summary>
    internal sealed class McpRegexNode
    {
        internal McpRegexNode(McpRegexNodeKind kind)
        {
            Kind = kind;
        }

        internal McpRegexNodeKind Kind { get; }

        // CharSet: the code points it matches.
        internal McpRegexCharSet? Set { get; set; }

        // Sequence and Alternation: the parts; Group, Quantifier, and Lookaround: the single child in Children[0].
        internal List<McpRegexNode> Children { get; } = new List<McpRegexNode>();

        // Group: the capture index (1-based), or 0 for a non-capturing group. Backreference: the referenced group.
        internal int CaptureIndex { get; set; }

        // Backreference by name, resolved to CaptureIndex after parsing.
        internal string? GroupName { get; set; }

        // Backreference by a name that several groups share (in different alternatives): every group of that name.
        internal int[]? CaptureIndexes { get; set; }

        // Backreference and word boundaries: inside an i modifier (or flag) scope.
        internal bool IgnoreCase { get; set; }

        // Start and End: inside an m modifier scope, so line terminators also count.
        internal bool Multiline { get; set; }

        // Quantifier: the bounds (Max -1 is unbounded) and greediness.
        internal int Min { get; set; }

        internal int Max { get; set; }

        internal bool Greedy { get; set; } = true;

        // Quantifier: the capture groups inside the repeated child (FirstCapture to LastCapture, inclusive; none when
        // LastCapture is below FirstCapture), which ECMA-262 resets at the start of every iteration.
        internal int FirstCapture { get; set; }

        internal int LastCapture { get; set; }

        // Lookaround: direction and polarity.
        internal bool Behind { get; set; }

        internal bool Negated { get; set; }
    }
}
