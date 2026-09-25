using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using CatalogContext = Chalk.Catalog.CatalogContext;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;
using FunctionKind = Chalk.Ir.FunctionKind;
using Type = System.Type;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Resolves a plan's <c>user_function</c> name against the live catalog and the host's registrations,
/// and turns the pair into something the engine can run (D79).
/// </summary>
/// <remarks>
/// The same checks run twice on purpose: once at engine creation, over every client-bodied function
/// in the catalog, so a missing or mistyped implementation is refused before any query exists; and
/// once here at plan compilation, because a catalog refresh can bring a function the engine has
/// never seen.
/// </remarks>
internal static class UserFunctionBinding
{
    /// <summary>The descriptor a <c>schema.name</c> reference names, or a failure that says so.</summary>
    public static FunctionDescriptor Resolve(CatalogContext catalog, string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        var schemaName = dot < 0 ? null : qualified[..dot];
        var name = dot < 0 ? qualified : qualified[(dot + 1)..];

        var schema = schemaName is null
            ? catalog.Schemas.FirstOrDefault()
            : catalog.FindSchema(schemaName);
        var function = schema?.FindFunction(name);
        if (function is null)
        {
            throw new UnsupportedFeatureException(
                $"function {qualified}",
                "The plan names a function the live catalog does not declare. A plan is only valid "
                + "for the catalog epoch it was made against.");
        }

        return function;
    }

    /// <summary>The implementation registered for a client-bodied function, or null.</summary>
    public static HostFunction? Find(HostFunctionSet functions, FunctionDescriptor descriptor) =>
        functions.Find(RegistrationOf(descriptor));

    /// <summary>The key a client-bodied function is registered under: its own name unless told otherwise.</summary>
    public static string RegistrationOf(FunctionDescriptor descriptor) =>
        descriptor.Body is ClientFunctionBody { Registration: { Length: > 0 } key } ? key : descriptor.Name;

    /// <summary>
    /// Whether the host's registration can serve the declaration, naming both sides when it cannot.
    /// This is §5's negative — "a Tier 1 delegate whose CLR types do not match the descriptor" — and
    /// the message is the whole value of the check.
    /// </summary>
    public static void Check(FunctionDescriptor descriptor, HostFunction? host, string where)
    {
        var key = RegistrationOf(descriptor);
        if (host is null)
        {
            throw new InvalidOperationException(
                $"{where}: '{descriptor.Name}' is declared with a client body but nothing is "
                + $"registered as '{key}'. Register it through ChalkEngineOptions.Functions.");
        }

        switch (descriptor.Kind)
        {
            case FunctionKind.Scalar:
                CheckScalar(descriptor, host, where, key);
                break;
            case FunctionKind.Aggregate:
                CheckAggregate(descriptor, host, where, key);
                break;
            case FunctionKind.Table:
                CheckTable(descriptor, host, where, key);
                break;
            default:
                throw new InvalidOperationException(
                    $"{where}: '{descriptor.Name}' has no function kind.");
        }
    }

    private static void CheckScalar(
        FunctionDescriptor descriptor, HostFunction host, string where, string key)
    {
        if (host is HostKernel)
        {
            // A Tier 2 kernel states its own signature; it is checked against the descriptor when the
            // kernel is bound, where the declared types are to hand.
            return;
        }

        if (host is not HostScalar scalar)
        {
            throw Mismatch(where, key, "a scalar function", host);
        }

        if (scalar.Arity != descriptor.Parameters.Count)
        {
            throw new InvalidOperationException(
                $"{where}: '{descriptor.Name}' declares {descriptor.Parameters.Count} parameters and "
                + $"the delegate registered as '{key}' takes {scalar.Arity}.");
        }

        for (var i = 0; i < scalar.Arity; i++)
        {
            var parameter = descriptor.Parameters[i];
            RequireLaneType(
                scalar.ParameterTypes[i],
                parameter.Type,
                descriptor.Strict,
                $"{where}: '{descriptor.Name}' parameter '{parameter.Name}'",
                key,
                lent: true);
        }

        RequireLaneType(
            scalar.ReturnType,
            descriptor.ReturnType!.Value,
            strict: true,
            $"{where}: '{descriptor.Name}' returns",
            key);
    }

    private static void CheckAggregate(
        FunctionDescriptor descriptor, HostFunction host, string where, string key)
    {
        if (host is not HostAggregate aggregate)
        {
            throw Mismatch(where, key, "an aggregate", host);
        }

        if (descriptor.Parameters.Count is 0 or > 2)
        {
            throw new InvalidOperationException(
                $"{where}: '{descriptor.Name}' declares {descriptor.Parameters.Count} parameters; a "
                + "Tier 1 aggregate takes one value, or a value and a weight.");
        }

        RequireLaneType(
            aggregate.InputType,
            descriptor.Parameters[0].Type,
            strict: true,
            $"{where}: '{descriptor.Name}' parameter '{descriptor.Parameters[0].Name}'",
            key);
        RequireLaneType(
            aggregate.ResultType,
            descriptor.ReturnType!.Value,
            strict: true,
            $"{where}: '{descriptor.Name}' returns",
            key);
        if (aggregate.InputType != typeof(ReadOnlySpan<byte>))
        {
            // D304: a STRING or BINARY input reaches a Tier 1 aggregate as a span over the value's
            // bytes, and only as that; the state and the result stay fixed-width.
            RequireFixedWidth(
                descriptor.Parameters[0].Type,
                $"{where}: '{descriptor.Name}' parameter '{descriptor.Parameters[0].Name}'",
                key,
                input: true);
        }

        RequireFixedWidth(descriptor.ReturnType!.Value, $"{where}: '{descriptor.Name}' returns", key, input: false);
    }

    /// <summary>
    /// D298: a Tier 1 aggregate folds fixed-width lanes — the hash aggregate hands it each value's
    /// bytes, and it answers into one — so a BINARY input or result, which a scalar may spell as
    /// <c>ReadOnlyMemory&lt;byte&gt;</c> or <c>byte[]</c>, is refused here rather than at the first row.
    /// </summary>
    private static void RequireFixedWidth(ChalkType declared, string what, string key, bool input)
    {
        // F132: a STRING is refused here too. The grouped accumulator reads fixed-width lanes and
        // refused a text input at its first row, after the engine had been created.
        if (declared.Kind is Ir.TypeKind.Binary or Ir.TypeKind.String)
        {
            var kind = declared.Kind == Ir.TypeKind.Binary ? "BINARY" : "STRING";
            var wayOut = input
                ? $"Spell the input ReadOnlySpan<byte>, which is the {kind}'s own bytes lent for the call, "
                    + "and keep the state fixed-width"
                : $"Answer a fixed-width value, or aggregate the {kind} with a Tier 2 kernel";
            throw new InvalidOperationException(
                $"{what} {IrTypes.Describe(declared.ToProto())}, and a Tier 1 aggregate registered as "
                + $"'{key}' folds fixed-width values: its state and its result may be bool, sbyte, "
                + "short, int, long, float, double, decimal, DateOnly, TimeOnly, DateTime, "
                + $"DateTimeOffset, TimeSpan or Guid. {wayOut}.");
        }
    }

    /// <summary>A CLR type as a refusal names it, its nullable form included.</summary>
    private static string Describe(Type clr) =>
        Nullable.GetUnderlyingType(clr) is { } underlying ? $"{underlying.Name}?" : clr.Name;

    private static void CheckTable(
        FunctionDescriptor descriptor, HostFunction host, string where, string key)
    {
        if (host is not HostTable table)
        {
            throw Mismatch(where, key, "a table function", host);
        }

        if (table.ParameterTypes.Count != descriptor.Parameters.Count)
        {
            throw new InvalidOperationException(
                $"{where}: '{descriptor.Name}' declares {descriptor.Parameters.Count} parameters and "
                + $"the producer registered as '{key}' takes {table.ParameterTypes.Count}.");
        }

        for (var i = 0; i < descriptor.Parameters.Count; i++)
        {
            var parameter = descriptor.Parameters[i];
            var what = $"{where}: '{descriptor.Name}' parameter '{parameter.Name}'";
            if (table.ParameterTypes[i] == typeof(ReadOnlySpan<byte>))
            {
                // D304: a producer is called once, with boxed arguments, and a span cannot be boxed.
                // Its text is a constant the producer may keep, so the owning spellings serve it.
                throw new InvalidOperationException(
                    $"{what} is spelled ReadOnlySpan<byte>, and a table function's producer registered "
                    + $"as '{key}' is called once with its arguments boxed, which a span cannot be. "
                    + "Spell a STRING parameter string or Utf8String, and a BINARY one byte[] or "
                    + "ReadOnlyMemory<byte>: the argument is a constant, and the producer may keep it.");
            }

            RequireLaneType(table.ParameterTypes[i], parameter.Type, strict: true, what, key);
        }
    }

    /// <summary>
    /// The lane type check. <paramref name="lent"/> says the value is a scalar's argument — a lane lent
    /// to the delegate for one call — which is where a borrowed <see cref="Utf8String"/> is refused
    /// (D304): the struct is storable, the lane's memory is the arena's to reuse, and the documented rule
    /// was the only guard. A span is the borrowed spelling the compiler keeps inside the call; a
    /// <c>string</c> is the copy. A result or a table function's argument is the host's own memory and
    /// may still be a <see cref="Utf8String"/>.
    /// </summary>
    private static void RequireLaneType(
        Type clr, ChalkType declared, bool strict, string what, string key, bool lent = false)
    {
        if (declared.Kind == Ir.TypeKind.Composite)
        {
            RequireCompositeType(clr, declared, what, key);
            return;
        }

        var underlying = Nullable.GetUnderlyingType(clr);
        if (lent && (underlying ?? clr) == typeof(Utf8String))
        {
            throw new InvalidOperationException(
                $"{what} is spelled {Describe(clr)}, which would lend the lane's memory to the delegate "
                + $"registered as '{key}' as a value it could keep past the call — and the engine reuses "
                + "that memory. Spell the parameter ReadOnlySpan<byte>, which the compiler keeps inside "
                + "the call, or string, which is a copy. A result may still be a Utf8String: that is the "
                + "host's own memory.");
        }

        if (clr == typeof(ReadOnlySpan<byte>) && !strict && declared.Nullable)
        {
            throw new InvalidOperationException(
                $"{what} is nullable and the function is not STRICT, so the implementation registered "
                + $"as '{key}' sees every NULL — and a ReadOnlySpan<byte> has no NULL, so one would arrive "
                + "as the empty value and be indistinguishable from it. Spell the parameter string?, "
                + "or declare the function Strict() and let the engine answer NULL for a NULL argument.");
        }

        if (declared.Kind == Ir.TypeKind.Decimal
            && declared.Precision > LaneCodec.DecimalDigits
            && (underlying ?? clr) == typeof(decimal))
        {
            // D298: named on its own, because the CLR type is the right family and only its width
            // is wrong — which the general message below would not say.
            throw new InvalidOperationException(
                $"{what} is {IrTypes.Describe(declared.ToProto())}, which has no Tier 1 spelling: a CLR "
                + $"decimal holds {LaneCodec.DecimalDigits} digits and this DECIMAL {declared.Precision}. "
                + $"Declare DECIMAL({LaneCodec.DecimalDigits},{Math.Min(declared.Scale, LaneCodec.DecimalDigits)}) "
                + $"or narrower, or implement '{key}' as a Tier 2 kernel (IVectorFunction), which reads the "
                + "DECIMAL's 16-byte lane.");
        }

        if (!LaneCodec.Accepts(clr, declared))
        {
            throw new InvalidOperationException(
                $"{what} is {LaneCodec.Describe(declared)} and the implementation registered as "
                + $"'{key}' uses {clr.Name}.");
        }

        if (!strict && declared.Nullable && underlying is null && clr.IsValueType)
        {
            throw new InvalidOperationException(
                $"{what} is nullable and the function is not STRICT, so the implementation "
                + $"registered as '{key}' must take {clr.Name}? — otherwise a NULL would arrive as "
                + $"{clr.Name}'s default and be indistinguishable from a value.");
        }
    }

    /// <summary>
    /// D294: a declared COMPOSITE is served by a record the engine reads exactly as the declaration read
    /// one — the same properties, in the same order — and the two must agree field by field: names
    /// ignoring case, and each field's type the declared one, or one of its kind the property's CLR
    /// type serves as a delegate's parameter would (D298): a <c>decimal</c> any DECIMAL of up to 28
    /// digits, a <c>DateTime</c> any TIMESTAMP — nullability exactly as declared. The composite's own
    /// nullability is not compared, as a scalar result's is not: a record that never answers NULL
    /// serves a nullable declaration, and one that does answers NULL for the whole composite.
    /// </summary>
    private static void RequireCompositeType(Type clr, ChalkType declared, string what, string key)
    {
        if (!CompositeInference.IsCandidate(clr))
        {
            throw new InvalidOperationException(
                $"{what} {IrTypes.Describe(declared.ToProto())} and the implementation registered as "
                + $"'{key}' uses {CompositeInference.Describe(clr)}, which is not a record a composite value can be "
                + "read from.");
        }

        if (!CompositeInference.TryInfer(clr, nullable: declared.Nullable, out var registered, out var refusal))
        {
            throw new InvalidOperationException(
                $"{what} {IrTypes.Describe(declared.ToProto())} and the implementation registered as "
                + $"'{key}' uses {CompositeInference.Describe(clr)}, which is not one: {refusal}.");
        }

        var mismatch = CompositeMismatch(
            declared, registered, CompositeInference.Properties(Nullable.GetUnderlyingType(clr) ?? clr));
        if (mismatch is not null)
        {
            throw new InvalidOperationException(
                $"{what} {IrTypes.Describe(declared.ToProto())} and the implementation registered as "
                + $"'{key}' uses {CompositeInference.Describe(clr)}, which reads as "
                + $"{IrTypes.Describe(registered.ToProto())}: {mismatch}.");
        }
    }

    /// <summary>
    /// Where two composite values part company, or null when they agree field by field.
    /// <paramref name="properties"/> are the record's, in the order its fields were read.
    /// </summary>
    private static string? CompositeMismatch(
        ChalkType declared, ChalkType registered, IReadOnlyList<System.Reflection.PropertyInfo> properties)
    {
        if (declared.Fields.Count != registered.Fields.Count)
        {
            return $"the declaration has {declared.Fields.Count} fields and the record "
                + $"{registered.Fields.Count}";
        }

        for (var i = 0; i < declared.Fields.Count; i++)
        {
            var want = declared.Fields[i];
            var have = registered.Fields[i];
            if (!string.Equals(want.Name, have.Name, StringComparison.OrdinalIgnoreCase))
            {
                return $"field {i + 1} is '{want.Name}' declared and '{have.Name}' registered";
            }

            if (!want.Type.Equals(have.Type) && !Serves(want.Type, have.Type, properties[i].PropertyType))
            {
                return $"field '{want.Name}' is {want.Type} declared and {have.Type} registered";
            }
        }

        return null;
    }

    /// <summary>
    /// D298: whether a property that reads as <paramref name="inferred"/> serves the declared field
    /// type: the same kind and nullability, and a CLR type the lane codec accepts for the declared
    /// type — which is what lets a <c>decimal</c> write a DECIMAL(18, 2) field, per value and refused
    /// when the value does not fit, as it writes a DECIMAL(18, 2) result.
    /// </summary>
    private static bool Serves(ChalkType declared, ChalkType inferred, Type property) =>
        declared.Kind == inferred.Kind
        && declared.Nullable == inferred.Nullable
        && LaneCodec.Accepts(property, declared);

    private static InvalidOperationException Mismatch(
        string where, string key, string expected, HostFunction host) =>
        new($"{where}: '{key}' is declared as {expected}, and what is registered under that name is "
            + $"{Describe(host)}.");

    private static string Describe(HostFunction host) => host switch
    {
        HostScalar => "a scalar delegate",
        HostAggregate => "an aggregate",
        HostTable => "a table function",
        HostKernel => "a Tier 2 kernel",
        _ => host.GetType().Name,
    };

    /// <summary>The kernel a scalar call runs: a registered Tier 2 one, or a Tier 1 delegate wrapped.</summary>
    public static IVectorFunction ScalarKernel(FunctionDescriptor descriptor, HostFunction host)
    {
        var signature = new FunctionSignature
        {
            Name = RegistrationOf(descriptor),
            Parameters = [.. descriptor.Parameters.Select(p => p.Type)],
            ReturnType = descriptor.ReturnType!.Value,
        };

        if (host is HostKernel kernel)
        {
            CheckKernelSignature(descriptor, kernel, signature);
            return kernel.Kernel;
        }

        return ((HostScalar)host).Accept(new KernelBuilder(signature, descriptor.Strict));
    }

    /// <summary>
    /// A Tier 2 kernel states what it implements; the engine compares that with what the catalog
    /// declares, so a kernel wired to the wrong function fails at compilation rather than answering.
    /// </summary>
    private static void CheckKernelSignature(
        FunctionDescriptor descriptor, HostKernel kernel, FunctionSignature expected)
    {
        var actual = kernel.Kernel.Signature;
        if (actual.Parameters.Count != expected.Parameters.Count
            || !actual.Parameters.SequenceEqual(expected.Parameters)
            || !actual.ReturnType.Equals(expected.ReturnType))
        {
            throw new InvalidOperationException(
                $"the kernel registered as '{expected.Name}' implements "
                + $"({string.Join(", ", actual.Parameters)}) -> {actual.ReturnType}, and "
                + $"'{descriptor.Name}' is declared "
                + $"({string.Join(", ", expected.Parameters)}) -> {expected.ReturnType}.");
        }
    }

    /// <summary>The accumulator a grouped user aggregate runs, resolved and checked here (D80).</summary>
    public static Aggregation.MeasureAccumulator AggregateAccumulator(
        CatalogContext? catalog, HostFunctionSet functions, string qualified, ChalkType resultType)
    {
        if (catalog is null)
        {
            throw new UnsupportedFeatureException(
                $"aggregate {qualified}",
                "This plan was compiled without a catalog, so a declared aggregate cannot be resolved.");
        }

        var descriptor = Resolve(catalog, qualified);
        var host = Find(functions, descriptor);
        Check(descriptor, host, $"aggregate {qualified}");
        return ((HostAggregate)host!).Accept(
            new Aggregation.UserAggregateFactory(descriptor.Parameters[0].Type, resultType, descriptor.Name));
    }

    /// <summary>The window evaluator for the same aggregate over a frame (D80).</summary>
    public static (HostAggregate Host, FunctionDescriptor Descriptor) AggregateFor(
        CatalogContext? catalog, HostFunctionSet functions, string qualified)
    {
        if (catalog is null)
        {
            throw new UnsupportedFeatureException(
                $"aggregate {qualified}",
                "This plan was compiled without a catalog, so a declared aggregate cannot be resolved.");
        }

        var descriptor = Resolve(catalog, qualified);
        var host = Find(functions, descriptor);
        Check(descriptor, host, $"aggregate {qualified}");
        if (!descriptor.Window)
        {
            throw new UnsupportedFeatureException(
                $"aggregate {qualified} over a window",
                "It is not declared WINDOW, so it may only be used with GROUP BY "
                + "(docs/design/17-user-defined-functions.md §1).");
        }

        return ((HostAggregate)host!, descriptor);
    }

    private sealed class KernelBuilder : IHostScalarVisitor<IVectorFunction>
    {
        private readonly FunctionSignature _signature;
        private readonly bool _strict;

        public KernelBuilder(FunctionSignature signature, bool strict)
        {
            _signature = signature;
            _strict = strict;
        }

        public IVectorFunction Visit<TOut>(Func<TOut> f)
            where TOut : allows ref struct =>
            new Tier1Kernel0<TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, TOut>(Func<T1, TOut> f)
            where T1 : allows ref struct
            where TOut : allows ref struct =>
            new Tier1Kernel1<T1, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, TOut>(Func<T1, T2, TOut> f)
            where T1 : allows ref struct
            where T2 : allows ref struct
            where TOut : allows ref struct =>
            new Tier1Kernel2<T1, T2, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, T3, TOut>(Func<T1, T2, T3, TOut> f)
            where T1 : allows ref struct
            where T2 : allows ref struct
            where T3 : allows ref struct
            where TOut : allows ref struct =>
            new Tier1Kernel3<T1, T2, T3, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, T3, T4, TOut>(Func<T1, T2, T3, T4, TOut> f)
            where T1 : allows ref struct
            where T2 : allows ref struct
            where T3 : allows ref struct
            where T4 : allows ref struct
            where TOut : allows ref struct =>
            new Tier1Kernel4<T1, T2, T3, T4, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, T3, T4, T5, TOut>(Func<T1, T2, T3, T4, T5, TOut> f)
            where T1 : allows ref struct
            where T2 : allows ref struct
            where T3 : allows ref struct
            where T4 : allows ref struct
            where T5 : allows ref struct
            where TOut : allows ref struct =>
            new Tier1Kernel5<T1, T2, T3, T4, T5, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, T3, T4, T5, T6, TOut>(
            Func<T1, T2, T3, T4, T5, T6, TOut> f)
            where T1 : allows ref struct
            where T2 : allows ref struct
            where T3 : allows ref struct
            where T4 : allows ref struct
            where T5 : allows ref struct
            where T6 : allows ref struct
            where TOut : allows ref struct =>
            new Tier1Kernel6<T1, T2, T3, T4, T5, T6, TOut>(_signature, _strict, f);
    }
}
