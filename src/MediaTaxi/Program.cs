using MediaTaxi.Configuration;
using MediaTaxi.Domain;
using MediaTaxi.Engine;
using MediaTaxi.Inventory;
using MediaTaxi.Media;
using MediaTaxi.Storage;
using MediaTaxi.Web;
using System.Text.Json.Serialization;

if (args.Contains("--version")) { Console.WriteLine("Media Taxi 0.2.0"); return; }
var configIndex = Array.IndexOf(args, "--config");
if (configIndex >= 0 && (configIndex + 1 >= args.Length || args[configIndex + 1].StartsWith("--", StringComparison.Ordinal)))
{ Console.Error.WriteLine("Configuration error: --config requires a path."); Environment.ExitCode = 2; return; }
var configPath = configIndex >= 0 ? args[configIndex + 1] : Path.Combine(AppContext.BaseDirectory, "media-taxi.ini");
TaxiOptions options;
try { options = OptionsLoader.Load(configPath); }
catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or TimeZoneNotFoundException or ArgumentException)
{ Console.Error.WriteLine($"Configuration error: {ex.Message}\nUse --config PATH; see media-taxi.example.ini."); Environment.ExitCode = 2; return; }
if (args.Contains("--validate-config")) { Console.WriteLine($"Configuration valid: {options.ConfigPath}"); return; }
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
builder.WebHost.UseUrls(options.Web.Listen);
builder.Logging.SetMinimumLevel(Enum.Parse<LogLevel>(options.LogLevel, ignoreCase: true));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new JobStore(options.Folders.State));
builder.Services.AddSingleton<IMediaService, MediaService>();
builder.Services.AddSingleton<InventoryService>();
builder.Services.AddHostedService(p => p.GetRequiredService<InventoryService>());
builder.Services.AddSingleton<JobEngine>();
builder.Services.AddSingleton<IApplicationView>(p => p.GetRequiredService<JobEngine>());
builder.Services.AddHostedService(p => p.GetRequiredService<JobEngine>());
await using var app = builder.Build();
DashboardEndpoints.Map(app, app.Services.GetRequiredService<IApplicationView>());
app.Logger.LogInformation("Media Taxi 0.2.0 | Config: {Config} | Dashboard: {Listen}", options.ConfigPath, options.Web.Listen);
await app.RunAsync();

public partial class Program;
