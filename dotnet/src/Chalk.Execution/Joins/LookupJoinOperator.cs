using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Joins;

/// <summary>
/// A cross-source join that asks the lookup source for the driving side's keys (D103's
/// <c>LOOKUP</c>, and its one-call variant <c>BROADCAST</c>;
/// <c>docs/design/20-m5-federation.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// The shape is: buffer driving rows until <c>max_keys_per_call</c> <em>distinct non-NULL</em> keys
/// have been collected or the input ends; bind them into the lookup query's key set; run it; hash
/// the buffered driving rows and probe with what comes back; emit; repeat. Distinct keys rather than
/// rows, because a hundred rows over five symbols is one call and not a hundred, and non-NULL
/// because a NULL key matches nothing anywhere — which for INNER and SEMI drops the row and for LEFT
/// and ANTI keeps it, exactly as the local operators have it.
/// </para>
/// <para>
/// The hash table is built on the <em>buffered driving rows</em> and probed with the lookup's, which
/// is the opposite of <see cref="PairJoinOperator"/> and is what makes the lookup side streamable:
/// its rows are consumed as they arrive and never held. What is held is one call's worth of driving
/// rows, which is bounded by construction.
/// </para>
/// <para>
/// LEFT, SEMI and ANTI are all decided after the call returns, from a per-row matched flag: LEFT
/// emits the unmatched driving rows null-padded, SEMI emits the matched ones, ANTI the unmatched.
/// Each of those is in driving-buffer order; the INNER pairs come out grouped by lookup row, which
/// is why the node claims no collation.
/// </para>
/// </remarks>
internal sealed class LookupJoinOperator : OperatorBase
{
    private readonly IBatchOperator _driving;
    private readonly ISourceRuntime _source;
    private readonly RemoteQuery _query;
    private readonly KeySetQuery _keySet;

    /// <summary>
    /// The lookup's own text with its literals as pseudonyms, when the engine's redaction is on
    /// (D262), and null otherwise. Computed at prepare; a failure path never makes a call.
    /// </summary>
    private readonly string? _redactedQueryText;
    private readonly JoinType _joinType;
    private readonly int _maxKeysPerCall;
    private readonly int[] _drivingKeys;
    private readonly int[] _lookupKeys;
    private readonly int _drivingWidth;
    private readonly IReadOnlyList<ChalkType> _drivingTypes;
    private readonly IReadOnlyList<ChalkType> _lookupTypes;
    private readonly ChalkType[] _keyTypes;
    private readonly TimeSpan? _timeout;
    private readonly ArrowSchema _lookupSchema;
    private readonly IVectorExpr? _residual;

    private readonly JoinRows _buffered;
    private readonly JoinKeys _keys;
    private readonly JoinHashTable _hash;
    private readonly JoinAssembler _assembler;
    private readonly ColumnarBatch _output;
    private readonly ColumnView[]?[] _lookupChildren;
    private readonly ColumnarBatch _lookupSlot;

    private int[] _outDriving = [];
    private int[] _outLookup = [];
    private bool[] _matched = [];
    private readonly Vector[] _buildKey;
    private readonly Vector[] _probeKey;

    public LookupJoinOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<ChalkType> drivingTypes,
        IReadOnlyList<ChalkType> lookupTypes,
        IBatchOperator driving,
        ISourceRuntime source,
        RemoteQuery query,
        int keySetOrdinal,
        bool keySetAsRows,
        JoinType joinType,
        int maxKeysPerCall,
        IReadOnlyList<int> drivingKeys,
        IReadOnlyList<int> lookupKeys,
        TimeSpan? timeout,
        ArrowSchema lookupSchema,
        IVectorExpr? residual,
        string? redactedQueryText = null)
        : base(context, schema, outputTypes, path)
    {
        _driving = driving;
        _source = source;
        _query = query;
        // The template, not the per-call expansion (D262, ADR 0043 §5): the keys a call binds travel
        // as parameters and are not in the text at all, so what a redacted failure has to say is
        // which lookup failed, and the template says exactly that without an expansion that could
        // fail while a failure is already being reported.
        _redactedQueryText = redactedQueryText;
        _keySet = new KeySetQuery(query.QueryText, keySetOrdinal, keySetAsRows, drivingKeys.Count);
        _joinType = joinType;
        _maxKeysPerCall = Math.Max(1, maxKeysPerCall);
        _drivingKeys = [.. drivingKeys];
        _lookupKeys = [.. lookupKeys];
        _drivingWidth = drivingTypes.Count;
        _drivingTypes = drivingTypes;
        _lookupTypes = lookupTypes;
        _keyTypes = [.. drivingKeys.Select(k => drivingTypes[k])];
        _timeout = timeout;
        _lookupSchema = lookupSchema;
        _residual = residual;

        _buildKey = new Vector[_drivingKeys.Length];
        _probeKey = new Vector[_lookupKeys.Length];
        _buffered = new JoinRows(drivingTypes);
        _keys = JoinKeys.Bind(drivingTypes, drivingKeys, lookupTypes, lookupKeys);
        _hash = new JoinHashTable(_keys);
        _assembler = new JoinAssembler(
            outputTypes,
            joinType is JoinType.Semi or JoinType.Anti ? outputTypes.Count : _drivingWidth);
        _output = NewOutput();
        _lookupSlot = new ColumnarBatch(lookupTypes);
        _lookupChildren = ArrowBatchViews.ChildHolders(lookupTypes);
    }

    protected override async ValueTask DisposeCoreAsync() => await _driving.DisposeAsync().ConfigureAwait(false);

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var arena = Context.Arena;
        var batchSize = Context.Settings.BatchSize;
        _outDriving = arena.Rent<int>(batchSize);
        _outLookup = arena.Rent<int>(batchSize);
        _matched = arena.Rent<bool>(_maxKeysPerCall * 4);

        // The keys one call carries, in first-seen order, and the set that makes "distinct" cheap.
        // Flat: a key of `width` columns occupies `width` consecutive entries, because that is the
        // order they are bound in. Boxed on purpose: they are about to be handed to a database
        // driver as parameter values, which is a boxed world, and there are at most
        // `max_keys_per_call` keys per call.
        var width = _drivingKeys.Length;
        var callKeys = new List<ScalarValue>(_maxKeysPerCall * width);
        var seen = new HashSet<KeyRow>(_maxKeysPerCall);

        // Two scratch rows, reused: `identity` is what the set is probed with, so a key already
        // seen costs no array at all, and only a new one is cloned into the set.
        var identity = new object?[width];
        var values = new ScalarValue[width];

        try
        {
            _buffered.Begin();
            await foreach (var batch in _driving.ExecuteAsync(ct).ConfigureAwait(false))
            {
                    for (var row = 0; row < batch.Count; row++)
                    {
                      
                        var lane = batch.RowAt(row);
                        var present = true;
                          
                        try
                        {
                        for (var k = 0; k < width && present; k++)
                        {
                            var value = ScalarValue.FromLane(batch.Column(_drivingKeys[k]), lane, _keyTypes[k]);
                            values[k] = value;

                            // A NULL anywhere in the key is a key that matches nothing, so the whole
                            // row is not one this call asks about — exactly as a NULL scalar key is.
                            present = !value.IsNull;
                            identity[k] = present ? value.ToHost() : null;
                        }
                        }
                        catch
                        {
                            throw;
                        }

                        // A key we have not sent yet, and the call is already full: flush before this
                        // row rather than after it, so no call ever exceeds the source's ceiling.
                        if (present
                            && !seen.Contains(new KeyRow(identity))
                            && callKeys.Count == _maxKeysPerCall * width)
                        {
                            _buffered.Finish();
                            await foreach (var emitted in CallAsync(callKeys, ct).ConfigureAwait(false))
                            {
                                yield return emitted;
                            }

                            _buffered.Begin();
                            callKeys.Clear();
                            seen.Clear();
                        }

                        try
                        {
                            Grow(ref _matched, arena, _buffered.Count + 1);
                            _buffered.AppendRow(batch, row);
                            if (present && seen.Add(new KeyRow((object?[])identity.Clone())))
                            {
                                for (var k = 0; k < width; k++)
                                {
                                    callKeys.Add(values[k]);
                                }
                            }
                        }
                        catch
                        {
                            throw;
                        }
                    }
            }

            if (_buffered.Count > 0)
            {
                _buffered.Finish();
                await foreach (var emitted in CallAsync(callKeys, ct).ConfigureAwait(false))
                {
                    yield return emitted;
                }
            }
        }
        finally
        {
            _buffered.Release();
            _hash.Release();
            arena.Return(_outDriving);
            arena.Return(_outLookup);
            arena.Return(_matched);
            _outDriving = [];
            _outLookup = [];
            _matched = [];
        }
    }

    /// <summary>
    /// One call: bind the keys, run the query, join what comes back against the buffered rows, then
    /// pay whatever the join type owes the driving rows that matched nothing.
    /// </summary>
    private async IAsyncEnumerable<ColumnarBatch> CallAsync(
        List<ScalarValue> keys,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        System.Array.Clear(_matched, 0, _buffered.Count);

        // Nothing to ask for: every buffered row has a NULL key, and a NULL matches nothing.
        if (keys.Count > 0)
        {
            // One table per call, and the previous one's arena arrays go back first: Build rents and
            // never releases, so a second call without this leaks a call's worth of slots.
            _hash.Release();
            JoinKeys.Columns(_buildKey, _buffered.Columns, _drivingKeys, _buffered.Count);
            _hash.Build(Context.Arena, _buildKey, _buffered.Count);

            await foreach (var emitted in ProbeAsync(keys, ct).ConfigureAwait(false))
            {
                yield return emitted;
            }
        }

        var trailing = Trailing();
        if (trailing.Count > 0)
        {
            yield return trailing.Batch;
        }
    }

    /// <summary>The lookup query's rows, probed against the buffered driving rows as they arrive.</summary>
    private async IAsyncEnumerable<ColumnarBatch> ProbeAsync(
        List<ScalarValue> keys,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // `keys` is flat, so a call of `width`-column keys carries `keys.Count / width` of them and
        // its parameter types cycle through the key's columns in the same order.
        var width = _drivingKeys.Length;
        var parameterTypes = new ChalkType[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            parameterTypes[i] = _keyTypes[i % width];
        }

        var request = new RemoteQueryRequest
        {
            QueryText = _keySet.For(keys.Count / width),
            PushedPlan = _query.PushedPlan,
            Parameters = keys.Select(k => k.ToHost()).ToArray(),
            ParameterTypes = parameterTypes,
            OutputSchema = _lookupSchema,
            BatchSize = Context.Settings.BatchSize,
            Timeout = _timeout,
            Dialect = _query.Dialect,
        };

        Context.Stats.AddRemoteCalls(1);
        using var lease = await Context.RemoteGate
            .EnterAsync(_source.SourceId, ct)
            .ConfigureAwait(false);

        var fetched = 0L;
        var projectsLookup = _joinType is not (JoinType.Semi or JoinType.Anti);
        var pairs = 0;

        await foreach (var batch in RemoteFetch.RunAsync(
            _source,
            request,
            Context,
            Context.Settings.RemotePrefetchDepth,
            ct,
            _redactedQueryText))
        {
            try
            {
                Context.Stats.AddRowsFetched(batch.Length);
                fetched += batch.Length;
                ArrowBatchViews.Fill(_lookupSlot, batch, _lookupTypes, _lookupChildren);
                JoinKeys.Columns(_probeKey, _lookupSlot.Columns, _lookupKeys, _lookupSlot.Count);

                for (var row = 0; row < _lookupSlot.Count; row++)
                {
                    if (_keys.AnyNull(_probeKey, row))
                    {
                        continue;
                    }

                    for (var driving = _hash.First(_probeKey, row); driving >= 0; driving = _hash.Next(driving))
                    {
                        if (!_keys.Equal(_hash.BuildKeys, driving, _probeKey, row))
                        {
                            continue;
                        }

                        _matched[driving] = true;
                        if (!projectsLookup)
                        {
                            continue; // SEMI and ANTI decide from the flag alone
                        }

                        _outDriving[pairs] = driving;
                        _outLookup[pairs] = row;
                        if (++pairs == _outDriving.Length)
                        {
                            yield return Emit(pairs);
                            pairs = 0;
                        }
                    }
                }

                if (pairs > 0)
                {
                    // The lookup slot's views point at this Arrow batch, which is released below, so
                    // the pairs it produced are emitted before it goes.
                    yield return Emit(pairs);
                    pairs = 0;
                }
            }
            finally
            {
                batch.Dispose();
            }
        }

        Context.Stats.RecordFetch(_source.SourceId, fetched, Context.Settings.TimeProvider.GetUtcNow());
    }

    /// <summary>What the join type owes the buffered rows after the call: nothing, or a padded pass.</summary>
    private (int Count, ColumnarBatch Batch) Trailing()
    {
        var wanted = _joinType switch
        {
            JoinType.Left => false,
            JoinType.Anti => false,
            JoinType.Semi => true,
            _ => (bool?)null,
        };
        if (wanted is not { } matched)
        {
            return (0, _output);
        }

        var rows = 0;
        for (var i = 0; i < _buffered.Count && rows < _outDriving.Length; i++)
        {
            if (_matched[i] == matched)
            {
                _outDriving[rows] = i;
                _outLookup[rows] = -1;
                rows++;
            }
        }

        return (rows, rows == 0 ? _output : Emit(rows));
    }

    private ColumnarBatch Emit(int rows)
    {
        var lookupColumns = _joinType is JoinType.Semi or JoinType.Anti
            ? ReadOnlySpan<ColumnView>.Empty
            : _lookupSlot.Columns;
        return _assembler.Emit(
            _output,
            _buffered.Columns,
            _outDriving.AsSpan(0, rows),
            lookupColumns,
            _outLookup.AsSpan(0, rows));
    }

    /// <summary>
    /// One key's host values, compared by value rather than by reference so a
    /// <see cref="HashSet{T}"/> can tell one key from another (F50). A struct over an array the
    /// caller owns: probing the set costs nothing, and only a key that turns out to be new is
    /// cloned into it.
    /// </summary>
    private readonly struct KeyRow(object?[] values) : IEquatable<KeyRow>
    {
        private readonly object?[] _values = values;

        public bool Equals(KeyRow other)
        {
            for (var i = 0; i < _values.Length; i++)
            {
                if (!Equals(_values[i], other._values[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is KeyRow other && Equals(other);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            foreach (var value in _values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    private static void Grow(ref bool[] flags, ExecutionArena arena, int wanted)
    {
        if (flags.Length >= wanted)
        {
            return;
        }

        var grown = arena.Rent<bool>(Math.Max(wanted, flags.Length * 2));
        System.Array.Copy(flags, grown, flags.Length);
        arena.Return(flags);
        flags = grown;
    }
}
