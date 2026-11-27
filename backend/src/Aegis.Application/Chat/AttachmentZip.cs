using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Aegis.Application.Chat;

internal sealed class AttachmentIndexLimitException : Exception;

internal static class AttachmentZip
{
    public const int MaximumEntries = 4096;
    private const int MaxDirectoryBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // ZipArchive materializes the central directory. Bound its allocation before asking for Entries.
    public static ZipArchive Open(Stream source, int remainingEntries)
    {
        int tailLength = (int)Math.Min(source.Length, ushort.MaxValue + 22L);
        byte[] tail = new byte[tailLength];
        source.Seek(-tailLength, SeekOrigin.End);
        source.ReadExactly(tail);
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            var end = tail.AsSpan(i);
            if (U32(end) != 0x06054b50 || U16(end[20..]) != end.Length - 22) continue;
            if (U32(end[4..]) != 0 || U16(end[8..]) != U16(end[10..])) throw new InvalidDataException();
            long entries = U16(end[10..]), length = U32(end[12..]), offset = U32(end[16..]);
            long footer = source.Length - tailLength + i;
            if (entries == ushort.MaxValue || length == uint.MaxValue || offset == uint.MaxValue)
            {
                if (footer < 20) throw new InvalidDataException();
                source.Position = footer - 20;
                byte[] locator = new byte[20];
                source.ReadExactly(locator);
                if (U32(locator) != 0x07064b50 || U32(locator.AsSpan(4)) != 0 || U32(locator.AsSpan(16)) != 1)
                    throw new InvalidDataException();
                long headerOffset = BinaryPrimitives.ReadInt64LittleEndian(locator.AsSpan(8));
                if (headerOffset < 0 || headerOffset > footer - 76) throw new InvalidDataException();
                source.Position = headerOffset;
                byte[] zip64 = new byte[56];
                source.ReadExactly(zip64);
                if (U32(zip64) != 0x06064b50 || U32(zip64.AsSpan(16)) != 0 || U32(zip64.AsSpan(20)) != 0)
                    throw new InvalidDataException();
                entries = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(32));
                if (entries != BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(24))) throw new InvalidDataException();
                length = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(40));
                offset = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(48));
                footer = headerOffset;
            }
            if (entries < 0 || entries > remainingEntries)
                throw new AttachmentIndexLimitException();
            if (length > MaxDirectoryBytes) throw new AttachmentIndexLimitException();
            if (length < 0 || offset < 0 || offset > footer - length)
                throw new InvalidDataException();
            source.Position = offset;
            var directory = new byte[(int)length];
            source.ReadExactly(directory);
            var encoding = (Encoding)Encoding.UTF8;
            var next = directory.AsSpan();
            for (int j = 0; j < entries; j++)
            {
                if (next.Length < 46 || U32(next) != 0x02014b50 || (U16(next[8..]) & 1) != 0)
                    throw new InvalidDataException();
                int names = U16(next[28..]);
                int total = 46 + names + U16(next[30..]) + U16(next[32..]);
                if (total > next.Length) throw new InvalidDataException();
                if ((U16(next[8..]) & 0x800) == 0 && encoding == Encoding.UTF8)
                    try { StrictUtf8.GetCharCount(next.Slice(46, names)); }
                    catch (DecoderFallbackException)
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        encoding = Encoding.GetEncoding(437);
                    }
                next = next[total..];
            }
            if (!next.IsEmpty) throw new InvalidDataException();
            source.Position = 0;
            return new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true, encoding);
        }
        throw new InvalidDataException();
    }

    public static string EntryPath(string name)
    {
        var path = name.Replace('\\', '/');
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length > 512 || path.Any(char.IsControl) || path.StartsWith('/') || path.Contains(':') || components.Contains(".."))
            throw new ArgumentException("O ZIP contém um caminho inválido.");
        return string.Join('/', components.Where(c => c != "."));
    }

    // Nested ZIPs need random access to their central directory. Only these files are spooled.
    public static async Task<FileStream> SpoolAsync(Stream input, long expectedBytes, CancellationToken ct)
    {
        if (expectedBytes is < 0 or > ChatUploadLimits.MaxFileBytes) throw new InvalidDataException();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var file = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), options);
        try
        {
            await input.CopyToAsync(file, 64 * 1024, ct);
            if (file.Length != expectedBytes) throw new InvalidDataException();
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private static ushort U16(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data);
    private static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);

    // A malicious entry may expand beyond its declared size. The final read checks both length and CRC.
    public sealed class EntryReader(ZipArchiveEntry entry) : Stream
    {
        private readonly Stream input = entry.Open();
        private long position;
        private uint crc = uint.MaxValue;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => entry.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            var slice = buffer[..(int)Math.Min(buffer.Length, Length - position + 1)];
            return Check(slice, input.Read(slice));
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return 0;
            var slice = buffer[..(int)Math.Min(buffer.Length, Length - position + 1)];
            int read = await input.ReadAsync(slice, ct);
            return Check(slice.Span, read);
        }
        private int Check(ReadOnlySpan<byte> data, int read)
        {
            position += read;
            if (position > Length || read == 0 && (position != Length || ~crc != entry.Crc32))
                throw new InvalidDataException();
            foreach (var b in data[..read]) crc = (crc >> 8) ^ CrcTable[(byte)(crc ^ b)];
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(index =>
    {
        var crc = (uint)index;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        return crc;
    }).ToArray();
}
