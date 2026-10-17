using System.Text;
using Chalk.Execution.Maintenance;

namespace Chalk.Execution.Tests.Maintenance;

/// <summary>
/// The byte-keyed map maintained operators keep (D343): keys added, found and removed at random give
/// what a dictionary gives, across its rehashes and compactions.
/// </summary>
public sealed class ByteMapTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Keys_map_as_a_dictionary_maps_them(int seed)
    {
        var random = new Random(seed);
        var map = new ByteMap();
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var step = 0; step < 50_000; step++)
        {
            var key = "k" + random.Next(3_000) + new string('.', random.Next(40));
            var bytes = Encoding.UTF8.GetBytes(key);
            switch (random.Next(4))
            {
                case 0:
                    Assert.Equal(expected.Remove(key, out var was), map.Remove(bytes, out var removed));
                    if (expected.ContainsKey(key) is false && was != 0)
                    {
                        Assert.Equal(was, removed);
                    }

                    break;
                case 1:
                    ref var value = ref map.GetOrAdd(bytes, out var added);
                    Assert.Equal(!expected.ContainsKey(key), added);
                    if (!added)
                    {
                        Assert.Equal(expected[key], value);
                    }

                    value = step;
                    expected[key] = step;
                    break;
                default:
                    Assert.Equal(expected.TryGetValue(key, out var held), map.TryGet(bytes, out var found));
                    if (expected.ContainsKey(key))
                    {
                        Assert.Equal(held, found);
                    }

                    break;
            }

            Assert.Equal(expected.Count, map.Count);
            if (step % 5_000 == 0)
            {
                var live = 0;
                for (var entry = 0; entry < map.Entries; entry++)
                {
                    if (map.IsLive(entry))
                    {
                        live++;
                        Assert.Equal(expected[Encoding.UTF8.GetString(map.Key(entry))], map.Value(entry));
                    }
                }

                Assert.Equal(expected.Count, live);
            }
        }
    }
}
