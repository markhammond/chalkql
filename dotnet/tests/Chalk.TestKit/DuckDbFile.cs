namespace Chalk.TestKit;

/// <summary>
/// A private DuckDB database for one fixture, as a file under the test temp root (D133 §0c).
/// </summary>
/// <remarks>
/// <para>
/// <c>DataSource=:memory:name</c> looks like DuckDB's own spelling of a named in-memory database and
/// is not one to DuckDB.NET 1.5.5: the provider passes the whole string to <c>duckdb_open</c> as a
/// path, so it opens a file literally called <c>:memory:name</c> in the process's working directory.
/// The fixtures were writing 3.4 MB databases and 7.8 MB write-ahead logs into
/// <c>bin/Release/net10.0</c> and, when a tool ran from elsewhere, into the working tree itself —
/// which is the same defect the M4 fix for the <c>?cache=shared&amp;mode=memory</c> spelling was meant
/// to close, one spelling further on.
/// </para>
/// <para>
/// So: an ordinary file, with an ordinary name, in a directory nothing reviews — deleted with its
/// <c>.wal</c> when the fixture is done, and asserted to be under that root before anything opens it.
/// A test suite should leave <c>git status --porcelain</c> empty, and now the only thing standing
/// between it and a blob in a commit is not a <c>.gitignore</c> pattern that has to guess the name.
/// </para>
/// </remarks>
public sealed class DuckDbFile : IDisposable
{
    private static readonly Lock Gate = new();
    private static readonly List<DuckDbFile> Live = [];
    private static bool _sweeperInstalled;

    private bool _disposed;

    private DuckDbFile(string path)
    {
        Path = path;
        ConnectionString = "DataSource=" + path;
    }

    /// <summary>The database file. Always under <see cref="RepoLayout.TestTemp"/>.</summary>
    public string Path { get; }

    /// <summary>The connection string DuckDB.NET takes for it.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// A new database named after <paramref name="label"/>, so a stray file says which fixture left
    /// it. The caller disposes it; nothing here survives the machine's own temp cleaning if it does not.
    /// </summary>
    public static DuckDbFile Create(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var path = RepoLayout.TestTempPath($"{label}-{Guid.NewGuid():N}.duckdb");

        // Checked where it is established, not only by the repository being clean afterwards.
        if (!RepoLayout.IsUnderTestTemp(path))
        {
            throw new ChalkTestSetupException(
                $"the DuckDB fixture file '{path}' is not under the test temp root "
                + $"'{RepoLayout.TestTemp.FullName}'; refusing to open it.");
        }

        var file = new DuckDbFile(path);
        lock (Gate)
        {
            Live.Add(file);
            if (!_sweeperInstalled)
            {
                _sweeperInstalled = true;

                // A fixture held in a static `Lazy` is never disposed by anything -- RemoteFixture
                // is exactly that -- so "deleted on dispose" alone would leave a 7.8 MB write-ahead
                // log per run under the temp root. Unlinking a file another handle still has open is
                // what an operating system is for; on Windows it fails and Delete swallows it, which
                // is the same outcome the temp directory would have reached on its own.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
            }
        }

        return file;
    }

    /// <summary>Deletes every database this process created and has not disposed.</summary>
    public static void DisposeAll()
    {
        DuckDbFile[] outstanding;
        lock (Gate)
        {
            outstanding = [.. Live];
        }

        foreach (var file in outstanding)
        {
            file.Dispose();
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Live.Remove(this);
        }

        Delete(Path);
        Delete(Path + ".wal");
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort. The file is under the machine's temp root, which is swept anyway, and a
            // fixture that cannot delete its own database must not fail the test that used it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
