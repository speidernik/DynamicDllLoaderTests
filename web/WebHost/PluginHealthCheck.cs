using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WebHost;

public class PluginHealthCheck : IHealthCheck
{
    private readonly PluginEndpointDataSource _dataSource;

    public PluginHealthCheck(PluginEndpointDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoints = _dataSource.Endpoints;
            var pluginCount = endpoints.Count(e => e.DisplayName?.StartsWith("Plugin:") == true);

            var data = new Dictionary<string, object>
            {
                ["pluginEndpoints"] = pluginCount,
                ["totalEndpoints"] = endpoints.Count
            };

            return Task.FromResult(
                pluginCount > 0
                    ? HealthCheckResult.Healthy($"{pluginCount} plugin endpoints active", data)
                    : HealthCheckResult.Degraded("No plugin endpoints loaded", data: data)
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Error checking plugin health", ex)
            );
        }
    }
}
