using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.Sources;
using Chalk.TestKit;
using Chalk.Tests;
using System.Diagnostics.CodeAnalysis;
using CatalogContext = Chalk.Catalog.CatalogContext;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;

namespace Chalk.Execution.Tests;

/// <summary>
/// The executor's half of step 22 (<c>docs/design/17-user-defined-functions.md</c> §6): Tier 1 lane
/// loops strict and not, the aggregate protocol and its arena-owned state, Tier 2's signature check
/// and its zero-allocation claim, and the registration failures that must name both sides.
/// </summary>
/// <remarks>
/// The end-to-end claims — a grouped aggregate's answers, a table function's rows, a sliding frame —
/// are the corpus's, where they are compared against the reference executor running the same host
/// implementations. What is here is what a corpus query cannot see.
/// </remarks>
[Experimental("CHALK001")]
public sealed class UserFunctionTests
{
    private sealed record Row(long Id, double? Value);

    private const int Rows = 4096;

    private readonly Xunit.ITestOutputHelper _output;

    public UserFunctionTests(Xunit.ITestOutputHelper output) => _output = output;

    // ---- Tier 1 lane loops ----

    [Fact]
    public async Task A_strict_delegate_never_sees_a_null_lane()
    {
        var seen = 0;
        var results = await ProjectAsync(
            Scalar("f", strict: true, ChalkType.Float64(nullable: true), ChalkType.Float64()),
            new HostScalar1<double, double>("f", v =>
            {
                seen++;
                return v * 2;
            }));

        Assert.Equal(Rows / 2, seen);
        Assert.Equal(Rows / 2, results.Count(r => r is null));
        Assert.All(results.Where(r => r is not null), r => Assert.True((double)r! > 0));
    }

    [Fact]
    public async Task A_non_strict_delegate_sees_the_null_and_decides()
    {
        var results = await ProjectAsync(
            Scalar(
                "f",
                strict: false,
                ChalkType.Float64(nullable: true),
                ChalkType.Float64(nullable: true)),
            new HostScalar1<double?, double?>("f", v => v ?? -1.0));

        Assert.Equal(Rows / 2, results.Count(r => r is double d && d == -1.0));
        Assert.DoesNotContain(results, r => r is null);
    }

    [Fact]
    public async Task A_delegate_that_answers_null_leaves_the_lane_null()
    {
        var results = await ProjectAsync(
            Scalar(
                "f",
                strict: true,
                ChalkType.Float64(nullable: true),
                ChalkType.Float64(nullable: true)),
            new HostScalar1<double, double?>("f", v => v > 4000 ? null : v));

        Assert.Contains(results, r => r is null);
        Assert.Contains(results, r => r is double);
    }

    [Theory]
    [InlineData(TypeKind.Bool, "Boolean")]
    [InlineData(TypeKind.I8, "SByte")]
    [InlineData(TypeKind.I16, "Int16")]
    [InlineData(TypeKind.I32, "Int32")]
    [InlineData(TypeKind.I64, "Int64")]
    [InlineData(TypeKind.Fp32, "Single")]
    [InlineData(TypeKind.Fp64, "Double")]
    [InlineData(TypeKind.String, "String")]
    [InlineData(TypeKind.Date, "Int32")]
    [InlineData(TypeKind.Timestamp, "Int64")]
    public void Every_supported_declared_kind_has_the_clr_type_a_delegate_writes(
        TypeKind kind, string clr)
    {
        var type = new ChalkType(kind, Nullable: true);
        Assert.Equal(clr, LaneCodec.ClrTypeOf(type)!.Name);
        Assert.Contains(clr, LaneCodec.Describe(type), StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_tier_1_cannot_carry_says_so_and_points_at_tier_2()
    {
        Assert.Null(LaneCodec.ClrTypeOf(ChalkType.Decimal()));
        Assert.Contains("Tier 2", LaneCodec.Describe(ChalkType.Decimal()), StringComparison.Ordinal);
    }

    // ---- the aggregate protocol ----

    [Fact]
    public void Add_and_finish_fold_a_group()
    {
        var spec = Summing();
        var state = spec.Init();
        spec.Add(ref state, 1.0);
        spec.Add(ref state, 2.0);
        spec.Add(ref state, 4.0);
        Assert.Equal(7.0, spec.Finish(state));
    }

    [Fact]
    public void Remove_undoes_an_add_exactly_which_is_what_lets_a_frame_slide()
    {
        var spec = Summing();
        var state = spec.Init();
        spec.Add(ref state, 1.0);
        spec.Add(ref state, 2.0);
        spec.Remove!(ref state, 1.0);
        Assert.Equal(2.0, spec.Finish(state));
    }

    [Fact]
    public void Merge_combines_two_partial_states_and_treats_init_as_the_identity()
    {
        var spec = Summing();
        var left = spec.Init();
        var right = spec.Init();
        spec.Add(ref left, 1.0);
        spec.Add(ref right, 2.0);
        Assert.Equal(3.0, spec.Finish(spec.Merge!(left, right)));
        Assert.Equal(1.0, spec.Finish(spec.Merge(left, spec.Init())));
    }

    [Fact]
    public void An_accumulator_holds_one_struct_state_per_group_in_the_arena()
    {
        using var arena = new ExecutionArena();
        arena.BeginExecution();
        var accumulator = new UserAggregateAccumulator<double, double, double?>(
            ChalkType.Float64(nullable: true), Summing());
        accumulator.Begin(arena);
        try
        {
            accumulator.EnsureCapacity(3);
            Span<byte> lane = stackalloc byte[8];
            for (var group = 0; group < 3; group++)
            {
                for (var i = 1; i <= 3; i++)
                {
                    System.Runtime.InteropServices.MemoryMarshal.Write(lane, (double)(group + i));
                    accumulator.Add(group, lane, valid: true);
                }

                // A NULL argument never reaches Add, exactly as it does not for a built-in.
                accumulator.Add(group, lane, valid: false);
            }

            var copier = new ColumnCopier(ChalkType.Float64(nullable: true));
            copier.Begin();
            for (var group = 0; group < 3; group++)
            {
                accumulator.Emit(copier, group);
            }

            var view = copier.FinishView();
            Assert.Equal(6.0, view.Lanes<double>()[0]);
            Assert.Equal(9.0, view.Lanes<double>()[1]);
            Assert.Equal(12.0, view.Lanes<double>()[2]);
        }
        finally
        {
            accumulator.Release();
            arena.EndExecution();
        }

        Assert.Equal(0, arena.OutstandingBytes);
    }

    [Fact]
    public void An_empty_group_gets_the_state_init_produced()
    {
        using var arena = new ExecutionArena();
        arena.BeginExecution();
        var accumulator = new UserAggregateAccumulator<double, double, double?>(
            ChalkType.Float64(nullable: true), NullWhenEmpty());
        accumulator.Begin(arena);
        try
        {
            accumulator.EnsureCapacity(1);
            var copier = new ColumnCopier(ChalkType.Float64(nullable: true));
            copier.Begin();
            accumulator.Emit(copier, 0);
            Assert.False(copier.FinishView().IsValid(0));
        }
        finally
        {
            accumulator.Release();
            arena.EndExecution();
        }
    }

    // ---- Tier 2 ----

    [Fact]
    public void A_kernel_whose_signature_disagrees_with_the_declaration_is_refused()
    {
        var descriptor = Scalar("k", strict: true, ChalkType.Float64(nullable: true), ChalkType.Float64());
        var host = new HostKernel("k", new DoublingKernel(ChalkType.Int64(nullable: true), ChalkType.Int64()));

        var error = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.ScalarKernel(descriptor, host));
        Assert.Contains("implements", error.Message, StringComparison.Ordinal);
        Assert.Contains("is declared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tier_2_kernel_allocates_nothing_per_batch()
    {
        var descriptor = Scalar("k", strict: true, ChalkType.Float64(nullable: true), ChalkType.Float64());
        var host = new HostKernel(
            "k", new DoublingKernel(ChalkType.Float64(nullable: true), ChalkType.Float64()));
        await AssertBatchIsAllocationFree(descriptor, host, "tier 2 kernel");
    }

    [Fact]
    public async Task A_tier_1_delegate_that_allocates_nothing_leaves_the_batch_allocation_free()
    {
        var descriptor = Scalar("f", strict: true, ChalkType.Float64(nullable: true), ChalkType.Float64());
        await AssertBatchIsAllocationFree(
            descriptor, new HostScalar1<double, double>("f", static v => v * 2), "tier 1 delegate");
    }

    // ---- registration ----

    [Fact]
    public void A_delegate_whose_clr_types_do_not_match_the_descriptor_is_refused_naming_both()
    {
        var descriptor = Scalar("f", strict: true, ChalkType.Float64(nullable: true), ChalkType.Float64());
        var error = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                descriptor, new HostScalar1<long, long>("f", static v => v), "test"));
        Assert.Contains("FP64", error.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_client_bodied_function_with_no_implementation_is_refused_naming_it()
    {
        var descriptor = Scalar("f", strict: true, ChalkType.Float64(nullable: true), ChalkType.Float64());
        var error = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(descriptor, null, "test"));
        Assert.Contains("nothing is registered as 'f'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_strict_function_over_a_nullable_type_needs_a_nullable_delegate()
    {
        var descriptor = Scalar(
            "f", strict: false, ChalkType.Float64(nullable: true), ChalkType.Float64(nullable: true));
        var error = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                descriptor, new HostScalar1<double, double>("f", static v => v), "test"));
        Assert.Contains("must take Double?", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_aggregate_registered_as_a_scalar_is_refused_naming_what_each_side_is()
    {
        var descriptor = new FunctionDescriptor
        {
            Name = "g",
            Kind = FunctionKind.Aggregate,
            Parameters = [new ParameterDescriptor { Name = "x", Type = ChalkType.Float64(nullable: true) }],
            ReturnType = ChalkType.Float64(nullable: true),
            Body = new ClientFunctionBody(),
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                descriptor, new HostScalar1<double, double>("g", static v => v), "test"));
        Assert.Contains("declared as an aggregate", error.Message, StringComparison.Ordinal);
        Assert.Contains("a scalar delegate", error.Message, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static AggregateSpec<double, double, double?> Summing() => new()
    {
        Init = static () => 0.0,
        Add = static (ref double s, double x) => s += x,
        Remove = static (ref double s, double x) => s -= x,
        Merge = static (a, b) => a + b,
        Finish = static s => s,
    };

    /// <summary>An aggregate with no answer for an empty group, which is what a nullable result says.</summary>
    private static AggregateSpec<double, double, double?> NullWhenEmpty() => new()
    {
        Init = static () => 0.0,
        Add = static (ref double s, double x) => s += x,
        Finish = static s => s == 0.0 ? null : s,
    };

    private static FunctionDescriptor Scalar(
        string name, bool strict, ChalkType parameter, ChalkType result) => new()
    {
        Name = name,
        Kind = FunctionKind.Scalar,
        Parameters = [new ParameterDescriptor { Name = "x", Type = parameter }],
        ReturnType = result,
        Strict = strict,
        Body = new ClientFunctionBody(),
    };

    private sealed class DoublingKernel : IVectorFunction
    {
        public DoublingKernel(ChalkType parameter, ChalkType result) => Signature = new FunctionSignature
        {
            Name = "k",
            Parameters = [parameter],
            ReturnType = result,
        };

        public FunctionSignature Signature { get; }

        public void Invoke(
            ReadOnlySpan<ColumnView> args, ColumnWriter result, in FunctionContext context)
        {
            var input = args[0];
            var values = result.Values<double>(context.RowCount);
            var validity = result.BeginValidity(context.RowCount);
            for (var row = 0; row < context.RowCount; row++)
            {
                if (!input.IsValid(row))
                {
                    values[row] = 0;
                    continue;
                }

                values[row] = input.Lanes<double>()[row] * 2;
                Apache.Arrow.BitUtility.SetBit(validity, row);
            }
        }
    }

    /// <summary>
    /// The plan under test, or — when <paramref name="descriptor"/> is null — the same projection
    /// written as an ordinary multiplication, which is the floor the user function is measured
    /// against.
    /// </summary>
    private static (CompiledPlan Plan, ExecutionArena Arena) Compile(
        FunctionDescriptor? descriptor, HostFunction? host, int batchSize = Rows)
    {
        var rows = new Row[Rows];
        for (var i = 0; i < Rows; i++)
        {
            rows[i] = new Row(i, i % 2 == 0 ? null : i * 2.0);
        }

        var source = new PocoSourceBuilder("mem").AddTable("t", rows).Build();
        var schema = source.DescribeSchema();
        var withFunction = new SchemaDescriptor
        {
            SourceId = schema.SourceId,
            Name = schema.Name,
            Kind = schema.Kind,
            Capabilities = schema.Capabilities,
            Tables = schema.Tables,
            Functions = descriptor is null ? [] : [descriptor],
        };
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [withFunction] };

        var compiled = PlanCompiler.Compile(
            ProjectPlan(withFunction.Tables[0], descriptor),
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings
            {
                BatchSize = batchSize,
                PooledOutput = true,
                Functions = host is null
                    ? HostFunctionSet.Empty
                    : new HostFunctionSet(
                        new Dictionary<string, HostFunction>(StringComparer.OrdinalIgnoreCase)
                        {
                            [host.Name] = host,
                        }),
            });
        return (compiled, new ExecutionArena());
    }

    private static async Task<List<object?>> ProjectAsync(
        FunctionDescriptor descriptor, HostFunction host)
    {
        var (compiled, arena) = Compile(descriptor, host);
        using (arena)
        {
            var values = new List<object?>();
            await foreach (var batch in compiled.ExecuteAsync([], new ExecutionStats(), arena, CancellationToken.None))
            {
                using (batch)
                {
                    values.AddRange(BatchReader.ToStorageRows([batch]).Select(r => r[0]));
                }
            }

            Assert.Equal(0, arena.OutstandingBytes);
            return values;
        }
    }

    /// <summary>
    /// The step 20 claim, applied to a user function: it adds <em>nothing</em> per batch over the
    /// engine's own floor. Measured as the difference against the same projection written with a
    /// built-in, because the floor is not zero — an output batch is an Arrow object graph the host
    /// keeps, and 15-zero-allocation-execution.md §5's gate is measured on the benchmark's pooled
    /// pipeline, not here. What this asserts is the part step 22 owns: a call to a host function
    /// costs the same per batch as a multiplication.
    /// </summary>
    /// <remarks>
    /// The comparison carries no slack, and should carry none: "adds nothing" is the claim, and a
    /// margin here would be a number nobody chose. That is only defensible because both sides are
    /// measured exactly — see <see cref="AllocationProbe"/> — and, so measured, the two of them
    /// agree to the byte. A margin would have buried the reported flake instead of the perturbation
    /// that caused it, which was a collection landing in a measured window and was never anything
    /// the function did.
    /// </remarks>
    private async Task AssertBatchIsAllocationFree(
        FunctionDescriptor descriptor, HostFunction host, string what)
    {
        var floor = await Measure(descriptor: null, host: null, batchSize: Rows / 8);
        var actual = await Measure(descriptor, host, Rows / 8);

        _output.WriteLine(
            $"{what}: {actual} bytes over 8 batches against a built-in's {floor} = "
            + $"{(actual - floor) / 8.0:0.###} bytes/batch.");
        Assert.True(
            actual <= floor,
            $"a warm {what} cost {(actual - floor) / 8.0:0.###} bytes per batch more than the "
            + $"same projection written with a built-in ({actual} against {floor}).");
    }

    /// <summary>
    /// What this projection costs per run once it is warm, measured through
    /// <see cref="AllocationProbe"/> so that the number is the pipeline's own: a collection inside
    /// the window would otherwise charge this thread for up to 8 KB it never allocated, which is
    /// where the difference between the two sides of this gate came from when it flaked.
    /// </summary>
    private static async Task<long> Measure(
        FunctionDescriptor? descriptor, HostFunction? host, int batchSize)
    {
        var (compiled, arena) = Compile(descriptor, host, batchSize);
        using (arena)
        {
            var (allocated, _) = await AllocationProbe.SteadyStateAsync(
                () => ConsumeAsync(compiled, arena));
            Assert.Equal(0, arena.OutstandingBytes);
            return allocated;
        }
    }

    private static async Task<int> ConsumeAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        var rows = 0;
        await foreach (var batch in compiled.ExecuteAsync([], new ExecutionStats(), arena, CancellationToken.None))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    private static Plan ProjectPlan(TableDescriptor table, FunctionDescriptor? descriptor)
    {
        var value = table.Columns[1];
        var argument = new Expr
        {
            Type = value.Type.ToProto(),
            FieldRef = new FieldRef { Index = 1 },
        };

        var call = descriptor is null
            ? new Expr
            {
                Type = value.Type.ToProto(),
                Call = new ScalarCall { Function = FunctionId.Multiply },
            }
            : new Expr
            {
                Type = descriptor.ReturnType!.Value.WithNullable(true).ToProto(),
                Call = new ScalarCall { UserFunction = $"main.{descriptor.Name}" },
            };
        call.Call.Args.Add(argument);
        if (descriptor is null)
        {
            call.Call.Args.Add(new Expr
            {
                Type = value.Type.ToProto(),
                Literal = new Literal { Fp64Value = 2.0 },
            });
        }

        var read = new Rel
        {
            RowType = new RowType(),
            Read = new Read
            {
                Table = new TableRef { SourceId = "mem", Schema = "main", Table = table.Name },
            },
        };
        read.Read.Projection.Add(0);
        read.Read.Projection.Add(1);
        read.RowType.Fields.Add(new Field { Name = table.Columns[0].Name, Type = table.Columns[0].Type.ToProto() });
        read.RowType.Fields.Add(new Field { Name = value.Name, Type = value.Type.ToProto() });

        var project = new Rel { RowType = new RowType(), Project = new Project { Input = read } };
        project.Project.Exprs.Add(call);
        project.RowType.Fields.Add(new Field { Name = "result", Type = call.Type });

        var plan = new Plan
        {
            IrVersion = IrVersion.Current,
            ContextId = "test",
            CatalogEpoch = 1,
            OutputType = project.RowType,
            Root = project,
        };
        return plan.Clone().Also();
    }
}

file static class PlanExtensions
{
    /// <summary>Stamps the digest the validator checks, which is the planner's job in a real plan.</summary>
    public static Plan Also(this Plan plan)
    {
        plan.PlanDigest = PlanDigest.Compute(plan);
        return plan;
    }
}
