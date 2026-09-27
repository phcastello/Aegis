using System.Net;
using System.Text;
using System.Text.Json;
using Aegis.Application.Email;
using Aegis.Application.Email.Tools;
using Aegis.Application.Tools;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Email;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class EmailModificationHardeningTests
{
    [Fact]
    public async Task CancellationBeforeFirstPostDoesNotRecordPossibleEffects()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        using var cancellation = new CancellationTokenSource();
        var service = new BatchEmailService { CancelSource = cancellation, CancelAfterMessages = 0 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfirmAsync(db, service, conversation, cancellation.Token));

        Assert.False(action.MayHaveAppliedChanges);
        Assert.True(action.IsOpen());
        Assert.Null(action.ConfirmedAt);
        Assert.Null(action.ExecutedAt);
        Assert.Empty(service.ReadIds);
    }

    [Fact]
    public async Task CancellationAfterTwoPostsPersistsPossibleEffectsAndPropagates()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        using var cancellation = new CancellationTokenSource();
        var service = new BatchEmailService { CancelSource = cancellation, CancelAfterMessages = 2 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfirmAsync(db, service, conversation, cancellation.Token));

        Assert.Equal(2, service.ReadIds.Count);
        Assert.True(action.IsOpen());
        Assert.Null(action.ConfirmedAt);
        Assert.Null(action.ExecutedAt);
        db.ChangeTracker.Clear();
        Assert.True(db.PendingEmailActions.Single().MayHaveAppliedChanges);
    }

    [Fact]
    public async Task CancellationDuringMetadataVerificationPersistsPossibleEffects()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        using var cancellation = new CancellationTokenSource();
        var service = new BatchEmailService { CancelSource = cancellation, CancelDuringMetadata = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfirmAsync(db, service, conversation, cancellation.Token));

        Assert.Equal(3, service.ReadIds.Count);
        Assert.True(action.IsOpen());
        Assert.Null(action.ConfirmedAt);
        Assert.Null(action.ExecutedAt);
        db.ChangeTracker.Clear();
        Assert.True(db.PendingEmailActions.Single().MayHaveAppliedChanges);
    }

    [Fact]
    public async Task CancelActionAfterInterruptedBatchAcknowledgesPossibleChanges()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        using var cancellation = new CancellationTokenSource();
        var service = new BatchEmailService { CancelSource = cancellation, CancelAfterMessages = 2 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConfirmAsync(db, service, conversation, cancellation.Token));
        var cancelContext = await CreateContextAsync(db, conversation, "cancela");

        var cancelled = await new EmailCancelPendingActionTool(db)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), cancelContext);

        Assert.True(cancelled.Success);
        using var payload = JsonDocument.Parse(cancelled.Content);
        var userMessage = payload.RootElement.GetProperty("userMessage").GetString();
        Assert.Contains("não foram revertidas", userMessage);
        Assert.DoesNotContain("Não mexi em nada", userMessage);
        Assert.NotNull(action.CancelledAt);
    }

    [Fact]
    public async Task FailureBeforeFirstEmailHasNoObservedEffectAndRemainsRetryable()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        var service = new BatchEmailService { FailAfterMessages = 0 };
        var result = await ConfirmAsync(db, service, conversation);

        Assert.False(result.Success);
        Assert.Equal("email_action_failed", result.ErrorCode);
        Assert.True(action.IsOpen());
        Assert.False(action.MayHaveAppliedChanges);
        using (var payload = JsonDocument.Parse(result.Content))
        {
            Assert.Contains("nenhuma requisição de modificação",
                payload.RootElement.GetProperty("message").GetString());
        }
        Assert.Null(action.ConfirmedAt);
        Assert.Null(action.ExecutedAt);
        Assert.NotNull(await db.GetLatestOpenPendingEmailActionAsync(conversation.Id));
        Assert.Single(db.EmailActionAudits);
    }

    [Fact]
    public async Task FailureAfterTwoEmailsReportsPartialEffectAndKeepsActionOpen()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        var service = new BatchEmailService { FailAfterMessages = 2 };

        var result = await ConfirmAsync(db, service, conversation);

        Assert.False(result.Success);
        Assert.Equal("email_action_partially_applied", result.ErrorCode);
        Assert.Contains("2 de 3", result.Content);
        Assert.Contains("idempotente", result.Content);
        Assert.True(action.IsOpen());
        Assert.True(action.MayHaveAppliedChanges);
        Assert.Null(action.ConfirmedAt);
        Assert.Null(action.ExecutedAt);
        Assert.Equal(2, service.ReadIds.Count);
        db.ChangeTracker.Clear();
        Assert.True(db.PendingEmailActions.Single().MayHaveAppliedChanges);
    }

    [Fact]
    public async Task RetryAfterPartialApplicationCompletesTheWholeIdempotentBatch()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        var service = new BatchEmailService { FailAfterMessages = 2 };
        Assert.Equal("email_action_partially_applied", (await ConfirmAsync(db, service, conversation)).ErrorCode);

        service.FailAfterMessages = null;
        var retry = await ConfirmAsync(db, service, conversation);

        Assert.True(retry.Success);
        Assert.Equal(2, service.ModificationCalls);
        Assert.Equal(3, service.ReadIds.Count);
        Assert.NotNull(action.ConfirmedAt);
        Assert.NotNull(action.ExecutedAt);
        Assert.False(action.IsOpen());
    }

    [Fact]
    public async Task CancellationAfterPartialApplicationDoesNotClaimNothingChanged()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        var service = new BatchEmailService { FailAfterMessages = 2 };
        await ConfirmAsync(db, service, conversation);
        var cancelContext = await CreateContextAsync(db, conversation, "cancela");

        var cancelled = await new EmailCancelPendingActionTool(db)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), cancelContext);

        Assert.True(cancelled.Success);
        using var payload = JsonDocument.Parse(cancelled.Content);
        var userMessage = payload.RootElement.GetProperty("userMessage").GetString();
        Assert.Contains("não foram revertidas", userMessage);
        Assert.DoesNotContain("Não mexi em nada", userMessage);
        Assert.NotNull(action.CancelledAt);
        Assert.True(action.MayHaveAppliedChanges);
    }

    [Fact]
    public async Task TransportFailureAfterAllEmailsWereChangedCompletesAfterVerification()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        var service = new BatchEmailService { FailAfterAll = true };

        var result = await ConfirmAsync(db, service, conversation);

        Assert.True(result.Success);
        Assert.Contains("recoveredAfterFailure", result.Content);
        Assert.NotNull(action.ConfirmedAt);
        Assert.NotNull(action.ExecutedAt);
        Assert.Equal(3, service.ReadIds.Count);
    }

    [Fact]
    public async Task UnavailableMetadataKeepsPossibleEffectsVisibleForCancellation()
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 3);
        var service = new BatchEmailService { FailAfterMessages = 1, FailMetadata = true };

        var result = await ConfirmAsync(db, service, conversation);

        Assert.Equal("email_action_partially_applied", result.ErrorCode);
        Assert.Contains("confirmou 1", result.Content);
        Assert.True(action.MayHaveAppliedChanges);
        Assert.True(action.IsOpen());
    }

    [Fact]
    public async Task MetadataBatchUsesOnlyMetadataFormatAndReturnsLabelState()
    {
        using var db = CreateDb();
        var protector = DataProtectionProvider.Create("Aegis.Metadata.Tests");
        var tokenProtector = new EmailTokenProtector(protector);
        db.EmailAccountConnections.Add(new EmailAccountConnection("gmail", "eval@example.test",
            tokenProtector.Protect("access"), tokenProtector.Protect("refresh"),
            DateTimeOffset.UtcNow.AddHours(1), GmailOptions.DefaultScope));
        await db.SaveChangesAsync();
        var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var service = new GmailService(http, Options.Create(new GmailOptions()), new GoogleAccessTokenProvider(db, http, Options.Create(new GmailOptions()), tokenProtector));

        var ids = Enumerable.Range(1, 8).Select(index => $"mail-{index}").ToArray();
        var emails = await service.ReadEmailMetadataBatchAsync(ids);

        Assert.Equal(8, emails.Count);
        Assert.All(emails, email => Assert.True(email.IsStarred));
        Assert.Equal(8, handler.Requests.Count);
        Assert.All(handler.Requests, url => Assert.Contains("format=metadata", url));
        Assert.All(handler.Requests, url => Assert.DoesNotContain("format=full", url));
        Assert.InRange(handler.MaxObservedConcurrency, 2, 4);
    }

    [Fact]
    public async Task GmailServiceReportsConfirmedRequestsWhenThirdModifyFails()
    {
        using var db = CreateDb();
        var protector = DataProtectionProvider.Create("Aegis.PartialBatch.Tests");
        var tokenProtector = new EmailTokenProtector(protector);
        db.EmailAccountConnections.Add(new EmailAccountConnection("gmail", "eval@example.test",
            tokenProtector.Protect("access"), tokenProtector.Protect("refresh"),
            DateTimeOffset.UtcNow.AddHours(1), GmailOptions.DefaultScope));
        await db.SaveChangesAsync();
        var handler = new PartialModificationHandler();
        using var http = new HttpClient(handler);
        var service = new GmailService(http, Options.Create(new GmailOptions()), new GoogleAccessTokenProvider(db, http, Options.Create(new GmailOptions()), tokenProtector));

        var failure = await Assert.ThrowsAsync<EmailModificationAttemptException>(() =>
            service.MarkReadAsync(["mail-1", "mail-2", "mail-3"]));
        var state = await service.ReadEmailMetadataBatchAsync(["mail-1", "mail-2", "mail-3"]);

        Assert.True(failure.RequestWasSent);
        Assert.Equal(2, failure.CompletedCount);
        Assert.Equal(new[] { false, false, true }, state.Select(email => email.IsUnread));
    }

    [Fact]
    public async Task GmailServiceCancellationBeforeFirstPostKeepsNoSendEvidence()
    {
        using var db = CreateDb();
        var protector = DataProtectionProvider.Create("Aegis.CancelBeforePost.Tests");
        var tokenProtector = new EmailTokenProtector(protector);
        db.EmailAccountConnections.Add(new EmailAccountConnection("gmail", "eval@example.test",
            tokenProtector.Protect("access"), tokenProtector.Protect("refresh"),
            DateTimeOffset.UtcNow.AddHours(1), GmailOptions.DefaultScope));
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new CancellingModificationHandler(cancellation);
        using var http = new HttpClient(handler);
        var service = new GmailService(http, Options.Create(new GmailOptions()), new GoogleAccessTokenProvider(db, http, Options.Create(new GmailOptions()), tokenProtector));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.MarkReadAsync(["mail-1"], cancellation.Token));

        Assert.Equal(0, handler.PostCount);
    }

    [Fact]
    public async Task GmailServiceCancellationDuringThirdPostRetainsSendEvidence()
    {
        using var db = CreateDb();
        var protector = DataProtectionProvider.Create("Aegis.CancelAfterPosts.Tests");
        var tokenProtector = new EmailTokenProtector(protector);
        db.EmailAccountConnections.Add(new EmailAccountConnection("gmail", "eval@example.test",
            tokenProtector.Protect("access"), tokenProtector.Protect("refresh"),
            DateTimeOffset.UtcNow.AddHours(1), GmailOptions.DefaultScope));
        await db.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = new CancellingModificationHandler(cancellation);
        using var http = new HttpClient(handler);
        var service = new GmailService(http, Options.Create(new GmailOptions()), new GoogleAccessTokenProvider(db, http, Options.Create(new GmailOptions()), tokenProtector));

        var failure = await Assert.ThrowsAsync<EmailModificationCancelledException>(() =>
            service.MarkReadAsync(["mail-1", "mail-2", "mail-3"], cancellation.Token));

        Assert.True(failure.RequestWasSent);
        Assert.Equal(2, failure.CompletedCount);
        Assert.Equal(3, handler.PostCount);
    }

    [Theory]
    [InlineData("Manda bala")]
    [InlineData("Pode")]
    [InlineData("É isso aí")]
    [InlineData("texto sem palavras reservadas")]
    public async Task ConfirmationUsesSelectedToolAndPersistedStateWithoutMatchingUserText(string content)
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 1);
        var context = await CreateContextAsync(db, conversation, content);
        var service = new BatchEmailService();
        var tool = new EmailConfirmPendingActionTool(db, service, new EmailToolContextService(db));
        Assert.True((await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context with { UserContent = "outro texto" })).Success);
        Assert.Equal(context.UserMessageId, db.EmailActionAudits.Single().UserConfirmationMessageId);
        Assert.NotNull(action.ExecutedAt);
        Assert.Equal("no_pending_action", (await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), await CreateContextAsync(db, conversation, content))).ErrorCode);
        Assert.Equal(1, service.ModificationCalls);
    }

    [Theory]
    [InlineData("Nah, deixa quieto")]
    [InlineData("Esquece")]
    [InlineData("texto interpretado pelo modelo")]
    public async Task CancellationDoesNotRequireReservedWords(string content)
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 1);
        var context = await CreateContextAsync(db, conversation, content);
        var tool = new EmailCancelPendingActionTool(db);
        Assert.True((await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context)).Success);
        Assert.NotNull(action.CancelledAt);
        Assert.Equal("no_pending_action", (await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context)).ErrorCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other_conversation")]
    [InlineData("assistant")]
    public async Task ConfirmAndCancelRejectInvalidCurrentMessageIdentity(string invalidState)
    {
        using var db = CreateDb();
        var (conversation, action) = await CreateActionAsync(db, 1);
        var message = new ChatMessage(invalidState == "other_conversation" ? Guid.NewGuid() : conversation.Id,
            invalidState == "assistant" ? ChatRoles.Assistant : ChatRoles.User, "Pode");
        if (invalidState != "missing") db.AddChatMessage(message);
        await db.SaveChangesAsync();
        var context = new ToolExecutionContext(conversation.Id, message.Id, "Pode");
        var service = new BatchEmailService();
        Assert.Equal("confirmation_required", (await new EmailConfirmPendingActionTool(db, service, new EmailToolContextService(db))
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context)).ErrorCode);
        Assert.False((await new EmailCancelPendingActionTool(db).ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context)).Success);
        Assert.True(action.IsOpen());
        Assert.Equal(0, service.ModificationCalls);
    }

    [Fact]
    public async Task SameTurnConfirmationIsBlockedEvenWhenModelSelectsConfirm()
    {
        using var db = CreateDb();
        var conversation = new Conversation();
        db.AddConversation(conversation);
        var context = await CreateContextAsync(db, conversation, "Pode marcar como lido");
        db.AddPendingEmailAction(new(conversation.Id, EmailActionTypes.MarkRead, "[\"mail-1\"]", "Marcar como lido", DateTimeOffset.UtcNow.AddMinutes(10)));
        await db.SaveChangesAsync();
        var service = new BatchEmailService();
        Assert.Equal("confirmation_required", (await new EmailConfirmPendingActionTool(db, service, new EmailToolContextService(db))
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context)).ErrorCode);
        Assert.Equal(0, service.ModificationCalls);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("cancelled")]
    [InlineData("executed")]
    public async Task ClosedOrExpiredActionsCannotBeConfirmed(string state)
    {
        using var db = CreateDb();
        var (conversation, original) = await CreateActionAsync(db, 1);
        if (state == "expired")
        {
            original.Cancel();
            db.AddPendingEmailAction(new(conversation.Id, original.ActionType, original.EmailIdsJson, original.HumanSummary, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        else if (state == "cancelled") original.Cancel();
        else { original.Confirm(); original.MarkExecuted(); }
        await db.SaveChangesAsync();
        var service = new BatchEmailService();
        Assert.Equal("no_pending_action", (await ConfirmAsync(db, service, conversation)).ErrorCode);
        Assert.Equal(0, service.ModificationCalls);
    }

    [Fact]
    public async Task EmailPreparationSupersedesAllOldProposalsAndPreservesHistory()
    {
        using var db = CreateDb();
        var (conversation, first) = await CreateActionAsync(db, 1);
        var legacy = new PendingEmailAction(conversation.Id, first.ActionType, first.EmailIdsJson, first.HumanSummary, DateTimeOffset.UtcNow.AddMinutes(10));
        db.AddPendingEmailAction(legacy);
        await db.SaveChangesAsync();
        var emailContext = new EmailToolContextService(db);
        await emailContext.RememberModifiedEmailsAsync(conversation.Id, [Email("mail-1"), Email("mail-2")], "email_search");
        var result = await new EmailMarkReadTool(db, emailContext).ExecuteAsync(JsonSerializer.SerializeToElement(new { emailIds = new[] { "mail-2" } }),
            await CreateContextAsync(db, conversation, "Faz no segundo em vez disso"));
        Assert.True(result.Success);
        var latest = (await db.GetLatestOpenPendingEmailActionAsync(conversation.Id))!;
        Assert.Equal(latest.Id, first.SupersededById);
        Assert.Equal(latest.Id, legacy.SupersededById);
        Assert.Null(first.CancelledAt);
        Assert.Equal("[\"mail-1\"]", first.EmailIdsJson);
        Assert.Single(await db.GetUnresolvedPendingEmailActionsAsync(conversation.Id));
        var service = new BatchEmailService();
        Assert.True((await ConfirmAsync(db, service, conversation)).Success);
        Assert.Equal("mail-2", Assert.Single(service.ReadIds));
        Assert.Empty(await db.GetUnresolvedPendingEmailActionsAsync(conversation.Id));
        Assert.Equal(3, db.PendingEmailActions.Count());
        Assert.Equal(2, db.EmailActionAudits.Count(a => a.ActionType == "supersede_mark_read"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmailPreparationCannotHidePossibleExternalEffectsEvenAfterExpiration(bool expired)
    {
        using var db = CreateDb();
        var (conversation, original) = await CreateActionAsync(db, 1);
        original.Cancel();
        var action = new PendingEmailAction(conversation.Id, original.ActionType, original.EmailIdsJson, original.HumanSummary,
            DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 10));
        action.RecordPossibleExternalEffects();
        db.AddPendingEmailAction(action);
        await db.SaveChangesAsync();
        var emailContext = new EmailToolContextService(db);
        await emailContext.RememberModifiedEmailsAsync(conversation.Id, [Email("mail-2")], "email_search");
        var result = await new EmailMarkReadTool(db, emailContext).ExecuteAsync(JsonSerializer.SerializeToElement(new { emailIds = new[] { "mail-2" } }),
            await CreateContextAsync(db, conversation, "Troca o email"));
        Assert.Equal("email_action_outcome_unknown", result.ErrorCode);
        Assert.Null(action.SupersededAt);
        Assert.Null(action.CancelledAt);
        var cancel = await new EmailCancelPendingActionTool(db).ExecuteAsync(JsonSerializer.SerializeToElement(new { }),
            await CreateContextAsync(db, conversation, "Esquece essa tentativa"));
        Assert.True(cancel.Success);
        Assert.Contains("não foram revertidas", JsonDocument.Parse(cancel.Content).RootElement.GetProperty("userMessage").GetString());
        Assert.True(action.MayHaveAppliedChanges);
    }

    [Fact]
    public async Task EmailConfirmationDoesNotRequireCancellingAnUnrelatedCalendarProposal()
    {
        using var db = CreateDb();
        var (conversation, _) = await CreateActionAsync(db, 1);
        var calendar = new PendingCalendarAction(conversation.Id, CalendarActionTypes.Create, "new-id", "{}", "Dentista", DateTimeOffset.UtcNow.AddMinutes(10));
        db.AddPendingCalendarAction(calendar);
        await db.SaveChangesAsync();
        Assert.True((await ConfirmAsync(db, new BatchEmailService(), conversation)).Success);
        Assert.True(calendar.IsOpen());
    }

    [Fact]
    public async Task EmailUncertaintyIsPersistedBeforeSendingTheMutation()
    {
        using var db = CreateDb();
        var (conversation, _) = await CreateActionAsync(db, 1);
        var service = new BatchEmailService { BeforeModification = () => Assert.True(db.PendingEmailActions.AsNoTracking().Single().MayHaveAppliedChanges) };
        Assert.True((await ConfirmAsync(db, service, conversation)).Success);
    }

    private static EmailSummaryData Email(string id) => new(id, "thread", null, null, null, null, null, ["UNREAD"], [], true, false, false);

    private static AegisDbContext CreateDb() => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Conversation, PendingEmailAction)> CreateActionAsync(AegisDbContext db, int count)
    {
        var conversation = new Conversation();
        db.Conversations.Add(conversation);
        var ids = Enumerable.Range(1, count).Select(index => $"mail-{index}").ToArray();
        var action = new PendingEmailAction(conversation.Id, EmailActionTypes.MarkRead,
            JsonSerializer.Serialize(ids), "marcar emails como lidos", DateTimeOffset.UtcNow.AddMinutes(10));
        db.PendingEmailActions.Add(action);
        await db.SaveChangesAsync();
        return (conversation, action);
    }

    private static async Task<ToolExecutionContext> CreateContextAsync(
        AegisDbContext db, Conversation conversation, string content)
    {
        var message = new ChatMessage(conversation.Id, ChatRoles.User, content);
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
        return new ToolExecutionContext(conversation.Id, message.Id, content);
    }

    private static async Task<AegisToolResult> ConfirmAsync(
        AegisDbContext db, IEmailService service, Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateContextAsync(db, conversation, "confirmo");
        return await new EmailConfirmPendingActionTool(db, service, new EmailToolContextService(db))
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context, cancellationToken);
    }

    private sealed class BatchEmailService : IEmailService
    {
        public Action? BeforeModification { get; set; }
        public int? FailAfterMessages { get; set; }
        public bool FailAfterAll { get; set; }
        public bool FailMetadata { get; set; }
        public CancellationTokenSource? CancelSource { get; set; }
        public int? CancelAfterMessages { get; set; }
        public bool CancelDuringMetadata { get; set; }
        public int ModificationCalls { get; private set; }
        public HashSet<string> ReadIds { get; } = new(StringComparer.Ordinal);
        public Task<EmailModificationResult> MarkReadAsync(IReadOnlyList<string> ids, CancellationToken token = default)
        {
            BeforeModification?.Invoke();
            ModificationCalls++;
            var modified = 0;
            foreach (var id in ids)
            {
                if (CancelAfterMessages == modified)
                {
                    CancelSource!.Cancel();
                    if (modified == 0) throw new OperationCanceledException(token);
                    throw new EmailModificationCancelledException(true, modified,
                        new OperationCanceledException(token), token);
                }
                if (FailAfterMessages == modified)
                    throw new EmailModificationAttemptException(modified > 0, modified,
                        new HttpRequestException("simulated Gmail failure"));
                ReadIds.Add(id);
                modified++;
            }
            if (FailAfterAll) throw new HttpRequestException("response lost after Gmail applied the batch");
            return Task.FromResult(new EmailModificationResult(EmailActionTypes.MarkRead, ids.Count, modified, ids));
        }
        public Task<EmailSearchResultData> SearchEmailsAsync(string? query, int? limit = null, bool? includeRead = null, int? newerThanDays = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<EmailContentData> ReadEmailAsync(string emailId, EmailBodyReadPurpose readPurpose = EmailBodyReadPurpose.Full, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<EmailSummaryData>> ReadEmailMetadataBatchAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        {
            if (CancelDuringMetadata)
            {
                CancelSource!.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }
            if (FailMetadata) throw new HttpRequestException("metadata unavailable");
            return Task.FromResult<IReadOnlyList<EmailSummaryData>>(ids.Select(id => new EmailSummaryData(
                id, "thread-1", null, null, null, null, null,
                ReadIds.Contains(id) ? [] : ["UNREAD"], [], !ReadIds.Contains(id), false, false)).ToList());
        }
        public Task<ThreadData> ReadThreadAsync(string threadId, EmailBodyReadPurpose readPurpose = EmailBodyReadPurpose.Full, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<EmailModificationResult> MarkUnreadAsync(IReadOnlyList<string> ids, CancellationToken token = default) => throw new NotImplementedException();
        public Task<EmailModificationResult> StarAsync(IReadOnlyList<string> ids, CancellationToken token = default) => throw new NotImplementedException();
        public Task<EmailModificationResult> UnstarAsync(IReadOnlyList<string> ids, CancellationToken token = default) => throw new NotImplementedException();
        public Task<EmailModificationResult> MarkImportantAsync(IReadOnlyList<string> ids, CancellationToken token = default) => throw new NotImplementedException();
        public Task<EmailModificationResult> UnmarkImportantAsync(IReadOnlyList<string> ids, CancellationToken token = default) => throw new NotImplementedException();
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        private int activeRequests;
        public int MaxObservedConcurrency { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
                activeRequests++;
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, activeRequests);
            }
            try
            {
                await Task.Delay(10, cancellationToken);
            }
            finally
            {
                lock (Requests) activeRequests--;
            }
            var id = request.RequestUri.Segments.Last().Split('?')[0];
            var body = JsonSerializer.Serialize(new { id, threadId = "thread-1", labelIds = new[] { "STARRED" },
                snippet = "metadata only", payload = new { headers = Array.Empty<object>() } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class PartialModificationHandler : HttpMessageHandler
    {
        private readonly HashSet<string> readIds = new(StringComparer.Ordinal);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.Segments;
            var id = segments[^1].TrimEnd('/') == "modify" ? segments[^2].TrimEnd('/') : segments[^1].TrimEnd('/');
            if (request.Method == HttpMethod.Post)
            {
                if (id == "mail-3") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                readIds.Add(id);
            }
            var labels = readIds.Contains(id) ? Array.Empty<string>() : ["UNREAD"];
            var body = JsonSerializer.Serialize(new { id, threadId = "thread-1", labelIds = labels,
                payload = new { headers = Array.Empty<object>() } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CancellingModificationHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        public int PostCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                PostCount++;
                if (PostCount == 3)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
