using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TavernDesk.Infrastructure.Compatibility;

internal sealed record PngTextEntry(string Keyword, string Text);

internal sealed class PngCardContainer
{
    private static readonly byte[] Signature =
        [137, 80, 78, 71, 13, 10, 26, 10];
    private const int MaximumChunkBytes = 128 * 1024 * 1024;
    private const int MaximumDecodedTextBytes = 32 * 1024 * 1024;

    private readonly IReadOnlyList<PngChunk> _chunks;
    private readonly byte[] _trailingBytes;

    private PngCardContainer(
        IReadOnlyList<PngChunk> chunks,
        byte[] trailingBytes)
    {
        _chunks = chunks;
        _trailingBytes = trailingBytes;
    }

    public static PngCardContainer Parse(byte[] bytes)
    {
        if (bytes.Length < Signature.Length
            || !bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("文件不是有效 PNG/APNG。");
        }

        var chunks = new List<PngChunk>();
        var offset = Signature.Length;
        var foundEnd = false;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
            {
                break;
            }

            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(offset, 4)));
            if (length < 0 || length > MaximumChunkBytes)
            {
                throw new InvalidDataException("PNG chunk 超过 128 MiB 安全上限。");
            }

            var totalLength = checked(length + 12);
            if (offset + totalLength > bytes.Length)
            {
                throw new InvalidDataException("PNG chunk 长度越过文件末尾。");
            }

            var typeBytes = bytes.AsSpan(offset + 4, 4);
            var hasInvalidTypeByte = false;
            foreach (var value in typeBytes)
            {
                if (value is not (>= (byte)'A' and <= (byte)'Z')
                    and not (>= (byte)'a' and <= (byte)'z'))
                {
                    hasInvalidTypeByte = true;
                    break;
                }
            }

            if (hasInvalidTypeByte)
            {
                throw new InvalidDataException("PNG chunk 类型无效。");
            }

            var type = Encoding.ASCII.GetString(typeBytes);
            var data = bytes.AsSpan(offset + 8, length).ToArray();
            // Validate every chunk before retaining it. Rewrite preserves unknown
            // ancillary/APNG chunks, so carrying a corrupt chunk forward would
            // produce an apparently successful but invalid exported card.
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(offset + 8 + length, 4));
            var actualCrc = PngCrc32.Compute(typeBytes, data);
            if (actualCrc != expectedCrc)
            {
                throw new InvalidDataException($"PNG chunk {type} 的 CRC 校验失败。");
            }

            chunks.Add(new PngChunk(type, data));
            offset += totalLength;
            if (type == "IEND")
            {
                foundEnd = true;
                break;
            }
        }

        if (!foundEnd)
        {
            throw new InvalidDataException("PNG 缺少 IEND chunk。");
        }

        return new PngCardContainer(chunks, bytes[offset..]);
    }

    public IReadOnlyList<PngTextEntry> ReadTextEntries()
    {
        var result = new List<PngTextEntry>();
        foreach (var chunk in _chunks)
        {
            PngTextEntry? entry = chunk.Type switch
            {
                "tEXt" => ReadText(chunk.Data),
                "zTXt" => ReadCompressedText(chunk.Data),
                "iTXt" => ReadInternationalText(chunk.Data),
                _ => null
            };
            if (entry is not null)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    public byte[] RewriteCharacterCard(
        string ccv3Base64,
        string charaBase64)
    {
        // Replace only Tavern card metadata. Image data, animation chunks,
        // unknown ancillary chunks, their order, and legal trailing bytes remain
        // byte-for-byte represented in the rewritten container.
        using var output = new MemoryStream();
        output.Write(Signature);
        foreach (var chunk in _chunks)
        {
            if (chunk.Type == "IEND")
            {
                WriteChunk(output, CreateTextChunk("ccv3", ccv3Base64));
                WriteChunk(output, CreateTextChunk("chara", charaBase64));
                WriteChunk(output, chunk);
                continue;
            }

            if (IsCharacterCardTextChunk(chunk))
            {
                continue;
            }

            WriteChunk(output, chunk);
        }

        output.Write(_trailingBytes);
        return output.ToArray();
    }

    private static bool IsCharacterCardTextChunk(PngChunk chunk)
    {
        if (chunk.Type is not ("tEXt" or "zTXt" or "iTXt"))
        {
            return false;
        }

        var zero = Array.IndexOf(chunk.Data, (byte)0);
        if (zero <= 0)
        {
            return false;
        }

        var keyword = Encoding.Latin1.GetString(chunk.Data, 0, zero);
        return keyword is "ccv3" or "chara";
    }

    private static PngTextEntry? ReadText(byte[] data)
    {
        var zero = Array.IndexOf(data, (byte)0);
        return zero is <= 0 or > 79
            ? null
            : new PngTextEntry(
                Encoding.Latin1.GetString(data, 0, zero),
                Encoding.Latin1.GetString(data, zero + 1, data.Length - zero - 1));
    }

    private static PngTextEntry? ReadCompressedText(byte[] data)
    {
        var zero = Array.IndexOf(data, (byte)0);
        if (zero is <= 0 or > 79
            || zero + 2 > data.Length
            || data[zero + 1] != 0)
        {
            return null;
        }

        var text = Decompress(
            data.AsSpan(zero + 2).ToArray(),
            Encoding.Latin1);
        return new PngTextEntry(
            Encoding.Latin1.GetString(data, 0, zero),
            text);
    }

    private static PngTextEntry? ReadInternationalText(byte[] data)
    {
        var keywordEnd = Array.IndexOf(data, (byte)0);
        if (keywordEnd is <= 0 or > 79 || keywordEnd + 3 > data.Length)
        {
            return null;
        }

        var compressed = data[keywordEnd + 1] == 1;
        if (data[keywordEnd + 1] is not (0 or 1)
            || data[keywordEnd + 2] != 0)
        {
            return null;
        }

        var languageEnd = Array.IndexOf(data, (byte)0, keywordEnd + 3);
        if (languageEnd < 0)
        {
            return null;
        }

        var translatedEnd = Array.IndexOf(data, (byte)0, languageEnd + 1);
        if (translatedEnd < 0)
        {
            return null;
        }

        var textBytes = data.AsSpan(translatedEnd + 1).ToArray();
        var text = compressed
            ? Decompress(textBytes, Encoding.UTF8)
            : Encoding.UTF8.GetString(textBytes);
        return new PngTextEntry(
            Encoding.Latin1.GetString(data, 0, keywordEnd),
            text);
    }

    private static string Decompress(byte[] compressed, Encoding encoding)
    {
        // Compressed PNG text is attacker-controlled import data; bound the
        // expanded size rather than trusting the small compressed input size.
        using var source = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = zlib.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            if (output.Length > MaximumDecodedTextBytes)
            {
                throw new InvalidDataException("PNG 文本块解压后超过 32 MiB 安全上限。");
            }
        }

        return encoding.GetString(output.ToArray());
    }

    private static PngChunk CreateTextChunk(string keyword, string text)
    {
        var keywordBytes = Encoding.Latin1.GetBytes(keyword);
        var textBytes = Encoding.ASCII.GetBytes(text);
        var data = new byte[keywordBytes.Length + 1 + textBytes.Length];
        keywordBytes.CopyTo(data, 0);
        textBytes.CopyTo(data, keywordBytes.Length + 1);
        return new PngChunk("tEXt", data);
    }

    private static void WriteChunk(Stream output, PngChunk chunk)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)chunk.Data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(chunk.Type);
        output.Write(typeBytes);
        output.Write(chunk.Data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            crc,
            PngCrc32.Compute(typeBytes, chunk.Data));
        output.Write(crc);
    }

    private sealed record PngChunk(string Type, byte[] Data);

    private static class PngCrc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xffffffffu;
            foreach (var value in type)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            return crc ^ 0xffffffffu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xedb88320u ^ (value >> 1)
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
