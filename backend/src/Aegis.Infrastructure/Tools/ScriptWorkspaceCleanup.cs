using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aegis.Infrastructure.Tools;

internal sealed class ScriptWorkspaceCleanup(ScriptWorkspace workspace, ILogger<ScriptWorkspaceCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try { await workspace.SweepAsync(stoppingToken); }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not clean script workspaces: {ErrorType}", e.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
