using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Chalk.Benchmarks.Akade;

internal static class Program
{
    /// <summary>
    /// The command line is BenchmarkDotNet's, so one case can be measured on its own:
    /// <c>dotnet run -c Release -- --filter "*band*"</c>. A quick look is <c>-- --job short</c>.
    /// </summary>
    private static int Main(string[] args)
    {
        BenchmarkRunner.Run<AkadeOverheadBenchmarks>(
            DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator), args);
        return 0;
    }
}
