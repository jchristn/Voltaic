namespace Voltaic.Mcp
{
    using System.Collections.Generic;

    /// <summary>
    /// A compiled ECMA-262 regular expression (or one of its lookaround bodies): its instructions, ending with
    /// <see cref="McpRegexOp.Match"/>.
    /// </summary>
    internal sealed class McpRegexProgram
    {
        internal List<McpRegexInstruction> Instructions { get; } = new List<McpRegexInstruction>();
    }
}
