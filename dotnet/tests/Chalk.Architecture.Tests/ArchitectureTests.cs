using System.CodeDom.Compiler;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClrType = System.Type;
using CatalogContext = Chalk.Catalog.CatalogContext;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;

namespace Chalk.Architecture.Tests;

/// <summary>
/// The invariants that are structural rather than behavioural (rev 3 §2, docs/design/05-testing.md §8).
/// These are cheap and they are the ones a well-meaning change breaks by accident.
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>Every assembly a host would load.</summary>
    private static readonly Assembly[] ClientAssemblies =
    [
        typeof(Plan).Assembly,                 // Chalk.Ir
        typeof(CatalogContext).Assembly,       // Chalk.Catalog
        typeof(ISourceRuntime).Assembly,       // Chalk.Sources.Abstractions
        typeof(PocoSourceBuilder).Assembly,    // Chalk.Sources.Poco
        ExecutionAssembly,                     // Chalk.Execution
        typeof(ChalkEngine).Assembly,          // Chalk.Client
        typeof(EntitledEngine).Assembly,       // Chalk.Entitlements
        typeof(TenancyPolicy).Assembly,        // Chalk.Entitlements.Tenancy
    ];

    private static Assembly ExecutionAssembly =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Chalk.Execution")
        ?? Assembly.Load("Chalk.Execution");

    /// <summary>
    /// I1 — the client contains no JVM artefact. Planning is always out of process; this is the guard
    /// against someone reaching for a convenience dependency.
    /// </summary>
    [Fact]
    public void I1_client_has_no_jvm_dependencies()
    {
        foreach (var assembly in ClientAssemblies)
        {
            var offenders = assembly.GetReferencedAssemblies()
                .Where(a => a.Name!.StartsWith("IKVM", StringComparison.OrdinalIgnoreCase)
                    || a.Name.StartsWith("org.apache", StringComparison.OrdinalIgnoreCase)
                    || a.Name.StartsWith("java.", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Name)
                .ToArray();

            Assert.True(
                offenders.Length == 0,
                $"{assembly.GetName().Name} references {string.Join(", ", offenders)}");
        }
    }

    /// <summary>
    /// I1, the other half: nothing in the client tree loads a jar, a JVM or the planner's Java
    /// packages. A reference is the obvious failure; a file path is the sneaky one.
    /// </summary>
    [Fact]
    public void No_client_assembly_ships_a_jar()
    {
        foreach (var assembly in ClientAssemblies)
        {
            var directory = Path.GetDirectoryName(assembly.Location)!;
            var jars = Directory.GetFiles(directory, "*.jar", SearchOption.TopDirectoryOnly);

            Assert.True(jars.Length == 0, $"{assembly.GetName().Name} ships {string.Join(", ", jars)}");
        }
    }

    /// <summary>
    /// Everything in <c>Chalk.Execution</c> is internal: operator shapes will churn and no host should
    /// be able to depend on them (docs/design/04-client.md §1).
    /// </summary>
    [Fact]
    public void Execution_exports_no_public_type()
    {
        var exported = ExecutionAssembly.GetExportedTypes()
            .Select(t => t.FullName!)
            .ToArray();

        Assert.True(
            exported.Length == 0,
            "Chalk.Execution must export nothing; found " + string.Join(", ", exported));
    }

    /// <summary>The package graph of §1, asserted arrow by arrow. No reverse edges.</summary>
    [Theory]
    [InlineData("Chalk.Ir", "Chalk.Catalog")]
    [InlineData("Chalk.Ir", "Chalk.Sources.Abstractions")]
    [InlineData("Chalk.Ir", "Chalk.Sources.Poco")]
    [InlineData("Chalk.Ir", "Chalk.Execution")]
    [InlineData("Chalk.Ir", "Chalk.Client")]
    [InlineData("Chalk.Catalog", "Chalk.Sources.Abstractions")]
    [InlineData("Chalk.Catalog", "Chalk.Sources.Poco")]
    [InlineData("Chalk.Catalog", "Chalk.Execution")]
    [InlineData("Chalk.Catalog", "Chalk.Client")]
    [InlineData("Chalk.Sources.Abstractions", "Chalk.Sources.Poco")]
    [InlineData("Chalk.Sources.Abstractions", "Chalk.Execution")]
    [InlineData("Chalk.Sources.Abstractions", "Chalk.Client")]
    [InlineData("Chalk.Execution", "Chalk.Client")]
    [InlineData("Chalk.Sources.Poco", "Chalk.Client")]
    // Step 26c, D213: the entitlement wrapper sits above the client, and the tenancy package above
    // the wrapper. The first of these is the arrow the whole step is about — a core client that
    // referenced the entitlements would make "zero cost by construction" a claim rather than a fact.
    [InlineData("Chalk.Ir", "Chalk.Entitlements")]
    [InlineData("Chalk.Catalog", "Chalk.Entitlements")]
    [InlineData("Chalk.Execution", "Chalk.Entitlements")]
    [InlineData("Chalk.Client", "Chalk.Entitlements")]
    [InlineData("Chalk.Entitlements", "Chalk.Entitlements.Tenancy")]
    public void The_package_graph_has_no_reverse_edge(string lower, string higher)
    {
        var lowerAssembly = ClientAssemblies.Single(a => a.GetName().Name == lower);

        Assert.DoesNotContain(
            higher,
            lowerAssembly.GetReferencedAssemblies().Select(a => a.Name));
    }

    /// <summary>
    /// D264 — <c>Chalk.Sources.Ado</c>'s only friends are its own tests, the integration suite, and
    /// the conformance harness that binds through <c>AdoParameters</c> for its provider trait probes
    /// (D263). A driver package such as <c>Chalk.Sources.DuckDb</c> binds through the public surface
    /// instead (ADR 0047), so this list must never grow a production package again.
    /// </summary>
    [Fact]
    public void The_ado_package_grants_internals_access_to_only_tests_and_the_conformance_harness()
    {
        var friends = typeof(AdoParameters).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName)
            .ToArray();

        var offenders = friends
            .Where(name => name != "Chalk.Sources.Conformance"
                && !name.EndsWith(".Tests", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Chalk.Sources.Ado grants internals access to " + string.Join(", ", offenders));
    }

    /// <summary><c>Chalk.Ir</c> depends on the protobuf runtime and nothing else of ours.</summary>
    [Fact]
    public void Chalk_ir_references_only_google_protobuf()
    {
        var references = typeof(Plan).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                && !string.Equals(name, "netstandard", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(["Google.Protobuf"], references);
    }

    /// <summary>
    /// D7 and D35: the Akade sample is a sample. No shipped package may reference it, or the
    /// "index creation is pluggable" claim would come with a dependency attached.
    /// </summary>
    [Fact]
    public void No_shipped_package_references_the_sample_index_adapter()
    {
        foreach (var assembly in ClientAssemblies)
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies().Select(a => a.Name),
                name => name is not null
                    && (name.Contains("Akade", StringComparison.Ordinal)
                        || name.Contains("Chalk.Sample", StringComparison.Ordinal)));
        }
    }

    /// <summary><c>Chalk.Sources.Poco</c> is an adapter: it knows nothing about execution or planning.</summary>
    [Fact]
    public void The_poco_source_does_not_know_about_execution_or_the_client()
    {
        var references = typeof(PocoSourceBuilder).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

        Assert.DoesNotContain("Chalk.Execution", references);
        Assert.DoesNotContain("Chalk.Client", references);
    }

    /// <summary>
    /// Zero cost by construction, at the contract (step 26c, §5, D212): no message of a core proto
    /// file names a type from <c>entitlements.proto</c>, so a request built from the core cannot
    /// carry entitlement bytes however it is filled in.
    /// </summary>
    /// <remarks>
    /// Read off the compiled descriptors rather than the text, because that is what the wire is:
    /// every field of every message of every core file, and the files they import.
    /// </remarks>
    [Fact]
    public void No_core_message_names_a_type_from_the_entitlement_file()
    {
        Google.Protobuf.Reflection.FileDescriptor[] core =
        [
            Chalk.Client.Rpc.PlannerReflection.Descriptor,
            Chalk.Ir.PlanReflection.Descriptor,
            Chalk.Ir.ExprReflection.Descriptor,
            Chalk.Ir.CatalogReflection.Descriptor,
            Chalk.Ir.TypesReflection.Descriptor,
            Chalk.Ir.DialectReflection.Descriptor,
        ];

        var offenders = new List<string>();
        foreach (var file in core)
        {
            foreach (var dependency in file.Dependencies)
            {
                if (dependency.Name == EntitlementFile)
                {
                    offenders.Add($"{file.Name} imports {dependency.Name}");
                }
            }

            foreach (var message in Messages(file))
            {
                foreach (var field in message.Fields.InDeclarationOrder())
                {
                    var referenced = field.FieldType switch
                    {
                        Google.Protobuf.Reflection.FieldType.Message => field.MessageType.File.Name,
                        Google.Protobuf.Reflection.FieldType.Group => field.MessageType.File.Name,
                        Google.Protobuf.Reflection.FieldType.Enum => field.EnumType.File.Name,
                        _ => null,
                    };
                    if (referenced == EntitlementFile)
                    {
                        offenders.Add($"{message.FullName}.{field.Name}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>And the entitlement file is a real file with the messages the wrapper carries.</summary>
    [Fact]
    public void The_entitlement_file_is_where_the_entitlement_messages_are()
    {
        Assert.Equal(
            EntitlementFile,
            Chalk.Entitlements.Rpc.EntitlementsOptions.Descriptor.File.Name);
        Assert.Equal(
            EntitlementFile,
            Chalk.Entitlements.Rpc.EntitlementsReport.Descriptor.File.Name);

        // And it is compiled into the wrapper's assembly and no other, which is the other half of
        // "a core client cannot construct one".
        Assert.Equal(
            typeof(EntitledEngine).Assembly,
            typeof(Chalk.Entitlements.Rpc.EntitlementsOptions).Assembly);
    }

    private const string EntitlementFile = "chalk/v1/entitlements.proto";

    /// <summary>Every message of a file, nested ones included.</summary>
    private static IEnumerable<Google.Protobuf.Reflection.MessageDescriptor> Messages(
        Google.Protobuf.Reflection.FileDescriptor file)
    {
        var pending = new Stack<Google.Protobuf.Reflection.MessageDescriptor>(file.MessageTypes);
        while (pending.Count > 0)
        {
            var message = pending.Pop();
            yield return message;
            foreach (var nested in message.NestedTypes)
            {
                pending.Push(nested);
            }
        }
    }

    /// <summary>
    /// The entitlement wrapper's allowed references (step 26c, D213): the client it decorates, the
    /// catalog the descriptors live in, and the IR the plan is in. Nothing else of ours.
    /// </summary>
    [Fact]
    public void The_entitlement_wrapper_references_only_the_client_the_catalog_and_the_ir()
    {
        Assert.Equal(
            ["Chalk.Catalog", "Chalk.Client", "Chalk.Ir"],
            Ours(typeof(EntitledEngine).Assembly));
    }

    /// <summary>The tenancy package is layer B: those three, and the wrapper it declares for.</summary>
    /// <remarks>
    /// <para>
    /// A subset rather than an equality, because a reference the compiler finds no type used from is
    /// elided: what the rule is about is that nothing <em>else</em> of ours is reachable from here.
    /// </para>
    /// <para>
    /// <c>Chalk.Sources.Abstractions</c> joined the list with D271 (f): the <c>Source</c> and
    /// <c>Table</c> handles are refresh targets, and <c>IRefreshTarget</c> lives beside the rest of
    /// the refresh contract there — the one layer every source package, the client and this one can
    /// all see. It is below this package either way, since the client already references it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_tenancy_package_references_only_those_and_the_wrapper()
    {
        Assert.Empty(
            Ours(typeof(TenancyPolicy).Assembly)
                .Except(
                    [
                        "Chalk.Catalog",
                        "Chalk.Client",
                        "Chalk.Entitlements",
                        "Chalk.Ir",
                        "Chalk.Sources.Abstractions",
                    ],
                    StringComparer.Ordinal));
    }

    /// <summary>The Chalk.* assemblies one of ours references, sorted.</summary>
    private static string[] Ours(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("Chalk.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// D26 — public API types are sealed classes with <c>init</c> properties, or interfaces, or enums.
    /// Positional records cannot grow additively, so they are not allowed in a package a host pins.
    /// </summary>
    /// <remarks>
    /// Generated transport stubs are exempt (ADR 0010): <c>grpc_csharp_plugin</c> emits an unsealed
    /// <c>PlannerServiceClient</c>, and the rule is about the API Chalk designs, not the shape a code
    /// generator happens to emit.
    /// </remarks>
    [Theory]
    [InlineData("Chalk.Catalog")]
    [InlineData("Chalk.Sources.Abstractions")]
    [InlineData("Chalk.Client")]
    [InlineData("Chalk.Entitlements")]
    public void Public_types_are_sealed_or_abstract(string assemblyName)
    {
        var assembly = ClientAssemblies.Single(a => a.GetName().Name == assemblyName);

        var offenders = assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsSealed: false, IsAbstract: false })
            .Where(t => !IsGenerated(t))
            .Select(t => t.FullName!)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{assemblyName} exports unsealed, non-abstract classes: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The three small value types are the only positional records in the public API (D26): each is a
    /// closed tuple of primitives that will never grow a member, which is the one shape a positional
    /// record models honestly. Nothing else should have quietly become one.
    /// </summary>
    [Fact]
    public void Only_the_documented_value_types_are_records()
    {
        var recordStructs = ClientAssemblies
            .Where(a => a.GetName().Name is "Chalk.Catalog" or "Chalk.Sources.Abstractions" or "Chalk.Client")
            .SelectMany(a => a.GetExportedTypes())
            .Where(IsRecord)
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["Chalk.Catalog.ChalkType", "Chalk.Catalog.KeyOrder", "Chalk.Client.SqlPosition"],
            recordStructs);
    }

    /// <summary>
    /// Whether the compiler synthesised this type as a record. A record class gets a public
    /// <c>&lt;Clone&gt;$</c>; a record struct gets none, and is recognised instead by the
    /// <em>private</em> <c>PrintMembers</c> every record has — which is why the binding flags matter.
    /// </summary>
    private static bool IsRecord(ClrType type) =>
        type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            is not null
        || type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(m => m.Name == "PrintMembers");

    /// <summary>Whether a code generator emitted this type, or the type that declares it.</summary>
    private static bool IsGenerated(ClrType type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            if (t.GetCustomAttribute<GeneratedCodeAttribute>() is not null)
            {
                return true;
            }
        }

        // grpc_csharp_plugin marks the members it generates but not the client class that holds
        // them, so that one is recognised by what it is rather than by an attribute it lacks.
        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            if (b.FullName?.StartsWith("Grpc.Core.ClientBase", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }
}
