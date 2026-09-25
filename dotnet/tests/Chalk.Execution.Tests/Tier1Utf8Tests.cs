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
/// Tier 1 delegates over a STRING lane written in <c>ReadOnlySpan&lt;byte&gt;</c> (D146, D304,
/// <c>docs/design/24-zero-gc.md</c> §4): the lane's bytes are lent for the call and cannot outlive
/// it, a span answered is copied into the column with one validity check, and none of it allocates.
/// A <c>Utf8String</c> over the lane is the spelling this replaced, and is refused by name.
/// </summary>
/// <remarks>
/// The gate itself — 0 bytes per batch, beside the existing Tier 1 gate — is the benchmark's, where
/// the pipeline is pooled and an output batch's Arrow graph is not in the measurement. What is here
/// is the part a unit test can hold: the same answers as the <c>string</c> spelling, NULLs, the
/// refusal of invalid UTF-8, the signature check and the boxed path.
/// </remarks>
[Experimental("CHALK001")]
public sealed class Tier1Utf8Tests
{
    private sealed record Row(Utf8String Symbol, Utf8String? Label);

    private static readonly string[] Words =
        ["btcusdt", "ÉTOILE", "MiXeD", "", "a_very_long_symbol_name", "日経"];

    [Fact]
    public async Task A_span_delegate_reads_the_lane_and_writes_bytes_back()
    {
        var results = await ProjectAsync(
            StringToString("upper_ascii"),
            new HostScalar1<ReadOnlySpan<byte>, ReadOnlySpan<byte>>("upper_ascii", Utf8Fixture.UpperAscii));

        Assert.Equal(Words.Select(UpperAsciiReference), results);
    }

    [Fact]
    public async Task A_span_delegate_and_a_string_delegate_agree()
    {
        var viaBytes = await ProjectAsync(
            StringToString("upper_ascii"),
            new HostScalar1<ReadOnlySpan<byte>, ReadOnlySpan<byte>>("upper_ascii", Utf8Fixture.UpperAscii));
        var viaString = await ProjectAsync(
            StringToString("upper_ascii"),
            new HostScalar1<string, string>("upper_ascii", static s => UpperAsciiReference(s)));

        Assert.Equal(viaString, viaBytes);
    }

    /// <summary>
    /// A span answered may be a slice of the span received: the input outlives the call, so the
    /// compiler allows it, and the engine copies the bytes out before the next row.
    /// </summary>
    [Fact]
    public async Task A_span_result_may_be_a_slice_of_the_input()
    {
        var results = await ProjectAsync(
            StringToString("before_underscore"),
            new HostScalar1<ReadOnlySpan<byte>, ReadOnlySpan<byte>>("before_underscore", static v =>
            {
                var at = v.IndexOf((byte)'_');
                return at < 0 ? v : v[..at];
            }));

        Assert.Equal(["btcusdt", "ÉTOILE", "MiXeD", "", "a", "日経"], results);
    }

    /// <summary>A result spelled <c>Utf8String</c> is the host's own memory, and stays legal.</summary>
    [Fact]
    public async Task A_Utf8String_result_is_the_hosts_own_memory()
    {
        var results = await ProjectAsync(
            StringToString("copy"),
            new HostScalar1<ReadOnlySpan<byte>, Utf8String>("copy", static v => Utf8String.Copy(v)));

        Assert.Equal(Words, results);
    }

    [Fact]
    public async Task A_span_argument_returning_a_long_is_the_byte_length()
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
            new HostScalar1<ReadOnlySpan<byte>, long>("byte_length", static v => v.Length),
            column: 0);

        Assert.Equal(
            Words.Select(w => (object?)(long)System.Text.Encoding.UTF8.GetByteCount(w)),
            results);
    }

    /// <summary>
    /// A non-strict delegate sees the NULL, and a span has no NULL to see it as: the nullable
    /// spelling of a STRING parameter is <c>string?</c>, the allocating one — the empty string and a
    /// NULL are different values, which is what the nullable spelling exists to say.
    /// </summary>
    [Fact]
    public async Task A_non_strict_string_delegate_sees_the_null()
    {
        var seenNull = 0;
        var results = await ProjectAsync(
            NullableStringToString("or_missing", strict: false),
            new HostScalar1<string?, string?>("or_missing", value =>
            {
                if (value is null)
                {
                    seenNull++;
                    return "<missing>";
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
    public async Task Invalid_UTF_8_from_a_span_is_refused_naming_the_row()
    {
        // The operator wraps what a plan throws, naming the failing node; the cause is the refusal.
        var wrapped = await Assert.ThrowsAsync<ExecutionException>(() => ProjectAsync(
            StringToString("bad"),
            new HostScalar1<ReadOnlySpan<byte>, ReadOnlySpan<byte>>(
                "bad",
                static v => v.Length == 0 ? v : new byte[] { 0xC3, 0x28 })));

        var failure = Assert.IsType<InvalidUtf8Exception>(wrapped.InnerException);
        Assert.Equal(0, failure.Row);
        Assert.Contains("Tier 1 function", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not valid UTF-8", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_signature_check_accepts_a_span_for_a_STRING()
    {
        var descriptor = StringToString("f");
        UserFunctionBinding.Check(
            descriptor, new HostScalar1<ReadOnlySpan<byte>, ReadOnlySpan<byte>>("f", static v => v), "test");
        UserFunctionBinding.Check(
            descriptor, new HostScalar1<string, string>("f", static v => v), "test");
        UserFunctionBinding.Check(
            descriptor, new HostScalar1<ReadOnlySpan<byte>, Utf8String>("f", static v => Utf8String.Copy(v)), "test");

        var wrong = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                descriptor, new HostScalar1<ReadOnlySpan<byte>, long>("f", static v => v.Length), "test"));
        Assert.Contains("ReadOnlySpan<Byte> or String", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", wrong.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spelling this design retired (D304): a <c>Utf8String</c> over the lane is storable, and the
    /// lane's memory is the arena's to reuse, so a parameter so spelled is refused when the engine is
    /// created — strict or not — and the refusal names the two spellings that replace it.
    /// </summary>
    [Fact]
    public void A_Utf8String_parameter_is_refused_as_a_value_the_delegate_could_keep()
    {
        var strict = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                StringToString("f"), new HostScalar1<Utf8String, Utf8String>("f", static v => v), "test"));
        Assert.Contains("parameter 's' is spelled Utf8String", strict.Message, StringComparison.Ordinal);
        Assert.Contains("keep past the call", strict.Message, StringComparison.Ordinal);
        Assert.Contains("ReadOnlySpan<byte>", strict.Message, StringComparison.Ordinal);
        Assert.Contains("A result may still be a Utf8String", strict.Message, StringComparison.Ordinal);

        var nullable = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                NullableStringToString("g", strict: false),
                new HostScalar1<Utf8String?, Utf8String?>("g", static v => v),
                "test"));
        Assert.Contains("parameter 's' is spelled Utf8String?", nullable.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-strict STRING parameter needs a nullable spelling, exactly as a <c>double</c> one does —
    /// and a span has none, so the refusal names <c>string?</c> and the other way out.
    /// </summary>
    [Fact]
    public void A_non_strict_span_parameter_is_refused_naming_the_nullable_spelling()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(
                NullableStringToString("f", strict: false),
                new HostScalar1<ReadOnlySpan<byte>, ReadOnlySpan<byte>>("f", static v => v),
                "test"));
        Assert.Contains("ReadOnlySpan<byte> has no NULL", error.Message, StringComparison.Ordinal);
        Assert.Contains("string?", error.Message, StringComparison.Ordinal);
        Assert.Contains("Strict()", error.Message, StringComparison.Ordinal);

        // The other way out: a strict function never sees the NULL, so the span serves it.
        UserFunctionBinding.Check(
            NullableStringToString("f", strict: true),
            new HostScalar1<ReadOnlySpan<byte>, ReadOnlySpan<byte>>("f", static v => v),
            "test");
    }

    /// <summary>
    /// The reference executor works in boxed values throughout (D13), so the same delegate runs
    /// under both engines: the string it is handed is encoded on the way in, and a span it answers
    /// leaves as its bytes, which the reference decodes for a STRING.
    /// </summary>
    [Fact]
    public void The_boxed_path_hands_a_span_delegate_the_encoded_bytes()
    {
        HostScalar host = new HostScalar1<ReadOnlySpan<byte>, ReadOnlySpan<byte>>("f", Utf8Fixture.UpperAscii);
        Assert.Equal("ÉTOILE", System.Text.Encoding.UTF8.GetString((byte[])host.InvokeBoxed(["ÉTOILE"])!));
        Assert.Equal("BTCUSDT", System.Text.Encoding.UTF8.GetString((byte[])host.InvokeBoxed(["btcusdt"])!));

        HostScalar length = new HostScalar1<ReadOnlySpan<byte>, long>("g", static v => v.Length);
        Assert.Equal(7L, length.InvokeBoxed(["ÉTOILE"]));

        HostScalar nullable = new HostScalar1<string?, long?>("h", static v => v?.Length);
        Assert.Equal(3L, nullable.InvokeBoxed(["abc"]));
        Assert.Null(nullable.InvokeBoxed([null]));

        // A Utf8String result leaves decoded, as it always did.
        HostScalar copy = new HostScalar1<ReadOnlySpan<byte>, Utf8String>("k", static v => Utf8String.Copy(v));
        Assert.Equal("日経", copy.InvokeBoxed(["日経"]));
    }

    private static FunctionDescriptor NullableStringToString(string name, bool strict) => new()
    {
        Name = name,
        Kind = FunctionKind.Scalar,
        Parameters = [new ParameterDescriptor { Name = "s", Type = ChalkType.String(nullable: true) }],
        ReturnType = ChalkType.String(nullable: true),
        Strict = strict,
        Body = new ClientFunctionBody(),
    };

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
