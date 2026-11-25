using System.Text;
using System.Text.Json;
using Aegis.Application.Models;

namespace Aegis.Application.Voice;

/// <summary>The terminal call commits each complete array string. An emitted unit is immutable.</summary>
public static class FinalSpeechSegments
{
    public const string ToolName = "hands_free_reply_segments";
    public const int MaxSegments = 48;
    public const int MaxSegmentCharacters = 1500;
    public static ModelToolDefinition Definition { get; } = new(ToolName,
        "Finalize a resposta falada deste turno após concluir as ferramentas necessárias. " +
        "segments contém unidades finais em ordem, separadas em limites linguísticos naturais (frase ou oração completa), " +
        "preservando abreviações, números, estilo, conteúdo e ordem natural. Cada unidade é definitiva e pode ser falada imediatamente; " +
        "não inclua raciocínio, comentários intermediários ou ações ainda não concluídas. " +
        "Use uma única chamada terminal, sem outras ferramentas. continuation é continue quando esperar resposta do proprietário, ou close.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                segments = new { type = "array", items = new { type = "string", minLength = 1, maxLength = MaxSegmentCharacters }, minItems = 1, maxItems = MaxSegments },
                continuation = new { type = "string", @enum = new[] { "continue", "close" } }
            },
            required = new[] { "segments", "continuation" }, additionalProperties = false
        }));

    public static string[] Read(IReadOnlyList<ModelToolCall> calls, out string continuation)
    {
        if (calls.Count != 1 || calls[0].Name != ToolName)
            throw new InvalidOperationException("A resposta segmentada deve ser a única chamada terminal.");
        var args = calls[0].Arguments;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("segments", out var segments) ||
            segments.ValueKind != JsonValueKind.Array || !args.TryGetProperty("continuation", out var decision) ||
            decision.ValueKind != JsonValueKind.String || decision.GetString() is not ("continue" or "close") ||
            args.EnumerateObject().Count() != 2)
            throw new InvalidOperationException("Resposta segmentada inválida.");
        continuation = decision.GetString()!;
        var result = segments.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "").ToArray();
        if (result.Length is < 1 or > MaxSegments) throw new InvalidOperationException("Quantidade de segmentos inválida.");
        foreach (var text in result) Validate(text);
        return result;
    }

    public static void Validate(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSegmentCharacters || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new InvalidOperationException("Unidade de fala inválida.");
    }
}

/// <summary>Reads only fully closed JSON strings in the root segments array, never partial values.</summary>
public sealed class FinalSpeechSegmentParser
{
    private readonly StringBuilder buffer = new();
    private readonly List<string> emitted = [];
    public IReadOnlyList<string> Emitted => emitted;
    public IReadOnlyList<string> Append(string delta)
    {
        buffer.Append(delta);
        if (buffer.Length > 100_000) throw new InvalidOperationException("Resposta segmentada excede o limite.");
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(buffer.ToString()), isFinalBlock: false, state: default);
        var current = new List<string>();
        var segments = false;
        string? property = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                property = reader.GetString();
            else if (reader.TokenType == JsonTokenType.StartArray && reader.CurrentDepth == 1)
            {
                if (property != "segments" || segments) throw new InvalidOperationException("Formato segmentado inválido.");
                segments = true;
            }
            else if (segments && reader.TokenType == JsonTokenType.String && reader.CurrentDepth == 2)
            {
                var text = reader.GetString()!;
                FinalSpeechSegments.Validate(text);
                current.Add(text);
                if (current.Count > FinalSpeechSegments.MaxSegments) throw new InvalidOperationException("Muitos segmentos.");
            }
            else if (segments && reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1)
                segments = false;
            else if (segments && reader.CurrentDepth >= 2)
                throw new InvalidOperationException("O segmento precisa ser uma string final.");
        }
        if (current.Count < emitted.Count || !current.Take(emitted.Count).SequenceEqual(emitted))
            throw new InvalidOperationException("Uma unidade de fala final foi alterada.");
        var added = current.Skip(emitted.Count).ToArray();
        emitted.AddRange(added);
        return added;
    }
}
