using Coravel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using AutoDispatchService.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHealthChecks();
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).WriteTo.Console().CreateLogger();

// SQL connection for direct DB queries (finding unscored/assigned jobs)
var sqlConnection = Environment.GetEnvironmentVariable("SQLConnection") ?? "";
if (string.IsNullOrEmpty(sqlConnection))
    throw new InvalidOperationException("SQLConnection environment variable is required.");

// AutoDispatch API base URL (for calling suggest/observe endpoints)
var apiBaseUrl = Environment.GetEnvironmentVariable("AutoDispatchApiBaseUrl") ?? "";
if (string.IsNullOrEmpty(apiBaseUrl))
    throw new InvalidOperationException("AutoDispatchApiBaseUrl environment variable is required.");

// Health checks
builder.Services.AddSingleton<IHealthCheckPublisher, HealthCheckPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(options =>
{
    options.Delay = TimeSpan.FromSeconds(5);
    options.Period = TimeSpan.FromSeconds(20);
});

// Coravel scheduler
builder.Services.AddScheduler();

// HTTP client for calling AutoDispatch API
builder.Services.AddHttpClient("AutoDispatchApi", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Services
builder.Services.AddSingleton(new ShadowModeOptions
{
    SqlConnection = sqlConnection,
    ApiBaseUrl = apiBaseUrl
});
builder.Services.AddScoped<ShadowModeProcessor>();
builder.Services.AddTransient<ShadowModeInvocable>();

var host = builder.Build();

var seconds = int.Parse(Environment.GetEnvironmentVariable("ScheduleFrequencySeconds") ?? "30");
host.Services.UseScheduler(scheduler =>
{
    scheduler.OnWorker("AutoDispatchService");
    var schedule = scheduler.Schedule<ShadowModeInvocable>();
    if (seconds >= 60)
        schedule.EveryMinute().PreventOverlapping("AutoDispatchService");
    else
        schedule.EverySeconds(seconds).PreventOverlapping("AutoDispatchService");
});

// Write health file immediately so K8S liveness probe passes during startup
try
{
    using var _ = File.Create("/tmp/healthy");
    Log.Information("Health file /tmp/healthy created on startup");
}
catch (Exception ex)
{
    Log.Error(ex, "Failed to create health file /tmp/healthy");
}

Log.Information("AutoDispatchService starting — schedule every {Seconds}s, API: {ApiUrl}", seconds, apiBaseUrl);
host.Run();
