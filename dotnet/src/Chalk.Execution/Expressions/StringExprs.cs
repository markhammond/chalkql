using System.Text;
using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>SQL <c>||</c>: NULL when any operand is NULL (<c>02-ir.md</c> §6).</summary>
internal sealed class ConcatExpr : VectorExprBase
{
    private readonly IVectorExpr[] _operands;
    private readonly Vector[] _vectors;
    private byte[] _buffer = new byte[64];

    public ConcatExpr(ChalkType type, IVectorExpr[] operands)
        : base(type)
    {
        _operands = operands;
        _vectors = new Vector[operands.Length];
    }

    public override Vector Evaluate(EvalContext context)
    {
        var length = context.Length;
        for (var k = 0; k < _operands.Length; k++)
        {
            _vectors[k] = _operands[k].Evaluate(context);
        }

        Scratch.BeginVarLen(length, nullable: true);
        for (var i = 0; i < length; i++)
        {
            var used = 0;
            var isNull = false;
            for (var k = 0; k < _vectors.Length && !isNull; k++)
            {
                if (!IsValidAt(_vectors[k], i))
                {
                    isNull = true;
                    break;
                }

                var part = VarOperand.From(_vectors[k])[i];
                Reserve(used + part.Length);
                part.CopyTo(_buffer.AsSpan(used));
                used += part.Length;
            }

            if (isNull)
            {
                Scratch.AppendNull();
            }
            else
            {
                Scratch.AppendValue(_buffer.AsSpan(0, used));
            }
        }

        return Scratch.FinishVarLen();
    }

    private void Reserve(int required)
    {
        if (_buffer.Length < required)
        {
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}

/// <summary>
/// UPPER and LOWER: culture-invariant simple case mapping (<c>02-ir.md</c> §6). The decode/map/encode
/// scratch is owned by the node and reused, so a batch costs no allocation per row.
/// </summary>
internal sealed class CaseMapExpr : VectorExprBase
{
    private readonly IVectorExpr _operand;
    private readonly bool _upper;
    private char[] _chars = new char[64];
    private char[] _mapped = new char[64];
    private byte[] _bytes = new byte[64];

    public CaseMapExpr(ChalkType type, IVectorExpr operand, bool upper)
        : base(type)
    {
        _operand = operand;
        _upper = upper;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var length = context.Length;
        var lanes = VarOperand.From(operand);

        Scratch.BeginVarLen(length, nullable: true);
        for (var i = 0; i < length; i++)
        {
            if (!IsValidAt(operand, i))
            {
                Scratch.AppendNull();
                continue;
            }

            var utf8 = lanes[i];
            var charCount = Encoding.UTF8.GetCharCount(utf8);
            if (_chars.Length < charCount)
            {
                _chars = new char[charCount];
                _mapped = new char[charCount];
            }

            var decoded = Encoding.UTF8.GetChars(utf8, _chars);
            var span = _chars.AsSpan(0, decoded);
            var target = _mapped.AsSpan(0, decoded);
            _ = _upper ? span.ToUpperInvariant(target) : span.ToLowerInvariant(target);

            var maxBytes = Encoding.UTF8.GetMaxByteCount(decoded);
            if (_bytes.Length < maxBytes)
            {
                _bytes = new byte[maxBytes];
            }

            var written = Encoding.UTF8.GetBytes(target, _bytes);
            Scratch.AppendValue(_bytes.AsSpan(0, written));
        }

        return Scratch.FinishVarLen();
    }
}

/// <summary>CHAR_LENGTH, measured in code points.</summary>
internal sealed class CharLengthExpr : VectorExprBase
{
    private readonly IVectorExpr _operand;

    public CharLengthExpr(ChalkType type, IVectorExpr operand)
        : base(type) => _operand = operand;

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var length = context.Length;
        var nulls = InheritValidity(length, operand, context.SelectionMask);
        var result = Scratch.Values<int>(length);
        var lanes = VarOperand.From(operand);
        for (var i = 0; i < length; i++)
        {
            result[i] = IsValidAt(operand, i) ? Utf8Text.CodePointCount(lanes[i]) : 0;
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>
/// SQL:2011 <c>SUBSTRING(value FROM start [FOR length])</c>. Start is 1-based and may be zero or
/// negative: the window runs from <c>start</c> to <c>start + length</c> and is then clipped to the
/// string, so <c>SUBSTRING('abcdef' FROM -1 FOR 4)</c> is <c>'ab'</c>. A negative length is a data
/// error, not an empty string.
/// </summary>
internal sealed class SubstringExpr : VectorExprBase
{
    private readonly IVectorExpr _value;
    private readonly IVectorExpr _start;
    private readonly IVectorExpr? _length;
    private readonly Vector[] _operands;

    public SubstringExpr(ChalkType type, IVectorExpr value, IVectorExpr start, IVectorExpr? length)
        : base(type)
    {
        _value = value;
        _start = start;
        _length = length;
        _operands = new Vector[length is null ? 2 : 3];
    }

    public override Vector Evaluate(EvalContext context)
    {
        var value = _value.Evaluate(context);
        var start = _start.Evaluate(context);
        var count = _length?.Evaluate(context);
        var rows = context.Length;

        _operands[0] = value;
        _operands[1] = start;
        if (count is not null)
        {
            _operands[2] = count.Value;
        }

        var lanes = VarOperand.From(value);
        var starts = Lanes<int>.From(start);
        var counts = count is null ? default : Lanes<int>.From(count.Value);

        Scratch.BeginVarLen(rows, nullable: true);
        for (var i = 0; i < rows; i++)
        {
            var valid = true;
            foreach (var operand in _operands)
            {
                valid &= IsValidAt(operand, i);
            }

            if (!valid)
            {
                Scratch.AppendNull();
                continue;
            }

            var utf8 = lanes[i];
            var total = Utf8Text.CodePointCount(utf8);
            long from = starts[i];
            long end;
            if (count is null)
            {
                end = Math.Max(total + 1L, from);
            }
            else
            {
                long span = counts[i];
                if (span < 0)
                {
                    throw new InvalidOperationException(
                        $"SUBSTRING was given a length of {span}; SQL:2011 makes a negative length a data error.");
                }

                end = from + span;
            }

            var first = Math.Max(from, 1L);
            var last = Math.Min(end, total + 1L);
            if (last <= first)
            {
                Scratch.AppendValue([]);
                continue;
            }

            Scratch.AppendValue(Utf8Text.Slice(utf8, (int)(first - 1), (int)(last - first)));
        }

        return Scratch.FinishVarLen();
    }
}

/// <summary>
/// <c>LIKE</c> against a constant pattern, compiled once into literal / any / one tokens (§6.4).
/// A non-constant pattern is rejected at plan compilation, never here.
/// </summary>
internal sealed class LikeExpr : VectorExprBase
{
    private readonly IVectorExpr _value;
    private readonly LikeMatcher _matcher;

    public LikeExpr(ChalkType type, IVectorExpr value, LikeMatcher matcher)
        : base(type)
    {
        _value = value;
        _matcher = matcher;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var value = _value.Evaluate(context);
        var length = context.Length;
        var nulls = InheritValidity(length, value, context.SelectionMask);
        var result = Scratch.Values<byte>(length);
        var lanes = VarOperand.From(value);
        for (var i = 0; i < length; i++)
        {
            result[i] = (byte)(IsValidAt(value, i) && _matcher.Matches(lanes[i]) ? 1 : 0);
        }

        return Scratch.Finish(length, nulls);
    }
}

/// <summary>
/// A compiled LIKE pattern. <c>%</c> matches any run of code points, <c>_</c> exactly one; an escape
/// character makes the next character literal. Runs of <c>%</c> collapse, so the matcher backtracks
/// over at most one wildcard position at a time.
/// </summary>
internal sealed class LikeMatcher
{
    private enum TokenKind
    {
        Literal,
        Any,
        One,
    }

    private readonly TokenKind[] _kinds;
    private readonly byte[][] _literals;

    private LikeMatcher(TokenKind[] kinds, byte[][] literals)
    {
        _kinds = kinds;
        _literals = literals;
    }

    /// <summary>Compiles a UTF-8 pattern, with an optional single-code-point escape.</summary>
    public static LikeMatcher Compile(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> escape)
    {
        var kinds = new List<TokenKind>();
        var literals = new List<byte[]>();
        var current = new List<byte>();

        void FlushLiteral()
        {
            if (current.Count == 0)
            {
                return;
            }

            kinds.Add(TokenKind.Literal);
            literals.Add(current.ToArray());
            current.Clear();
        }

        for (var i = 0; i < pattern.Length;)
        {
            var width = Utf8Text.CodePointLength(pattern, i);
            var code = pattern.Slice(i, width);

            if (!escape.IsEmpty && code.SequenceEqual(escape))
            {
                i += width;
                if (i >= pattern.Length)
                {
                    // A trailing escape has nothing to escape; treat it as itself rather than failing
                    // mid-stream, since the pattern is a constant and this is not a plan error.
                    current.AddRange(escape);
                    break;
                }

                var nextWidth = Utf8Text.CodePointLength(pattern, i);
                current.AddRange(pattern.Slice(i, nextWidth));
                i += nextWidth;
                continue;
            }

            if (width == 1 && code[0] == (byte)'%')
            {
                FlushLiteral();
                if (kinds.Count == 0 || kinds[^1] != TokenKind.Any)
                {
                    kinds.Add(TokenKind.Any);
                    literals.Add([]);
                }

                i += width;
                continue;
            }

            if (width == 1 && code[0] == (byte)'_')
            {
                FlushLiteral();
                kinds.Add(TokenKind.One);
                literals.Add([]);
                i += width;
                continue;
            }

            current.AddRange(code);
            i += width;
        }

        FlushLiteral();
        return new LikeMatcher([.. kinds], [.. literals]);
    }

    public bool Matches(ReadOnlySpan<byte> value)
    {
        var token = 0;
        var position = 0;
        var starToken = -1;
        var starPosition = 0;

        while (position < value.Length)
        {
            if (token < _kinds.Length)
            {
                switch (_kinds[token])
                {
                    case TokenKind.Literal when value[position..].StartsWith(_literals[token]):
                        position += _literals[token].Length;
                        token++;
                        continue;
                    case TokenKind.One:
                        position += Utf8Text.CodePointLength(value, position);
                        token++;
                        continue;
                    case TokenKind.Any:
                        starToken = token++;
                        starPosition = position;
                        continue;
                    default:
                        break;
                }
            }

            if (starToken < 0)
            {
                return false;
            }

            // Give the last '%' one more code point and retry from just after it.
            if (starPosition >= value.Length)
            {
                return false;
            }

            starPosition += Utf8Text.CodePointLength(value, starPosition);

            position = starPosition;
            token = starToken + 1;
        }

        while (token < _kinds.Length && _kinds[token] == TokenKind.Any)
        {
            token++;
        }

        return token == _kinds.Length;
    }
}
