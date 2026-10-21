namespace Chalk.Integration.Tests;

/// <summary>
/// Tests that compare two timings, run alone after the rest: beside other test classes the machine is
/// too busy for one timing to say anything about another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "timing";
}
