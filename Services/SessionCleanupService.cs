using DV.Web.Services;

namespace DV.Web.Services;

/// <summary>
/// Placeholder cleanup service for Web UI. 
/// Actual cleanup runs only in the API to avoid multiple services competing on the same DB table.
/// This service simply logs that it is deferring to the API.
/// </summary>
public class SessionCleanupService : BackgroundService
{
    private readonly ILogger<SessionCleanupService> _logger;

    public SessionCleanupService(ILogger<SessionCleanupService> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session cleanup deferred to DV_API service");
        return Task.CompletedTask;
    }
}