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
/// returned by discovery. In particular, computed, compound, multi-key and specialised Akade indexes
/// remain physical implementation details until Chalk's catalog can represent their key semantics.
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
            candidate => Shapes(
                Inspect(
                    ConcurrentIndexedSetAccess.CaptureForQuiescentRead(candidate),
                    options)));
    }

    private static AkadeIndexPlan<T, TSet> CreatePlan<T, TSet>(
        IReadOnlyList<AkadeDiscoveredIndex<T>> discovered,
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
                index.Member);

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

            var member = DirectMember<T>(name, keyType);
            if (member is null || !Supports(kind.Value, keyType, member))
            {
                continue;
            }

            result.Add(new AkadeDiscoveredIndex<T>(
                name,
                kind.Value,
                keyType,
                accessor,
                member));
        }

        result.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return result;
    }

    private static MemberInfo? DirectMember<T>(string indexName, Type keyType)
    {
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

        var prefix = parameter + ".";
        if (!body.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var memberName = body[prefix.Length..];
        if (!Identifier(memberName))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        MemberInfo? member = typeof(T).GetProperty(memberName, flags)
            ?? (MemberInfo?)typeof(T).GetField(memberName, flags);

        var memberType = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => null,
        };

        // This also rejects Akade's multi-key overloads: their physical lookup key is one element of
        // the returned sequence, whereas the accessor itself returns IEnumerable<TKey>.
        return memberType == keyType ? member : null;
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

    private static bool Supports(
        AkadePhysicalIndexKind kind,
        Type keyType,
        MemberInfo member)
    {
        // Nullable keys need explicit SQL NULL semantics; float/double need Chalk's NaN ordering;
        // ordered strings need an explicit comparer contract. Do not advertise those yet.
        if (Nullable.GetUnderlyingType(keyType) is not null || IsNullableReference(member))
        {
            return false;
        }

        var exactEquality = keyType == typeof(bool)
            || keyType == typeof(byte)
            || keyType == typeof(sbyte)
            || keyType == typeof(short)
            || keyType == typeof(ushort)
            || keyType == typeof(int)
            || keyType == typeof(uint)
            || keyType == typeof(long)
            || keyType == typeof(ulong)
            || keyType == typeof(decimal)
            || keyType == typeof(Guid)
            || keyType == typeof(string);

        if (kind is AkadePhysicalIndexKind.Hash or AkadePhysicalIndexKind.UniqueHash)
        {
            return exactEquality;
        }

        return keyType == typeof(byte)
            || keyType == typeof(sbyte)
            || keyType == typeof(short)
            || keyType == typeof(ushort)
            || keyType == typeof(int)
            || keyType == typeof(uint)
            || keyType == typeof(long)
            || keyType == typeof(ulong)
            || keyType == typeof(decimal);
    }

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
                index.Member.Name);
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
}

internal sealed record AkadeDiscoveredIndex<T>(
    string Name,
    AkadePhysicalIndexKind Kind,
    Type KeyType,
    Delegate Accessor,
    MemberInfo Member);

internal sealed record AkadeIndexShape(
    string Name,
    AkadePhysicalIndexKind Kind,
    Type KeyType,
    MethodInfo AccessorMethod,
    string Member);

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
        $"{shape.Name}:{shape.Kind}<{shape.KeyType.Name}>/{shape.Member}";
}

internal interface IAkadeIndexRegistration<T>
{
    void Register(PocoTableBuilder<T> table, IndexedSet<T> set);
}

/// <summary>
/// One direct scalar Akade index registered through PocoTableBuilder's late-bound host-index API.
/// The builder resolves the member selector to its final column ordinal; the factory closes over the
/// exact IndexedSet captured for the enclosing Akade snapshot.
/// </summary>
internal sealed class AkadeIndexRegistration<T, TKey> : IAkadeIndexRegistration<T>
    where TKey : notnull
{
    private readonly string _name;
    private readonly AkadePhysicalIndexKind _kind;
    private readonly Func<T, TKey> _key;
    private readonly MemberInfo _member;

    public AkadeIndexRegistration(
        string name,
        AkadePhysicalIndexKind kind,
        Delegate key,
        MemberInfo member)
    {
        _name = name;
        _kind = kind;
        _key = (Func<T, TKey>)key;
        _member = member;
    }

    public void Register(PocoTableBuilder<T> table, IndexedSet<T> set)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(set);

        var kind = _kind == AkadePhysicalIndexKind.Range
            ? IndexKind.Ordered
            : IndexKind.Hash;

        var unique = _kind == AkadePhysicalIndexKind.UniqueHash;
        IReadOnlyList<SortDirection> directions = kind == IndexKind.Ordered
            ? [SortDirection.AscNullsLast]
            : Array.Empty<SortDirection>();

        var member = MemberSelector(_member);

        table.Index(
            _name,
            kind,
            unique,
            directions,
            (descriptor, _) => new AkadeScalarIndex<T, TKey>(
                descriptor,
                set,
                _key,
                _name),
            member);
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
