using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Tools;

namespace Aegis.Application.Reminders;

public abstract class ReminderToolBase(ReminderService service) : IAegisTool
{
    protected ReminderService Service => service;
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement ParametersSchema { get; }
    protected static JsonElement Schema(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    protected static AegisToolResult Ok(object data) => new(true, JsonSerializer.Serialize(data, JsonOptions));
    public async Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(p => !ParametersSchema.GetProperty("properties").TryGetProperty(p.Name, out _)))
                throw new ArgumentException("Parâmetros inválidos.");
            if (arguments.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Null &&
                ParametersSchema.GetProperty("properties").GetProperty(p.Name).GetProperty("type").ValueKind == JsonValueKind.String))
                throw new ArgumentException("Omita campos opcionais para preservar valores; não envie null neste campo.");
            if (ParametersSchema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(p => !arguments.TryGetProperty(p.GetString()!, out var v) || v.ValueKind == JsonValueKind.Null))
                throw new ArgumentException("Faltam parâmetros obrigatórios.");
            return await RunAsync(arguments, context, cancellationToken);
        }
        catch (ReminderException e) { return Error(e.Code, e.Message); }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException or FormatException)
        { return Error("invalid_tool_arguments", "Parâmetros ou referência inválidos. " + (e is ArgumentException ? e.Message : "Consulte reminder_list e use os campos do schema.")); }
    }
    private static AegisToolResult Error(string code, string message) => new(false, JsonSerializer.Serialize(new { error = code, message }, JsonOptions), code);
    protected abstract Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct);
}
public sealed class ReminderCreateTool(ReminderService service) : ReminderToolBase(service)
{
    public override string Name => "reminder_create";
    public override string Description => "Cria imediatamente um lembrete único interno da Aegis SOMENTE após pedido explícito ou aceitação suficiente do usuário. Não requer confirmação em segundo turno. Para 'me lembra às 18h de comprar ração' e 'daqui 20 minutos', interprete o horário usando o timestamp runtime e envie dueAt absoluto RFC3339 com offset. Não cria eventos Calendar. Sem horário suficiente ou delegação, pergunte. Não use por mera menção a prazo, lembrar ou aviso. Se notificações indisponíveis, a tool falha sem criar: oriente ativação na interface, sem prometer aviso.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":600,"description":"Texto final da notificação no disparo; somente a tarefa, sem instruções de agendamento ou referências relativas ao momento da criação."},"dueAt":{"type":"string","description":"Instante futuro absoluto RFC3339 com offset explícito, ex. 2026-10-01T18:00:00-03:00. Use America/Sao_Paulo como referência runtime."},"timeZoneId":{"type":"string","description":"Timezone IANA associado ao pedido; padrão America/Sao_Paulo."}},"required":["text","dueAt"],"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct) =>
        Ok(ReminderService.View(await Service.CreateAsync(context.ConversationId, args.GetProperty("text").GetString()!, args.GetProperty("dueAt").GetString()!, ct, args.TryGetProperty("timeZoneId", out var zone) ? zone.GetString() : null)));
}
public sealed class ReminderListTool(ReminderService service) : ReminderToolBase(service)
{
    public override string Name => "reminder_list";
    public override string Description => "Lista lembretes internos ativos da Aegis, em ordem de horário. Para 'quais lembretes tenho', não use Calendar. Pode filtrar intervalo [timeMin,timeMax) RFC3339 com offset. Para 'essa semana', calcule intervalo no timezone runtime. Retorna reminderId real e registra referências nesta conversa por 30 minutos. Para alterar/cancelar algo não observado, consulte primeiro; preserve a ordem para referências como 'o segundo'. Pergunte se múltiplos resultados correspondem ao alvo.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"timeMin":{"type":["string","null"]},"timeMax":{"type":["string","null"]},"limit":{"type":"integer","minimum":1,"maximum":50}},"additionalProperties":false}""");
    private sealed record Arguments(string? TimeMin = null, string? TimeMax = null, int Limit = 20);
    protected override async Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
    {
        var a = args.Deserialize<Arguments>(JsonOptions)!;
        var items = await Service.ListAsync(context.ConversationId, a.TimeMin, a.TimeMax, a.Limit, ct);
        return Ok(new { reminders = items.Take(a.Limit).Select(ReminderService.View), hasMore = items.Count > a.Limit });
    }
}
public sealed class ReminderUpdateTool(ReminderService service) : ReminderToolBase(service)
{
    public override string Name => "reminder_update";
    public override string Description => "Altera imediatamente texto e/ou horário de um lembrete futuro interno da Aegis solicitado pelo usuário. Use somente reminderId observado nesta conversa por reminder_list/create/update; nunca invente UUIDs. Consulte reminder_list se necessário, e pergunte diante de alvos ambíguos. dueAt absoluto RFC3339 com offset; omita text/dueAt para preservar. Alertas de uma reunião existente pertencem ao Calendar, não a esta tool.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"reminderId":{"type":"string"},"text":{"type":"string","minLength":1,"maxLength":600},"dueAt":{"type":"string"},"timeZoneId":{"type":"string","description":"Timezone IANA, opcional; alterar requer dueAt."}},"required":["reminderId"],"additionalProperties":false}""");
    private sealed record Arguments(Guid ReminderId, string? Text = null, string? DueAt = null, string? TimeZoneId = null);
    protected override async Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct)
    {
        var a = args.Deserialize<Arguments>(JsonOptions)!;
        return Ok(ReminderService.View(await Service.ChangeAsync(context.ConversationId, a.ReminderId, a.Text, a.DueAt, false, ct, a.TimeZoneId)));
    }
}
public sealed class ReminderCancelTool(ReminderService service) : ReminderToolBase(service)
{
    public override string Name => "reminder_cancel";
    public override string Description => "Cancela imediatamente um lembrete futuro inequívoco interno da Aegis mediante pedido do usuário. Use reminderId real observado por reminder_list/create/update nesta conversa; consulte reminder_list antes se necessário. Nunca invente ID. Se 'da ração' corresponde a múltiplos reminders, pergunte qual. Não cancela eventos nem alertas do Google Calendar.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"reminderId":{"type":"string"}},"required":["reminderId"],"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct) =>
        Ok(ReminderService.View(await Service.ChangeAsync(context.ConversationId, args.GetProperty("reminderId").GetGuid(), null, null, true, ct)));
}
