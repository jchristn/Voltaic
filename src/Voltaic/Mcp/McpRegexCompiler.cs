namespace Voltaic.Mcp
{
    using System.Collections.Generic;

    /// <summary>
    /// Compiles a parsed ECMA-262 regular expression into <see cref="McpRegexProgram"/> instructions. Lookbehind bodies
    /// are compiled to match right to left, as ECMA-262 specifies. Every general repetition gets its own two registers
    /// (iteration count and iteration start).
    /// </summary>
    internal sealed class McpRegexCompiler
    {
        private int _Registers;

        /// <summary>
        /// Gets the number of repetition registers the compiled programs use.
        /// </summary>
        internal int RegisterCount => _Registers;

        internal McpRegexProgram Compile(McpRegexNode root, bool backward)
        {
            McpRegexProgram program = new McpRegexProgram();
            Emit(program.Instructions, root, backward);
            program.Instructions.Add(new McpRegexInstruction(McpRegexOp.Match));
            return program;
        }

        private void Emit(List<McpRegexInstruction> code, McpRegexNode node, bool backward)
        {
            switch (node.Kind)
            {
                case McpRegexNodeKind.Empty:
                    return;
                case McpRegexNodeKind.CharSet:
                    code.Add(new McpRegexInstruction(McpRegexOp.Char) { Set = node.Set, Backward = backward });
                    return;
                case McpRegexNodeKind.Sequence:
                    if (backward)
                    {
                        for (int i = node.Children.Count - 1; i >= 0; i--) Emit(code, node.Children[i], true);
                    }
                    else
                    {
                        foreach (McpRegexNode child in node.Children) Emit(code, child, false);
                    }

                    return;
                case McpRegexNodeKind.Alternation:
                    EmitAlternation(code, node, backward);
                    return;
                case McpRegexNodeKind.Group:
                    if (node.CaptureIndex == 0)
                    {
                        Emit(code, node.Children[0], backward);
                        return;
                    }

                    // Slots 2i and 2i+1 hold a group's start and end; right to left, the end is reached first.
                    code.Add(new McpRegexInstruction(McpRegexOp.Save) { A = node.CaptureIndex * 2 + (backward ? 1 : 0) });
                    Emit(code, node.Children[0], backward);
                    code.Add(new McpRegexInstruction(McpRegexOp.Save) { A = node.CaptureIndex * 2 + (backward ? 0 : 1) });
                    return;
                case McpRegexNodeKind.Start:
                    code.Add(new McpRegexInstruction(McpRegexOp.Start));
                    return;
                case McpRegexNodeKind.End:
                    code.Add(new McpRegexInstruction(McpRegexOp.End));
                    return;
                case McpRegexNodeKind.WordBoundary:
                    code.Add(new McpRegexInstruction(McpRegexOp.WordBoundary));
                    return;
                case McpRegexNodeKind.NotWordBoundary:
                    code.Add(new McpRegexInstruction(McpRegexOp.NotWordBoundary));
                    return;
                case McpRegexNodeKind.Backreference:
                    code.Add(new McpRegexInstruction(McpRegexOp.Backreference) { A = node.CaptureIndex, Backward = backward });
                    return;
                case McpRegexNodeKind.Lookaround:
                    code.Add(new McpRegexInstruction(McpRegexOp.Lookaround)
                    {
                        Program = Compile(node.Children[0], node.Behind),
                        Negated = node.Negated
                    });
                    return;
                case McpRegexNodeKind.Quantifier:
                    EmitQuantifier(code, node, backward);
                    return;
            }
        }

        private void EmitAlternation(List<McpRegexInstruction> code, McpRegexNode node, bool backward)
        {
            List<McpRegexInstruction> jumps = new List<McpRegexInstruction>();
            for (int i = 0; i < node.Children.Count; i++)
            {
                McpRegexInstruction? split = null;
                if (i < node.Children.Count - 1)
                {
                    split = new McpRegexInstruction(McpRegexOp.Split);
                    code.Add(split);
                    split.A = code.Count;
                }

                Emit(code, node.Children[i], backward);
                if (split != null)
                {
                    McpRegexInstruction jump = new McpRegexInstruction(McpRegexOp.Jump);
                    code.Add(jump);
                    jumps.Add(jump);
                    split.B = code.Count;
                }
            }

            foreach (McpRegexInstruction jump in jumps) jump.A = code.Count;
        }

        private void EmitQuantifier(List<McpRegexInstruction> code, McpRegexNode node, bool backward)
        {
            McpRegexNode child = node.Children[0];
            if (node.Max == 0) return;

            // A repeated single character set needs no per-iteration bookkeeping: it never matches empty and holds no
            // captures.
            if (child.Kind == McpRegexNodeKind.CharSet && !backward)
            {
                code.Add(new McpRegexInstruction(McpRegexOp.SetLoop) { Set = child.Set, Min = node.Min, Max = node.Max, Greedy = node.Greedy });
                return;
            }

            int register = _Registers;
            _Registers += 2;
            code.Add(new McpRegexInstruction(McpRegexOp.RepeatInit) { A = register });
            McpRegexInstruction loop = new McpRegexInstruction(McpRegexOp.RepeatLoop) { A = register, Min = node.Min, Max = node.Max, Greedy = node.Greedy };
            int loopIndex = code.Count;
            code.Add(loop);
            loop.B = code.Count;
            code.Add(new McpRegexInstruction(McpRegexOp.RepeatEnter) { A = register, Min = node.FirstCapture, Max = node.LastCapture });
            Emit(code, child, backward);
            code.Add(new McpRegexInstruction(McpRegexOp.RepeatContinue) { A = register, Min = node.Min, B = loopIndex });

            // RepeatLoop: B is the body, C the exit after it.
            loop.C = code.Count;
        }
    }
}
