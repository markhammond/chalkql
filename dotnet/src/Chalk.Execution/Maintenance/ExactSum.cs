namespace Chalk.Execution.Maintenance;

/// <summary>
/// A sum of doubles kept without rounding, rounded once when it is read (D343, design 68 §4): what a
/// maintained aggregate keeps for <c>SUM</c> over <c>DOUBLE</c> or <c>REAL</c>, so that rows may be
/// added and taken out in any order and the sum never drifts.
/// </summary>
/// <remarks>
/// <para>
/// Every finite double is an integer multiple of 2<sup>-1074</sup>, so the exact sum is an integer
/// in those units. It is held as 32-bit digits in 64-bit words, each digit standing for 2<sup>32k</sup>
/// units, over a window of the digits the values seen have reached, which for prices and quantities is
/// a handful. A word absorbs some 2<sup>30</sup> additions before its carries are propagated.
/// </para>
/// <para>
/// NaN and the infinities are counted apart. Read, the sum is NaN when a NaN is held, or both
/// infinities are; otherwise an infinity when one is held; otherwise the exact sum rounded to the
/// nearest double, ties to even, which overflows to an infinity only when the exact sum is out of range.
/// An exact zero is +0.0, as the executor's sum of normalised zeros is.
/// </para>
/// </remarks>
internal sealed class ExactSum
{
    /// <summary>Digits for every bit position a double reaches, with room for carries above.</summary>
    private const int Digits = 72;

    /// <summary>Additions, in units of weight, after which the carries are propagated.</summary>
    private const long Headroom = 1L << 29;

    private long[] _digits = [];
    private int _low;
    private long _pending;
    private long _nans;
    private long _positiveInfinities;
    private long _negativeInfinities;

    /// <summary>The last rounding, kept until the sum changes: a group's old row reads it again.</summary>
    private double _rounded;
    private bool _roundedValid;
    private float _roundedSingle;
    private bool _roundedSingleValid;

    [ThreadStatic]
    private static long[]? _magnitude;

    /// <summary>Bytes held: the sum itself and its digits, at their size (F223).</summary>
    public long Bytes => Self + HeapSizes.Array(_digits.Length, sizeof(long));

    // The sum's own object: its header, its digits and its counts.
    private const int Self = 80;

    public void Clear()
    {
        _roundedValid = false;
        _roundedSingleValid = false;
        Array.Clear(_digits);
        _pending = 0;
        _nans = 0;
        _positiveInfinities = 0;
        _negativeInfinities = 0;
    }

    /// <summary>Adds <paramref name="weight"/> copies of <paramref name="value"/>; a negative weight takes them out.</summary>
    public void Add(double value, long weight)
    {
        if (weight == 0)
        {
            return;
        }

        _roundedValid = false;
        _roundedSingleValid = false;
        if (Math.Abs(weight) >= Headroom)
        {
            // A weight this large never comes from a view's rows; split it rather than overflow a word.
            var half = weight / 2;
            Add(value, half);
            Add(value, weight - half);
            return;
        }

        if (double.IsNaN(value))
        {
            _nans += weight;
            return;
        }

        if (double.IsInfinity(value))
        {
            if (value > 0)
            {
                _positiveInfinities += weight;
            }
            else
            {
                _negativeInfinities += weight;
            }

            return;
        }

        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & ((1L << 52) - 1);
        if (exponent == 0)
        {
            if (mantissa == 0)
            {
                return;
            }
        }
        else
        {
            mantissa |= 1L << 52;
        }

        // value = mantissa * 2^(position - 1074), position >= 0.
        var position = exponent == 0 ? 0 : exponent - 1;
        if (bits < 0)
        {
            weight = -weight;
        }

        var magnitude = Math.Abs(weight);
        if (_pending + magnitude >= Headroom)
        {
            Carry();
        }

        _pending += magnitude;
        var digit = position >> 5;
        var shifted = (UInt128)(ulong)mantissa << (position & 31);
        Reach(digit, digit + 2);
        var at = digit - _low;
        _digits[at] += weight * (long)(ulong)(shifted & 0xFFFF_FFFF);
        _digits[at + 1] += weight * (long)(ulong)((shifted >> 32) & 0xFFFF_FFFF);
        _digits[at + 2] += weight * (long)(ulong)(shifted >> 64);
    }

    /// <summary>Adds everything <paramref name="other"/> holds, exactly.</summary>
    public void Merge(ExactSum other)
    {
        _roundedValid = false;
        _roundedSingleValid = false;
        _nans += other._nans;
        _positiveInfinities += other._positiveInfinities;
        _negativeInfinities += other._negativeInfinities;
        other.Carry();
        if (other._digits.Length == 0)
        {
            return;
        }

        // Each of the other's digits is now under 2^32 but its top one, which is small: one unit of headroom.
        if (_pending + 2 >= Headroom)
        {
            Carry();
        }

        _pending += 2;
        Reach(other._low, other._low + other._digits.Length - 1);
        for (var i = 0; i < other._digits.Length; i++)
        {
            _digits[other._low + i - _low] += other._digits[i];
        }
    }

    /// <summary>The sum, rounded once to the nearest double.</summary>
    public double Round()
    {
        if (!_roundedValid)
        {
            _rounded = RoundTo(mantissaBits: 53, lowestPosition: 0, limitPosition: 2098);
            _roundedValid = true;
        }

        return _rounded;
    }

    /// <summary>The sum, rounded once to the nearest single: a <c>REAL</c> sum, never rounded through a double.</summary>
    public float RoundSingle()
    {
        if (!_roundedSingleValid)
        {
            _roundedSingle = (float)RoundTo(mantissaBits: 24, lowestPosition: 1074 - 149, limitPosition: 1074 + 128);
            _roundedSingleValid = true;
        }

        return _roundedSingle;
    }

    /// <summary>
    /// The exact sum rounded to a binary floating-point format: <paramref name="mantissaBits"/> of
    /// significand, its least subnormal unit at <paramref name="lowestPosition"/>, and its first
    /// overflowing power of two at <paramref name="limitPosition"/>, all in 2<sup>-1074</sup> units.
    /// </summary>
    private double RoundTo(int mantissaBits, int lowestPosition, int limitPosition)
    {
        if (_nans != 0 || (_positiveInfinities != 0 && _negativeInfinities != 0))
        {
            return double.NaN;
        }

        if (_positiveInfinities != 0)
        {
            return double.PositiveInfinity;
        }

        if (_negativeInfinities != 0)
        {
            return double.NegativeInfinity;
        }

        Carry();
        if (_digits.Length == 0)
        {
            return 0d;
        }

        // After the carries every digit is in [0, 2^32) but the top one, which carries the sign. The
        // magnitude has a digit more above, which a negation's carries can reach, and room for three
        // empty ones past that, so that a significand's window is always three whole digits.
        var negative = _digits[^1] < 0;
        var length = _digits.Length + 1;
        var magnitude = _magnitude is { } scratch && scratch.Length >= length + 3 ? scratch : _magnitude = new long[length + 8];
        magnitude.AsSpan(0, length + 3).Clear();
        _digits.AsSpan().CopyTo(magnitude);
        if (negative)
        {
            for (var i = 0; i < magnitude.Length; i++)
            {
                magnitude[i] = -magnitude[i];
            }

            Propagate(magnitude.AsSpan(0, length));
        }

        var top = length - 1;
        while (top >= 0 && magnitude[top] == 0)
        {
            top--;
        }

        if (top < 0)
        {
            return 0d;
        }

        var topBit = ((top + _low) * 32) + (63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)magnitude[top]));
        var lsb = Math.Max(lowestPosition, topBit - (mantissaBits - 1));
        var significand = Window(magnitude, lsb, topBit - lsb + 1);
        var roundBit = lsb > 0 && Bit(magnitude, lsb - 1);
        var sticky = lsb > 1 && AnyBelow(magnitude, lsb - 1);
        if (roundBit && (sticky || (significand & 1) != 0))
        {
            significand++;
            if (significand >> mantissaBits != 0)
            {
                significand >>= 1;
                lsb++;
            }
        }

        if (lsb + mantissaBits > limitPosition)
        {
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        var result = Math.ScaleB(significand, lsb - 1074);
        return negative ? -result : result;
    }

    /// <summary>Propagates every carry, so each digit but the top is in [0, 2^32).</summary>
    private void Carry()
    {
        _pending = 0;
        if (_digits.Length == 0)
        {
            return;
        }

        var carry = Propagate(_digits);
        if (carry != 0)
        {
            Reach(_low + _digits.Length, _low + _digits.Length);
            _digits[^1] += carry;
        }
    }

    /// <summary>Carries each digit's overflow into the next, and returns what leaves the top.</summary>
    private static long Propagate(long[] digits) => Propagate(digits.AsSpan());

    private static long Propagate(Span<long> digits)
    {
        long carry = 0;
        for (var i = 0; i < digits.Length - 1; i++)
        {
            var value = digits[i] + carry;
            carry = value >> 32;
            digits[i] = value - (carry << 32);
        }

        digits[^1] += carry;
        var top = digits[^1];
        if (top is >= int.MinValue and <= int.MaxValue)
        {
            return 0;
        }

        carry = top >> 32;
        digits[^1] = top - (carry << 32);
        return carry;
    }

    /// <summary>Widens the window to hold digits <paramref name="from"/> through <paramref name="to"/>.</summary>
    private void Reach(int from, int to)
    {
        if (_digits.Length == 0)
        {
            _low = Math.Max(0, Math.Min(from, Digits - 4));
            _digits = new long[Math.Max(4, to - _low + 2)];
            return;
        }

        var high = _low + _digits.Length - 1;
        if (from >= _low && to <= high)
        {
            return;
        }

        var low = Math.Min(_low, from);
        var newHigh = Math.Max(high, to) + 1;
        var widened = new long[newHigh - low + 1];
        Array.Copy(_digits, 0, widened, _low - low, _digits.Length);
        _digits = widened;
        _low = low;
    }

    /// <summary>Bit <paramref name="position"/> of a non-negative digit array over this window.</summary>
    private bool Bit(long[] digits, int position)
    {
        var digit = (position >> 5) - _low;
        return digit >= 0 && digit < digits.Length && ((digits[digit] >> (position & 31)) & 1) != 0;
    }

    /// <summary><paramref name="count"/> (at most 53) bits from <paramref name="position"/> up, a word at a time.</summary>
    private ulong Window(long[] digits, int position, int count)
    {
        var digit = (position >> 5) - _low;
        UInt128 value = 0;
        for (var i = 2; i >= 0; i--)
        {
            var at = digit + i;
            value = (value << 32) | (at >= 0 && at < digits.Length ? (uint)digits[at] : 0u);
        }

        return (ulong)(value >> (position & 31)) & ((1UL << count) - 1);
    }

    /// <summary>Whether any bit below <paramref name="position"/> is set.</summary>
    private bool AnyBelow(long[] digits, int position)
    {
        var digit = (position >> 5) - _low;
        for (var i = 0; i < digits.Length && i < digit; i++)
        {
            if (digits[i] != 0)
            {
                return true;
            }
        }

        if (digit < 0 || digit >= digits.Length)
        {
            return false;
        }

        var mask = (1L << (position & 31)) - 1;
        return (digits[digit] & mask) != 0;
    }
}
