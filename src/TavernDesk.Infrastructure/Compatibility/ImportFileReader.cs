using System.Security.Cryptography;
using System.Text;

namespace TavernDesk.Infrastructure.Compatibility;

internal static class ImportFileReader
{
    internal static long MaximumBytesFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".json" => 16L * 1024 * 1024,
        ".png" or ".charx" => 256L * 1024 * 1024,
        _ => throw new NotSupportedException($"不支持此导入格式：{extension}")
    };

    internal static async Task<byte[]> ReadAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        await using var source = Open(path, maximumBytes);
        using var buffer = new MemoryStream();
        await CopyAsync(source, buffer, maximumBytes, cancellationToken);
        return buffer.ToArray();
    }

    internal static async Task<string> ReadTextAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(await ReadAsync(path, maximumBytes, cancellationToken));
        // Preserve the JSON codec's previous BOM detection, including UTF-16 cards.
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    internal static async Task<string> HashAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        await using var source = Open(path, maximumBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            Check(total, maximumBytes);
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static FileStream Open(string path, long maximumBytes)
    {
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { Check(source.Length, maximumBytes); return source; }
        catch { source.Dispose(); throw; }
    }

    internal static async Task CopyAsync(Stream source, Stream destination, long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            Check(total, maximumBytes);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void Check(long bytes, long maximumBytes)
    {
        if (bytes > maximumBytes)
            throw new InvalidDataException($"导入内容超过 {maximumBytes / (1024 * 1024)} MiB 安全上限。");
    }
}
