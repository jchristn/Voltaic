namespace Voltaic.Mcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// An immutable set of Unicode code points (0 to U+10FFFF), stored as sorted, non-overlapping ranges. Used by the
    /// ECMA-262 regular expression engine for character classes, escapes, and Unicode properties. Thread-safe.
    /// </summary>
    internal sealed class McpRegexCharSet
    {
        internal const int MaxCodePoint = 0x10FFFF;

        private static readonly ConcurrentDictionary<string, McpRegexCharSet> _Categories = new ConcurrentDictionary<string, McpRegexCharSet>(StringComparer.Ordinal);
        private static List<int[]>[]? _CategoryRanges;
        private static readonly object _CategoryLock = new object();

        // Sorted, merged ranges: _Low[i] to _High[i], inclusive.
        private readonly int[] _Low;
        private readonly int[] _High;

        private McpRegexCharSet(int[] low, int[] high)
        {
            _Low = low;
            _High = high;
        }

        internal static McpRegexCharSet Empty { get; } = new McpRegexCharSet(Array.Empty<int>(), Array.Empty<int>());

        internal static McpRegexCharSet All { get; } = new McpRegexCharSet(new[] { 0 }, new[] { MaxCodePoint });

        // ECMA-262 LineTerminator.
        internal static McpRegexCharSet LineTerminators { get; } = FromRanges(new[] { new[] { 0x0A, 0x0A }, new[] { 0x0D, 0x0D }, new[] { 0x2028, 0x2029 } });

        // ECMA-262 \d.
        internal static McpRegexCharSet Digits { get; } = FromRanges(new[] { new[] { (int)'0', (int)'9' } });

        // ECMA-262 \w (without the i and u-case-folding combination, which JSON Schema patterns never use).
        internal static McpRegexCharSet WordCharacters { get; } = FromRanges(new[] { new[] { (int)'0', (int)'9' }, new[] { (int)'A', (int)'Z' }, new[] { (int)'_', (int)'_' }, new[] { (int)'a', (int)'z' } });

        // ECMA-262 \s: WhiteSpace (TAB, VT, FF, ZWNBSP, and every Space_Separator) and LineTerminator.
        internal static McpRegexCharSet Whitespace { get; } = FromRanges(new[] { new[] { 0x09, 0x0D }, new[] { 0x20, 0x20 }, new[] { 0xA0, 0xA0 }, new[] { 0xFEFF, 0xFEFF }, new[] { 0x2028, 0x2029 } }).Union(Category("Zs") ?? Empty);

        internal static McpRegexCharSet Single(int codePoint)
        {
            return new McpRegexCharSet(new[] { codePoint }, new[] { codePoint });
        }

        internal static McpRegexCharSet Range(int low, int high)
        {
            return new McpRegexCharSet(new[] { low }, new[] { high });
        }

        internal static McpRegexCharSet FromRanges(IEnumerable<int[]> ranges)
        {
            List<int[]> sorted = new List<int[]>(ranges);
            sorted.Sort((a, b) => a[0].CompareTo(b[0]));
            List<int> low = new List<int>();
            List<int> high = new List<int>();
            foreach (int[] range in sorted)
            {
                if (range[0] > range[1]) continue;
                if (low.Count > 0 && range[0] <= high[high.Count - 1] + 1)
                {
                    if (range[1] > high[high.Count - 1]) high[high.Count - 1] = range[1];
                    continue;
                }

                low.Add(range[0]);
                high.Add(range[1]);
            }

            return new McpRegexCharSet(low.ToArray(), high.ToArray());
        }

        internal bool Contains(int codePoint)
        {
            int lo = 0;
            int hi = _Low.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (codePoint < _Low[mid]) hi = mid - 1;
                else if (codePoint > _High[mid]) lo = mid + 1;
                else return true;
            }

            return false;
        }

        internal McpRegexCharSet Union(McpRegexCharSet other)
        {
            List<int[]> ranges = new List<int[]>(_Low.Length + other._Low.Length);
            for (int i = 0; i < _Low.Length; i++) ranges.Add(new[] { _Low[i], _High[i] });
            for (int i = 0; i < other._Low.Length; i++) ranges.Add(new[] { other._Low[i], other._High[i] });
            return FromRanges(ranges);
        }

        internal McpRegexCharSet Complement()
        {
            List<int[]> ranges = new List<int[]>();
            int next = 0;
            for (int i = 0; i < _Low.Length; i++)
            {
                if (_Low[i] > next) ranges.Add(new[] { next, _Low[i] - 1 });
                next = _High[i] + 1;
            }

            if (next <= MaxCodePoint) ranges.Add(new[] { next, MaxCodePoint });
            return FromRanges(ranges);
        }

        /// <summary>
        /// Returns the code points of a Unicode general category, by its two-letter code (such as <c>Lu</c>) or its
        /// one-letter group (<c>L</c>, <c>M</c>, <c>N</c>, <c>P</c>, <c>S</c>, <c>Z</c>, <c>C</c>), or <c>LC</c> (cased
        /// letters). Null for an unknown code. Uses the Unicode data of the running .NET version.
        /// </summary>
        internal static McpRegexCharSet? Category(string code)
        {
            if (_Categories.TryGetValue(code, out McpRegexCharSet? cached)) return cached;

            List<UnicodeCategory> members = new List<UnicodeCategory>();
            foreach (UnicodeCategory category in Enum.GetValues(typeof(UnicodeCategory)))
            {
                string shortCode = ShortCode(category);
                if (shortCode == code || (code.Length == 1 && shortCode[0] == code[0])
                    || (code == "LC" && (category == UnicodeCategory.UppercaseLetter || category == UnicodeCategory.LowercaseLetter || category == UnicodeCategory.TitlecaseLetter)))
                {
                    members.Add(category);
                }
            }

            if (members.Count == 0) return null;
            List<int[]>[] byCategory = CategoryRanges();
            List<int[]> ranges = new List<int[]>();
            foreach (UnicodeCategory member in members) ranges.AddRange(byCategory[(int)member]);
            McpRegexCharSet set = FromRanges(ranges);
            _Categories[code] = set;
            return set;
        }

        private static List<int[]>[] CategoryRanges()
        {
            lock (_CategoryLock)
            {
                if (_CategoryRanges != null) return _CategoryRanges;
                int count = Enum.GetValues(typeof(UnicodeCategory)).Length;
                List<int[]>[] ranges = new List<int[]>[count];
                for (int i = 0; i < count; i++) ranges[i] = new List<int[]>();

                int start = 0;
                UnicodeCategory current = CharUnicodeInfo.GetUnicodeCategory(0);
                for (int codePoint = 1; codePoint <= MaxCodePoint + 1; codePoint++)
                {
                    UnicodeCategory category = codePoint <= MaxCodePoint ? CharUnicodeInfo.GetUnicodeCategory(codePoint) : (UnicodeCategory)(-1);
                    if (category == current) continue;
                    ranges[(int)current].Add(new[] { start, codePoint - 1 });
                    start = codePoint;
                    current = category;
                }

                _CategoryRanges = ranges;
                return ranges;
            }
        }

        private static string ShortCode(UnicodeCategory category)
        {
            switch (category)
            {
                case UnicodeCategory.UppercaseLetter: return "Lu";
                case UnicodeCategory.LowercaseLetter: return "Ll";
                case UnicodeCategory.TitlecaseLetter: return "Lt";
                case UnicodeCategory.ModifierLetter: return "Lm";
                case UnicodeCategory.OtherLetter: return "Lo";
                case UnicodeCategory.NonSpacingMark: return "Mn";
                case UnicodeCategory.SpacingCombiningMark: return "Mc";
                case UnicodeCategory.EnclosingMark: return "Me";
                case UnicodeCategory.DecimalDigitNumber: return "Nd";
                case UnicodeCategory.LetterNumber: return "Nl";
                case UnicodeCategory.OtherNumber: return "No";
                case UnicodeCategory.SpaceSeparator: return "Zs";
                case UnicodeCategory.LineSeparator: return "Zl";
                case UnicodeCategory.ParagraphSeparator: return "Zp";
                case UnicodeCategory.Control: return "Cc";
                case UnicodeCategory.Format: return "Cf";
                case UnicodeCategory.Surrogate: return "Cs";
                case UnicodeCategory.PrivateUse: return "Co";
                case UnicodeCategory.ConnectorPunctuation: return "Pc";
                case UnicodeCategory.DashPunctuation: return "Pd";
                case UnicodeCategory.OpenPunctuation: return "Ps";
                case UnicodeCategory.ClosePunctuation: return "Pe";
                case UnicodeCategory.InitialQuotePunctuation: return "Pi";
                case UnicodeCategory.FinalQuotePunctuation: return "Pf";
                case UnicodeCategory.OtherPunctuation: return "Po";
                case UnicodeCategory.MathSymbol: return "Sm";
                case UnicodeCategory.CurrencySymbol: return "Sc";
                case UnicodeCategory.ModifierSymbol: return "Sk";
                case UnicodeCategory.OtherSymbol: return "So";
                case UnicodeCategory.OtherNotAssigned: return "Cn";
                default: return "Cn";
            }
        }
    }
}
