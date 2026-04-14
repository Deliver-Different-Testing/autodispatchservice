using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

namespace AutoDispatchService.Services;

public class HealthCheckPublisher : IHealthCheckPublisher
{
    private const string _fileName = "/tmp/healthy";
    private HealthStatus _prevStatus = HealthStatus.Unhealthy;

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        Log.Information("Health check: {Status}, writing to {FileName}", report.Status, _fileName);
        var fileExists = _prevStatus == HealthStatus.Healthy;

        if (report.Status == HealthStatus.Healthy)
        {
            using var _ = File.Create(_fileName);
        }
        else if (fileExists)
        {
            File.Delete(_fileName);
        }

        _prevStatus = report.Status;
        return Task.CompletedTask;
    }
}
