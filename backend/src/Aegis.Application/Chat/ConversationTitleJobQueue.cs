using System.Threading.Channels;

namespace Aegis.Application.Chat;

public sealed class ConversationTitleJobQueue : IConversationTitleJobQueue
{
    private readonly Channel<ConversationTitleJob> channel = Channel.CreateUnbounded<ConversationTitleJob>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(
        ConversationTitleJob job,
        CancellationToken cancellationToken = default)
    {
        return channel.Writer.WriteAsync(job, cancellationToken);
    }

    public ValueTask<ConversationTitleJob> DequeueAsync(CancellationToken cancellationToken = default)
    {
        return channel.Reader.ReadAsync(cancellationToken);
    }
}
