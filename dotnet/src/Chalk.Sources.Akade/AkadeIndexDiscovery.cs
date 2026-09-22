using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Akade.IndexedSet;
using Akade.IndexedSet.Concurrency;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Akade;

/// <summary>
/// Reflection boundary for Akade index internals. ConcurrentIndexedSet itself is unwrapped through
/// its public Read API; reflection is confined to the private physical-index registries/selectors
/// that Akade does not otherwise expose.
///
/// Only indexes that Chalk can describe truthfully with today's column-ordinal IndexDescriptor are
/// returned by discovery: a direct-member scalar key, and a compound key of two to four direct
/// members (D280) — written as a tuple lambda, whose recorded source text names the members, or as a
/// method whose members the host states with CompoundIndex. Computed, multi-key and specialised
/// Akade indexes remain physical implementation details until Chalk's catalog can represent their
/// key semantics.
/// </summary>
internal static class AkadeIndexDiscovery
{
    public static AkadeIndexPlan<T, IndexedSet<T>> Discover<T>(
        IndexedSet<T> set,
        AkadeSourceOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(options);

        var discovered = Inspect(set, options);
        return CreatePlan<T, IndexedSet<T>>(
            discovered,
            options,
            candidate => Shapes(Inspect(candidate, options)));
    }

    public static AkadeIndexPlan<T, ConcurrentIndexedSet<T>> Discover<T>(
        ConcurrentIndexedSet<T> set,
        AkadeSourceOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(options);

        var discovered = Inspect(
            ConcurrentIndexedSetAccess.CaptureForQuiescentRead(set),
            options);

        return CreatePlan<T, ConcurrentIndexedSet<T>>(
            discovered,
            options,
            candidate => Shapes(
                Inspect(
                    ConcurrentIndexedSetAccess.CaptureForQuiescentRead(candidate),
                    options)));
    }

    private static AkadeIndexPlan<T, TSet> CreatePlan<T, TSet>(
        IReadOnlyList<AkadeDiscoveredIndex<T>> discovered,
        AkadeSourceOptions<T> options,
        Func<TSet, AkadeIndexShape[]> inspect)
        where TSet : class
    {
        var registrations = new List<IAkadeIndexRegistration<T>>(discovered.Count);

        foreach (var index in discovered)
        {
            var registrationType = typeof(AkadeIndexRegistration<,>)
                .MakeGenericType(typeof(T), index.KeyType);

            var registration = (IAkadeIndexRegistration<T>?)Activator.CreateInstance(
                registrationType,
                index.Name,
                index.Kind,
                index.Accessor,
                index.Members,
                index.Order,
                index.Comparer,
                options.SourceId,
                options.TableName);

            if (registration is not null)
            {
                registrations.Add(registration);
            }
        }

        return new AkadeIndexPlan<T, TSet>(
            registrations,
            Shapes(discovered),
            inspect);
    }

    private static IReadOnlyList<AkadeDiscoveredIndex<T>> Inspect<T>(
        IndexedSet<T> metadataSet,
        AkadeSourceOptions<T> options)
    {
        var physical = ReadDictionary(metadataSet, "_indices");
        var writers = ReadDictionary(metadataSet, "_indexWriters");

        var result = new List<AkadeDiscoveredIndex<T>>(physical.Count);

        foreach (DictionaryEntry pair in physical)
        {
            if (pair.Key is not string name || pair.Value is null)
            {
                continue;
            }

            var kind = Classify(pair.Value.GetType());
            if (kind is null)
            {
                // Prefix/full-text/spatial/vector and any future Akade structures are not planner
                // access paths until Chalk has a matching source-index contract for them.
                continue;
            }

            var writer = writers[name];
            if (writer is null)
            {
                continue;
            }

            var accessor = FindKeyAccessor<T>(writer);
            if (accessor is null)
            {
                continue;
            }

            var invoke = accessor.GetType().GetMethod("Invoke");
            var keyType = invoke?.ReturnType;
            if (keyType is null || keyType == typeof(void))
            {
                continue;
            }

            // A registered function index is known to be a functional expression rather than a base
            // column. Keep it undisclosed until IndexDescriptor can carry expression-valued keys.
            if (options.FunctionIndexes.Any(binding => binding.Method == accessor.Method))
            {
                continue;
            }

            var members = Members<T>(name, keyType, accessor, options);
            if (members is null || !Shaped(keyType, members))
            {
                continue;
            }

            var declared = Declaration(name, accessor, keyType, options);
            var order = Admit(kind.Value, keyType, members, declared, name);
            if (order == AkadeOrder.None)
            {
                continue;
            }

            result.Add(new AkadeDiscoveredIndex<T>(
                name,
                kind.Value,
                keyType,
                accessor,
                members,
                order,
                declared?.Comparer));
        }

        result.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return result;
    }

    /// <summary>
    /// The row members this index's key is made of, in key order, or null when Chalk cannot say what
    /// they are.
    /// </summary>
    /// <remarks>
    /// Two spellings (D280). A lambda accessor names its own members in the text Akade filed the
    /// index under — <c>x =&gt; x.Amount</c> for a scalar key, <c>x =&gt; (x.A, x.B)</c> for a
    /// compound one — and is parsed from it. A method accessor names none, so the host states them
    /// with <c>CompoundIndex</c>, keyed by the accessor's method identity exactly as a function index
    /// is bound; a method with no such declaration stays undisclosed, as it was before.
    /// </remarks>
    private static MemberInfo[]? Members<T>(
        string indexName,
        Type keyType,
        Delegate accessor,
        AkadeSourceOptions<T> options)
    {
        foreach (var binding in options.CompoundIndexes)
        {
            if (binding.Method == accessor.Method)
            {
                return Resolve<T>(binding.Members);
            }
        }

        var arrow = indexName.IndexOf("=>", StringComparison.Ordinal);
        if (arrow <= 0)
        {
            return null;
        }

        var parameter = indexName[..arrow].Trim();
        var body = indexName[(arrow + 2)..].Trim();
        if (parameter.Length == 0)
        {
            return null;
        }

        var components = body.StartsWith('(') && body.EndsWith(')')
            ? Split(body[1..^1])
            : [body];

        if (components is null || components.Length == 0)
        {
            return null;
        }

        var prefix = parameter + ".";
        var names = new string[components.Length];
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i].Trim();
            if (!component.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            var memberName = component[prefix.Length..];
            if (!Identifier(memberName))
            {
                return null;
            }

            names[i] = memberName;
        }

        return Resolve<T>(names);
    }

    /// <summary>
    /// A tuple body's components, or null when it nests anything — a call, an index or a tuple of
    /// tuples is not a key of direct members, whatever else it may be.
    /// </summary>
    private static string[]? Split(string body)
    {
        var components = new List<string>();
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case ',':
                    components.Add(body[start..i]);
                    start = i + 1;
                    break;
                case '(' or ')' or '[' or ']' or '"' or '\'':
                    return null;
                default:
                    break;
            }
        }

        components.Add(body[start..]);
        return [.. components];
    }

    private static MemberInfo[]? Resolve<T>(IReadOnlyList<string> names)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

        var members = new MemberInfo[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            MemberInfo? member = typeof(T).GetProperty(names[i], flags)
                ?? (MemberInfo?)typeof(T).GetField(names[i], flags);

            if (member is null)
            {
                return null;
            }

            members[i] = member;
        }

        return members;
    }

    private static bool Identifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!(char.IsLetterOrDigit(value[i]) || value[i] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the key's CLR type really is the members' — one member's type, or the tuple of them,
    /// in order.
    /// </summary>
    /// <remarks>
    /// Nullable keys need explicit SQL NULL semantics and stay excluded. The tuple check also rejects
    /// Akade's multi-key overloads, whose accessor returns <c>IEnumerable&lt;TKey&gt;</c>.
    /// </remarks>
    private static bool Shaped(Type keyType, MemberInfo[] members)
    {
        foreach (var member in members)
        {
            if (Nullable.GetUnderlyingType(MemberType(member)) is not null || IsNullableReference(member))
            {
                return false;
            }
        }

        if (members.Length == 1)
        {
            return MemberType(members[0]) == keyType;
        }

        var components = AkadeTupleKey.ComponentTypes(keyType);
        if (components is null || components.Length != members.Length)
        {
            return false;
        }

        for (var i = 0; i < members.Length; i++)
        {
            if (components[i] != MemberType(members[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What order this index is in, or <see cref="AkadeOrder.None"/> when it is not an access path
    /// Chalk will claim (D281).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A HASH index answers equality and claims no order, so it needs only exact equality on every
    /// component, and a comparer declared for it says nothing Chalk reads.
    /// </para>
    /// <para>
    /// An ORDERED index is a claim that its keys arrive in Chalk's order. Without a declaration that
    /// holds for the types whose CLR order already is Chalk's and for no others. With one, the
    /// declared comparer decides — and one Chalk does not recognise is refused by name rather than
    /// left silently undisclosed, because the host asked for this index to be an access path.
    /// </para>
    /// </remarks>
    private static AkadeOrder Admit<T>(
        AkadePhysicalIndexKind kind,
        Type keyType,
        MemberInfo[] members,
        AkadeComparerBinding<T>? declared,
        string indexName)
    {
        if (kind == AkadePhysicalIndexKind.Prefix)
        {
            // Akade's trie is keyed by text and answers one column; it claims no order at all, so
            // there is no comparer to classify and nothing wider to admit (D282).
            return members.Length == 1 && MemberType(members[0]) == typeof(string)
                ? AkadeOrder.Ascending
                : AkadeOrder.None;
        }

        if (kind is AkadePhysicalIndexKind.Hash or AkadePhysicalIndexKind.UniqueHash)
        {
            foreach (var member in members)
            {
                if (!AkadeKeyOrder.IsHashKeyType(MemberType(member)))
                {
                    return AkadeOrder.None;
                }
            }

            return AkadeOrder.Ascending;
        }

        if (declared is null)
        {
            foreach (var member in members)
            {
                if (!AkadeKeyOrder.IsOrderedKeyType(MemberType(member)))
                {
                    return AkadeOrder.None;
                }
            }

            return AkadeOrder.Ascending;
        }

        var order = AkadeKeyOrder.Classify(declared.Comparer, keyType);
        if (order == AkadeOrder.None)
        {
            throw new InvalidOperationException(
                $"the comparer declared for the Akade index '{indexName}' is a "
                + $"'{declared.Comparer.GetType().Name}', which is not an order Chalk recognises. An "
                + "ORDERED index is a claim that its keys arrive in Chalk's order, and Chalk "
                + "classifies the comparer rather than trusting it. Accepted here: "
                + $"{AkadeKeyOrder.Accepted(keyType)}.");
        }

        foreach (var member in members)
        {
            var type = MemberType(member);
            if (!AkadeKeyOrder.IsOrderedKeyType(type) && !AkadeKeyOrder.NeedsDeclaredComparer(type))
            {
                return AkadeOrder.None;
            }
        }

        return order;
    }

    /// <summary>
    /// The comparer the host declared for this index, if any. Matched by the text Akade files the
    /// index under — which is the text the compiler recorded at the declaration — or by the
    /// accessor's method identity, which is what a static key method has instead of a distinctive
    /// text.
    /// </summary>
    private static AkadeComparerBinding<T>? Declaration<T>(
        string indexName,
        Delegate accessor,
        Type keyType,
        AkadeSourceOptions<T> options)
    {
        foreach (var binding in options.Comparers)
        {
            var matches = string.Equals(binding.Accessor, indexName, StringComparison.Ordinal)
                || binding.Method == accessor.Method;

            if (!matches)
            {
                continue;
            }

            if (binding.KeyType != keyType)
            {
                throw new InvalidOperationException(
                    $"the comparer declared for the Akade index '{indexName}' orders "
                    + $"'{binding.KeyType.Name}', but that index's key is a '{keyType.Name}'. Declare "
                    + "the comparer against the accessor the index was registered with.");
            }

            return binding;
        }

        return null;
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(void),
    };

    private static bool IsNullableReference(MemberInfo member)
    {
        var type = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => null,
        };

        if (type is null || type.IsValueType)
        {
            return false;
        }

        var context = new NullabilityInfoContext();
        var state = member switch
        {
            PropertyInfo property => context.Create(property).ReadState,
            FieldInfo field => context.Create(field).ReadState,
            _ => NullabilityState.Unknown,
        };

        // Oblivious metadata is not enough to make a planner-visible NOT NULL index promise.
        return state != NullabilityState.NotNull;
    }

    private static AkadeIndexShape[] Shapes<T>(
        IReadOnlyList<AkadeDiscoveredIndex<T>> discovered)
    {
        var shapes = new AkadeIndexShape[discovered.Count];
        for (var i = 0; i < shapes.Length; i++)
        {
            var index = discovered[i];
            shapes[i] = new AkadeIndexShape(
                index.Name,
                index.Kind,
                index.KeyType,
                index.Accessor.Method,
                string.Join("+", index.Members.Select(m => m.Name)),
                index.Order);
        }

        return shapes;
    }

    private static IDictionary ReadDictionary<T>(IndexedSet<T> set, string fieldName)
    {
        var field = Fields(set.GetType())
            .FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal));

        if (field?.GetValue(set) is IDictionary dictionary)
        {
            return dictionary;
        }

        // Deliberately fail closed rather than guessing another dictionary-shaped field.
        throw new NotSupportedException(
            $"Akade {set.GetType().Assembly.GetName().Version}: expected private field '{fieldName}' "
            + $"was not found on {set.GetType().FullName}. Update Chalk's Akade reflection adapter.");
    }

    private static Delegate? FindKeyAccessor<T>(object writer)
    {
        var fields = Fields(writer.GetType()).ToArray();

        // Current Akade writer implementations retain the original selector as _keyAccessor.
        var named = fields.FirstOrDefault(
            f => string.Equals(f.Name, "_keyAccessor", StringComparison.Ordinal));

        if (named?.GetValue(writer) is Delegate exact && IsRowAccessor<T>(exact))
        {
            return exact;
        }

        // Compatibility fallback: accept exactly one plausible T -> key delegate, never the first of
        // several. Ambiguity means "do not advertise", not "guess".
        var candidates = fields
            .Select(f => f.GetValue(writer))
            .OfType<Delegate>()
            .Where(IsRowAccessor<T>)
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsRowAccessor<T>(Delegate value)
    {
        var invoke = value.GetType().GetMethod("Invoke");
        var parameters = invoke?.GetParameters();
        return parameters is { Length: 1 } && parameters[0].ParameterType == typeof(T);
    }

    private static AkadePhysicalIndexKind? Classify(Type type)
    {
        var name = type.Name;

        if (name.StartsWith("UniqueIndex", StringComparison.Ordinal))
        {
            return AkadePhysicalIndexKind.UniqueHash;
        }

        if (name.StartsWith("NonUniqueIndex", StringComparison.Ordinal))
        {
            return AkadePhysicalIndexKind.Hash;
        }

        if (name.StartsWith("RangeIndex", StringComparison.Ordinal))
        {
            return AkadePhysicalIndexKind.Range;
        }

        // The trie (D282). It answers StartsWith and nothing else, which is exactly what Chalk's
        // PREFIX kind promises; FuzzyStartsWith is not an access path, because no range says it.
        if (name.StartsWith("PrefixIndex", StringComparison.Ordinal))
        {
            return AkadePhysicalIndexKind.Prefix;
        }

        return null;
    }

    private static IEnumerable<FieldInfo> Fields(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(
                         BindingFlags.Instance |
                         BindingFlags.NonPublic |
                         BindingFlags.Public |
                         BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }
}

internal enum AkadePhysicalIndexKind
{
    Hash,
    UniqueHash,
    Range,
    Prefix,
}

internal sealed record AkadeDiscoveredIndex<T>(
    string Name,
    AkadePhysicalIndexKind Kind,
    Type KeyType,
    Delegate Accessor,
    MemberInfo[] Members,
    AkadeOrder Order,
    object? Comparer);

internal sealed record AkadeIndexShape(
    string Name,
    AkadePhysicalIndexKind Kind,
    Type KeyType,
    MethodInfo AccessorMethod,
    string Member,
    AkadeOrder Order);

internal sealed class AkadeIndexTopologyException(string message) : Exception(message);

/// <summary>
/// Immutable planner-visible Akade index topology captured for one published table shape. Indexes
/// Chalk cannot currently describe are deliberately absent from this fingerprint.
/// </summary>
internal sealed class AkadeIndexPlan<T, TSet>
    where TSet : class
{
    private readonly IAkadeIndexRegistration<T>[] _registrations;
    private readonly AkadeIndexShape[] _expected;
    private readonly Func<TSet, AkadeIndexShape[]> _inspect;

    public AkadeIndexPlan(
        IEnumerable<IAkadeIndexRegistration<T>> registrations,
        AkadeIndexShape[] expected,
        Func<TSet, AkadeIndexShape[]> inspect)
    {
        _registrations = [.. registrations];
        _expected = expected;
        _inspect = inspect;
    }

    public void Register(PocoTableBuilder<T> table, IndexedSet<T> set)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(set);

        foreach (var registration in _registrations)
        {
            registration.Register(table, set);
        }
    }

    public void Validate(TSet set)
    {
        var actual = _inspect(set);

        if (actual.Length != _expected.Length)
        {
            throw new AkadeIndexTopologyException(
                $"the planner-visible Akade index topology changed: expected {_expected.Length} index(es) "
                + $"[{Describe(_expected)}], found {actual.Length} [{Describe(actual)}].");
        }

        for (var i = 0; i < _expected.Length; i++)
        {
            var expected = _expected[i];
            var found = actual[i];

            if (!string.Equals(expected.Name, found.Name, StringComparison.Ordinal)
                || expected.Kind != found.Kind
                || expected.KeyType != found.KeyType
                || expected.AccessorMethod != found.AccessorMethod
                || expected.Order != found.Order
                || !string.Equals(expected.Member, found.Member, StringComparison.Ordinal))
            {
                throw new AkadeIndexTopologyException(
                    $"the planner-visible Akade index topology changed at '{expected.Name}': expected "
                    + $"{Describe(expected)}, found {Describe(found)}.");
            }
        }
    }

    private static string Describe(IEnumerable<AkadeIndexShape> shapes) =>
        string.Join(", ", shapes.Select(Describe));

    private static string Describe(AkadeIndexShape shape) =>
        $"{shape.Name}:{shape.Kind}<{shape.KeyType.Name}>/{shape.Member}/{shape.Order}";
}

internal interface IAkadeIndexRegistration<T>
{
    void Register(PocoTableBuilder<T> table, IndexedSet<T> set);
}

/// <summary>
/// One Akade index registered through PocoTableBuilder's late-bound host-index API: a direct scalar
/// key, or a compound key of two to four direct members (D280). The builder resolves the member
/// selectors to their final column ordinals; the factory closes over the exact IndexedSet captured
/// for the enclosing Akade snapshot.
/// </summary>
internal sealed class AkadeIndexRegistration<T, TKey> : IAkadeIndexRegistration<T>
    where TKey : notnull
{
    private readonly string _name;
    private readonly AkadePhysicalIndexKind _kind;
    private readonly Func<T, TKey> _key;
    private readonly MemberInfo[] _members;
    private readonly AkadeOrder _order;
    private readonly IComparer<TKey>? _declared;
    private readonly string _sourceId;
    private readonly string _table;

    public AkadeIndexRegistration(
        string name,
        AkadePhysicalIndexKind kind,
        Delegate key,
        MemberInfo[] members,
        AkadeOrder order,
        object? declared,
        string sourceId,
        string table)
    {
        _name = name;
        _kind = kind;
        _key = (Func<T, TKey>)key;
        _members = members;
        _order = order;
        _declared = declared as IComparer<TKey>;
        _sourceId = sourceId;
        _table = table;
    }

    public void Register(PocoTableBuilder<T> table, IndexedSet<T> set)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(set);

        var kind = _kind switch
        {
            AkadePhysicalIndexKind.Range => IndexKind.Ordered,
            AkadePhysicalIndexKind.Prefix => IndexKind.Prefix,
            _ => IndexKind.Hash,
        };

        var unique = _kind == AkadePhysicalIndexKind.UniqueHash;
        var descending = _order == AkadeOrder.Descending;

        // The exact reverse of the ascending default: NULLS LAST reversed is NULLS FIRST, which is
        // also where `ORDER BY x DESC` puts them, so the index serves that ordering without a sort.
        var direction = descending
            ? SortDirection.DescNullsFirst
            : SortDirection.AscNullsLast;

        IReadOnlyList<SortDirection> directions = kind == IndexKind.Ordered
            ? [.. _members.Select(_ => direction)]
            : Array.Empty<SortDirection>();

        var selectors = Array.ConvertAll(_members, MemberSelector);

        if (kind == IndexKind.Prefix)
        {
            table.Index(
                _name,
                kind,
                unique: false,
                directions,
                (descriptor, _) => new AkadePrefixIndex<T>(
                    descriptor,
                    set,
                    (Func<T, string>)(object)_key,
                    _name,
                    _sourceId,
                    _table),
                selectors);
            return;
        }

        if (_members.Length == 1)
        {
            // The declared comparer where there is one, and the type's Chalk order otherwise. The
            // guard checks against exactly the order the descriptor claims.
            var comparer = _declared
                ?? (descending ? AkadeKeyOrder.Descending<TKey>() : AkadeKeyOrder.Ascending<TKey>());

            table.Index(
                _name,
                kind,
                unique,
                directions,
                (descriptor, _) => new AkadeScalarIndex<T, TKey>(
                    descriptor,
                    set,
                    _key,
                    _name,
                    _sourceId,
                    _table,
                    comparer),
                selectors);
            return;
        }

        // Compiled once, here, and shared by every snapshot's index: the compiled fill and the
        // composed comparer depend on the key type and the direction alone.
        var shape = AkadeTupleKey<TKey>.Create(
            [.. _members.Select(m => Order(m, descending))],
            descending);

        table.Index(
            _name,
            kind,
            unique,
            directions,
            (descriptor, _) => new AkadeTupleIndex<T, TKey>(
                descriptor,
                set,
                _key,
                shape,
                _name,
                _sourceId,
                _table),
            selectors);
    }

    /// <summary>The Chalk order of one key component, which discovery has already admitted.</summary>
    private static object Order(MemberInfo member, bool descending)
    {
        var type = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException(
                $"Akade index member '{member.Name}' is neither a property nor a field."),
        };

        // A component Chalk has no order for — GUID — is admitted for a HASH key, which never asks
        // for one; the CLR's own is what the composed comparer then carries, and nothing reads it.
        return (descending ? AkadeKeyOrder.Descending(type) : AkadeKeyOrder.Ascending(type))
            ?? typeof(Comparer<>).MakeGenericType(type).GetProperty("Default")!.GetValue(null)!;
    }

    private static Expression<Func<T, object?>> MemberSelector(MemberInfo member)
    {
        var row = Expression.Parameter(typeof(T), "x");
        Expression value = member switch
        {
            PropertyInfo property => Expression.Property(row, property),
            FieldInfo field => Expression.Field(row, field),
            _ => throw new NotSupportedException(
                $"Akade index member '{member.Name}' is neither a property nor a field."),
        };

        return Expression.Lambda<Func<T, object?>>(
            Expression.Convert(value, typeof(object)),
            row);
    }
}
