using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Application.Models;
using Aegis.Application.Prompts;

namespace Aegis.Application.Voice;

/// <summary>A terminal reply, not an executable tool: never starts another model round.</summary>
public static class HandsFreeReply
{
    public const string ToolName = "hands_free_reply";
    public const string Origin = "hands_free";
    public const string Close = "close";

    public static ModelToolDefinition Definition { get; } = new(
        ToolName,
        "Finalize este turno por voz com a resposta falada e a decisão de continuação. " +
        "Use continue quando esperar uma resposta do proprietário; use close quando o pedido estiver concluído. " +
        "Não executa ações nem confirma ferramentas. Deve ser a única chamada desta resposta final.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                speech = new { type = "string", description = "Resposta final completa, em português brasileiro." },
                continuation = new { type = "string", @enum = new[] { "continue", Close } }
            },
            required = new[] { "speech", "continuation" },
            additionalProperties = false
        }));

    public static string UserContent(SendMessageRequest request)
    {
        var content = request.Content.Trim();
        if (!request.IsHandsFree) return content;
        if (request.VoiceSessionId is null || request.VoiceSessionId == Guid.Empty)
            throw new ArgumentException("Uma sessão de voz válida é necessária.");
        // The native pre-roll deliberately includes the name. A wake-only activation is not a command.
        if (content.StartsWith("Aegis", StringComparison.OrdinalIgnoreCase) &&
            (content.Length == 5 || !char.IsLetterOrDigit(content[5])))
            content = content[5..].TrimStart(' ', '\t', '\r', '\n', ',', '.', ':', ';', '!', '?', '—', '-');
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Nenhum pedido foi reconhecido.");
        return content;
    }

    public static PromptBuildResult AddOperationalContext(PromptBuildResult prompt, SendMessageRequest request)
    {
        if (!request.IsHandsFree) return prompt;
        var context = $"Origem operacional: hands_free; sessão de voz: {request.VoiceSessionId:D}. " +
            "O áudio ocorre no assistente Android e pode estar com a tela bloqueada. " +
            "Mantenha todas as confirmações e verificações normais de ferramentas. " +
            "Ao concluir o turno, use hands_free_reply como única chamada, com speech contendo a resposta final " +
            "e continuation=continue somente quando uma continuação for útil; caso contrário, close. " +
            "continue abre no máximo dois segundos para início de fala após o fim real da reprodução. " +
            "Não altere permissões ou exigências de confirmação por ser voz.";
        var item = JsonSerializer.SerializeToElement(new { role = "developer", content = context });
        return prompt with
        {
            RuntimeContext = Join(prompt.RuntimeContext, context),
            AuditRuntimeContext = Join(prompt.AuditRuntimeContext ?? prompt.RuntimeContext, context),
            InputItems = [.. prompt.InputItems, item],
            AuditInputItems = [.. prompt.AuditInputItems ?? prompt.InputItems, item]
        };
    }

    public static bool TryRead(IReadOnlyList<ModelToolCall> calls, out string speech, out string continuation)
    {
        speech = string.Empty;
        continuation = Close;
        if (!calls.Any(call => call.Name == ToolName)) return false;
        // A terminal answer cannot accompany action calls: never execute part of an ambiguous batch.
        if (calls.Count != 1) throw new InvalidOperationException("A resposta por voz contém chamadas incompatíveis.");
        var arguments = calls[0].Arguments;
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("speech", out var text) || text.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(text.GetString()))
            throw new InvalidOperationException("A resposta por voz está vazia ou inválida.");
        speech = text.GetString()!.Trim();
        if (arguments.TryGetProperty("continuation", out var decision) &&
            decision.ValueKind == JsonValueKind.String && decision.GetString() == "continue")
            continuation = "continue";
        return true;
    }

    private static string Join(string? current, string value) =>
        string.IsNullOrWhiteSpace(current) ? value : current + "\n" + value;
}
