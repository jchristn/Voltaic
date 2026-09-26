namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Runs a compiled ECMA-262 regular expression against one input. A backtracking virtual machine with an explicit
    /// stack, so the input length never limits the call stack; captures and repetition registers are restored through
    /// undo entries on the same stack. Matching works on code points (the <c>u</c> flag): a surrogate pair is one
    /// character. A step budget bounds the work (catastrophic backtracking stops instead of running away). Not
    /// thread-safe; create one per match.
    /// </summary>
    internal sealed class McpRegexMatcher
    {
        private const int _Exhausted = -1;
        private readonly string _Input;
        private readonly int[] _Captures;
        private readonly int[] _Registers;
        private readonly long _MaxSteps;
        private long _Steps;

        internal McpRegexMatcher(string input, int captureCount, int registerCount, long maxSteps)
        {
            _Input = input;
            _Captures = new int[(captureCount + 1) * 2];
            _Registers = new int[Math.Max(registerCount, 1)];
            _MaxSteps = maxSteps;
        }

        /// <summary>
        /// Searches the input: true when the pattern matches starting at some code point boundary, false when it does
        /// not, and null when the step budget ran out first.
        /// </summary>
        internal bool? Search(McpRegexProgram program)
        {
            int position = 0;
            while (true)
            {
                Array.Fill(_Captures, -1);
                Array.Clear(_Registers);
                int result = Run(program, position);
                if (result == 1) return true;
                if (result == _Exhausted) return null;
                if (position >= _Input.Length) return false;
                position += CodePointLength(position);
            }
        }

        // Runs a program from a position: 1 when it reaches Match, 0 when every path fails, -1 when out of steps.
        private int Run(McpRegexProgram program, int start)
        {
            List<McpRegexInstruction> code = program.Instructions;
            List<McpRegexBacktrack> stack = new List<McpRegexBacktrack>();
            int pc = 0;
            int position = start;

            while (true)
            {
                if (++_Steps > _MaxSteps) return _Exhausted;
                McpRegexInstruction instruction = code[pc];
                bool advance = true;
                switch (instruction.Op)
                {
                    case McpRegexOp.Match:
                        return 1;

                    case McpRegexOp.Char:
                        if (instruction.Backward)
                        {
                            if (position > 0 && instruction.Set!.Contains(CodePointBefore(position, out int before))) position -= before;
                            else advance = false;
                        }
                        else if (position < _Input.Length && instruction.Set!.Contains(CodePointAt(position, out int after)))
                        {
                            position += after;
                        }
                        else
                        {
                            advance = false;
                        }

                        if (advance) pc++;
                        break;

                    case McpRegexOp.Jump:
                        pc = instruction.A;
                        break;

                    case McpRegexOp.Split:
                        stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.Branch, instruction.B, position, 0, 0));
                        pc = instruction.A;
                        break;

                    case McpRegexOp.Save:
                        stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.RestoreCapture, 0, 0, instruction.A, _Captures[instruction.A]));
                        _Captures[instruction.A] = position;
                        pc++;
                        break;

                    case McpRegexOp.Start:
                        advance = position == 0;
                        if (advance) pc++;
                        break;

                    case McpRegexOp.End:
                        advance = position == _Input.Length;
                        if (advance) pc++;
                        break;

                    case McpRegexOp.WordBoundary:
                    case McpRegexOp.NotWordBoundary:
                        bool boundary = IsWordAt(position - 1) != IsWordAt(position);
                        advance = instruction.Op == McpRegexOp.WordBoundary ? boundary : !boundary;
                        if (advance) pc++;
                        break;

                    case McpRegexOp.Backreference:
                        advance = MatchBackreference(instruction, ref position);
                        if (advance) pc++;
                        break;

                    case McpRegexOp.Lookaround:
                        int[] snapshot = (int[])_Captures.Clone();
                        int inner = Run(instruction.Program!, position);
                        if (inner == _Exhausted) return _Exhausted;
                        if (instruction.Negated)
                        {
                            Array.Copy(snapshot, _Captures, snapshot.Length);
                            advance = inner == 0;
                        }
                        else
                        {
                            advance = inner == 1;
                            if (advance)
                            {
                                // Captures set inside a positive lookaround stay set, and are undone on backtracking.
                                for (int slot = 0; slot < snapshot.Length; slot++)
                                {
                                    if (_Captures[slot] != snapshot[slot]) stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.RestoreCapture, 0, 0, slot, snapshot[slot]));
                                }
                            }
                        }

                        if (advance) pc++;
                        break;

                    case McpRegexOp.RepeatInit:
                        stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.RestoreRegister, 0, 0, instruction.A, _Registers[instruction.A]));
                        _Registers[instruction.A] = 0;
                        pc++;
                        break;

                    case McpRegexOp.RepeatLoop:
                        int count = _Registers[instruction.A];
                        if (instruction.Max >= 0 && count >= instruction.Max)
                        {
                            pc = instruction.C;
                        }
                        else if (count < instruction.Min)
                        {
                            pc = instruction.B;
                        }
                        else if (instruction.Greedy)
                        {
                            stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.Branch, instruction.C, position, 0, 0));
                            pc = instruction.B;
                        }
                        else
                        {
                            stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.Branch, instruction.B, position, 0, 0));
                            pc = instruction.C;
                        }

                        break;

                    case McpRegexOp.RepeatEnter:
                        stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.RestoreRegister, 0, 0, instruction.A + 1, _Registers[instruction.A + 1]));
                        _Registers[instruction.A + 1] = position;

                        // ECMA-262 RepeatMatcher: the captures inside the repeated atom are reset for every iteration.
                        for (int group = instruction.Min; group <= instruction.Max; group++)
                        {
                            for (int slot = group * 2; slot <= group * 2 + 1; slot++)
                            {
                                if (_Captures[slot] == -1) continue;
                                stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.RestoreCapture, 0, 0, slot, _Captures[slot]));
                                _Captures[slot] = -1;
                            }
                        }

                        pc++;
                        break;

                    case McpRegexOp.RepeatContinue:
                        int iterations = _Registers[instruction.A];

                        // An iteration past the minimum that matched the empty string fails (ECMA-262 RepeatMatcher).
                        if (iterations >= instruction.Min && position == _Registers[instruction.A + 1])
                        {
                            advance = false;
                            break;
                        }

                        stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.RestoreRegister, 0, 0, instruction.A, iterations));
                        _Registers[instruction.A] = iterations + 1;
                        pc = instruction.B;
                        break;

                    case McpRegexOp.SetLoop:
                        advance = RunSetLoop(instruction, pc, stack, ref position);
                        if (advance) pc++;
                        break;
                }

                if (advance) continue;

                // Backtrack: undo changes until an untried alternative is found.
                bool resumed = false;
                while (stack.Count > 0 && !resumed)
                {
                    McpRegexBacktrack entry = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    switch (entry.Kind)
                    {
                        case McpRegexBacktrackKind.Branch:
                            pc = entry.Pc;
                            position = entry.Position;
                            resumed = true;
                            break;
                        case McpRegexBacktrackKind.RestoreCapture:
                            _Captures[entry.Slot] = entry.Value;
                            break;
                        case McpRegexBacktrackKind.RestoreRegister:
                            _Registers[entry.Slot] = entry.Value;
                            break;
                        case McpRegexBacktrackKind.GreedyStep:
                            int shorter = entry.Position - CodePointBeforeLength(entry.Position);
                            if (shorter > entry.Value) stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.GreedyStep, entry.Pc, shorter, 0, entry.Value));
                            pc = entry.Pc;
                            position = shorter;
                            resumed = true;
                            break;
                        case McpRegexBacktrackKind.LazyStep:
                            McpRegexInstruction loop = code[entry.Pc];
                            int taken = entry.Value;
                            if ((loop.Max < 0 || taken < loop.Max) && entry.Position < _Input.Length && loop.Set!.Contains(CodePointAt(entry.Position, out int length)))
                            {
                                int longer = entry.Position + length;
                                if (loop.Max < 0 || taken + 1 < loop.Max) stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.LazyStep, entry.Pc, longer, 0, taken + 1));
                                pc = entry.Pc + 1;
                                position = longer;
                                resumed = true;
                            }

                            break;
                    }
                }

                if (!resumed) return 0;
            }
        }

        private bool RunSetLoop(McpRegexInstruction loop, int pc, List<McpRegexBacktrack> stack, ref int position)
        {
            McpRegexCharSet set = loop.Set!;
            int taken = 0;
            int current = position;

            // The minimum is mandatory for both greedy and lazy loops.
            while (taken < loop.Min)
            {
                if (current >= _Input.Length || !set.Contains(CodePointAt(current, out int length))) return false;
                current += length;
                taken++;
            }

            if (!loop.Greedy)
            {
                if (loop.Max < 0 || taken < loop.Max) stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.LazyStep, pc, current, 0, taken));
                position = current;
                return true;
            }

            int floor = current;
            while ((loop.Max < 0 || taken < loop.Max) && current < _Input.Length && set.Contains(CodePointAt(current, out int length)))
            {
                current += length;
                taken++;
            }

            if (current > floor) stack.Add(new McpRegexBacktrack(McpRegexBacktrackKind.GreedyStep, pc + 1, current, 0, floor));
            position = current;
            return true;
        }

        private bool MatchBackreference(McpRegexInstruction instruction, ref int position)
        {
            int start = _Captures[instruction.A * 2];
            int end = _Captures[instruction.A * 2 + 1];

            // A group that has not participated matches the empty string.
            if (start < 0 || end < 0) return true;
            int length = end - start;
            if (instruction.Backward)
            {
                if (position - length < 0 || String.CompareOrdinal(_Input, start, _Input, position - length, length) != 0) return false;
                position -= length;
                return true;
            }

            if (position + length > _Input.Length || String.CompareOrdinal(_Input, start, _Input, position, length) != 0) return false;
            position += length;
            return true;
        }

        private bool IsWordAt(int index)
        {
            if (index < 0 || index >= _Input.Length) return false;
            char unit = _Input[index];
            return (unit >= 'a' && unit <= 'z') || (unit >= 'A' && unit <= 'Z') || (unit >= '0' && unit <= '9') || unit == '_';
        }

        private int CodePointAt(int index, out int length)
        {
            char unit = _Input[index];
            if (Char.IsHighSurrogate(unit) && index + 1 < _Input.Length && Char.IsLowSurrogate(_Input[index + 1]))
            {
                length = 2;
                return Char.ConvertToUtf32(unit, _Input[index + 1]);
            }

            length = 1;
            return unit;
        }

        private int CodePointLength(int index)
        {
            CodePointAt(index, out int length);
            return length;
        }

        private int CodePointBefore(int index, out int length)
        {
            char unit = _Input[index - 1];
            if (Char.IsLowSurrogate(unit) && index - 2 >= 0 && Char.IsHighSurrogate(_Input[index - 2]))
            {
                length = 2;
                return Char.ConvertToUtf32(_Input[index - 2], unit);
            }

            length = 1;
            return unit;
        }

        private int CodePointBeforeLength(int index)
        {
            CodePointBefore(index, out int length);
            return length;
        }
    }
}
