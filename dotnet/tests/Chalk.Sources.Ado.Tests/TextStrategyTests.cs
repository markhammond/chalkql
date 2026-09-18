using System.Data;
using System.Data.Common;
using System.Text;
using Apache.Arrow;
using Chalk.Catalog;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// The text strategies of D149 (<c>docs/design/24-zero-gc.md</c> §7), against a provider whose
/// answers are controlled: which strategy a column takes, that every one produces the same bytes,
/// and that a provider whose <c>GetBytes</c> is expensive is not taken up on it (V44).
/// </summary>
/// <remarks>
/// Against a fake rather than against SQLite or PostgreSQL because what is under test is the
/// <em>choice</em>, and a real provider only ever demonstrates its own. The real providers are
/// covered by the corpora, which run the same text through all three paths and compare.
/// </remarks>
public sealed class TextStrategyTests
{
    /// <summary>
    /// The measured strategy is cached per provider type for the whole process, and this class's
    /// one provider type plays every behaviour in turn — so each test starts from nothing measured.
    /// </summary>
    public TextStrategyTests() => AdoBatchReader.ForgetMeasuredStrategies();

    private static readonly string[] Values = ["", "short", "a value longer than twelve bytes", "café", "日経"];

    public static TheoryData<TextProviderBehaviour, string> Behaviours() => new()
    {
        { TextProviderBehaviour.CheapBytes, "GetBytes" },
        { TextProviderBehaviour.ExpensiveBytes, "GetString" },
        { TextProviderBehaviour.SlightlyExpensiveBytes, "GetString" },
        { TextProviderBehaviour.CheapChars, "GetChars" },
        { TextProviderBehaviour.NeitherBytesNorChars, "GetString" },
    };

    /// <summary>
    /// Every strategy produces the same bytes, and the one it takes is the one the provider serves
    /// best: <c>GetBytes</c> when it is free, <c>GetChars</c> when that is what is on offer, and a
    /// string when the alternative would cost more than the string does.
    /// </summary>
    [Theory]
    [MemberData(nameof(Behaviours))]
    public async Task The_strategy_is_the_cheapest_the_provider_serves(
        TextProviderBehaviour behaviour, string expected)
    {
        var reader = new TextReader(behaviour, Values);
        var values = await ReadAsync(reader).ConfigureAwait(true);

        Assert.Equal(Values, values);
        Assert.Equal(expected, reader.Chosen);
    }

    /// <summary>
    /// The measurement is paid once per provider type and data type in the process: a second
    /// reader of the same provider takes the cached answer and probes nothing, which is what keeps
    /// a provider that refuses the query from paying an exception per column per scan.
    /// </summary>
    [Fact]
    public async Task A_second_reader_of_the_same_provider_is_not_probed_again()
    {
        var first = new TextReader(TextProviderBehaviour.CheapBytes, Values);
        _ = await ReadAsync(first).ConfigureAwait(true);
        Assert.Equal("GetBytes", first.Chosen);

        var second = new TextReader(TextProviderBehaviour.CheapBytes, Values);
        var values = await ReadAsync(second).ConfigureAwait(true);
        Assert.Equal(Values, values);
        Assert.Equal("GetBytes", second.Chosen);

        // One fewer length query: the probe the first reader paid and the second did not.
        Assert.Equal(first.BytesProbes - 1, second.BytesProbes);
    }

    /// <summary>
    /// D263 (ADR 0046 §2): the table naming which strategy a known provider costs least is gone.
    /// DuckDB no longer has a name here at all — <c>Chalk.Sources.DuckDb</c> declares its own trait
    /// instead (<c>ProviderTraitsTests</c>) — so an undeclared DuckDB reader is simply probed like
    /// any other provider whose <c>GetBytes</c> refuses a VARCHAR column. SQLite alone keeps a name,
    /// and it names a different question: <see cref="The_one_provider_that_cannot_be_probed_is_not_probed"/>.
    /// </summary>
    [Fact]
    public void DuckDB_has_no_name_here_and_can_be_probed_like_any_other_provider()
    {
        Assert.True(AdoBatchReader.CanBeProbed(typeof(DuckDB.NET.Data.DuckDBDataReader)));
    }

    /// <summary>
    /// A declared trait (D263, ADR 0046 §2) is used exactly as given, and the provider is never
    /// asked: a fake set up to answer <c>GetBytes</c> for nothing — which an undeclared probe would
    /// have chosen — declares <see cref="TextStrategy.String"/> instead, and gets it, with no probe
    /// paid at all.
    /// </summary>
    [Fact]
    public async Task A_declared_trait_is_used_without_a_probe()
    {
        var before = AdoBatchReader.MeasurementRuns;
        var reader = new TextReader(TextProviderBehaviour.CheapBytes, Values);
        var values = await ReadAsync(reader, new AdoProviderTraits { Text = TextStrategy.String })
            .ConfigureAwait(true);

        Assert.Equal(Values, values);
        Assert.Equal(0, reader.BytesProbes);
        Assert.Equal(0, reader.CharsProbes);
        Assert.True(reader.StringReads > 0);
        Assert.Equal(before, AdoBatchReader.MeasurementRuns);
    }

    /// <summary>
    /// An undeclared provider is measured once per process per column type, as before D263 — proven
    /// here against the process-wide count of length queries actually run, not merely against the
    /// fake's own count of them (<see cref="A_second_reader_of_the_same_provider_is_not_probed_again"/>
    /// already does that).
    /// </summary>
    [Fact]
    public async Task An_undeclared_provider_is_probed_once_per_process()
    {
        var before = AdoBatchReader.MeasurementRuns;
        var first = new TextReader(TextProviderBehaviour.CheapBytes, Values);
        _ = await ReadAsync(first).ConfigureAwait(true);
        Assert.Equal(before + 1, AdoBatchReader.MeasurementRuns);

        var second = new TextReader(TextProviderBehaviour.CheapBytes, Values);
        _ = await ReadAsync(second).ConfigureAwait(true);
        Assert.Equal(before + 1, AdoBatchReader.MeasurementRuns);
    }

    /// <summary>
    /// A provider that refuses the length query is not asked twice, and a value it did refuse is
    /// still readable as a string — which is what makes the probe safe to run on the first value
    /// rather than on a value of its own.
    /// </summary>
    [Fact]
    public async Task A_refusal_costs_one_probe_and_no_rows()
    {
        var reader = new TextReader(TextProviderBehaviour.NeitherBytesNorChars, Values);
        _ = await ReadAsync(reader).ConfigureAwait(true);

        Assert.Equal(1, reader.BytesProbes);
        Assert.Equal(1, reader.CharsProbes);
        Assert.Equal(Values.Length, reader.StringReads);
    }

    /// <summary>
    /// The cheap path reads each value once — a length query and one fill — and never makes a
    /// string.
    /// </summary>
    [Fact]
    public async Task The_bytes_path_never_makes_a_string()
    {
        var reader = new TextReader(TextProviderBehaviour.CheapBytes, Values);
        _ = await ReadAsync(reader).ConfigureAwait(true);

        Assert.Equal(0, reader.StringReads);

        // One probe, then a length query per value and a fill for every value that has bytes: the
        // empty string needs no fill.
        Assert.Equal(1 + Values.Length + Values.Count(v => v.Length > 0), reader.BytesProbes);
    }

    /// <summary>
    /// V55: the one provider whose length query is not a question but a crash — on either
    /// accessor. A SIGSEGV cannot be caught, so it cannot be pinned by running it; what is pinned
    /// is the guard that stops it being run, and that the guard is narrow — no other reader loses
    /// the measured path.
    /// </summary>
    [Fact]
    public void The_one_provider_that_cannot_be_probed_is_not_probed()
    {
        Assert.False(AdoBatchReader.CanBeProbed(
            typeof(Microsoft.Data.Sqlite.SqliteDataReader)));

        Assert.True(AdoBatchReader.CanBeProbed(typeof(TextReader)));
        Assert.True(AdoBatchReader.CanBeProbed(typeof(DbDataReader)));
    }

    /// <summary>
    /// And the guard where it matters: two computed TEXT columns out of real SQLite, which is the
    /// shape that killed the process before (V55). Reaching the end of the reader <em>is</em> the
    /// assertion; a regression here does not fail, it takes the test run with it.
    /// </summary>
    [Fact]
    public async Task Two_computed_text_columns_out_of_sqlite_are_read_without_a_probe()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                "CREATE TABLE t(a TEXT, b TEXT); INSERT INTO t VALUES ('AIR', 'R'), ('SHIP', 'N');";
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT a || '-' || b AS j, a || '!' AS k FROM t ORDER BY a";
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var schema = new ArrowSchema.Builder()
            .Field(new Field("j", Apache.Arrow.Types.StringType.Default, nullable: true))
            .Field(new Field("k", Apache.Arrow.Types.StringType.Default, nullable: true))
            .Build();

        using var arena = new ExecutionArena();
        using var batches = new AdoBatchReader(
            "sqlite",
            "SELECT a || '-' || b, a || '!' FROM t",
            schema,
            [ChalkType.String(nullable: true), ChalkType.String(nullable: true)],
            arena,
            16);

        var joined = new List<string?>();
        while (await batches.ReadBatchAsync(reader, TestContext.Current.CancellationToken)
            .ConfigureAwait(true) is { } batch)
        {
            using (batch)
            {
                var first = (StringArray)batch.Column(0);
                for (var i = 0; i < batch.Length; i++)
                {
                    joined.Add(first.GetUtf8(i).ToString());
                }
            }
        }

        Assert.Equal(["AIR-R", "SHIP-N"], joined);
    }

    private static Task<List<string?>> ReadAsync(TextReader reader) => ReadAsync(reader, traits: null);

    private static async Task<List<string?>> ReadAsync(TextReader reader, AdoProviderTraits? traits)
    {
        var schema = new ArrowSchema.Builder()
            .Field(new Field("v", Apache.Arrow.Types.StringType.Default, nullable: true))
            .Build();

        using var arena = new ExecutionArena();
        using var batches = new AdoBatchReader(
            "fake", "a pushed query", schema, [ChalkType.String(nullable: true)], arena, 16,
            profile: null, traits: traits);

        var values = new List<string?>();
        while (await batches.ReadBatchAsync(reader, TestContext.Current.CancellationToken)
            .ConfigureAwait(true) is { } batch)
        {
            using (batch)
            {
                var column = (StringArray)batch.Column(0);
                for (var i = 0; i < batch.Length; i++)
                {
                    values.Add(column.IsNull(i) ? null : column.GetUtf8(i).ToString());
                }
            }
        }

        return values;
    }
}

/// <summary>What a provider does when asked for a TEXT value's bytes or characters.</summary>
public enum TextProviderBehaviour
{
    /// <summary>Answers out of its own buffer, which is what Npgsql does (V41).</summary>
    CheapBytes,

    /// <summary>
    /// Answers, but materialises the value to do it — which is what
    /// <c>Microsoft.Data.Sqlite</c> does, and why capability is not the question (V44).
    /// </summary>
    ExpensiveBytes,

    /// <summary>
    /// Answers for a little: 128 bytes, which is what <c>Microsoft.Data.Sqlite</c> costs on a
    /// <em>computed</em> column and what the old budget admitted (V55). "Free" is zero, not a
    /// threshold, so this is a <c>GetString</c> column like its expensive sibling.
    /// </summary>
    SlightlyExpensiveBytes,

    /// <summary>Refuses <c>GetBytes</c> and streams characters instead.</summary>
    CheapChars,

    /// <summary>Refuses both, so the value arrives as a string.</summary>
    NeitherBytesNorChars,
}

/// <summary>
/// One TEXT column whose <c>GetBytes</c>, <c>GetChars</c> and <c>GetString</c> behave as the test
/// tells them to, and which counts what was called.
/// </summary>
internal sealed class TextReader : DbDataReader
{
    private readonly TextProviderBehaviour _behaviour;
    private readonly string[] _values;

    /// <summary>
    /// The UTF-8 up front, so a cheap provider's length query really is free: the probe measures
    /// allocation, and a fake that encoded on demand would allocate to answer and so be an
    /// expensive provider wearing a cheap one's name (V55).
    /// </summary>
    private readonly byte[][] _utf8;

    private int _row = -1;

    public TextReader(TextProviderBehaviour behaviour, string[] values)
    {
        _behaviour = behaviour;
        _values = values;
        _utf8 = [.. values.Select(Encoding.UTF8.GetBytes)];
    }

    /// <summary>Which accessor the reader settled on, inferred from what was called.</summary>
    public string Chosen =>
        StringReads > 0 ? "GetString" : CharsProbes > 1 ? "GetChars" : "GetBytes";

    public int BytesProbes { get; private set; }

    public int CharsProbes { get; private set; }

    public int StringReads { get; private set; }

    public override int FieldCount => 1;

    public override bool HasRows => _values.Length > 0;

    public override bool IsClosed => false;

    public override int RecordsAffected => 0;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override string GetName(int ordinal) => "v";

    public override int GetOrdinal(string name) => 0;

    public override Type GetFieldType(int ordinal) => typeof(string);

    public override string GetDataTypeName(int ordinal) => "TEXT";

    public override bool Read() => ++_row < _values.Length;

    public override Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(Read());

    public override bool IsDBNull(int ordinal) => false;

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken ct) =>
        Task.FromResult(false);

    public override bool NextResult() => false;

    public override object GetValue(int ordinal) => GetString(ordinal);

    public override string GetString(int ordinal)
    {
        StringReads++;
        return _values[_row];
    }

    public override long GetBytes(
        int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        BytesProbes++;
        if (_behaviour is TextProviderBehaviour.CheapChars
            or TextProviderBehaviour.NeitherBytesNorChars)
        {
            throw new InvalidCastException("GetBytes is not supported on this column.");
        }

        if (_behaviour == TextProviderBehaviour.ExpensiveBytes)
        {
            // What a provider that materialises the value to answer looks like from outside.
            GC.KeepAlive(new byte[4096]);
        }
        else if (_behaviour == TextProviderBehaviour.SlightlyExpensiveBytes)
        {
            // And what one that only caches a little looks like — 128 bytes, the old budget (V55).
            GC.KeepAlive(new byte[104]);
        }

        var bytes = _utf8[_row];
        if (buffer is null)
        {
            return bytes.Length;
        }

        var taken = Math.Min(length, bytes.Length - (int)dataOffset);
        bytes.AsSpan((int)dataOffset, taken).CopyTo(buffer.AsSpan(bufferOffset));
        return taken;
    }

    public override long GetChars(
        int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        CharsProbes++;
        if (_behaviour != TextProviderBehaviour.CheapChars)
        {
            throw new InvalidCastException("GetChars is not supported on this column.");
        }

        var chars = _values[_row].AsSpan();
        if (buffer is null)
        {
            return chars.Length;
        }

        var taken = Math.Min(length, chars.Length - (int)dataOffset);
        chars.Slice((int)dataOffset, taken).CopyTo(buffer.AsSpan(bufferOffset));
        return taken;
    }

    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

    public override byte GetByte(int ordinal) => throw new NotSupportedException();

    public override char GetChar(int ordinal) => throw new NotSupportedException();

    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

    public override double GetDouble(int ordinal) => throw new NotSupportedException();

    public override float GetFloat(int ordinal) => throw new NotSupportedException();

    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

    public override short GetInt16(int ordinal) => throw new NotSupportedException();

    public override int GetInt32(int ordinal) => throw new NotSupportedException();

    public override long GetInt64(int ordinal) => throw new NotSupportedException();

    public override int GetValues(object[] values) => throw new NotSupportedException();

    public override System.Collections.IEnumerator GetEnumerator() =>
        throw new NotSupportedException();
}
