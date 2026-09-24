using System.Diagnostics.CodeAnalysis;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;

namespace Chalk.TestKit;

/// <summary>
/// The user-defined functions the corpus is planned and executed against
/// (<c>docs/design/17-user-defined-functions.md</c> §5). Declared here and mirrored in the planner's
/// Java twin (<c>chalk.planner.TestCatalogs.functions</c>) message for message, because
/// <c>CorpusSchemaTest</c> compares the two.
/// </summary>
public static class CorpusFunctions
{
    /// <summary>Declares every §5 function on a POCO source builder.</summary>
    public static PocoSourceBuilder Declare(PocoSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            // SQL bodies: inlined, so the call disappears from every plan.
            .AddFunction("pct_change", f => f
                .Scalar<double, double, double>("a", "b")
                .Strict()
                .Sql("(b - a) / a"))
            .AddFunction("wavg", f => f
                .Aggregate<double, double, double>("x", "w")
                .Sql("SUM(x * w) / SUM(w)"))
            .AddFunction("bars_for", f => f
                .TableFunction()
                .Parameter<string>("sym")
                .Column<string>("symbol")
                .Column("ts", ChalkType.Timestamp(9))
                .Column<double>("close")
                .Sql("SELECT symbol, ts, \"close\" FROM bars WHERE symbol = sym"))

            // Client bodies, Tier 1.
            .AddFunction("bucket_price", f => f
                .Scalar<double, double, double>("price", "width")
                .Optional("off", ChalkType.Float64(nullable: true), 0.0)
                .Strict()
                .Client())
            .AddFunction("geo_mean", f => f
                .Aggregate<double, double>("x")
                .Client())
            .AddFunction("wsum", f => f
                .Aggregate<double, double, double>("x", "w")
                .Window()
                .Client())
            .AddFunction("generate_series", f => f
                .TableFunction()
                .Parameter<long>("start")
                .Parameter<long>("stop")
                .Parameter<long>("step")
                .Column<long>("value")
                .Client())

            // A client body, Tier 2: the same contract with a whole-batch kernel behind it.
            .AddFunction("fast_hash", f => f
                .Scalar<string, long>("s")
                .Strict()
                .Client())

            // The volatility trio, and the monotone one.
            .AddFunction("next_seq", f => f.Scalar<long>().Volatile().Client())
            .AddFunction("as_of", f => f
                .Scalar()
                .Returns(ChalkType.Timestamp(9))
                .Stable()
                .Client())
            .AddFunction("minute_of", f => f
                .Scalar()
                .Parameter("t", ChalkType.Timestamp(9, nullable: true))
                .Returns<long>()
                .Strict()
                .Increasing("t")
                .Client())

            // Structured results (D291–D294): a scalar and an aggregate that each answer a record,
            // declared from the record itself.
            .AddFunction("price_move", f => f
                .Scalar<double, double, PriceMove>("open_price", "close_price")
                .Strict()
                .Client())
            .AddFunction("close_range", f => f
                .Aggregate<double, PriceRange>("x")
                .Window()
                .Client());
    }

    /// <summary>
    /// The implementations, exactly as a host would write them. Every delegate here allocates
    /// nothing (which is what the allocation gate on corpus 04 asserts).
    /// </summary>
    [Experimental("CHALK001")]
    public static void Register(IFunctionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // Tier 1 scalars.
        registry.AddScalar<double, double, double, double>(
            "bucket_price", static (price, width, off) => (Math.Floor(price / width) * width) + off);
        registry.AddScalar<long, long>("minute_of", static ticks => ticks / 60_000_000_000L);

        // VOLATILE: a new value per lane, and the planner never folds or de-duplicates a call.
        registry.AddScalar("next_seq", static () => Interlocked.Increment(ref _sequence));

        // STABLE: one value per execution, which the engine arranges by evaluating it once.
        registry.AddScalar("as_of", static () => AsOfTicks);

        // Tier 1 aggregates. `geo_mean` declares Merge and no Remove; `wsum` declares both, which is
        // what makes its sliding frame slide (D80).
        registry.AddAggregate("geo_mean", new AggregateSpec<GeoMeanState, double, double?>
        {
            Init = static () => default,
            Add = static (ref s, x) =>
            {
                s.LogSum += Math.Log(x);
                s.Count++;
            },
            Merge = static (a, b) => new GeoMeanState
            {
                LogSum = a.LogSum + b.LogSum,
                Count = a.Count + b.Count,
            },
            Finish = static s => s.Count == 0 ? null : Math.Exp(s.LogSum / s.Count),
        });
        registry.AddAggregate("wsum", new AggregateSpec<WeightedSumState, double, double?>
        {
            Init = static () => default,
            Add = static (ref s, x) =>
            {
                s.Sum += x;
                s.Count++;
            },
            Remove = static (ref s, x) =>
            {
                s.Sum -= x;
                s.Count--;
            },
            Merge = static (a, b) => new WeightedSumState
            {
                Sum = a.Sum + b.Sum,
                Count = a.Count + b.Count,
            },
            Finish = static s => s.Count == 0 ? null : s.Sum,
        });

        // Structured results (D294): a delegate that answers a record, and an aggregate whose Finish
        // does. Neither allocates: the direction is one of three arrays made once.
        registry.AddScalar<double, double, PriceMove>("price_move", static (open, close) => Move(open, close));
        registry.AddAggregate("close_range", new AggregateSpec<PriceRangeState, double, PriceRange?>
        {
            Init = static () => default,
            Add = static (ref s, x) =>
            {
                s.Low = s.Count == 0 ? x : Math.Min(s.Low, x);
                s.High = s.Count == 0 ? x : Math.Max(s.High, x);
                s.Count++;
            },
            Merge = static (a, b) => a.Count == 0 ? b : b.Count == 0 ? a : new PriceRangeState
            {
                Low = Math.Min(a.Low, b.Low),
                High = Math.Max(a.High, b.High),
                Count = a.Count + b.Count,
            },
            Finish = static s => s.Count == 0 ? null : new PriceRange(s.Low, s.High),
        });

        // Tier 1 table function.
        registry.AddTable<long>(
            "generate_series",
            (Func<long, long, long, IEnumerable<long>>)GenerateSeries);

        // Tier 2: the same registry, a whole-batch kernel.
        registry.AddKernel("fast_hash", new FastHashKernel());
    }

    /// <summary>
    /// What <c>price_move</c> answers (D291): which way a bar moved — <c>up</c>, <c>down</c> or
    /// <c>flat</c> — and by how much. Declared from this record, so the composite's fields are its
    /// properties in order, <c>Direction STRING</c> and <c>Change FP64</c>, both non-nullable.
    /// </summary>
    public readonly record struct PriceMove(Utf8String Direction, double Change);

    /// <summary>What <c>close_range</c> answers: the lowest and the highest value it saw.</summary>
    public readonly record struct PriceRange(double Low, double High);

    /// <summary><c>close_range</c>'s state: the extremes so far, and how many values made them.</summary>
    public struct PriceRangeState
    {
        public double Low;
        public double High;
        public long Count;
    }

    private static readonly Utf8String Up = "up"u8.ToArray();
    private static readonly Utf8String Down = "down"u8.ToArray();
    private static readonly Utf8String Flat = "flat"u8.ToArray();

    /// <summary><c>price_move</c> itself: a comparison and a subtraction, and nothing allocated.</summary>
    public static PriceMove Move(double open, double close) =>
        new(close > open ? Up : close < open ? Down : Flat, close - open);

    /// <summary>What <c>as_of()</c> answers: fixed, so the corpus is deterministic.</summary>
    public static readonly DateTime AsOf = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The same instant in the units a TIMESTAMP(9) column carries.</summary>
    public static long AsOfTicks =>
        (AsOf - DateTime.UnixEpoch).Ticks * 100L;

    /// <summary>Resets the sequence, so a test that counts distinct values starts from a known place.</summary>
    public static void ResetSequence() => Interlocked.Exchange(ref _sequence, 0);

    private static long _sequence;

    /// <summary>The state of a geometric mean: the sum of logs, and how many there were.</summary>
    public struct GeoMeanState
    {
        public double LogSum;
        public long Count;
    }

    /// <summary>A running sum that can be moved backwards, which is what <c>Remove</c> promises.</summary>
    public struct WeightedSumState
    {
        public double Sum;
        public long Count;
    }

    private static IEnumerable<long> GenerateSeries(long start, long stop, long step)
    {
        if (step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "the step must be positive");
        }

        for (var value = start; value <= stop; value += step)
        {
            yield return value;
        }
    }

    /// <summary>
    /// A Tier 2 kernel: FNV-1a over the UTF-8 bytes of a string column, whole batch at a time and
    /// with nothing allocated per row — which is the reason the tier exists.
    /// </summary>
    [Experimental("CHALK001")]
    private sealed class FastHashKernel : IVectorFunction
    {
        public FunctionSignature Signature { get; } = new()
        {
            Name = "fast_hash",
            Parameters = [ChalkType.String(nullable: true)],
            ReturnType = ChalkType.Int64(),
        };

        public void Invoke(
            ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
        {
            var input = args[0];
            var length = context.RowCount;
            var values = result.Values<long>(length);
            var validity = result.BeginValidity(length);
            for (var row = 0; row < length; row++)
            {
                if (!input.IsValid(row))
                {
                    values[row] = 0;
                    continue;
                }

                var hash = 14695981039346656037UL;
                foreach (var b in input.VarValue(row))
                {
                    hash = (hash ^ b) * 1099511628211UL;
                }

                values[row] = (long)(hash & long.MaxValue);
                Apache.Arrow.BitUtility.SetBit(validity, row);
            }
        }
    }
}
