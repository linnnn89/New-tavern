using TavernDesk.Infrastructure.Diagnostics;

namespace TavernDesk.Infrastructure.Speech;

public sealed record SpeechCacheUsage(bool Available, int Files, long Bytes);

// Owned by the application session, never by an individual chat window.
public sealed class SpeechAudioCache
{
    public const long DefaultMaximumBytes = 120L * 1024 * 1024;
    private const string Marker = ".taverndesk-audio-cache";
    private const string MarkerContents = "TavernDesk.AudioCache.v1";
    private readonly ITavernDeskDiagnostics _diagnostics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _available;
    public string DirectoryPath { get; }
    public long MaximumBytes { get; }

    public SpeechAudioCache(string directoryPath, ITavernDeskDiagnostics diagnostics, long maximumBytes = DefaultMaximumBytes)
    {
        if (!Path.IsPathFullyQualified(directoryPath)) throw new ArgumentException("Cache path must be absolute.");
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        DirectoryPath = Path.GetFullPath(directoryPath);
        MaximumBytes = maximumBytes;
        _diagnostics = diagnostics;
    }

    public void Initialize()
    {
        try
        {
            ValidateDirectory();
            var marker = Path.Combine(DirectoryPath, Marker);
            if (Directory.Exists(DirectoryPath) && Directory.EnumerateFileSystemEntries(DirectoryPath).Any()
                && (!File.Exists(marker) || File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint)
                    || File.ReadAllText(marker) != MarkerContents))
                throw new IOException("Audio cache ownership could not be verified.");
            Directory.CreateDirectory(DirectoryPath);
            // Never recurse into directories or delete files not created by this cache.
            foreach (var file in OwnedFiles()) file.Delete();
            File.WriteAllText(marker, MarkerContents);
            _available = true;
        }
        catch (Exception error) { Disable(error); }
    }

    private void ValidateDirectory()
    {
        for (var parent = new DirectoryInfo(DirectoryPath); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Audio cache cannot use a linked directory.");
    }

    private FileInfo[] OwnedFiles()
    {
        ValidateDirectory();
        var entries = new DirectoryInfo(DirectoryPath).GetFileSystemInfos();
        if (entries.Any(entry => entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new IOException("Audio cache contains a link.");
        if (entries.Any(entry => entry.Name != Marker &&
            (entry is not FileInfo || !IsCacheName(entry.Name))))
            throw new IOException("Audio cache contains unrecognized files; caching is disabled to protect them.");
        return entries.OfType<FileInfo>().Where(file => IsCacheName(file.Name)).ToArray();
    }

    private static bool IsKey(string key) => key.Length == 64 && key.All(char.IsAsciiHexDigit);
    private static bool IsCacheName(string name) =>
        (name.EndsWith(".pcm", StringComparison.Ordinal) || name.EndsWith(".part", StringComparison.Ordinal))
        && IsKey(Path.GetFileNameWithoutExtension(name));

    public async Task<SpeechCacheUsage> GetUsageAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_available) return new(false, 0, 0);
            var files = OwnedFiles();
            return new(true, files.Length, files.Sum(file => file.Length));
        }
        catch (Exception error) { Disable(error); return new(false, 0, 0); }
        finally { _gate.Release(); }
    }

    public async Task<byte[]?> TryReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!IsKey(key)) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_available) return null;
            var file = OwnedFiles().FirstOrDefault(file => file.Name == key + ".pcm");
            if (file is null || file.Length == 0 || file.Length > Math.Min(MaximumBytes, 32L * 1024 * 1024)
                || file.Length % 2 != 0) return null;
            return await File.ReadAllBytesAsync(file.FullName, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { Disable(error); return null; }
        finally { _gate.Release(); }
    }

    public async Task StoreAsync(string key, byte[] audio, CancellationToken cancellationToken)
    {
        if (!IsKey(key) || audio.Length == 0 || audio.Length % 2 != 0
            || audio.LongLength > Math.Min(MaximumBytes, 32L * 1024 * 1024)) return;
        await _gate.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            if (!_available) return;
            var files = OwnedFiles();
            if (files.Any(file => file.Name == key + ".pcm")) return;
            var size = files.Sum(file => file.Length);
            // Reserve room before writing, so even the temporary file stays within budget.
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name))
            {
                if (size + audio.LongLength <= MaximumBytes) break;
                var length = file.Length;
                file.Delete(); size -= length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            temporary = Path.Combine(DirectoryPath, key + ".part");
            await File.WriteAllBytesAsync(temporary, audio, cancellationToken);
            File.Move(temporary, Path.Combine(DirectoryPath, key + ".pcm"));
            temporary = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { Disable(error); }
        finally
        {
            if (temporary is not null)
            {
                try { ValidateDirectory(); if (File.Exists(temporary) && !File.GetAttributes(temporary).HasFlag(FileAttributes.ReparsePoint)) File.Delete(temporary); }
                catch (Exception error) { Disable(error); }
            }
            _gate.Release();
        }
    }

    private void Disable(Exception error)
    {
        _available = false;
        _diagnostics.LogError("speech.cache", error);
    }
}
