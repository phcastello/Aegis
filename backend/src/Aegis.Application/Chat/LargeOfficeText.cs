using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Aegis.Application.Chat;

// Open XML parts are read independently. Pictures and embedded objects never need to be inflated.
public static class LargeOfficeText
{
    private const int MaxOutputBytes = 128 * 1024 * 1024;
    private const int MaxCellChars = 32767; // Excel's maximum text length per cell.
    private const int HeaderBytes = 192;
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly SemaphoreSlim Slots = new(2, 2);

    public static async Task<Stream> OpenAsync(Stream source, string contentType, CancellationToken ct)
    {
        await Slots.WaitAsync(ct);
        try
        {
            await using var copy = source.CanSeek ? null : await AttachmentZip.SpoolAsync(source, source.Length, ct);
            var output = TemporaryFile();
            try
            {
                string status = "Texto extraído. Formatação visual e objetos incorporados não estão incluídos.";
                output.Write(new byte[HeaderBytes]);
                using (var writer = new StreamWriter(output, Utf8, 8192, leaveOpen: true))
                {
                    try
                    {
                        using var archive = AttachmentZip.Open(copy ?? source, AttachmentZip.MaximumEntries);
                        new Package(archive, writer, ct).Extract(contentType);
                    }
                    catch (Exception e) when (e is ExtractionLimit or AttachmentIndexLimitException)
                    {
                        status = "Leitura parcial. O arquivo alcançou o limite de extração. A busca cobre somente o texto extraído.";
                        writer.WriteLine("\n[Limite de extração alcançado.]");
                    }
                    catch (Exception e) when (e is InvalidDataException or XmlException or ArgumentException)
                    {
                        status = "Leitura incompleta. Não foi possível interpretar uma parte do documento. A busca cobre somente o texto extraído.";
                        writer.WriteLine("\n[Uma parte do documento não pôde ser lida.]");
                    }
                }
                ct.ThrowIfCancellationRequested();
                output.Position = 0;
                var header = new byte[HeaderBytes];
                Array.Fill(header, (byte)' ');
                Utf8.GetBytes(status.AsSpan(), header);
                header[^1] = (byte)'\n';
                output.Write(header);
                output.Position = 0;
                return output;
            }
            catch
            {
                await output.DisposeAsync();
                throw;
            }
        }
        finally { Slots.Release(); }
    }

    private static FileStream TemporaryFile()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.DeleteOnClose };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), options);
    }

    private sealed class ExtractionLimit : Exception;
    private sealed record Relation(string Type, string Path);

    private sealed class Package
    {
        private readonly Dictionary<string, ZipArchiveEntry> parts = new(StringComparer.Ordinal);
        private readonly StreamWriter output;
        private readonly CancellationToken ct;
        private readonly char[] chars = new char[4096];
        private long remainingXmlBytes = ChatUploadLimits.MaxFileBytes;

        public Package(ZipArchive archive, StreamWriter output, CancellationToken ct)
        {
            this.output = output;
            this.ct = ct;
            foreach (var entry in archive.Entries)
            {
                var path = AttachmentZip.EntryPath(entry.FullName);
                if (entry.Name.Length != 0 && !parts.TryAdd(path, entry))
                    throw new InvalidDataException();
            }
        }

        public void Extract(string type)
        {
            var root = Relationships("").Values.FirstOrDefault(r => r.Type == "officeDocument")?.Path;
            switch (type)
            {
                case "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
                    var document = root ?? "word/document.xml";
                    RenderParagraphs(document);
                    foreach (var linked in Relationships(document).Values.Distinct())
                    {
                        if (linked.Type is not ("header" or "footer" or "endnotes" or "footnotes")) continue;
                        output.WriteLine($"\n[{linked.Path}]");
                        RenderParagraphs(linked.Path);
                    }
                    break;
                case "application/vnd.openxmlformats-officedocument.presentationml.presentation":
                    var deck = root ?? "ppt/presentation.xml";
                    var slideLinks = Relationships(deck);
                    int number = 0;
                    foreach (var (id, _) in OrderedParts(deck, "sldId"))
                    {
                        if (!slideLinks.TryGetValue(id, out var slide) || slide.Type != "slide")
                            throw new InvalidDataException();
                        output.WriteLine($"\nSlide {++number}");
                        RenderParagraphs(slide.Path);
                    }
                    break;
                case "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet":
                    RenderWorkbook(root ?? "xl/workbook.xml");
                    break;
                default: throw new ArgumentException("Formato Office desconhecido.");
            }
        }

        private XmlReader OpenXml(string path, bool metadata = false)
        {
            ct.ThrowIfCancellationRequested();
            if (!parts.TryGetValue(path, out var entry)) throw new InvalidDataException();
            if (entry.Length > remainingXmlBytes || metadata && entry.Length > 8 * 1024 * 1024)
                throw new ExtractionLimit();
            remainingXmlBytes -= entry.Length;
            var input = new AttachmentZip.EntryReader(entry);
            try
            {
                return XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, CloseInput = true, IgnoreComments = true, IgnoreProcessingInstructions = true,
                    MaxCharactersInDocument = Math.Max(1, entry.Length) });
            }
            catch { input.Dispose(); throw; }
        }

        private bool Read(XmlReader xml)
        {
            ct.ThrowIfCancellationRequested();
            if (output.BaseStream.Position >= MaxOutputBytes) throw new ExtractionLimit();
            if (!xml.Read()) return false;
            if (xml.Depth > 128) throw new ExtractionLimit();
            return true;
        }

        private Dictionary<string, Relation> Relationships(string owner)
        {
            int slash = owner.LastIndexOf('/') + 1;
            string directory = owner[..slash];
            string path = directory + "_rels/" + owner[slash..] + ".rels";
            var result = new Dictionary<string, Relation>(StringComparer.Ordinal);
            if (!parts.ContainsKey(path)) return result;
            using var xml = OpenXml(path, metadata: true);
            while (Read(xml))
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "Relationship" ||
                    xml.GetAttribute("TargetMode") == "External") continue;
                var id = xml.GetAttribute("Id");
                var type = xml.GetAttribute("Type")?.Split('/')[^1];
                if (type is not ("officeDocument" or "footer" or "header" or "endnotes" or "footnotes" or
                    "worksheet" or "sharedStrings" or "slide" or "chartsheet" or "dialogsheet" or "macrosheet")) continue;
                var target = xml.GetAttribute("Target");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) throw new InvalidDataException();
                if (result.Count >= AttachmentZip.MaximumEntries) throw new ExtractionLimit();
                target = Uri.UnescapeDataString(target).Replace('\\', '/');
                if (target.StartsWith("//") || target.Contains(':')) throw new InvalidDataException();
                var segments = new List<string>();
                foreach (var element in (target.StartsWith('/') ? target : directory + target).Split('/'))
                {
                    if (element is "" or ".") continue;
                    if (element == "..")
                    {
                        if (segments.Count == 0) throw new InvalidDataException();
                        segments.RemoveAt(segments.Count - 1);
                    }
                    else segments.Add(element);
                }
                if (!result.TryAdd(id, new Relation(type, AttachmentZip.EntryPath(string.Join('/', segments)))))
                    throw new InvalidDataException();
            }
            return result;
        }

        private IEnumerable<(string Id, string? Name)> OrderedParts(string owner, string node)
        {
            using var xml = OpenXml(owner, metadata: true);
            int count = 0;
            while (Read(xml))
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != node) continue;
                if (++count > AttachmentZip.MaximumEntries) throw new ExtractionLimit();
                string? id = xml.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                    ?? xml.GetAttribute("id", "http://purl.oclc.org/ooxml/officeDocument/relationships");
                yield return (id ?? throw new InvalidDataException(), xml.GetAttribute("name"));
            }
        }

        private void RenderParagraphs(string path)
        {
            using var xml = OpenXml(path);
            while (Read(xml))
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "p") output.WriteLine();
                if (xml.NodeType != XmlNodeType.Element) continue;
                switch (xml.LocalName)
                {
                    case "t":
                        using (var element = xml.ReadSubtree())
                            CopyText(element, null);
                        break;
                    case "tab": output.Write('\t'); break;
                    case "br" or "cr": output.WriteLine(); break;
                    case "cSld" when xml.GetAttribute("name") is { Length: > 0 } name:
                        output.WriteLine(name);
                        break;
                }
            }
        }

        // ReadValueChunk avoids allocating an entire text node from a large paragraph.
        private void CopyText(XmlReader xml, StringBuilder? cell)
        {
            while (Read(xml))
            {
                if (xml.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)) continue;
                int n;
                while ((n = xml.ReadValueChunk(chars, 0, chars.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (cell is null)
                    {
                        if (output.BaseStream.Position >= MaxOutputBytes) throw new ExtractionLimit();
                        output.Write(chars, 0, n);
                    }
                    else
                    {
                        if (cell.Length + n > MaxCellChars) throw new ExtractionLimit();
                        cell.Append(chars, 0, n);
                    }
                }
            }
        }

        private void RenderWorkbook(string book)
        {
            var links = Relationships(book);
            using var strings = new SharedStrings();
            var shared = links.Values.FirstOrDefault(r => r.Type == "sharedStrings");
            if (shared is not null)
            {
                using var xml = OpenXml(shared.Path);
                while (Read(xml))
                {
                    if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "si") continue;
                    using var element = xml.ReadSubtree();
                    strings.Add(CellValue(element).Value);
                }
            }
            output.WriteLine("Planilhas em ordem do arquivo. Números e datas conservam o valor armazenado. Fórmulas usam o resultado salvo quando disponível.");
            foreach (var (id, name) in OrderedParts(book, "sheet"))
            {
                if (!links.TryGetValue(id, out var sheet)) throw new InvalidDataException();
                output.WriteLine($"\nPlanilha: {name}");
                if (sheet.Type != "worksheet")
                {
                    output.WriteLine("[Esta folha não contém uma tabela de células.]");
                    continue;
                }
                RenderSheet(sheet.Path, strings);
            }
        }

        private void RenderSheet(string path, SharedStrings strings)
        {
            using var xml = OpenXml(path);
            int row = 0, column = 0;
            while (Read(xml))
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "row") output.WriteLine();
                if (xml.NodeType != XmlNodeType.Element) continue;
                if (xml.LocalName == "row")
                {
                    row = int.TryParse(xml.GetAttribute("r"), out var n) ? n : row + 1;
                    column = 0;
                }
                if (xml.LocalName != "c") continue;
                var coordinate = xml.GetAttribute("r");
                var type = xml.GetAttribute("t");
                if (coordinate is null) coordinate = ColumnName(++column) + row.ToString(CultureInfo.InvariantCulture);
                else
                {
                    column = 0;
                    foreach (var letter in coordinate.TakeWhile(c => c is >= 'A' and <= 'Z'))
                    {
                        column = column * 26 + letter - 'A' + 1;
                        if (column > 16384) throw new InvalidDataException();
                    }
                }
                using var element = xml.ReadSubtree();
                var (value, formula) = CellValue(element);
                if (type == "s" && value.Length != 0)
                    value = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                        ? strings.Get(index) : throw new InvalidDataException();
                if (formula.Length == 0 && value.Length == 0) continue;
                output.Write($"{coordinate}=");
                if (formula.Length > 0) output.Write($"[{formula}] ");
                output.Write(value);
                output.Write('\t');
            }
        }

        private (string Value, string Formula) CellValue(XmlReader xml)
        {
            var value = new StringBuilder();
            var formula = new StringBuilder();
            // Phonetic runs assist pronunciation and aren't part of the displayed value.
            int phoneticDepth = -1;
            while (Read(xml))
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == phoneticDepth) phoneticDepth = -1;
                if (xml.NodeType != XmlNodeType.Element) continue;
                if (xml.LocalName == "rPh" && !xml.IsEmptyElement) phoneticDepth = xml.Depth;
                if (phoneticDepth >= 0 || xml.LocalName is not ("t" or "v" or "f")) continue;
                var target = xml.LocalName == "f" ? formula : value;
                using var element = xml.ReadSubtree();
                CopyText(element, target);
            }
            return (value.ToString(), formula.ToString());
        }

        private static string ColumnName(int column)
        {
            if (column is < 1 or > 16384) throw new InvalidDataException();
            Span<char> name = stackalloc char[3];
            int i = name.Length;
            while (column > 0)
            {
                column--;
                name[--i] = (char)('A' + column % 26);
                column /= 26;
            }
            return new string(name[i..]);
        }
    }

    // Keep both shared values and their offsets on disk. A large workbook can have millions of them.
    private sealed class SharedStrings : IDisposable
    {
        private readonly FileStream offsets = TemporaryFile();
        private readonly FileStream values = TemporaryFile();
        private readonly BinaryWriter indexWriter;
        private readonly BinaryWriter valueWriter;
        private readonly BinaryReader indexReader;
        private readonly BinaryReader valueReader;

        public SharedStrings()
        {
            indexWriter = new BinaryWriter(offsets, Utf8, true);
            valueWriter = new BinaryWriter(values, Utf8, true);
            indexReader = new BinaryReader(offsets, Utf8, true);
            valueReader = new BinaryReader(values, Utf8, true);
        }

        public void Add(string value)
        {
            indexWriter.Write(values.Position);
            valueWriter.Write(value);
        }

        public string Get(int id)
        {
            long offset = (long)id * sizeof(long);
            if (id < 0 || offset >= offsets.Length) throw new InvalidDataException();
            offsets.Position = offset;
            values.Position = indexReader.ReadInt64();
            return valueReader.ReadString();
        }

        public void Dispose()
        {
            indexWriter.Dispose();
            valueWriter.Dispose();
            indexReader.Dispose();
            valueReader.Dispose();
            offsets.Dispose();
            values.Dispose();
        }
    }
}
