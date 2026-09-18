using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Chalk.TestKit;

/// <summary>
/// A PostgreSQL server for the duration of one test run: an ephemeral cluster this fixture creates
/// with <c>initdb</c>, starts with <c>pg_ctl</c>, and deletes on dispose (D133,
/// docs/design/20-m5-federation.md §0b).
/// </summary>
/// <remarks>
/// <para>
/// Not a service container and not Testcontainers. PostgreSQL ships as ordinary binaries that run
/// as an ordinary process, and every machine this project cares about already has them or can get
/// them from its own package manager: GitHub's <c>ubuntu-24.04</c> image carries PostgreSQL 16 with
/// the service disabled, and the developer machines carry a Homebrew keg. Starting the postmaster
/// directly is faster than a container, needs no daemon, and touches no network — the cluster
/// listens on a loopback port and has its Unix socket directory switched off entirely.
/// </para>
/// <para>
/// When no binaries can be found the fixture <em>skips with a reason that names what to install</em>,
/// which is visible in the test output and in the conformance report. It is never silent, and
/// <c>CHALK_TEST_POSTGRES_REQUIRED=1</c> turns the skip into a failure so CI cannot lose the leg
/// quietly.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IDisposable
{
    /// <summary>The role every connection uses. <c>--auth=trust</c>, so there is no password.</summary>
    public const string User = "postgres";

    private readonly string? _dataDirectory;
    private readonly string? _binDirectory;
    private bool _disposed;

    private PostgresFixture(
        string? connectionString,
        string? skipReason,
        string serverVersion,
        string? dataDirectory,
        string? binDirectory,
        string origin)
    {
        ConnectionString = connectionString;
        SkipReason = skipReason;
        ServerVersion = serverVersion;
        Origin = origin;
        _dataDirectory = dataDirectory;
        _binDirectory = binDirectory;
    }

    /// <summary>
    /// A connection string for the <c>postgres</c> database, or null when <see cref="SkipReason"/>
    /// says why there is none.
    /// </summary>
    public string? ConnectionString { get; }

    /// <summary>
    /// Why there is no server, naming what to install — or null when there is one. Non-null is a
    /// skip, unless <c>CHALK_TEST_POSTGRES_REQUIRED=1</c>, in which case the fixture throws instead.
    /// </summary>
    public string? SkipReason { get; }

    /// <summary>
    /// The server's version, as the report records it: <c>"PostgreSQL 16.13"</c> from the binaries
    /// this fixture started, or an empty string for a server named by <c>CHALK_TEST_POSTGRES</c>,
    /// where the connection itself is the only thing that knows.
    /// </summary>
    public string ServerVersion { get; }

    /// <summary>Where the server came from, for the skip/diagnostic line.</summary>
    public string Origin { get; }

    /// <summary>Whether this fixture started the server (and will stop it) rather than borrowing one.</summary>
    public bool IsEphemeral => _dataDirectory is not null;

    /// <summary>
    /// Starts a cluster, or explains why it could not. Never throws for a missing PostgreSQL unless
    /// <c>CHALK_TEST_POSTGRES_REQUIRED=1</c> is set.
    /// </summary>
    public static PostgresFixture Start()
    {
        var required = Environment.GetEnvironmentVariable("CHALK_TEST_POSTGRES_REQUIRED") == "1";
        PostgresFixture fixture;
        try
        {
            fixture = StartCore();
        }
        catch (Exception failure) when (failure is not ChalkTestSetupException)
        {
            fixture = Unavailable(
                $"PostgreSQL could not be started: {failure.Message}", "a start that failed");
        }

        if (required && fixture.SkipReason is { } why)
        {
            fixture.Dispose();
            throw new ChalkTestSetupException(
                "CHALK_TEST_POSTGRES_REQUIRED=1, so the PostgreSQL leg may not be skipped. " + why);
        }

        return fixture;
    }

    private static PostgresFixture StartCore()
    {
        // 1. An existing server named by the environment wins outright: a host that has one already
        //    running (a developer's own, a CI service) should not have a second started for it.
        var existing = Environment.GetEnvironmentVariable("CHALK_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return new PostgresFixture(
                existing, null, string.Empty, null, null, "CHALK_TEST_POSTGRES");
        }

        // 2-6. Binaries, in the order §0b sets out.
        var (bin, origin) = FindBinaries();
        if (bin is null)
        {
            return Unavailable(InstallAdvice(), origin);
        }

        var data = RepoLayout.TestTempPath("pg-" + Guid.NewGuid().ToString("N")[..12]);
        if (!RepoLayout.IsUnderTestTemp(data))
        {
            throw new ChalkTestSetupException(
                $"the data directory '{data}' is not under the test temp root; refusing to initdb there.");
        }

        Directory.CreateDirectory(data);
        var log = Path.Combine(data, "postmaster.log");

        Run(
            Path.Combine(bin, "initdb"),
            ["-D", data, "-U", User, "--auth=trust", "--no-sync", "-E", "UTF8", "--locale=C"],
            "initdb");

        var port = FreeLoopbackPort();
        var options = string.Join(
            ' ',
            "-p " + port.ToString(CultureInfo.InvariantCulture),
            "-c listen_addresses=127.0.0.1",

            // No Unix socket at all. The cluster is reachable only over loopback TCP on a port this
            // process chose, so two runs on one machine cannot collide through /tmp.
            "-c unix_socket_directories=''",
            "-c fsync=off",
            "-c synchronous_commit=off",
            "-c full_page_writes=off",
            "-c shared_buffers=32MB",

            // One backend per connection, and the suite holds several at once: a keep-alive per
            // fixture, Npgsql's pool behind each source, and `createdb` needs one of its own.
            // Twenty was enough when this was written and is not any more — under the Debug
            // configuration, which is slow enough that more of the suite's fixtures are alive
            // together, the server answered `53300: sorry, too many clients already`. Sixty costs a
            // few hundred kilobytes per idle slot and nothing at all until a slot is used.
            "-c max_connections=60");

        var fixture = new PostgresFixture(
            $"Host=127.0.0.1;Port={port.ToString(CultureInfo.InvariantCulture)};Username={User};"
            + "Database=postgres;Include Error Detail=true",
            null,
            VersionOf(bin),
            data,
            bin,
            origin);

        try
        {
            Run(
                Path.Combine(bin, "pg_ctl"),
                ["-D", data, "-l", log, "-w", "-o", options, "start"],
                "pg_ctl start",
                log);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A fresh database on this server, for a test class that needs its own. Created with
    /// <c>createdb</c> rather than a driver, so this project stays free of a PostgreSQL client
    /// dependency; the caller's own provider connects to it.
    /// </summary>
    public string CreateDatabase(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (ConnectionString is null)
        {
            throw new InvalidOperationException("there is no PostgreSQL server: " + SkipReason);
        }

        if (_binDirectory is null)
        {
            throw new InvalidOperationException(
                "CHALK_TEST_POSTGRES names a server this fixture did not start; create the database "
                + "through your own provider instead.");
        }

        var port = PortOf(ConnectionString);
        Run(
            Path.Combine(_binDirectory, "createdb"),
            ["-h", "127.0.0.1", "-p", port, "-U", User, name],
            "createdb " + name);
        return ConnectionString.Replace(
            "Database=postgres", "Database=" + name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The resolution order of §0b, and the first hit wins: <c>CHALK_PG_BIN</c>, then
    /// <c>PATH</c>, then <c>pg_config --bindir</c>, then the Homebrew kegs, then Debian's
    /// versioned directories, highest major first.
    /// </summary>
    private static (string? Bin, string Origin) FindBinaries()
    {
        if (Environment.GetEnvironmentVariable("CHALK_PG_BIN") is { Length: > 0 } named
            && HasBinaries(named))
        {
            return (named, "CHALK_PG_BIN");
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (HasBinaries(dir))
            {
                return (dir, "PATH");
            }
        }

        if (TryPgConfig() is { Length: > 0 } configured && HasBinaries(configured))
        {
            return (configured, "pg_config --bindir");
        }

        foreach (var keg in new[] { "/opt/homebrew/opt", "/usr/local/opt" })
        {
            if (Newest(keg, "postgresql@*", "bin") is { } brewed)
            {
                return (brewed, "the Homebrew keg " + Path.GetFileName(Path.GetDirectoryName(brewed)!));
            }
        }

        if (Newest("/usr/lib/postgresql", "*", "bin") is { } debian)
        {
            return (debian, "Debian's " + debian);
        }

        return (null, "nothing found");
    }

    /// <summary>
    /// The highest-numbered match of <paramref name="pattern"/> under <paramref name="parent"/> whose
    /// <paramref name="child"/> directory holds the binaries. "Highest" is by the trailing major
    /// number, so <c>postgresql@16</c> beats <c>postgresql@9</c> where a string sort would not.
    /// </summary>
    private static string? Newest(string parent, string pattern, string child)
    {
        if (!Directory.Exists(parent))
        {
            return null;
        }

        return Directory.EnumerateDirectories(parent, pattern)
            .Select(d => (Dir: Path.Combine(d, child), Major: MajorOf(Path.GetFileName(d))))
            .Where(c => HasBinaries(c.Dir))
            .OrderByDescending(c => c.Major)
            .ThenByDescending(c => c.Dir, StringComparer.Ordinal)
            .Select(c => c.Dir)
            .FirstOrDefault();
    }

    private static int MajorOf(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        return int.TryParse(digits, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    private static bool HasBinaries(string? dir) =>
        dir is { Length: > 0 }
        && File.Exists(Path.Combine(dir, Executable("initdb")))
        && File.Exists(Path.Combine(dir, Executable("pg_ctl")))
        && File.Exists(Path.Combine(dir, Executable("createdb")));

    private static string Executable(string name) =>
        OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string? TryPgConfig()
    {
        try
        {
            var output = Capture(Executable("pg_config"), ["--bindir"]);
            return output?.Trim();
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private static string VersionOf(string bin)
    {
        var output = Capture(Path.Combine(bin, Executable("postgres")), ["--version"])?.Trim();

        // "postgres (PostgreSQL) 16.13 (Homebrew)" — the report wants the product and the number.
        if (output is null)
        {
            return string.Empty;
        }

        var words = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var number = words.FirstOrDefault(w => w.Length > 0 && char.IsAsciiDigit(w[0]));
        return number is null ? output : "PostgreSQL " + number;
    }

    private static string PortOf(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && part[..eq].Trim().Equals("Port", StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim();
            }
        }

        return "5432";
    }

    /// <summary>
    /// A loopback port the kernel says is free. There is no way to reserve one for another process,
    /// so this is the usual bind-and-release: the window is small and the fixture starts exactly one
    /// server.
    /// </summary>
    private static int FreeLoopbackPort()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)listener.LocalEndPoint!).Port;
    }

    private static PostgresFixture Unavailable(string why, string origin) =>
        new(null, why, string.Empty, null, null, origin);

    private static string InstallAdvice() =>
        "No PostgreSQL binaries were found, so the PostgreSQL leg is skipped. Looked at "
        + "CHALK_TEST_POSTGRES (an existing server), CHALK_PG_BIN, PATH, `pg_config --bindir`, "
        + "/opt/homebrew/opt/postgresql@*/bin, /usr/local/opt/postgresql@*/bin and "
        + "/usr/lib/postgresql/*/bin. Install them with `brew install postgresql@16` on macOS, "
        + "`sudo apt-get install postgresql-16` on Debian or Ubuntu, or the EDB installer on Windows "
        + "with CHALK_PG_BIN pointing at its `bin`. Set CHALK_TEST_POSTGRES_REQUIRED=1 to make this "
        + "a failure instead of a skip.";

    private static void Run(string exe, string[] arguments, string what, string? log = null)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Without this macOS fails initdb and the postmaster with "postmaster became multithreaded
        // during startup": a locale the C library has to consult a service for makes libSystem spawn
        // a thread, and PostgreSQL forbids that before its own fork.
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";

        using var process = Process.Start(start)
            ?? throw new ChalkTestSetupException($"{what}: '{exe}' did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            return;
        }

        var message = new StringBuilder($"{what} failed with exit code ")
            .Append(process.ExitCode.ToString(CultureInfo.InvariantCulture))
            .Append(":\n")
            .Append(stderr.Length > 0 ? stderr : stdout);
        if (log is not null && File.Exists(log))
        {
            message.Append("\npostmaster log:\n").Append(File.ReadAllText(log));
        }

        throw new ChalkTestSetupException(message.ToString());
    }

    private static string? Capture(string exe, string[] arguments)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["LC_ALL"] = "C";

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_dataDirectory is null || _binDirectory is null)
        {
            return;
        }

        try
        {
            Run(
                Path.Combine(_binDirectory, "pg_ctl"),
                ["-D", _dataDirectory, "-m", "fast", "-w", "stop"],
                "pg_ctl stop");
        }
        catch (ChalkTestSetupException)
        {
            // A server that is already gone is the outcome this wanted. Deleting the directory below
            // is what actually matters, and a stop that failed must not stop it happening.
        }

        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the directory is under the machine's temp root, which is swept anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A fixture could not set itself up. Distinct from an assertion failure: nothing was tested.
/// </summary>
public sealed class ChalkTestSetupException : Exception
{
    public ChalkTestSetupException(string message)
        : base(message)
    {
    }

    public ChalkTestSetupException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public ChalkTestSetupException()
    {
    }
}
