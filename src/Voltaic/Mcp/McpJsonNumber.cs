namespace Voltaic.Mcp
{
    using System;
    using System.Globalization;
    using System.Numerics;

    /// <summary>
    /// An exact JSON number: sign, significant digits, and a decimal exponent, parsed from the number's text so no
    /// precision is lost. Used by JSON Schema's numeric keywords. The significant digits are kept as text, so parsing,
    /// comparing, and the canonical form take time linear in the number's length, and any number of digits is exact.
    /// The written exponent may have at most <see cref="MaxExponentDigits"/> digits (a magnitude beyond 10 to the
    /// power 10^1000 is not supported): <see cref="Parse"/> returns null for a larger one, which validation rejects.
    /// Immutable and thread-safe.
    /// </summary>
    internal sealed class McpJsonNumber
    {
        // Exponent texts up to this many digits are parsed directly; longer ones are split in halves.
        private const int _DirectParseDigits = 1000;

        /// <summary>
        /// The most digits a number's written exponent may have (after leading zeros). 1000.
        /// </summary>
        internal const int MaxExponentDigits = 1000;

        /// <summary>
        /// The most significant digits a <c>multipleOf</c> divisor may have. 1000.
        /// </summary>
        internal const int MaxDivisorDigits = 1000;

        // Digits of the value taken per step when reducing it modulo a divisor.
        private const int _ModuloChunkDigits = 18;

        private McpJsonNumber(bool negative, string digits, BigInteger exponent)
        {
            Negative = negative && digits.Length > 0;
            Digits = digits;
            Exponent = exponent;
        }

        // The value is (Negative ? -1 : 1) * Digits * 10^Exponent. Digits has no leading or trailing zeros, and is
        // empty for zero.
        internal bool Negative { get; }

        internal string Digits { get; }

        internal BigInteger Exponent { get; }

        private bool IsZero => Digits.Length == 0;

        /// <summary>
        /// Parses the text of a JSON number, or returns null when it is not one.
        /// </summary>
        internal static McpJsonNumber? Parse(string text)
        {
            if (String.IsNullOrEmpty(text)) return null;
            int index = 0;
            bool negative = text[0] == '-';
            if (negative) index++;

            int integerStart = index;
            while (index < text.Length && Char.IsAsciiDigit(text[index])) index++;
            int integerLength = index - integerStart;
            if (integerLength == 0) return null;

            int fractionStart = index;
            int fractionLength = 0;
            if (index < text.Length && text[index] == '.')
            {
                fractionStart = ++index;
                while (index < text.Length && Char.IsAsciiDigit(text[index])) index++;
                fractionLength = index - fractionStart;
                if (fractionLength == 0) return null;
            }

            BigInteger exponent = BigInteger.Zero;
            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                bool negativeExponent = index < text.Length && text[index] == '-';
                if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                int exponentStart = index;
                while (index < text.Length && Char.IsAsciiDigit(text[index])) index++;
                if (index == exponentStart) return null;
                while (exponentStart < index - 1 && text[exponentStart] == '0') exponentStart++;
                if (index - exponentStart > MaxExponentDigits) return null;
                exponent = ParseInteger(text, exponentStart, index - exponentStart);
                if (negativeExponent) exponent = -exponent;
            }

            if (index != text.Length) return null;

            // The significant digits: integer and fraction digits without leading and trailing zeros.
            string all = text.Substring(integerStart, integerLength) + text.Substring(fractionStart, fractionLength);
            int first = 0;
            while (first < all.Length && all[first] == '0') first++;
            if (first == all.Length) return new McpJsonNumber(false, String.Empty, BigInteger.Zero);
            int last = all.Length - 1;
            while (all[last] == '0') last--;

            string digits = all.Substring(first, last - first + 1);
            exponent += (all.Length - 1 - last) - fractionLength;
            return new McpJsonNumber(negative, digits, exponent);
        }

        /// <summary>
        /// Compares two numbers exactly: negative, zero, or positive as <paramref name="left"/> is below, equal to, or
        /// above <paramref name="right"/>.
        /// </summary>
        internal static int Compare(McpJsonNumber left, McpJsonNumber right)
        {
            int leftSign = left.IsZero ? 0 : (left.Negative ? -1 : 1);
            int rightSign = right.IsZero ? 0 : (right.Negative ? -1 : 1);
            if (leftSign != rightSign) return leftSign.CompareTo(rightSign);
            if (leftSign == 0) return 0;

            // Compare magnitudes by the position of the most significant digit first; at the same position, the
            // digit texts compare as written (neither has trailing zeros, so a longer text with an equal prefix is larger).
            BigInteger leftMagnitude = left.Digits.Length + left.Exponent;
            BigInteger rightMagnitude = right.Digits.Length + right.Exponent;
            int absolute = leftMagnitude != rightMagnitude
                ? leftMagnitude.CompareTo(rightMagnitude)
                : Math.Sign(String.CompareOrdinal(left.Digits, right.Digits));

            return leftSign > 0 ? absolute : -absolute;
        }

        /// <summary>
        /// Returns whether <paramref name="value"/> divided by <paramref name="divisor"/> is an integer, exactly; null when
        /// the divisor is not positive.
        /// </summary>
        internal static bool? IsMultipleOf(McpJsonNumber value, McpJsonNumber divisor)
        {
            if (divisor.IsZero || divisor.Negative) return null;
            if (value.IsZero) return true;

            BigInteger shift = value.Exponent - divisor.Exponent;
            if (shift.Sign < 0)
            {
                // value's digits have no trailing zeros, so they cannot be divisible by divisor * 10^-shift.
                return false;
            }

            // A divisor of up to 18 significant digits (almost every schema) is reduced with 128-bit arithmetic.
            if (divisor.Digits.Length <= 18)
            {
                ulong small = UInt64.Parse(divisor.Digits, NumberStyles.None, CultureInfo.InvariantCulture);
                UInt128 reduced = SmallModulo(value.Digits, small);
                UInt128 power = (UInt128)(ulong)BigInteger.ModPow(10, shift, small);
                return reduced * power % small == 0;
            }

            BigInteger modulus = ParseInteger(divisor.Digits, 0, divisor.Digits.Length);
            BigInteger remainder = Modulo(value.Digits, modulus) * BigInteger.ModPow(10, shift, modulus) % modulus;
            return remainder.IsZero;
        }

        /// <summary>
        /// Gets whether the number is a non-negative integer (1.0 counts).
        /// </summary>
        internal bool IsNonNegativeInteger => !Negative && (IsZero || Exponent.Sign >= 0);

        /// <summary>
        /// Gets whether the number is greater than zero.
        /// </summary>
        internal bool IsPositive => !Negative && !IsZero;

        /// <summary>
        /// Gets a canonical text for the value (equal numbers, such as 1, 1.0, and 10e-1, give the same text). The
        /// exponent is written in hexadecimal, which takes linear time for any size.
        /// </summary>
        internal string Canonical => IsZero ? "0" : (Negative ? "-" : String.Empty) + Digits + "x" + Exponent.ToString("X", CultureInfo.InvariantCulture);

        /// <summary>
        /// Returns the value as a count: the number itself when it fits, <see cref="Int64.MaxValue"/> for larger
        /// non-negative integers. Only meaningful when <see cref="IsNonNegativeInteger"/>.
        /// </summary>
        internal long ToCount()
        {
            if (IsZero) return 0;
            if (Digits.Length + Exponent > 18) return Int64.MaxValue;
            return Int64.Parse(Digits, NumberStyles.None, CultureInfo.InvariantCulture) * (long)BigInteger.Pow(10, (int)Exponent);
        }

        // Parses decimal digits; long texts are split in halves so the cost stays close to linear.
        private static BigInteger ParseInteger(string text, int start, int length)
        {
            if (length <= _DirectParseDigits) return BigInteger.Parse(text.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture);
            int low = length / 2;
            int high = length - low;
            return ParseInteger(text, start, high) * BigInteger.Pow(10, low) + ParseInteger(text, start + high, low);
        }

        // The digits' value modulo a modulus of up to 18 digits, one digit at a time in 128-bit arithmetic.
        private static UInt128 SmallModulo(string digits, ulong modulus)
        {
            UInt128 remainder = 0;
            foreach (char digit in digits)
            {
                remainder = (remainder * 10 + (uint)(digit - '0')) % modulus;
            }

            return remainder;
        }

        // The digits' value modulo a positive modulus, one chunk of digits at a time.
        private static BigInteger Modulo(string digits, BigInteger modulus)
        {
            BigInteger remainder = BigInteger.Zero;
            for (int start = 0; start < digits.Length; start += _ModuloChunkDigits)
            {
                int length = Math.Min(_ModuloChunkDigits, digits.Length - start);
                long chunk = Int64.Parse(digits.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture);
                remainder = (remainder * BigInteger.Pow(10, length) + chunk) % modulus;
            }

            return remainder;
        }
    }
}
