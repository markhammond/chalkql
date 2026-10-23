using System.Globalization;
using Chalk.Incremental;
using Chalk.Sample.Blotter;
using Chalk.Sample.Blotter.Components;
using Chalk.Sample.Blotter.Live;

var options = new BlotterOptions
{
    Trades = Number(Argument(args, "--trades")) ?? 5_000_000,
    BudgetBytes = (Number(Argument(args, "--budget-mb")) ?? 64) << 20,
    Maintenance = Argument(args, "--maintenance") == "recompute" ? ViewMaintenance.Recompute : ViewMaintenance.Incremental,
};

if (args.Contains("--probe"))
{
    return await Probe.RunAsync(options);
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Argument(args, "--urls") ?? "http://localhost:5910");

// Run from the build output rather than published, the framework's script (blazor.web.js) is
// served from its package only when static web assets are on, which outside Development they are not.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<BlotterHost>();
builder.Services.AddHostedService<BlotterService>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.RunAsync();
return 0;

static string? Argument(string[] args, string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

// 5M, 500k and 20m are all numbers of trades here.
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
