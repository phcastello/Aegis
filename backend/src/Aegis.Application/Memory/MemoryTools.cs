using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public abstract class MemoryToolBase(MemoryService service) : IAegisTool
{
    protected MemoryService Service => service;
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement ParametersSchema { get; }
    protected static JsonElement Schema(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    protected static AegisToolResult Ok(object data) => new(true, JsonSerializer.Serialize(data, JsonOptions));
    protected static object View(MemoryRecord record) => new { memoryId = record.Id, content = record.Content, status = record.Status.ToString(), validFrom = record.ValidFrom, validUntil = record.ValidUntil };
    protected static DateTimeOffset? Instant(string? input)
    {
        if (input is null) return null;
        if (!Regex.IsMatch(input, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            throw new ArgumentException("Use RFC3339 com offset explícito em validFrom/validUntil.");
        return instant.ToUniversalTime();
    }
    public async Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(x => !ParametersSchema.GetProperty("properties").TryGetProperty(x.Name, out _)))
                throw new ArgumentException("Parâmetros inválidos.");
            if (ParametersSchema.GetProperty("required").EnumerateArray().Any(x => !arguments.TryGetProperty(x.GetString()!, out var value) || value.ValueKind == JsonValueKind.Null))
                throw new ArgumentException("Faltam parâmetros obrigatórios.");
            return await RunAsync(arguments, context, cancellationToken);
        }
        catch (MemoryException e) { return new(false, JsonSerializer.Serialize(new { error = e.Code, message = e.Message }), e.Code); }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException or FormatException)
        { return new(false, JsonSerializer.Serialize(new { error = "invalid_tool_arguments", message = e.Message }), "invalid_tool_arguments"); }
    }
    protected abstract Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct);
}

public sealed class MemoryRememberTool(MemoryService service) : MemoryToolBase(service)
{
    public override string Name => "memory_remember";
    public override string Description => "Guarda conhecimento persistente somente quando o usuário pede claramente para lembrar/guardar uma informação. Não use para declaração casual nem para lembrete com horário, RAM, cache ou memória virtual. Cria imediatamente, sem nova confirmação. content é uma frase canônica fiel ao pedido; pode tratar de qualquer pessoa, projeto ou entidade. Não forneça IDs de origem.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"content":{"type":"string","minLength":1,"maxLength":2000},"validFrom":{"type":"string","description":"Opcional; RFC3339 com offset, apenas se o usuário explicitou validade."},"validUntil":{"type":"string","description":"Opcional; RFC3339 com offset, apenas se o usuário explicitou validade."}},"required":["content"],"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
    {
        var a = args.Deserialize<Arguments>(JsonOptions)!;
        var (record, deduplicated) = await Service.RememberAsync(a.Content, Instant(a.ValidFrom), Instant(a.ValidUntil), context, ct);
        return Ok(new { memory = View(record), deduplicated });
    }
    private sealed record Arguments(string Content, string? ValidFrom = null, string? ValidUntil = null);
}

public sealed class MemorySearchTool(MemoryService service) : MemoryToolBase(service)
{
    public override string Name => "memory_search";
    public override string Description => "Busca conhecimento persistente com memória semântica e relações relevantes. Use para perguntas sobre fatos lembrados, inclusive históricos; asOf é opcional em RFC3339 com offset quando o usuário especifica um instante passado. Retorna conhecimento canônico válido naquele instante e referências temporárias para correção/esquecimento. Não busca RAM, cache, Calendar, Gmail nem lembretes.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":200},"limit":{"type":"integer","minimum":1,"maximum":30},"asOf":{"type":"string","description":"Opcional: instante histórico RFC3339 com offset."}},"required":["query"],"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
    {
        var a = args.Deserialize<Arguments>(JsonOptions)!;
        var asOf = Instant(a.AsOf);
        var userQuestion = context.UserContent.Trim();
        var useOriginalQuestion = userQuestion.Length is > 0 and <= 200 && userQuestion.EndsWith('?');
        var result = await Service.SearchHybridAsync(useOriginalQuestion ? userQuestion : a.Query,
            a.Limit, asOf, context.ConversationId, ct);
        // A model-formulated query can be broader than the user's actual question. For a
        // concrete question, keep the original wording as the relevance anchor. Anaphoric
        // questions can still fall back to the model's resolved query when it found nothing.
        if (useOriginalQuestion && result.Memories.Count == 0 && result.Paths.Count == 0 &&
            !string.Equals(userQuestion, a.Query, StringComparison.OrdinalIgnoreCase))
            result = await Service.SearchHybridAsync(a.Query, a.Limit, asOf, context.ConversationId, ct);
        var relationItems = result.Paths.SelectMany(x => x.Relations.Select(r => new
        {
            id = r.Id,
            subject = x.Entities.First(e => e.Id == r.SubjectEntityId).CanonicalName,
            predicate = r.Predicate,
            @object = x.Entities.First(e => e.Id == r.ObjectEntityId).CanonicalName,
            r.ValidFrom, r.ValidUntil
        })).DistinctBy(x => (x.subject, x.predicate, x.@object, x.ValidFrom, x.ValidUntil))
            .Take(a.Limit).ToArray();
        var response = Ok(new { memories = result.Memories.Select((record, i) => new { position = i + 1, memory = View(record) }),
            relations = relationItems.Select(x => new { x.subject, x.predicate, x.@object, x.ValidFrom, x.ValidUntil }),
            corrections = (result.Corrections ?? []).Select(x => new
                { incorrectStatement = x.IncorrectContent, correctedStatement = x.ReplacementContent,
                    meaning = "A declaração antiga era incorreta; não foi um fato histórico verdadeiro." }),
            searchMode = result.Mode });
        await Service.RecordConsultedAsync(context, result.Memories.Select(x => x.Id).ToArray(),
            relationItems.Select(x => x.id).ToArray(), ct);
        return response;
    }
    private sealed record Arguments(string Query, int Limit = MemoryService.DefaultSearchLimit, string? AsOf = null);
}

public sealed class MemoryUpdateTool(MemoryService service) : MemoryToolBase(service)
{
    public override string Name => "memory_update";
    public override string Description => "Corrige uma memória específica mediante pedido do usuário. Requer memoryId real observado nesta conversa por memory_search/remember/update, nunca inventado; consulte primeiro e pergunte se houver ambiguidade. Substitui por nova memória, preservando histórico. Não execute correções em lote.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"memoryId":{"type":"string"},"content":{"type":"string","minLength":1,"maxLength":2000},"validFrom":{"type":"string"},"validUntil":{"type":"string"}},"required":["memoryId","content"],"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
    {
        var a = args.Deserialize<Arguments>(JsonOptions)!;
        return Ok(new { memory = View(await Service.UpdateAsync(a.MemoryId, a.Content, Instant(a.ValidFrom), Instant(a.ValidUntil), context, ct)) });
    }
    private sealed record Arguments(Guid MemoryId, string Content, string? ValidFrom = null, string? ValidUntil = null);
}

public sealed class MemoryForgetTool(MemoryService service) : MemoryToolBase(service)
{
    public override string Name => "memory_forget";
    public override string Description => "Esquece uma única memória específica observada nesta conversa por memory_search/remember/update. Requer memoryId real, nunca inventado. Consulte primeiro e pergunte quando o alvo for ambíguo. Não execute pedidos amplos como 'esquece tudo sobre mim' nesta etapa.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"memoryId":{"type":"string"}},"required":["memoryId"],"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct) =>
        Ok(new { memory = View(await Service.ForgetAsync(args.GetProperty("memoryId").GetGuid(), context, ct)) });
}
