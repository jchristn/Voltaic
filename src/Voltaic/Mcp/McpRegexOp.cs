namespace Voltaic.Mcp
{
    /// <summary>
    /// The instructions of the ECMA-262 regular expression virtual machine.
    /// </summary>
    internal enum McpRegexOp
    {
        /// <summary>The pattern matched.</summary>
        Match,

        /// <summary>Consume one code point in the set (backward in a lookbehind).</summary>
        Char,

        /// <summary>Continue at <see cref="McpRegexInstruction.A"/>.</summary>
        Jump,

        /// <summary>Try <see cref="McpRegexInstruction.A"/> first, then <see cref="McpRegexInstruction.B"/>.</summary>
        Split,

        /// <summary>Record the position in capture slot <see cref="McpRegexInstruction.A"/>.</summary>
        Save,

        /// <summary>Assert the start of the input.</summary>
        Start,

        /// <summary>Assert the end of the input.</summary>
        End,

        /// <summary>Assert a word boundary.</summary>
        WordBoundary,

        /// <summary>Assert no word boundary.</summary>
        NotWordBoundary,

        /// <summary>Match the text of capture group <see cref="McpRegexInstruction.A"/> again.</summary>
        Backreference,

        /// <summary>Run a lookahead or lookbehind sub-program.</summary>
        Lookaround,

        /// <summary>Reset the iteration counter of a general repetition.</summary>
        RepeatInit,

        /// <summary>Decide whether a general repetition iterates again.</summary>
        RepeatLoop,

        /// <summary>Start one iteration: remember its start and reset the captures inside.</summary>
        RepeatEnter,

        /// <summary>End one iteration: reject an empty one past the minimum, count it, and loop.</summary>
        RepeatContinue,

        /// <summary>A repetition of a single character set, run without per-iteration bookkeeping.</summary>
        SetLoop,
    }
}
