using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Prompts;
using Aegis.Application.Tools;
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
    public void RejectsUnreadableAndUnsafeArchivesBeforePersisting()
    {
        Assert.Throws<ArgumentException>(() => ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("erro.zip", "lixo"u8.ToArray())]));
        foreach (var path in new[] { "../arquivo", "C:/arquivo", "/arquivo" })
            Assert.Throws<ArgumentException>(() => ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("pasta.zip", Zip((path, [1])))]));
        var encrypted = Zip(("segredo", [1]));
        var header = encrypted.AsSpan().IndexOf("PK\x01\x02"u8);
        encrypted[header + 8] |= 1;
        Assert.Throws<ArgumentException>(() => ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("senha.zip", encrypted)]));
    }

    [Fact]
    public async Task LargeNestedZipSupportsSearchAcrossBufferBoundariesAndReadingNearTheEnd()
    {
        const int size = 512 * 1024 * 1024;
        int position = size - 32768 - 6;
        var marker = "marcador-quase-no-fim"u8.ToArray();
        using var compressed = new MemoryStream();
        using (var zip = new ZipArchive(compressed, ZipArchiveMode.Create, true))
        using (var file = zip.CreateEntry("grande.csv", CompressionLevel.Fastest).Open())
        {
            var block = new byte[65536];
            Array.Fill(block, (byte)'x');
            for (int written = 0; written < position; written += block.Length)
                file.Write(block, 0, Math.Min(block.Length, position - written));
            file.Write(marker);
            file.Write(block, 0, size - position - marker.Length);
        }
        var archive = Zip(("detalhes.zip", compressed.ToArray()), ("nota.txt", "pronto"u8.ToArray()));
        var content = ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("material.zip", archive)]);
        var preview = content.UserInput("Encontre o marcador").GetRawText();
        Assert.True(content.OffersReader);
        Assert.Contains("pronto", preview);
        Assert.DoesNotContain("marcador-quase-no-fim", preview);
        Assert.True(preview.Length < 100_000);

        var context = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "", AttachmentReader: content);
        var tool = new AttachmentReadTool();
        var index = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { action = "list", query = ".csv" }), context);
        var entry = Assert.Single(JsonSerializer.Deserialize<JsonElement>(index.Content).GetProperty("items").EnumerateArray());
        Assert.Equal("material.zip/detalhes.zip/grande.csv", entry.GetProperty("name").GetString());
        Assert.Equal(size, entry.GetProperty("size").GetInt32());
        int id = entry.GetProperty("id").GetInt32();
        var search = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { action = "search", id, query = "marcador-quase-no-fim" }), context);
        var match = JsonSerializer.Deserialize<JsonElement>(search.Content);
        Assert.True(match.GetProperty("found").GetBoolean());
        Assert.Contains("marcador-quase-no-fim", match.GetProperty("text").GetString());
        var read = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { action = "read", id, offset = position }), context);
        Assert.StartsWith("marcador-quase-no-fim", JsonSerializer.Deserialize<JsonElement>(read.Content).GetProperty("text").GetString());
    }

    [Fact]
    public void ReadsLargeOfficeDocumentInsideZipWithoutSendingEmbeddedBinaryToModel()
    {
        using var office = new MemoryStream();
        using (var package = new ZipArchive(office, ZipArchiveMode.Create, true))
        {
            using (var image = package.CreateEntry("word/media/large.jpg", CompressionLevel.NoCompression).Open())
            {
                var block = new byte[65536];
                for (var i = 0; i < 272; i++) image.Write(block);
            }
            using var document = package.CreateEntry("word/document.xml").Open();
            document.Write("<document><p><t>encontro no galpão 47</t></p></document>"u8);
        }
        Assert.True(office.Length > 16 * 1024 * 1024);
        var zip = Zip(("ata.docx", office.ToArray()));
        var content = ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("entrega.zip", zip)]);
        var input = content.UserInput("Onde?").GetProperty("content").EnumerateArray().ToArray();
        Assert.Contains(input, item => item.TryGetProperty("text", out var text) && text.GetString()!.Contains("encontro no galpão 47"));
        Assert.DoesNotContain(input, item => item.TryGetProperty("file_data", out _));
    }

    [Fact]
    public async Task PaginatesTheZipIndexAndKeepsTheOriginalBeyondTheIndexBudget()
    {
        var items = Enumerable.Range(0, 300).Select(i => ($"item-{i:D4}.bin", new byte[] { 0, 255 })).ToArray();
        var content = ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("itens.zip", Zip(items))]);
        var ctx = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "", AttachmentReader: content);
        var index = await new AttachmentReadTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { action = "list", query = ".bin", offset = 282 }), ctx);
        var page = JsonSerializer.Deserialize<JsonElement>(index.Content);
        Assert.Equal(300, page.GetProperty("total").GetInt32());
        Assert.Equal(18, page.GetProperty("items").GetArrayLength());
        Assert.Equal("itens.zip/item-0299.bin", page.GetProperty("items")[17].GetProperty("name").GetString());

        var archive = Zip(Enumerable.Range(0, 4097).Select(i => ($"a{i}", Array.Empty<byte>())).ToArray());
        var partial = ChatAttachmentContent.Prepare(Guid.NewGuid(), [Upload("extenso.zip", archive)]);
        Assert.True(partial.OffersReader);
        Assert.Contains("4096", partial.UserInput("Leia").GetRawText());
        Assert.Equal(archive, Assert.Single(partial.Files).Data);
    }
}
