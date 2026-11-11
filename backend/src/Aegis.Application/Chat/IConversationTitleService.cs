// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Chat;

public interface IConversationTitleService
{
    Task GenerateTitleAsync(ConversationTitleJob job, CancellationToken cancellationToken = default);
}
