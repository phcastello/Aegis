using System.IO.Compression;
using System.Text;
using Aegis.Application.Chat;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class LargeOfficeTextTests
{
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string BaseType = "application/vnd.openxmlformats-officedocument.";

    [Fact]
    public async Task WorkbookResolvesSharedStringsCoordinatesAndCachedFormulaAtEndOfLargeSheet()
    {
        using var input = new MemoryStream();
        using (var archive = new ZipArchive(input, ZipArchiveMode.Create, true))
        {
            Part(archive, "_rels/.rels", $"<Relationships><Relationship Id='main' Type='{Rel}/officeDocument' Target='suite/book.xml'/></Relationships>");
            Part(archive, "suite/book.xml", $"<workbook xmlns:r='{Rel}'><sheets><sheet name='Vendas' r:id='data'/></sheets></workbook>");
            Part(archive, "suite/_rels/book.xml.rels", $"<Relationships><Relationship Id='data' Type='{Rel}/worksheet' Target='worksheets/two.xml'/><Relationship Id='words' Type='{Rel}/sharedStrings' Target='../words.xml'/></Relationships>");
            Part(archive, "words.xml", "<sst><si><r><t>Destino </t></r><rPh><t>fonética</t></rPh><r><t>líquido</t></r></si><si><t>Saldo</t></si></sst>");
            using var writer = Writer(archive, "suite/worksheets/two.xml");
            writer.Write("<worksheet><sheetData><row r='1'><c r='A1' t='s'><v>1</v></c><c t='s'><v>0</v></c></row>");
            for (int row = 2; row < 100_000; row++)
                writer.Write($"<row r='{row}'><c r='A{row}'><v>{row}</v></c></row>");
            writer.Write("<row r='100000'><c r='C100000' t='inlineStr'><is><t>último marcador</t></is></c><c><f>2*4</f><v>8</v></c></row></sheetData></worksheet>");
        }

        await using var text = await LargeOfficeText.OpenAsync(input, BaseType + "spreadsheetml.sheet", default);
        using var reader = new StreamReader(text);
        var extracted = await reader.ReadToEndAsync();
        Assert.True(text.CanSeek);
        Assert.DoesNotContain("parcial", extracted);
        Assert.Contains("Planilha: Vendas", extracted);
        Assert.Contains("A1=Saldo\tB1=Destino líquido", extracted);
        Assert.DoesNotContain("fonética", extracted);
        Assert.Contains("C100000=último marcador\tD100000=[2*4] 8", extracted);
    }

    [Fact]
    public async Task WordReadsParagraphsAndLinkedFooterFromSequentialSource()
    {
        using var input = new MemoryStream();
        using (var archive = new ZipArchive(input, ZipArchiveMode.Create, true))
        {
            Part(archive, "word/document.xml", "<w:document xmlns:w='urn:word'><w:body><w:p><w:r><w:t>Primeiro</w:t><w:tab/><w:t>campo</w:t></w:r></w:p><w:p><w:r><w:t>Segundo.</w:t></w:r></w:p></w:body></w:document>");
            Part(archive, "word/_rels/document.xml.rels", $"<Relationships><Relationship Id='foot' Type='{Rel}/footer' Target='rodape.xml'/></Relationships>");
            Part(archive, "word/rodape.xml", "<ftr><p><t>Rodapé final</t></p></ftr>");
        }
        input.Position = 0;
        using var sequential = new Sequential(input);
        await using var text = await LargeOfficeText.OpenAsync(sequential, BaseType + "wordprocessingml.document", default);
        using var reader = new StreamReader(text);
        var extracted = await reader.ReadToEndAsync();
        Assert.Contains("Primeiro\tcampo" + Environment.NewLine + "Segundo.", extracted);
        Assert.Contains("Rodapé final", extracted);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task PresentationPreservesSlideOrderAndNames()
    {
        using var input = new MemoryStream();
        using (var archive = new ZipArchive(input, ZipArchiveMode.Create, true))
        {
            Part(archive, "ppt/presentation.xml", $"<presentation xmlns:r='{Rel}'><sldIdLst><sldId id='300' r:id='s2'/><sldId id='299' r:id='s1'/></sldIdLst></presentation>");
            Part(archive, "ppt/_rels/presentation.xml.rels", $"<Relationships><Relationship Id='s1' Type='{Rel}/slide' Target='slides/slide1.xml'/><Relationship Id='s2' Type='{Rel}/slide' Target='slides/slide2.xml'/></Relationships>");
            Part(archive, "ppt/slides/slide1.xml", "<sld><cSld name='Fechamento'><p><t>Obrigada</t></p></cSld></sld>");
            Part(archive, "ppt/slides/slide2.xml", "<sld><cSld name='Abertura'><p><t>Bem-vindo</t></p></cSld></sld>");
        }
        await using var text = await LargeOfficeText.OpenAsync(input, BaseType + "presentationml.presentation", default);
        using var reader = new StreamReader(text);
        var extracted = (await reader.ReadToEndAsync()).ReplaceLineEndings("\n");
        Assert.Contains("Slide 1\nAbertura\nBem-vindo", extracted);
        Assert.Contains("Slide 2\nFechamento\nObrigada", extracted);
    }

    [Fact]
    public async Task ForbiddenXmlIsReportedAsIncompleteAndCancellationIsPreserved()
    {
        using var input = new MemoryStream();
        using (var archive = new ZipArchive(input, ZipArchiveMode.Create, true))
            Part(archive, "word/document.xml", "<!DOCTYPE foo [<!ENTITY disallowed 'bad'>]><document><p><t>&disallowed;</t></p></document>");
        await using var text = await LargeOfficeText.OpenAsync(input, BaseType + "wordprocessingml.document", default);
        using var reader = new StreamReader(text);
        Assert.StartsWith("Leitura incompleta.", await reader.ReadLineAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LargeOfficeText.OpenAsync(input, BaseType + "wordprocessingml.document", new CancellationToken(true)));
    }

    private static void Part(ZipArchive zip, string path, string xml)
    {
        using var writer = Writer(zip, path);
        writer.Write(xml);
    }

    private static StreamWriter Writer(ZipArchive zip, string path) =>
        new(zip.CreateEntry(path, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));

    private sealed class Sequential(Stream source) : Stream
    {
        public override bool CanSeek => false;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => source.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
