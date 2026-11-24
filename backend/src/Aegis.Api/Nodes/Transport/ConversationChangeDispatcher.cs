using Aegis.Application.Chat;
using System.Threading.Channels;

namespace Aegis.Api.Nodes.Transport;

/// <summary>One process-local wakeup and a bounded set of persisted conversation IDs.</summary>
public sealed class ConversationChangeDispatcher(NodeConnectionRegistry connections)
    : BackgroundService, IConversationChangePublisher
{
    private const int MaximumPendingConversations = 64;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(100);
    private readonly object gate = new();
    private readonly HashSet<Guid> pending = [];
    private bool reconcileAll;
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        AllowSynchronousContinuations = false
    });

    public void Publish(Guid conversationId)
    {
        if (conversationId == Guid.Empty) return;
        lock (gate)
        {
            if (!reconcileAll)
            {
                pending.Add(conversationId);
                if (pending.Count > MaximumPendingConversations)
                {
                    // Global invalidation subsumes every pending targeted invalidation.
                    pending.Clear();
                    reconcileAll = true;
                }
            }
            wake.Writer.TryWrite(true);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await wake.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(CoalesceWindow, stoppingToken);
                Guid?[] batch;
                lock (gate)
                {
                    wake.Reader.TryRead(out _);
                    batch = reconcileAll ? [null] : pending.Select(id => (Guid?)id).ToArray();
                    pending.Clear();
                    reconcileAll = false;
                }

                foreach (var id in batch)
                    await connections.BroadcastConversationChangedAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
