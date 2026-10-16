namespace MusicHoarder.Api.Review.Retry;

internal sealed class TargetedRetryBackgroundService(IServiceScopeFactory scopes, ILogger<TargetedRetryBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<TargetedRetryProcessor>().RunNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning(e, "Targeted retry interrupted; durable progress remains available"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
