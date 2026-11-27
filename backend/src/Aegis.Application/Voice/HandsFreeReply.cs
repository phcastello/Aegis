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
    public const string DeliveryGuidance =
        "Comece direto pela conclusão ou providência principal quando houver uma. Se Pedro pediu apenas um texto para ditar, " +
        "fale só o texto pedido. Mesmo em voz, vá até o fim da resposta, com os fatos, números e ressalvas pertinentes.";

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
        if (request.InterruptedGeneration is { } interruption &&
            (!request.IsHandsFree || !request.VoiceSegments || !request.AllowSilentIgnore ||
             request.InterruptedPlayback is not null || interruption.TurnId == Guid.Empty ||
             request.TurnId is null || request.TurnId == Guid.Empty ||
             interruption.TurnId == request.TurnId || interruption.PreviousContent?.Length > 16384))
            throw new ArgumentException("A interrupção da geração por voz é inválida.");
        var content = request.Content?.Trim() ?? "";
        if (content.Length == 0 && request.HasAttachments) content = "Analise os arquivos anexados.";
        if (!request.IsHandsFree) return content;
        if (request.VoiceSessionId is null || request.VoiceSessionId == Guid.Empty)
            throw new ArgumentException("Uma sessão de voz válida é necessária.");
        // The native pre-roll deliberately includes the name. A wake-only activation is not a command.
        if (request.InterruptionReceipt is null && content.StartsWith("Aegis", StringComparison.OrdinalIgnoreCase) &&
            (content.Length == 5 || !char.IsLetterOrDigit(content[5])))
            content = content[5..].TrimStart(' ', '\t', '\r', '\n', ',', '.', ':', ';', '!', '?', '—', '-');
        if (string.IsNullOrWhiteSpace(content) && !request.AllowSilentIgnore)
            throw new ArgumentException("Nenhum pedido foi reconhecido.");
        if (request.AllowSilentIgnore && (request.TurnId is null || request.TurnId == Guid.Empty))
            throw new ArgumentException("Uma identificação de turno válida é necessária.");
        return content;
    }

    public static PromptBuildResult AddOperationalContext(PromptBuildResult prompt, SendMessageRequest request)
    {
        if (!request.IsHandsFree) return prompt;
        var context = $"Origem operacional: hands_free; sessão de voz: {request.VoiceSessionId:D}. " +
            "O pedido chegou por voz e a tela do dispositivo pode estar bloqueada. " +
            DeliveryGuidance + " " +
            "Mantenha todas as confirmações e verificações normais de ferramentas. " +
            "Ao concluir o turno, use hands_free_reply como única chamada, com speech contendo a resposta final " +
            "e continuation=continue somente quando uma continuação for útil; caso contrário, close. " +
            "continue abre no máximo dois segundos para início de fala após o fim real da reprodução. " +
            "Não altere permissões ou exigências de confirmação por ser voz.";
        if (request.VoiceSegments)
            context = context.Replace("hands_free_reply como única chamada, com speech contendo a resposta final",
                "hands_free_reply_segments como única chamada, com segments contendo unidades finais ordenadas da resposta");
        context += " Memória semântica é consultada sob demanda nesta rodada: quando um fato pessoal necessário não estiver no histórico ou contexto, use memory_search; não adivinhe. Considere também referências indiretas, preferências pertinentes, correções e validade temporal. Para comparar o presente com o passado, consulte cada instante separadamente, usando asOf na busca histórica; ausência na busca atual não prova ausência histórica. Se as buscas pertinentes não trouxerem evidência, reconheça a ausência e não use fatos de outra pessoa como substitutos.";
        if (request.AllowSilentIgnore && request.InterruptionReceipt is null) context += " " + HandsFreeDirection.Guidance;
        if (request.InterruptionReceipt.HasValue) context += " A nova fala já foi aceita como dirigida à Aegis.";
        if (request.InterruptedGeneration is not null)
            context += " O usuário interrompeu a geração anterior antes de ouvir uma resposta. Considere o pedido anterior e a nova fala em conjunto, priorizando a correção mais recente. Não suponha que uma ação externa foi ou não executada apenas porque a geração foi interrompida.";
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
