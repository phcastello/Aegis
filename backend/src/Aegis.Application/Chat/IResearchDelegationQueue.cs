using Aegis.Domain.Entities;
using Aegis.Application.Prompts;

namespace Aegis.Application.Chat;

public interface IResearchDelegationQueue
{
    public const int MaximumBriefLength = 4_000;

    // Stage a job in the turn's unit of work so it becomes visible with the acknowledgement.
    void Stage(ChatMessage userMessage, string brief, Guid? voiceSessionId = null, bool isPython = false,
        bool isVideo = false, InstructionSettings? instructions = null);
}
