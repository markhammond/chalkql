using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Chalk.Client;

/// <summary>
/// The Apache Calcite planner artefact embedded in Chalk.Client.
/// </summary>
public static class PlannerArtifact
{
    /// <summary>
    /// Environment variable overriding the per-user directory in which an embedded planner
    /// is materialised for local execution.
    /// </summary>
    public const string CacheEnvironmentVariable = "CHALK_PLANNER_CACHE";

    private const string ResourceName = "Chalk.Client.chalk-planner.jar";
    private const string JarFileName = "chalk-planner.jar";

    private static readonly Lazy<Task<string>> Hash = new(
        ComputeHashAsync,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Materialisations =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    /// <summary>
    /// Opens the exact planner JAR embedded in this Chalk.Client assembly.
    /// The caller owns the returned stream.
    /// </summary>
    public static Stream Open() =>
        typeof(PlannerArtifact).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException(
            $"embedded planner resource '{ResourceName}' was not found");

    /// <summary>The SHA-256 of the embedded planner JAR.</summary>
    public static async ValueTask<string> Sha256Async(
        CancellationToken ct = default) =>
        await Hash.Value.WaitAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Copies the embedded planner JAR to a caller-owned destination, for example when
    /// deploying the matching sidecar to another host.
    /// </summary>
    public static async Task CopyToAsync(
        Stream destination,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        await using var source = Open();
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Materialises the embedded JAR in the local per-user cache. Repeated calls in this
    /// process, and subsequent processes using the same artefact, reuse the same file.
    /// </summary>
    internal static async ValueTask<string> MaterializeAsync(
        string? configuredCacheDirectory,
        CancellationToken ct)
    {
        var root = ResolveCacheRoot(configuredCacheDirectory);

        var lazy = Materialisations.GetOrAdd(
            root,
            static directory => new Lazy<Task<string>>(
                () => MaterializeCoreAsync(directory),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // Cancellation stops this caller waiting; it deliberately does not cancel the
            // shared materialisation another PlannerProcess may also be waiting for.
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted)
            {
                Materialisations.TryRemove(root, out _);
            }

            throw;
        }
    }

    private static async Task<string> ComputeHashAsync()
    {
        await using var source = Open();

        // No seekability assumption: SHA256 consumes the resource as a forward-only stream.
        var hash = await SHA256.HashDataAsync(source).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> MaterializeCoreAsync(string cacheRoot)
    {
        var hash = await Hash.Value.ConfigureAwait(false);

        var directory = Path.Combine(
            cacheRoot,
            "chalkql",
            "planner",
            hash);

        var jar = Path.Combine(directory, JarFileName);

        if (File.Exists(jar))
        {
            return jar;
        }

        CreateCacheDirectory(directory);

        // Same directory means the final rename stays on one filesystem and is atomic.
        var temporary = Path.Combine(
            directory,
            $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using var source = Open();

            await using (var destination = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }

            try
            {
                File.Move(temporary, jar);
            }
            catch (IOException) when (File.Exists(jar))
            {
                // Another process materialised the same immutable artefact first.
            }

            return jar;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Best effort: the completed/cache file is independent of this temporary.
            }
            catch (UnauthorizedAccessException)
            {
                // Likewise.
            }
        }
    }

    private static string ResolveCacheRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var environment =
            Environment.GetEnvironmentVariable(CacheEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(environment))
        {
            return Path.GetFullPath(environment);
        }

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(local))
            {
                return local;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, "Library", "Caches");
            }
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

            if (!string.IsNullOrWhiteSpace(xdg))
            {
                return Path.GetFullPath(xdg);
            }

            var home = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".cache");
            }
        }

        throw new InvalidOperationException(
            $"no per-user cache directory is available; set PlannerProcessOptions.ArtifactCacheDirectory "
            + $"or {CacheEnvironmentVariable}");
    }

    private static void CreateCacheDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
    }
}