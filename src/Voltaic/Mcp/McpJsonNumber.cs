namespace Voltaic.Mcp
{
    using System;
    using System.Globalization;
    using System.Numerics;

    /// <summary>
    /// An exact JSON number: sign, digits, and a decimal exponent, parsed from the number's text so no precision is
    /// lost (JSON numbers have arbitrary precision and range). Used by JSON Schema's numeric keywords. Immutable and
    /// thread-safe.
    /// </summary>
    internal sealed class McpJsonNumber
    {
        private McpJsonNumber(bool negative, BigInteger digits, long exponent)
        {
            Negative = negative && !digits.IsZero;
            Digits = digits;
            Exponent = exponent;
        }

        // The value is (Negative ? -1 : 1) * Digits * 10^Exponent; Digits has no trailing zeros (or is zero).
        internal bool Negative { get; }

        internal BigInteger Digits { get; }

        internal long Exponent { get; }

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
            string integerPart = text.Substring(integerStart, index - integerStart);
            if (integerPart.Length == 0) return null;

            string fraction = String.Empty;
            if (index < text.Length && text[index] == '.')
            {
                int fractionStart = ++index;
                while (index < text.Length && Char.IsAsciiDigit(text[index])) index++;
                fraction = text.Substring(fractionStart, index - fractionStart);
                if (fraction.Length == 0) return null;
            }

            long exponent = 0;
            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                bool negativeExponent = index < text.Length && text[index] == '-';
                if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                int exponentStart = index;
                while (index < text.Length && Char.IsAsciiDigit(text[index]))
                {
                    exponent = Math.Min(exponent * 10 + (text[index] - '0'), Int64.MaxValue / 20);
                    index++;
                }

                if (index == exponentStart) return null;
                if (negativeExponent) exponent = -exponent;
            }

            if (index != text.Length) return null;

            BigInteger digits = BigInteger.Parse(integerPart + fraction, NumberStyles.None, CultureInfo.InvariantCulture);
            exponent -= fraction.Length;
            if (digits.IsZero) return new McpJsonNumber(false, BigInteger.Zero, 0);

            while (digits % 10 == 0)
            {
                digits /= 10;
                exponent++;
            }

            return new McpJsonNumber(negative, digits, exponent);
        }

        /// <summary>
        /// Compares two numbers exactly: negative, zero, or positive as <paramref name="left"/> is below, equal to, or
        /// above <paramref name="right"/>.
        /// </summary>
        internal static int Compare(McpJsonNumber left, McpJsonNumber right)
        {
            int leftSign = left.Digits.IsZero ? 0 : (left.Negative ? -1 : 1);
            int rightSign = right.Digits.IsZero ? 0 : (right.Negative ? -1 : 1);
            if (leftSign != rightSign) return leftSign.CompareTo(rightSign);
            if (leftSign == 0) return 0;

            // Compare magnitudes by the position of the most significant digit first, then digit by digit.
            long leftMagnitude = DigitCount(left.Digits) + left.Exponent;
            long rightMagnitude = DigitCount(right.Digits) + right.Exponent;
            int absolute;
            if (leftMagnitude != rightMagnitude)
            {
                absolute = leftMagnitude.CompareTo(rightMagnitude);
            }
            else
            {
                // Equal magnitudes: the exponents differ by less than the digit counts, so aligning them is cheap.
                long shift = left.Exponent - right.Exponent;
                BigInteger a = shift > 0 ? left.Digits * BigInteger.Pow(10, (int)shift) : left.Digits;
                BigInteger b = shift < 0 ? right.Digits * BigInteger.Pow(10, (int)-shift) : right.Digits;
                absolute = a.CompareTo(b);
            }

            return leftSign > 0 ? absolute : -absolute;
        }

        /// <summary>
        /// Returns whether <paramref name="value"/> divided by <paramref name="divisor"/> is an integer, exactly; null when
        /// the divisor is not positive.
        /// </summary>
        internal static bool? IsMultipleOf(McpJsonNumber value, McpJsonNumber divisor)
        {
            if (divisor.Digits.IsZero || divisor.Negative) return null;
            if (value.Digits.IsZero) return true;

            long shift = value.Exponent - divisor.Exponent;
            if (shift < 0)
            {
                // value's digits have no trailing zeros, so they cannot be divisible by divisor * 10^-shift.
                return false;
            }

            BigInteger remainder = (value.Digits % divisor.Digits) * BigInteger.ModPow(10, shift, divisor.Digits) % divisor.Digits;
            return remainder.IsZero;
        }

        private static long DigitCount(BigInteger digits)
        {
            return digits.IsZero ? 1 : digits.ToString(CultureInfo.InvariantCulture).Length;
        }
    }
}
