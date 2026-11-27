using System.Text;
using System.Text.Json;
using Aegis.Application.Tools;

namespace Aegis.Application.Chat;

public sealed partial class ChatAttachmentContent
{
    private sealed record Item(int Id, int FileIndex, int[] Path, string Name, long Size)
    {
        public string Type { get; set; } = "application/octet-stream";
        public string? Unavailable { get; set; }
        public bool IsText => Type.StartsWith("text/", StringComparison.Ordinal) || Type == "application/json";
        public bool IsOffice => Type.StartsWith("application/vnd.openxmlformats-officedocument.", StringComparison.Ordinal);
        public bool IsImage => Type.StartsWith("image/", StringComparison.Ordinal);
    }

    private readonly List<Item> items = [];

    internal async Task<object> ReadAsync(string action, int id, string query, int offset, int page, CancellationToken ct)
    {
        if (action == "list")
        {
            var matches = items.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            var pageItems = matches.Skip(offset).Take(20).Select(i => new { id = i.Id, name = i.Name, size = i.Size, type = i.Type, note = i.Unavailable });
            return new { items = pageItems, total = matches.Count, nextOffset = Math.Min(offset + 20L, matches.Count) };
        }

        if (id < 1 || id > items.Count) throw new ArgumentException("Use o número de um arquivo deste turno. Consulte list para ver o índice.");
        var item = items[id - 1];
        if (item.Unavailable is not null) return new { note = item.Unavailable, name = item.Name };
        if (action == "search" && string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Informe o trecho de texto a buscar.");
        if (!item.IsText && !item.IsOffice && item.Type != "application/pdf") return new { note = "Use list para consultar o índice. Este formato não oferece leitura adicional como texto.", name = item.Name };

        var file = Files[item.FileIndex];
        await using var root = db is null ? new MemoryStream(file.Data, false) : await db.OpenAttachmentContentAsync(file, ct);
        return await ReadItemAsync(root, item, 0, action, query, offset, page, ct);
    }

    private static async Task<object> ReadItemAsync(Stream stream, Item item, int depth, string action,
        string query, int offset, int page, CancellationToken ct)
    {
        if (depth < item.Path.Length)
        {
            await using var temp = stream.CanSeek ? null : await AttachmentZip.SpoolAsync(stream, stream.Length, ct);
            using var archive = AttachmentZip.Open(temp ?? stream, AttachmentZip.MaximumEntries);
            using var nested = new AttachmentZip.EntryReader(archive.Entries[item.Path[depth]]);
            return await ReadItemAsync(nested, item, depth + 1, action, query, offset, page, ct);
        }
        if (item.Type == "application/pdf")
        {
            if (action == "search") return new { note = "Para pesquisar no PDF, consulte páginas com read e page. A busca literal opera em arquivos de texto." };
            var result = await LargeAttachmentMedia.PreviewAsync(stream, item.Type, page, ct, includeImage: false);
            return new { page, text = result.Text };
        }

        // Search by chunks rather than lines, so a one-line JSON file cannot exhaust memory.
        using var office = item.IsOffice ? await LargeOfficeText.OpenAsync(stream, item.Type, ct) : null;
        string? coverage = null;
        if (office is not null)
        {
            using var header = new StreamReader(office, StrictUtf8, true, 1024, leaveOpen: true);
            coverage = (await header.ReadLineAsync(ct))?.TrimEnd();
            office.Position = 0;
        }
        using var reader = new StreamReader(office ?? stream, StrictUtf8, true, 32768, leaveOpen: true);
        const int blockSize = 32768, resultLength = 8000;
        int maxOverlap = action == "search" ? query.Length - 1 : 0;
        var buffer = new char[blockSize + maxOverlap];
        long cursor = 0;
        int overlap = 0;
        while (true)
        {
            int count = await reader.ReadBlockAsync(buffer.AsMemory(overlap, blockSize), ct);
            if (count == 0) return new { found = false, endOfFile = true, characters = cursor, coverage };
            long windowStart = cursor - overlap;
            cursor += count;
            if (cursor <= offset) continue;
            int first = (int)Math.Max(0, offset - windowStart);
            int end = overlap + count;
            int position = action == "search"
                ? MemoryExtensions.IndexOf(buffer.AsSpan(first, end - first), query.AsSpan(), StringComparison.OrdinalIgnoreCase) : 0;
            if (position >= 0)
            {
                int beginning = first + Math.Max(0, position - 500);
                int take = Math.Min(end - beginning, resultLength);
                string text = new(buffer, beginning, take);
                if (take < resultLength && count == blockSize)
                {
                    int extra = await reader.ReadBlockAsync(buffer.AsMemory(0, resultLength - take), ct);
                    text += new string(buffer, 0, extra);
                }
                if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
                if (text.Length > 0 && char.IsLowSurrogate(text[0])) { text = text[1..]; beginning++; }
                return new { found = true, offset = windowStart + beginning, nextOffset = windowStart + beginning + text.Length, text, coverage };
            }
            overlap = Math.Min(end - first, maxOverlap);
            Array.Copy(buffer, end - overlap, buffer, 0, overlap);
        }
    }
}

public sealed class AttachmentReadTool : IAegisTool
{
    public const string ToolName = "read_attachment";
    public string Name => ToolName;
    public string Description => "Leia anexos do turno localmente. list pagina ou filtra caminhos pelo query; read devolve texto de um id a partir de offset (caracteres), ou uma página de PDF (page começa em 1); search procura query literalmente em todo o arquivo de texto desde offset. Use quando a prévia for parcial.";
    public JsonElement ParametersSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            action = new { type = "string", @enum = new[] { "list", "read", "search" } },
            id = new { type = "integer", minimum = 1 },
            query = new { type = "string", maxLength = 256 },
            offset = new { type = "integer", minimum = 0 },
            page = new { type = "integer", minimum = 1 }
        },
        required = new[] { "action" }, additionalProperties = false
    });

    public async Task<AegisToolResult> ExecuteAsync(JsonElement args, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (context.AttachmentReader is not { OffersReader: true } reader)
            return Error("Nenhum anexo está disponível para esta consulta.");
        try
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("action", out var act) ||
                act.ValueKind != JsonValueKind.String || act.GetString() is not ("read" or "search" or "list"))
                return Error("A ação deve ser list, read ou search.");
            var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            if (query.Length > 256) return Error("O texto de busca pode ter até 256 caracteres.");
            int offset = args.TryGetProperty("offset", out var at) ? at.GetInt32() : 0;
            int id = args.TryGetProperty("id", out var item) ? item.GetInt32() : 0;
            int page = args.TryGetProperty("page", out var p) ? p.GetInt32() : 1;
            if (offset < 0 || page < 1) return Error("Use offset a partir de zero e page a partir de um.");
            var value = await reader.ReadAsync(act.GetString()!, id, query, offset, page, ct);
            return new AegisToolResult(true, JsonSerializer.Serialize(value));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or IOException)
        {
            return Error(ex is ArgumentException and not DecoderFallbackException ? ex.Message : "Não foi possível ler este trecho com os parâmetros fornecidos.");
        }
    }

    private static AegisToolResult Error(string message) => new(false,
        JsonSerializer.Serialize(new { error = "invalid_tool_arguments", message }), "invalid_tool_arguments");
}
