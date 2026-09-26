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
        private McpJsonNumber(bool negative, BigInteger digits, BigInteger exponent)
        {
            Negative = negative && !digits.IsZero;
            Digits = digits;
            Exponent = exponent;
        }

        // The value is (Negative ? -1 : 1) * Digits * 10^Exponent; Digits has no trailing zeros (or is zero).
        internal bool Negative { get; }

        internal BigInteger Digits { get; }

        internal BigInteger Exponent { get; }

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

            BigInteger exponent = BigInteger.Zero;
            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                bool negativeExponent = index < text.Length && text[index] == '-';
                if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                int exponentStart = index;
                while (index < text.Length && Char.IsAsciiDigit(text[index])) index++;
                if (index == exponentStart) return null;
                exponent = BigInteger.Parse(text.Substring(exponentStart, index - exponentStart), NumberStyles.None, CultureInfo.InvariantCulture);
                if (negativeExponent) exponent = -exponent;
            }

            if (index != text.Length) return null;

            BigInteger digits = BigInteger.Parse(integerPart + fraction, NumberStyles.None, CultureInfo.InvariantCulture);
            exponent -= fraction.Length;
            if (digits.IsZero) return new McpJsonNumber(false, BigInteger.Zero, BigInteger.Zero);

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
            BigInteger leftMagnitude = DigitCount(left.Digits) + left.Exponent;
            BigInteger rightMagnitude = DigitCount(right.Digits) + right.Exponent;
            int absolute;
            if (leftMagnitude != rightMagnitude)
            {
                absolute = leftMagnitude.CompareTo(rightMagnitude);
            }
            else
            {
                // Equal magnitudes: the exponents differ by less than the digit counts, so aligning them is cheap.
                int shift = (int)(left.Exponent - right.Exponent);
                BigInteger a = shift > 0 ? left.Digits * BigInteger.Pow(10, shift) : left.Digits;
                BigInteger b = shift < 0 ? right.Digits * BigInteger.Pow(10, -shift) : right.Digits;
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

            BigInteger shift = value.Exponent - divisor.Exponent;
            if (shift.Sign < 0)
            {
                // value's digits have no trailing zeros, so they cannot be divisible by divisor * 10^-shift.
                return false;
            }

            BigInteger remainder = (value.Digits % divisor.Digits) * BigInteger.ModPow(10, shift, divisor.Digits) % divisor.Digits;
            return remainder.IsZero;
        }

        /// <summary>
        /// Gets whether the number is a non-negative integer (1.0 counts).
        /// </summary>
        internal bool IsNonNegativeInteger => !Negative && (Digits.IsZero || Exponent.Sign >= 0);

        /// <summary>
        /// Gets whether the number is greater than zero.
        /// </summary>
        internal bool IsPositive => !Negative && !Digits.IsZero;

        /// <summary>
        /// Gets a canonical text for the value (equal numbers, such as 1, 1.0, and 10e-1, give the same text).
        /// </summary>
        internal string Canonical => Digits.IsZero ? "0" : (Negative ? "-" : String.Empty) + Digits.ToString(CultureInfo.InvariantCulture) + "e" + Exponent.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Returns the value as a count: the number itself when it fits, <see cref="Int64.MaxValue"/> for larger
        /// non-negative integers. Only meaningful when <see cref="IsNonNegativeInteger"/>.
        /// </summary>
        internal long ToCount()
        {
            if (Digits.IsZero) return 0;
            if (DigitCount(Digits) + Exponent > 18) return Int64.MaxValue;
            return (long)(Digits * BigInteger.Pow(10, (int)Exponent));
        }

        private static BigInteger DigitCount(BigInteger digits)
        {
            return digits.IsZero ? 1 : digits.ToString(CultureInfo.InvariantCulture).Length;
        }
    }
}
