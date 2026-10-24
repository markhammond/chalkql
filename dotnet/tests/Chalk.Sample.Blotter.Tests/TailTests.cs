using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>How the tail keeps each row's wait for the database: when it starts, moves on, stops, and goes.</summary>
public sealed class TailTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 9, 0, 0);

    [Fact]
    public void An_order_waits_from_the_oldest_version_the_database_does_not_hold_until_a_lookup_finds_it()
    {
        var tail = new Tail();
        var v1 = Order(1, Now);
        var entry = new TailEntry(v1, null);
        tail.Set(entry, v1);
        var v2 = Order(2, Now.AddSeconds(1));
        entry.Live = v2;
        tail.Set(entry, v2);
        Assert.Equal(TimeSpan.FromSeconds(3), tail.Reconciling[v1.OrderId].Lag(Now.AddSeconds(3)));

        // The database now holds v1: the order has waited since v2.
        tail.Rebase(entry, v1);
        Assert.Equal(TimeSpan.FromSeconds(2), tail.Reconciling[v1.OrderId].Lag(Now.AddSeconds(3)));

        // Found at v2: the lag stops, and stays, until the row leaves.
        tail.Confirm(entry, Now.AddSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(3), tail.Reconciling[v1.OrderId].Lag(Now.AddSeconds(60)));
        Assert.Same(v2, tail.Reconciling[v1.OrderId].Row);

        // A newer version starts a new wait.
        var v3 = Order(3, Now.AddSeconds(5));
        entry.Live = v3;
        tail.Set(entry, v3);
        Assert.Equal((Now.AddSeconds(5), (DateTime?)null), (tail.Reconciling[v1.OrderId].Since, tail.Reconciling[v1.OrderId].At));

        tail.Remove(v1.OrderId);
        Assert.Empty(tail.Reconciling);
    }

    [Fact]
    public void A_fill_waits_from_its_own_time_and_the_wait_goes_with_it()
    {
        var tail = new Tail();
        var fill = new Fill(Guid.CreateVersion7(), Guid.CreateVersion7(), Now, "CEDAR-1", "CEDAR", "EMEA", "API", "BTC/USD Perp", "Buy", 1, 100, 100, 0);
        tail.AddFill(fill);
        tail.ConfirmFill(fill.FillId, Now.AddSeconds(2));
        tail.ConfirmFill(fill.FillId, Now.AddSeconds(9));
        Assert.Equal(TimeSpan.FromSeconds(2), tail.Reconciling[fill.FillId].Lag(Now.AddSeconds(30)));
        Assert.Null(tail.OldestUnconfirmed);

        tail.RemoveFill(fill);
        Assert.Empty(tail.Reconciling);
    }

    private static readonly Guid Id = Guid.CreateVersion7();

    private static Order Order(int version, DateTime at) =>
        new(Id, version, at, Now, "CEDAR-1", "CEDAR", "EMEA", "API", "BTC/USD Perp", "Buy", 2, 0, 0, 0, "New");
}
