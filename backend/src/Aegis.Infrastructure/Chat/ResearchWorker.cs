using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Aegis.Infrastructure.Chat;

public sealed class ResearchWorker(IServiceScopeFactory scopes, TimeProvider clock,
    WebSearchOptions options, ILogger<ResearchWorker> logger) : BackgroundService
{
    private const string Instructions =
        "Você é a Aegis e está concluindo uma pesquisa web solicitada pelo usuário. " +
        "Em uma tarefa delegada, o pedido já inclui o contexto público necessário. " +
        "Use a pesquisa nativa para verificar as informações públicas pertinentes. " +
        "Responda em português quando o pedido não indicar outro idioma. " +
        "Quando não puder verificar uma informação necessária, diga o que ficou sem confirmação.";

    public async Task<bool> ProcessNextAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return false;
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<ResearchJobStore>();
        var job = await store.ClaimAsync(stoppingToken);
        if (job is null) return false;

        var remaining = job.DeadlineAt - clock.GetUtcNow();
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = StopWhenCancelledAsync(job.JobId, jobCancellation, monitorCancellation.Token);
        try
        {
            // Reuse hosted web search, budgets, evidence and audit without exposing local side-effect tools.
            var loop = new AegisToolLoop(services.GetRequiredService<IAegisModelClient>(),
                new AegisToolRegistry([]), services.GetRequiredService<ILogger<AegisToolLoop>>(),
                services.GetRequiredService<AegisMetrics>(), options);
            var request = new ModelRequest(Instructions, job.Content);
            var context = new ToolExecutionContext(job.ConversationId, job.UserMessageId, job.Content);
            var content = new StringBuilder();
            var progress = new ResearchProgress();

            await foreach (var chunk in loop.StreamAsync(request, context, jobCancellation.Token))
            {
                jobCancellation.Token.ThrowIfCancellationRequested();
                if (progress.Update(chunk) &&
                    !await store.ReportAsync(job.JobId, progress.Phase, progress.Sources, jobCancellation.Token))
                    return true;
                content.Append(chunk.Content);
                if (!chunk.IsDone) continue;

                if (string.IsNullOrWhiteSpace(chunk.Model) || chunk.AuditData is null ||
                    string.IsNullOrWhiteSpace(content.ToString()))
                    break;

                await store.CompleteAsync(job, new ResearchJobResult(content.ToString(), chunk.Model,
                    chunk.MetadataJson, chunk.AuditData), jobCancellation.Token);
                return true;
            }

            await store.FailAsync(job.JobId, "research_incomplete", null, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            // If the user already cancelled, the terminal status is unchanged.
            await store.FailAsync(job.JobId, deadline.IsCancellationRequested
                ? "research_timeout" : "research_interrupted", null, stoppingToken);
        }
        catch (LlmRequestException e)
        {
            await store.FailAsync(job.JobId, "research_unavailable", e.AuditData, stoppingToken);
            logger.LogWarning("Background research provider unavailable for job {JobId}", job.JobId);
        }
        catch (Exception e)
        {
            await store.FailAsync(job.JobId, "research_unavailable", null, stoppingToken);
            logger.LogWarning("Background research failed for job {JobId}: {ErrorType}", job.JobId, e.GetType().Name);
        }
        finally
        {
            await monitorCancellation.CancelAsync();
            await monitor;
        }

        return true;
    }

    private async Task StopWhenCancelledAsync(Guid id, CancellationTokenSource provider, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ResearchJobStore>();
                if (!await store.IsRunningAsync(id, ct))
                {
                    await provider.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogWarning("Background research cancellation check failed: {ErrorType}", e.GetType().Name);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), clock, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogWarning("Background research worker paused: {ErrorType}", e.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}
