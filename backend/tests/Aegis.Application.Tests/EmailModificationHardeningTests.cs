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
    public async Task FailedGmailExecutionLeavesPendingActionOpenForRetryOrCancellation()
    {
        using var db = CreateDb();
        var conversation = new Conversation();
        db.Conversations.Add(conversation);
        var action = new PendingEmailAction(conversation.Id, EmailActionTypes.MarkRead,
            "[\"mail-1\"]", "marcar email como lido", DateTimeOffset.UtcNow.AddMinutes(10));
        db.PendingEmailActions.Add(action);
        await db.SaveChangesAsync();
        var userMessage = conversation.AddMessage(ChatRoles.User, "confirmo");
        db.ChatMessages.Add(userMessage);
        await db.SaveChangesAsync();

        var tool = new EmailConfirmPendingActionTool(db, new FailingEmailService(), new EmailToolContextService(db));
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }),
            new ToolExecutionContext(conversation.Id, userMessage.Id, "confirmo"));

        Assert.False(result.Success);
        Assert.True(action.IsOpen());
        Assert.Null(action.ConfirmedAt);
        Assert.Null(action.ExecutedAt);
        Assert.NotNull(await db.GetLatestOpenPendingEmailActionAsync(conversation.Id));
        Assert.Single(db.EmailActionAudits);
        action.Cancel();
        await db.SaveChangesAsync();
        Assert.NotNull(action.CancelledAt);
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

    private static AegisDbContext CreateDb() => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FailingEmailService : IEmailService
    {
        public Task<EmailModificationResult> MarkReadAsync(IReadOnlyList<string> ids, CancellationToken token = default) =>
            throw new HttpRequestException("simulated Gmail failure");
        public Task<EmailSearchResultData> SearchEmailsAsync(string? query, int? limit = null, bool? includeRead = null, int? newerThanDays = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<EmailContentData> ReadEmailAsync(string emailId, EmailBodyReadPurpose readPurpose = EmailBodyReadPurpose.Full, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<EmailSummaryData>> ReadEmailMetadataBatchAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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
}
