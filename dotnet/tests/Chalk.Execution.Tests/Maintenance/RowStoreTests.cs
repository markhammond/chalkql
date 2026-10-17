using System.Text;
using Chalk.Catalog;
using Chalk.Execution.Maintenance;
using Chalk.Sources;

namespace Chalk.Execution.Tests.Maintenance;

/// <summary>
/// The rows a maintained operator keeps (D343): slots added, removed and reused at random hold what
/// was put in them, across the compactions of their strings' buffer.
/// </summary>
public sealed class RowStoreTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Slots_hold_what_was_put_in_them(int seed)
    {
        var random = new Random(seed);
        var store = new RowStore([ChalkType.String(nullable: true), ChalkType.Int64()]);
        var held = new Dictionary<int, (string? Text, long Number)>();
        for (var step = 0; step < 20_000; step++)
        {
            if (held.Count > 0 && random.NextDouble() < 0.45)
            {
                var slot = held.Keys.ElementAt(random.Next(held.Count));
                store.Remove(slot);
                held.Remove(slot);
                continue;
            }

            string? text = random.Next(8) switch
            {
                0 => null,
                1 => string.Empty,
                _ => new string('x', random.Next(0, 300)) + step,
            };
            var number = random.NextInt64();
            var columns = Columns(text, number);
            var added = store.Add(columns, 0);
            Assert.False(held.ContainsKey(added), $"step {step}: slot {added} given out twice");
            held[added] = (text, number);

            if (step % 50 == 0)
            {
                foreach (var (slot, (expectedText, expectedNumber)) in held)
                {
                    var column = store.Column(0);
                    Assert.Equal(expectedText is not null, column.IsValid(slot));
                    if (expectedText is not null)
                    {
                        Assert.Equal(expectedText, Encoding.UTF8.GetString(column.Bytes(slot)));
                    }

                    Assert.Equal(expectedNumber, BitConverter.ToInt64(store.Column(1).Lane(slot)));
                }
            }
        }
    }

    private static ColumnView[] Columns(string? text, long number)
    {
        var bytes = text is null ? [] : Encoding.UTF8.GetBytes(text);
        var offsets = new byte[8];
        BitConverter.GetBytes(bytes.Length).CopyTo(offsets, 4);
        return
        [
            new ColumnView
            {
                Type = ChalkType.String(nullable: true),
                Length = 1,
                Values = bytes,
                Offsets = offsets,
                Validity = text is null ? new byte[] { 0 } : default,
                NullCount = text is null ? 1 : 0,
            },
            new ColumnView { Type = ChalkType.Int64(), Length = 1, Values = BitConverter.GetBytes(number) },
        ];
    }
}
