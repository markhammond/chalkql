using Microsoft.Extensions.Logging;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// The host's memory budget: the bytes it may hold, and, until one is chosen on the page or given at
/// start, the steps it raises itself through rather than refuse for want of it
/// (<see cref="BlotterOptions.BudgetSteps"/>). It never lowers itself; what gives way when the host is
/// past it is the host's to decide.
/// </summary>
public sealed class HostBudget(BlotterOptions options, ILogger log)
{
    private long _bytes = options.BudgetBytes;
    private bool _finding = options.FindBudget;

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>A budget chosen on the page: from now on, a refusal for want of budget is a refusal.</summary>
    public void Choose(long bytes)
    {
        Interlocked.Exchange(ref _bytes, Math.Max(1L << 20, bytes));
        _finding = false;
    }

    /// <summary>While no budget is chosen, raises it to the next step, if there is one, saying why.</summary>
    public bool TryRaise(string why)
    {
        var next = options.BudgetSteps.FirstOrDefault(step => step > Bytes);
        if (!_finding || next == 0)
        {
            return false;
        }

        log.LogInformation("raising the budget from {From} to {To}: {Why}", BlotterHost.Megabytes(Bytes), BlotterHost.Megabytes(next), why);
        Interlocked.Exchange(ref _bytes, next);
        return true;
    }

    /// <summary>
    /// The rows the kept windows may hold: their share of the budget, or what the host's other holdings
    /// (<paramref name="liveBytes"/>) leave, whichever is less.
    /// </summary>
    public long PageRows(long liveBytes)
    {
        var left = Math.Max(0, Bytes - liveBytes);
        var share = (long)(Bytes * options.PageShare);
        return Math.Min(left, share) / Order.Bytes;
    }
}
