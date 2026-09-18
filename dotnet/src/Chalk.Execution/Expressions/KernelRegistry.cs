using System.Numerics;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Resolves <c>(FunctionId, operand kinds)</c> to a kernel — at plan compilation, so that a missing
/// one is an <see cref="UnsupportedFeatureException"/> from <c>PlanCompiler</c> and never a surprise
/// mid-stream (§6.4). The set implemented here is exactly the ✔ column of <c>02-ir.md</c> §6.
/// </summary>
internal static class KernelRegistry
{
    /// <summary>Binds one call. <paramref name="compile"/> compiles a non-enum argument.</summary>
    public static IVectorExpr Bind(Expr expr, Func<Expr, IVectorExpr> compile)
    {
        var call = expr.Call;
        var result = ChalkType.FromProto(expr.Type);
        return call.Function switch
        {
            FunctionId.Eq or FunctionId.Ne or FunctionId.Lt or FunctionId.Le
                or FunctionId.Gt or FunctionId.Ge =>
                Comparison(call.Function, result, compile(call.Args[0]), compile(call.Args[1])),

            FunctionId.IsDistinctFrom or FunctionId.IsNotDistinctFrom =>
                DistinctFrom(
                    result,
                    compile(call.Args[0]),
                    compile(call.Args[1]),
                    call.Function == FunctionId.IsNotDistinctFrom),

            FunctionId.And or FunctionId.Or =>
                new KleeneExpr(result, call.Function == FunctionId.And, Map(call.Args, compile)),

            FunctionId.Not => new NotExpr(result, compile(call.Args[0])),

            FunctionId.IsNull or FunctionId.IsNotNull or FunctionId.IsTrue or FunctionId.IsNotTrue
                or FunctionId.IsFalse or FunctionId.IsNotFalse =>
                new IsPredicateExpr(result, call.Function, compile(call.Args[0])),

            FunctionId.Add or FunctionId.Subtract or FunctionId.Multiply
                or FunctionId.Divide or FunctionId.Modulus =>
                Arithmetic(call, result, compile),

            FunctionId.Negate or FunctionId.Abs or FunctionId.Floor
                or FunctionId.Ceil or FunctionId.Round =>
                Unary(call, result, compile),

            FunctionId.Concat => new ConcatExpr(result, Map(call.Args, compile)),
            FunctionId.Upper => new CaseMapExpr(result, compile(call.Args[0]), upper: true),
            FunctionId.Lower => new CaseMapExpr(result, compile(call.Args[0]), upper: false),
            FunctionId.CharLength => new CharLengthExpr(result, compile(call.Args[0])),
            FunctionId.Substring => new SubstringExpr(
                result,
                compile(call.Args[0]),
                compile(call.Args[1]),
                call.Args.Count > 2 ? compile(call.Args[2]) : null),
            FunctionId.Like => Like(call, result, compile),

            FunctionId.Coalesce => new CoalesceExpr(result, Map(call.Args, compile)),

            // NULLIF is the ordinary '=' kernel plus a selection, so NaN and DECIMAL scale behave
            // exactly as they do in a comparison. The first argument is compiled twice on purpose:
            // two independent nodes own separate scratch, which is cheaper to reason about than one
            // node evaluated from two places.
            FunctionId.Nullif => new NullIfExpr(
                result,
                compile(call.Args[0]),
                Comparison(
                    FunctionId.Eq,
                    ChalkType.Bool(nullable: true),
                    compile(call.Args[0]),
                    compile(call.Args[1]))),

            FunctionId.Extract => Extract(call, result, compile),
            FunctionId.FloorTemporal => FloorTemporal(call, result, compile),
            FunctionId.TimeBucket => TimeBucket(call, result, compile),

            // Lists (D58). Producing, projecting and indexing into one is all v1 does with them.
            FunctionId.Cardinality => new CardinalityExpr(result, compile(call.Args[0])),
            FunctionId.Item => new ListItemExpr(result, compile(call.Args[0]), compile(call.Args[1])),
            FunctionId.ArrayToString =>
                new ArrayToStringExpr(result, compile(call.Args[0]), compile(call.Args[1])),

            // The entitlement layer's two (step 26, 16-entitlements.md §4). Ordinary scalar
            // functions: nothing here knows what an entitlement is.
            FunctionId.Fingerprint =>
                new FingerprintExpr(result, compile(call.Args[0]), compile(call.Args[1])),
            FunctionId.Present => new PresentExpr(result, compile(call.Args[0])),

            _ => throw Unsupported(call.Function, call.Args),
        };
    }

    private static IVectorExpr[] Map(IReadOnlyList<Expr> args, Func<Expr, IVectorExpr> compile)
    {
        var compiled = new IVectorExpr[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            compiled[i] = compile(args[i]);
        }

        return compiled;
    }

    private static IVectorExpr Comparison(
        FunctionId function, ChalkType result, IVectorExpr left, IVectorExpr right)
    {
        var operand = left.Type;
        if (IrTypes.IsInterval(operand.Kind))
        {
            throw new UnsupportedFeatureException(
                $"{function}({operand}, {operand})",
                "02-ir.md §6 excludes the INTERVAL kinds from comparison in M1.");
        }

        return ColumnKinds.Of(operand) switch
        {
            ColumnKind.Boolean => Numeric<byte>(function, result, left, right),
            ColumnKind.Int8 => Numeric<sbyte>(function, result, left, right),
            ColumnKind.Int16 => Numeric<short>(function, result, left, right),
            ColumnKind.Int32 => Numeric<int>(function, result, left, right),
            ColumnKind.Int64 => Numeric<long>(function, result, left, right),
            ColumnKind.Float => Numeric<float>(function, result, left, right),
            ColumnKind.Double => Numeric<double>(function, result, left, right),
            ColumnKind.Utf8 or ColumnKind.Binary => function switch
            {
                FunctionId.Eq => new VarBytesComparisonExpr<EqResult>(result, left, right),
                FunctionId.Ne => new VarBytesComparisonExpr<NeResult>(result, left, right),
                FunctionId.Lt => new VarBytesComparisonExpr<LtResult>(result, left, right),
                FunctionId.Le => new VarBytesComparisonExpr<LeResult>(result, left, right),
                FunctionId.Gt => new VarBytesComparisonExpr<GtResult>(result, left, right),
                _ => new VarBytesComparisonExpr<GeResult>(result, left, right),
            },
            var kind => Bytes16(function, result, left, right, kind == ColumnKind.Decimal128),
        };

        static IVectorExpr Numeric<T>(FunctionId function, ChalkType result, IVectorExpr left, IVectorExpr right)
            where T : unmanaged, IComparisonOperators<T, T, bool> => function switch
            {
                FunctionId.Eq => new ComparisonExpr<T, EqOp<T>>(result, left, right),
                FunctionId.Ne => new ComparisonExpr<T, NeOp<T>>(result, left, right),
                FunctionId.Lt => new ComparisonExpr<T, LtOp<T>>(result, left, right),
                FunctionId.Le => new ComparisonExpr<T, LeOp<T>>(result, left, right),
                FunctionId.Gt => new ComparisonExpr<T, GtOp<T>>(result, left, right),
                _ => new ComparisonExpr<T, GeOp<T>>(result, left, right),
            };

        static IVectorExpr Bytes16(
            FunctionId function, ChalkType result, IVectorExpr left, IVectorExpr right, bool isDecimal) =>
            function switch
            {
                FunctionId.Eq => new FixedBytesComparisonExpr<EqResult>(result, left, right, isDecimal),
                FunctionId.Ne => new FixedBytesComparisonExpr<NeResult>(result, left, right, isDecimal),
                FunctionId.Lt => new FixedBytesComparisonExpr<LtResult>(result, left, right, isDecimal),
                FunctionId.Le => new FixedBytesComparisonExpr<LeResult>(result, left, right, isDecimal),
                FunctionId.Gt => new FixedBytesComparisonExpr<GtResult>(result, left, right, isDecimal),
                _ => new FixedBytesComparisonExpr<GeResult>(result, left, right, isDecimal),
            };
    }

    private static IVectorExpr DistinctFrom(
        ChalkType result, IVectorExpr left, IVectorExpr right, bool negate) =>
        ColumnKinds.Of(left.Type) switch
        {
            ColumnKind.Boolean => new DistinctFromExpr<byte>(result, left, right, negate),
            ColumnKind.Int8 => new DistinctFromExpr<sbyte>(result, left, right, negate),
            ColumnKind.Int16 => new DistinctFromExpr<short>(result, left, right, negate),
            ColumnKind.Int32 => new DistinctFromExpr<int>(result, left, right, negate),
            ColumnKind.Int64 => new DistinctFromExpr<long>(result, left, right, negate),
            ColumnKind.Float => new DistinctFromExpr<float>(result, left, right, negate),
            ColumnKind.Double => new DistinctFromExpr<double>(result, left, right, negate),
            ColumnKind.Utf8 or ColumnKind.Binary => new VarBytesDistinctFromExpr(result, left, right, negate),
            _ => new FixedBytesDistinctFromExpr(result, left, right, negate),
        };

    private static IVectorExpr Arithmetic(ScalarCall call, ChalkType result, Func<Expr, IVectorExpr> compile)
    {
        var left = compile(call.Args[0]);
        var right = compile(call.Args[1]);

        // The one heterogeneous arithmetic 02-ir.md §6 defines: a temporal moved by a day-time
        // interval. TUMBLE's window_end is exactly this (D52), so the window step implements it.
        if (IrTypes.IsTemporal(result.Kind)
            && call.Function is FunctionId.Add or FunctionId.Subtract)
        {
            return TemporalInterval(call, result, left, right);
        }

        if (!IrTypes.IsNumeric(result.Kind))
        {
            throw new UnsupportedFeatureException(
                $"{call.Function}({left.Type}, {right.Type})",
                "M1 implements arithmetic on the numeric kinds only; interval arithmetic is optional "
                + "(docs/design/02-ir.md §6).");
        }

        if (result.Kind == TypeKind.Decimal)
        {
            return new DecimalArithmeticExpr(result, call.Function, left, right);
        }

        var isModulus = call.Function == FunctionId.Modulus;
        return ColumnKinds.Of(result) switch
        {
            ColumnKind.Int8 => Make<sbyte>(call.Function, result, left, right),
            ColumnKind.Int16 => Make<short>(call.Function, result, left, right),
            ColumnKind.Int32 => Make<int>(call.Function, result, left, right),
            ColumnKind.Int64 => Make<long>(call.Function, result, left, right),
            ColumnKind.Float when !isModulus => Make<float>(call.Function, result, left, right),
            ColumnKind.Double when !isModulus => Make<double>(call.Function, result, left, right),
            _ => throw new UnsupportedFeatureException(
                $"{call.Function}({left.Type}, {right.Type})",
                "MODULUS is defined for the exact kinds only (docs/design/02-ir.md §6)."),
        };

        static IVectorExpr Make<T>(FunctionId function, ChalkType result, IVectorExpr left, IVectorExpr right)
            where T : unmanaged, INumber<T> => function switch
            {
                FunctionId.Add => new BinaryNumericExpr<T, AddOp<T>>(result, left, right),
                FunctionId.Subtract => new BinaryNumericExpr<T, SubtractOp<T>>(result, left, right),
                FunctionId.Multiply => new BinaryNumericExpr<T, MultiplyOp<T>>(result, left, right),
                FunctionId.Divide => new BinaryNumericExpr<T, DivideOp<T>>(result, left, right),
                _ => new BinaryNumericExpr<T, ModulusOp<T>>(result, left, right),
            };
    }

    /// <summary>
    /// <c>temporal ± INTERVAL_DAY</c>. The interval may be written either side of a <c>+</c>; a
    /// <c>-</c> only makes sense with the temporal on the left, which is the only form SQL builds.
    /// </summary>
    private static IVectorExpr TemporalInterval(
        ScalarCall call, ChalkType result, IVectorExpr left, IVectorExpr right)
    {
        var subtract = call.Function == FunctionId.Subtract;
        var (temporal, interval) = IrTypes.IsInterval(left.Type.Kind) && !subtract
            ? (right, left)
            : (left, right);

        if (!IrTypes.IsTemporal(temporal.Type.Kind) || interval.Type.Kind != TypeKind.IntervalDay)
        {
            throw new UnsupportedFeatureException(
                $"{call.Function}({left.Type}, {right.Type})",
                "M1 moves a DATE, TIMESTAMP or TIMESTAMP_TZ by an INTERVAL_DAY; month arithmetic is "
                + "not implemented (docs/design/02-ir.md §6).");
        }

        return new TemporalIntervalExpr(result, temporal, interval, subtract);
    }

    /// <summary>
    /// <c>TIME_BUCKET(INTERVAL size, value [, origin])</c> (D52). The size is an interval and the
    /// value a DATE, TIMESTAMP or TIMESTAMP_TZ; the result is the value's own type.
    /// </summary>
    private static IVectorExpr TimeBucket(
        ScalarCall call, ChalkType result, Func<Expr, IVectorExpr> compile)
    {
        if (call.Args.Count is < 2 or > 3)
        {
            throw new UnsupportedFeatureException(
                $"TIME_BUCKET with {call.Args.Count} arguments",
                "It takes an interval, a value and an optional origin.");
        }

        var size = compile(call.Args[0]);
        var value = compile(call.Args[1]);
        var origin = call.Args.Count > 2 ? compile(call.Args[2]) : null;

        if (size.Type.Kind != TypeKind.IntervalDay)
        {
            throw new UnsupportedFeatureException(
                $"TIME_BUCKET with a {size.Type} width",
                "The bucket width is an INTERVAL_DAY (docs/design/13-window-functions.md §2).");
        }

        RequireTemporal(value.Type, "TIME_BUCKET");
        if (origin is not null && origin.Type.Kind != value.Type.Kind)
        {
            throw new UnsupportedFeatureException(
                $"TIME_BUCKET with a {origin.Type} origin over a {value.Type} value",
                "The origin has the value's own type.");
        }

        return new TimeBucketExpr(result, size, value, origin);
    }

    private static IVectorExpr Unary(ScalarCall call, ChalkType result, Func<Expr, IVectorExpr> compile)
    {
        var operand = compile(call.Args[0]);
        var digits = call.Args.Count > 1 ? compile(call.Args[1]) : null;
        var rounding = call.Function is FunctionId.Floor or FunctionId.Ceil or FunctionId.Round;

        if (digits is not null && call.Function != FunctionId.Round)
        {
            throw new UnsupportedFeatureException(
                $"{call.Function}({operand.Type}, {digits.Type})",
                "Only ROUND takes a digit count in M1.");
        }

        if (result.Kind == TypeKind.Decimal)
        {
            return new DecimalUnaryExpr(result, call.Function, operand, digits);
        }

        if (rounding && !ColumnKinds.IsFloatingPoint(result.Kind))
        {
            throw new UnsupportedFeatureException(
                $"{call.Function}({operand.Type})",
                "FLOOR, CEIL and ROUND are defined for FP and DECIMAL (docs/design/02-ir.md §6).");
        }

        if (!IrTypes.IsNumeric(result.Kind))
        {
            throw new UnsupportedFeatureException(
                $"{call.Function}({operand.Type})",
                "M1 implements this function on the numeric kinds only.");
        }

        return ColumnKinds.Of(result) switch
        {
            ColumnKind.Int8 => Exact<sbyte>(call.Function, result, operand),
            ColumnKind.Int16 => Exact<short>(call.Function, result, operand),
            ColumnKind.Int32 => Exact<int>(call.Function, result, operand),
            ColumnKind.Int64 => Exact<long>(call.Function, result, operand),
            ColumnKind.Float => Approximate<float>(call.Function, result, operand, digits),
            _ => Approximate<double>(call.Function, result, operand, digits),
        };

        static IVectorExpr Exact<T>(FunctionId function, ChalkType result, IVectorExpr operand)
            where T : unmanaged, INumber<T> => function switch
            {
                FunctionId.Negate => new UnaryNumericExpr<T, NegateOp<T>>(result, operand),
                _ => new UnaryNumericExpr<T, AbsOp<T>>(result, operand),
            };

        static IVectorExpr Approximate<T>(
            FunctionId function, ChalkType result, IVectorExpr operand, IVectorExpr? digits)
            where T : unmanaged, IFloatingPoint<T> => function switch
            {
                FunctionId.Negate => new UnaryNumericExpr<T, NegateOp<T>>(result, operand),
                FunctionId.Abs => new UnaryNumericExpr<T, AbsOp<T>>(result, operand),
                FunctionId.Floor => new UnaryNumericExpr<T, FloorOp<T>>(result, operand),
                FunctionId.Ceil => new UnaryNumericExpr<T, CeilOp<T>>(result, operand),
                _ => digits is null
                    ? new UnaryNumericExpr<T, RoundOp<T>>(result, operand)
                    : new RoundDigitsExpr<T>(result, operand, digits),
            };
    }

    private static IVectorExpr Like(ScalarCall call, ChalkType result, Func<Expr, IVectorExpr> compile)
    {
        var pattern = ConstantUtf8(call.Args[1], "LIKE pattern");
        var escape = call.Args.Count > 2 ? ConstantUtf8(call.Args[2], "LIKE escape") : [];
        return new LikeExpr(result, compile(call.Args[0]), LikeMatcher.Compile(pattern, escape));
    }

    /// <summary>
    /// LIKE compiles its pattern once (§6.4), so the pattern has to be a literal. A column pattern is
    /// legal IR and simply not implemented in M1.
    /// </summary>
    private static byte[] ConstantUtf8(Expr expr, string what)
    {
        if (expr.KindCase != Expr.KindOneofCase.Literal
            || expr.Literal.ValueCase != Literal.ValueOneofCase.StringValue)
        {
            throw new UnsupportedFeatureException(
                $"LIKE with a non-constant {what}",
                "M1 compiles the pattern once per plan (docs/design/02-ir.md §6, 'constant pattern').");
        }

        return System.Text.Encoding.UTF8.GetBytes(expr.Literal.StringValue);
    }

    private static IVectorExpr Extract(ScalarCall call, ChalkType result, Func<Expr, IVectorExpr> compile)
    {
        var unit = TemporalUnits.Parse(EnumArgument(call, 0, "EXTRACT"), "EXTRACT");
        if (unit is TemporalUnit.Quarter or TemporalUnit.Week
            or TemporalUnit.Millisecond or TemporalUnit.Microsecond)
        {
            throw new UnsupportedFeatureException(
                $"EXTRACT({unit})",
                "M1 implements YEAR MONTH DAY HOUR MINUTE SECOND DOW DOY EPOCH "
                + "(docs/design/02-ir.md §6); the rest are optional.");
        }

        var operand = compile(call.Args[1]);
        RequireTemporal(operand.Type, "EXTRACT");
        return new ExtractExpr(result, unit, operand);
    }

    private static IVectorExpr FloorTemporal(ScalarCall call, ChalkType result, Func<Expr, IVectorExpr> compile)
    {
        var operand = compile(call.Args[0]);
        var unit = TemporalUnits.Parse(EnumArgument(call, 1, "FLOOR"), "FLOOR");
        if (unit is not (TemporalUnit.Year or TemporalUnit.Quarter or TemporalUnit.Month
            or TemporalUnit.Day or TemporalUnit.Hour or TemporalUnit.Minute or TemporalUnit.Second))
        {
            throw new UnsupportedFeatureException(
                $"FLOOR(… TO {unit})",
                "M1 implements YEAR QUARTER MONTH DAY HOUR MINUTE SECOND (docs/design/02-ir.md §6).");
        }

        RequireTemporal(operand.Type, "FLOOR");
        return new FloorTemporalExpr(result, unit, operand);
    }

    private static void RequireTemporal(ChalkType type, string function)
    {
        if (type.Kind is not (TypeKind.Date or TypeKind.Timestamp or TypeKind.TimestampTz))
        {
            throw new UnsupportedFeatureException(
                $"{function} over {type}",
                "The temporal functions take DATE, TIMESTAMP or TIMESTAMP_TZ (docs/design/02-ir.md §6).");
        }
    }

    private static string EnumArgument(ScalarCall call, int index, string function)
    {
        var arg = call.Args[index];
        return arg.KindCase == Expr.KindOneofCase.EnumArg
            ? arg.EnumArg.Value
            : throw new InvalidPlanException(
                "I-IR-10", $"{function}.args[{index}]", "the unit argument is not an EnumArg");
    }

    private static UnsupportedFeatureException Unsupported(FunctionId function, IReadOnlyList<Expr> args)
    {
        var operands = string.Join(", ", args.Select(a => IrTypes.Describe(a.Type)));
        return new UnsupportedFeatureException(
            $"{function}({operands})",
            "It is outside the M1 kernel set (the ✔ column of docs/design/02-ir.md §6).");
    }
}
