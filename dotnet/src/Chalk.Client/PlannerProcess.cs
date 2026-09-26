using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chalk.Client;

/// <summary>Which transport a launched sidecar listens on (docs/design/09-unix-socket-transport.md §4).</summary>
public enum PlannerTransport
{
    /// <summary>A Unix domain socket on macOS and Linux; TCP on loopback everywhere else.</summary>
    Default,

    /// <summary>A Unix domain socket, whatever the platform.</summary>
    UnixSocket,

    /// <summary>An ephemeral loopback port.</summary>
    Tcp,
}

/// <summary>How to launch a planner sidecar.</summary>
public sealed class PlannerProcessOptions
{
    /// <summary>The environment variable a host may point at the planner jar.</summary>
    public const string JarEnvironmentVariable = "CHALK_PLANNER_JAR";

    /// <summary>
    /// The planner shadow JAR to launch. When null, <c>$CHALK_PLANNER_JAR</c> is consulted;
    /// when neither is set, the planner embedded in Chalk.Client is materialised into the
    /// per-user cache and used automatically.
    ///
    /// An explicitly configured path that does not exist is an error rather than a request
    /// to fall back to the embedded planner.
    /// </summary>
    public string? JarPath { get; init; }

    /// <summary>
    /// Root directory in which the embedded planner may be materialised for local execution.
    /// When null, <c>$CHALK_PLANNER_CACHE</c> is consulted, then the platform's normal
    /// per-user cache directory.
    /// </summary>
    public string? ArtifactCacheDirectory { get; init; }

    /// <summary>
    /// A JDK 21+ home. When null: <c>$JAVA_HOME</c>, then <c>/usr/libexec/java_home -v 21</c> on
    /// macOS, then a Homebrew <c>opt/openjdk@21</c>, then <c>java</c> on <c>PATH</c> — the same order
    /// as <c>scripts/java-home.sh</c>.
    /// </summary>
    public string? JavaHome { get; init; }

    public PlannerTransport Transport { get; init; } = PlannerTransport.Default;

    /// <summary>
    /// Where the socket goes. Null means a short unique path under <c>/tmp</c>, which is what keeps
    /// the quickstart free of configuration. A path over 100 bytes is refused before launch.
    /// </summary>
    public string? SocketPath { get; init; }

    /// <summary>How long to wait for the sidecar's listening line. JVM start-up dominates it.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Extra JVM arguments, inserted before <c>-jar</c>.</summary>
    public IReadOnlyList<string> JvmArguments { get; init; } = [];

    /// <summary>
    /// The sidecar's planning scheduler worker count (D243, D240): carried into
    /// <c>--planning-workers</c>. Null — the default — sends no argument at all, and the sidecar
    /// uses every available processor unless <see cref="LoadFactor"/> says otherwise.
    /// </summary>
    public int? Workers { get; init; }

    /// <summary>
    /// A fraction of the sidecar's available processors (D243), 0 &lt; f ≤ 1: carried into
    /// <c>--planning-load-factor</c>. The effective worker count is the minimum of this and
    /// <see cref="Workers"/> that are given, floored at one.
    /// </summary>
    public double? LoadFactor { get; init; }

    /// <summary>
    /// How many shape versions the sidecar's catalog registry keeps per engine instance (D271 (e)):
    /// carried into <c>--catalog-versions</c>. Null — the default — sends no argument and the sidecar
    /// keeps its own default. A host has no reason to set this; a test that wants to see an eviction
    /// without waiting for one does.
    /// </summary>
    public int? CatalogVersions { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }
}

/// <summary>
/// Starts a planner sidecar and owns it (docs/design/09-unix-socket-transport.md §4). On a Unix-like
/// system the default is a Unix domain socket on a path nobody has to agree in advance, so a
/// co-located host needs no port, no environment variable and no handshake:
///
/// <code>
/// await using var planner = await PlannerProcess.StartAsync(new PlannerProcessOptions { JarPath = jar });
/// await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
/// {
///     ContextId = "demo", Sources = [source], Planner = planner.CreatePlanner(),
/// });
/// </code>
///
/// The sidecar's stderr is forwarded to the logger at Debug; its stdout carries exactly one line,
/// which is what this parses.
/// </summary>
public sealed class PlannerProcess : IAsyncDisposable
{
    /// <summary>How long a SIGTERMed sidecar has to drain before it is killed.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private const string ListeningMarker = "chalk-planner listening on ";

    /// <summary>Enough stderr to explain a failure without holding a whole run's logs.</summary>
    private const int DiagnosticLines = 50;

    private readonly Process _process;
    private readonly string? _socketPath;
    private readonly ILogger _log;
    private readonly Queue<string> _stderr;

    private PlannerProcess(Process process, Uri address, string? socketPath, ILogger log, Queue<string> stderr)
    {
        _process = process;
        Address = address;
        _socketPath = socketPath;
        _log = log;
        _stderr = stderr;
    }

    /// <summary>Where the sidecar listens: <c>unix:///…</c> or <c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
    public Uri Address { get; }

    /// <summary>The sidecar's process id, for a host that wants it in its own logs.</summary>
    public int ProcessId => _process.Id;

    /// <summary>
    /// Starts the embedded planner sidecar with platform defaults.
    /// </summary>
    public static ValueTask<PlannerProcess> StartAsync() =>
        StartAsync(new PlannerProcessOptions());
    
    /// <summary>Starts a sidecar and waits for it to say it is listening.</summary>
    /// <exception cref="FileNotFoundException">No jar at <c>JarPath</c>, or no JDK to run it.</exception>
    /// <exception cref="TimeoutException">It did not print its listening line in time.</exception>
    /// <exception cref="InvalidOperationException">It exited first; the message carries the exit code and stderr.</exception>
    public static async ValueTask<PlannerProcess> StartAsync(
        PlannerProcessOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var log = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PlannerProcess>();

        var jar = await ResolveJarAsync(
                options.JarPath,
                Environment.GetEnvironmentVariable(
                    PlannerProcessOptions.JarEnvironmentVariable),
                options.ArtifactCacheDirectory,
                ct)
            .ConfigureAwait(false);

        var java = await ResolveJavaAsync(options.JavaHome, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException(
                "no JDK 21+: set PlannerProcessOptions.JavaHome or JAVA_HOME, or run scripts/install-tools.sh");

        var useSocket = Resolve(options.Transport) == PlannerTransport.UnixSocket;
        string? socketPath = null;
        if (useSocket)
        {
            socketPath = options.SocketPath ?? DefaultSocketPath();
            if (!Path.IsPathRooted(socketPath))
            {
                throw new ArgumentException(
                    $"a socket path must be absolute; got {socketPath}", nameof(options));
            }

            PlannerAddress.RequireBindablePath(socketPath, nameof(options));
        }

        var start = new ProcessStartInfo(java)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in options.JvmArguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("-jar");
        start.ArgumentList.Add(jar);
        if (useSocket)
        {
            start.ArgumentList.Add("--socket");
            start.ArgumentList.Add(socketPath!);
        }
        else
        {
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add("0");
        }

        // The scheduler's worker count (D243, D240): no argument at all when the host names
        // neither, which is every available processor, exactly as leaving both unset on the
        // command line does.
        if (options.Workers is { } workers)
        {
            start.ArgumentList.Add("--planning-workers");
            start.ArgumentList.Add(workers.ToString(CultureInfo.InvariantCulture));
        }

        if (options.LoadFactor is { } loadFactor)
        {
            start.ArgumentList.Add("--planning-load-factor");
            start.ArgumentList.Add(loadFactor.ToString(CultureInfo.InvariantCulture));
        }

        // The catalog registry's bound (D271 (e)). Same shape as the two above: no argument at all
        // when the host names none, which is the sidecar's own default.
        if (options.CatalogVersions is { } catalogVersions)
        {
            start.ArgumentList.Add("--catalog-versions");
            start.ArgumentList.Add(catalogVersions.ToString(CultureInfo.InvariantCulture));
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {java}");
        var stderr = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            log.LogDebug("planner: {Line}", e.Data);
            lock (stderr)
            {
                stderr.Enqueue(e.Data);
                while (stderr.Count > DiagnosticLines)
                {
                    stderr.Dequeue();
                }
            }
        };
        process.BeginErrorReadLine();

        try
        {
            var address = await ReadListeningLineAsync(process, stderr, options.StartTimeout, ct)
                .ConfigureAwait(false);
            log.LogDebug("planner {Pid} listening on {Address}, from {Jar}", process.Id, address, jar);
            return new PlannerProcess(process, address, socketPath, log, stderr);
        }
        catch
        {
            await StopAsync(process, socketPath, NullLogger.Instance).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A planner talking to this sidecar; the caller disposes it. Anything set on
    /// <paramref name="overrides"/> other than its <c>Address</c> — which is always this process's —
    /// is used as given.
    /// </summary>
    public IQueryPlanner CreatePlanner(GrpcPlannerOptions? overrides = null)
    {
        var template = overrides ?? new GrpcPlannerOptions { Address = Address };
        return new GrpcQueryPlanner(new GrpcPlannerOptions
        {
            Address = Address,
            Deadline = template.Deadline,
            MaxReceiveMessageSizeMb = template.MaxReceiveMessageSizeMb,
            LoggerFactory = template.LoggerFactory,
        });
    }

    /// <summary>The last lines the sidecar wrote to stderr, for a failure message.</summary>
    public IReadOnlyList<string> Diagnostics()
    {
        lock (_stderr)
        {
            return _stderr.ToArray();
        }
    }

    /// <summary>SIGTERM, then a kill if it will not go, then the socket file.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(_process, _socketPath, _log).ConfigureAwait(false);
        _process.Dispose();
    }

    /// <summary>
    /// Resolves an explicitly configured planner first; otherwise materialises the
    /// planner embedded in Chalk.Client into the per-user cache.
    /// </summary>
    internal static async ValueTask<string> ResolveJarAsync(
        string? configured,
        string? environment,
        string? cacheDirectory,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ResolveNamedJar(configured, "PlannerProcessOptions.JarPath");
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            return ResolveNamedJar(environment, PlannerProcessOptions.JarEnvironmentVariable);
        }

        return await PlannerArtifact
            .MaterializeAsync(cacheDirectory, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A jar the host named. An absolute path is taken as it is. A relative one is searched for from
    /// the working directory upward and then from the application's directory upward, the nearest
    /// match winning, so a value written from a repository's root holds from a test host's output
    /// directory or a sample's without this library knowing any layout (F106). Refused by name when
    /// nothing has it, with every directory that was tried.
    /// </summary>
    internal static string ResolveNamedJar(string path, string setting)
    {
        if (TryResolveNamedJar(path, [Directory.GetCurrentDirectory(), AppContext.BaseDirectory], out var resolved, out var tried))
        {
            return resolved;
        }

        var where = Path.IsPathRooted(path)
            ? "nothing is at that path."
            : "a relative path is tried from the working directory and each directory above it, then "
              + "from the application's directory and each directory above that, and none of these has "
              + $"it: {string.Join(", ", tried)}. Give an absolute path, or one relative to a directory "
              + "above the process.";
        throw new FileNotFoundException($"no planner jar at '{path}' ({setting}): {where}", path);
    }

    /// <summary>
    /// The search behind <see cref="ResolveNamedJar"/>, with the directories it climbs from made
    /// explicit so a test can hand it a tree of its own. <paramref name="tried"/> is every directory
    /// the relative path was joined to, in the order tried; for an absolute path it is the path's own
    /// directory.
    /// </summary>
    internal static bool TryResolveNamedJar(
        string path, IReadOnlyList<string> climbFrom, out string resolved, out IReadOnlyList<string> tried)
    {
        if (Path.IsPathRooted(path))
        {
            resolved = Path.GetFullPath(path);
            tried = [Path.GetDirectoryName(resolved) ?? resolved];
            return File.Exists(resolved);
        }

        var bases = new List<string>();
        foreach (var start in climbFrom)
        {
            if (string.IsNullOrEmpty(start))
            {
                continue;
            }

            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (!bases.Contains(directory.FullName, StringComparer.Ordinal))
                {
                    bases.Add(directory.FullName);
                }
            }
        }

        foreach (var @base in bases)
        {
            var candidate = Path.GetFullPath(Path.Combine(@base, path));
            if (File.Exists(candidate))
            {
                resolved = candidate;
                tried = bases;
                return true;
            }
        }

        resolved = string.Empty;
        tried = bases;
        return false;
    }

    private static async Task StopAsync(Process process, string? socketPath, ILogger log)
    {
        try
        {
            if (!process.HasExited)
            {
                // SIGTERM, not Kill: the planner's shutdown hook drains in-flight calls and unlinks
                // its own socket. Process.Kill is SIGKILL, which does neither.
                if (!TryTerminate(process.Id))
                {
                    process.Kill(entireProcessTree: true);
                }

                using var deadline = new CancellationTokenSource(StopTimeout);
                try
                {
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    log.LogDebug("planner {Pid} did not stop within {Timeout}; killing it", process.Id, StopTimeout);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone; nothing to stop.
        }
        finally
        {
            if (socketPath is not null)
            {
                // Belt and braces: the sidecar removes it on a clean shutdown, but not after a kill.
                try
                {
                    File.Delete(socketPath);
                }
                catch (IOException)
                {
                    // Someone else's problem now.
                }
                catch (UnauthorizedAccessException)
                {
                    // Likewise.
                }
            }
        }
    }

    /// <summary>
    /// The sidecar prints exactly one line on stdout when it binds (03-planner.md §1); everything
    /// else goes to stderr, which is what makes this parse rather than guess.
    /// </summary>
    private static async Task<Uri> ReadListeningLineAsync(
        Process process, Queue<string> stderr, TimeSpan startTimeout, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(startTimeout);

        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith(ListeningMarker, StringComparison.Ordinal))
                {
                    return Parse(line[ListeningMarker.Length..].Trim());
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"the planner did not print its listening line within {startTimeout}."
                + Environment.NewLine + Tail(stderr));
        }

        // Stdout is at EOF, so the process is on its way out; waiting lets the stderr reader drain
        // before the message is built. Without this the most common failure — macOS's /usr/bin/java
        // stub, which is on PATH whether or not a JDK is installed — reports nothing at all.
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Still running but done talking; report what there is.
        }

        var exit = process.HasExited
            ? $" It exited with {process.ExitCode.ToString(CultureInfo.InvariantCulture)}."
            : string.Empty;
        throw new InvalidOperationException(
            "the planner exited before printing its listening line." + exit + Environment.NewLine + Tail(stderr));
    }

    /// <summary><c>unix:/tmp/x.sock</c> or <c>127.0.0.1:53359</c>, as the sidecar prints them.</summary>
    private static Uri Parse(string listenAddress) =>
        listenAddress.StartsWith("unix:", StringComparison.Ordinal)
            ? PlannerAddress.ForSocket(listenAddress["unix:".Length..])
            : new Uri("http://" + listenAddress);

    private static string Tail(Queue<string> stderr)
    {
        lock (stderr)
        {
            return string.Join(Environment.NewLine, stderr);
        }
    }

    private static PlannerTransport Resolve(PlannerTransport transport) => transport switch
    {
        PlannerTransport.Default =>
            OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()
                ? PlannerTransport.UnixSocket
                : PlannerTransport.Tcp,
        _ => transport,
    };

    /// <summary>
    /// Short by construction, so <c>sockaddr_un</c> is never the reason a launch fails: the process
    /// id keeps two hosts apart and the suffix keeps two engines in one host apart.
    /// </summary>
    private static string DefaultSocketPath()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var suffix = string.Create(6, alphabet, (span, letters) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = letters[Random.Shared.Next(letters.Length)];
            }
        });

        return string.Create(
            CultureInfo.InvariantCulture, $"/tmp/chalk-{Environment.ProcessId}-{suffix}.sock");
    }

    /// <summary>The order in <c>scripts/java-home.sh</c>, so a shell run and a host run agree.</summary>
    private static async Task<string?> ResolveJavaAsync(string? configuredHome, CancellationToken ct)
    {
        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";

        foreach (var home in new[] { configuredHome, Environment.GetEnvironmentVariable("JAVA_HOME") })
        {
            if (!string.IsNullOrWhiteSpace(home))
            {
                var candidate = Path.Combine(home, "bin", executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (OperatingSystem.IsMacOS() && await JavaHomeToolAsync(ct).ConfigureAwait(false) is { } fromTool)
        {
            var candidate = Path.Combine(fromTool, "bin", executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var prefix in new[] { "/opt/homebrew", "/usr/local" })
        {
            foreach (var cellar in new[] { $"{prefix}/opt/openjdk@21", $"{prefix}/opt/openjdk" })
            {
                foreach (var home in new[] { $"{cellar}/libexec/openjdk.jdk/Contents/Home", cellar })
                {
                    var candidate = Path.Combine(home, "bin", executable);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>macOS's own JDK locator. Absent or unhelpful is normal; it is one candidate of four.</summary>
    private static async Task<string?> JavaHomeToolAsync(CancellationToken ct)
    {
        const string tool = "/usr/libexec/java_home";
        if (!File.Exists(tool))
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-v");
            start.ArgumentList.Add("21");

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0 && output.Trim().Length > 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// SIGTERM through libc, because .NET has no portable "ask a child to stop": <c>Process.Kill</c>
    /// is SIGKILL. False means the caller should fall back to that.
    /// </summary>
    private static bool TryTerminate(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return Kill(processId, SIGTERM) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private const int SIGTERM = 15;

    // DllImport, not LibraryImport: the source generator emits unsafe code, and one signal is not a
    // reason to turn AllowUnsafeBlocks on for the whole client package (ADR 0013). The signature is
    // two blittable ints, which the runtime marshaller handles without any generated code at all.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
#pragma warning restore SYSLIB1054
}
