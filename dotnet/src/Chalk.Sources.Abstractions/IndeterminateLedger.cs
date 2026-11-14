using System.Data.Common;

namespace Chalk.Sources;

/// <summary>How the quarantine settled a decision its policy could not reach (F302, design 82).</summary>
internal enum IndeterminateResolution
{
    /// <summary>A filter's condition raised for rows, and they were not admitted.</summary>
    RowsNotAdmitted,

    /// <summary>A guarded rule condition raised for rows, and the next rule decided them.</summary>
    RuleNotApplied,

    /// <summary>A source raised, and its part was evaluated here instead.</summary>
    EvaluatedHere,

    /// <summary>It could not be settled closed, and the statement's result was withheld.</summary>
    StatementWithheld,
}

/// <summary>
/// One kind of indeterminate decision in one execution: the tables, how it was settled, how many it
/// covered, and the first one's cause. The cause may quote a value the policy hides.
/// </summary>
internal sealed record IndeterminateRecord(
    Guid CorrelationId,
    string Tables,
    IndeterminateResolution Resolution,
    long Occurrences,
    Exception Cause,
    bool AtSource,
    string? SourceId,
    string? SqlState);

/// <summary>
/// What one execution's access policy could not decide (F302, design 82), kept per tables and
/// resolution with the first cause and a count, so a filter that settles forty thousand rows is one
/// entry. The correlation id is the execution's: made on first need, shared by every entry and by the
/// exception a withheld result throws. Thread-safe: a parallel scan records from several threads.
/// </summary>
internal sealed class IndeterminateLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tables, IndeterminateResolution Resolution), Entry> _entries = [];
    private Guid _correlation;

    /// <summary>The execution's correlation id, made the first time it is asked for.</summary>
    public Guid CorrelationId
    {
        get
        {
            lock (_gate)
            {
                if (_correlation == Guid.Empty)
                {
                    _correlation = Guid.NewGuid();
                }

                return _correlation;
            }
        }
    }

    /// <summary>Records <paramref name="count"/> more of one kind, keeping the first cause.</summary>
    public void Record(string tables, IndeterminateResolution resolution, long count, Exception cause)
    {
        var (atSource, sourceId, sqlState) = SourceOf(cause);
        lock (_gate)
        {
            if (_entries.TryGetValue((tables, resolution), out var entry))
            {
                entry.Occurrences += count;
                return;
            }

            _entries[(tables, resolution)] = new Entry(cause, atSource, sourceId, sqlState) { Occurrences = count };
        }
    }

    /// <summary>Every kind recorded, once each, and empties the ledger for the next execution.</summary>
    public IReadOnlyList<IndeterminateRecord> Drain()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return [];
            }

            if (_correlation == Guid.Empty)
            {
                _correlation = Guid.NewGuid();
            }

            var correlation = _correlation;
            var drained = _entries
                .Select(e => new IndeterminateRecord(
                    correlation,
                    e.Key.Tables, e.Key.Resolution, e.Value.Occurrences, e.Value.Cause, e.Value.AtSource,
                    e.Value.SourceId, e.Value.SqlState))
                .ToList();
            _entries.Clear();
            return drained;
        }
    }

    /// <summary>
    /// An error a value causes, as against cancellation or one of the engine's own refusals: what the
    /// quarantine settles. A source's own error counts, a provider's <see cref="DbException"/>.
    /// </summary>
    public static bool IsDataError(Exception error) =>
        error switch
        {
            OperationCanceledException => false,
            ExecutionException { InnerException: { } inner } => IsDataError(inner),
            ChalkException => false,
            InvalidOperationException or FormatException or ArithmeticException or InvalidCastException
                or ArgumentException or DbException => true,
            _ => false,
        };

    /// <summary>The source's error class where the cause is a provider's, the innermost such.</summary>
    public static string? SqlStateOf(Exception error)
    {
        for (var at = error; at is not null; at = at.InnerException)
        {
            if (at is DbException { SqlState: { Length: > 0 } state })
            {
                return state;
            }
        }

        return null;
    }

    private static (bool AtSource, string? SourceId, string? SqlState) SourceOf(Exception cause)
    {
        var state = SqlStateOf(cause);
        var atSource = false;
        for (var at = cause; at is not null; at = at.InnerException)
        {
            atSource |= at is DbException;
        }

        return (atSource, null, state);
    }

    private sealed class Entry(Exception cause, bool atSource, string? sourceId, string? sqlState)
    {
        public Exception Cause { get; } = cause;

        public bool AtSource { get; } = atSource;

        public string? SourceId { get; } = sourceId;

        public string? SqlState { get; } = sqlState;

        public long Occurrences { get; set; }
    }
}
