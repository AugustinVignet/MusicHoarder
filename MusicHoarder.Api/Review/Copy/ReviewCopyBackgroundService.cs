namespace MusicHoarder.Api.Review.Copy;

internal sealed class ReviewCopyBackgroundService(IServiceScopeFactory scopes, ILogger<ReviewCopyBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ReviewCopyProcessor>();
                if (await processor.RunNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // No source paths or raw file errors in durable public receipts.
                logger.LogWarning(e, "Verified review job interrupted; durable progress will be resumed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
