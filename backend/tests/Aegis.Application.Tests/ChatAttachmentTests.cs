using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Prompts;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ChatAttachmentTests
{
    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        return output.ToArray();
    }

    private static ChatAttachmentRequest Upload(string name, byte[] bytes) =>
        new(name, "application/octet-stream", Convert.ToBase64String(bytes));

    [Fact]
    public async Task ZipContentsReachModelAndReopenWithOriginalBytesAndMessageOwnership()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var conversation = new Conversation(Conversation.DefaultTitle);
        var message = conversation.AddMessage("user", "Que horas começa?");
        var pdf = "%PDF-1.4\nexample"u8.ToArray();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII=");
        var zip = Zip(("./horários\\agenda.csv", "evento,início\nrevisão,18h47"u8.ToArray()),
            ("mapas/piso.png", png), ("docs/interno.zip", Zip(("aviso.pdf", pdf))),
            ("dados.bin", new byte[] { 255, 0, 1 }));
        var content = ChatAttachmentContent.Prepare(message.Id, [Upload("material.zip", zip)]);
        var parts = content.UserInput(message.Content).GetProperty("content").EnumerateArray().ToArray();
        Assert.Contains(parts, p => p.TryGetProperty("text", out var t) && t.GetString()!.Contains("revisão,18h47") &&
            t.GetString()!.Contains("material.zip/horários/agenda.csv"));
        Assert.Contains(parts, p => p.TryGetProperty("image_url", out var url) && url.GetString() == "data:image/png;base64," + Convert.ToBase64String(png));
        Assert.Contains(parts, p => p.TryGetProperty("file_data", out var data) && data.GetString() == "data:application/pdf;base64," + Convert.ToBase64String(pdf));
        Assert.Contains(parts, p => p.TryGetProperty("text", out var text) && text.GetString()!.Contains("dados.bin"));
        var originalQuestion = JsonSerializer.SerializeToElement(new { role = "user", content = message.Content });
        var prompt = content.Apply(new PromptBuildResult("", null, [originalQuestion]), message.Content);
        Assert.DoesNotContain("18h47", JsonSerializer.Serialize(prompt.AuditInputItems));
        Assert.DoesNotContain("base64", JsonSerializer.Serialize(prompt.AuditInputItems));
        var audit = ChatAttachmentContent.PrivateAudit(new LlmRequestAuditData("provider", "model", false, 2,
            """{"toolCalls":1,"toolExecutions":[{"args":"private"}]}""", 400, "private", "private", null));
        Assert.Null(audit.ResponseBody);
        Assert.DoesNotContain("private", audit.RequestPayloadJson + audit.FailureReason);
        db.AddConversation(conversation);
        db.AddChatAttachments(content.Files);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var index = await db.GetAttachmentIndexAsync(conversation.Id, default);
        var descriptor = Assert.Single(index[message.Id]);
        Assert.Equal("material.zip", descriptor.FileName);
        Assert.Equal("application/zip", descriptor.ContentType);
        Assert.Equal(zip.Length, descriptor.SizeBytes);
        Assert.Equal(zip, (await db.GetAttachmentAsync(message.Id, descriptor.Id, default))!.Data);
        Assert.Null(await db.GetAttachmentAsync(Guid.NewGuid(), descriptor.Id, default));
        var reopened = await db.GetConversationWithMessagesAsync(conversation.Id);
        var followUp = await ChatAttachmentContent.ForFollowUpAsync(db, conversation.Id, reopened!.Messages, "E a revisão?", default);
        Assert.Contains("18h47", followUp.UserInput("E a revisão?").GetRawText());
        for (var i = 0; i < 4; ++i) db.AddChatMessage(reopened.AddMessage("user", $"Outro assunto {i}"));
        Assert.Empty((await ChatAttachmentContent.ForFollowUpAsync(db, conversation.Id, reopened.Messages, "Quanto é 2 + 2?", default)).Files);
        Assert.Single((await ChatAttachmentContent.ForFollowUpAsync(db, conversation.Id, reopened.Messages, "Abra material.zip", default)).Files);
        reopened.Delete();
        await db.SaveChangesAsync();
        Assert.Null(await db.GetAttachmentAsync(message.Id, descriptor.Id, default));
    }

    [Fact]
    public void RejectsUnreadableUnsafeOrUnboundedArchivesBeforePersisting()
    {
        Assert.Throws<ArgumentException>(() => ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("erro.zip", "lixo"u8.ToArray())]));
        foreach (var path in new[] { "../arquivo", "C:/arquivo", "/arquivo" })
            Assert.Throws<ArgumentException>(() => ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("pasta.zip", Zip((path, [1])))]));
        var large = Zip(("limite.txt", new byte[24 * 1024 * 1024 + 1]));
        Assert.Throws<ArgumentException>(() => ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("grande.zip", large)]));
        var many = Zip(Enumerable.Range(0, 257).Select(i => (i.ToString(), Array.Empty<byte>())).ToArray());
        Assert.Throws<ArgumentException>(() => ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("itens.zip", many)]));
        var encrypted = Zip(("segredo", [1]));
        var header = encrypted.AsSpan().IndexOf("PK\x01\x02"u8);
        encrypted[header + 8] |= 1;
        Assert.Throws<ArgumentException>(() => ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("senha.zip", encrypted)]));
    }
}
