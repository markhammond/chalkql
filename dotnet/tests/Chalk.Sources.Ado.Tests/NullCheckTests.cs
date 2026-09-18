using System.Data.Common;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// D263 (ADR 0046 §1): whether <c>IsDBNull</c> is asked synchronously is decided by reflection over
/// <c>IsDBNullAsync</c>'s declaring type, not by a provider's name.
/// </summary>
/// <remarks>
/// Against the two fakes the suite already has rather than new ones: <see cref="FakeReader"/>
/// (<c>FakeProvider.cs</c>) never overrides <c>IsDBNullAsync</c>, and <see cref="TextReader"/>
/// (<c>TextStrategyTests.cs</c>) does — so between them they are exactly the "a subclass that
/// overrides <c>IsDBNullAsync</c> and one that does not" the decision asks for, with no new
/// <c>DbDataReader</c> double to keep in sync with the real ones.
/// </remarks>
public sealed class NullCheckTests
{
    [Fact]
    public void A_reader_that_does_not_override_IsDBNullAsync_resolves_to_the_synchronous_path()
    {
        Assert.True(AdoBatchReader.NullCheckIsSynchronous(typeof(FakeReader)));

        // The base type's own IsDBNullAsync trivially "resolves to DbDataReader's own declaration":
        // it is that declaration.
        Assert.True(AdoBatchReader.NullCheckIsSynchronous(typeof(DbDataReader)));
    }

    [Fact]
    public void A_reader_that_overrides_IsDBNullAsync_resolves_to_the_asynchronous_path()
    {
        Assert.False(AdoBatchReader.NullCheckIsSynchronous(typeof(TextReader)));
    }

    /// <summary>
    /// The reflection is memoised per type (D263): asking twice about a type that overrides the
    /// method, and twice about one that does not, must keep agreeing with itself.
    /// </summary>
    [Fact]
    public void The_answer_is_stable_across_repeated_questions_about_the_same_type()
    {
        Assert.True(AdoBatchReader.NullCheckIsSynchronous(typeof(FakeReader)));
        Assert.True(AdoBatchReader.NullCheckIsSynchronous(typeof(FakeReader)));
        Assert.False(AdoBatchReader.NullCheckIsSynchronous(typeof(TextReader)));
        Assert.False(AdoBatchReader.NullCheckIsSynchronous(typeof(TextReader)));
    }
}
