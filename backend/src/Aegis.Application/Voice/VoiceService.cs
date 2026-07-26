using Aegis.Application.Common;
using Aegis.Application.Turns;
using Aegis.Domain;

namespace Aegis.Application.Voice;

public sealed class VoiceService(
    IAegisDbContext dbContext,
    IActiveTurnRegistry turnRegistry,
    IAegisSpeechClient speechClient,
    VoiceStatusCache statusCache) : IVoiceService
{
    public async Task<VoiceStream> StartSpeechAsync(StartSpeechRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TurnId == Guid.Empty || request.SpeechRequestId == Guid.Empty || request.AssistantMessageId == Guid.Empty)
        {
            throw new ArgumentException("Valid turn, speech request, and assistant message identifiers are required.");
        }

        var message = await dbContext.GetChatMessageAsync(request.AssistantMessageId, cancellationToken)
            ?? throw new KeyNotFoundException("The assistant message was not found.");
        if (!string.Equals(message.Role, ChatRoles.Assistant, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only Aegis messages can be spoken.");
        }

        var turn = turnRegistry.Find(request.TurnId);
        if (turn is null)
        {
            turn = turnRegistry.Register(request.TurnId, message.ConversationId);
            turnRegistry.TryTransition(turn.TurnId, TurnStatus.Created, TurnStatus.GeneratingText);
            if (!turnRegistry.TrySetTextCompleted(turn.TurnId, message.Id))
            {
                throw new OperationCanceledException(turn.Cancellation.Token);
            }
        }

        if (turn.ConversationId != message.ConversationId ||
            (turn.AssistantMessageId is { } knownMessage && knownMessage != message.Id) ||
            !turnRegistry.IsCurrent(turn.ConversationId, turn.TurnId))
        {
            throw new InvalidOperationException("The speech request does not belong to the active conversation turn.");
        }

        var nativeRequestId = NativeSpeechRequestId.Create();
        if (!turnRegistry.TryBeginSpeech(turn.TurnId, request.SpeechRequestId, nativeRequestId))
        {
            throw new InvalidOperationException("The turn is no longer eligible for speech.");
        }

        try
        {
            var upstream = await speechClient.StreamSpeechAsync(
                new SpeechRequest(turn.TurnId, request.SpeechRequestId, nativeRequestId, message.Content, message.ConversationId),
                turn.Cancellation.Token);
            if (!turnRegistry.TrySetStreamingAudio(turn.TurnId, request.SpeechRequestId))
            {
                await upstream.DisposeAsync();
                await speechClient.CancelAsync(nativeRequestId, CancellationToken.None);
                throw new OperationCanceledException(turn.Cancellation.Token);
            }

            return new VoiceStream(turn.TurnId, request.SpeechRequestId, turn.Cancellation.Token, upstream);
        }
        catch (OperationCanceledException) when (turn.Cancellation.IsCancellationRequested)
        {
            await speechClient.CancelAsync(nativeRequestId, CancellationToken.None);
            throw;
        }
        catch
        {
            turnRegistry.Fail(turn.TurnId);
            await speechClient.CancelAsync(nativeRequestId, CancellationToken.None);
            throw;
        }
    }

    public async Task<CancelTurnResult> CancelSpeechAsync(Guid speechRequestId, CancellationToken cancellationToken = default)
    {
        var turn = turnRegistry.FindBySpeechRequest(speechRequestId);
        if (turn is null)
        {
            return new CancelTurnResult(speechRequestId, "cancelled", false, true);
        }

        var result = await turnRegistry.CancelAsync(turn.TurnId, "speech_stop", cancellationToken);
        if (result.SpeechCancellationRequested && !string.IsNullOrWhiteSpace(turn.NativeSpeechRequestId))
        {
            await speechClient.CancelAsync(turn.NativeSpeechRequestId, CancellationToken.None);
        }

        return result;
    }

    public async Task<CancelTurnResult> CancelTurnAsync(Guid turnId, string reason, CancellationToken cancellationToken = default)
    {
        var info = turnRegistry.GetCancellationInfo(turnId);
        var result = await turnRegistry.CancelAsync(turnId, reason, cancellationToken);
        if (result.SpeechCancellationRequested && !string.IsNullOrWhiteSpace(info?.NativeSpeechRequestId))
        {
            await speechClient.CancelAsync(info.NativeSpeechRequestId, CancellationToken.None);
        }
        return result;
    }

    public async Task CancelAllTurnsAsync(string reason, CancellationToken cancellationToken = default)
    {
        foreach (var turnId in turnRegistry.GetActiveTurnIds())
        {
            await CancelTurnAsync(turnId, reason, cancellationToken);
        }
    }

    public void CompleteTurnWithoutSpeech(Guid turnId) => turnRegistry.Complete(turnId);

    public void CompleteSpeech(Guid speechRequestId)
    {
        var turn = turnRegistry.FindBySpeechRequest(speechRequestId);
        if (turn is not null && !turn.Cancellation.IsCancellationRequested)
        {
            turnRegistry.Complete(turn.TurnId);
        }
    }

    public void FailSpeech(Guid speechRequestId)
    {
        var turn = turnRegistry.FindBySpeechRequest(speechRequestId);
        if (turn is not null) turnRegistry.Fail(turn.TurnId);
    }

    public Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        statusCache.GetAsync(cancellationToken);
}

internal static class NativeSpeechRequestId
{
    private const string Crockford32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // Native IDs are actual ULIDs; browser UUIDs remain the public correlation IDs.
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var index = 5; index >= 0; index--)
        {
            bytes[index] = (byte)(milliseconds & 0xff);
            milliseconds >>= 8;
        }
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes[6..]);

        Span<char> result = stackalloc char[26];
        var buffer = 0;
        var bits = 2; // ULID is 128 bits represented in 26 base32 characters.
        var byteIndex = 0;
        for (var index = 0; index < result.Length; index++)
        {
            while (bits < 5)
            {
                buffer = (buffer << 8) | bytes[byteIndex++];
                bits += 8;
            }
            bits -= 5;
            result[index] = Crockford32[(buffer >> bits) & 31];
            buffer &= (1 << bits) - 1;
        }
        return new string(result);
    }
}
