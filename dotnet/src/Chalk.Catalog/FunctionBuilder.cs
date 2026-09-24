using Chalk.Ir;

namespace Chalk.Catalog;

/// <summary>
/// The declaration surface for a user-defined function (D81,
/// <c>docs/design/17-user-defined-functions.md</c> §4). A source builder hands one of these to the
/// host's callback, exactly as it hands a table builder for <c>AddTable</c>.
/// </summary>
/// <remarks>
/// <para>
/// The generic overloads infer a parameter's type from its CLR type and declare it <em>nullable</em>,
/// because a call may always pass NULL — what a <c>STRICT</c> function does about that is the
/// function's business, not the signature's. A return type inferred the same way is non-nullable,
/// and <c>STRICT</c> widens it per call: that is what SQL's <c>RETURNS NULL ON NULL INPUT</c> means.
/// Anything the CLR type cannot say — the precision of a TIMESTAMP, the scale of a DECIMAL — is said
/// with the explicit <see cref="Parameter(string, ChalkType)"/> and <see cref="Returns(ChalkType)"/>.
/// </para>
/// <para>
/// A result type outside the Tier 1 set that is a record — a <c>class</c> or <c>struct</c> of the
/// host's own — is a COMPOSITE: its public readable properties, in declaration order, are the fields,
/// each a Tier 1 type, nullable as its CLR type is. <c>Nullable&lt;TRecord&gt;</c> is a nullable
/// composite. A composite is only ever a result: a parameter or a column typed as a record is refused.
/// </para>
/// <para>
/// A textual <c>CREATE FUNCTION</c> form is a follow-up, not part of this step (§4).
/// </para>
/// </remarks>
public sealed class FunctionBuilder
{
    private readonly string _name;
    private readonly List<ParameterDescriptor> _parameters = [];
    private readonly List<Monotonicity> _monotonicity = [];
    private readonly List<ColumnDescriptor> _returnsTable = [];

    private FunctionKind _kind = FunctionKind.Scalar;
    private ChalkType? _returnType;
    private Volatility _volatility = Volatility.Immutable;
    private bool _strict;
    private bool _leakproof;
    private double _cost;
    private long _rows;
    private bool _window;
    private bool _ordered;
    private bool _nullTreatment;
    private FunctionBody? _body;

    /// <summary>A builder for a function called <paramref name="name"/>. Source builders create these.</summary>
    public FunctionBuilder(string name) => _name = name;

    // ---- kind and signature ----

    /// <summary>A scalar function. The default, so it need only be written for clarity.</summary>
    public FunctionBuilder Scalar()
    {
        _kind = FunctionKind.Scalar;
        return this;
    }

    /// <summary>A scalar function of no arguments — a clock, a sequence, a session value.</summary>
    public FunctionBuilder Scalar<TResult>() => Scalar().Returns<TResult>();

    /// <summary>A scalar function of one named argument.</summary>
    public FunctionBuilder Scalar<T1, TResult>(string p1) =>
        Scalar().Parameter<T1>(p1).Returns<TResult>();

    /// <summary>A scalar function of two named arguments.</summary>
    public FunctionBuilder Scalar<T1, T2, TResult>(string p1, string p2) =>
        Scalar().Parameter<T1>(p1).Parameter<T2>(p2).Returns<TResult>();

    /// <summary>A scalar function of three named arguments.</summary>
    public FunctionBuilder Scalar<T1, T2, T3, TResult>(string p1, string p2, string p3) =>
        Scalar().Parameter<T1>(p1).Parameter<T2>(p2).Parameter<T3>(p3).Returns<TResult>();

    /// <summary>An aggregate over one named argument.</summary>
    public FunctionBuilder Aggregate<T1, TResult>(string p1)
    {
        _kind = FunctionKind.Aggregate;
        return Parameter<T1>(p1).ReturnsNullable<TResult>();
    }

    /// <summary>An aggregate over two named arguments — a value and a weight.</summary>
    public FunctionBuilder Aggregate<T1, T2, TResult>(string p1, string p2)
    {
        _kind = FunctionKind.Aggregate;
        return Parameter<T1>(p1).Parameter<T2>(p2).ReturnsNullable<TResult>();
    }

    /// <summary>A table function. Its columns are declared with <see cref="Column"/>.</summary>
    public FunctionBuilder TableFunction()
    {
        _kind = FunctionKind.Table;
        return this;
    }

    /// <summary>One column of a table function's <c>RETURNS TABLE</c>, in order.</summary>
    public FunctionBuilder Column(string name, ChalkType type)
    {
        _kind = FunctionKind.Table;
        _returnsTable.Add(new ColumnDescriptor { Name = name, Type = type });
        return this;
    }

    /// <summary>The same, with the type inferred from <typeparamref name="T"/>.</summary>
    public FunctionBuilder Column<T>(string name) => Column(name, TypeOf<T>(nullable: false, Role.Column, name));

    /// <summary>A required parameter of the given type.</summary>
    public FunctionBuilder Parameter(string name, ChalkType type)
    {
        _parameters.Add(new ParameterDescriptor { Name = name, Type = type });
        return this;
    }

    /// <summary>A required parameter, typed from <typeparamref name="T"/> and nullable.</summary>
    public FunctionBuilder Parameter<T>(string name) =>
        Parameter(name, TypeOf<T>(nullable: true, Role.Parameter, name));

    /// <summary>An optional parameter, and the constant an omitted argument stands for.</summary>
    public FunctionBuilder Optional(string name, ChalkType type, object? defaultValue)
    {
        _parameters.Add(new ParameterDescriptor
        {
            Name = name,
            Type = type,
            Optional = true,
            Default = defaultValue,
        });
        return this;
    }

    /// <summary>The same, typed from <typeparamref name="T"/>.</summary>
    public FunctionBuilder Optional<T>(string name, T defaultValue) =>
        Optional(name, TypeOf<T>(nullable: true, Role.Parameter, name), defaultValue);

    /// <summary>The result type of a scalar or aggregate function.</summary>
    public FunctionBuilder Returns(ChalkType type)
    {
        _returnType = type;
        return this;
    }

    /// <summary>
    /// The same, typed from <typeparamref name="T"/> and non-nullable — a COMPOSITE when
    /// <typeparamref name="T"/> is a record, nullable when it is a <c>Nullable</c> of one (D294).
    /// </summary>
    public FunctionBuilder Returns<T>() => Returns(TypeOf<T>(nullable: false, Role.Result, null));

    /// <summary>The same, saying whether the result may be NULL even when no argument is.</summary>
    public FunctionBuilder ReturnsNullable<T>() => Returns(TypeOf<T>(nullable: true, Role.Result, null));

    // ---- properties ----

    /// <summary>`RETURNS NULL ON NULL INPUT`: NULL in, NULL out, without calling the body.</summary>
    public FunctionBuilder Strict(bool strict = true)
    {
        _strict = strict;
        return this;
    }

    /// <summary>Foldable and cacheable. The default.</summary>
    public FunctionBuilder Immutable()
    {
        _volatility = Volatility.Immutable;
        return this;
    }

    /// <summary>One value per execution when the arguments are constant.</summary>
    public FunctionBuilder Stable()
    {
        _volatility = Volatility.Stable;
        return this;
    }

    /// <summary>Evaluated per lane, never folded and never de-duplicated.</summary>
    public FunctionBuilder Volatile()
    {
        _volatility = Volatility.Volatile;
        return this;
    }

    /// <summary>Safe over a protected column. Reserved for the security step.</summary>
    public FunctionBuilder Leakproof(bool leakproof = true)
    {
        _leakproof = leakproof;
        return this;
    }

    /// <summary>Per-row evaluation cost, in the same units a <see cref="CostProfile"/> uses.</summary>
    public FunctionBuilder Cost(double cost)
    {
        _cost = cost;
        return this;
    }

    /// <summary>A table function's estimated output rows.</summary>
    public FunctionBuilder Rows(long rows)
    {
        _rows = rows;
        return this;
    }

    /// <summary>An aggregate that may be used over a frame.</summary>
    public FunctionBuilder Window(bool window = true)
    {
        _window = window;
        return this;
    }

    /// <summary>An aggregate that takes <c>WITHIN GROUP (ORDER BY …)</c>.</summary>
    public FunctionBuilder Ordered(bool ordered = true)
    {
        _ordered = ordered;
        return this;
    }

    /// <summary>An aggregate that accepts <c>IGNORE NULLS</c> / <c>RESPECT NULLS</c>.</summary>
    public FunctionBuilder NullTreatment(bool nullTreatment = true)
    {
        _nullTreatment = nullTreatment;
        return this;
    }

    /// <summary>
    /// The result rises with <paramref name="parameter"/>, so an ordering on that argument survives
    /// the call — which is what lets <c>ORDER BY minute_of(ts)</c> plan without a sort.
    /// </summary>
    public FunctionBuilder Increasing(string parameter, bool strictly = false) =>
        Monotone(
            parameter,
            strictly ? Monotonicity.StrictlyIncreasing : Monotonicity.Increasing);

    /// <summary>The result falls as <paramref name="parameter"/> rises.</summary>
    public FunctionBuilder Decreasing(string parameter, bool strictly = false) =>
        Monotone(
            parameter,
            strictly ? Monotonicity.StrictlyDecreasing : Monotonicity.Decreasing);

    // ---- bodies ----

    /// <summary>A SQL body, inlined by the planner so the call disappears from the plan.</summary>
    public FunctionBuilder Sql(string text)
    {
        _body = new SqlFunctionBody { Text = text };
        return this;
    }

    /// <summary>A body implemented in the host process, registered under <paramref name="registration"/>.</summary>
    public FunctionBuilder Client(string? registration = null)
    {
        _body = new ClientFunctionBody { Registration = registration };
        return this;
    }

    /// <summary>A body the declaring source implements, spelled <paramref name="dialectName"/> there.</summary>
    public FunctionBuilder Native(string? dialectName = null)
    {
        _body = new NativeFunctionBody { DialectName = dialectName };
        return this;
    }

    /// <summary>The descriptor this builder describes. Source builders call it from Build().</summary>
    public FunctionDescriptor Build()
    {
        if (_body is null)
        {
            throw new CatalogValidationException(
                $"functions ({_name})",
                "declare where the function is implemented: Sql, Client or Native");
        }

        return new FunctionDescriptor
        {
            Name = _name,
            Kind = _kind,
            Parameters = _parameters.ToArray(),
            ReturnType = _kind == FunctionKind.Table ? null : _returnType,
            ReturnsTable = _returnsTable.ToArray(),
            Volatility = _volatility,
            Strict = _strict,
            Leakproof = _leakproof,
            Monotonicity = _monotonicity.Count == 0 ? [] : Padded(),
            Cost = _cost,
            Rows = _rows,
            Window = _window,
            Ordered = _ordered,
            NullTreatment = _nullTreatment,
            Body = _body,
        };
    }

    private Monotonicity[] Padded()
    {
        var declared = new Monotonicity[_parameters.Count];
        for (var i = 0; i < declared.Length; i++)
        {
            declared[i] = i < _monotonicity.Count ? _monotonicity[i] : Monotonicity.None;
        }

        return declared;
    }

    private FunctionBuilder Monotone(string parameter, Monotonicity monotonicity)
    {
        var index = _parameters.FindIndex(
            p => string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new CatalogValidationException(
                $"functions ({_name})",
                $"'{parameter}' is not one of the parameters declared so far; declare the parameter "
                + "before saying how the result moves with it");
        }

        while (_monotonicity.Count <= index)
        {
            _monotonicity.Add(Monotonicity.None);
        }

        _monotonicity[index] = monotonicity;
        return this;
    }

    /// <summary>Where an inferred type is going to stand, which decides whether a record may.</summary>
    private enum Role
    {
        Parameter,
        Column,
        Result,
    }

    /// <summary>
    /// The declared type a CLR type stands for: the Tier 1 set and no more — <c>Utf8String</c> being a
    /// STRING spelled without an allocation per row — or, for a result, a COMPOSITE inferred from a record
    /// (D294). A record anywhere else is refused, because a composite value is only ever a function's result.
    /// </summary>
    private ChalkType TypeOf<T>(bool nullable, Role role, string? name)
    {
        var clr = typeof(T);
        var type = Nullable.GetUnderlyingType(clr) ?? clr;
        if (CompositeInference.ScalarOf(type) is { } scalar)
        {
            return scalar.WithNullable(nullable);
        }

        if (CompositeInference.IsCandidate(clr))
        {
            if (role != Role.Result)
            {
                var what = role == Role.Parameter ? "parameter" : "column";
                throw new CatalogValidationException(
                    $"functions ({_name})",
                    $"{what} '{name}' is typed {CompositeInference.Describe(clr)}, which would be a COMPOSITE; "
                    + $"a composite value is only ever a function's result, never a {what}. Declare its fields "
                    + $"as {what}s of their own.");
            }

            return CompositeInference.Infer(clr, nullable, $"functions ({_name})");
        }

        throw new CatalogValidationException(
            "functions",
            $"{type.Name} has no inferred declared type; write the type out with Parameter(name, "
            + "type) or Returns(type). A function's declared types are the catalog's, and only the "
            + "unambiguous CLR types are guessed at.");
    }
}
