using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Api.Controllers;
using Aegis.Application.Chat;
using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Application.Tools;
using Aegis.Application.Turns;
using Aegis.Application.Voice;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ScriptFileDeliveryIntegrationTests
{
    private sealed class PackagedRuntimeFactAttribute : FactAttribute
    {
        public PackagedRuntimeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("AEGIS_SCRIPT_DELIVERY_TEST") != "1")
                Skip = "Requires the packaged WASI runtime, /run/aegis-script tmpfs and AEGIS_OPERATION_TEST_DATABASE.";
        }
    }

    [PackagedRuntimeFact]
    public Task UploadedDeflatedZipProducesOfficeFilesAndAnExpiredWorkspaceCanBeRestored() =>
        PrivateOperationPostgresTests.WithSchema(async create =>
    {
        await using var db = create();
        await db.Database.MigrateAsync();
        var storage = Directory.CreateTempSubdirectory("aegis-script-delivery-");
        var clock = new TestClock();
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var services = builder.Services;
            services.AddControllers().AddApplicationPart(typeof(ChatController).Assembly);
            services.AddScoped<AegisDbContext>(_ => create());
            services.AddScoped<IAegisDbContext>(sp => sp.GetRequiredService<AegisDbContext>());
            services.AddScoped<ChatUploadStore>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            services.AddSingleton<IScriptArtifactStore, ScriptArtifactStore>();
            services.AddSingleton(sp => new ScriptWorkspace(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IScriptArtifactStore>(), clock, sp.GetRequiredService<ILogger<ScriptWorkspace>>(),
                storage.FullName, "/run/aegis-script"));
            services.AddSingleton<IPythonScriptRunner, WasmPythonScriptRunner>();
            services.AddScoped<IAegisTool, PythonScriptTool>();
            services.AddScoped<IAegisToolRegistry, AegisToolRegistry>();
            services.AddScoped<IAegisToolLoop, AegisToolLoop>();
            services.AddSingleton<IAegisModelClient, FileTaskModel>();
            services.AddSingleton<IConversationTitleJobQueue, ConversationTitleJobQueue>();
            services.AddSingleton<IRuntimeContextProvider, RuntimeContextProvider>();
            services.AddSingleton<AegisMetrics>();
            services.AddSingleton<IActiveTurnRegistry, ActiveTurnRegistry>();
            services.AddScoped<IVoiceService>(sp => new VoiceService(sp.GetRequiredService<IAegisDbContext>(),
                sp.GetRequiredService<IActiveTurnRegistry>(), null!, null!));
            services.AddScoped<IPromptBuilder, PromptBuilder>();
            services.AddScoped<IChatService, ChatService>();
            await using var app = builder.Build();
            app.MapControllers();
            await app.StartAsync();
            Assert.True(app.Services.GetRequiredService<IPythonScriptRunner>().IsAvailable,
                $"The packaged WASI runner is unavailable. Scratch filesystem: {new DriveInfo("/run/aegis-script").DriveFormat}.");
            using var client = app.GetTestClient();
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry("loja/vendas.csv", CompressionLevel.Optimal).Open())
                entry.Write("mes,valor\nfevereiro,42\nmarco,13\n"u8);
            using var upload = await client.PostAsJsonAsync("/api/chat/uploads", new { fileName = "vendas.zip", contentType = "application/zip", sizeBytes = zip.Length });
            upload.EnsureSuccessStatusCode();
            var uploadId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            using var chunk = await client.PutAsync($"/api/chat/uploads/{uploadId}/chunks/0", new ByteArrayContent(zip.ToArray()));
            chunk.EnsureSuccessStatusCode();

            async Task<SendMessageResponse> Ask(SendMessageRequest request)
            {
                using var response = await client.PostAsJsonAsync("/api/chat/messages", request);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<SendMessageResponse>())!;
            }
            async Task<byte[]> Download(ChatMessageResponse message, string name, string expectedType)
            {
                var file = Assert.Single(message.Attachments!, item => item.FileName == name);
                using var download = await client.GetAsync($"/api/chat/messages/{message.Id}/attachments/{file.Id}");
                download.EnsureSuccessStatusCode();
                Assert.Equal(file.FileName, download.Content.Headers.ContentDisposition!.FileNameStar);
                Assert.Equal(expectedType, download.Content.Headers.ContentType!.MediaType);
                return await download.Content.ReadAsByteArrayAsync();
            }
            async Task VerifyFiles(ChatMessageResponse message, int total)
            {
                Assert.Equal("total\n" + total + "\n", System.Text.Encoding.UTF8.GetString(
                    await Download(message, "resultado.csv", "text/csv")));
                using var excel = new ZipArchive(new MemoryStream(await Download(message, "resultado.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")));
                using var row = new StreamReader(excel.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                Assert.Contains($"<v>{total}</v>", await row.ReadToEndAsync());
                using var word = new ZipArchive(new MemoryStream(await Download(message, "relatorio.docx",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document")));
                using var paragraph = new StreamReader(word.GetEntry("word/document.xml")!.Open());
                Assert.Contains($"Total: {total}", await paragraph.ReadToEndAsync());
            }
            var first = await Ask(new() { Content = "Consolide o ZIP em CSV.", AttachmentUploadIds = [uploadId] });
            await VerifyFiles(first.Message, 55);
            var workspace = app.Services.GetRequiredService<ScriptWorkspace>();
            var workPath = Path.Combine(storage.FullName, first.ConversationId.ToString("N"), "work.zip");
            Assert.True(File.Exists(workPath));
            clock.Value += TimeSpan.FromHours(73);
            await workspace.SweepAsync(default);
            Assert.False(File.Exists(workPath));
            var second = await Ask(new() { ConversationId = first.ConversationId, Content = "Refaça sem março." });
            await VerifyFiles(second.Message, 42);
            var history = await client.GetFromJsonAsync<ConversationResponse>($"/api/chat/conversations/{first.ConversationId}");
            var answers = history!.Messages.Where(m => m.Role == "assistant").ToArray();
            Assert.Equal(2, answers.Length);
            await VerifyFiles(answers[0], 55);
            await VerifyFiles(answers[1], 42);
            var executionRecords = await db.ToolContextEntries
                .Where(entry => entry.ConversationId == first.ConversationId && entry.EntryType == ScriptRecipeReferences.Execution)
                .Select(entry => entry.DataJson).ToListAsync();
            Assert.Equal(2, executionRecords.Count);
            Assert.All(executionRecords, json =>
            {
                Assert.DoesNotContain("private_recipe_marker", json);
                Assert.Contains("SanitizedCode", json);
                Assert.Contains("RuntimeRevision", json);
            });
        }
        finally { storage.Delete(recursive: true); }
    });

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Value;
    }

    // The model selects tools deterministically; chat, WASI, file persistence and HTTP downloads are real.
    private sealed class FileTaskModel : IAegisModelClient
    {
        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken ct = default)
        {
            var output = request.InputItems?.LastOrDefault(item => item.TryGetProperty("type", out var value) &&
                value.GetString() == "function_call_output");
            ModelToolCall[] calls = [];
            if (output is null or { ValueKind: JsonValueKind.Undefined })
            {
                var first = request.Request.Input.Contains("ZIP", StringComparison.Ordinal);
                const string load = """
                    import csv, io, json, zipfile
                    private_recipe_marker = "execute only inside WASI"
                    path = next(f['path'] for f in json.load(open('/inputs/index.json')) if f['name'] == 'vendas.zip')
                    with zipfile.ZipFile(path) as z:
                        item = z.getinfo('loja/vendas.csv')
                        assert item.compress_type == zipfile.ZIP_DEFLATED
                        rows = list(csv.DictReader(io.StringIO(z.read(item).decode())))
                    with open('/work/vendas.json', 'w') as out:
                        json.dump(rows, out)
                    """;
                const string resume = """
                    import json
                    rows = [r for r in json.load(open('/work/vendas.json')) if r['mes'] != 'marco']
                    """;
                const string write = """
                    from openpyxl import Workbook
                    from aegis_docx import write_document
                    total = sum(int(r['valor']) for r in rows)
                    with open('/outputs/resultado.csv', 'w') as out:
                        out.write('total\n' + str(total) + '\n')
                    book = Workbook()
                    book.active.append(['Total', total])
                    book.save('/outputs/resultado.xlsx')
                    write_document('/outputs/relatorio.docx', [f'Total: {total}'])
                    """;
                var args = first
                    ? JsonSerializer.SerializeToElement(new { code = load + "\n" + write, input = (string?)null,
                        input_files = new[] { "vendas.zip" } })
                    : JsonSerializer.SerializeToElement(new { code = resume + "\n" + write, input = (string?)null,
                        restore_from = "resultado.xlsx" });
                calls = [new ModelToolCall("script", PythonScriptTool.ToolName, args)];
            }
            else
            {
                using var result = JsonDocument.Parse(output.Value.GetProperty("output").GetString()!);
                Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString());
                if (!request.Request.Input.Contains("ZIP", StringComparison.Ordinal))
                    Assert.Equal(1, result.RootElement.GetProperty("restored_workspace_steps").GetInt32());
            }
            return Task.FromResult(new ModelToolResponse("Preparei o CSV.", "fixture", "script", ModelPurpose.Chat,
                calls, [], null, null, new LlmRequestAuditData("fixture", "script", true, 1, "{}", 200, null, null, null)));
        }

        public IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
