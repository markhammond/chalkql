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

    /// <summary>
    /// Above zero while the compiler is inside an expression of constants it is wrapping, so that only
    /// the largest one is wrapped and not every part of it (D337).
    /// </summary>
    private int _withinConstant;

    /// <summary>
    /// Above zero while the compiler is inside a <c>CASE</c> branch, which is evaluated over the
    /// lanes that take it alone (F289). A branch reuses a shared node's answer for the batch but never
    /// computes one: the node writes its answer into scratch of its own, and answering again for a
    /// branch's lanes would overwrite what the batch's other readers of it hold.
    /// </summary>
    private int _withinBranch;

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

    /// <summary>
    /// The entitled tables of the quarantine this compiler's operator stands in (F302, design 82), or
    /// empty outside one: what a guard records its settled rows under.
    /// </summary>
    internal string Quarantine { get; init; } = "";

    /// <summary>Distinct nodes this compiler has handed to more than one occurrence (D299).</summary>
    public int SharedNodes { get; private set; }

    /// <summary>
    /// Compiles one expression. Every "unsupported" surfaces from here (§6.8). A non-trivial subtree
    /// this compiler has met before is the node it compiled then (D299).
    /// </summary>
    public IVectorExpr Compile(Expr expr)
    {
        // An expression of constants is answered once for the execution, not once a row (D337).
        if (_withinConstant == 0 && Memoisable(expr) && IsConstant(expr))
        {
            _withinConstant++;
            try
            {
                return new ConstantExpr(CompileShared(expr));
            }
            finally
            {
                _withinConstant--;
            }
        }

        return CompileShared(expr);
    }

    /// <summary>One expression, shared with an earlier occurrence when it can be (D299).</summary>
    private IVectorExpr CompileShared(Expr expr)
    {
        if (!Memoisable(expr))
        {
            return CompileNode(expr);
        }

        // Inside a CASE branch (F289): the batch's answer of a subtree shared outside, else a copy of
        // its own; and nothing entered for later occurrences to share.
        if (_withinBranch > 0)
        {
            if (!_memo.TryGetValue(expr, out var outside))
            {
                return CompileNode(expr);
            }

            if (!outside.IsShared)
            {
                outside.Share();
                SharedNodes++;
                _tally?.Observe(SharedNodes);
            }

            return new BranchReuseExpr(outside, CompileNode(expr));
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
        or Expr.KindOneofCase.FieldAccess
        or Expr.KindOneofCase.ContextMembership;

    /// <summary>
    /// Whether <paramref name="expr"/> is the same for every row of an execution: it reads no column,
    /// its leaves are literals, parameters and the current time, and it calls nothing
    /// <c>VOLATILE</c> (D337). A membership reads the execution's relations and is left out, as is a
    /// LIST or a composite, which have no single-value form here.
    /// </summary>
    private bool IsConstant(Expr expr)
    {
        if (expr.KindCase != Expr.KindOneofCase.EnumArg
            && expr.Type is { } type
            && type.Kind is Ir.TypeKind.List or Ir.TypeKind.Composite)
        {
            return false;
        }

        switch (expr.KindCase)
        {
            case Expr.KindOneofCase.Literal:
            case Expr.KindOneofCase.Param:
            case Expr.KindOneofCase.EnumArg:
                return true;
            case Expr.KindOneofCase.Call:
                if (expr.Call.UserFunction.Length > 0 && IsVolatile(expr.Call.UserFunction))
                {
                    return false;
                }

                foreach (var arg in expr.Call.Args)
                {
                    if (!IsConstant(arg))
                    {
                        return false;
                    }
                }

                return true;
            case Expr.KindOneofCase.Cast:
                return IsConstant(expr.Cast.Input);
            case Expr.KindOneofCase.IfThen:
                foreach (var clause in expr.IfThen.Clauses)
                {
                    if (!IsConstant(clause.Condition) || !IsConstant(clause.Result))
                    {
                        return false;
                    }
                }

                return IsConstant(expr.IfThen.ElseBranch);
            case Expr.KindOneofCase.InList:
                if (!IsConstant(expr.InList.Value))
                {
                    return false;
                }

                foreach (var option in expr.InList.Options)
                {
                    if (!IsConstant(option))
                    {
                        return false;
                    }
                }

                return true;
            case Expr.KindOneofCase.FieldAccess:
                return IsConstant(expr.FieldAccess.Input);
            default:
                return false;
        }
    }

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
            case Expr.KindOneofCase.ContextMembership:
                foreach (var column in expr.ContextMembership.Columns)
                {
                    if (ContainsVolatile(column))
                    {
                        return true;
                    }
                }

                return false;
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
            // D302: the one composite literal is the typed NULL, compiled as a column of NULL composites.
            Expr.KindOneofCase.Literal => type.Kind == TypeKind.Composite
                ? new CompositeNullExpr(type)
                : new LiteralExpr(Literals.ToScalar(expr)),
            Expr.KindOneofCase.Param => new ParameterExpr(BoundSlots.Of(expr.Param, _boundSlots), type),
            Expr.KindOneofCase.Call => expr.Call.UserFunction.Length > 0
                ? UserCall(expr, type)
                : expr.Call.Function is FunctionId.And or FunctionId.Or
                    ? Kleene(expr, type)
                    : expr.Call.Function == FunctionId.Guarded
                    ? Guarded(expr, type)
                    : expr.Call.Function == FunctionId.Coalesce && type.Kind == TypeKind.Composite
                    // D295: COALESCE over composites of one type, the first operand that holds one.
                    ? CompositeChoiceExpr.Coalesce(type, [.. expr.Call.Args.Select(Compile)])
                    : KernelRegistry.Bind(expr, Compile),
            Expr.KindOneofCase.Cast => Cast(expr, type),
            Expr.KindOneofCase.IfThen => IfThen(expr, type),
            Expr.KindOneofCase.InList => new InListExpr(
                type,
                Compile(expr.InList.Value),
                [.. expr.InList.Options.Select(Compile)]),

            // F161: a membership over a context list the executor holds, one lookup per row.
            Expr.KindOneofCase.ContextMembership => new ContextMembershipExpr(
                type,
                expr.ContextMembership.List,
                [.. expr.ContextMembership.Columns.Select(Compile)]),

            // D291: a field of a composite value, a view of the composite's field column.
            Expr.KindOneofCase.FieldAccess => new FieldAccessExpr(
                type, Compile(expr.FieldAccess.Input), (int)expr.FieldAccess.Index),
            _ => throw new UnsupportedFeatureException(
                ChalkErrorCodes.UnsupportedOperator,
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
                ChalkErrorCodes.UserFunctionUnavailable,
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

    /// <summary>
    /// <c>AND</c> and <c>OR</c>: the first operand over the batch, each later one over the lanes the
    /// operands before it left undecided, and so shared with nothing, as a <c>CASE</c> branch is.
    /// </summary>
    private IVectorExpr Kleene(Expr expr, ChalkType type)
    {
        var args = expr.Call.Args;
        var operands = new IVectorExpr[args.Count];
        if (operands.Length > 0)
        {
            operands[0] = Compile(args[0]);
        }

        _withinBranch++;
        try
        {
            for (var i = 1; i < operands.Length; i++)
            {
                operands[i] = Compile(args[i]);
            }
        }
        finally
        {
            _withinBranch--;
        }

        return new KleeneExpr(type, expr.Call.Function == FunctionId.And, operands);
    }

    /// <summary>
    /// A rule's guarded condition (F302): compiled as a branch, since on an error it is evaluated over
    /// each row alone, narrowed, and so shared with nothing.
    /// </summary>
    private IVectorExpr Guarded(Expr expr, ChalkType type)
    {
        _withinBranch++;
        try
        {
            return new GuardedExpr(type, Compile(expr.Call.Args[0]), Quarantine.Length > 0 ? Quarantine : "the policy's tables");
        }
        finally
        {
            _withinBranch--;
        }
    }

    private IVectorExpr IfThen(Expr expr, ChalkType type)
    {
        var clauses = expr.IfThen.Clauses;
        var conditions = new IVectorExpr[clauses.Count];
        var results = new IVectorExpr[clauses.Count];
        if (clauses.Count > 0)
        {
            conditions[0] = Compile(clauses[0].Condition);
        }

        // The later conditions and the branches, each evaluated over its own lanes — a condition
        // over the lanes no earlier one took, a branch over the lanes that chose it (F289) — and so
        // shared with nothing.
        IVectorExpr elseBranch;
        _withinBranch++;
        try
        {
            for (var i = 1; i < clauses.Count; i++)
            {
                conditions[i] = Compile(clauses[i].Condition);
            }

            for (var i = 0; i < clauses.Count; i++)
            {
                results[i] = Compile(clauses[i].Result);
            }

            elseBranch = Compile(expr.IfThen.ElseBranch);
        }
        finally
        {
            _withinBranch--;
        }

        // D295: a CASE over composites of one type chooses per lane and answers field by field.
        return type.Kind == TypeKind.Composite
            ? CompositeChoiceExpr.Case(type, conditions, results, elseBranch)
            : new IfThenExpr(type, conditions, results, elseBranch);
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

        if (target.Kind == TypeKind.String
            && (sourceNumeric || source.Kind is TypeKind.Bool or TypeKind.Date or TypeKind.Time or TypeKind.Timestamp
                or TypeKind.TimestampTz))
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

        // A time of day: of a timestamp (F191), or of a time at another precision.
        var timeOfDay = target.Kind == TypeKind.Time
            && source.Kind is TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz;
        if (temporalPair || timeOfDay)
        {
            return new FixedCastExpr(target, operand, failure);
        }

        throw new UnsupportedFeatureException(
            ChalkErrorCodes.UnsupportedCast,
            $"CAST({source} AS {target})",
            "This executor does not convert between those two types.");
    }
}
