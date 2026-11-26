using System.Buffers.Binary;

namespace Aegis.Infrastructure.Chat;

internal static class ResearchFaviconRaster
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal static string? ContentType(ReadOnlySpan<byte> data)
    {
        if (IsPng(data)) return "image/png";
        if (IsIco(data)) return "image/x-icon";
        if (IsWebp(data)) return "image/webp";
        if (data.Length >= 14 && (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) &&
            GoodSize(Little16(data[6..]), Little16(data[8..])) && data[^1] == 0x3b) return "image/gif";
        return IsJpeg(data) ? "image/jpeg" : null;
    }

    private static bool GoodSize(uint width, uint height) => width is > 1 and <= 512 && height is > 1 and <= 512;

    private static bool IsPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 45 || !data.StartsWith(PngSignature) || Big32(data[8..]) != 13 ||
            !data.Slice(12, 4).SequenceEqual("IHDR"u8) || !GoodSize(Big32(data[16..]), Big32(data[20..]))) return false;

        var hasData = false;
        for (var offset = 33; offset <= data.Length - 12;)
        {
            var length = Big32(data[offset..]);
            if (length > data.Length - offset - 12) return false;
            var type = data.Slice(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8) && length > 0) hasData = true;
            if (type.SequenceEqual("IEND"u8)) return hasData && length == 0 && offset == data.Length - 12;
            offset += 12 + (int)length;
        }
        return false;
    }

    private static bool IsIco(ReadOnlySpan<byte> data)
    {
        if (data.Length < 22 || Little16(data) != 0 || Little16(data[2..]) != 1) return false;
        var count = Little16(data[4..]);
        if (count is 0 or > 16 || data.Length < 6 + count * 16) return false;

        for (var i = 0; i < count; ++i)
        {
            var entry = data.Slice(6 + i * 16, 16);
            var width = entry[0] == 0 ? 256u : entry[0];
            var height = entry[1] == 0 ? 256u : entry[1];
            var length = Little32(entry[8..]);
            var offset = Little32(entry[12..]);
            if (!GoodSize(width, height) || offset < 6 + count * 16 || offset > data.Length ||
                length > data.Length - offset || length < 12) return false;
            var frame = data.Slice((int)offset, (int)length);
            if (frame.StartsWith(PngSignature))
            {
                if (!IsPng(frame) || Big32(frame[16..]) != width || Big32(frame[20..]) != height) return false;
            }
            else
            {
                var header = Little32(frame);
                if (header >= length) return false;
                if (header == 12)
                {
                    if (Little16(frame[4..]) != width || Little16(frame[6..]) != height * 2) return false;
                }
                else if (header < 40 || frame.Length < 16 ||
                    Little32(frame[4..]) != width || Little32(frame[8..]) != height * 2) return false;
            }
        }
        return true;
    }

    private static bool IsWebp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 25 || !data.StartsWith("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8) ||
            Little32(data[4..]) != data.Length - 8) return false;
        for (var offset = 12; offset <= data.Length - 8;)
        {
            var type = data.Slice(offset, 4);
            var length = Little32(data[(offset + 4)..]);
            if (length > data.Length - offset - 8) return false;
            var payload = data.Slice(offset + 8, (int)length);
            if (type.SequenceEqual("VP8 "u8))
                return payload.Length >= 10 && payload.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 1, 0x2a }) &&
                    GoodSize((uint)(Little16(payload[6..]) & 0x3fff), (uint)(Little16(payload[8..]) & 0x3fff));
            if (type.SequenceEqual("VP8L"u8))
            {
                if (payload.Length < 5 || payload[0] != 0x2f) return false;
                var sizes = Little32(payload[1..]);
                return GoodSize((sizes & 0x3fff) + 1, ((sizes >> 14) & 0x3fff) + 1);
            }
            if (type.SequenceEqual("VP8X"u8) && (payload.Length < 10 || (payload[0] & 2) != 0 ||
                !GoodSize(OnePlusLittle24(payload[4..]), OnePlusLittle24(payload[7..])))) return false;
            offset += 8 + (int)length + (int)(length & 1);
        }
        return false;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || data[0] != 0xff || data[1] != 0xd8 ||
            data[^2] != 0xff || data[^1] != 0xd9) return false;
        for (var offset = 2; offset <= data.Length - 4;)
        {
            if (data[offset++] != 0xff) return false;
            while (offset < data.Length - 2 && data[offset] == 0xff) offset++;
            var marker = data[offset++];
            if (offset > data.Length - 2) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (length < 2 || length > data.Length - offset) return false;
            if (marker is 0xc0 or 0xc1 or 0xc2)
                return length >= 8 && GoodSize(BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]));
            if (marker is 0xda or 0xd9) return false;
            offset += length;
        }
        return false;
    }

    private static ushort Little16(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16LittleEndian(value);
    private static uint Little32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32LittleEndian(value);
    private static uint Big32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);
    private static uint OnePlusLittle24(ReadOnlySpan<byte> value) => 1u + value[0] + ((uint)value[1] << 8) + ((uint)value[2] << 16);
}
