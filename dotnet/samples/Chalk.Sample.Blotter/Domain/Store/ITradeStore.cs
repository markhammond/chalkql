namespace Chalk.Sample.Blotter.Domain;

/// <summary>How <c>orders_current</c> keeps up with <c>order_versions</c>.</summary>
public enum CurrentSync
{
    /// <summary>A trigger on <c>order_versions</c> upserts the order's row in the statement that writes the version.</summary>
    Trigger,

    /// <summary>A job of its own applies the versions some time after they are written, and stalls now and then.</summary>
    Lagging,
}

/// <summary>
/// The system of record, as the simulated house writes it: every version of every client order
/// (<c>order_versions</c>), every fill (<c>fills</c>), and <c>orders_current</c>, one row per order at its
/// latest version. A real deployment has none of this: someone else's system writes the tables, and
/// the host only reads them (<see cref="Live.IBlotterProvider"/>).
/// </summary>
/// <remarks>
/// A corpus is best loaded before any index or trigger exists: <see cref="CreateTables"/>, then
/// <see cref="BeginLoad"/> on as many writers as suit, then <see cref="CreateIndexesAsync"/> and
/// <see cref="KeepCurrent"/>. Text must be compared byte by byte, whatever the database's own
/// collation: the host merges the database's rows with its own and orders text exactly as the
/// database does.
/// </remarks>
public interface ITradeStore
{
    /// <summary>Creates the three tables, empty, with no index or trigger yet.</summary>
    void CreateTables();

    /// <summary>The keys and the indexes the blotter reads by, and whatever the planner needs to know of them.</summary>
    Task CreateIndexesAsync(CancellationToken ct);

    /// <summary>From now on keeps <c>orders_current</c> as <paramref name="sync"/> says: by a trigger, or left to a job.</summary>
    void KeepCurrent(CurrentSync sync);

    /// <summary>A bulk load into all three tables, one of several that may run side by side.</summary>
    ITradeLoad BeginLoad();

    /// <summary>The orders still open, latest version each, for the exchange to work.</summary>
    List<Order> OpenOrders();

    /// <summary>Writes order versions as an ingestion job would; under the trigger, <c>orders_current</c> moves with them.</summary>
    void WriteOrders(IReadOnlyList<Order> versions);

    void WriteFills(IReadOnlyList<Fill> fills);

    /// <summary>What the lagging job does with a batch: each order's row moves to the newest version in it, never back.</summary>
    void UpsertCurrent(IReadOnlyList<Order> versions);
}

/// <summary>One writer of a bulk load: rows added, then completed; disposed without completing, nothing is kept.</summary>
public interface ITradeLoad : IDisposable
{
    void AddVersion(Order version);

    /// <summary>An order's row in <c>orders_current</c>: its latest version.</summary>
    void AddCurrent(Order order);

    void AddFill(Fill fill);

    void Complete();
}
