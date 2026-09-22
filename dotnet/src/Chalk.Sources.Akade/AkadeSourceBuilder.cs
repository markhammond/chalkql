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

    private string _schemaName = "main";
    private string _tableName = "data";
    private PocoNamingPolicy _namingPolicy = PocoNamingPolicy.AsIs;
    private int _decimalScale = 10;
    private Action<PocoTableBuilder<T>>? _configureTable;
    private CostProfile _costProfile = Catalog.CostProfile.Inherit;
    private bool _trustSourceRowSecurity;

    protected AkadeSourceBuilder(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        SourceId = sourceId;
    }

    protected string SourceId { get; }

    protected TSelf Self => (TSelf)this;

    public TSelf SchemaName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _schemaName = name;
        return Self;
    }

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

    internal AkadeSourceOptions<T> Freeze() => new()
    {
        SourceId = SourceId,
        SchemaName = _schemaName,
        TableName = _tableName,
        NamingPolicy = _namingPolicy,
        DecimalScale = _decimalScale,
        Functions = [.. _functions],
        FunctionIndexes = [.. _functionIndexes],
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
    public static string MemberName<T>(Expression<Func<T, object?>> selector)
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
                "An Akade function-index argument must be a direct row member selector, e.g. x => x.Start.",
                nameof(selector));
        }

        return member.Member.Name;
    }
}

internal sealed record AkadeFunctionIndexBinding<T>(
    System.Reflection.MethodInfo Method,
    string Function,
    string[] Members);
