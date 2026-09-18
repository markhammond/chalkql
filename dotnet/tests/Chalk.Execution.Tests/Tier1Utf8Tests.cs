using System.Diagnostics.CodeAnalysis;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;

namespace Chalk.Execution.Tests;

/// <summary>
/// Tier 1 delegates written in <c>Utf8String</c> (D146, <c>docs/design/24-zero-gc.md</c> §4): the
/// lane is lent as a memory-backed slice, the result's bytes are copied into the column with one
/// validity check, and none of it allocates.
/// </summary>
/// <remarks>
/// The gate itself — 0 bytes per batch, beside the existing Tier 1 gate — is the benchmark's, where
/// the pipeline is pooled and an output batch's Arrow graph is not in the measurement. What is here
/// is the part a unit test can hold: the same answers as the <c>string</c> spelling, NULLs, the
/// refusal of invalid UTF-8, and the signature check.
/// </remarks>
[Experimental("CHALK001")]
public sealed class Tier1Utf8Tests
{
    private sealed record Row(Utf8String Symbol, Utf8String? Label);

    private static readonly string[] Words =
        ["btcusdt", "ÉTOILE", "MiXeD", "", "a_very_long_symbol_name", "日経"];

    [Fact]
    public async Task A_Utf8String_delegate_reads_the_lane_and_writes_bytes_back()
    {
        var results = await ProjectAsync(
            StringToString("upper_ascii"),
            new HostScalar1<Utf8String, Utf8String>("upper_ascii", Utf8Fixture.UpperAscii));

        Assert.Equal(Words.Select(UpperAsciiReference), results);
    }

    [Fact]
    public async Task A_Utf8String_delegate_and_a_string_delegate_agree()
    {
        var viaBytes = await ProjectAsync(
            StringToString("upper_ascii"),
            new HostScalar1<Utf8String, Utf8String>("upper_ascii", Utf8Fixture.UpperAscii));
        var viaString = await ProjectAsync(
            StringToString("upper_ascii"),
            new HostScalar1<string, string>("upper_ascii", static s => UpperAsciiReference(s)));

        Assert.Equal(viaString, viaBytes);
    }

    [Fact]
    public async Task A_Utf8String_argument_returning_a_long_is_the_byte_length()
    {
        var results = await ProjectAsync(
            new FunctionDescriptor
            {
                Name = "byte_length",
                Kind = FunctionKind.Scalar,
                Parameters = [new ParameterDescriptor { Name = "s", Type = ChalkType.String() }],
                ReturnType = ChalkType.Int64(),
                Strict = true,
                Body = new ClientFunctionBody(),
            },
            new HostScalar1<Utf8String, long>("byte_length", static v => v.Length),
            column: 0);

        Assert.Equal(
            Words.Select(w => (object?)(long)System.Text.Encoding.UTF8.GetByteCount(w)),
            results);
    }

    /// <summary>
    /// A non-strict delegate sees the NULL as <c>Utf8String?</c> and decides — the empty string and
    /// a NULL are different values, which is what the nullable spelling exists to say.
    /// </summary>
    [Fact]
    public async Task A_nullable_Utf8String_delegate_sees_the_null()
    {
        var seenNull = 0;
        var results = await ProjectAsync(
            new FunctionDescriptor
            {
                Name = "or_missing",
                Kind = FunctionKind.Scalar,
                Parameters = [new ParameterDescriptor { Name = "s", Type = ChalkType.String(nullable: true) }],
                ReturnType = ChalkType.String(nullable: true),
                Strict = false,
                Body = new ClientFunctionBody(),
            },
            new HostScalar1<Utf8String?, Utf8String?>("or_missing", value =>
            {
                if (value is null)
                {
                    seenNull++;
                    return Utf8String.FromString("<missing>");
                }

                return value;
            }),
            column: 1);

        // Every third label is NULL in the fixture rows below.
        Assert.Equal(2, seenNull);
        Assert.Equal(
            ["btcusdt", "ÉTOILE", "<missing>", "", "a_very_long_symbol_name", "<missing>"],
            results);
    }

    [Fact]
    public async Task Invalid_UTF_8_from_a_delegate_is_refused_naming_the_row()
    {
        // The operator wraps what a plan throws, naming the failing node; the cause is the refusal.
        var wrapped = await Assert.ThrowsAsync<ExecutionException>(() => ProjectAsync(
            StringToString("bad"),
            new HostScalar1<Utf8String, Utf8String>(
                "bad",
                static v => v.Length == 0
                    ? v
                    : Utf8String.FromBytes(new byte[] { 0xC3, 0x28 }))));

        var failure = Assert.IsType<InvalidUtf8Exception>(wrapped.InnerException);
        Assert.Equal(0, failure.Row);
        Assert.Contains("Tier 1 function", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not valid UTF-8", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_signature_check_accepts_Utf8String_for_a_STRING()
    {
        var descriptor = StringToString("f");
        UserFunctionBinding.Check(
            descriptor, new HostScalar1<Utf8String, Utf8String>("f", static v => v), "test");
        UserFunctionBinding.Check(
            descriptor, new HostScalar1<string, string>("f", static v => v), "test");

        var wrong = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                descriptor, new HostScalar1<Utf8String, long>("f", static v => v.Length), "test"));
        Assert.Contains("Utf8String, or string", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", wrong.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-strict STRING parameter needs the nullable spelling, exactly as a <c>double</c> one
    /// does: a <c>Utf8String</c> is a value type, so a NULL would arrive as the empty string.
    /// </summary>
    [Fact]
    public void A_non_strict_Utf8String_parameter_must_be_nullable()
    {
        var descriptor = new FunctionDescriptor
        {
            Name = "f",
            Kind = FunctionKind.Scalar,
            Parameters = [new ParameterDescriptor { Name = "s", Type = ChalkType.String(nullable: true) }],
            ReturnType = ChalkType.String(nullable: true),
            Strict = false,
            Body = new ClientFunctionBody(),
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                descriptor, new HostScalar1<Utf8String, Utf8String>("f", static v => v), "test"));
        Assert.Contains("must take Utf8String?", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reference executor works in boxed values throughout (D13), so the same delegate runs
    /// under both engines: the string it is handed is encoded on the way in and its answer decoded
    /// on the way out.
    /// </summary>
    [Fact]
    public void The_boxed_path_hands_a_Utf8String_delegate_the_encoded_bytes()
    {
        HostScalar host = new HostScalar1<Utf8String, Utf8String>("f", Utf8Fixture.UpperAscii);
        Assert.Equal("ÉTOILE", host.InvokeBoxed(["ÉTOILE"]));
        Assert.Equal("BTCUSDT", host.InvokeBoxed(["btcusdt"]));

        HostScalar nullable = new HostScalar1<Utf8String?, long?>(
            "g", static v => v?.Length);
        Assert.Equal(3L, nullable.InvokeBoxed(["abc"]));
        Assert.Null(nullable.InvokeBoxed([null]));
    }

    private static string UpperAsciiReference(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is >= (byte)'a' and <= (byte)'z')
            {
                bytes[i] -= 32;
            }
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static FunctionDescriptor StringToString(string name) => new()
    {
        Name = name,
        Kind = FunctionKind.Scalar,
        Parameters = [new ParameterDescriptor { Name = "s", Type = ChalkType.String() }],
        ReturnType = ChalkType.String(),
        Strict = true,
        Body = new ClientFunctionBody(),
    };

    private static async Task<List<object?>> ProjectAsync(
        FunctionDescriptor descriptor, HostFunction host, int column = 0)
    {
        var rows = new Row[Words.Length];
        for (var i = 0; i < Words.Length; i++)
        {
            Utf8String? label = default;
            if (i % 3 != 2)
            {
                label = Utf8String.FromString(Words[i]);
            }

            rows[i] = new Row(Utf8String.FromString(Words[i]), label);
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
            Functions = [descriptor],
        };
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [withFunction] };
        var table = withFunction.Tables[0];

        var rowType = IrBuilder.Row(
            [.. table.Columns.Select(c => IrBuilder.F(c.Name, c.Type.ToProto()))]);
        var read = IrBuilder.Read("t", rowType);
        var call = new Expr
        {
            Type = descriptor.ReturnType!.Value.WithNullable(true).ToProto(),
            Call = new ScalarCall { UserFunction = $"main.{descriptor.Name}" },
        };
        call.Call.Args.Add(IrBuilder.Ref(rowType, column));
        var project = IrBuilder.Project(read, [("f", call)]);
        var plan = new Plan
        {
            IrVersion = IrVersion.Current,
            ContextId = "test",
            CatalogEpoch = 1,
            OutputType = project.RowType,
            Root = project,
        };
        plan.PlanDigest = PlanDigest.Compute(plan);

        var compiled = PlanCompiler.Compile(
            plan,
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings
            {
                BatchSize = 4,
                Functions = new HostFunctionSet(
                    new Dictionary<string, HostFunction>(StringComparer.OrdinalIgnoreCase)
                    {
                        [host.Name] = host,
                    }),
            });

        using var arena = new ExecutionArena();
        var values = new List<object?>();
        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
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
