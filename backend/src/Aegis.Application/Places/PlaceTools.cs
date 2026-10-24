using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Nodes;
using Aegis.Application.Tools;

namespace Aegis.Application.Places;

public abstract class PlaceTool(IPlaceService places) : IAegisTool
{
    protected IPlaceService Places => places;
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement ParametersSchema { get; }
    protected static JsonElement Schema(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    protected static AegisToolResult Ok(object value) => new(true, JsonSerializer.Serialize(value, Json));
    protected static object Summary(PlaceView place) => new { place.Id, place.Name, place.Type, place.ParentId, place.Revision, place.Aliases, hasArea = place.Circles.Count > 0 };
    public async Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try {
            if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) ||
                arguments.EnumerateObject().Any(p => !ParametersSchema.GetProperty("properties").TryGetProperty(p.Name, out _))) throw new ArgumentException();
            if (ParametersSchema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(p => !arguments.TryGetProperty(p.GetString()!, out var value) || value.ValueKind == JsonValueKind.Null)) throw new ArgumentException();
            foreach (var key in new[] { "startsAt", "endsAt" }) if (arguments.TryGetProperty(key, out var instant) && instant.ValueKind != JsonValueKind.Null) _ = Aegis.Application.Reminders.ReminderService.ParseInstant(instant.GetString()!);
            return await Run(arguments, context, cancellationToken);
        } catch (NodeException e) { return new(false, JsonSerializer.Serialize(new { error = e.Code, message = e.Message }, Json), e.Code); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException) {
            return new(false, "{\"error\":\"invalid_tool_arguments\",\"message\":\"Confira os campos estruturados e consulte os lugares/lembretes antes de usar IDs.\"}", "invalid_tool_arguments");
        }
    }
    protected abstract Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct);
}
public sealed class PlaceListTool(IPlaceService places) : PlaceTool(places)
{
    public override string Name => "place_list";
    public override string Description => "Lista lugares pessoais cadastrados, hierarquia e apelidos. Retorna IDs reais observados por 30 minutos. Cômodos herdam contexto da propriedade; não representam GPS indoor. Não invente lugares, coordenadas ou IDs. Cadastro/área são confirmados pelo usuário na interface Lugares.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var places = await Places.ListAsync(ct); await Places.RememberAsync(context.ConversationId, "place", places.Select(p => p.Id), ct);
        return Ok(new { places = places.Select(Summary) });
    }
}
public sealed class PlaceResolveTool(IPlaceService places) : PlaceTool(places)
{
    public override string Name => "place_resolve";
    public override string Description => "Resolve nome explícito ou apelido contextual como casa usando o celular pessoal selecionado; computador/RDP não determina a posição. Nome explícito prevalece. Sem contexto recente/confiável retorna candidatos para esclarecer. Para 'qualquer uma das minhas casas' envie allCandidates=true; não faça isso sem pedido explícito. IDs resolvidos ficam observados por30min. Confirme nome completo na resposta e fixe PlaceId ao criar: viajar depois não altera o alvo. Nunca use destino da tarefa como geofence: 'sair de casa para passar na padaria' requer somente casa.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"reference":{"type":"string","minLength":1,"maxLength":100},"allCandidates":{"type":"boolean"}},"required":["reference"],"additionalProperties":false}""");
    private sealed record Request(string Reference, bool AllCandidates = false);
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var request = arguments.Deserialize<Request>(Json)!; var result = await Places.ResolveAsync(request.Reference, request.AllCandidates, ct);
        var ids = result.PlaceId is { } selected ? new[] { selected } : result.Status == "resolved_set" ? result.Candidates.Select(p => p.Id) : [];
        await Places.RememberAsync(context.ConversationId, "place", ids, ct);
        return Ok(new { result.Status, result.PlaceId, result.Reason, result.LocationAgeSeconds, candidates = result.Candidates.Select(Summary) });
    }
}
public sealed class LocationStatusTool(IPlaceService places) : PlaceTool(places)
{
    public override string Name => "location_status";
    public override string Description => "Consulta a última localização do celular pessoal escolhido e sua idade, sem expor coordenadas. Para pedido de posição atual/perto de mim use refresh=true (aquisição bounded15s, depende permissões/serviços/rede). Não substitua posição ausente pela casa cadastrada; explicite idade quando só houver posição antiga. Sem fonte selecionada oriente Dispositivos. Localização aproximada é incerta, não prova presença.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"refresh":{"type":"boolean"}},"additionalProperties":false}""");
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (arguments.TryGetProperty("refresh", out var refresh) && refresh.GetBoolean()) await Places.RefreshAsync(ct);
        var settings = await Places.SettingsAsync(ct); var latest = settings.Latest;
        var place = latest?.PlaceId is { } id ? (await Places.ListAsync(ct)).SingleOrDefault(p => p.Id == id) : null;
        return Ok(new { sourceSelected = settings.SourceNodeId is not null, available = latest is not null, ageSeconds = latest?.AgeSeconds, capturedAt = latest?.CapturedAt, source = latest?.Source, place = place is null ? null : Summary(place), approximate = latest?.AccuracyMeters > 200 });
    }
}
public sealed class GeographicReminderCreateTool(IPlaceService places) : PlaceTool(places)
{
    public override string Name => "geographic_reminder_create";
    public override string Description => "Cria lembrete interno de entrada/saída após pedido explícito. reference é nome/apelido do lugar no pedido (ex.casa ou Faculdade): backend resolve deterministicamente usando celular escolhido e fixa PlaceIds; modelo não escolhe IDs/coordenadas. allCandidates=true somente pedido explícito 'qualquer uma das minhas casas'. Se resolução continuar incerta, retorne candidatos e esclareça antes de criar. Texto contém tarefa; 'sair de casa, passar na padaria' monitora só casa. repeat=false para única vez; true somente 'toda vez'. startsAt/endsAt RFC3339 com offset para 'chegar amanhã'. Regra criada estando dentro/fora espera condição inicial oposta e transição real; não dispara imediatamente. Resultado pending_sync confirma a criação, sem confirmar instalação no celular. Confirme brevemente a criação e o nome do lugar resolvido; o sistema acompanha a ativação. Não repita advertências genéricas nem peça ao usuário que confira cada sincronização. Comunique um problema concreto quando diagnosticado, sem prometer instalação ou prazo não confirmados.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":600},"reference":{"type":"string","minLength":1,"maxLength":100},"allCandidates":{"type":"boolean"},"transition":{"type":"string","enum":["enter","exit"]},"repeat":{"type":"boolean"},"startsAt":{"type":["string","null"]},"endsAt":{"type":["string","null"]},"timeZoneId":{"type":"string"}},"required":["text","reference","transition"],"additionalProperties":false}""");
    private sealed record Request(string Text, string Reference, string Transition, bool AllCandidates = false, bool Repeat = false, DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null, string TimeZoneId = "America/Sao_Paulo");
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var request = arguments.Deserialize<Request>(Json)!;
        var resolution = await Places.ResolveAsync(request.Reference, request.AllCandidates, ct);
        if (resolution.Status is not ("resolved" or "resolved_set")) return new(false, JsonSerializer.Serialize(new { error = "place_clarification_required", status = resolution.Status, reason = resolution.Reason, candidates = resolution.Candidates.Select(Summary), created = false }, Json), "place_clarification_required");
        var placeIds = resolution.PlaceId is { } selected ? new[] { selected } : resolution.Candidates.Select(p => p.Id).ToArray();
        var source = (await Places.SettingsAsync(ct)).SourceNodeId ?? throw new NodeException("location_source_required", "Escolha o celular pessoal em Dispositivos antes de criar um lembrete por lugar.");
        var rule = await Places.SaveRuleAsync(null, new(request.Text, placeIds, source, request.Transition, request.Repeat, request.StartsAt, request.EndsAt, request.TimeZoneId), context.ConversationId, ct);
        await Places.RememberAsync(context.ConversationId, "geographic_reminder", [rule.Id], ct);
        return Ok(new { rule, places = resolution.Candidates.Where(p => placeIds.Contains(p.Id)).Select(Summary), created = true });
    }
}
public sealed class GeographicReminderListTool(IPlaceService places) : PlaceTool(places)
{
    public override string Name => "geographic_reminder_list";
    public override string Description => "Lista lembretes internos por lugar e estado de instalação no celular; distingue salvo/pending_sync, installed/active, waiting_initial, paused, completed e recoverable_error. Não trate salvo como ativo. Retorna IDs/revisões observados para alterar/pausar/cancelar, sem expor coordenadas.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var rules = await Places.RulesAsync(ct); await Places.RememberAsync(context.ConversationId, "geographic_reminder", rules.Select(r => r.Id), ct); return Ok(new { rules });
    }
}
public sealed class GeographicReminderChangeTool(IPlaceService places) : PlaceTool(places)
{
    public override string Name => "geographic_reminder_change";
    public override string Description => "Edita texto/validade de lembrete por lugar ou pausa/retoma/cancela regra observada. Use ruleId/revisão retornados por geographic_reminder_list/create. Cancelamento offline fica pendente até sincronização ou expiração da definição local (máximo7dias); nunca prometa revogação instantânea. operation update preserva campos omitidos; não muda lugar/observador silenciosamente. Se alvo for ambíguo esclareça.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"ruleId":{"type":"string"},"expectedRevision":{"type":"integer","minimum":1},"operation":{"type":"string","enum":["update","pause","resume","cancel"]},"text":{"type":"string","minLength":1,"maxLength":600},"startsAt":{"type":"string"},"endsAt":{"type":"string"}},"required":["ruleId","expectedRevision","operation"],"additionalProperties":false}""");
    private sealed record Request(Guid RuleId, int ExpectedRevision, string Operation, string? Text = null, DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null);
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var request = arguments.Deserialize<Request>(Json)!; await Places.RequireObservedAsync(context.ConversationId, "geographic_reminder", [request.RuleId], ct);
        if (request.Operation != "update") return Ok(await Places.ActOnRuleAsync(request.RuleId, request.ExpectedRevision, request.Operation, ct));
        var old = (await Places.RulesAsync(ct)).SingleOrDefault(r => r.Id == request.RuleId) ?? throw new NodeException("geofence_not_found", "Lembrete não encontrado.");
        return Ok(await Places.SaveRuleAsync(old.Id, new(request.Text ?? old.Text, old.PlaceIds, old.ObserverNodeId, old.Transition, old.Repeat,
            request.StartsAt ?? old.StartsAt, request.EndsAt ?? old.EndsAt, old.TimeZoneId, request.ExpectedRevision), ct: ct));
    }
}
