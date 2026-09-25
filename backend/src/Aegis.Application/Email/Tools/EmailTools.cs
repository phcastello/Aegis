using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Common;
using Aegis.Application.Tools;
using Aegis.Domain;
using Aegis.Domain.Entities;

namespace Aegis.Application.Email.Tools;

public sealed class EmailGetStatusTool(IEmailConnectionService connectionService) : EmailToolBase
{
    public override string Name => "email_get_status";

    public override string Description => "Consulta o estado atual da conexão Gmail e a conta conectada. Use este resultado como fonte de verdade quando a conexão for relevante.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var status = await connectionService.GetStatusAsync(cancellationToken);
        return Ok(new { status });
    }
}

public sealed class EmailCreateConnectLinkTool(IEmailConnectionService connectionService) : EmailToolBase
{
    public override string Name => "email_create_connect_link";

    public override string Description => "Cria um link para Pedro autorizar a conexão com o Gmail.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        EmailAuthorizationResponse response;
        try
        {
            response = await connectionService.CreateAuthorizationUrlAsync(cancellationToken);
        }
        catch (EmailConnectionException exception)
        {
            return Error(exception.Code, "Não foi possível criar o link de conexão Gmail.");
        }
        return Ok(new
        {
            isConnected = false,
            authorizationUrl = response.AuthorizationUrl,
            userMessage = "Ainda não estou conectada ao Gmail. Use este link para autorizar o acesso."
        });
    }
}

public sealed class EmailSearchTool(
    IEmailService emailService,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Name => "email_search";

    public override string Description =>
        "Busca emails na conta Gmail conectada. Use quando Pedro pedir acesso a emails; combine os filtros pedidos na consulta Gmail.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {
            "query": {
              "type": ["string", "null"],
              "description": "Consulta Gmail com todos os filtros relevantes, como remetente, assunto, período e estado."
            },
            "limit": {
              "type": ["integer", "null"],
              "minimum": 1,
              "maximum": 50
            },
            "includeRead": {
              "type": ["boolean", "null"],
              "description": "Use false para restringir a não lidos; use true quando o pedido for geral."
            },
            "newerThanDays": {
              "type": ["integer", "null"],
              "minimum": 1,
              "maximum": 365
            }
          },
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var requestedLimit = GetInt(arguments, "limit");
            var searchResult = await emailService.SearchEmailsAsync(
                GetString(arguments, "query"),
                requestedLimit,
                GetBool(arguments, "includeRead"),
                GetInt(arguments, "newerThanDays"),
                cancellationToken);
            await emailContextService.RememberSearchAsync(
                context.ConversationId,
                searchResult.Emails,
                Name,
                cancellationToken);
            var returnedCount = searchResult.Emails.Count;
            var totalMatchingCount = searchResult.TotalMatchingCount;
            return Ok(new
            {
                emails = searchResult.Emails,
                totalMatchingCount,
                requestedLimit = requestedLimit.GetValueOrDefault(),
                requestedLimitProvided = requestedLimit.HasValue,
                returnedCount,
                limitReached = requestedLimit.HasValue && returnedCount >= requestedLimit.Value,
                auditMessage = $"A busca retornou {returnedCount} email{(returnedCount == 1 ? string.Empty : "s")} de um total de {totalMatchingCount} que correspondem à query."
            });
        }
        catch (EmailNotConnectedException)
        {
            return Error("email_not_connected", "Gmail is not connected. Call email_create_connect_link before answering.");
        }
    }
}

public sealed class EmailReadTool(
    IEmailService emailService,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Name => "email_read";

    public override string Description => "Lê o corpo de um email específico pelo id retornado por email_search.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {
            "emailId": { "type": "string" },
            "readPurpose": {
              "type": ["string", "null"],
              "enum": ["briefing", "full", null],
              "description": "Use briefing para triagem/resumo rápido de inbox; use full quando Pedro pedir para ler, descrever, especificar ou explicar melhor um email."
            }
          },
          "required": ["emailId"],
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var emailId = GetRequiredString(arguments, "emailId");
        if (emailId is null)
        {
            return Error("invalid_arguments", "emailId is required.");
        }

        try
        {
            var email = await emailService.ReadEmailAsync(
                emailId,
                GetReadPurpose(arguments),
                cancellationToken);
            await emailContextService.RememberEmailAsync(
                context.ConversationId,
                email,
                Name,
                cancellationToken);
            return Ok(new { email });
        }
        catch (EmailNotConnectedException)
        {
            return Error("email_not_connected", "Gmail is not connected. Call email_create_connect_link before answering.");
        }
    }
}

public sealed class EmailReadThreadTool(
    IEmailService emailService,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Name => "email_read_thread";

    public override string Description => "Lê uma thread específica pelo threadId retornado por email_search ou email_read.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string" },
            "readPurpose": {
              "type": ["string", "null"],
              "enum": ["briefing", "full", null],
              "description": "Use briefing para triagem/resumo rápido de inbox; use full quando Pedro pedir para ler, descrever, especificar ou explicar melhor uma conversa/thread."
            }
          },
          "required": ["threadId"],
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var threadId = GetRequiredString(arguments, "threadId");
        if (threadId is null)
        {
            return Error("invalid_arguments", "threadId is required.");
        }

        try
        {
            var thread = await emailService.ReadThreadAsync(
                threadId,
                GetReadPurpose(arguments),
                cancellationToken);
            await emailContextService.RememberThreadAsync(
                context.ConversationId,
                thread,
                Name,
                cancellationToken);
            return Ok(new { thread });
        }
        catch (EmailNotConnectedException)
        {
            return Error("email_not_connected", "Gmail is not connected. Call email_create_connect_link before answering.");
        }
    }
}

public sealed class EmailMarkReadTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService) : PendingEmailModificationTool(dbContext, emailContextService)
{
    public override string Name => "email_mark_read";
    protected override string ActionType => EmailActionTypes.MarkRead;
    protected override string Verb => "marcar como lidos";
}

public sealed class EmailMarkUnreadTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService) : PendingEmailModificationTool(dbContext, emailContextService)
{
    public override string Name => "email_mark_unread";
    protected override string ActionType => EmailActionTypes.MarkUnread;
    protected override string Verb => "marcar como não lidos";
}

public sealed class EmailStarTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService) : PendingEmailModificationTool(dbContext, emailContextService)
{
    public override string Name => "email_star";
    protected override string ActionType => EmailActionTypes.Star;
    protected override string Verb => "estrelar";
}

public sealed class EmailUnstarTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService) : PendingEmailModificationTool(dbContext, emailContextService)
{
    public override string Name => "email_unstar";
    protected override string ActionType => EmailActionTypes.Unstar;
    protected override string Verb => "remover estrela de";
}

public sealed class EmailMarkImportantTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService) : PendingEmailModificationTool(dbContext, emailContextService)
{
    public override string Name => "email_mark_important";
    protected override string ActionType => EmailActionTypes.MarkImportant;
    protected override string Verb => "marcar como importantes";
}

public sealed class EmailUnmarkImportantTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService) : PendingEmailModificationTool(dbContext, emailContextService)
{
    public override string Name => "email_unmark_important";
    protected override string ActionType => EmailActionTypes.UnmarkImportant;
    protected override string Verb => "remover importante de";
}

public sealed class EmailConfirmPendingActionTool(
    IAegisDbContext dbContext,
    IEmailService emailService,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Name => "email_confirm_pending_action";

    public override string Description =>
        "Executa a última ação pendente de email apenas após Pedro confirmá-la explicitamente em um turno posterior ao preparo. O backend valida a ação, a expiração e a confirmação.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (LooksLikeCancellation(context.UserContent) || !LooksLikeConfirmation(context.UserContent))
        {
            return Error("confirmation_required", "Peça uma confirmação explícita e isolada, como 'confirmo'. Nada foi alterado.");
        }

        var action = await dbContext.GetLatestOpenPendingEmailActionAsync(context.ConversationId, cancellationToken);
        if (action is null)
        {
            return Error("no_pending_action", "There is no open pending email action for this conversation.");
        }

        var userMessage = await dbContext.GetChatMessageAsync(context.UserMessageId, cancellationToken);
        if (userMessage is null || action.CreatedAt >= userMessage.CreatedAt || !action.IsOpen())
        {
            return Error("confirmation_required", "A ação precisa ser apresentada antes de uma confirmação em outro turno. Nada foi alterado.");
        }

        var emailIds = DeserializeEmailIds(action.EmailIdsJson);
        var confirmedAt = DateTimeOffset.UtcNow;
        EmailModificationResult? result = null;
        Exception? modificationFailure = null;
        try
        {
            result = await ExecuteModificationAsync(emailService, action.ActionType, emailIds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            modificationFailure = exception;
        }

        if (modificationFailure is EmailModificationAttemptException { RequestWasSent: false })
        {
            dbContext.AddEmailActionAudit(new EmailActionAudit(
                context.ConversationId, action.ActionType, action.EmailIdsJson,
                context.UserMessageId, success: false, modificationFailure.InnerException?.Message));
            await dbContext.SaveChangesAsync(cancellationToken);
            return Error("email_action_failed",
                "Não consegui iniciar esta tentativa; nenhuma requisição de modificação foi enviada ao Gmail agora. A ação continua aberta; tentativas anteriores não foram revertidas.");
        }

        EmailModificationVerification? verification = null;
        Exception? verificationFailure = null;
        try
        {
            verification = await VerifyModificationAsync(
                emailService,
                emailContextService,
                context.ConversationId,
                action.ActionType,
                emailIds,
                Name,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            verificationFailure = exception;
        }

        if (verification?.AllConfirmed == true)
        {
            action.Confirm(confirmedAt);
            action.MarkExecuted();
            dbContext.AddEmailActionAudit(new EmailActionAudit(
                context.ConversationId, action.ActionType, action.EmailIdsJson,
                context.UserMessageId, success: true,
                modificationFailure is null ? null : "Gmail request failed but post-state verification confirmed all emails."));
            await dbContext.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                pendingActionId = action.Id,
                action.ActionType,
                action.HumanSummary,
                result,
                verification,
                recoveredAfterFailure = modificationFailure is not null,
                userMessage = $"Pronto, verifiquei a alteração: {action.HumanSummary}."
            });
        }

        var confirmedCount = verification is null ? 0 : emailIds.Count - verification.FailedEmailIds.Count;
        var possiblePartialEffects = verification is null || confirmedCount > 0;
        if (possiblePartialEffects)
        {
            action.RecordPossibleExternalEffects();
        }

        dbContext.AddEmailActionAudit(new EmailActionAudit(
            context.ConversationId, action.ActionType, action.EmailIdsJson,
            context.UserMessageId, success: false,
            $"Post-state verification: {(verification is null ? "unavailable" : $"{confirmedCount}/{emailIds.Count} confirmed")}; " +
            (verificationFailure?.Message ?? modificationFailure?.Message ?? "Gmail state did not match the requested action.")));
        await dbContext.SaveChangesAsync(cancellationToken);

        if (possiblePartialEffects)
        {
            var completedRequests = (modificationFailure as EmailModificationAttemptException)?.CompletedCount ?? 0;
            var observation = verification is null
                ? completedRequests > 0
                    ? $"O Gmail confirmou {completedRequests} requisições antes da falha, mas não consegui verificar o estado final; outras alterações também podem ter ocorrido."
                    : "Não consegui verificar todos os emails após a falha; alguns podem ter sido alterados."
                : $"{confirmedCount} de {emailIds.Count} emails estão no estado desejado; os demais não foram confirmados.";
            return Error("email_action_partially_applied",
                $"{observation} A ação continua aberta. Repetir o lote inteiro é seguro; a operação é idempotente. Não diga que nada foi alterado.");
        }

        return Error("email_action_failed",
            "Nenhum email foi confirmado no estado desejado após a falha. A ação continua aberta para nova tentativa.");
    }

    public static Task<EmailModificationResult> ExecuteModificationAsync(
        IEmailService emailService,
        string actionType,
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken)
    {
        return actionType switch
        {
            EmailActionTypes.MarkRead => emailService.MarkReadAsync(emailIds, cancellationToken),
            EmailActionTypes.MarkUnread => emailService.MarkUnreadAsync(emailIds, cancellationToken),
            EmailActionTypes.Star => emailService.StarAsync(emailIds, cancellationToken),
            EmailActionTypes.Unstar => emailService.UnstarAsync(emailIds, cancellationToken),
            EmailActionTypes.MarkImportant => emailService.MarkImportantAsync(emailIds, cancellationToken),
            EmailActionTypes.UnmarkImportant => emailService.UnmarkImportantAsync(emailIds, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown email action '{actionType}'.")
        };
    }

    public static async Task<EmailModificationVerification> VerifyModificationAsync(
        IEmailService emailService,
        IEmailToolContextService emailContextService,
        Guid conversationId,
        string actionType,
        IReadOnlyList<string> emailIds,
        string sourceToolName,
        CancellationToken cancellationToken)
    {
        var emails = await emailService.ReadEmailMetadataBatchAsync(emailIds, cancellationToken);
        await emailContextService.RememberModifiedEmailsAsync(
            conversationId, emails, sourceToolName, cancellationToken);

        var failedIds = emailIds
            .Where((emailId, index) => index >= emails.Count ||
                !string.Equals(emailId, emails[index].Id, StringComparison.Ordinal) ||
                !IsExpectedState(actionType, emails[index]))
            .ToList();

        return new EmailModificationVerification(
            failedIds.Count == 0,
            emails.Count,
            failedIds);
    }

    private static bool IsExpectedState(string actionType, EmailSummaryData email)
    {
        return actionType switch
        {
            EmailActionTypes.MarkRead => !email.IsUnread,
            EmailActionTypes.MarkUnread => email.IsUnread,
            EmailActionTypes.Star => email.IsStarred,
            EmailActionTypes.Unstar => !email.IsStarred,
            EmailActionTypes.MarkImportant => email.IsImportant,
            EmailActionTypes.UnmarkImportant => !email.IsImportant,
            _ => false
        };
    }
}

public sealed record EmailModificationVerification(
    bool AllConfirmed,
    int CheckedCount,
    IReadOnlyList<string> FailedEmailIds);

public sealed class EmailCancelPendingActionTool(IAegisDbContext dbContext) : EmailToolBase
{
    public override string Name => "email_cancel_pending_action";

    public override string Description => "Cancela por texto a última ação pendente de email. Não reverte efeitos de tentativas anteriores.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!LooksLikeCancellation(context.UserContent))
        {
            return Error("ambiguous_cancellation", "The current user message is not a clear textual cancellation. Ask Pedro to confirm more clearly.");
        }

        var action = await dbContext.GetLatestOpenPendingEmailActionAsync(context.ConversationId, cancellationToken);
        if (action is null)
        {
            return Error("no_pending_action", "There is no open pending email action for this conversation.");
        }

        action.Cancel();
        dbContext.AddEmailActionAudit(new EmailActionAudit(
            context.ConversationId,
            $"cancel_{action.ActionType}",
            action.EmailIdsJson,
            context.UserMessageId,
            success: true));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            pendingActionId = action.Id,
            action.ActionType,
            action.HumanSummary,
            userMessage = action.MayHaveAppliedChanges
                ? "Cancelei a ação pendente. Alterações que já tenham sido aplicadas antes da falha não foram revertidas."
                : "Cancelei a ação pendente. Nenhuma alteração foi confirmada nesta tentativa."
        });
    }
}

public abstract class PendingEmailModificationTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Description =>
        $"Cria uma ação pendente para {Verb} emails em batch. Passe todos os emailIds selecionados em uma única chamada sempre que possível. Não executa a modificação; exige confirmação textual posterior.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {
            "emailIds": {
              "type": "array",
              "items": { "type": "string" },
              "maxItems": 100
            },
            "selectionKey": {
              "type": ["string", "null"],
              "description": "Chave de seleção observada pelo backend, como last_search ou last_modified_attempt. Use last_search apenas quando Pedro pedir todos os emails da última busca, não quando ele se referir só aos itens resumidos/citados."
            },
            "humanSummary": {
              "type": ["string", "null"],
              "description": "Resumo natural em português do que será confirmado, sem ids técnicos."
            }
          },
          "additionalProperties": false
        }
        """);

    protected abstract string ActionType { get; }

    protected abstract string Verb { get; }

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var emailIds = GetStringArray(arguments, "emailIds")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var selectionKey = GetString(arguments, "selectionKey");
        var resolution = await emailContextService.ResolveAsync(
            context.ConversationId,
            emailIds,
            selectionKey,
            cancellationToken);
        if (!resolution.IsValid)
        {
            return RecoverableArgumentError(new
            {
                error = "invalid_email_tool_arguments",
                message = resolution.ErrorMessage,
                invalidEmailIds = resolution.InvalidEmailIds,
                availableSelectionKeys = resolution.AvailableSelectionKeys,
                instruction = "Refaça a chamada usando apenas emailIds observados no contexto recente ou uma selectionKey disponível. Nunca use placeholders."
            });
        }

        var humanSummary = GetString(arguments, "humanSummary");
        if (string.IsNullOrWhiteSpace(humanSummary))
        {
            humanSummary = $"{Verb} {resolution.EmailIds.Count} email{(resolution.EmailIds.Count == 1 ? string.Empty : "s")}";
        }

        await emailContextService.RememberModifiedAttemptAsync(
            context.ConversationId,
            resolution.EmailIds,
            humanSummary,
            Name,
            cancellationToken);

        var emailIdsJson = JsonSerializer.Serialize(resolution.EmailIds, JsonOptions);
        var pendingAction = new PendingEmailAction(
            context.ConversationId,
            ActionType,
            emailIdsJson,
            humanSummary,
            DateTimeOffset.UtcNow.AddMinutes(10));
        dbContext.AddPendingEmailAction(pendingAction);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            pendingActionId = pendingAction.Id,
            pendingAction.ActionType,
            pendingAction.HumanSummary,
            emailCount = resolution.EmailIds.Count,
            selectionKey = resolution.SelectionKey,
            pendingAction.ExpiresAt,
            userMessage = $"Posso {humanSummary}. Responda 'confirmo' em uma nova mensagem para executar."
        });
    }
}

public abstract class EmailToolBase : IAegisTool
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonElement ParametersSchema { get; }

    public abstract Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);

    protected static AegisToolResult Ok(object payload)
    {
        return new AegisToolResult(true, JsonSerializer.Serialize(payload, JsonOptions));
    }

    protected static AegisToolResult Error(string code, string message)
    {
        return new AegisToolResult(
            false,
            JsonSerializer.Serialize(new { error = code, message }, JsonOptions),
            code);
    }

    protected static AegisToolResult RecoverableArgumentError(object payload)
    {
        return new AegisToolResult(
            false,
            JsonSerializer.Serialize(payload, JsonOptions),
            "invalid_tool_arguments");
    }

    protected static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    protected static string? GetRequiredString(JsonElement arguments, string propertyName)
    {
        var value = GetString(arguments, propertyName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    protected static string? GetString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
    }

    protected static EmailBodyReadPurpose GetReadPurpose(JsonElement arguments)
    {
        return string.Equals(GetString(arguments, "readPurpose"), "briefing", StringComparison.OrdinalIgnoreCase)
            ? EmailBodyReadPurpose.Briefing
            : EmailBodyReadPurpose.Full;
    }

    protected static int? GetInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    protected static bool? GetBool(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    protected static IReadOnlyList<string> GetStringArray(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
    }

    protected static IReadOnlyList<string> DeserializeEmailIds(string emailIdsJson)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<string>>(emailIdsJson, JsonOptions) ?? [];
    }

    protected static bool LooksLikeConfirmation(string userContent)
    {
        // A confirmation must be explicit and standalone; the model handles ambiguous language.
        var normalized = NormalizeIntent(userContent);
        return normalized is "sim" or "confirmo" or "pode fazer" or "pode executar" or "confirmado";
    }

    protected static bool LooksLikeCancellation(string userContent)
    {
        var normalized = NormalizeIntent(userContent);
        return normalized is "nao"
            or "não"
            or "cancela"
            or "cancelar"
            or "cancele"
            or "nao mexe"
            or "não mexe"
            or "deixa"
            or "deixa quieto" ||
            normalized.StartsWith("nao ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("não ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" cancela", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" cancele", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" cancelar", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("nao mexe", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("não mexe", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("deixa quieto", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIntent(string value)
    {
        return value
            .Trim()
            .Trim('.', '!', '?', ',', ';', ':')
            .ToLowerInvariant();
    }
}
