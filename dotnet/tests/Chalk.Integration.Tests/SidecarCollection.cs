using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// One sidecar for the whole assembly. Starting a JVM is not free and nothing in these tests mutates
/// the planner, so a single instance serves them all; <c>DeterminismTests</c> owns a private one
/// because it restarts the process (docs/design/05-testing.md §9).
/// </summary>
public sealed class SharedSidecar : IAsyncLifetime
{
    private SidecarFixture? _sidecar;

    public SidecarFixture Sidecar =>
        _sidecar ?? throw new InvalidOperationException("the sidecar fixture has not been initialised");

    /// <summary>Non-null when no sidecar could be started; tests report it instead of failing.</summary>
    public string? SkipReason => Sidecar.SkipReason;

    public async ValueTask InitializeAsync() => _sidecar = await SidecarFixture.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_sidecar is not null)
        {
            await _sidecar.DisposeAsync();
        }
    }

    /// <summary>A planner talking to the shared sidecar. The caller disposes it.</summary>
    public IQueryPlanner CreatePlanner() => Sidecar.CreatePlanner();
}

/// <summary>
/// One PostgreSQL server for the whole test run (D133, docs/design/20-m5-federation.md §0b), created
/// on first use so a run that never asks for one pays nothing, and stopped when the collection ends.
/// </summary>
/// <remarks>
/// It hangs off the same collection as the sidecar because a collection fixture is per collection
/// and a test class belongs to exactly one: the federation corpus wants both, and every integration
/// class that touches a remote source is already here.
/// </remarks>
public sealed class SharedPostgres : IDisposable
{
    private readonly Lock _gate = new();
    private PostgresFixture? _postgres;
    private bool _disposed;

    /// <summary>The server, started on first access. Its <c>SkipReason</c> says why if there is none.</summary>
    public PostgresFixture Fixture
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _postgres ??= PostgresFixture.Start();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _postgres?.Dispose();
            _postgres = null;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SidecarCollection
    : ICollectionFixture<SharedSidecar>,
      ICollectionFixture<SharedPostgres>,
      ICollectionFixture<PolicyEngines>
{
    public const string Name = "sidecar";
}
