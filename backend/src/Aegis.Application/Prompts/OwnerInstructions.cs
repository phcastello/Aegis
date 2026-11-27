using System.Text.Json;

namespace Aegis.Application.Prompts;

public sealed record InstructionSettings(string Text, Guid Revision);

public interface IOwnerInstructions
{
    Task<InstructionSettings> ReadAsync(CancellationToken ct);
    Task<InstructionSettings?> UpdateAsync(string text, Guid expectedRevision, CancellationToken ct);
}

public static class OwnerInstructions
{
    public static void Include(List<JsonElement> input, List<JsonElement> audit, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // Keep owner-authored text below developer authority and before the current request.
        input.Insert(1, JsonSerializer.SerializeToElement(new
        {
            role = "user", content = "Minhas instruções personalizadas para as respostas da Aegis:\n" + text
        }));
        audit.Insert(1, JsonSerializer.SerializeToElement(new { role = "user", content = "[custom_instructions_redacted]" }));
    }
}
