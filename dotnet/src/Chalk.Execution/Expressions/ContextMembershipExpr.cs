using System.Buffers.Binary;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>
/// A membership over a context list the executor holds (F161): the tuple of this row's columns is one
/// of the rows the execution bound under the list's name. Once per execution the list becomes a hash
/// set of encoded tuples, and each row's tuple is encoded the same way and looked up — which is what
/// replaced a join against the list for every list a policy reads.
/// </summary>
/// <remarks>
/// <para>
/// SQL's <c>IN</c> over a tuple, three-valued: TRUE where some row equals the tuple in every column;
/// UNKNOWN where none does and some row differs from it only where one side is NULL; FALSE otherwise
/// — so an empty list is FALSE for every row. A NaN equals nothing, as in <see cref="InListExpr"/>.
/// </para>
/// <para>
/// The common case is one lookup. The rare UNKNOWN cases — a NULL in the row's tuple, or rows of the
/// list that hold a NULL — are settled by walking only the rows that could make one: every row for a
/// tuple holding a NULL, the rows holding one otherwise.
/// </para>
/// </remarks>
internal sealed class ContextMembershipExpr : VectorExprBase
{
    private readonly string _list;
    private readonly IVectorExpr[] _columns;
    private readonly ChalkType[] _types;
    private readonly ColumnKind[] _kinds;
    private readonly Vector[] _values;

    private ByteSet _set = new();
    private ScalarValue[][] _rows = [];
    private ScalarValue[][] _nullRows = [];
    private int _built = -1;
    private byte[] _key = new byte[64];

    public ContextMembershipExpr(ChalkType type, string list, IVectorExpr[] columns)
        : base(type)
    {
        _list = list;
        _columns = columns;

        // The list's values are bound into the columns' own types — the planner casts a key whose
        // type differs from the list's — made nullable, because a host may bind a NULL into a list
        // whatever the column holds, and the NULL is the list's to answer UNKNOWN with.
        _types = [.. columns.Select(c => c.Type.WithNullable(true))];
        _kinds = [.. columns.Select(c => ColumnKinds.Of(c.Type))];
        foreach (var kind in _kinds)
        {
            if (kind is ColumnKind.List or ColumnKind.Composite)
            {
                throw new UnsupportedFeatureException(
                    $"a membership of the context list '{list}' over a {kind} column",
                    "A context list holds scalars; the planner never compares anything else with one.");
            }
        }

        _values = new Vector[columns.Length];
    }

    public override Vector Evaluate(EvalContext context)
    {
        Build(context);
        for (var c = 0; c < _columns.Length; c++)
        {
            _values[c] = _columns[c].Evaluate(context);
        }

        var length = context.Length;
        var result = Scratch.Values<byte>(length);
        var bits = Scratch.BeginValidity(length);
        var nulls = 0;

        for (var i = 0; i < length; i++)
        {
            var answer = Answer(i);
            if (answer is null)
            {
                nulls++;
                result[i] = 0;
            }
            else
            {
                BitUtility.SetBit(bits, i);
                result[i] = (byte)(answer.Value ? 1 : 0);
            }
        }

        if (nulls == 0)
        {
            Scratch.NoValidity();
        }

        return Scratch.Finish(length, nulls);
    }

    private bool? Answer(int row)
    {
        for (var c = 0; c < _values.Length; c++)
        {
            if (!IsValidAt(_values[c], row))
            {
                // A NULL in the tuple: nothing equals it, and a row that agrees with it wherever both
                // hold a value leaves the answer unknown.
                return Unknown(row, _rows) ? null : false;
            }
        }

        var nan = false;
        var length = Encode(row, ref nan);
        if (!nan && _set.Contains(_key.AsSpan(0, length)))
        {
            return true;
        }

        // Nothing equals it, and a row with no NULL differs from it somewhere both hold a value; only a
        // row holding a NULL can leave the answer unknown.
        return Unknown(row, _nullRows) ? null : false;
    }

    /// <summary>Whether some candidate agrees with this row's tuple wherever both hold a value.</summary>
    private bool Unknown(int row, ScalarValue[][] candidates)
    {
        foreach (var candidate in candidates)
        {
            var agrees = true;
            for (var c = 0; c < _values.Length && agrees; c++)
            {
                if (!candidate[c].IsNull && IsValidAt(_values[c], row))
                {
                    agrees = Equal(c, row, candidate[c]);
                }
            }

            if (agrees)
            {
                return true;
            }
        }

        return false;
    }

    private bool Equal(int c, int row, in ScalarValue value)
    {
        var vector = _values[c];
        switch (_kinds[c])
        {
            case ColumnKind.Float:
            {
                var v = Lanes<float>.From(vector)[row];
                return !float.IsNaN(v) && v == value.Single;
            }

            case ColumnKind.Double:
            {
                var v = Lanes<double>.From(vector)[row];
                return !double.IsNaN(v) && v == value.Double;
            }

            case ColumnKind.Utf8:
            case ColumnKind.Binary:
                return VarOperand.From(vector)[row].SequenceEqual(value.ReadBytes());

            case ColumnKind.Decimal128:
            case ColumnKind.Bytes16:
                return RawLanes.From(vector, 16)[row].SequenceEqual(value.ReadBytes());

            default:
                return ReadInteger(c, row) == value.Integer;
        }
    }

    /// <summary>
    /// This row's tuple, encoded into <see cref="_key"/> as the list's rows were: a whole number as
    /// eight bytes, a floating value as its double's with −0 made +0, a string or binary value
    /// length-prefixed, and a decimal or a sixteen-byte value as it is stored.
    /// </summary>
    private int Encode(int row, ref bool nan)
    {
        var at = 0;
        for (var c = 0; c < _values.Length; c++)
        {
            var vector = _values[c];
            switch (_kinds[c])
            {
                case ColumnKind.Float:
                    at = PutDouble(at, Lanes<float>.From(vector)[row], ref nan);
                    break;
                case ColumnKind.Double:
                    at = PutDouble(at, Lanes<double>.From(vector)[row], ref nan);
                    break;
                case ColumnKind.Utf8:
                case ColumnKind.Binary:
                    at = PutBytes(at, VarOperand.From(vector)[row]);
                    break;
                case ColumnKind.Decimal128:
                case ColumnKind.Bytes16:
                    at = PutFixed(at, RawLanes.From(vector, 16)[row]);
                    break;
                default:
                    at = PutLong(at, ReadInteger(c, row));
                    break;
            }
        }

        return at;
    }

    /// <summary>One bound row of the list, encoded as <see cref="Encode"/> encodes a tuple.</summary>
    private int Encode(ScalarValue[] values, ref bool nan)
    {
        var at = 0;
        for (var c = 0; c < values.Length; c++)
        {
            var value = values[c];
            switch (_kinds[c])
            {
                case ColumnKind.Float:
                    at = PutDouble(at, value.Single, ref nan);
                    break;
                case ColumnKind.Double:
                    at = PutDouble(at, value.Double, ref nan);
                    break;
                case ColumnKind.Utf8:
                case ColumnKind.Binary:
                    at = PutBytes(at, value.ReadBytes());
                    break;
                case ColumnKind.Decimal128:
                case ColumnKind.Bytes16:
                    at = PutFixed(at, value.ReadBytes());
                    break;
                default:
                    at = PutLong(at, value.Integer);
                    break;
            }
        }

        return at;
    }

    private long ReadInteger(int c, int row) => _kinds[c] switch
    {
        ColumnKind.Boolean => Lanes<byte>.From(_values[c])[row],
        ColumnKind.Int8 => Lanes<sbyte>.From(_values[c])[row],
        ColumnKind.Int16 => Lanes<short>.From(_values[c])[row],
        ColumnKind.Int32 => Lanes<int>.From(_values[c])[row],
        _ => Lanes<long>.From(_values[c])[row],
    };

    private int PutLong(int at, long value)
    {
        Ensure(at + 8);
        BinaryPrimitives.WriteInt64LittleEndian(_key.AsSpan(at), value);
        return at + 8;
    }

    private int PutDouble(int at, double value, ref bool nan)
    {
        if (double.IsNaN(value))
        {
            nan = true;
        }

        // −0 and +0 are equal and their bits are not.
        return PutLong(at, BitConverter.DoubleToInt64Bits(value == 0 ? 0d : value));
    }

    private int PutBytes(int at, ReadOnlySpan<byte> value)
    {
        Ensure(at + 4 + value.Length);
        BinaryPrimitives.WriteInt32LittleEndian(_key.AsSpan(at), value.Length);
        value.CopyTo(_key.AsSpan(at + 4));
        return at + 4 + value.Length;
    }

    private int PutFixed(int at, ReadOnlySpan<byte> value)
    {
        Ensure(at + value.Length);
        value.CopyTo(_key.AsSpan(at));
        return at + value.Length;
    }

    private void Ensure(int length)
    {
        if (_key.Length < length)
        {
            System.Array.Resize(ref _key, Math.Max(length, _key.Length * 2));
        }
    }

    private void Build(EvalContext context)
    {
        if (_built == context.Generation)
        {
            return;
        }

        // The list belongs to one execution, not to the plan: another execution binds another.
        _built = context.Generation;
        var supplied = context.ContextRelation(_list)
            ?? throw new InvalidOperationException(
                $"the plan tests membership of the context list '{_list}' and the execution bound "
                + "none. A list the plan reads at execution has to be bound at execution as well as "
                + "at prepare.");

        var rows = new ScalarValue[supplied.Count][];
        var nullRows = new List<ScalarValue[]>();
        var set = new ByteSet(supplied.Count);
        for (var r = 0; r < supplied.Count; r++)
        {
            var row = supplied[r];
            if (row.Count != _types.Length)
            {
                throw new InvalidOperationException(
                    $"row {r} of the context list '{_list}' has {row.Count} value(s) and the plan "
                    + $"compares {_types.Length} column(s) with it.");
            }

            ScalarValue[] bound;
            try
            {
                bound = ParameterBinder.Bind(row, _types);
            }
            catch (ArgumentException failure)
            {
                throw new InvalidOperationException(
                    $"row {r} of the context list '{_list}' does not match the column types the plan "
                    + $"compares it in: {failure.Message}",
                    failure);
            }

            rows[r] = bound;
            if (System.Array.Exists(bound, v => v.IsNull))
            {
                nullRows.Add(bound);
                continue;
            }

            var nan = false;
            var length = Encode(bound, ref nan);
            if (!nan)
            {
                set.Add(_key.AsSpan(0, length));
            }
        }

        _set = set;
        _rows = rows;
        _nullRows = [.. nullRows];
    }
}
