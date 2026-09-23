using System.Linq.Expressions;
using Akade.IndexedSet;
using Akade.IndexedSet.Concurrency;
using Chalk.Catalog;
using Chalk.Sources.Poco;

namespace Chalk.Sources.Akade;

internal sealed class AkadeSourceOptions<T>
{
    public SourceSharing Sharing { get; init; } = SourceSharing.Shared;
    public required string SourceId { get; init; }
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required PocoNamingPolicy NamingPolicy { get; init; }
    public required int DecimalScale { get; init; }
    public required FunctionDescriptor[] Functions { get; init; }
    public required AkadeFunctionIndexBinding<T>[] FunctionIndexes { get; init; }
    public required AkadeCompoundIndexBinding<T>[] CompoundIndexes { get; init; }
    public required AkadeComparerBinding<T>[] Comparers { get; init; }
    public Action<PocoTableBuilder<T>>? ConfigureTable { get; init; }
    public CostProfile CostProfile { get; init; } = CostProfile.Inherit;
    public bool TrustSourceRowSecurity { get; init; }
}

/// <summary>
/// Common configuration for the two physical Akade set flavours. TSelf preserves fluent return types.
/// </summary>
public abstract class AkadeSourceBuilder<T, TSelf>
    where TSelf : AkadeSourceBuilder<T, TSelf>
{
    private readonly List<FunctionDescriptor> _functions = [];
    private readonly List<AkadeFunctionIndexBinding<T>> _functionIndexes = [];
    private readonly List<AkadeCompoundIndexBinding<T>> _compoundIndexes = [];
    private readonly List<AkadeComparerBinding<T>> _comparers = [];

    private string _schemaName = "main";
    private string _tableName;
    private PocoNamingPolicy _namingPolicy = PocoNamingPolicy.AsIs;
    private int _decimalScale = 10;
    private Action<PocoTableBuilder<T>>? _configureTable;
    private CostProfile _costProfile = Catalog.CostProfile.Inherit;
    private bool _trustSourceRowSecurity;

    protected AkadeSourceBuilder(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        SourceId = sourceId;
        // One set is one table, so the table takes the source's name unless the host says otherwise.
        _tableName = sourceId;
    }

    protected string SourceId { get; }

    protected TSelf Self => (TSelf)this;

    public TSelf SchemaName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _schemaName = name;
        return Self;
    }

    /// <summary>
    /// The table's name. By default it is the source's own name, which is the common case for a
    /// source that holds one set; call this only when the two should differ.
    /// </summary>
    public TSelf TableName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _tableName = name;
        return Self;
    }

    public TSelf NamingPolicy(PocoNamingPolicy policy)
    {
        _namingPolicy = policy;
        return Self;
    }

    public TSelf DefaultDecimalScale(int scale)
    {
        if (scale is < 0 or > 38)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "DECIMAL scale must be 0..38.");
        }

        _decimalScale = scale;
        return Self;
    }

    /// <summary>
    /// Reuses the existing POCO table configuration surface for columns, entitlements, declared
    /// unique keys and other row-shape metadata. Akade indexes themselves are discovered separately.
    /// </summary>
    public TSelf ConfigureTable(Action<PocoTableBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureTable += configure;
        return Self;
    }

    public TSelf CostProfile(CostProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _costProfile = profile;
        return Self;
    }

    public TSelf TrustSourceRowSecurity(bool trust = true)
    {
        _trustSourceRowSecurity = trust;
        return Self;
    }

    public TSelf AddFunction(string name, Action<FunctionBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new FunctionBuilder(name);
        configure(builder);
        _functions.Add(builder.Build());
        return Self;
    }

    /// <summary>
    /// Binds an Akade static/computed key accessor to a catalog scalar function. Method identity is
    /// retained only in the .NET source runtime; the FunctionDescriptor remains planner-facing data.
    /// Arguments are restricted here to member selectors deliberately. The planner-facing index-key
    /// expression can later use the normal Chalk scalar IR.
    /// </summary>
    public TSelf BindFunctionIndex<TKey>(
        Func<T, TKey> key,
        string function,
        params Expression<Func<T, object?>>[] arguments)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        ArgumentNullException.ThrowIfNull(arguments);

        _functionIndexes.Add(new AkadeFunctionIndexBinding<T>(
            key.Method,
            function,
            arguments.Select(AkadeMemberSelector.MemberName).ToArray()));

        return Self;
    }

    /// <summary>
    /// Names the row members a compound Akade key is made of, in tuple order (D280). Akade files an
    /// index under the source text of its accessor, so a lambda tuple key — <c>x =&gt; (x.A, x.B)</c>
    /// — names its own columns and is discovered without this; a method accessor
    /// (<c>PurchaseKeys.ProductAndUnitPrice</c>) names none, so the host states them here.
    /// </summary>
    /// <param name="key">
    /// The accessor the Akade index was registered with, by method identity — the same method group,
    /// not an equivalent lambda.
    /// </param>
    /// <param name="components">
    /// One direct row-member selector per tuple component, in tuple order.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The accessor does not return a <c>ValueTuple</c> of exactly these components' types, in this
    /// order. The index would then describe a key the rows do not have, which is a claim the planner
    /// would act on.
    /// </exception>
    public TSelf CompoundIndex<TKey>(
        Func<T, TKey> key,
        params Expression<Func<T, object?>>[] components)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(components);

        var members = components.Select(AkadeMemberSelector.Member).ToArray();
        var componentTypes = AkadeTupleKey.ComponentTypes(typeof(TKey));

        if (componentTypes is null)
        {
            throw new ArgumentException(
                $"The Akade compound index '{Describe(key)}' returns "
                + $"'{typeof(TKey).FullName ?? typeof(TKey).Name}', which is not a ValueTuple of two "
                + "to four components. A compound key is a tuple of the row members it is made of.",
                nameof(key));
        }

        if (componentTypes.Length != members.Length)
        {
            throw new ArgumentException(
                $"The Akade compound index '{Describe(key)}' returns a tuple of "
                + $"{componentTypes.Length} component(s) but {members.Length} member(s) were given "
                + $"({string.Join(", ", members.Select(m => m.Name))}). Give one member per "
                + "component, in tuple order.",
                nameof(components));
        }

        for (var i = 0; i < members.Length; i++)
        {
            var memberType = AkadeMemberSelector.MemberType(members[i]);
            if (memberType != componentTypes[i])
            {
                throw new ArgumentException(
                    $"The Akade compound index '{Describe(key)}' has "
                    + $"'{componentTypes[i].Name}' at component {i + 1}, but the member given there, "
                    + $"'{members[i].Name}', is a '{memberType.Name}'. A compound key's components "
                    + "must be the members' own types, in tuple order.",
                    nameof(components));
            }
        }

        _compoundIndexes.Add(new AkadeCompoundIndexBinding<T>(
            key.Method,
            [.. members.Select(m => m.Name)]));

        return Self;
    }

    /// <summary>
    /// States the comparer an Akade range index was built with (D281). Chalk classifies it rather
    /// than trusting it: an ORDERED index is a claim that its keys arrive in Chalk's order, and only
    /// a comparer Chalk recognises makes that claim true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without a declaration, a key type whose CLR order already is Chalk's — the integer, decimal
    /// and temporal types — is an ordered access path on the strength of the type alone, and a
    /// string or a real is not an access path at all.
    /// </para>
    /// <para>
    /// A descending comparer makes a descending index, which the planner serves
    /// <c>ORDER BY … DESC</c> from without a sort.
    /// </para>
    /// </remarks>
    /// <param name="key">
    /// The accessor the Akade index was registered with. Matched by the text Akade files the index
    /// under — the same text the compiler records here — or by the accessor's method identity, which
    /// is what a static key method has instead.
    /// </param>
    /// <param name="comparer">
    /// <c>Comparer&lt;TKey&gt;.Default</c> where that is Chalk's order, one of
    /// <see cref="ChalkComparers"/>'s, or <c>StringComparer.Ordinal</c>.
    /// </param>
    public TSelf Comparer<TKey>(
        Func<T, TKey> key,
        IComparer<TKey> comparer,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(key))] string? accessor = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(comparer);

        _comparers.Add(new AkadeComparerBinding<T>(
            key.Method,
            accessor,
            comparer,
            typeof(TKey)));

        return Self;
    }

    private static string Describe<TKey>(Func<T, TKey> key) =>
        key.Method.DeclaringType is { } declaring
            ? declaring.Name + "." + key.Method.Name
            : key.Method.Name;

    internal AkadeSourceOptions<T> Freeze() => new()
    {
        SourceId = SourceId,
        SchemaName = _schemaName,
        TableName = _tableName,
        NamingPolicy = _namingPolicy,
        DecimalScale = _decimalScale,
        Functions = [.. _functions],
        FunctionIndexes = [.. _functionIndexes],
        CompoundIndexes = [.. _compoundIndexes],
        Comparers = [.. _comparers],
        ConfigureTable = _configureTable,
        CostProfile = _costProfile,
        TrustSourceRowSecurity = _trustSourceRowSecurity,
    };
}

public sealed class IndexedSetSourceBuilder<T>
    : AkadeSourceBuilder<T, IndexedSetSourceBuilder<T>>
{
    private readonly Func<IndexedSet<T>> _registration;
    private IndexedSetEditor<T>? _editor;

    internal IndexedSetSourceBuilder(string sourceId, Func<IndexedSet<T>> registration)
        : base(sourceId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _registration = registration;
    }

    /// <summary>
    /// Enables transactional RefreshBuilder.Replace/Append by supplying the operation that constructs
    /// a fresh set containing exactly the target rows. This is optional: without an editor the source
    /// still supports direct host mutation plus scoped Refresh(target), but not row-bearing
    /// Replace/Append.
    /// </summary>
    /// <remarks>
    /// The editor is never given the currently published set. Returning that set is refused. This is
    /// what preserves old-reader/new-reader snapshot semantics for RefreshBuilder row operations.
    /// </remarks>
    public IndexedSetSourceBuilder<T> Editor(IndexedSetEditor<T> editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        return this;
    }

    public IndexedSetSourceBuilder<T> RebuildWith(
        Func<IReadOnlyList<T>, IndexedSet<T>> build) =>
        Editor(new IndexedSetEditor<T>(build));

    public IndexedSetSource<T> Build()
    {
        var options = Freeze();
        var initial = _registration()
            ?? throw new InvalidOperationException($"Akade source '{options.SourceId}' returned null.");

        var plan = IndexedSetBackend<T>.Discover(initial, options);
        var source = new IndexedSetSource<T>(
            options,
            _registration,
            _editor,
            plan,
            initial);

        CatalogValidator.Validate(new CatalogContext
        {
            ContextId = options.SourceId,
            Epoch = 0,
            Schemas = [source.DescribeSchema()],
        });

        return source;
    }
}

/// <summary>Builder for a Chalk source over Akade's ConcurrentIndexedSet.</summary>
/// <remarks>
/// Instances are obtained through a CHALK002-marked AkadeSource factory overload. Chalk captures the
/// wrapped IndexedSet and reads it outside Akade's reader lock; the host must enforce read quiescence.
/// </remarks>
public sealed class ConcurrentIndexedSetSourceBuilder<T>
    : AkadeSourceBuilder<T, ConcurrentIndexedSetSourceBuilder<T>>
{
    private readonly Func<ConcurrentIndexedSet<T>> _registration;
    private ConcurrentIndexedSetEditor<T>? _editor;

    internal ConcurrentIndexedSetSourceBuilder(
        string sourceId,
        Func<ConcurrentIndexedSet<T>> registration)
        : base(sourceId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _registration = registration;
    }

    /// <summary>
    /// Enables transactional RefreshBuilder.Replace/Append by constructing a fresh concurrent set
    /// containing exactly the target rows. Direct host mutation and scoped Refresh(target) do not
    /// require an editor.
    /// </summary>
    public ConcurrentIndexedSetSourceBuilder<T> Editor(ConcurrentIndexedSetEditor<T> editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        return this;
    }

    public ConcurrentIndexedSetSourceBuilder<T> RebuildWith(
        Func<IReadOnlyList<T>, ConcurrentIndexedSet<T>> build) =>
        Editor(new ConcurrentIndexedSetEditor<T>(build));

    public ConcurrentIndexedSetSource<T> Build()
    {
        var options = Freeze();
        var initial = _registration()
            ?? throw new InvalidOperationException($"Akade source '{options.SourceId}' returned null.");

        var plan = ConcurrentIndexedSetBackend<T>.Discover(initial, options);
        var source = new ConcurrentIndexedSetSource<T>(
            options,
            _registration,
            _editor,
            plan,
            initial);

        CatalogValidator.Validate(new CatalogContext
        {
            ContextId = options.SourceId,
            Epoch = 0,
            Schemas = [source.DescribeSchema()],
        });

        return source;
    }
}

internal static class AkadeMemberSelector
{
    public static string MemberName<T>(Expression<Func<T, object?>> selector) =>
        Member(selector).Name;

    public static System.Reflection.MemberInfo Member<T>(Expression<Func<T, object?>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        Expression body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member || member.Expression is not ParameterExpression)
        {
            throw new ArgumentException(
                "An Akade index member must be a direct row member selector, e.g. x => x.Start.",
                nameof(selector));
        }

        return member.Member;
    }

    public static Type MemberType(System.Reflection.MemberInfo member) => member switch
    {
        System.Reflection.PropertyInfo property => property.PropertyType,
        System.Reflection.FieldInfo field => field.FieldType,
        _ => throw new ArgumentException(
            $"An Akade index member must be a property or a field; '{member.Name}' is neither.",
            nameof(member)),
    };
}

internal sealed record AkadeFunctionIndexBinding<T>(
    System.Reflection.MethodInfo Method,
    string Function,
    string[] Members);

/// <summary>
/// The row members a compound Akade key is made of, in tuple order, bound to the accessor's method
/// identity — the same way a function index is bound (D280).
/// </summary>
internal sealed record AkadeCompoundIndexBinding<T>(
    System.Reflection.MethodInfo Method,
    string[] Members);

/// <summary>
/// The comparer a host says an Akade index was built with (D281), bound to the accessor two ways:
/// by the text the compiler recorded for it, which is the text Akade files the index under, and by
/// its method identity, which is what a static key method has instead. Two lambdas spelled the same
/// way at two places are two methods, so the text is what matches them.
/// </summary>
internal sealed record AkadeComparerBinding<T>(
    System.Reflection.MethodInfo Method,
    string? Accessor,
    object Comparer,
    Type KeyType);
