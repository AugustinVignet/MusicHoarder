namespace MusicHoarder.Api.Review.Audit;

/// <summary>Native API worker, independent of browser sessions. One track then release the gate.</summary>
internal sealed class CopyAuditBackgroundService(IServiceScopeFactory scopes, ILogger<CopyAuditBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(1);
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CopyAuditProcessor>().RunNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Never publish private paths, tool stderr, connection strings or provider data.
                logger.LogWarning("Copy audit worker deferred after a storage/database failure; durable work is retained.");
                delay = TimeSpan.FromSeconds(5);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
