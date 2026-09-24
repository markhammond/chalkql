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
    /// The non-trivial subtrees this compiler has compiled, by the <c>Expr</c> message that spells
    /// them (D293, D299): a second occurrence of a built-in call, a cast, a <c>CASE</c>, an
    /// <c>IN</c>, a field access or a user call compiles to the same node, which from then on answers
    /// once per batch. One compiler per operator, so this is the operator's expression list and no
    /// wider. Messages compare structurally, which is what makes two spellings of one subtree one key;
    /// a subtree holding a <c>VOLATILE</c> call is never entered.
    /// </summary>
    private readonly Dictionary<Expr, SharedExpr> _memo = [];

    /// <summary>Whether each user function this compiler has met is <c>VOLATILE</c>, by name.</summary>
    private readonly Dictionary<string, bool> _volatile = new(StringComparer.Ordinal);

    /// <summary>Where this operator's sharing is counted, for tests; null when nothing counts it.</summary>
    private readonly SharingTally? _tally;

    /// <summary>A compiler for a plan that cannot name a user function — every test before step 22.</summary>
    public ExpressionCompiler()
        : this(null, HostFunctionSet.Empty)
    {
    }

    /// <summary>A compiler that can resolve a client-bodied call against the live catalog (D78).</summary>
    public ExpressionCompiler(
        CatalogContext? catalog,
        HostFunctionSet functions,
        IReadOnlyDictionary<string, int>? boundSlots = null,
        SharingTally? tally = null)
    {
        _catalog = catalog;
        _functions = functions;
        _boundSlots = boundSlots ?? BoundSlots.None;
        _tally = tally;
    }

    /// <summary>Distinct nodes this compiler has handed to more than one occurrence (D299).</summary>
    public int SharedNodes { get; private set; }

    /// <summary>
    /// Compiles one expression. Every "unsupported" surfaces from here (§6.8). A non-trivial subtree
    /// this compiler has met before is the node it compiled then (D299).
    /// </summary>
    public IVectorExpr Compile(Expr expr)
    {
        if (!Memoisable(expr))
        {
            return CompileNode(expr);
        }

        if (_memo.TryGetValue(expr, out var known))
        {
            if (!known.IsShared)
            {
                known.Share();
                SharedNodes++;
                _tally?.Observe(SharedNodes);
            }

            return known;
        }

        var node = CompileNode(expr);

        // A VOLATILE call answers per lane per occurrence and is never de-duplicated (D79), and a
        // subtree holding one would de-duplicate it (D299).
        if (ContainsVolatile(expr))
        {
            return node;
        }

        var shared = new SharedExpr(node, _tally);
        _memo[expr] = shared;
        return shared;
    }

    /// <summary>
    /// Whether a subtree is worth a memo entry: an expression that computes something. A field
    /// reference, a literal or a parameter is already a view or a constant, and sharing one would
    /// cost more than it saves (D299).
    /// </summary>
    private static bool Memoisable(Expr expr) => expr.KindCase is Expr.KindOneofCase.Call
        or Expr.KindOneofCase.Cast
        or Expr.KindOneofCase.IfThen
        or Expr.KindOneofCase.InList
        or Expr.KindOneofCase.FieldAccess;

    /// <summary>Whether <paramref name="expr"/> calls a <c>VOLATILE</c> user function anywhere in it.</summary>
    private bool ContainsVolatile(Expr expr)
    {
        switch (expr.KindCase)
        {
            case Expr.KindOneofCase.Call:
                if (expr.Call.UserFunction.Length > 0 && IsVolatile(expr.Call.UserFunction))
                {
                    return true;
                }

                foreach (var arg in expr.Call.Args)
                {
                    if (ContainsVolatile(arg))
                    {
                        return true;
                    }
                }

                return false;
            case Expr.KindOneofCase.Cast:
                return ContainsVolatile(expr.Cast.Input);
            case Expr.KindOneofCase.IfThen:
                foreach (var clause in expr.IfThen.Clauses)
                {
                    if (ContainsVolatile(clause.Condition) || ContainsVolatile(clause.Result))
                    {
                        return true;
                    }
                }

                return ContainsVolatile(expr.IfThen.ElseBranch);
            case Expr.KindOneofCase.InList:
                if (ContainsVolatile(expr.InList.Value))
                {
                    return true;
                }

                foreach (var option in expr.InList.Options)
                {
                    if (ContainsVolatile(option))
                    {
                        return true;
                    }
                }

                return false;
            case Expr.KindOneofCase.FieldAccess:
                return ContainsVolatile(expr.FieldAccess.Input);
            default:
                return false;
        }
    }

    /// <summary>Whether the user function <paramref name="name"/> is declared <c>VOLATILE</c>.</summary>
    private bool IsVolatile(string name)
    {
        if (_volatile.TryGetValue(name, out var known))
        {
            return known;
        }

        // Resolved as the call itself resolves it, which has already happened by the time a subtree
        // holding the call is asked about: a name the catalog does not declare never gets this far.
        var answer = _catalog is not null
            && UserFunctionBinding.Resolve(_catalog, name).Volatility == Volatility.Volatile;
        _volatile[name] = answer;
        return answer;
    }

    /// <summary>Compiles one node, its children through <see cref="Compile"/>.</summary>
    private IVectorExpr CompileNode(Expr expr)
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

        // Shared, when an IMMUTABLE or STABLE call is named twice, by Compile's memo like any other
        // subtree (D293, D299).
        return new UserScalarExpr(
            type,
            UserFunctionBinding.ScalarKernel(descriptor, host!),
            args,
            descriptor.Strict,
            descriptor.Volatility);
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
