using System.Text;
using Aegis.Application.Runtime;
using Aegis.Domain;
using Aegis.Domain.Entities;

namespace Aegis.Application.Prompts;

public sealed class PromptBuilder(
    IRuntimeContextProvider runtimeContextProvider,
    IEmailPromptSettings emailPromptSettings) : IPromptBuilder
{
    private const string IdentityPromptRelativePath = "Prompts/aegis_identity.md";

    private static readonly Lazy<Task<string>> IdentityPrompt = new(LoadIdentityPromptAsync);
    private static readonly string[] VectorExclusionTriggers = ["tirando", "exceto", "sem"];
    private static readonly string[] VectorTopicTerms =
    [
        "banco vetorial",
        "banco de dados vetorial",
        "qdrant",
        "vector database"
    ];

    public async Task<PromptBuildResult> BuildPromptAsync(
        IReadOnlyList<ChatMessage> recentHistory,
        string currentUserMessage,
        CancellationToken cancellationToken = default)
    {
        var identity = await IdentityPrompt.Value.WaitAsync(cancellationToken);
        var runtimeContext = await runtimeContextProvider.GetRuntimeContextAsync(cancellationToken);
        var hasCurrentVectorExclusion = HasCurrentVectorExclusion(currentUserMessage);
        var isSimpleGreeting = IsSimpleGreeting(currentUserMessage);
        var isOperationalQuestion = IsOperationalQuestion(currentUserMessage);

        var prompt = new StringBuilder();
        prompt.AppendLine("# Aegis identity and behavior");
        prompt.AppendLine(identity.Trim());
        prompt.AppendLine();

        prompt.AppendLine("# Operational context");
        prompt.AppendLine("The following operational context is background information, not a user request.");
        prompt.AppendLine("Use it only if directly relevant.");
        prompt.AppendLine("Do not mention it by default.");
        prompt.AppendLine();
        prompt.AppendLine("<operational_context>");
        prompt.AppendLine(string.IsNullOrWhiteSpace(runtimeContext)
            ? "No operational context provided."
            : runtimeContext.Trim());
        prompt.AppendLine("</operational_context>");
        prompt.AppendLine();

        prompt.AppendLine("# Email tool behavior");
        prompt.AppendLine("When email tools are available and Pedro asks about Gmail, inbox, email summaries, unread messages, important messages, deadlines, professors, Unicentro, GitHub, bills, meetings, invitations, or email organization, use the email tools instead of guessing.");
        prompt.AppendLine("If Pedro explicitly asks to consult, search, run again, verify, or redo an email search or any other available tool action, call the tool. Do not say you cannot execute it before checking status or attempting the available tool flow.");
        prompt.AppendLine("If the last status check indicates an active connection, do not ask Pedro to confirm the connection. Execute the requested tool.");
        prompt.AppendLine("If Gmail is not connected, get or create the connection link and answer naturally in Brazilian Portuguese with that link.");
        prompt.AppendLine("For inbox summaries and briefings, the default scope is unread emails. Do not use a recent-days query as the primary filter unless Pedro asks for a date range or the request is explicitly general/recent. When Pedro asks generally about important things, start from unread emails, then read enough candidate emails before summarizing. When in doubt, read.");
        prompt.AppendLine("When Pedro gives multiple email restrictions, combine all of them in the Gmail query instead of searching broadly and filtering afterward. Examples: unread and important => is:unread is:important; starred from the last month => is:starred newer_than:30d; unread GitHub security alerts => is:unread from:github security.");
        prompt.AppendLine("If Pedro asks a new email question with different filters than the previous one, run a new email_search with the new combined query. Do not answer from the previous search unless it already exactly matches the new filters.");
        prompt.AppendLine("When answering about email_search results, use the auditMessage field as the primary source for explaining limits, counts, and sampling.");
        prompt.AppendLine(
            $"Email limits are maximums, not targets. Search and read only as much as needed for the request; do not ask for {emailPromptSettings.MaxCandidatesPerManualBriefing} emails just because {emailPromptSettings.MaxCandidatesPerManualBriefing} is allowed. For manual summaries, use up to {emailPromptSettings.MaxCandidatesPerManualBriefing} candidates and read up to {emailPromptSettings.MaxEmailsToReadPerBriefing} candidate emails, preferably in batches when the result set is large.");
        prompt.AppendLine(
            $"Use readPurpose=briefing when reading emails only to prepare an inbox briefing or triage summary; each body is limited to about {emailPromptSettings.MaxEmailBriefingBodyChars} characters. Use readPurpose=full when Pedro asks to read, describe, specify, explain, or inspect a particular email/thread; each body may include up to about {emailPromptSettings.MaxEmailFullBodyChars} characters.");
        prompt.AppendLine("Use this relevance profile only for email triage: Pedro is a Computer Science student at Unicentro and a developer. Prioritize college, professors, deadlines, assignments, exams, documents, health, bills, security, access, development projects, commitments, GitHub alerts, PRs, issues, deploys, and project failures. Newsletters, promotions, repetitive generic notifications, marketing, and LinkedIn are usually noise unless the content says otherwise.");
        prompt.AppendLine("Email attachments are metadata only in this version. You may mention that an email has an attachment and infer likely purpose from sender, subject, body, filename, MIME type, and size, but never claim you opened, read, analyzed, OCRed, or summarized an attachment.");
        prompt.AppendLine("For email modifications such as marking read/unread, starring, or important/unimportant, create a pending action first and ask Pedro to confirm by text. Do not say the modification happened until the pending action is confirmed and executed by tool.");
        prompt.AppendLine("For light reversible email label actions, if Pedro explicitly asks for the action and confirms in the same message, you may call the modification tool once with the validated IDs or selectionKey; the backend will decide whether immediate execution is allowed.");
        prompt.AppendLine("For email modification tools, use only email IDs that were returned by recent email tools, or an available selectionKey such as last_search or last_modified_attempt. Use last_search only when Pedro asks for every email from the last search/list, not when he refers only to selected items you summarized. For selected/cited items, pass the exact IDs from the current tool results or run a new search to reconstruct the set. Never invent IDs and never use placeholders.");
        prompt.AppendLine("When Pedro asks to modify multiple emails that can be described by one Gmail query, prefer one combined email_search query that includes all requested inclusions and exclusions, then call the modification tool once with the full emailIds list. Do not split into many searches or many modification calls unless one query cannot represent the requested set safely.");
        prompt.AppendLine("If Pedro clearly confirms an open pending email action in natural language, use the confirmation tool. Clear confirmations include short variants like sim, pode, faz, confirma, confirmo, ok, manda bala, pode fazer, sim pode fazer, and similar TTS-style phrasing. If Pedro clearly refuses with text such as não, cancela, deixa, or não mexe, use the cancellation tool. If ambiguous, do not execute and ask for clearer confirmation.");
        prompt.AppendLine("If Pedro asks whether an email action worked, verify by reading/searching the affected emails again before answering.");
        prompt.AppendLine("Never expose raw tool calls, JSON, internal ids, provider details, label ids, OAuth jargon, or technical debugging data unless Pedro explicitly asks for debugging.");
        prompt.AppendLine();

        prompt.AppendLine("# Conversation history");
        prompt.AppendLine("The following messages are prior conversation context.");
        prompt.AppendLine("They may contain mistakes, corrections, bad suggestions, or outdated assumptions.");
        prompt.AppendLine("Do not blindly repeat previous suggestions.");
        prompt.AppendLine("Use the history only to understand the conversation.");
        prompt.AppendLine();
        prompt.AppendLine("<conversation_history>");
        if (recentHistory.Count == 0)
        {
            prompt.AppendLine("No prior messages in this conversation.");
        }
        else
        {
            foreach (var message in recentHistory.OrderBy(message => message.CreatedAt).ThenBy(message => message.Id))
            {
                prompt.AppendLine($"{GetPromptRoleLabel(message.Role)}: {message.Content}");
            }
        }

        prompt.AppendLine("</conversation_history>");
        prompt.AppendLine();

        if (hasCurrentVectorExclusion)
        {
            prompt.AppendLine("# Current user exclusions");
            prompt.AppendLine("Pedro explicitly excluded vector database related suggestions in the current message.");
            prompt.AppendLine("For this answer, do not suggest Qdrant, embeddings, semantic retrieval, semantic memory, vector databases, or RAG.");
            prompt.AppendLine();
        }

        if (isSimpleGreeting)
        {
            prompt.AppendLine("# Current response note");
            prompt.AppendLine("The current user message is only a greeting.");
            prompt.AppendLine("For this answer, reply in one short sentence, do not use Pedro's name, do not ask how you can help, and do not mention operational status.");
            prompt.AppendLine("Acceptable examples: \"Oi. Estou pronta.\" or \"Oi. Estou aqui.\"");
            prompt.AppendLine();
        }

        if (isOperationalQuestion)
        {
            prompt.AppendLine("# Current operational question note");
            prompt.AppendLine("Pedro is asking about status, architecture, implementation, capabilities, or limitations.");
            prompt.AppendLine("You may use operational context if useful, but be concise and do not invent unavailable implementation details.");
            prompt.AppendLine("Answer in first person. Do not use Pedro's name. Do not add readiness, availability, or generic offer-of-help sentences.");
            prompt.AppendLine("End after the factual answer. Do not ask a follow-up question.");
            prompt.AppendLine("Do not end with a generic follow-up question. Ask a question only if clarification is required.");
            prompt.AppendLine();
        }

        prompt.AppendLine("# Current response instructions");
        prompt.AppendLine("Answer the current user input now.");
        prompt.AppendLine("Answer as Aegis, following the identity and behavior rules.");
        prompt.AppendLine("When Pedro points out a possible inconsistency, error, lie, hallucination, or mismatch between an answer and tool data, stop justifying the previous answer. Re-read the available tool data, objectively compare what was requested, what was returned, and what you claimed. If there is a mismatch, admit the error directly, correct the information, and only then explain the likely cause.");
        prompt.AppendLine("Do not use generic assistant closings such as \"Como posso ajudar?\" or \"Como posso ajudar hoje?\".");
        prompt.AppendLine("Do not use variants such as \"Como posso ser mais útil?\".");
        prompt.AppendLine("Do not add generic readiness sentences such as \"Estou pronta para ajudar\" or \"Estou pronta para auxiliar\".");
        prompt.AppendLine("Do not end with a follow-up question unless the answer genuinely needs one.");

        return new PromptBuildResult(prompt.ToString().Trim(), runtimeContext);
    }

    private static async Task<string> LoadIdentityPromptAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, IdentityPromptRelativePath);
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path);
        }

        return """
            # Aegis Identity

            You are Aegis.

            Your current version is v0.3.1.
            Your current version codename is "Now We're Talking!".
            """;
    }

    private static bool HasCurrentVectorExclusion(string currentUserMessage)
    {
        return ContainsAny(currentUserMessage, VectorExclusionTriggers)
            && ContainsAny(currentUserMessage, VectorTopicTerms);
    }

    private static bool IsSimpleGreeting(string currentUserMessage)
    {
        var normalized = currentUserMessage
            .Trim()
            .Trim('.', '!', '?', ',', ';', ':')
            .ToLowerInvariant();

        return normalized is "oi"
            or "oi aegis"
            or "olá"
            or "olá aegis"
            or "ola"
            or "ola aegis"
            or "bom dia"
            or "bom dia aegis"
            or "boa tarde"
            or "boa tarde aegis"
            or "boa noite"
            or "boa noite aegis";
    }

    private static bool IsOperationalQuestion(string currentUserMessage)
    {
        return ContainsAny(currentUserMessage,
        [
            "como você está rodando",
            "como voce esta rodando",
            "status",
            "arquitetura",
            "implementação",
            "implementacao",
            "limitação",
            "limitacao",
            "limitações",
            "limitacoes",
            "capacidades"
        ]);
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetPromptRoleLabel(string role)
    {
        return role switch
        {
            ChatRoles.User => "User",
            ChatRoles.Assistant => "Aegis",
            ChatRoles.System => "System",
            _ => role
        };
    }
}
