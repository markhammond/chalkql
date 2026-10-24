using System.Globalization;

namespace Chalk.Sample.Blotter.Grid;

/// <summary>
/// The sounds an alert can make: short recordings in <c>wwwroot/sounds</c>, mono, 8-bit, at no more than
/// 11,025 samples a second, served by the host with the rest of the page's assets. A page loads one the
/// first time it plays it, and the browser keeps it.
/// </summary>
public static class AlertSounds
{
    /// <summary>Each sound by its file's name, and as the alerts panel names it.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        ("kaching", "Ka-ching"),
        ("classiccoin", "Coin"),
        ("chimes", "Chimes"),
        ("notify", "Notify"),
        ("cowbell", "Cowbell"),
        ("cuckoo", "Cuckoo"),
        ("rooster", "Rooster"),
        ("whistledown", "Whistle down"),
        ("boing", "Boing"),
        ("drip", "Drip"),
        ("ploop", "Ploop"),
        ("busy", "Busy tone"),
        ("oldcarhorn", "Old car horn"),
        ("vwhorn", "Beetle horn"),
        ("ship", "Ship's horn"),
        ("laserfire", "Laser"),
        ("bangecho", "Bang"),
        ("crash", "Crash"),
        ("cartooncharge", "Charge"),
        ("grow", "Grow"),
        ("passing", "Passing"),
        ("flush", "Flush"),
    ];

    public const string Default = "kaching";

    public static bool Exists(string id) => All.Any(s => s.Id == id);

    public static string Label(string id) => All.FirstOrDefault(s => s.Id == id).Label ?? id;

    /// <summary>Where the page finds a sound, before the host gives it its versioned address.</summary>
    public static string Path(string id) => $"sounds/{id}.wav";
}

/// <summary>
/// An alert: a condition on a tab's rows, in SQL, with a sound and a throttle. It fires when a row
/// comes to pass the condition: a fill that passes it, or an order whose version passes it when the
/// one before did not. With <see cref="LagOver"/>, it fires instead when a row that passes the
/// condition has waited longer than that for the database to hold it: once for each wait. Fired
/// again within <see cref="Throttle"/>, it sounds once when the throttle allows, saying how many.
/// </summary>
public sealed record AlertRule(
    string Id, string Name, Condition Condition, string Sound, TimeSpan Throttle, bool Muted, TimeSpan? LagOver = null)
{
    /// <summary>The throttles a rule may have.</summary>
    public static readonly TimeSpan[] Throttles =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    /// <summary>The waits for the database a rule may fire after.</summary>
    public static readonly TimeSpan[] Lags =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public Tab Tab => Condition.Tab;

    /// <summary>As the browser keeps it.</summary>
    public SavedRule Save() => new(
        Id, Name, null, Sound, Throttle.TotalSeconds, Muted, LagOver?.TotalSeconds, Tab.Name, Condition.Text);

    /// <summary>
    /// A rule the browser kept, on one of <paramref name="tabs"/>. One kept before conditions were
    /// written in SQL keeps its filter as a view's link, and takes it as the condition it means.
    /// </summary>
    /// <exception cref="ArgumentException">It is not a rule: said why.</exception>
    public static AlertRule Load(SavedRule saved, IReadOnlyList<Tab> tabs)
    {
        Condition condition;
        if (saved.Condition is { } text)
        {
            var tab = tabs.FirstOrDefault(t => t.Name == saved.Tab)
                ?? throw new ArgumentException($"the alert '{saved.Name}' is on no tab '{saved.Tab}'");
            condition = Condition.Parse(tab, text);
        }
        else if (saved.View is { } link)
        {
            var view = ViewLink.Parse(link, tabs);
            if (!view.Sort.IsDefault || view.At is not null || !view.Hidden.SetEquals(view.Tab.HiddenAtFirst))
            {
                throw new ArgumentException($"the alert '{saved.Name}' is not a filter alone");
            }

            condition = Condition.Parse(view.Tab, view.Filter.Condition());
        }
        else
        {
            throw new ArgumentException($"the alert '{saved.Name}' has no condition");
        }

        if (!condition.Errors.IsEmpty)
        {
            throw new ArgumentException($"the alert '{saved.Name}': {condition.Errors[0].Message}");
        }

        // The sounds the page once drew itself, two of which have recordings like them now.
        var sound = saved.Sound switch { "Register" => "kaching", "Laser" => "laserfire", var named => named };
        if (!AlertSounds.Exists(sound))
        {
            throw new ArgumentException($"the alert '{saved.Name}' has no sound '{saved.Sound}'");
        }

        var throttle = Throttles.FirstOrDefault(t => t.TotalSeconds >= saved.ThrottleSeconds, Throttles[^1]);
        var lag = saved.LagOverSeconds is { } seconds
            ? Lags.FirstOrDefault(l => l.TotalSeconds == seconds) is { Ticks: > 0 } known
                ? known
                : throw new ArgumentException($"the alert '{saved.Name}' waits {seconds} s for the database, which is not one of the waits offered")
            : (TimeSpan?)null;
        return new AlertRule(saved.Id, saved.Name, condition, sound, throttle, saved.Muted, lag);
    }
}

/// <summary>
/// An alert as the browser keeps it, in its local settings: its tab and condition, or, kept before
/// conditions were written in SQL, its filter as a view's link.
/// </summary>
public sealed record SavedRule(
    string Id, string Name, string? View, string Sound, double ThrottleSeconds, bool Muted, double? LagOverSeconds = null,
    string? Tab = null, string? Condition = null);
