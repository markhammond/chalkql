namespace Chalk.Sample.Blotter.Fixtures;

/// <summary>
/// The history the sample starts with: client orders worked over the days before it starts, each
/// worked out from its number alone, so a corpus of one size is the same corpus every time and any
/// part of it can be made on its own, in parallel with the rest.
/// </summary>
/// <remarks>
/// An order is filled in up to three fills that split its lots, at prices around a mid with its
/// channel's markup on top; its average price is the fills', weighted; one in fifty is cancelled with
/// some of its lots unfilled. The newest few are left new, for the exchange to work from its first
/// second. Ids are version 7 UUIDs: the time they were made, then bits hashed from the order's number.
/// </remarks>
public static class Corpus
{
    private static readonly double[] ClientWeights = [.. Reference.Clients.Select((_, i) => 1.0 / (i + 1))];
    private static readonly double[] AssetWeights = [.. Reference.Assets.Select(a => a.Weight)];

    /// <summary>Order <paramref name="n"/>: its versions oldest first, and its fills.</summary>
    public static (Order[] Versions, Fill[] Fills) Make(long n, DateTime origin, long spacingMicros, bool open)
    {
        var u = (ulong)n;
        var rank = 1 + Pick(ClientWeights, Uniform(u, 1));
        var (reference, _, region) = Reference.Clients[rank - 1];
        var channels = Reference.ChannelsOf(rank);
        var channel = string.Intern(channels[Pick([.. channels.Select(c => c.Weight)], Uniform(u, 2))].Name);
        var asset = Pick(AssetWeights, Uniform(u, 3));
        var (_, _, lot, price) = Reference.Assets[asset];
        var side = Reference.Sides[Uniform(u, 4) < 0.5 ? 0 : 1];
        var lots = 1 + (int)(Uniform(u, 5) * 6);
        var fills = open ? 0 : 1 + (int)(Uniform(u, 6) * 3);
        var cancelled = !open && Uniform(u, 7) < 0.02;
        var px = price * (1 + (Uniform(u, 8) - 0.5) * 0.2);
        var markup = Reference.MarkupBps(channel);
        var time = origin.AddTicks(n * spacingMicros * 10);
        var id = UuidV7(time, u);
        var clOrdId = $"{reference}-{n}";

        var first = new Order(
            id, 1, time, time, clOrdId, reference, region, channel, Reference.Instruments[asset], side,
            lots * lot, 0, 0, 0, markup, Reference.Statuses[0]);
        var versions = new List<Order>(5) { first };
        var made = new List<Fill>(3);
        var fillable = lots * (cancelled ? 0.6 : 1);
        double filled = 0, cost = 0, amount = 0;
        for (var k = 1; k <= fills; k++)
        {
            var share = Math.Floor(fillable * k / fills) - Math.Floor(fillable * (k - 1) / fills);
            if (share <= 0)
            {
                continue;
            }

            var at = time.AddMilliseconds(750 * k);
            var qty = share * lot;
            var mid = Math.Round(px * (1 + (Uniform(u, 10 + (ulong)k) - 0.5) * 0.002), 6);
            var fillPrice = Math.Round(mid * (1 + (side == "Buy" ? 1 : -1) * markup / 10_000), 4);
            var amt = Math.Round(qty * fillPrice, 2);
            filled += qty;
            cost += qty * fillPrice;
            amount = Math.Round(amount + amt, 2);
            made.Add(new Fill(
                UuidV7(at, (u << 2) + (ulong)k), id, at, clOrdId, reference, region, channel, first.Instrument, side,
                qty, fillPrice, amt, mid, markup));
            versions.Add(versions[^1] with
            {
                Version = versions.Count + 1,
                EventTime = at,
                FilledQty = filled,
                AvgPrice = Math.Round(cost / filled, 4),
                FilledAmt = amount,
                Status = filled >= first.Qty - 1e-9 ? Reference.Statuses[2] : Reference.Statuses[1],
            });
        }

        if (cancelled)
        {
            versions.Add(versions[^1] with
            {
                Version = versions.Count + 1,
                EventTime = time.AddMilliseconds(750 * (fills + 1)),
                Status = Reference.Statuses[3],
            });
        }

        return ([.. versions], [.. made]);
    }

    /// <summary>
    /// A version 7 UUID for <paramref name="time"/>: its Unix milliseconds, the version, twelve bits and
    /// then sixty-two hashed from <paramref name="n"/>, and the variant.
    /// </summary>
    public static Guid UuidV7(DateTime time, ulong n)
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = (ulong)(time - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMillisecond;
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(milliseconds >> (8 * (5 - i)));
        }

        var a = Mix(n * 0x9E37 + 71);
        var b = Mix(n * 0x9E37 + 73);
        bytes[6] = (byte)(0x70 | (int)(a & 0x0F));
        bytes[7] = (byte)(a >> 8);
        bytes[8] = (byte)(0x80 | (int)(b & 0x3F));
        for (var i = 9; i < 16; i++)
        {
            bytes[i] = (byte)(b >> (8 * (i - 8)));
        }

        return new Guid(bytes, bigEndian: true);
    }

    private static double Uniform(ulong n, ulong salt) => (Mix(n * 0x100 + salt) >> 11) * (1.0 / (1UL << 53));

    /// <summary>SplitMix64: a hash good enough to stand in for a random draw, and the same every time.</summary>
    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return x ^ (x >> 31);
    }

    private static int Pick(double[] weights, double uniform)
    {
        var r = uniform * weights.Sum();
        for (var i = 0; i < weights.Length - 1; i++)
        {
            if ((r -= weights[i]) < 0)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }
}
