using System.Globalization;
using Chalk.Incremental;
using Microsoft.AspNetCore.DataProtection;
using Chalk.Sample.Blotter;
using Chalk.Sample.Blotter.Components;
using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Market;
using Chalk.Sample.Blotter.Trades;

var budget = Number(Argument(args, "--budget-mb"));
var options = new BlotterOptions
{
    Orders = Number(Argument(args, "--orders")) ?? 5_000_000,
    BudgetBytes = (budget ?? 8) << 20,
    FindBudget = budget is null,
    Maintenance = Argument(args, "--maintenance") == "recompute" ? ViewMaintenance.Recompute : ViewMaintenance.Incremental,
    Postgres = Argument(args, "--postgres"),
    PostgresBinaries = Argument(args, "--pg-bin"),
    Current = Argument(args, "--current") switch
    {
        null or "trigger" => CurrentSync.Trigger,
        "lagging" => CurrentSync.Lagging,
        var other => throw new ArgumentException($"--current takes trigger or lagging, not '{other}'"),
    },
};

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Argument(args, "--urls") ?? "http://localhost:5910");

// Run from the build output rather than published, the framework's script (blazor.web.js) is
// served from its package only when static web assets are on, which outside Development they are not.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<BlotterHost>();
builder.Services.AddHostedService<BlotterService>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// The keys that shroud the values in a view's link, kept in a folder so that a link opens after a
// restart: rotated every 90 days, an old one kept to open what it shrouded.
var linkKeys = Argument(args, "--link-keys")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chalk.Sample.Blotter", "link-keys");
builder.Services.AddDataProtection()
    .SetApplicationName("Chalk.Sample.Blotter")
    .PersistKeysToFileSystem(new DirectoryInfo(linkKeys));
builder.Services.AddSingleton<LinkShroud>();
builder.Services.AddSingleton<AlertDesk>();

var app = builder.Build();
app.UseAntiforgery();
// An alert's sound is addressed by its version (sounds/kaching.pd1r8b6lth.wav), so a browser may
// keep it for good: a new file gets a new address. Set as the response starts, after the static
// assets have set theirs, which run from a build rather than a publish say not to keep anything.
app.Use((context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/sounds/", StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(path).Contains('.'))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Task.CompletedTask;
        });
    }

    return next(context);
});
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.RunAsync();
return 0;

static string? Argument(string[] args, string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

// 5M, 500k and 20m are all numbers of orders here.
static long? Number(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return null;
    }

    var scale = char.ToLowerInvariant(text[^1]) switch { 'k' => 1_000L, 'm' => 1_000_000L, _ => 1L };
    var digits = scale == 1 ? text : text[..^1];
    return (long)(double.Parse(digits, CultureInfo.InvariantCulture) * scale);
}
