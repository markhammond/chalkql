using System.Text;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>Turns an IR <see cref="Literal"/> into the broadcast constant the kernels read (§6.4).</summary>
internal static class Literals
{
    /// <summary>Reads a literal expression. The value field is guaranteed to match the type by I-IR-8.</summary>
    public static ScalarValue ToScalar(Expr expr)
    {
        var type = ChalkType.FromProto(expr.Type);
        var literal = expr.Literal;
        if (literal.ValueCase == Literal.ValueOneofCase.IsNull)
        {
            return ScalarValue.Null(type);
        }

        return literal.ValueCase switch
        {
            Literal.ValueOneofCase.BoolValue => new ScalarValue { Type = type, Integer = literal.BoolValue ? 1 : 0 },
            Literal.ValueOneofCase.I8Value => new ScalarValue { Type = type, Integer = literal.I8Value },
            Literal.ValueOneofCase.I16Value => new ScalarValue { Type = type, Integer = literal.I16Value },
            Literal.ValueOneofCase.I32Value => new ScalarValue { Type = type, Integer = literal.I32Value },
            Literal.ValueOneofCase.I64Value => new ScalarValue { Type = type, Integer = literal.I64Value },
            Literal.ValueOneofCase.Fp32Value => new ScalarValue { Type = type, Single = literal.Fp32Value },
            Literal.ValueOneofCase.Fp64Value => new ScalarValue { Type = type, Double = literal.Fp64Value },
            Literal.ValueOneofCase.StringValue => Text(type, literal.StringValue),
            Literal.ValueOneofCase.BinaryValue =>
                new ScalarValue { Type = type, Bytes = literal.BinaryValue.ToByteArray() },
            Literal.ValueOneofCase.DateValue => new ScalarValue { Type = type, Integer = literal.DateValue },
            Literal.ValueOneofCase.TimeValue => new ScalarValue { Type = type, Integer = literal.TimeValue },
            Literal.ValueOneofCase.TimestampValue =>
                new ScalarValue { Type = type, Integer = literal.TimestampValue },
            Literal.ValueOneofCase.TimestampTzValue =>
                new ScalarValue { Type = type, Integer = literal.TimestampTzValue },
            Literal.ValueOneofCase.DecimalValue =>
                new ScalarValue { Type = type, Bytes = literal.DecimalValue.Unscaled.ToByteArray() },
            Literal.ValueOneofCase.UuidValue => new ScalarValue { Type = type, Bytes = literal.UuidValue.ToByteArray() },
            Literal.ValueOneofCase.IntervalDayValue =>
                new ScalarValue { Type = type, Integer = literal.IntervalDayValue },
            Literal.ValueOneofCase.IntervalYearValue =>
                new ScalarValue { Type = type, Integer = literal.IntervalYearValue },
            Literal.ValueOneofCase.ListValue => List(type, literal),
            _ => throw new UnsupportedFeatureException(
                $"literal of kind {literal.ValueCase}",
                "The execution engine has no representation for it."),
        };
    }

    /// <summary>
    /// A LIST constant (D58): its elements, read at the element type the list declares. One level
    /// deep, so this recursion terminates at the first element.
    /// </summary>
    private static ScalarValue List(ChalkType type, Literal literal)
    {
        var element = type.Element
            ?? throw new UnsupportedFeatureException(
                "a LIST literal with no element type",
                "docs/design/02-ir.md §3 requires Type.element on a LIST.");
        var elementProto = element.ToProto();
        var elements = new ScalarValue[literal.ListValue.Elements.Count];
        for (var i = 0; i < elements.Length; i++)
        {
            elements[i] = ToScalar(
                new Expr { Type = elementProto, Literal = literal.ListValue.Elements[i] });
        }

        return new ScalarValue { Type = type, Elements = elements };
    }

    /// <summary>
    /// A STRING constant carries both forms: the CLR string for diagnostics and the UTF-8 bytes the
    /// kernels compare against, encoded once here rather than per batch.
    /// </summary>
    public static ScalarValue Text(ChalkType type, string value) =>
        new() { Type = type, Text = value, Bytes = Encoding.UTF8.GetBytes(value) };
}
