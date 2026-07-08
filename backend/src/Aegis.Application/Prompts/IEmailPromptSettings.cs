namespace Aegis.Application.Prompts;

public interface IEmailPromptSettings
{
    int MaxCandidatesPerManualBriefing { get; }

    int MaxEmailsToReadPerBriefing { get; }

    int MaxEmailBriefingBodyChars { get; }

    int MaxEmailFullBodyChars { get; }
}
