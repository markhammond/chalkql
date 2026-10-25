using System.Diagnostics;
using System.Globalization;

namespace Chalk.Sample.Blotter;

/// <summary>
///     A line on the console that turns while something slow runs, saying what it is doing and for how
///     long, and is wiped when it is done. Only when the output is a console: redirected to a file or a
///     test's log, it writes nothing.
/// </summary>
internal sealed class Spinner : IAsyncDisposable
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly Func<string> _status;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _turning;
    private int _width;

    private Spinner(Func<string> status)
    {
        _status = status;
        _turning = Task.Run(TurnAsync);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _turning.ConfigureAwait(false);
        Console.Write("\r" + new string(' ', _width) + "\r");
        _stop.Dispose();
    }

    /// <summary>
    ///     A spinner showing <paramref name="status" />, read again on every turn; or nothing, when the output is not a
    ///     console.
    /// </summary>
    public static IAsyncDisposable Start(Func<string> status)
    {
        return Console.IsOutputRedirected ? new Nothing() : new Spinner(status);
    }

    private async Task TurnAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var frame = 0;
        do
        {
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{Frames[frame++ % Frames.Length]} {_status()} ({_clock.Elapsed.TotalSeconds:F1} s)");
            Console.Write("\r" + line.PadRight(_width));
            _width = Math.Max(_width, line.Length);
        } while (await Wait(timer).ConfigureAwait(false));
    }

    private async Task<bool> Wait(PeriodicTimer timer)
    {
        try
        {
            return await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}