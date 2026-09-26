namespace Voltaic.Mcp
{
    /// <summary>
    /// The kinds of entry on the backtracking stack of the ECMA-262 regular expression matcher.
    /// </summary>
    internal enum McpRegexBacktrackKind
    {
        /// <summary>An untried alternative: resume at the entry's instruction and position.</summary>
        Branch,

        /// <summary>Undo a capture slot change.</summary>
        RestoreCapture,

        /// <summary>Undo a repetition register change.</summary>
        RestoreRegister,

        /// <summary>A greedy character-set loop gives back one code point.</summary>
        GreedyStep,

        /// <summary>A lazy character-set loop takes one more code point.</summary>
        LazyStep,
    }
}
