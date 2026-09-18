using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chalk.Catalog;
using Chalk.Client.Rpc;

namespace Chalk.Client;

/// <summary>
/// The named values a statement is planned or executed with
/// (<c>docs/design/16-entitlements.md</c> §2, D152).
/// </summary>
/// <remarks>
/// <para>
/// Three kinds of thing, and a policy's SQL refers to them as <c>@ctx.&lt;name&gt;</c>: a
/// <see cref="Scalars">scalar</see>, a typed value; a <see cref="Lists">list</see>, a relation of one
/// or more columns used only inside an <c>IN</c> list and NOT NULL in every column; and a
/// <see cref="Relations">relation</see>, a table in a <c>FROM</c>. The kind is which dictionary the
/// binding is in, and it is declared rather than guessed: the same rows read as a membership set and
/// as a table are different things to the planner, and a mistake can then be named.
/// </para>
/// <para>
/// There is no imperative hook. A predicate the planner cannot see cannot be pushed, costed or
/// audited, so a host that needs code runs it before the statement and binds the result.
/// </para>
/// <para>
/// <see cref="Purpose"/> and <see cref="Actor"/> are audit metadata rather than context (D205): they
/// are carried into the audit event, which is otherwise value-free, and they never reach a
/// predicate.
/// </para>
/// </remarks>
public sealed class RequestContext
{
    /// <summary>Nothing bound, which is what every statement over an unentitled catalog uses.</summary>
    public static RequestContext Empty { get; } = new();

    /// <summary>The shipped fold ceiling: a list of at most this many rows becomes literals.</summary>
    public const int DefaultFoldMaxRows = 64;

    /// <summary>Typed values, by name.</summary>
    public IReadOnlyDictionary<string, object?> Scalars { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// The type of a scalar the planner cannot read one off, by name (D209, D232). Needed by a
    /// <see cref="ShapeOnly"/> context, which has no values to infer from; elsewhere it states a type
    /// the inference would get wrong.
    /// </summary>
    /// <remarks>
    /// A name stated here and <em>absent</em> from <see cref="Scalars"/> is a <b>shape</b>: the
    /// planner types a parameter in the value's place and execution binds it (D232). A name in both
    /// is an ordinary bound value whose type the host stated rather than left to inference — so the
    /// two spellings never collide, and a NULL value is an entry in <see cref="Scalars"/> like any
    /// other rather than an absence.
    /// </remarks>
    public IReadOnlyDictionary<string, ChalkType> ScalarTypes { get; init; } =
        new Dictionary<string, ChalkType>(StringComparer.Ordinal);

    /// <summary>
    /// This context is a <em>shape</em>: names, kinds and types, and no value at all (D209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prepare with one and the planner folds nothing: every scalar becomes a parameter carrying its
    /// own name, every list and relation a bound relation the executor materialises, and each
    /// entitled column's sanitiser stays a per-row choice. Two principals whose bindings have the
    /// same shape therefore share one plan and one digest, which is what a host with many distinct
    /// role sets prepares this way for.
    /// </para>
    /// <para>
    /// Execution then binds the values: <c>engine.ExecuteAsync(query, context, …)</c> with a context
    /// of this same shape. A shape and a binding are not interchangeable — a query prepared with
    /// values carries them, and one prepared with a shape refuses to run without them.
    /// </para>
    /// </remarks>
    public bool ShapeOnly { get; init; }

    /// <summary>
    /// Membership sets, by name. A list is NOT NULL in every column: a NULL member makes every
    /// non-matching row UNKNOWN and <c>NOT IN</c> never true, so it is refused at bind rather than
    /// allowed to empty a result quietly.
    /// </summary>
    public IReadOnlyDictionary<string, ContextRelation> Lists { get; init; } =
        new Dictionary<string, ContextRelation>(StringComparer.Ordinal);

    /// <summary>Tables, by name, for a host-written predicate that needs one.</summary>
    public IReadOnlyDictionary<string, ContextRelation> Relations { get; init; } =
        new Dictionary<string, ContextRelation>(StringComparer.Ordinal);

    /// <summary>
    /// A list of at most this many rows is folded into literals; a larger one stays a relation the
    /// executor materialises, so its rows are never in the plan or the digest.
    /// </summary>
    public int FoldMaxRows { get; init; } = DefaultFoldMaxRows;

    /// <summary>Why this statement is being run, for the audit event alone (D205).</summary>
    public string Purpose { get; init; } = "";

    /// <summary>Who is running it, for the audit event alone (D205).</summary>
    public string Actor { get; init; } = "";

    /// <summary>Whether anything at all is bound. An empty context costs nothing anywhere.</summary>
    public bool IsEmpty =>
        Scalars.Count == 0 && ScalarTypes.Count == 0 && Lists.Count == 0 && Relations.Count == 0;

    /// <summary>
    /// Whether this name is bound as a <em>shape</em> — a name, a kind and a type with no value
    /// (D232) — rather than as a value the planner folds.
    /// </summary>
    /// <remarks>
    /// A scalar is a shape when <see cref="ScalarTypes"/> names it and <see cref="Scalars"/> does
    /// not; a list or a relation is one when its <see cref="ContextRelation.IsShape"/> says so. Under
    /// <see cref="ShapeOnly"/> every bound name is one.
    /// </remarks>
    public bool IsShape(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (ShapeOnly)
        {
            return ScalarTypes.ContainsKey(name)
                || Scalars.ContainsKey(name)
                || Lists.ContainsKey(name)
                || Relations.ContainsKey(name);
        }

        if (ScalarTypes.ContainsKey(name))
        {
            return !Scalars.ContainsKey(name);
        }

        if (Lists.TryGetValue(name, out var list))
        {
            return list.IsShape;
        }

        return Relations.TryGetValue(name, out var relation) && relation.IsShape;
    }

    /// <summary>The names bound as shapes, in ordinal order — what execution must still bind (D232).</summary>
    public IReadOnlyList<string> ShapeNames =>
        _shapeNames ??= Ordered(Names().Where(IsShape)).ToArray();

    private IReadOnlyList<string>? _shapeNames;

    /// <summary>Every name this context binds, as a value or as a shape.</summary>
    private IEnumerable<string> Names() =>
        Scalars.Keys
            .Concat(ScalarTypes.Keys)
            .Concat(Lists.Keys)
            .Concat(Relations.Keys)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// This context with one scalar bound to a value — the shape filled in, or a new name added
    /// (D232). Everything else is unchanged.
    /// </summary>
    public RequestContext With(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RefuseShapeOnly(name);
        var scalars = new Dictionary<string, object?>(Scalars, StringComparer.Ordinal)
        {
            [name] = value,
        };
        return Copy(scalars: scalars);
    }

    /// <summary>
    /// This context with one list or relation bound to its rows (D232). The kind is the one the name
    /// already has, and a name this context does not know becomes a list, which is what a policy
    /// binds. Named apart from <see cref="With(string, object?)"/> rather than overloaded on it, so
    /// that binding a scalar to NULL is not an ambiguous call.
    /// </summary>
    public RequestContext WithRows(string name, ContextRelation rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rows);
        RefuseShapeOnly(name);
        if (Relations.ContainsKey(name))
        {
            var relations = new Dictionary<string, ContextRelation>(Relations, StringComparer.Ordinal)
            {
                [name] = rows,
            };
            return Copy(relations: relations);
        }

        var lists = new Dictionary<string, ContextRelation>(Lists, StringComparer.Ordinal)
        {
            [name] = rows,
        };
        return Copy(lists: lists);
    }

    /// <summary>
    /// The same bindings with these names left <em>open</em> — bound as shapes, and everything else
    /// folded at prepare exactly as it is today (D232).
    /// </summary>
    /// <remarks>
    /// This is partial binding's own spelling: one plan for a tenant, shared by every principal of
    /// that tenant and bound per execution to the principal. <see cref="Shape()"/> is the same thing
    /// with every name named.
    /// </remarks>
    /// <exception cref="ArgumentException">A name this context does not bind at all.</exception>
    public RequestContext Shape(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return this;
        }

        var scalars = new Dictionary<string, object?>(Scalars, StringComparer.Ordinal);
        var scalarTypes = new Dictionary<string, ChalkType>(ScalarTypes, StringComparer.Ordinal);
        var lists = new Dictionary<string, ContextRelation>(Lists, StringComparer.Ordinal);
        var relations = new Dictionary<string, ContextRelation>(Relations, StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (scalars.ContainsKey(name) || scalarTypes.ContainsKey(name))
            {
                scalarTypes[name] = ScalarType(name);
                scalars.Remove(name);
                continue;
            }

            if (lists.TryGetValue(name, out var list))
            {
                lists[name] = list.Shape(name);
                continue;
            }

            if (relations.TryGetValue(name, out var relation))
            {
                relations[name] = relation.Shape(name);
                continue;
            }

            throw new ArgumentException(
                $"'{name}' is not bound by this context, so there is nothing to leave open. Name "
                + "one of " + string.Join(", ", Ordered(Names())) + ".",
                nameof(names));
        }

        return Copy(scalars, scalarTypes, lists, relations);
    }

    /// <summary>
    /// This context's <em>folded</em> half, with <paramref name="more"/>'s bindings over everything
    /// it left open — the union a narrowing is prepared with (§2.1, D233).
    /// </summary>
    /// <remarks>
    /// <para>
    /// What this context folded is in the plan and cannot be taken back, so a name
    /// <paramref name="more"/> binds and this one already folded is refused naming it: the host
    /// narrows from the base instead, which is where that value is still open. Everything this
    /// context left open takes <paramref name="more"/>'s binding — a value, which folds, or a shape,
    /// which stays open — and a name <paramref name="more"/> says nothing about stays as it was.
    /// </para>
    /// <para>
    /// This is the whole of narrowing's semantics. Preparing the result is identical to preparing
    /// with the union from the start, plan for plan and digest for digest, because it <em>is</em> the
    /// union; the sidecar's tree retention is an efficiency and changes nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="more"/> binds a name this context already folded.
    /// </exception>
    public RequestContext Narrow(RequestContext more)
    {
        ArgumentNullException.ThrowIfNull(more);
        var closed = new List<string>();
        foreach (var name in more.Names())
        {
            if (!IsShape(name) && (Scalars.ContainsKey(name) || Lists.ContainsKey(name) || Relations.ContainsKey(name)))
            {
                closed.Add(name);
            }
        }

        if (closed.Count > 0)
        {
            throw new ArgumentException(
                "this plan folded " + string.Join(", ", Ordered(closed)) + " at prepare, so they are "
                + "in its leaves and in its digest and cannot be given another value. Narrow from "
                + "the plan that left them open (docs/design/16-entitlements.md §2.1, D233).",
                nameof(more));
        }

        var scalars = new Dictionary<string, object?>(Scalars, StringComparer.Ordinal);
        var scalarTypes = new Dictionary<string, ChalkType>(ScalarTypes, StringComparer.Ordinal);
        var lists = new Dictionary<string, ContextRelation>(Lists, StringComparer.Ordinal);
        var relations = new Dictionary<string, ContextRelation>(Relations, StringComparer.Ordinal);
        foreach (var name in more.Names())
        {
            if (more.Relations.TryGetValue(name, out var relation))
            {
                lists.Remove(name);
                relations[name] = more.ShapeOnly ? relation.Shape(name) : relation;
                continue;
            }

            if (more.Lists.TryGetValue(name, out var list))
            {
                relations.Remove(name);
                lists[name] = more.ShapeOnly ? list.Shape(name) : list;
                continue;
            }

            lists.Remove(name);
            relations.Remove(name);
            if (more.IsShape(name))
            {
                scalars.Remove(name);
                scalarTypes[name] = more.ScalarTypes[name];
                continue;
            }

            scalars[name] = more.Scalars[name];
            if (more.ScalarTypes.TryGetValue(name, out var declared))
            {
                scalarTypes[name] = declared;
            }
            else
            {
                scalarTypes.Remove(name);
            }
        }

        return new RequestContext
        {
            Scalars = scalars,
            ScalarTypes = scalarTypes,
            Lists = lists,
            Relations = relations,
            FoldMaxRows = FoldMaxRows,
            Purpose = more.Purpose.Length > 0 ? more.Purpose : Purpose,
            Actor = more.Actor.Length > 0 ? more.Actor : Actor,
        };
    }

    private void RefuseShapeOnly(string name)
    {
        if (ShapeOnly)
        {
            throw new InvalidOperationException(
                $"this context is shape-only, so binding '{name}' would leave the rest of it "
                + "without values. Build the values and leave the open names to Shape(names) "
                + "(docs/design/16-entitlements.md §2.1, D232).");
        }
    }

    private RequestContext Copy(
        IReadOnlyDictionary<string, object?>? scalars = null,
        IReadOnlyDictionary<string, ChalkType>? scalarTypes = null,
        IReadOnlyDictionary<string, ContextRelation>? lists = null,
        IReadOnlyDictionary<string, ContextRelation>? relations = null) =>
        new()
        {
            Scalars = scalars ?? Scalars,
            ScalarTypes = scalarTypes ?? ScalarTypes,
            Lists = lists ?? Lists,
            Relations = relations ?? Relations,
            FoldMaxRows = FoldMaxRows,
            Purpose = Purpose,
            Actor = Actor,
        };

    /// <summary>
    /// The same names, kinds and types with every value dropped — what a prepare binds under
    /// execute-time binding (D209).
    /// </summary>
    /// <remarks>
    /// A host that holds one principal's bindings gets the shape every principal shares by calling
    /// this; a host that holds none states <see cref="ScalarTypes"/> and
    /// <see cref="ContextRelation.ColumnTypes"/> and sets <see cref="ShapeOnly"/> itself. Either way
    /// no value reaches the planner, which is the property the mode exists for.
    /// </remarks>
    public RequestContext Shape()
    {
        var scalarTypes = new Dictionary<string, ChalkType>(
            Scalars.Count + ScalarTypes.Count, StringComparer.Ordinal);
        foreach (var (name, type) in ScalarTypes)
        {
            scalarTypes[name] = type;
        }

        foreach (var (name, value) in Scalars)
        {
            if (scalarTypes.ContainsKey(name))
            {
                continue;
            }

            if (value is null or DBNull)
            {
                throw new ArgumentException(
                    $"context scalar '{name}' is NULL and states no type, so its shape cannot be "
                    + "read off it. State it in ScalarTypes.",
                    nameof(Scalars));
            }

            scalarTypes[name] = ContextValues.InferType(value);
        }

        return new RequestContext
        {
            ShapeOnly = true,
            ScalarTypes = scalarTypes,
            Lists = Shapes(Lists),
            Relations = Shapes(Relations),
            FoldMaxRows = FoldMaxRows,
            Purpose = Purpose,
            Actor = Actor,
        };
    }

    private static IReadOnlyDictionary<string, ContextRelation> Shapes(
        IReadOnlyDictionary<string, ContextRelation> bindings)
    {
        if (bindings.Count == 0)
        {
            return bindings;
        }

        var shapes = new Dictionary<string, ContextRelation>(bindings.Count, StringComparer.Ordinal);
        foreach (var (name, relation) in bindings)
        {
            shapes[name] = relation.Shape(name);
        }

        return shapes;
    }

    /// <summary>
    /// The names, kinds and types this context binds, in a canonical order and without a value
    /// (D209) — what a prepared query's context and an execution's must agree on.
    /// </summary>
    public string ShapeSignature => _signature ??= ComputeSignature();

    private string? _signature;

    private string ComputeSignature()
    {
        var text = new StringBuilder(128);
        foreach (var name in Ordered(Scalars.Keys.Concat(ScalarTypes.Keys).Distinct(StringComparer.Ordinal)))
        {
            Append(text, "s");
            Append(text, name);
            Append(text, ScalarType(name).ToProto().ToString());
        }

        foreach (var (kind, bindings) in new[] { ("l", Lists), ("r", Relations) })
        {
            foreach (var name in Ordered(bindings.Keys))
            {
                Append(text, kind);
                Append(text, name);
                Append(text, bindings[name].RowTypeProto(name).ToString());
            }
        }

        return text.ToString();
    }

    /// <summary>The declared type of one bound scalar, or the one its value carries.</summary>
    private ChalkType ScalarType(string name)
    {
        if (ScalarTypes.TryGetValue(name, out var declared))
        {
            return declared;
        }

        var value = Scalars[name];
        if (value is null or DBNull)
        {
            throw new ArgumentException(
                $"context scalar '{name}' is NULL and states no type. State it in ScalarTypes.",
                nameof(name));
        }

        return ContextValues.InferType(value);
    }

    /// <summary>
    /// A content hash of the whole bound context — every name, kind, type and value, in a canonical
    /// order — as 32 lower-case hex characters.
    /// </summary>
    /// <remarks>
    /// Under prepare-time binding the folded values are in the plan and therefore in the digest, so
    /// two role sets are two plans; this hash is what a plan cache keys on beside the descriptor
    /// hashes, so a cache can never hand one principal another's plan. Like the descriptor hash it
    /// is a content hash and not a secret. <see cref="Purpose"/> and <see cref="Actor"/> are not in
    /// it: they are audit metadata and do not change a plan.
    /// </remarks>
    public string CanonicalHash => _hash ??= ComputeHash();

    private string? _hash;

    /// <summary>
    /// A content hash of the values this context binds for <paramref name="names"/> alone, as 32
    /// lower-case hex characters — what a query prepared under partial binding checks its execution
    /// context's folded half against (D232).
    /// </summary>
    /// <remarks>
    /// The folded values are <em>in</em> the plan: a leaf that reads <c>org_id IN (1)</c> answers for
    /// organization 1 whoever executes it. So an execution that bound another organization's values
    /// would be handed this one's rows, silently. Comparing the folded half is what refuses that, and
    /// it is a comparison of two hashes rather than of two dictionaries.
    /// </remarks>
    internal string ValueHash(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
        {
            return "";
        }

        var proto = ToProto();
        var text = new StringBuilder(128);
        if (proto is not null)
        {
            foreach (var scalar in proto.Scalars)
            {
                if (!names.Contains(scalar.Name))
                {
                    continue;
                }

                Append(text, "s");
                Append(text, scalar.Name);
                Append(text, scalar.Value.ToString());
            }

            foreach (var relation in proto.Relations)
            {
                if (!names.Contains(relation.Name))
                {
                    continue;
                }

                Append(text, "l");
                Append(text, relation.Name);
                Append(text, relation.RowType.ToString());
                foreach (var row in relation.Rows)
                {
                    Append(text, row.ToString());
                }
            }
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()), hash);
        return Convert.ToHexStringLower(hash[..16]);
    }

    /// <summary>The wire form, or null when nothing is bound.</summary>
    internal Rpc.RequestContext? ToProto()
    {
        if (IsEmpty)
        {
            return null;
        }

        var message = new Rpc.RequestContext { FoldMaxRows = (uint)FoldMaxRows, ShapeOnly = ShapeOnly };
        if (ShapeOnly)
        {
            // Names, kinds and types and nothing else: a scalar is a typed expression with no
            // literal in it, and a relation a row type with no row (D209).
            foreach (var name in Ordered(ScalarTypes.Keys))
            {
                message.Scalars.Add(new ContextScalar
                {
                    Name = name,
                    Value = new Chalk.Ir.Expr { Type = ScalarTypes[name].ToProto() },
                });
            }

            foreach (var name in Ordered(Lists.Keys))
            {
                message.Relations.Add(Lists[name].ShapeProto(name, ContextRelationKind.List));
            }

            foreach (var name in Ordered(Relations.Keys))
            {
                message.Relations.Add(Relations[name].ShapeProto(name, ContextRelationKind.Relation));
            }

            return message;
        }

        // Partial binding (D232): a name in ScalarTypes and not in Scalars carries its type and no
        // literal, and says so on the entry. Everything else folds at prepare exactly as it did.
        foreach (var name in Ordered(Scalars.Keys.Concat(ScalarTypes.Keys).Distinct(StringComparer.Ordinal)))
        {
            if (!Scalars.TryGetValue(name, out var value))
            {
                message.Scalars.Add(new ContextScalar
                {
                    Name = name,
                    Shape = true,
                    Value = new Chalk.Ir.Expr { Type = ScalarTypes[name].ToProto() },
                });
                continue;
            }

            message.Scalars.Add(new ContextScalar
            {
                Name = name,
                Value = ScalarTypes.TryGetValue(name, out var declared)
                    ? ContextValues.ToLiteral(value, declared, $"context scalar '{name}'")
                    : ContextValues.ToLiteral(value, $"context scalar '{name}'"),
            });
        }

        foreach (var name in Ordered(Lists.Keys))
        {
            var list = Lists[name];
            message.Relations.Add(
                list.IsShape
                    ? list.ShapeProto(name, ContextRelationKind.List)
                    : list.ToProto(name, ContextRelationKind.List));
        }

        foreach (var name in Ordered(Relations.Keys))
        {
            var relation = Relations[name];
            message.Relations.Add(
                relation.IsShape
                    ? relation.ShapeProto(name, ContextRelationKind.Relation)
                    : relation.ToProto(name, ContextRelationKind.Relation));
        }

        return message;
    }

    /// <summary>
    /// Names in ordinal order, so a dictionary's iteration order never reaches the wire — the same
    /// bindings must produce the same request bytes, the same plan and the same hash.
    /// </summary>
    private static IEnumerable<string> Ordered(IEnumerable<string> names) =>
        names.OrderBy(n => n, StringComparer.Ordinal);

    private string ComputeHash()
    {
        var text = new StringBuilder(256);
        Append(text, FoldMaxRows.ToString(CultureInfo.InvariantCulture));
        var proto = ToProto();
        if (proto is not null)
        {
            // A shape is part of the key as much as a value is (D232): a plan built for a shape and
            // one built for the values are different plans, and the entry's own flag is what tells
            // a shape from an empty binding.
            Append(text, proto.ShapeOnly ? "shape" : "values");
            foreach (var scalar in proto.Scalars)
            {
                Append(text, scalar.Shape ? "s?" : "s");
                Append(text, scalar.Name);
                Append(text, scalar.Value.ToString());
            }

            foreach (var relation in proto.Relations)
            {
                Append(
                    text,
                    (relation.Kind == ContextRelationKind.Relation ? "r" : "l")
                    + (relation.Shape ? "?" : ""));
                Append(text, relation.Name);
                Append(text, relation.RowType.ToString());
                Append(text, relation.Rows.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var row in relation.Rows)
                {
                    Append(text, row.ToString());
                }
            }
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()), hash);
        return Convert.ToHexStringLower(hash[..16]);
    }

    private static void Append(StringBuilder text, string value)
    {
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}

/// <summary>
/// The rows of one context binding: a membership set in <see cref="RequestContext.Lists"/>, or a
/// table in <see cref="RequestContext.Relations"/>.
/// </summary>
public sealed class ContextRelation
{
    /// <summary>The column names, which a host-written predicate refers to.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The rows, each with one value per column.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>
    /// The column types. Null — the default — reads each one off the first non-null value in the
    /// column, which is right for the identifiers and names a policy binds; state them where the
    /// inference would be wrong, or where a column is empty.
    /// </summary>
    public IReadOnlyList<ChalkType>? ColumnTypes { get; init; }

    /// <summary>
    /// What the host says the binding holds when it is too large to send whole. Negative — the
    /// default — is unknown. The rows are sent either way: what the planner will not fold, the
    /// executor materialises.
    /// </summary>
    public long EstimatedRowCount { get; init; } = -1;

    /// <summary>One column of values, under the name <paramref name="column"/>.</summary>
    public static ContextRelation Of(string column, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ContextRelation
        {
            Columns = [column],
            Rows = values.Select(v => (IReadOnlyList<object?>)[v]).ToArray(),
        };
    }

    /// <summary>Several columns, each row given in column order.</summary>
    public static ContextRelation Of(IReadOnlyList<string> columns, params object?[][] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new ContextRelation
        {
            Columns = columns,
            Rows = rows.Select(r => (IReadOnlyList<object?>)r).ToArray(),
        };
    }

    /// <summary>
    /// This binding is a <em>shape</em>: its columns and their types, and no rows at all (D232). The
    /// planner reads it as a bound relation with membership markers and execution binds the rows.
    /// </summary>
    /// <remarks>
    /// Stated rather than read off an empty <see cref="Rows"/>, because an empty list is a
    /// legitimate binding — it is what makes a membership test FALSE for a principal who holds no
    /// grant of that kind — and the two must not look alike (V96).
    /// </remarks>
    public bool IsShape { get; init; }

    /// <summary>
    /// One binding's shape: the columns and their types, with no rows (D209, D232). What a host
    /// writes for a name it wants left open and holds no values for.
    /// </summary>
    public static ContextRelation Shape(
        IReadOnlyList<string> columns, IReadOnlyList<ChalkType> columnTypes) =>
        new()
        {
            Columns = columns,
            Rows = [],
            ColumnTypes = columnTypes,
            IsShape = true,
        };

    /// <summary>The same binding with its rows dropped: names, kinds and types alone (D209).</summary>
    internal ContextRelation Shape(string name)
    {
        return new ContextRelation
        {
            Columns = Columns,
            Rows = [],
            ColumnTypes = ResolveTypes(name),
            EstimatedRowCount = EstimatedRowCount,
            IsShape = true,
        };
    }

    /// <summary>The declared row type, without serialising a row.</summary>
    internal Chalk.Ir.RowType RowTypeProto(string name)
    {
        if (Columns.Count == 0)
        {
            throw new ArgumentException(
                $"context binding '{name}' declares no columns; a list or a relation has at least one.",
                nameof(name));
        }

        var types = ResolveTypes(name);
        var rowType = new Chalk.Ir.RowType();
        for (var c = 0; c < Columns.Count; c++)
        {
            rowType.Fields.Add(new Chalk.Ir.Field { Name = Columns[c], Type = types[c].ToProto() });
        }

        return rowType;
    }

    /// <summary>The wire form of the shape: the row type, and no row at all.</summary>
    /// <remarks>
    /// The entry says it is a shape whether or not the whole context does (D232). Explicit, because
    /// no rows and a shape must not read the same: an empty list is a legitimate binding, and it is
    /// what makes a membership test FALSE for a principal who holds no grant of that kind (V96).
    /// </remarks>
    internal ContextRelationValue ShapeProto(string name, ContextRelationKind kind) =>
        new()
        {
            Name = name,
            Kind = kind,
            Shape = true,
            RowType = RowTypeProto(name),
            EstRowCount = EstimatedRowCount,
        };

    internal ContextRelationValue ToProto(string name, ContextRelationKind kind)
    {
        var types = ResolveTypes(name);
        var message = new ContextRelationValue
        {
            Name = name,
            Kind = kind,
            RowType = RowTypeProto(name),
            EstRowCount = EstimatedRowCount,
        };

        foreach (var row in Rows)
        {
            if (row.Count != Columns.Count)
            {
                throw new ArgumentException(
                    $"context binding '{name}' has a row of {row.Count} values and {Columns.Count} "
                    + "column(s).",
                    nameof(name));
            }

            var virtualRow = new Chalk.Ir.VirtualRow();
            for (var c = 0; c < row.Count; c++)
            {
                virtualRow.Values.Add(
                    ContextValues.ToLiteral(row[c], types[c], $"context binding '{name}' column '{Columns[c]}'"));
            }

            message.Rows.Add(virtualRow);
        }

        return message;
    }

    private IReadOnlyList<ChalkType> ResolveTypes(string name)
    {
        if (ColumnTypes is { } declared)
        {
            if (declared.Count != Columns.Count)
            {
                throw new ArgumentException(
                    $"context binding '{name}' declares {Columns.Count} column(s) and "
                    + $"{declared.Count} type(s).",
                    nameof(name));
            }

            return declared;
        }

        var types = new ChalkType[Columns.Count];
        for (var c = 0; c < Columns.Count; c++)
        {
            var found = false;
            foreach (var row in Rows)
            {
                if (row.Count > c && row[c] is { } value and not DBNull)
                {
                    types[c] = ContextValues.InferType(value);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new ArgumentException(
                    $"context binding '{name}' column '{Columns[c]}' holds no value to read a type "
                    + "from. State ColumnTypes, or leave the binding out.",
                    nameof(name));
            }
        }

        return types;
    }
}

/// <summary>
/// A prepared query needs a context value at execution and none was bound
/// (<c>docs/design/16-entitlements.md</c> §2). Thrown before anything runs.
/// </summary>
public sealed class ContextRequiredException : InvalidOperationException
{
    /// <summary>The names the plan still needs.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Creates the exception for the named bindings.</summary>
    public ContextRequiredException(IReadOnlyList<string> names)
        : base(Describe(names))
    {
        Names = names;
    }

    private static string Describe(IReadOnlyList<string> names) =>
        "this plan was prepared for execute-time context binding and needs "
        + string.Join(", ", names ?? [])
        + " bound at execution. Execute it with the context, or prepare it with one.";
}
