using Aegis.Application.Chat;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ConversationChangePersistenceTests
{
    private sealed class RecordingPublisher : IConversationChangePublisher
    {
        public List<Guid> Changes { get; } = [];
        public void Publish(Guid conversationId) => Changes.Add(conversationId);
    }

    [Fact]
    public async Task SuccessfulWritesPublishEachAffectedConversationOnceAndNoOpsPublishNothing()
    {
        var publisher = new RecordingPublisher();
        var options = new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AegisDbContext(options, publisher);
        var conversation = new Conversation("Synthetic conversation");
        db.AddConversation(conversation);
        db.AddChatMessage(conversation.AddMessage("user", "Synthetic user message."));
        await db.SaveChangesAsync();
        Assert.Equal(new[] { conversation.Id }, publisher.Changes);
        publisher.Changes.Clear();

        Assert.Equal(0, await db.SaveChangesAsync());
        Assert.Empty(publisher.Changes);
        var answer = conversation.AddMessage("assistant", "Part one.");
        db.AddChatMessage(answer);
        db.SaveChanges();
        Assert.Equal(new[] { conversation.Id }, publisher.Changes);
        publisher.Changes.Clear();

        // A streamed segment updates an existing message without adding another row.
        answer.UpdateContent("Part one. Part two.");
        await db.SaveChangesAsync();
        Assert.Equal(new[] { conversation.Id }, publisher.Changes);
    }

    [NodePostgresTests.PostgresFact]
    public Task ExplicitTransactionPublishesOnlyAfterCommitAndDirectTitleFollowsSameRule() =>
        NodePostgresTests.WithDatabase(async create =>
        {
            await using var prepared = create();
            var publisher = new RecordingPublisher();
            var options = new DbContextOptionsBuilder<AegisDbContext>()
                .UseNpgsql(prepared.Database.GetConnectionString())
                .AddInterceptors(new ConversationChangeCommitInterceptor())
                .Options;
            await using var db = new AegisDbContext(options, publisher);

            var conversation = new Conversation("Synthetic conversation");
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.AddConversation(conversation);
                db.AddChatMessage(conversation.AddMessage("user", "Synthetic user message."));
                await db.SaveChangesAsync();
                Assert.Empty(publisher.Changes);

                db.AddChatMessage(conversation.AddMessage("assistant", "Synthetic assistant message."));
                await db.SaveChangesAsync();
                Assert.Empty(publisher.Changes);

                await tx.CommitAsync();
                Assert.Equal(new[] { conversation.Id }, publisher.Changes);
            }
            publisher.Changes.Clear();

            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                Assert.True(await db.SetGeneratedTitleIfEligibleAsync(conversation.Id, "Rolled back title", null, default));
                Assert.Empty(publisher.Changes);
                await tx.RollbackAsync();
                Assert.Empty(publisher.Changes);
            }

            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                Assert.True(await db.SetGeneratedTitleIfEligibleAsync(conversation.Id, "Committed title", null, default));
                Assert.Empty(publisher.Changes);
                await tx.CommitAsync();
                Assert.Equal(new[] { conversation.Id }, publisher.Changes);
            }

            publisher.Changes.Clear();
            Assert.False(await db.SetGeneratedTitleIfEligibleAsync(conversation.Id, "Ineligible second title", null, default));
            Assert.Empty(publisher.Changes);
        });
}
