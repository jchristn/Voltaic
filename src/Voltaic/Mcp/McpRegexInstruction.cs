namespace Voltaic.Mcp
{
    /// <summary>
    /// One instruction of a compiled ECMA-262 regular expression. The meaning of the operands depends on
    /// <see cref="Op"/>.
    /// </summary>
    internal sealed class McpRegexInstruction
    {
        internal McpRegexInstruction(McpRegexOp op)
        {
            Op = op;
        }

        internal McpRegexOp Op { get; }

        // Char and SetLoop: the code points to match.
        internal McpRegexCharSet? Set { get; set; }

        // Jump/Split targets; Save slot; Backreference group; repetition register.
        internal int A { get; set; }

        internal int B { get; set; }

        // RepeatLoop: the exit target.
        internal int C { get; set; }

        // Repetition bounds (Max -1 is unbounded); RepeatEnter: the first and last capture group to reset.
        internal int Min { get; set; }

        internal int Max { get; set; }

        internal bool Greedy { get; set; }

        // Char and Backreference: match right to left (inside a lookbehind).
        internal bool Backward { get; set; }

        // Lookaround: the sub-program and whether it is negative.
        internal McpRegexProgram? Program { get; set; }

        internal bool Negated { get; set; }
    }
}
