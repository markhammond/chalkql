using Chalk.Sources;

namespace Chalk.Execution.Tests;

/// <summary>
/// What the executor stamps on a remote request from the host's per-source settings (D86): the
/// host's entry when there is one, and nothing at all when there is not — so that a source the host
/// never named runs under the timeout its own builder declared rather than under a default the
/// executor chose for it.
/// </summary>
public sealed class ExecutionSettingsTests
{
    [Fact]
    public void A_source_the_host_named_carries_the_hosts_timeout()
    {
        var settings = new ExecutionSettings
        {
            SourceOptions = new Dictionary<string, SourceOptions>(StringComparer.Ordinal)
            {
                ["warehouse"] = new() { QueryTimeout = TimeSpan.FromMinutes(2) },
            },
        };

        Assert.Equal(TimeSpan.FromMinutes(2), settings.SourceTimeout("warehouse"));
    }

    /// <summary>
    /// The request for a source the host said nothing about carries no timeout: the source applies
    /// its builder's. Before this, the executor filled in thirty seconds and the builder's own
    /// <c>QueryTimeout</c> never reached a pushed query.
    /// </summary>
    [Fact]
    public void A_source_the_host_did_not_name_carries_no_timeout()
    {
        var settings = new ExecutionSettings();

        Assert.Null(settings.SourceTimeout("warehouse"));
    }
}
