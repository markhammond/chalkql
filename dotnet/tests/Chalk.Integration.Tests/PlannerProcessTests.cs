using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The launcher of docs/design/09-unix-socket-transport.md §4: it starts a real JVM, so what is
/// tested here is the lifecycle — the address it reports, the file it leaves behind (none), and what
/// it says when the sidecar will not start.
/// </summary>
public sealed class PlannerProcessTests
{
    private static string Jar =>
        RepoLayout.PlannerJar?.FullName
        ?? throw new InvalidOperationException("no planner jar");

    [Theory]
    [InlineData(PlannerTransport.UnixSocket)]
    [InlineData(PlannerTransport.Tcp)]
    public async Task It_starts_and_stops_over_either_transport(PlannerTransport transport)
    {
        SkipWithoutJar();

        var socket = transport == PlannerTransport.UnixSocket
            ? Path.Combine("/tmp", $"chalk-test-{Environment.ProcessId}-{Guid.NewGuid():N}"[..28] + ".sock")
            : null;

        PlannerProcess sidecar = await PlannerProcess.StartAsync(
            new PlannerProcessOptions { JarPath = Jar, Transport = transport, SocketPath = socket },
            TestContext.Current.CancellationToken);

        int pid;
        await using (sidecar)
        {
            pid = sidecar.ProcessId;
            Assert.True(pid > 0);

            if (socket is null)
            {
                Assert.Equal("http", sidecar.Address.Scheme);
                Assert.Equal("127.0.0.1", sidecar.Address.Host);
                Assert.NotEqual(0, sidecar.Address.Port);
            }
            else
            {
                Assert.Equal(new Uri("unix://" + socket), sidecar.Address);
                Assert.True(File.Exists(socket), $"{socket} should exist while the sidecar runs");
            }

            await using var planner = sidecar.CreatePlanner();
            var info = await planner.GetInfoAsync(TestContext.Current.CancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(info.PlannerVersion));
        }

        if (socket is not null)
        {
            Assert.False(File.Exists(socket), $"{socket} should be gone after dispose");
        }

        Assert.True(Exited(pid), "the sidecar process should be gone after dispose");
    }

    /// <summary>
    /// The whole point of the default path: a host that says nothing gets a socket short enough for
    /// <c>sockaddr_un</c> and unique enough for two engines in one process.
    /// </summary>
    [Fact]
    public async Task The_default_socket_path_is_short_unique_and_cleaned_up()
    {
        SkipWithoutJar();

        var paths = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            await using var sidecar = await PlannerProcess.StartAsync(
                new PlannerProcessOptions { JarPath = Jar, Transport = PlannerTransport.UnixSocket },
                TestContext.Current.CancellationToken);

            var path = sidecar.Address.LocalPath;
            Assert.StartsWith("/tmp/chalk-", path, StringComparison.Ordinal);
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(path) <= 100,
                $"{path} is longer than sockaddr_un allows");
            Assert.True(File.Exists(path));
            paths.Add(path);
        }

        Assert.Distinct(paths);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
    }

    /// <summary>
    /// A jar that cannot run at all: the failure a host sees must carry the exit code and what the
    /// process said, or "the planner did not start" is unactionable.
    /// </summary>
    [Fact]
    public async Task A_sidecar_that_exits_first_reports_its_exit_code_and_stderr()
    {
        var corrupt = Path.Combine(Path.GetTempPath(), $"chalk-corrupt-{Guid.NewGuid():N}.jar");
        await File.WriteAllTextAsync(corrupt, "not a jar", TestContext.Current.CancellationToken);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await PlannerProcess.StartAsync(
                    new PlannerProcessOptions { JarPath = corrupt, Transport = PlannerTransport.Tcp },
                    TestContext.Current.CancellationToken));

            Assert.Contains(
                "exited before printing its listening line", error.Message, StringComparison.Ordinal);
            Assert.Contains("It exited with 1.", error.Message, StringComparison.Ordinal);
            Assert.Contains("Invalid or corrupt jarfile", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(corrupt);
        }
    }

    /// <summary>
    /// A sidecar that never prints its line must not hang a host for ever. <c>suspend=y</c> parks the
    /// JVM before <c>main</c> after printing one line of its own, which is exactly that shape.
    /// </summary>
    [Fact]
    public async Task A_sidecar_that_never_prints_its_line_hits_the_start_timeout()
    {
        SkipWithoutJar();

        var error = await Assert.ThrowsAsync<TimeoutException>(
            async () => await PlannerProcess.StartAsync(
                new PlannerProcessOptions
                {
                    JarPath = Jar,
                    Transport = PlannerTransport.Tcp,
                    JvmArguments = ["-agentlib:jdwp=transport=dt_socket,server=y,suspend=y,address=127.0.0.1:0"],
                    StartTimeout = TimeSpan.FromSeconds(3),
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("did not print its listening line", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_explicit_jar_is_not_replaced_by_the_embedded_planner()
    {
        const string missing = "/tmp/there-is-no-such-chalk-planner.jar";

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await PlannerProcess.StartAsync(
                new PlannerProcessOptions { JarPath = missing },
                TestContext.Current.CancellationToken));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "PlannerProcessOptions.JarPath",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_socket_path_sockaddr_un_cannot_hold_is_refused_before_launch()
    {
        SkipWithoutJar();

        var tooLong = "/tmp/" + new string('x', 120) + ".sock";
        var error = await Assert.ThrowsAsync<ArgumentException>(
            async () => await PlannerProcess.StartAsync(
                new PlannerProcessOptions
                {
                    JarPath = Jar,
                    Transport = PlannerTransport.UnixSocket,
                    SocketPath = tooLong,
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("100-byte limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relative_socket_path_is_refused_before_launch()
    {
        SkipWithoutJar();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            async () => await PlannerProcess.StartAsync(
                new PlannerProcessOptions
                {
                    JarPath = Jar,
                    Transport = PlannerTransport.UnixSocket,
                    SocketPath = "chalk.sock",
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("must be absolute", error.Message, StringComparison.Ordinal);
    }

    private static void SkipWithoutJar() =>
        Assert.SkipWhen(
            RepoLayout.PlannerJar is null,
            "no planner jar; run 'planner/gradlew -p planner shadowJar'");

    /// <summary>True once the process id is not a live process of ours.</summary>
    private static bool Exited(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
