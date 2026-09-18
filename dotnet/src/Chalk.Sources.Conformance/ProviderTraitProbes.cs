using Chalk.Sources.Ado;

namespace Chalk.Sources.Conformance;

/// <summary>
/// D263 (ADR 0046 §3): what <c>Chalk.Sources.Ado</c>'s reader would do for each of this source's
/// STRING data types — declared through <see cref="AdoProviderTraits"/>, or measured — so a host
/// reading the report can declare it next time instead of paying the probe next run.
/// </summary>
/// <remarks>
/// Runs only against an <see cref="AdoSource"/>: every other <see cref="ISourceRuntime"/> — a POCO
/// source, a host's own — has no text strategy to report, and reports nothing here. Not gated by
/// <see cref="ConformanceChecks"/>: it never fails, so it is reporting rather than a check, exactly
/// like <see cref="ConformanceReport.Server"/>.
/// </remarks>
internal static class ProviderTraitProbes
{
    public static async Task<IReadOnlyList<ConformanceFinding>> RunAsync(
        ISourceRuntime source, string table, CancellationToken ct)
    {
        if (source is not AdoSource ado)
        {
            return [];
        }

        IReadOnlyList<AdoTextStrategyReport> measured;
        try
        {
            measured = await ado.ProbeTextStrategiesAsync(table, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Reported, not thrown: a probe that cannot run is not a failure of the checks that
            // did (D88's "never throws for a failed check").
            return
            [
                new ConformanceFinding
                {
                    Subject = "text strategy",
                    Declared = "(varies by data type)",
                    Observed = $"could not be measured: {failure.Message}",
                    Outcome = ConformanceOutcome.Skipped,
                },
            ];
        }

        if (measured.Count == 0)
        {
            return [];
        }

        var findings = new List<ConformanceFinding>(measured.Count);
        foreach (var report in measured.OrderBy(r => r.DataType, StringComparer.Ordinal))
        {
            findings.Add(new ConformanceFinding
            {
                Subject = $"text strategy: {report.DataType}",
                Declared = report.Declared ? report.Strategy.ToString() : "(undeclared)",
                Observed = report.Strategy.ToString(),
                Outcome = ConformanceOutcome.Skipped,
                Advice = report.Declared
                    ? string.Empty
                    : "Declare .Provider(new AdoProviderTraits { Text = TextStrategy."
                        + report.Strategy + " }) on this source's builder to skip the measurement.",
            });
        }

        return findings;
    }
}
