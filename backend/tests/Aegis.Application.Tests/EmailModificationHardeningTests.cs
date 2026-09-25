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
        var service = new GmailService(db, http, Options.Create(new GmailOptions()), tokenProtector);

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
        var service = new GmailService(db, http, Options.Create(new GmailOptions()), tokenProtector);

        var failure = await Assert.ThrowsAsync<EmailModificationAttemptException>(() =>
            service.MarkReadAsync(["mail-1", "mail-2", "mail-3"]));
        var state = await service.ReadEmailMetadataBatchAsync(["mail-1", "mail-2", "mail-3"]);

        Assert.True(failure.RequestWasSent);
        Assert.Equal(2, failure.CompletedCount);
        Assert.Equal(new[] { false, false, true }, state.Select(email => email.IsUnread));
    }

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
        AegisDbContext db, IEmailService service, Conversation conversation)
    {
        var context = await CreateContextAsync(db, conversation, "confirmo");
        return await new EmailConfirmPendingActionTool(db, service, new EmailToolContextService(db))
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context);
    }

    private sealed class BatchEmailService : IEmailService
    {
        public int? FailAfterMessages { get; set; }
        public bool FailAfterAll { get; set; }
        public bool FailMetadata { get; set; }
        public int ModificationCalls { get; private set; }
        public HashSet<string> ReadIds { get; } = new(StringComparer.Ordinal);
        public Task<EmailModificationResult> MarkReadAsync(IReadOnlyList<string> ids, CancellationToken token = default)
        {
            ModificationCalls++;
            var modified = 0;
            foreach (var id in ids)
            {
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
}
