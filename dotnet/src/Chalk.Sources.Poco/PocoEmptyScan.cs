using Chalk.Sources;

namespace Chalk.Sources.Poco;

/// <summary>A scan with no rows: what a maintained view reads of a table nothing was appended to (D343).</summary>
internal sealed class PocoEmptyScan : IColumnarScan
{
    public static readonly PocoEmptyScan Instance = new();

    private PocoEmptyScan()
    {
    }

    public bool TryNext(ColumnarBatch batch) => false;

    public ValueTask<bool> WaitAsync(ColumnarBatch batch, CancellationToken ct) => ValueTask.FromResult(false);

    public ValueTask DisposeAsync() => default;
}
