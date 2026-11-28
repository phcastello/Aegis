using Aegis.Application.Chat;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ChatLinkAttachmentTests
{
    private sealed class PostgreSqlFact : FactAttribute
    {
        public PostgreSqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_OPERATION_TEST_DATABASE")))
                Skip = "Requires the disposable PostgreSQL operation test database.";
        }
    }

    [Fact]
    public async Task HistoryRetainsFullLinkWhilePromptUsesOnlyPublicReference()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        const string url = "https://example.com/material?token=private#topic";
        const string privateUrl = "http://192.168.1.10/aulas";
        var conversation = new Conversation(Conversation.DefaultTitle);
        var message = conversation.AddMessage("user", "Quando é a prova?");
        var turn = await ChatAttachmentContent.PrepareAsync(db, message.Id, null, null, default, [url, privateUrl]);
        Assert.True(new SendMessageRequest { AttachmentLinks = [url] }.HasMessage);
        Assert.Equal([0, 1], turn.Files.Select(f => f.Position));
        var prompt = turn.UserInput(message.Content).GetRawText();
        Assert.Contains("https://example.com/material", prompt);
        Assert.Contains("omitidos", prompt);
        Assert.DoesNotContain("token", prompt);
        Assert.DoesNotContain("topic", prompt);
        Assert.DoesNotContain("192.168.1.10", prompt);

        db.AddConversation(conversation);
        db.AddChatAttachments(turn.Files);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var index = await db.GetAttachmentIndexAsync(conversation.Id, default);
        Assert.Equal([url, privateUrl], index[message.Id].Select(link => link.LinkUrl));
        Assert.All(index[message.Id], file => Assert.Equal(ChatLinkAttachment.ContentType, file.ContentType));

        var reloaded = (await db.GetConversationWithMessagesAsync(conversation.Id))!;
        var unrelated = reloaded.AddMessage("user", "Confira esta anotação.");
        db.AddChatMessage(unrelated);
        db.AddChatAttachments([new ChatAttachment(unrelated.Id, 0, "anotacao.txt", "text/plain", "Nota."u8.ToArray())]);
        await db.SaveChangesAsync();
        var history = reloaded.Messages;
        var followUp = await ChatAttachmentContent.ForFollowUpAsync(db, conversation.Id, history, "O que diz o link?", default);
        var followUpPrompt = followUp.UserInput("O que diz o link?").GetRawText();
        Assert.Contains("https://example.com/material", followUpPrompt);
        Assert.DoesNotContain("token", followUpPrompt);
        Assert.DoesNotContain("192.168.1.10", followUpPrompt);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://owner:pass@example.com/")]
    [InlineData("https://example.com/bad path")]
    [InlineData("https://example.com\\bad")]
    public async Task RejectsMalformedOrCredentialBearingLinks(string link)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await Assert.ThrowsAsync<ArgumentException>(() => ChatAttachmentContent.PrepareAsync(db, Guid.NewGuid(),
            null, null, default, [link]));
    }

    [Fact]
    public async Task FileAndLinkShareOneAttachmentCount()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var links = Enumerable.Range(1, 20).Select(i => $"https://example.com/{i}").ToArray();
        var file = new ChatAttachmentRequest("aula.txt", "text/plain", "U2FsYQ==");
        var allowed = await ChatAttachmentContent.PrepareAsync(db, Guid.NewGuid(), [file], null, default, links[..19]);
        Assert.Equal(20, allowed.Files.Count);
        Assert.Equal("aula.txt", allowed.Files[0].FileName);
        Assert.All(allowed.Files.Skip(1), link => Assert.Equal(ChatLinkAttachment.ContentType, link.ContentType));
        await Assert.ThrowsAsync<ArgumentException>(() => ChatAttachmentContent.PrepareAsync(db, Guid.NewGuid(),
            [file], null, default, links));
    }

    [Theory]
    [InlineData("https://server.tailnet.ts.net/aulas")]
    [InlineData("http://server.localdomain/aulas")]
    [InlineData("https://example.com:9443/aulas")]
    public async Task InternalLinksStayInTheChatOnly(string link)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var prepared = await ChatAttachmentContent.PrepareAsync(db, Guid.NewGuid(), null, null, default, [link]);
        var file = Assert.Single(prepared.Files);
        Assert.Equal(link, ChatLinkAttachment.ReadUrl(file.Data));
        Assert.DoesNotContain(file.FileName, prepared.UserInput("Abra este link.").GetRawText());
    }

    [PostgreSqlFact]
    public Task LinkAndChunkedUploadReopenFromPostgres() =>
        PrivateOperationPostgresTests.WithSchema(async create =>
        {
            await using var db = create();
            await db.Database.MigrateAsync();
            const string url = "https://example.com/documento?item=99#p1";
            var bytes = "Sala 517"u8.ToArray();
            var uploads = new ChatUploadStore(db);
            var upload = await uploads.CreateAsync("aulas.txt", "text/plain", bytes.Length, default);
            using (var stream = new MemoryStream(bytes))
                Assert.Equal(ChunkWriteResult.Saved, await uploads.WriteChunkAsync(upload.Id, 0, stream, bytes.Length, default));
            var conversation = new Conversation(Conversation.DefaultTitle);
            var message = conversation.AddMessage("user", "Abra os anexos.");
            db.AddConversation(conversation);
            var prepared = await ChatAttachmentContent.PrepareAsync(db, message.Id, null, [upload.Id], default, [url]);
            db.AddChatAttachments(prepared.Files);
            await db.SaveChangesAsync();

            await using var otherNode = create();
            var attachments = (await otherNode.GetAttachmentIndexAsync(conversation.Id, default))[message.Id].ToArray();
            Assert.Collection(attachments,
                file => { Assert.Equal("aulas.txt", file.FileName); Assert.Null(file.LinkUrl); },
                link => { Assert.Equal(ChatLinkAttachment.ContentType, link.ContentType); Assert.Equal(url, link.LinkUrl); });
            var original = await otherNode.GetAttachmentAsync(message.Id, attachments[0].Id, default);
            Assert.NotNull(original);
            await using var originalStream = await otherNode.OpenAttachmentContentAsync(original, default);
            using var downloaded = new MemoryStream();
            await originalStream.CopyToAsync(downloaded);
            Assert.Equal(bytes, downloaded.ToArray());
        });
}
