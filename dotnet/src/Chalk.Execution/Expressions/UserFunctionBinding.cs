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
                key);
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
        RequireFixedWidth(
            descriptor.Parameters[0].Type,
            $"{where}: '{descriptor.Name}' parameter '{descriptor.Parameters[0].Name}'",
            key);
        RequireFixedWidth(descriptor.ReturnType!.Value, $"{where}: '{descriptor.Name}' returns", key);
    }

    /// <summary>
    /// D298: a Tier 1 aggregate folds fixed-width lanes — the hash aggregate hands it each value's
    /// bytes, and it answers into one — so a BINARY input or result, which a scalar may spell as
    /// <c>ReadOnlyMemory&lt;byte&gt;</c> or <c>byte[]</c>, is refused here rather than at the first row.
    /// </summary>
    private static void RequireFixedWidth(ChalkType declared, string what, string key)
    {
        if (declared.Kind == Ir.TypeKind.Binary)
        {
            throw new InvalidOperationException(
                $"{what} {IrTypes.Describe(declared.ToProto())}, and a Tier 1 aggregate registered as "
                + $"'{key}' folds fixed-width values only: its input and result may be bool, sbyte, "
                + "short, int, long, float, double, decimal, DateOnly, TimeOnly, DateTime, "
                + "DateTimeOffset, TimeSpan or Guid. Aggregate a BINARY with a Tier 2 kernel, or over "
                + "a fixed-width value derived from it.");
        }
    }

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
            RequireLaneType(
                table.ParameterTypes[i],
                parameter.Type,
                strict: true,
                $"{where}: '{descriptor.Name}' parameter '{parameter.Name}'",
                key);
        }
    }

    private static void RequireLaneType(
        Type clr, ChalkType declared, bool strict, string what, string key)
    {
        if (declared.Kind == Ir.TypeKind.Composite)
        {
            RequireCompositeType(clr, declared, what, key);
            return;
        }

        var underlying = Nullable.GetUnderlyingType(clr);
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
    /// ignoring case, types exact. The composite's own nullability is not compared, as a scalar result's
    /// is not: a record that never answers NULL serves a nullable declaration, and one that does
    /// answers NULL for the whole composite.
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

        var mismatch = CompositeMismatch(declared, registered);
        if (mismatch is not null)
        {
            throw new InvalidOperationException(
                $"{what} {IrTypes.Describe(declared.ToProto())} and the implementation registered as "
                + $"'{key}' uses {CompositeInference.Describe(clr)}, which reads as "
                + $"{IrTypes.Describe(registered.ToProto())}: {mismatch}.");
        }
    }

    /// <summary>Where two composite values part company, or null when they agree field by field.</summary>
    private static string? CompositeMismatch(ChalkType declared, ChalkType registered)
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

            if (!want.Type.Equals(have.Type))
            {
                return $"field '{want.Name}' is {want.Type} declared and {have.Type} registered";
            }
        }

        return null;
    }

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

        public IVectorFunction Visit<TOut>(Func<TOut> f) =>
            new Tier1Kernel0<TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, TOut>(Func<T1, TOut> f) =>
            new Tier1Kernel1<T1, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, TOut>(Func<T1, T2, TOut> f) =>
            new Tier1Kernel2<T1, T2, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, T3, TOut>(Func<T1, T2, T3, TOut> f) =>
            new Tier1Kernel3<T1, T2, T3, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, T3, T4, TOut>(Func<T1, T2, T3, T4, TOut> f) =>
            new Tier1Kernel4<T1, T2, T3, T4, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, T3, T4, T5, TOut>(Func<T1, T2, T3, T4, T5, TOut> f) =>
            new Tier1Kernel5<T1, T2, T3, T4, T5, TOut>(_signature, _strict, f);

        public IVectorFunction Visit<T1, T2, T3, T4, T5, T6, TOut>(
            Func<T1, T2, T3, T4, T5, T6, TOut> f) =>
            new Tier1Kernel6<T1, T2, T3, T4, T5, T6, TOut>(_signature, _strict, f);
    }
}
