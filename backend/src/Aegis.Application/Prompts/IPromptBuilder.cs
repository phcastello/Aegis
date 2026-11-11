// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using Aegis.Domain.Entities;

namespace Aegis.Application.Prompts;

public interface IPromptBuilder
{
    Task<PromptBuildResult> BuildPromptAsync(
        IReadOnlyList<ChatMessage> recentHistory,
        string currentUserMessage,
        string? pendingActionState = null,
        CancellationToken cancellationToken = default,
        string? automaticMemoryContext = null);
}
