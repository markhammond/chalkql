using System.Security.Cryptography;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Expressions;

/// <summary>
/// The two kernels the entitlement layer ships (<c>docs/design/16-entitlements.md</c> §4). They are
/// ordinary scalar functions — nothing here knows what an entitlement is — and they exist because a
/// mask that hides a value usually needs one of two things a plain expression cannot give it: a
/// stable pseudonym, or the fact that there was a value at all.
/// </summary>
internal static class EntitlementsKernels
{
    /// <summary>The bytes of the fingerprint that reach the output: 16, rendered as 32 hex chars.</summary>
    internal const int FingerprintBytes = 16;
}

/// <summary>
/// <c>FINGERPRINT(value, key)</c>: HMAC-SHA-256 over the value's UTF-8 bytes keyed with the key's,
/// the first 16 bytes as 32 lower-case hex characters. NULL in either argument gives NULL out.
/// </summary>
/// <remarks>
/// <para>
/// A <em>stable pseudonym</em>, which is the whole point: equal values fingerprint equally under one
/// key, so an equality join on the token joins masked rows across tenancies without either value
/// being disclosed, and a host can fingerprint what a user supplied and compare it against what a
/// masked read returned. Different keys give unrelated tokens, so a key per principal or per purpose
/// keeps two audiences from correlating their tokens.
/// </para>
/// <para>
/// Truncating to 128 bits is deliberate: it is a pseudonym, not an authenticator, and 32 characters
/// fit a column a host may already have. It is keyed, so it is not reversible by a dictionary of
/// hashes the way a bare SHA-256 of a postcode is — which is exactly the failure mode a masking
/// function has to avoid.
/// </para>
/// <para>
/// Allocation-free per batch: one <see cref="HMACSHA256"/> instance is kept and reused, rebuilt only
/// when the key's bytes change — which for the literal key a folded descriptor produces is once —
/// and the digest and the hex go into reused buffers. Nothing here allocates once warm.
/// </para>
/// </remarks>
internal sealed class FingerprintExpr : VectorExprBase
{
    private readonly IVectorExpr _value;
    private readonly IVectorExpr _key;
    private readonly byte[] _digest = new byte[SHA256.HashSizeInBytes];
    private readonly byte[] _hex = new byte[EntitlementsKernels.FingerprintBytes * 2];
    private byte[] _keyBytes = [];
    private HMACSHA256? _mac;

    public FingerprintExpr(ChalkType type, IVectorExpr value, IVectorExpr key)
        : base(type)
    {
        _value = value;
        _key = key;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var value = _value.Evaluate(context);
        var key = _key.Evaluate(context);
        var length = context.Length;
        var values = VarOperand.From(value);
        var keys = VarOperand.From(key);

        Scratch.BeginVarLen(length, nullable: true);
        for (var i = 0; i < length; i++)
        {
            if (!IsValidAt(value, i) || !IsValidAt(key, i))
            {
                Scratch.AppendNull();
                continue;
            }

            Scratch.AppendValue(Token(values[i], keys[i]));
        }

        return Scratch.FinishVarLen();
    }

    /// <summary>The token for one value under one key, in this node's reused buffer.</summary>
    private ReadOnlySpan<byte> Token(ReadOnlySpan<byte> value, ReadOnlySpan<byte> key)
    {
        Rekey(key);
        _ = _mac!.TryComputeHash(value, _digest, out _);
        Hex(_digest.AsSpan(0, EntitlementsKernels.FingerprintBytes), _hex);
        return _hex;
    }

    /// <summary>
    /// Points the MAC at <paramref name="key"/>, rebuilding it only when the key actually changed.
    /// A folded descriptor's key is a literal, so this happens once per execution and never per row.
    /// </summary>
    private void Rekey(ReadOnlySpan<byte> key)
    {
        if (_mac is not null && key.SequenceEqual(_keyBytes))
        {
            return;
        }

        _mac?.Dispose();
        _keyBytes = key.ToArray();
        _mac = new HMACSHA256(_keyBytes);
    }

    /// <summary>Lower-case hex, written straight into the output buffer.</summary>
    private static void Hex(ReadOnlySpan<byte> bytes, Span<byte> into)
    {
        const string Digits = "0123456789abcdef";
        for (var i = 0; i < bytes.Length; i++)
        {
            into[i * 2] = (byte)Digits[bytes[i] >> 4];
            into[(i * 2) + 1] = (byte)Digits[bytes[i] & 0xF];
        }
    }
}

/// <summary>
/// <c>PRESENT(value)</c>: whether there is a value at all — not NULL, and for a string not empty.
/// Never NULL itself.
/// </summary>
/// <remarks>
/// A constant mask such as <c>'********'</c> replaces a value and so hides whether one existed,
/// which is usually what a host wants and occasionally not: a form that must say "a national
/// identifier is on file" without saying what it is asks this instead. Keeping it a function rather
/// than a per-column flag means the host chooses per statement, and the answer is derived from the
/// masked leaf like any other value.
/// </remarks>
internal sealed class PresentExpr : VectorExprBase
{
    private readonly IVectorExpr _operand;
    private readonly bool _isString;

    public PresentExpr(ChalkType type, IVectorExpr operand)
        : base(type)
    {
        _operand = operand;
        _isString = operand.Type.Kind is Chalk.Ir.TypeKind.String or Chalk.Ir.TypeKind.Binary;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var length = context.Length;
        var result = Scratch.Values<byte>(length);
        var lanes = _isString ? VarOperand.From(operand) : default;

        for (var i = 0; i < length; i++)
        {
            var present = IsValidAt(operand, i) && (!_isString || !lanes[i].IsEmpty);
            result[i] = (byte)(present ? 1 : 0);
        }

        Scratch.NoValidity();
        return Scratch.Finish(length, 0);
    }
}

/// <summary>
/// <c>$chalk$guarded(condition)</c> (F302, design 82): a rule's condition in the sanitising projection,
/// answered over the batch, and, should evaluating it raise, again over each row alone, NULL for a row
/// where it raises. A rule whose condition has no answer is not applied: the row falls to the next rule,
/// exactly as when the condition is false. Each row it settles so is recorded as the policy's
/// indeterminate decision, a <c>RuleNotApplied</c>, under the quarantine's tables.
/// </summary>
internal sealed class GuardedExpr : VectorExprBase
{
    private readonly IVectorExpr _condition;
    private readonly string _tables;
    private byte[] _lane = [];
    private byte[] _answers = [];
    private byte[] _known = [];

    public GuardedExpr(ChalkType type, IVectorExpr condition, string tables)
        : base(type)
    {
        _condition = condition;
        _tables = tables;
    }

    public override Vector Evaluate(EvalContext context)
    {
        try
        {
            return _condition.Evaluate(context);
        }
        catch (Exception error) when (Chalk.Sources.IndeterminateLedger.IsDataError(error))
        {
            return RowByRow(context, error);
        }
    }

    /// <summary>The condition over each selected row alone, NULL where it raises.</summary>
    private Vector RowByRow(EvalContext context, Exception first)
    {
        var length = context.Length;
        var selection = context.SelectionMask;
        var bytes = Validity.ByteCount(length);
        if (_lane.Length < Math.Max(bytes, 1))
        {
            _lane = new byte[Math.Max(bytes, 1)];
        }

        if (_answers.Length < length)
        {
            _answers = new byte[length];
            _known = new byte[length];
        }

        long settled = 0;
        for (var i = 0; i < length; i++)
        {
            _answers[i] = 0;
            _known[i] = 0;
            if (!selection.IsEmpty && !BitUtility.GetBit(selection, i))
            {
                continue;
            }

            System.Array.Clear(_lane, 0, bytes);
            BitUtility.SetBit(_lane, i);
            var scope = context.Narrow(_lane);
            try
            {
                var answer = _condition.Evaluate(context);
                if (answer.IsScalar)
                {
                    _known[i] = (byte)(answer.Scalar.IsNull ? 0 : 1);
                    _answers[i] = (byte)(!answer.Scalar.IsNull && answer.Scalar.Integer != 0 ? 1 : 0);
                }
                else
                {
                    var lanes = Lanes<byte>.From(answer);
                    _known[i] = (byte)(lanes.IsValid(i) ? 1 : 0);
                    _answers[i] = (byte)(lanes.IsValid(i) && lanes[i] != 0 ? 1 : 0);
                }
            }
            catch (Exception error) when (Chalk.Sources.IndeterminateLedger.IsDataError(error))
            {
                settled++;
            }
            finally
            {
                context.Restore(scope);
            }
        }

        if (settled > 0)
        {
            context.Arena.Indeterminate?.Record(
                _tables, Chalk.Sources.IndeterminateResolution.RuleNotApplied, settled, first);
        }

        var result = Scratch.Values<byte>(length);
        var nulls = 0;
        for (var i = 0; i < length; i++)
        {
            result[i] = _answers[i];
        }

        var bits = Scratch.BeginValidity(length);
        for (var i = 0; i < length; i++)
        {
            if (_known[i] != 0)
            {
                BitUtility.SetBit(bits, i);
            }
            else
            {
                nulls++;
            }
        }

        if (nulls == 0)
        {
            Scratch.NoValidity();
        }

        return Scratch.Finish(length, nulls);
    }
}
