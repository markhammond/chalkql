using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Sources;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Client;

/// <summary>
/// Turns <c>ChalkEngineOptions.Functions</c> into what the engine reads, and checks it against the
/// catalog before an engine exists (D77, D79).
/// </summary>
internal static class HostFunctions
{
    /// <summary>Runs the host's registration callback once, and freezes the result.</summary>
    public static HostFunctionSet Build(Action<IFunctionRegistry>? configure)
    {
        if (configure is null)
        {
            return HostFunctionSet.Empty;
        }

        var registry = new FunctionRegistry();
        configure(registry);
        return registry.Freeze();
    }

    /// <summary>
    /// Every client-bodied function the catalog declares has an implementation whose CLR types match
    /// what it declares. A native or SQL body needs nothing from the host and is skipped.
    /// </summary>
    public static void CheckCatalog(CatalogContext catalog, HostFunctionSet functions)
    {
        foreach (var schema in catalog.Schemas)
        {
            foreach (var function in schema.Functions)
            {
                if (function.Body is not ClientFunctionBody)
                {
                    continue;
                }

                var host = UserFunctionBinding.Find(functions, function);
                UserFunctionBinding.Check(function, host, $"schema '{schema.Name}'");
            }
        }
    }
}
