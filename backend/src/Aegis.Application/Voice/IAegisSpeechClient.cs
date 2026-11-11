// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Voice;

public interface IAegisSpeechClient
{
    Task<SpeechStreamResponse> StreamSpeechAsync(SpeechRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(string nativeSpeechRequestId, CancellationToken cancellationToken = default);
    Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
