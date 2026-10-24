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
/// An alert: a tab's filter, saved, with a sound and a throttle. It fires when a row comes to pass the
/// filter: a fill that passes it, or an order whose version passes it when the one before did not.
/// Fired again within <see cref="Throttle"/>, it sounds once when the throttle allows, saying how many.
/// </summary>
public sealed record AlertRule(string Id, string Name, Tab Tab, FilterSpec Filter, string Sound, TimeSpan Throttle, bool Muted)
{
    /// <summary>The throttles a rule may have.</summary>
    public static readonly TimeSpan[] Throttles =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    public bool Matches(IRow row) => Filter.Columns.All(f => f.Matches(row));

    /// <summary>As the browser keeps it: the filter as a view's readable link.</summary>
    public SavedRule Save() => new(
        Id, Name, new ViewLink(Tab, Tab.DefaultSort, Filter, Tab.HiddenAtFirst, null).Query(), Sound, Throttle.TotalSeconds, Muted);

    /// <summary>A rule the browser kept, on one of <paramref name="tabs"/>.</summary>
    /// <exception cref="ArgumentException">It is not a rule: said why.</exception>
    public static AlertRule Load(SavedRule saved, IReadOnlyList<Tab> tabs)
    {
        var view = ViewLink.Parse(saved.View, tabs);
        if (!view.Sort.IsDefault || view.At is not null || !view.Hidden.SetEquals(view.Tab.HiddenAtFirst))
        {
            throw new ArgumentException($"the alert '{saved.Name}' is not a filter alone");
        }

        // The sounds the page once drew itself, two of which have recordings like them now.
        var sound = saved.Sound switch { "Register" => "kaching", "Laser" => "laserfire", var named => named };
        if (!AlertSounds.Exists(sound))
        {
            throw new ArgumentException($"the alert '{saved.Name}' has no sound '{saved.Sound}'");
        }

        var throttle = Throttles.FirstOrDefault(t => t.TotalSeconds >= saved.ThrottleSeconds, Throttles[^1]);
        return new AlertRule(saved.Id, saved.Name, view.Tab, view.Filter, sound, throttle, saved.Muted);
    }
}

/// <summary>An alert as the browser keeps it, in its local settings.</summary>
public sealed record SavedRule(string Id, string Name, string View, string Sound, double ThrottleSeconds, bool Muted);
