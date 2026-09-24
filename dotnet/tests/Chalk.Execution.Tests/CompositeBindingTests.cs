using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Sources;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;

namespace Chalk.Execution.Tests;

/// <summary>
/// D294's binding check (ADR 0077): the engine reads the registered delegate's result record as a
/// composite exactly as the declaration read one, and a registration whose record does not agree with the
/// declared composite is refused at engine creation naming both sides.
/// </summary>
public sealed class CompositeBindingTests
{
    public readonly record struct Classification(Utf8String Category, double Confidence);

    /// <summary>The same fields under other names.</summary>
    public readonly record struct Renamed(Utf8String Category, double Score);

    /// <summary>The same names at another width.</summary>
    public readonly record struct Narrow(Utf8String Category, float Confidence);

    public readonly record struct Priced(Utf8String Name, decimal Amount);

    public readonly record struct Summary(double Total, long Count);

    public struct SummaryState
    {
        public double Total;
        public long Count;
    }

    private static FunctionDescriptor Classify() =>
        new FunctionBuilder("classify_transaction")
            .Scalar<Utf8String, double, Classification>("description", "amount")
            .Strict()
            .Client()
            .Build();

    private static Classification Answer(Utf8String description, double amount) =>
        new(description, amount > 100 ? 0.9 : 0.1);

    [Fact]
    public void The_record_the_declaration_was_inferred_from_binds()
    {
        UserFunctionBinding.Check(
            Classify(),
            new HostScalar2<Utf8String, double, Classification>("classify_transaction", Answer),
            "test");
    }

    [Fact]
    public void A_nullable_form_of_the_record_binds_too()
    {
        UserFunctionBinding.Check(
            Classify(),
            new HostScalar2<Utf8String, double, Classification?>(
                "classify_transaction", static (d, a) => a < 0 ? null : new Classification(d, a)),
            "test");
    }

    [Fact]
    public void A_record_whose_field_names_differ_is_refused_naming_both_sides()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UserFunctionBinding.Check(
            Classify(),
            new HostScalar2<Utf8String, double, Renamed>(
                "classify_transaction", static (d, a) => new Renamed(d, a)),
            "test"));

        Assert.Contains("COMPOSITE(Category STRING, Confidence FP64)", error.Message, StringComparison.Ordinal);
        Assert.Contains("uses Renamed", error.Message, StringComparison.Ordinal);
        Assert.Contains("COMPOSITE(Category STRING, Score FP64)", error.Message, StringComparison.Ordinal);
        Assert.Contains("field 2 is 'Confidence' declared and 'Score' registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_whose_field_type_differs_is_refused_naming_the_field()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UserFunctionBinding.Check(
            Classify(),
            new HostScalar2<Utf8String, double, Narrow>(
                "classify_transaction", static (d, a) => new Narrow(d, (float)a)),
            "test"));

        Assert.Contains(
            "field 'Confidence' is FP64 declared and FP32 registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_names_are_compared_ignoring_case()
    {
        var declared = new FunctionBuilder("classify_transaction")
            .Scalar()
            .Parameter("description", ChalkType.String(nullable: true))
            .Parameter("amount", ChalkType.Float64(nullable: true))
            .Returns(ChalkType.Composite(
                new CompositeField("category", ChalkType.String()),
                new CompositeField("confidence", ChalkType.Float64())))
            .Strict()
            .Client()
            .Build();

        UserFunctionBinding.Check(
            declared,
            new HostScalar2<Utf8String, double, Classification>("classify_transaction", Answer),
            "test");
    }

    [Fact]
    public void A_registered_record_no_composite_can_be_read_from_is_refused_saying_why()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UserFunctionBinding.Check(
            Classify(),
            new HostScalar2<Utf8String, double, Priced>(
                "classify_transaction", static (d, a) => new Priced(d, (decimal)a)),
            "test"));

        Assert.Contains("property 'Amount' of Priced is Decimal", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scalar_registered_for_a_composite_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => UserFunctionBinding.Check(
            Classify(),
            new HostScalar2<Utf8String, double, double>("classify_transaction", static (_, a) => a),
            "test"));

        Assert.Contains("uses Double, which is not a record", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_valued_aggregate_binds_to_its_record()
    {
        var descriptor = new FunctionBuilder("summarize").Aggregate<double, Summary>("x").Client().Build();

        UserFunctionBinding.Check(
            descriptor,
            new HostAggregate<SummaryState, double, Summary?>("summarize", new AggregateSpec<SummaryState, double, Summary?>
            {
                Init = static () => default,
                Add = static (ref s, x) =>
                {
                    s.Total += x;
                    s.Count++;
                },
                Finish = static s => s.Count == 0 ? null : new Summary(s.Total, s.Count),
            }),
            "test");
    }
}
