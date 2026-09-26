namespace Voltaic.Mcp
{
    /// <summary>
    /// One entry on the backtracking stack of the ECMA-262 regular expression matcher.
    /// </summary>
    internal readonly struct McpRegexBacktrack
    {
        internal McpRegexBacktrack(McpRegexBacktrackKind kind, int pc, int position, int slot, int value)
        {
            Kind = kind;
            Pc = pc;
            Position = position;
            Slot = slot;
            Value = value;
        }

        internal McpRegexBacktrackKind Kind { get; }

        // Branch, GreedyStep, LazyStep: the instruction to resume at (LazyStep: the SetLoop instruction itself).
        internal int Pc { get; }

        // Branch, GreedyStep, LazyStep: the input position.
        internal int Position { get; }

        // RestoreCapture and RestoreRegister: the slot or register.
        internal int Slot { get; }

        // RestoreCapture and RestoreRegister: the value to restore; GreedyStep: the lowest position to give back to;
        // LazyStep: the number of code points taken so far.
        internal int Value { get; }
    }
}
