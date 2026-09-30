using System.Security.Cryptography;
using Chalk.Entitlements;
using Google.Protobuf;

namespace Chalk.Client;

/// <summary>
/// The refusals an engine has answered, by the request that drew them (D330). A statement refused
/// once is refused again, for exactly the same request, without asking the planner, and every such
/// refusal carries the one model the first built.
/// </summary>
/// <remarks>
/// <para>
/// Planning is deterministic, and a request names the catalog and statistics versions it is planned
/// against, so a changed catalog, context, option or statement is a different key and nothing needs
/// invalidating: an entry nobody asks for again ages out of the bound.
/// </para>
/// <para>
/// The key is a digest of the request exactly as it would cross the wire, less what belongs to one
/// call — the id a stop addresses and whether the host had already stopped — so a field added to the
/// request later is in the key without anyone remembering to add it. No key is computed until
/// something has been remembered: an engine that is never refused pays nothing.
/// </para>
/// <para>
/// Refusals only. A statement the planner accepts is a <see cref="PreparedQuery"/> the host keeps,
/// which is already the memo.
/// </para>
/// </remarks>
internal sealed class RefusalMemo
{
    /// <summary>
    /// How many refusals an engine remembers: a host's working set of refused statements. One outside
    /// it is asked of the planner again, which is all forgetting costs.
    /// </summary>
    internal const int Capacity = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Remembered>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Remembered> _recency = new();
    private volatile bool _any;

    /// <summary>One refusal, as the next identical request is answered with it.</summary>
    internal sealed record Remembered(
        string Key, string Message, SqlPosition? Position, EntitlementRefusal? Refusal)
    {
        /// <summary>A fresh exception each time, carrying the same refusal.</summary>
        internal EntitlementException Refuse() => new(Message, Position, Refusal, innerException: null);
    }

    /// <summary>Whether anything is remembered, so a prepare computes no key until something is.</summary>
    internal bool Any => _any;

    /// <summary>The number remembered, for a test of the bound.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The key a request is remembered by.</summary>
    internal static string Key(PlanRequest request)
    {
        var wire = GrpcQueryPlanner.ToWire(request);
        if (wire.PlanningOptions is { } planning)
        {
            planning.RequestId = string.Empty;
            planning.StopAtFirstPlan = false;
        }

        return Convert.ToBase64String(SHA256.HashData(wire.ToByteArray()));
    }

    /// <summary>The refusal remembered under <paramref name="key"/>, or null for none.</summary>
    internal Remembered? Find(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return null;
            }

            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value;
        }
    }

    /// <summary>Remembers a refusal, forgetting the least recently asked for beyond the bound.</summary>
    internal void Remember(Remembered entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out var existing))
            {
                _recency.Remove(existing);
            }

            _entries[entry.Key] = _recency.AddFirst(entry);
            while (_entries.Count > Capacity)
            {
                var oldest = _recency.Last!;
                _recency.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }

            _any = true;
        }
    }
}
