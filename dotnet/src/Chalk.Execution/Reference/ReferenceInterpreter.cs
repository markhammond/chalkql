using System.Globalization;
using System.Text;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Reference;

/// <summary>
/// Evaluates one IR expression against one row of boxed values, with SQL's three-valued logic written
/// out longhand (D13, §8). It shares no kernel with the vectorised engine; its whole job is to be
/// obviously correct and to disagree loudly.
/// </summary>
internal sealed class ReferenceInterpreter
{
    private readonly IReadOnlyList<object?> _parameters;
    private readonly Chalk.Catalog.CatalogContext? _catalog;
    private readonly Chalk.Sources.HostFunctionSet _functions;
    private DateTimeOffset _now;

    public ReferenceInterpreter(IReadOnlyList<object?> parameters)
        : this(parameters, null, Chalk.Sources.HostFunctionSet.Empty)
    {
    }

    /// <summary>
    /// The interpreter a plan with client-bodied functions needs (D79). It calls the host's own
    /// delegates, so the differential test compares Chalk's plumbing and not the host's arithmetic.
    /// </summary>
    public ReferenceInterpreter(
        IReadOnlyList<object?> parameters,
        Chalk.Catalog.CatalogContext? catalog,
        Chalk.Sources.HostFunctionSet functions,
        IReadOnlyDictionary<string, int>? boundSlots = null)
    {
        _parameters = parameters;
        _catalog = catalog;
        _functions = functions;
        _boundSlots = boundSlots ?? BoundSlots.None;
    }

    /// <summary>Where this plan's named bound scalars sit among the values (D209).</summary>
    private readonly IReadOnlyDictionary<string, int> _boundSlots;

    /// <summary>
    /// The value bound to <paramref name="param"/>, by the slot the plan gives it. What a
    /// parameterised <c>LIMIT</c> or <c>OFFSET</c> bound reads when the execution starts (D285).
    /// </summary>
    public object? Parameter(Chalk.Ir.DynamicParam param) =>
        _parameters[BoundSlots.Of(param, _boundSlots)];

    /// <summary>The execution's clock, which a Tier 2 kernel is handed with every batch.</summary>
    public DateTimeOffset Now
    {
        get => _now;
        set => _now = value;
    }

    /// <summary>A fresh boxed accumulator for a client-bodied aggregate named <paramref name="name"/>.</summary>
    public Chalk.Sources.IBoxedAggregate UserAggregate(string name)
    {
        var descriptor = Expressions.UserFunctionBinding.Resolve(
            _catalog ?? throw NoCatalog(name), name);
        var host = Expressions.UserFunctionBinding.Find(_functions, descriptor);
        Expressions.UserFunctionBinding.Check(descriptor, host, $"aggregate {name}");
        var aggregate = (Chalk.Sources.HostAggregate)host!;
        var boxed = aggregate.NewBoxed();

        // D291: a record Finish answered is held as the reference's composite value. D298: the input
        // reaches Add in the delegate's own CLR spelling, and a scalar answer leaves in the storage
        // vocabulary, through the conversions the vectorised engine's lanes use.
        var returns = descriptor.ReturnType!.Value;
        return new ReferenceComposites.Converted(
            boxed,
            descriptor.Parameters[0].Type,
            aggregate.InputType,
            returns,
            descriptor.Name);
    }

    /// <summary>The row reader for a client-bodied table function, shared with the vectorised engine.</summary>
    public Operators.ITableRows UserTable(string name, object?[] arguments)
    {
        var descriptor = Expressions.UserFunctionBinding.Resolve(
            _catalog ?? throw NoCatalog(name), name);
        var host = Expressions.UserFunctionBinding.Find(_functions, descriptor);
        Expressions.UserFunctionBinding.Check(descriptor, host, $"table function {name}");
        var table = (Chalk.Sources.HostTable)host!;

        // D298: each argument in the producer's own CLR spelling, as the vectorised engine hands it.
        var converted = new object?[arguments.Length];
        for (var i = 0; i < converted.Length; i++)
        {
            converted[i] = Expressions.ClrBoxes.ToClr(
                arguments[i], descriptor.Parameters[i].Type, table.ParameterTypes[i]);
        }

        return table.Accept(
            new Operators.TableRowsFactory(
                converted, descriptor.ReturnsTable, $"table function {name}"));
    }

    private static UnsupportedFeatureException NoCatalog(string name) =>
        new($"function {name}",
            "The reference executor was given no catalog, so a declared function cannot be resolved.");

    /// <summary>
    /// A client-bodied scalar call. STRICT is applied here exactly as the vectorised engine applies
    /// it, so a NULL argument answers NULL without the delegate ever seeing it.
    /// </summary>
    private object? UserCall(Expr expr, object?[] row)
    {
        var call = expr.Call;
        var descriptor = Expressions.UserFunctionBinding.Resolve(
            _catalog ?? throw NoCatalog(call.UserFunction), call.UserFunction);
        var host = Expressions.UserFunctionBinding.Find(_functions, descriptor);
        Expressions.UserFunctionBinding.Check(descriptor, host, $"function {call.UserFunction}");

        var arguments = new object?[call.Args.Count];
        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = Evaluate(call.Args[i], row);
            if (descriptor.Strict && arguments[i] is null)
            {
                return null;
            }
        }

        if (host is Chalk.Sources.HostKernel kernel)
        {
            // A Tier 2 kernel, on one-lane views: the host's own code, under both engines.
            var reference = _kernels.TryGetValue(call.UserFunction, out var cached)
                ? cached
                : _kernels[call.UserFunction] = new ReferenceKernel(kernel.Kernel, descriptor);
            return reference.Invoke(arguments, _now);
        }

        // D298: each argument in the delegate's own CLR spelling, from the storage vocabulary.
        var scalar = (Chalk.Sources.HostScalar)host!;
        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = Expressions.ClrBoxes.ToClr(
                arguments[i], descriptor.Parameters[i].Type, scalar.ParameterTypes[i]);
        }

        var answer = scalar.InvokeBoxed(arguments);

        // D291: a record is held as the reference's composite value, read through the same properties
        // the vectorised engine writes from; D298: a scalar answer in the storage vocabulary, refused
        // as the vectorised engine refuses it.
        var returns = descriptor.ReturnType!.Value;
        return returns.Kind == TypeKind.Composite
            ? ReferenceComposites.FromRecord(answer, returns, $"the result of '{descriptor.Name}'")
            : Expressions.ClrBoxes.ToStorage(
                answer, new Expressions.LaneFormat(returns, $"the result of '{descriptor.Name}'"));
    }

    private readonly Dictionary<string, ReferenceKernel> _kernels = new(StringComparer.Ordinal);

    public object? Evaluate(Expr expr, object?[] row) => expr.KindCase switch
    {
        Expr.KindOneofCase.FieldRef => row[(int)expr.FieldRef.Index],
        Expr.KindOneofCase.Literal => ReadLiteral(expr),
        Expr.KindOneofCase.Param => _parameters[BoundSlots.Of(expr.Param, _boundSlots)],
        Expr.KindOneofCase.Cast => Cast(
            Evaluate(expr.Cast.Input, row),
            ChalkType.FromProto(expr.Cast.Input.Type),
            ChalkType.FromProto(expr.Type),
            expr.Cast.OnFailure),
        Expr.KindOneofCase.IfThen => IfThen(expr, row),
        Expr.KindOneofCase.InList => InList(expr, row),
        Expr.KindOneofCase.Call => Call(expr, row),

        // D291: a field of the composite value, which a NULL composite answers NULL for.
        Expr.KindOneofCase.FieldAccess =>
            Evaluate(expr.FieldAccess.Input, row) is object?[] fields
                ? fields[(int)expr.FieldAccess.Index]
                : null,
        _ => throw new UnsupportedFeatureException(
            $"expression kind {expr.KindCase}", "The reference executor cannot evaluate it."),
    };

    /// <summary>True only when the value is TRUE: NULL and FALSE both drop (<c>02-ir.md</c> §4).</summary>
    public bool IsTrue(Expr expr, object?[] row) => Evaluate(expr, row) is true;

    private static object? ReadLiteral(Expr expr)
    {
        var literal = expr.Literal;
        return literal.ValueCase switch
        {
            Literal.ValueOneofCase.IsNull => null,
            Literal.ValueOneofCase.BoolValue => literal.BoolValue,
            Literal.ValueOneofCase.I8Value => (long)literal.I8Value,
            Literal.ValueOneofCase.I16Value => (long)literal.I16Value,
            Literal.ValueOneofCase.I32Value => (long)literal.I32Value,
            Literal.ValueOneofCase.I64Value => literal.I64Value,
            Literal.ValueOneofCase.Fp32Value => literal.Fp32Value,
            Literal.ValueOneofCase.Fp64Value => literal.Fp64Value,
            Literal.ValueOneofCase.StringValue => literal.StringValue,
            Literal.ValueOneofCase.BinaryValue => literal.BinaryValue.ToByteArray(),
            Literal.ValueOneofCase.DateValue => (long)literal.DateValue,
            Literal.ValueOneofCase.TimeValue => literal.TimeValue,
            Literal.ValueOneofCase.TimestampValue => literal.TimestampValue,
            Literal.ValueOneofCase.TimestampTzValue => literal.TimestampTzValue,
            Literal.ValueOneofCase.DecimalValue => ReferenceValues.ReadDecimal(
                literal.DecimalValue.Unscaled.Span, (int)expr.Type.Scale),
            Literal.ValueOneofCase.UuidValue => literal.UuidValue.ToByteArray(),
            Literal.ValueOneofCase.IntervalDayValue => literal.IntervalDayValue,
            Literal.ValueOneofCase.IntervalYearValue => (long)literal.IntervalYearValue,
            Literal.ValueOneofCase.ListValue => literal.ListValue.Elements
                .Select(e => ReadLiteral(new Expr { Type = expr.Type.Element, Literal = e }))
                .ToArray(),
            _ => null,
        };
    }

    private object? IfThen(Expr expr, object?[] row)
    {
        foreach (var clause in expr.IfThen.Clauses)
        {
            if (Evaluate(clause.Condition, row) is true)
            {
                return Evaluate(clause.Result, row);
            }
        }

        return Evaluate(expr.IfThen.ElseBranch, row);
    }

    private object? InList(Expr expr, object?[] row)
    {
        var value = Evaluate(expr.InList.Value, row);
        if (value is null)
        {
            return null;
        }

        var sawNull = false;
        foreach (var option in expr.InList.Options)
        {
            var candidate = Evaluate(option, row);
            if (candidate is null)
            {
                sawNull = true;
            }
            else if (Equal(value, candidate) is true)
            {
                return true;
            }
        }

        return sawNull ? null : false;
    }

    private object? Call(Expr expr, object?[] row)
    {
        var call = expr.Call;
        var type = ChalkType.FromProto(expr.Type);
        if (call.UserFunction.Length > 0)
        {
            return UserCall(expr, row);
        }

        switch (call.Function)
        {
            case FunctionId.And:
            case FunctionId.Or:
                return Kleene(call, row);

            case FunctionId.Not:
                return Evaluate(call.Args[0], row) switch { null => null, bool b => !b, _ => null };

            case FunctionId.IsNull:
                return Evaluate(call.Args[0], row) is null;
            case FunctionId.IsNotNull:
                return Evaluate(call.Args[0], row) is not null;
            case FunctionId.IsTrue:
                return Evaluate(call.Args[0], row) is true;
            case FunctionId.IsNotTrue:
                return Evaluate(call.Args[0], row) is not true;
            case FunctionId.IsFalse:
                return Evaluate(call.Args[0], row) is false;
            case FunctionId.IsNotFalse:
                return Evaluate(call.Args[0], row) is not false;

            // PRESENT sees its argument NULL or not and answers either way, so it belongs here
            // rather than among the calls whose operands are evaluated strictly (§4).
            case FunctionId.Present:
                return Evaluate(call.Args[0], row) switch
                {
                    null => false,
                    string text => text.Length > 0,
                    byte[] bytes => bytes.Length > 0,
                    _ => true,
                };

            case FunctionId.IsDistinctFrom:
            case FunctionId.IsNotDistinctFrom:
            {
                var left = Evaluate(call.Args[0], row);
                var right = Evaluate(call.Args[1], row);
                var distinct = !ReferenceValues.GroupEquals(left, right);
                return call.Function == FunctionId.IsDistinctFrom ? distinct : !distinct;
            }

            case FunctionId.Coalesce:
                foreach (var argument in call.Args)
                {
                    if (Evaluate(argument, row) is { } value)
                    {
                        return value;
                    }
                }

                return null;

            case FunctionId.Nullif:
            {
                var left = Evaluate(call.Args[0], row);
                var right = Evaluate(call.Args[1], row);
                return left is null || Equal(left, right) is true ? null : left;
            }

            case FunctionId.Extract:
                return Temporal.Extract(
                    call.Args[0].EnumArg.Value,
                    Evaluate(call.Args[1], row),
                    ChalkType.FromProto(call.Args[1].Type));

            case FunctionId.FloorTemporal:
                return Temporal.Floor(
                    call.Args[1].EnumArg.Value,
                    Evaluate(call.Args[0], row),
                    ChalkType.FromProto(call.Args[0].Type));

            default:
                break;
        }

        var operands = new object?[call.Args.Count];
        for (var i = 0; i < operands.Length; i++)
        {
            operands[i] = Evaluate(call.Args[i], row);
            if (operands[i] is null)
            {
                // Every remaining function propagates NULL (02-ir.md §5).
                return null;
            }
        }

        return call.Function switch
        {
            FunctionId.Eq => Equal(operands[0], operands[1]),
            FunctionId.Ne => Equal(operands[0], operands[1]) is bool eq ? !eq : null,
            FunctionId.Lt => ReferenceValues.Compare(operands[0], operands[1]) < 0 && Ordered(operands),
            FunctionId.Le => ReferenceValues.Compare(operands[0], operands[1]) <= 0 && Ordered(operands),
            FunctionId.Gt => ReferenceValues.Compare(operands[0], operands[1]) > 0 && Ordered(operands),
            FunctionId.Ge => ReferenceValues.Compare(operands[0], operands[1]) >= 0 && Ordered(operands),

            FunctionId.TimeBucket => Temporal.Bucket(
                (long)operands[0]!,
                operands[1]!,
                type,
                operands.Length > 2 ? operands[2]! : null),

            // A temporal moved by a day-time interval is the one heterogeneous arithmetic
            // 02-ir.md §6 defines; TUMBLE's window_end needs it (D52).
            FunctionId.Add or FunctionId.Subtract when IrTypes.IsTemporal(type.Kind) =>
                Temporal.Shift(call.Function, call, operands, type),

            FunctionId.Add or FunctionId.Subtract or FunctionId.Multiply
                or FunctionId.Divide or FunctionId.Modulus =>
                Arithmetic.Binary(call.Function, operands[0]!, operands[1]!, type),
            FunctionId.Negate or FunctionId.Abs or FunctionId.Floor
                or FunctionId.Ceil or FunctionId.Round =>
                Arithmetic.Unary(call.Function, operands, type),

            // The entitlement layer's own (16-entitlements.md §4), computed the obvious way: the
            // oracle hashes and formats per row and shares nothing with the vectorised kernel.
            FunctionId.Fingerprint => Fingerprint((string)operands[0]!, (string)operands[1]!),

            FunctionId.Concat => string.Concat(operands.Select(o => (string)o!)),
            FunctionId.Upper => ((string)operands[0]!).ToUpperInvariant(),
            FunctionId.Lower => ((string)operands[0]!).ToLowerInvariant(),
            FunctionId.CharLength => (long)CodePoints((string)operands[0]!),
            FunctionId.Substring => Text.Substring(
                (string)operands[0]!,
                (long)operands[1]!,
                operands.Length > 2 ? (long)operands[2]! : null),
            FunctionId.Like => Text.Like(
                (string)operands[0]!,
                (string)operands[1]!,
                operands.Length > 2 ? (string)operands[2]! : null),

            // Lists (D58). CARDINALITY counts, ITEM is 1-based and total, ARRAY_TO_STRING joins the
            // non-NULL elements.
            FunctionId.Cardinality => (long)((object?[])operands[0]!).Length,
            FunctionId.Item => Item((object?[])operands[0]!, (long)operands[1]!),
            FunctionId.ArrayToString => string.Join(
                (string)operands[1]!,
                ((object?[])operands[0]!).Where(e => e is not null).Select(e => (string)e!)),

            _ => throw new UnsupportedFeatureException(
                call.Function.ToString(), "The reference executor implements the M1 function set only."),
        };
    }

    /// <summary>SQL's <c>arr[i]</c>: 1-based, and NULL outside the list rather than an error.</summary>
    /// <summary>HMAC-SHA-256 of the value's UTF-8 bytes, keyed, first 16 bytes as lower-case hex.</summary>
    private static string Fingerprint(string value, string key)
    {
        var digest = System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static object? Item(object?[] list, long index) =>
        index < 1 || index > list.Length ? null : list[(int)index - 1];

    /// <summary>
    /// A comparison against NaN is false whichever way it is spelled, so an ordering test that the
    /// total order would have answered "true" is corrected here (<c>02-ir.md</c> §3).
    /// </summary>
    private static bool Ordered(object?[] operands) => !IsNaN(operands[0]) && !IsNaN(operands[1]);

    private static bool IsNaN(object? value) => value switch
    {
        double d => double.IsNaN(d),
        float f => float.IsNaN(f),
        _ => false,
    };

    private object? Kleene(ScalarCall call, object?[] row)
    {
        var isAnd = call.Function == FunctionId.And;
        var sawNull = false;
        foreach (var argument in call.Args)
        {
            switch (Evaluate(argument, row))
            {
                case null:
                    sawNull = true;
                    break;
                case bool value when value != isAnd:
                    return !isAnd;
                default:
                    break;
            }
        }

        return sawNull ? null : isAnd;
    }

    /// <summary>SQL equality: NULL propagates, and NaN is equal to nothing, itself included.</summary>
    private static object? Equal(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        if (IsNaN(left) || IsNaN(right))
        {
            return false;
        }

        return ReferenceValues.Compare(left, right) == 0;
    }

    private static int CodePoints(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            count++;
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
        }

        return count;
    }

    private object? Cast(object? value, ChalkType source, ChalkType target, CastFailure onFailure)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Conversions.Cast(value, source, target);
        }
        catch (Exception exception)
            when (onFailure == CastFailure.Null
                  && exception is OverflowException or FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Arithmetic, longhand, with the overflow and rounding rules of <c>02-ir.md</c> §6.</summary>
    private static class Arithmetic
    {
        public static object Binary(FunctionId function, object left, object right, ChalkType type)
        {
            switch (type.Kind)
            {
                case TypeKind.Fp32:
                {
                    var a = (float)left;
                    var b = (float)right;
                    return function switch
                    {
                        FunctionId.Add => a + b,
                        FunctionId.Subtract => a - b,
                        FunctionId.Multiply => a * b,
                        _ => a / b,
                    };
                }

                case TypeKind.Fp64:
                {
                    var a = (double)left;
                    var b = (double)right;
                    return function switch
                    {
                        FunctionId.Add => a + b,
                        FunctionId.Subtract => a - b,
                        FunctionId.Multiply => a * b,
                        _ => a / b,
                    };
                }

                case TypeKind.Decimal:
                {
                    var a = (decimal)left;
                    var b = (decimal)right;
                    var result = function switch
                    {
                        FunctionId.Add => a + b,
                        FunctionId.Subtract => a - b,
                        FunctionId.Multiply => a * b,
                        FunctionId.Divide => a / b,
                        _ => a % b,
                    };
                    return Conversions.Rescale(result, type);
                }

                default:
                {
                    var a = (long)left;
                    var b = (long)right;
                    var result = function switch
                    {
                        FunctionId.Add => checked(a + b),
                        FunctionId.Subtract => checked(a - b),
                        FunctionId.Multiply => checked(a * b),
                        FunctionId.Divide => a / b,
                        _ => a % b,
                    };
                    return Conversions.CheckRange(result, type);
                }
            }
        }

        public static object Unary(FunctionId function, object?[] operands, ChalkType type)
        {
            var value = operands[0]!;
            var digits = operands.Length > 1 ? (int)(long)operands[1]! : 0;
            switch (type.Kind)
            {
                case TypeKind.Fp32:
                {
                    var a = (float)value;
                    return function switch
                    {
                        FunctionId.Negate => -a,
                        FunctionId.Abs => Math.Abs(a),
                        FunctionId.Floor => MathF.Floor(a),
                        FunctionId.Ceil => MathF.Ceiling(a),
                        _ => (float)RoundAway((double)a, digits),
                    };
                }

                case TypeKind.Fp64:
                {
                    var a = (double)value;
                    return function switch
                    {
                        FunctionId.Negate => -a,
                        FunctionId.Abs => Math.Abs(a),
                        FunctionId.Floor => Math.Floor(a),
                        FunctionId.Ceil => Math.Ceiling(a),
                        _ => RoundAway(a, digits),
                    };
                }

                case TypeKind.Decimal:
                {
                    var a = (decimal)value;
                    var result = function switch
                    {
                        FunctionId.Negate => -a,
                        FunctionId.Abs => Math.Abs(a),
                        FunctionId.Floor => decimal.Floor(a),
                        FunctionId.Ceil => decimal.Ceiling(a),
                        _ => RoundAway(a, digits),
                    };
                    return Conversions.Rescale(result, type);
                }

                default:
                {
                    var a = (long)value;
                    var result = function switch
                    {
                        FunctionId.Negate => checked(-a),
                        FunctionId.Abs => a == long.MinValue ? throw new OverflowException() : Math.Abs(a),
                        _ => a,
                    };
                    return Conversions.CheckRange(result, type);
                }
            }
        }

        // F134: the same exact rounding as the vectorised engine, so the two agree on every midpoint.
        private static double RoundAway(double value, int digits) =>
            Numeric.ExactRounding.Round(value, digits, MidpointRounding.AwayFromZero);

        private static decimal RoundAway(decimal value, int digits) =>
            Numeric.ExactRounding.Round(value, digits, MidpointRounding.AwayFromZero);
    }

    /// <summary>SUBSTRING and LIKE, in code points, spelled out against SQL:2011 (<c>02-ir.md</c> §6).</summary>
    private static class Text
    {
        public static string Substring(string value, long start, long? length)
        {
            var runes = ToRunes(value);
            long end;
            if (length is null)
            {
                end = Math.Max(runes.Count + 1L, start);
            }
            else
            {
                if (length.Value < 0)
                {
                    throw new InvalidOperationException(
                        $"SUBSTRING was given a length of {length.Value}; SQL:2011 makes that a data error.");
                }

                end = start + length.Value;
            }

            var first = Math.Max(start, 1L);
            var last = Math.Min(end, runes.Count + 1L);
            if (last <= first)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (var i = first - 1; i < last - 1; i++)
            {
                builder.Append(runes[(int)i].ToString());
            }

            return builder.ToString();
        }

        public static bool Like(string value, string pattern, string? escape)
        {
            var text = ToRunes(value);
            var tokens = new List<(char Kind, System.Text.Rune Literal)>();
            var patternRunes = ToRunes(pattern);
            var escapeRune = string.IsNullOrEmpty(escape) ? (System.Text.Rune?)null : ToRunes(escape)[0];

            for (var i = 0; i < patternRunes.Count; i++)
            {
                var rune = patternRunes[i];
                if (escapeRune is { } marker && rune == marker && i + 1 < patternRunes.Count)
                {
                    tokens.Add(('l', patternRunes[++i]));
                    continue;
                }

                tokens.Add(rune.Value switch
                {
                    '%' => ('%', rune),
                    '_' => ('_', rune),
                    _ => ('l', rune),
                });
            }

            return Match(text, 0, tokens, 0);
        }

        private static bool Match(
            List<System.Text.Rune> text, int textIndex, List<(char Kind, System.Text.Rune Literal)> tokens, int token)
        {
            while (token < tokens.Count)
            {
                switch (tokens[token].Kind)
                {
                    case '%':
                        for (var skip = textIndex; skip <= text.Count; skip++)
                        {
                            if (Match(text, skip, tokens, token + 1))
                            {
                                return true;
                            }
                        }

                        return false;
                    case '_':
                        if (textIndex >= text.Count)
                        {
                            return false;
                        }

                        textIndex++;
                        token++;
                        break;
                    default:
                        if (textIndex >= text.Count || text[textIndex] != tokens[token].Literal)
                        {
                            return false;
                        }

                        textIndex++;
                        token++;
                        break;
                }
            }

            return textIndex == text.Count;
        }

        private static List<System.Text.Rune> ToRunes(string value)
        {
            var runes = new List<System.Text.Rune>(value.Length);
            foreach (var rune in value.EnumerateRunes())
            {
                runes.Add(rune);
            }

            return runes;
        }
    }

    /// <summary>EXTRACT and FLOOR … TO, over the storage integers.</summary>
    private static class Temporal
    {
        public static object? Extract(string unit, object? value, ChalkType source)
        {
            if (value is null)
            {
                return null;
            }

            var raw = (long)value;
            var (days, secondOfDay, subSecond, unitsPerSecond) = Split(raw, source);
            var date = DateOnly.FromDayNumber((int)days + 719_162);
            return unit.ToUpperInvariant() switch
            {
                "YEAR" => (long)date.Year,
                "QUARTER" => (long)(((date.Month - 1) / 3) + 1),
                "MONTH" => (long)date.Month,
                "DAY" => (long)date.Day,
                "DOW" or "DAYOFWEEK" => (long)(int)date.DayOfWeek,
                "DOY" or "DAYOFYEAR" => (long)date.DayOfYear,
                "HOUR" => secondOfDay / 3600,
                "MINUTE" => secondOfDay / 60 % 60,
                "SECOND" => secondOfDay % 60,
                "MILLISECOND" => (secondOfDay % 60 * 1000) + (subSecond * 1000 / unitsPerSecond),
                "MICROSECOND" => (secondOfDay % 60 * 1_000_000) + (subSecond * 1_000_000 / unitsPerSecond),
                "EPOCH" => (days * 86_400L) + secondOfDay,
                _ => throw new UnsupportedFeatureException(
                    $"EXTRACT {unit}", "The reference executor implements the M1 unit set."),
            };
        }

        /// <summary>
        /// <c>TIME_BUCKET</c> (D52), written independently of the kernel: the bucket width in the
        /// value's own units, a floored division, and back. PostgreSQL's <c>date_bin</c>.
        /// </summary>
        public static object? Bucket(long microseconds, object value, ChalkType type, object? origin)
        {
            if (microseconds <= 0)
            {
                throw new InvalidOperationException(
                    $"TIME_BUCKET needs a positive interval; it was given {microseconds} microseconds.");
            }

            var width = Units(microseconds, type);
            if (width <= 0)
            {
                throw new InvalidOperationException(
                    $"TIME_BUCKET's interval of {microseconds} microseconds is shorter than one unit "
                    + $"of {type}.");
            }

            var current = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            var start = origin is null
                ? 0L
                : Convert.ToInt64(origin, System.Globalization.CultureInfo.InvariantCulture);
            var offset = current - start;
            var buckets = offset >= 0 ? offset / width : ((offset + 1) / width) - 1;
            var bucket = start + (buckets * width);
            return type.Kind == TypeKind.Date ? (object)(long)bucket : bucket;
        }

        /// <summary>A temporal moved by a day-time interval, in the temporal's own units.</summary>
        public static object Shift(
            FunctionId function, ScalarCall call, object?[] operands, ChalkType type)
        {
            var temporalIndex =
                IrTypes.IsInterval(ChalkType.FromProto(call.Args[0].Type).Kind) ? 1 : 0;
            var intervalIndex = 1 - temporalIndex;
            if (ChalkType.FromProto(call.Args[intervalIndex].Type).Kind != TypeKind.IntervalDay)
            {
                throw new UnsupportedFeatureException(
                    $"{function} over {type}",
                    "The reference executor moves a temporal by an INTERVAL_DAY only.");
            }

            var current = Convert.ToInt64(
                operands[temporalIndex], System.Globalization.CultureInfo.InvariantCulture);
            var units = Units(
                Convert.ToInt64(operands[intervalIndex], System.Globalization.CultureInfo.InvariantCulture),
                type);
            return function == FunctionId.Subtract ? current - units : current + units;
        }

        /// <summary>Microseconds in the temporal's own units; days for a DATE.</summary>
        private static long Units(long microseconds, ChalkType type)
        {
            if (type.Kind == TypeKind.Date)
            {
                return microseconds >= 0
                    ? microseconds / 86_400_000_000L
                    : ((microseconds + 1) / 86_400_000_000L) - 1;
            }

            var perSecond = (long)IrTypes.TimestampUnitsPerSecond((uint)type.Precision);
            return perSecond >= 1_000_000L
                ? microseconds * (perSecond / 1_000_000L)
                : microseconds / (1_000_000L / perSecond);
        }

        public static object? Floor(string unit, object? value, ChalkType source)
        {
            if (value is null)
            {
                return null;
            }

            var raw = (long)value;
            var (days, secondOfDay, _, unitsPerSecond) = Split(raw, source);
            switch (unit.ToUpperInvariant())
            {
                case "SECOND":
                    break;
                case "MINUTE":
                    secondOfDay -= secondOfDay % 60;
                    break;
                case "HOUR":
                    secondOfDay -= secondOfDay % 3600;
                    break;
                case "DAY":
                    secondOfDay = 0;
                    break;
                case "YEAR":
                case "QUARTER":
                case "MONTH":
                {
                    secondOfDay = 0;
                    var date = DateOnly.FromDayNumber((int)days + 719_162);
                    var month = unit.ToUpperInvariant() switch
                    {
                        "YEAR" => 1,
                        "QUARTER" => (((date.Month - 1) / 3) * 3) + 1,
                        _ => date.Month,
                    };
                    days = new DateOnly(date.Year, month, 1).DayNumber - 719_162;
                    break;
                }

                default:
                    throw new UnsupportedFeatureException(
                        $"FLOOR TO {unit}", "The reference executor implements the M1 unit set.");
            }

            return source.Kind == TypeKind.Date
                ? days
                : (((days * 86_400L) + secondOfDay) * unitsPerSecond);
        }

        private static (long Days, long SecondOfDay, long SubSecond, long UnitsPerSecond) Split(
            long raw, ChalkType type)
        {
            if (type.Kind == TypeKind.Date)
            {
                return (raw, 0, 0, 1);
            }

            var unitsPerSecond = IrTypes.TimestampUnitsPerSecond((uint)type.Precision);
            var seconds = FloorDiv(raw, unitsPerSecond);
            var days = FloorDiv(seconds, 86_400L);
            return (days, seconds - (days * 86_400L), raw - (seconds * unitsPerSecond), unitsPerSecond);
        }

        private static long FloorDiv(long value, long divisor)
        {
            var quotient = value / divisor;
            return value % divisor != 0 && ((value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
        }
    }

    /// <summary>The M1 cast matrix, spelled out over the boxed representation.</summary>
    internal static class Conversions
    {
        public static object Cast(object value, ChalkType source, ChalkType target)
        {
            if (source.Kind == target.Kind && source.Scale == target.Scale
                && source.Precision == target.Precision)
            {
                return value;
            }

            if (target.Kind == TypeKind.String)
            {
                return value switch
                {
                    bool flag => flag ? "true" : "false",
                    long integer => integer.ToString(CultureInfo.InvariantCulture),
                    float single => single.ToString("R", CultureInfo.InvariantCulture),
                    double real => real.ToString("R", CultureInfo.InvariantCulture),
                    decimal number => number.ToString(CultureInfo.InvariantCulture),
                    _ => throw new InvalidCastException($"cannot render {source} as STRING"),
                };
            }

            if (source.Kind == TypeKind.String)
            {
                var text = ((string)value).Trim();
                return target.Kind switch
                {
                    TypeKind.Bool => text.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || (text.Equals("false", StringComparison.OrdinalIgnoreCase)
                            ? false
                            : throw new FormatException($"'{text}' is not a BOOL literal")),
                    TypeKind.Fp32 => float.Parse(text, CultureInfo.InvariantCulture),
                    TypeKind.Fp64 => double.Parse(text, CultureInfo.InvariantCulture),
                    TypeKind.Decimal => Rescale(decimal.Parse(text, CultureInfo.InvariantCulture), target),
                    TypeKind.Date => (long)(DateOnly.Parse(text, CultureInfo.InvariantCulture).DayNumber - 719_162),
                    TypeKind.Time => TimeOnly.Parse(text, CultureInfo.InvariantCulture).Ticks
                        / TimeSpan.TicksPerMicrosecond,
                    TypeKind.Timestamp or TypeKind.TimestampTz => TimestampUnits(
                        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault),
                        target),
                    _ => CheckRange(long.Parse(text, CultureInfo.InvariantCulture), target),
                };
            }

            if (IrTypes.IsTemporal(source.Kind) && IrTypes.IsTemporal(target.Kind))
            {
                return TemporalCast((long)value, source, target);
            }

            return target.Kind switch
            {
                // F137, F135: between DECIMAL and floating point, exactly, as the vectorised engine.
                TypeKind.Fp32 when value is decimal exact => DecimalToSingle(exact),
                TypeKind.Fp32 => (float)ToDouble(value),
                TypeKind.Fp64 when value is decimal exact => DecimalToDouble(exact),
                TypeKind.Fp64 => ToDouble(value),
                TypeKind.Decimal when value is double or float => DoubleToDecimal(ToDouble(value), target),
                TypeKind.Decimal => Rescale(ToDecimal(value), target),
                TypeKind.Bool => ToDouble(value) != 0,
                _ => CheckRange(ToInteger(value), target),
            };
        }

        /// <summary>
        /// Rounds to the target scale and refuses a value too big for the target precision.
        /// </summary>
        /// <remarks>
        /// The IR's DECIMAL goes to 38 digits and <see cref="decimal"/> holds 28, so neither the
        /// rounding scale nor the precision limit can be taken literally: a DECIMAL(38,4) — which is
        /// what Calcite gives <c>SUM(price * (1 - discount))</c> — asks for 10^34, and computing that
        /// as a decimal overflows before any value is even looked at. Both are clamped to what the
        /// type can express, which is sound in the direction that matters: a bound wider than
        /// <see cref="decimal"/> cannot be exceeded by a decimal, so there is nothing left to check.
        /// The vectorised engine stores decimals as 128-bit unscaled integers
        /// (<c>Chalk.Execution.Numeric.Decimals</c>) and so has no such ceiling. That the two agree
        /// anyway is the differential's job to prove, not this method's to assume — the reference
        /// executor is deliberately a separate implementation (D13), and where a value really is out
        /// of <see cref="decimal"/>'s range it throws rather than quietly rounding.
        /// </remarks>
        public static decimal Rescale(decimal value, ChalkType target)
        {
            const int MaxDecimalDigits = 28;

            var rounded = Math.Round(value, Math.Min(target.Scale, MaxDecimalDigits), MidpointRounding.AwayFromZero);
            var integerDigits = target.Precision - target.Scale;
            if (integerDigits <= MaxDecimalDigits)
            {
                var limit = 1m;
                for (var i = 0; i < integerDigits; i++)
                {
                    limit *= 10m;
                }

                if (Math.Abs(rounded) >= limit)
                {
                    throw new OverflowException(
                        $"{value.ToString(CultureInfo.InvariantCulture)} does not fit in "
                        + $"DECIMAL({target.Precision},{target.Scale}).");
                }
            }

            return rounded;
        }

        public static long CheckRange(long value, ChalkType target)
        {
            var (min, max) = target.Kind switch
            {
                TypeKind.I8 => ((long)sbyte.MinValue, (long)sbyte.MaxValue),
                TypeKind.I16 => (short.MinValue, short.MaxValue),
                TypeKind.I32 => (int.MinValue, int.MaxValue),
                _ => (long.MinValue, long.MaxValue),
            };

            return value >= min && value <= max
                ? value
                : throw new OverflowException($"{value} is outside {target}.");
        }

        private static long TemporalCast(long raw, ChalkType source, ChalkType target)
        {
            if (source.Kind == TypeKind.Date && target.Kind != TypeKind.Date)
            {
                return raw * 86_400L * IrTypes.TimestampUnitsPerSecond((uint)target.Precision);
            }

            if (source.Kind != TypeKind.Date && target.Kind == TypeKind.Date)
            {
                var units = IrTypes.TimestampUnitsPerSecond((uint)source.Precision);
                var seconds = raw >= 0 ? raw / units : ((raw + 1) / units) - 1;
                return seconds >= 0 ? seconds / 86_400L : ((seconds + 1) / 86_400L) - 1;
            }

            var from = IrTypes.TimestampUnitsPerSecond((uint)source.Precision);
            var to = IrTypes.TimestampUnitsPerSecond((uint)target.Precision);
            if (from == to)
            {
                return raw;
            }

            if (to > from)
            {
                return raw * (to / from);
            }

            var divisor = from / to;
            return raw >= 0 ? raw / divisor : ((raw + 1) / divisor) - 1;
        }

        private static long TimestampUnits(DateTime value, ChalkType target)
        {
            var unitsPerSecond = IrTypes.TimestampUnitsPerSecond((uint)target.Precision);
            var days = (long)(DateOnly.FromDateTime(value).DayNumber - 719_162);
            var secondOfDay = (value.Hour * 3600L) + (value.Minute * 60L) + value.Second;
            var subSecond = value.Ticks % TimeSpan.TicksPerSecond * unitsPerSecond / TimeSpan.TicksPerSecond;
            return (((days * 86_400L) + secondOfDay) * unitsPerSecond) + subSecond;
        }

        private static double DecimalToDouble(decimal value)
        {
            var (unscaled, scale) = Numeric.Decimals.Parts(value);
            return Numeric.Decimals.ToDouble(unscaled, scale);
        }

        private static float DecimalToSingle(decimal value)
        {
            var (unscaled, scale) = Numeric.Decimals.Parts(value);
            return Numeric.Decimals.ToSingle(unscaled, scale);
        }

        /// <summary>
        /// The double's exact value rounded half away from zero to the target's scale, as the
        /// vectorised cast does; the reference holds it as a decimal, so a value past 28 digits is
        /// this executor's own limit rather than the cast's.
        /// </summary>
        private static decimal DoubleToDecimal(double value, ChalkType target) =>
            Numeric.Decimals.FromUnscaled(
                Numeric.Decimals.FromDouble(value, target.Precision, target.Scale), target.Scale);

        private static double ToDouble(object value) => value switch
        {
            long integer => integer,
            float single => single,
            double real => real,
            decimal number => DecimalToDouble(number),
            bool flag => flag ? 1 : 0,
            _ => throw new InvalidCastException($"cannot read {value.GetType().Name} as a number"),
        };

        private static decimal ToDecimal(object value) => value switch
        {
            long integer => integer,
            float single => (decimal)single,
            double real => (decimal)real,
            decimal number => number,
            _ => throw new InvalidCastException($"cannot read {value.GetType().Name} as a decimal"),
        };

        private static long ToInteger(object value) => value switch
        {
            long integer => integer,
            float single => checked((long)MathF.Truncate(single)),
            double real => checked((long)Math.Truncate(real)),
            decimal number => checked((long)decimal.Truncate(number)),
            bool flag => flag ? 1 : 0,
            _ => throw new InvalidCastException($"cannot read {value.GetType().Name} as an integer"),
        };
    }
}
