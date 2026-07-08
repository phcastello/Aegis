namespace Aegis.Application.Prompts;

public sealed record EmailPromptSettings(
    int MaxCandidatesPerManualBriefing,
    int MaxEmailsToReadPerBriefing,
    int MaxEmailBriefingBodyChars,
    int MaxEmailFullBodyChars) : IEmailPromptSettings;
