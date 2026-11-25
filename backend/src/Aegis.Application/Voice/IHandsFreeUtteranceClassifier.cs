namespace Aegis.Application.Voice;

public sealed record HandsFreeContextTurn(string Role, string Content);

/// <summary>Only clearly unrelated utterances should be discarded. Uncertainty attends.</summary>
public interface IHandsFreeUtteranceClassifier
{
    Task<bool> ShouldIgnoreAsync(string transcript, IReadOnlyList<HandsFreeContextTurn> context,
        CancellationToken cancellationToken = default);
}
