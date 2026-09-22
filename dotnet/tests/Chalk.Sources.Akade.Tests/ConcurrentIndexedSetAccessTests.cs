using Akade.IndexedSet;

namespace Chalk.Sources.Akade.Tests;

public sealed class ConcurrentIndexedSetAccessTests
{
    [Fact]
    public void Capture_returns_the_wrapped_indexed_set()
    {
        var concurrent = IndexedSetBuilder.Create(new[] { 1, 2, 3 })
            .WithIndex(x => x)
            .BuildConcurrent();

        var inner = ConcurrentIndexedSetAccess.CaptureForQuiescentRead(concurrent);

        Assert.Equal(3, inner.Count);
        Assert.Equal([1, 2, 3], inner.FullScan().Order().ToArray());
    }
}
