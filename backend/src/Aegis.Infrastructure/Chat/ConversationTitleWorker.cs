using Aegis.Application.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aegis.Infrastructure.Chat;

public sealed class ConversationTitleWorker(
    IConversationTitleJobQueue titleJobQueue,
    IServiceScopeFactory scopeFactory,
    ILogger<ConversationTitleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ConversationTitleJob job;
            try
            {
                job = await titleJobQueue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var titleService = scope.ServiceProvider.GetRequiredService<IConversationTitleService>();
                await titleService.GenerateTitleAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not generate a conversation title for {ConversationId}.",
                    job.ConversationId);
            }
        }
    }
}
