using System.Globalization;
using System.Text.Json.Serialization;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>The sounds an alert can make: drawn by the browser, so there is nothing to load. Sent to it by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertSound>))]
public enum AlertSound
{
    /// <summary>A till's bell: cha-ching.</summary>
    Register,

    /// <summary>A rising alarm, twice.</summary>
    Klaxon,

    /// <summary>A crackle that falls away.</summary>
    Zap,

    /// <summary>A falling ray-gun shot.</summary>
    Laser,
}

/// <summary>
/// An alert: a tab's filter, saved, with a sound and a throttle. It fires when a row comes to pass the
/// filter: a fill that passes it, or an order whose version passes it when the one before did not.
/// Fired again within <see cref="Throttle"/>, it sounds once when the throttle allows, saying how many.
/// </summary>
public sealed record AlertRule(string Id, string Name, Tab Tab, FilterSpec Filter, AlertSound Sound, TimeSpan Throttle, bool Muted)
{
    /// <summary>The throttles a rule may have.</summary>
    public static readonly TimeSpan[] Throttles =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    public bool Matches(IRow row) => Filter.Columns.All(f => f.Matches(row));

    /// <summary>As the browser keeps it: the filter as a view's readable link.</summary>
    public SavedRule Save() => new(
        Id, Name, new ViewLink(Tab, Tab.DefaultSort, Filter, Tab.HiddenAtFirst, null).Query(), Sound.ToString(), Throttle.TotalSeconds, Muted);

    /// <summary>A rule the browser kept.</summary>
    /// <exception cref="ArgumentException">It is not a rule: said why.</exception>
    public static AlertRule Load(SavedRule saved)
    {
        var view = ViewLink.Parse(saved.View);
        if (!view.Sort.IsDefault || view.At is not null || !view.Hidden.SetEquals(view.Tab.HiddenAtFirst))
        {
            throw new ArgumentException($"the alert '{saved.Name}' is not a filter alone");
        }

        if (!Enum.TryParse<AlertSound>(saved.Sound, out var sound))
        {
            throw new ArgumentException($"the alert '{saved.Name}' has no sound '{saved.Sound}'");
        }

        var throttle = Throttles.FirstOrDefault(t => t.TotalSeconds >= saved.ThrottleSeconds, Throttles[^1]);
        return new AlertRule(saved.Id, saved.Name, view.Tab, view.Filter, sound, throttle, saved.Muted);
    }

    /// <summary>What a row that fired the rule says, in a line.</summary>
    public static string Describe(IRow row) => row switch
    {
        Order o => string.Create(CultureInfo.InvariantCulture,
            $"{o.Client} {o.Side} {Column.Finance(o.Qty, 4)} {o.Instrument} · {o.Status}"),
        Fill f => string.Create(CultureInfo.InvariantCulture,
            $"{f.Client} {f.Side} {Column.Finance(f.Qty, 4)} {f.Instrument} @ {Column.Significant(f.Price)}"),
        _ => row.Id.ToString(),
    };
}

/// <summary>An alert as the browser keeps it, in its local settings.</summary>
public sealed record SavedRule(string Id, string Name, string View, string Sound, double ThrottleSeconds, bool Muted);
