using System.Text;
using Aegis.Application.Runtime;
using Aegis.Domain;
using Aegis.Domain.Entities;

namespace Aegis.Application.Prompts;

public sealed class PromptBuilder(IRuntimeContextProvider runtimeContextProvider) : IPromptBuilder
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

        prompt.AppendLine("# Current user message");
        prompt.AppendLine("Answer this message now.");
        prompt.AppendLine();
        prompt.AppendLine("<current_user_message>");
        prompt.AppendLine(currentUserMessage.Trim());
        prompt.AppendLine("</current_user_message>");
        prompt.AppendLine();
        prompt.AppendLine("Answer as Aegis, following the identity and behavior rules.");
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

            Your current version is v0.1.1.
            Your current version codename is "Finding My Voice".
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
