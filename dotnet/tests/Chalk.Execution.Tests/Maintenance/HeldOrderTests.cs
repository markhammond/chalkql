using System.Text;
using Chalk.Catalog;
using Chalk.Execution.Maintenance;
using Chalk.Execution.Numeric;
using Chalk.Execution.Operators;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Tests.Maintenance;

/// <summary>
/// A maintained LIMIT compares the rows it holds as the executor's sort compares rows (D360): for every
/// comparable kind, every direction and every NULL placement, two values come in the same order whether
/// both are in batches, one is held, or both are — NaN, -0.0 and text above U+FFFF included.
/// </summary>
public sealed class HeldOrderTests
{
    public static TheoryData<string> Kinds() => new() { "bool", "i8", "i16", "i32", "i64", "f32", "f64", "string", "decimal" };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_held_row_sorts_as_the_executor_sorts_it(string kind)
    {
        var random = new Random(Array.IndexOf(["bool", "i8", "i16", "i32", "i64", "f32", "f64", "string", "decimal"], kind) + 1729);
        var (type, values) = Values(kind, random);
        var views = values.Select(value => View(kind, type, value)).ToArray();
        var store = new RowStore([type]);
        var slots = views.Select(view => store.Add([view], 0)).ToArray();

        foreach (var direction in new[]
        {
            SortDirection.AscNullsFirst, SortDirection.AscNullsLast, SortDirection.DescNullsFirst, SortDirection.DescNullsLast,
        })
        {
            var ordering = new SortOrdering(
                [new SortField { Expr = new Expr { FieldRef = new FieldRef { Index = 0 } }, Direction = direction }],
                new RowType { Fields = { new Field { Name = "v", Type = type.ToProto() } } });
            for (var a = 0; a < views.Length; a++)
            {
                for (var b = 0; b < views.Length; b++)
                {
                    var expected = Math.Sign(ordering.Compare([views[a]], 0, [views[b]], 0));
                    Assert.Equal(expected, Math.Sign(HeldOrder.Compare(ordering, store, slots[a], [views[b]], 0)));
                    Assert.Equal(expected, Math.Sign(HeldOrder.Compare(ordering, store, slots[a], store, slots[b])));
                }
            }
        }
    }

    private static (ChalkType Type, object?[] Values) Values(string kind, Random random)
    {
        object?[] Mixed(IEnumerable<object> fixedOnes, Func<object> next) =>
            [null, .. fixedOnes, .. Enumerable.Range(0, 24).Select(_ => next())];

        return kind switch
        {
            "bool" => (ChalkType.Bool(nullable: true), [null, false, true, true, false]),
            "i8" => (ChalkType.Int8(nullable: true), Mixed([sbyte.MinValue, sbyte.MaxValue, (sbyte)0, (sbyte)-1], () => (sbyte)random.Next(-128, 128))),
            "i16" => (ChalkType.Int16(nullable: true), Mixed([short.MinValue, short.MaxValue, (short)0, (short)-1], () => (short)random.Next(-32768, 32768))),
            "i32" => (ChalkType.Int32(nullable: true), Mixed([int.MinValue, int.MaxValue, 0, -1], () => random.Next(int.MinValue, int.MaxValue))),
            "i64" => (ChalkType.Int64(nullable: true), Mixed([long.MinValue, long.MaxValue, 0L, -1L], () => random.NextInt64(long.MinValue, long.MaxValue))),
            "f32" => (ChalkType.Float32(nullable: true), Mixed(
                [float.NaN, -0.0f, 0.0f, float.PositiveInfinity, float.NegativeInfinity, float.MinValue, float.MaxValue],
                () => (float)((random.NextDouble() - 0.5) * 1000))),
            "f64" => (ChalkType.Float64(nullable: true), Mixed(
                [double.NaN, -0.0, 0.0, double.PositiveInfinity, double.NegativeInfinity, double.MinValue, double.MaxValue, double.Epsilon],
                () => (random.NextDouble() - 0.5) * 1e6)),
            "string" => (ChalkType.String(nullable: true), Mixed(
                ["", "a", "aa", "b", "é", "", "\U0001F600", "z\U0001F600", "￿"],
                () => new string([.. Enumerable.Range(0, random.Next(0, 4)).Select(_ => (char)random.Next(0x20, 0x7F))]))),
            "decimal" => (ChalkType.Decimal(18, 2, nullable: true), Mixed(
                [0m, -0.01m, 0.01m, 9999999999999999.99m, -9999999999999999.99m],
                () => Math.Round((decimal)((random.NextDouble() - 0.5) * 1e6), 2))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static ColumnView View(string kind, ChalkType type, object? value)
    {
        byte[] values;
        byte[] offsets = [];
        switch (kind)
        {
            case "string":
                values = value is string text ? Encoding.UTF8.GetBytes(text) : [];
                offsets = new byte[8];
                BitConverter.GetBytes(values.Length).CopyTo(offsets, 4);
                break;
            case "bool":
                values = [(byte)(value is true ? 1 : 0)];
                break;
            case "i8":
                values = [(byte)(value is sbyte s ? s : 0)];
                break;
            case "i16":
                values = BitConverter.GetBytes(value is short h ? h : (short)0);
                break;
            case "i32":
                values = BitConverter.GetBytes(value is int i ? i : 0);
                break;
            case "i64":
                values = BitConverter.GetBytes(value is long l ? l : 0L);
                break;
            case "f32":
                values = BitConverter.GetBytes(value is float f ? f : 0f);
                break;
            case "f64":
                values = BitConverter.GetBytes(value is double d ? d : 0d);
                break;
            case "decimal":
                values = new byte[16];
                Decimals.Write(values, value is decimal m ? m : 0m, 18, 2);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new ColumnView
        {
            Type = type,
            Length = 1,
            Values = values,
            Offsets = offsets,
            Validity = value is null ? new byte[] { 0 } : default,
            NullCount = value is null ? 1 : 0,
        };
    }
}
