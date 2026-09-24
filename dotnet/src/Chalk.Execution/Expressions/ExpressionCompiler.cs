using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Turns an IR expression tree into a tree of <see cref="IVectorExpr"/>. One compiler per execution:
/// nodes own reusable scratch, so two executions never share a tree (§6.4, D22).
/// </summary>
internal sealed class ExpressionCompiler
{
    private readonly CatalogContext? _catalog;
    private readonly HostFunctionSet _functions;

    /// <summary>
    /// Where each of the plan's named bound scalars sits among this execution's values
    /// (16-entitlements.md §2, D209). Empty for every plan that binds none, which is every plan under
    /// prepare-time binding.
    /// </summary>
    private readonly IReadOnlyDictionary<string, int> _boundSlots;

    /// <summary>
    /// The user calls this compiler has already compiled, by the <c>Expr</c> message that names them
    /// (D293): a second occurrence of an <c>IMMUTABLE</c> or <c>STABLE</c> call compiles to the same
    /// node, which answers once per batch. One compiler per operator, so this is the operator's
    /// expression list and no wider. Messages compare structurally, which is what makes two spellings
    /// of the same call one key.
    /// </summary>
    private readonly Dictionary<Expr, UserScalarExpr> _shared = [];

    /// <summary>A compiler for a plan that cannot name a user function — every test before step 22.</summary>
    public ExpressionCompiler()
        : this(null, HostFunctionSet.Empty)
    {
    }

    /// <summary>A compiler that can resolve a client-bodied call against the live catalog (D78).</summary>
    public ExpressionCompiler(
        CatalogContext? catalog,
        HostFunctionSet functions,
        IReadOnlyDictionary<string, int>? boundSlots = null)
    {
        _catalog = catalog;
        _functions = functions;
        _boundSlots = boundSlots ?? BoundSlots.None;
    }

    /// <summary>Compiles one expression. Every "unsupported" surfaces from here (§6.8).</summary>
    public IVectorExpr Compile(Expr expr)
    {
        var type = expr.KindCase == Expr.KindOneofCase.EnumArg
            ? default
            : ChalkType.FromProto(expr.Type);

        return expr.KindCase switch
        {
            Expr.KindOneofCase.FieldRef => type.Kind == TypeKind.Bool
                ? new BooleanFieldRefExpr((int)expr.FieldRef.Index, type)
                : new FieldRefExpr((int)expr.FieldRef.Index, type),
            Expr.KindOneofCase.Literal => new LiteralExpr(Literals.ToScalar(expr)),
            Expr.KindOneofCase.Param => new ParameterExpr(BoundSlots.Of(expr.Param, _boundSlots), type),
            Expr.KindOneofCase.Call => expr.Call.UserFunction.Length > 0
                ? UserCall(expr, type)
                : KernelRegistry.Bind(expr, Compile),
            Expr.KindOneofCase.Cast => Cast(expr, type),
            Expr.KindOneofCase.IfThen => IfThen(expr, type),
            Expr.KindOneofCase.InList => new InListExpr(
                type,
                Compile(expr.InList.Value),
                [.. expr.InList.Options.Select(Compile)]),

            // D291: a field of a composite value, a view of the composite's field column.
            Expr.KindOneofCase.FieldAccess => new FieldAccessExpr(
                type, Compile(expr.FieldAccess.Input), (int)expr.FieldAccess.Index),
            _ => throw new UnsupportedFeatureException(
                $"expression kind {expr.KindCase}",
                "The execution engine does not know how to evaluate it."),
        };
    }

    /// <summary>
    /// A call to a function the catalog declared (D78). Only a client body reaches an executor: a SQL
    /// body was inlined by the planner and a native one runs inside a pushed query.
    /// </summary>
    private IVectorExpr UserCall(Expr expr, ChalkType type)
    {
        if (_shared.TryGetValue(expr, out var shared))
        {
            shared.Share();
            return shared;
        }

        var name = expr.Call.UserFunction;
        if (_catalog is null)
        {
            throw new UnsupportedFeatureException(
                $"function {name}",
                "This expression was compiled without a catalog, so a declared function cannot be "
                + "resolved.");
        }

        var descriptor = UserFunctionBinding.Resolve(_catalog, name);
        var host = UserFunctionBinding.Find(_functions, descriptor);
        UserFunctionBinding.Check(descriptor, host, $"function {name}");

        var args = new IVectorExpr[expr.Call.Args.Count];
        for (var i = 0; i < args.Length; i++)
        {
            args[i] = Compile(expr.Call.Args[i]);
        }

        var node = new UserScalarExpr(
            type,
            UserFunctionBinding.ScalarKernel(descriptor, host!),
            args,
            descriptor.Strict,
            descriptor.Volatility);

        // A VOLATILE call answers per lane per occurrence and is never de-duplicated (D79, D293).
        if (descriptor.Volatility != Volatility.Volatile)
        {
            _shared[expr] = node;
        }

        return node;
    }

    private IVectorExpr IfThen(Expr expr, ChalkType type)
    {
        var clauses = expr.IfThen.Clauses;
        var conditions = new IVectorExpr[clauses.Count];
        var results = new IVectorExpr[clauses.Count];
        for (var i = 0; i < clauses.Count; i++)
        {
            conditions[i] = Compile(clauses[i].Condition);
            results[i] = Compile(clauses[i].Result);
        }

        return new IfThenExpr(type, conditions, results, Compile(expr.IfThen.ElseBranch));
    }

    /// <summary>
    /// The M1 cast matrix (<c>02-ir.md</c> §6). Anything outside it is unsupported here rather than
    /// approximated at run time.
    /// </summary>
    private IVectorExpr Cast(Expr expr, ChalkType target)
    {
        var operand = Compile(expr.Cast.Input);
        var source = operand.Type;
        var failure = expr.Cast.OnFailure;

        if (source.Kind == target.Kind
            && source.Precision == target.Precision
            && source.Scale == target.Scale)
        {
            return new IdentityCastExpr(target, operand);
        }

        var sourceNumeric = IrTypes.IsNumeric(source.Kind);
        var targetNumeric = IrTypes.IsNumeric(target.Kind);

        if (sourceNumeric && targetNumeric)
        {
            return new FixedCastExpr(target, operand, failure);
        }

        if (target.Kind == TypeKind.String && (sourceNumeric || source.Kind == TypeKind.Bool))
        {
            return new CastToStringExpr(target, operand, failure);
        }

        if (source.Kind == TypeKind.String
            && (targetNumeric || target.Kind is TypeKind.Bool or TypeKind.Date or TypeKind.Time
                or TypeKind.Timestamp or TypeKind.TimestampTz))
        {
            return new CastFromStringExpr(target, operand, failure);
        }

        var temporalPair = source.Kind is TypeKind.Date or TypeKind.Timestamp or TypeKind.TimestampTz
            && target.Kind is TypeKind.Date or TypeKind.Timestamp or TypeKind.TimestampTz;
        if (temporalPair || (source.Kind == TypeKind.Time && target.Kind == TypeKind.Time))
        {
            return new FixedCastExpr(target, operand, failure);
        }

        throw new UnsupportedFeatureException(
            $"CAST({source} AS {target})",
            "It is outside the M1 cast matrix (docs/design/02-ir.md §6).");
    }
}
