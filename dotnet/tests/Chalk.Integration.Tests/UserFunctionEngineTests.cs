using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Arrow;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The claims of step 22 that are properties of an <em>execution</em> rather than of a SQL text's
/// result, so they are tests rather than corpus queries — the treatment ADR 0020 gave the same shape
/// of claim in step 21.
/// </summary>
[Collection(SidecarCollection.Name)]
[Experimental("CHALK001")]
public sealed class UserFunctionEngineTests(SharedSidecar sidecar)
{
    private sealed record Reading(long Id, double Value);

    /// <summary>
    /// §5 corpus 08. A VOLATILE call is evaluated per lane and never de-duplicated, so three rows
    /// carry three different values — which is exactly what a differential comparison cannot assert,
    /// because two executions of a volatile function do not agree with each other either.
    /// </summary>
    [Fact]
    public async Task A_volatile_call_answers_once_per_row()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        CorpusFunctions.ResetSequence();
        await using var engine = await CreateAsync();
        var values = await ValuesAsync(engine, "SELECT next_seq() AS n FROM bars_small LIMIT 3");

        Assert.Equal(3, values.Count);
        Assert.Equal(3, values.Distinct().Count());
    }

    /// <summary>
    /// §5 corpus 09's claim, stated as a fact about the execution: a STABLE call of constants is
    /// evaluated once and broadcast, so every row carries the same value. The corpus query asserts
    /// the answer; this asserts that it was computed once.
    /// </summary>
    [Fact]
    public async Task A_stable_call_answers_once_per_execution()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateAsync();
        var values = await ValuesAsync(engine, "SELECT as_of() AS a FROM bars_small LIMIT 100");

        Assert.Equal(100, values.Count);
        Assert.Single(values.Distinct());
        Assert.Equal(CorpusFunctions.AsOfTicks, values[0]);
    }

    /// <summary>
    /// §5's negative: engine creation fails when a client-bodied function in the catalog has no
    /// registered implementation, before any query runs.
    /// </summary>
    [Fact]
    public async Task Engine_creation_fails_when_a_client_body_has_no_implementation()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("t", new[] { new Reading(1, 1.0) })
            .AddFunction("missing", f => f.Scalar<double, double>("x").Strict().Client())
            .Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "no-implementation",
                Sources = [source],
                Planner = new RecordedPlanner(RepoLayout.Plans.FullName),
            }).AsTask());

        Assert.Contains("'missing'", error.Message, StringComparison.Ordinal);
        Assert.Contains("ChalkEngineOptions.Functions", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The same, for a delegate whose CLR types disagree with the declaration.</summary>
    [Fact]
    public async Task Engine_creation_fails_when_a_delegate_has_the_wrong_clr_types()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("t", new[] { new Reading(1, 1.0) })
            .AddFunction("wrong", f => f.Scalar<double, double>("x").Strict().Client())
            .Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "wrong-types",
                Sources = [source],
                Functions = registry => registry.AddScalar<long, long>("wrong", static v => v),
                Planner = new RecordedPlanner(RepoLayout.Plans.FullName),
            }).AsTask());

        Assert.Contains("FP64", error.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A name a built-in already has is refused at registration, not shadowed (D77).</summary>
    [Fact]
    public async Task A_clash_with_a_built_in_is_a_registration_error()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = new PocoSourceBuilder("mem")
            .AddTable("t", new[] { new Reading(1, 1.0) })
            .AddFunction("upper", f => f.Scalar<string, string>("s").Strict().Client())
            .Build();

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "clash",
                Sources = [source],
                Functions = registry => registry.AddScalar<string, string>("upper", static s => s),
                Planner = sidecar.CreatePlanner(),
            }).AsTask());

        Assert.Contains("upper", error.Message, StringComparison.Ordinal);
    }

    // ---- composite results: the owner's classifier, end to end ----

    /// <summary>What the classifier answers: a category and how sure it is.</summary>
    public readonly record struct Classification(Utf8String Category, double Confidence);

    /// <summary>A transaction; every fifth amount is missing, which a strict classifier never sees.</summary>
    private sealed record Transaction(long Id, Utf8String Description, double? Amount);

    private static readonly Utf8String Large = "large"u8.ToArray();
    private static readonly Utf8String Small = "small"u8.ToArray();

    private sealed class Counter
    {
        public long Calls;
    }

    private static Classification Classify(double amount) =>
        new(amount >= 100 ? Large : Small, Math.Min(1.0, amount / 1000));

    /// <summary>
    /// The owner's query: two fields of one composite-valued call. The fields come back as ordinary
    /// columns, and the classifier runs once per row that reaches it, not once per field.
    /// </summary>
    [Fact]
    public async Task Two_fields_of_a_composite_valued_call_are_two_columns_and_one_call_per_row()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var counter = new Counter();
        await using var engine = await CreateClassifyingAsync(counter, StringLayouts.Utf8View);
        var prepared = await engine.PrepareAsync(
            "SELECT id,"
            + " classify_transaction(description, amount).category AS category,"
            + " classify_transaction(description, amount).confidence AS confidence"
            + " FROM transactions ORDER BY id");
        var rows = await RowsAsync(engine, prepared);

        Assert.Equal(Transactions.Length, rows.Count);
        foreach (var row in rows)
        {
            var amount = Transactions[(int)(long)row[0]!].Amount;
            Assert.Equal(amount is { } a ? Classify(a).Category.ToString() : null, row[1]);
            Assert.Equal(amount is { } b ? Classify(b).Confidence : null, (double?)row[2]);
        }

        Assert.Equal(Transactions.Count(t => t.Amount is not null), counter.Calls);
    }

    /// <summary>
    /// The whole value: one Arrow struct column whose children are named after the record's
    /// properties and carry their nullability, the composite's own on the column; the string layout the
    /// host asked for reaches the fields; and a host reads a cell as the object array of its fields.
    /// </summary>
    [Theory]
    [InlineData(StringLayouts.Utf8View, typeof(StringViewType))]
    [InlineData(StringLayouts.Utf8, typeof(StringType))]
    public async Task The_whole_value_reaches_the_host_as_an_arrow_struct(StringLayouts strings, System.Type text)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateClassifyingAsync(new Counter(), strings);
        var prepared = await engine.PrepareAsync(
            "SELECT id, classify_transaction(description, amount) AS c FROM transactions ORDER BY id");

        var declared = prepared.OutputSchema.GetFieldByIndex(1);
        Assert.True(declared.IsNullable);
        var type = Assert.IsType<StructType>(declared.DataType);
        Assert.Equal(["Category", "Confidence"], type.Fields.Select(f => f.Name));
        Assert.Equal([false, false], type.Fields.Select(f => f.IsNullable));
        Assert.IsType(text, type.Fields[0].DataType);
        Assert.IsType<DoubleType>(type.Fields[1].DataType);

        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        var seen = 0;
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                var column = Assert.IsType<StructArray>(batch.Column(1));
                Assert.IsType(text, column.Fields[0].Data.DataType);
                foreach (var row in batch.ToRows())
                {
                    var amount = Transactions[(int)(long)row[0]!].Amount;
                    if (amount is not { } value)
                    {
                        Assert.Null(row[1]);
                        continue;
                    }

                    var expected = Classify(value);
                    Assert.Equal([expected.Category.ToString(), expected.Confidence], (object?[])row[1]!);
                }

                // A slice of the column reads the same rows: the composite's offset reaches its fields.
                if (batch.Length > 2)
                {
                    var sliced = ArrowArrayFactory.BuildArray(column.Data.Slice(1, 2));
                    Assert.Equal(
                        RecordBatchExtensions.ValueAt(column, 2),
                        RecordBatchExtensions.ValueAt(sliced, 1));
                }

                seen += batch.Length;
            }
        }

        Assert.Equal(Transactions.Length, seen);
    }

    /// <summary>UNION ALL compares nothing, so it carries a composite value from both branches.</summary>
    [Fact]
    public async Task Union_all_carries_a_composite_from_both_branches()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateClassifyingAsync(new Counter(), StringLayouts.Utf8View);
        var prepared = await engine.PrepareAsync(
            "SELECT id, classify_transaction(description, amount) AS c FROM transactions WHERE id < 3"
            + " UNION ALL"
            + " SELECT id, classify_transaction(description, amount) FROM transactions WHERE id >= 47");
        var rows = (await RowsAsync(engine, prepared)).OrderBy(r => (long)r[0]!).ToList();

        Assert.Equal([0L, 1L, 2L, 47L, 48L, 49L], rows.Select(r => (long)r[0]!));
        foreach (var row in rows)
        {
            var amount = Transactions[(int)(long)row[0]!].Amount;
            object?[]? expected = amount is { } value
                ? [Classify(value).Category.ToString(), Classify(value).Confidence]
                : null;
            Assert.Equal(expected, (object?[]?)row[1]);
        }
    }

    /// <summary>
    /// A composite value on the nullable side of an outer join: an unmatched row is a NULL composite. Calcite
    /// widens the join's composite fields as it widens its columns, and the composite is carried whole.
    /// </summary>
    [Fact]
    public async Task A_left_join_carries_a_composite_from_its_nullable_side()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await CreateClassifyingAsync(new Counter(), StringLayouts.Utf8View);
        var prepared = await engine.PrepareAsync(
            "SELECT t.id, s.c, (s.c).category AS category FROM transactions AS t"
            + " LEFT JOIN (SELECT id, classify_transaction(description, amount) AS c"
            + " FROM transactions WHERE id >= 25) AS s ON t.id = s.id"
            + " ORDER BY t.id");
        var rows = await RowsAsync(engine, prepared);

        Assert.Equal(Transactions.Length, rows.Count);
        foreach (var row in rows)
        {
            var id = (long)row[0]!;
            var amount = Transactions[(int)id].Amount;
            if (id < 25 || amount is null)
            {
                Assert.Null(row[1]);
                Assert.Null(row[2]);
                continue;
            }

            var expected = Classify(amount.Value);
            Assert.Equal([expected.Category.ToString(), expected.Confidence], (object?[])row[1]!);
            Assert.Equal(expected.Category.ToString(), row[2]);
        }
    }

    /// <summary>
    /// A registered record that disagrees with the declared composite fails engine creation, naming
    /// both sides and the first field that differs.
    /// </summary>
    [Fact]
    public async Task Engine_creation_fails_when_the_record_disagrees_with_the_declared_composite()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("transactions", Transactions)
            .AddFunction("classify_transaction", f => f
                .Scalar<Utf8String, double, Classification>("description", "amount")
                .Strict()
                .Client())
            .Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "composite-mismatch",
                Sources = [source],
                Functions = registry => registry.AddScalar<Utf8String, double, Scored>(
                    "classify_transaction", static (_, amount) => new Scored(default, amount)),
                Planner = new RecordedPlanner(RepoLayout.Plans.FullName),
            }).AsTask());

        Assert.Contains("COMPOSITE(Category STRING, Confidence FP64)", error.Message, StringComparison.Ordinal);
        Assert.Contains("Scored", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Confidence' declared and 'Score' registered", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The owner's record with its second field renamed.</summary>
    public readonly record struct Scored(Utf8String Category, double Score);

    private static readonly Transaction[] Transactions =
    [
        .. Enumerable.Range(0, 50).Select(i => new Transaction(
            i, i % 2 == 0 ? Large : Small, i % 5 == 4 ? null : i * 7.5)),
    ];

    private async ValueTask<ChalkEngine> CreateClassifyingAsync(Counter counter, StringLayouts strings)
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("transactions", Transactions)
            .AddFunction("classify_transaction", f => f
                .Scalar<Utf8String, double, Classification>("description", "amount")
                .Strict()
                .Client())
            .Build();

        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "composite-results",
            Sources = [source],
            Functions = registry => registry.AddScalar<Utf8String, double, Classification>(
                "classify_transaction",
                (description, amount) =>
                {
                    counter.Calls++;
                    return Classify(amount);
                }),
            Output = new OutputOptions { Strings = strings },
            Planner = sidecar.CreatePlanner(),
        });
    }

    private static async Task<List<object?[]>> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        var rows = new List<object?[]>();
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(batch.ToRows());
            }
        }

        return rows;
    }

    private async ValueTask<ChalkEngine> CreateAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = CorpusFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
        });

    private static async Task<List<long>> ValuesAsync(ChalkEngine engine, string sql)
    {
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        var values = new List<long>();
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                values.AddRange(
                    ResultComparer.Rows([batch]).Select(row => Convert.ToInt64(row[0])));
            }
        }

        return values;
    }
}
