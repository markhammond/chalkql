using System.Security.Cryptography;
using Chalk.Client;

namespace Chalk.Integration.Tests;

/// <summary>
/// How a locally launched sidecar resolves its planner JAR:
/// an explicit <c>JarPath</c>, then <c>CHALK_PLANNER_JAR</c>, then the planner embedded in
/// <c>Chalk.Client</c>, materialised into a content-addressed per-user cache.
///
/// A path explicitly named by the host is authoritative: if it does not exist it is refused
/// by name rather than silently replaced by the embedded planner.
/// </summary>
public sealed class PlannerJarResolutionTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("chalk-jar-").FullName;

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0x50, 0x4B]);
        return path;
    }

    [Fact]
    public async Task An_explicit_path_wins_over_the_variable_and_the_embedded_planner()
    {
        var explicitJar = Touch("explicit.jar");
        var environmentJar = Touch("environment.jar");

        var resolved = await PlannerProcess.ResolveJarAsync(
            explicitJar,
            environmentJar,
            _dir,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(explicitJar), resolved);
    }

    [Fact]
    public async Task The_variable_wins_over_the_embedded_planner_when_nothing_is_configured()
    {
        var environmentJar = Touch("environment.jar");

        var resolved = await PlannerProcess.ResolveJarAsync(
            configured: null,
            environmentJar,
            _dir,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(environmentJar), resolved);
    }

    [Fact]
    public async Task A_relative_explicit_path_is_made_absolute()
    {
        Touch("relative.jar");

        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_dir);

        try
        {
            // macOS may report /var through its resolved /private/var spelling, so assert the
            // contract rather than one textual representation of the path.
            var resolved = await PlannerProcess.ResolveJarAsync(
                "relative.jar",
                environment: null,
                _dir,
                TestContext.Current.CancellationToken);

            Assert.True(Path.IsPathRooted(resolved), resolved);
            Assert.Equal("relative.jar", Path.GetFileName(resolved));
            Assert.True(File.Exists(resolved), resolved);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    /// <summary>A file at a relative path under the temp root, its directories created on the way.</summary>
    private string TouchAt(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x50, 0x4B]);
        return path;
    }

    /// <summary>
    /// The case that kept recurring: a value written from a checkout's root, read by a test host or a
    /// sample whose working directory is somewhere beneath it. The search climbs from the working
    /// directory and finds it above.
    /// </summary>
    [Fact]
    public void A_relative_path_is_found_above_the_directory_it_is_climbed_from()
    {
        var jar = TouchAt(Path.Combine("planner", "build", "libs", "x.jar"));
        var nested = Directory.CreateDirectory(Path.Combine(_dir, "dotnet", "tests", "bin")).FullName;

        var found = PlannerProcess.TryResolveNamedJar(
            Path.Combine("planner", "build", "libs", "x.jar"), [nested], out var resolved, out var tried);

        Assert.True(found);
        Assert.Equal(Path.GetFullPath(jar), resolved);
        Assert.Equal(new DirectoryInfo(nested).FullName, tried[0]);
    }

    [Fact]
    public void The_nearest_match_wins()
    {
        var far = TouchAt(Path.Combine("planner", "x.jar"));
        var near = TouchAt(Path.Combine("a", "planner", "x.jar"));
        var start = Directory.CreateDirectory(Path.Combine(_dir, "a", "b", "c")).FullName;

        var found = PlannerProcess.TryResolveNamedJar(Path.Combine("planner", "x.jar"), [start], out var resolved, out _);

        Assert.True(found);
        Assert.Equal(Path.GetFullPath(near), resolved);
        Assert.NotEqual(Path.GetFullPath(far), resolved);
    }

    [Fact]
    public void The_application_directory_is_climbed_after_the_working_directory()
    {
        var jar = TouchAt(Path.Combine("planner", "x.jar"));
        var appDirectory = Directory.CreateDirectory(Path.Combine(_dir, "app", "bin")).FullName;
        var elsewhere = Directory.CreateTempSubdirectory("chalk-elsewhere-").FullName;
        try
        {
            var found = PlannerProcess.TryResolveNamedJar(
                Path.Combine("planner", "x.jar"), [elsewhere, appDirectory], out var resolved, out var tried);

            Assert.True(found);
            Assert.Equal(Path.GetFullPath(jar), resolved);
            Assert.Equal(new DirectoryInfo(elsewhere).FullName, tried[0]);
            Assert.Contains(new DirectoryInfo(appDirectory).FullName, tried);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task A_relative_path_found_nowhere_is_refused_naming_every_directory_tried()
    {
        var start = Directory.CreateDirectory(Path.Combine(_dir, "a", "b")).FullName;

        var found = PlannerProcess.TryResolveNamedJar("nowhere/x.jar", [start], out _, out var tried);

        Assert.False(found);
        Assert.Equal(new DirectoryInfo(start).FullName, tried[0]);
        Assert.Contains(new DirectoryInfo(_dir).FullName, tried);
        Assert.Equal(Path.GetPathRoot(start), tried[^1]);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await PlannerProcess.ResolveJarAsync(
                "nowhere/x.jar",
                environment: null,
                _dir,
                TestContext.Current.CancellationToken));

        Assert.Contains("'nowhere/x.jar' (PlannerProcessOptions.JarPath)", error.Message, StringComparison.Ordinal);
        Assert.Contains("none of these has it", error.Message, StringComparison.Ordinal);
        Assert.Contains("Give an absolute path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_path_that_does_not_exist_is_refused_even_though_an_embedded_planner_exists()
    {
        var missing = Path.Combine(_dir, "missing.jar");

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await PlannerProcess.ResolveJarAsync(
                missing,
                environment: null,
                _dir,
                TestContext.Current.CancellationToken));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "PlannerProcessOptions.JarPath",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_variable_that_does_not_exist_is_refused_naming_the_variable()
    {
        var missing = Path.Combine(_dir, "missing.jar");

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await PlannerProcess.ResolveJarAsync(
                configured: null,
                missing,
                _dir,
                TestContext.Current.CancellationToken));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        Assert.Contains(
            PlannerProcessOptions.JarEnvironmentVariable,
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_external_path_the_embedded_planner_is_materialised_into_the_cache()
    {
        var resolved = await PlannerProcess.ResolveJarAsync(
            configured: null,
            environment: null,
            _dir,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(resolved), resolved);
        Assert.Equal("chalk-planner.jar", Path.GetFileName(resolved));

        var relative = Path.GetRelativePath(_dir, resolved);
        var parts = relative.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        Assert.Equal("chalkql", parts[0]);
        Assert.Equal("planner", parts[1]);
        Assert.Equal(64, parts[2].Length); // SHA-256 as lower-case hex
        Assert.Equal("chalk-planner.jar", parts[3]);
    }

    [Fact]
    public async Task The_materialised_path_is_addressed_by_the_embedded_jars_sha256()
    {
        var resolved = await PlannerProcess.ResolveJarAsync(
            configured: null,
            environment: null,
            _dir,
            TestContext.Current.CancellationToken);

        var expected = await PlannerArtifact.Sha256Async(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            expected,
            Directory.GetParent(resolved)!.Name);

        await using var file = File.OpenRead(resolved);
        var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    file,
                    TestContext.Current.CancellationToken))
            .ToLowerInvariant();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Repeated_resolution_reuses_the_same_materialised_jar()
    {
        var first = await PlannerProcess.ResolveJarAsync(
            configured: null,
            environment: null,
            _dir,
            TestContext.Current.CancellationToken);

        var written = File.GetLastWriteTimeUtc(first);

        var second = await PlannerProcess.ResolveJarAsync(
            configured: null,
            environment: null,
            _dir,
            TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(written, File.GetLastWriteTimeUtc(second));
    }

    [Fact]
    public async Task The_embedded_artifact_can_be_exported_without_knowing_its_resource_name()
    {
        await using var copy = new MemoryStream();

        await PlannerArtifact.CopyToAsync(
            copy,
            TestContext.Current.CancellationToken);

        Assert.True(copy.Length > 0);

        copy.Position = 0;

        var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    copy,
                    TestContext.Current.CancellationToken))
            .ToLowerInvariant();

        Assert.Equal(
            await PlannerArtifact.Sha256Async(
                TestContext.Current.CancellationToken),
            actual);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover cache directory is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Likewise.
        }
    }
}