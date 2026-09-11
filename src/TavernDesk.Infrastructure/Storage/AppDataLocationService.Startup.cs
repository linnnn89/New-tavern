using System.Text.Json;

namespace TavernDesk.Infrastructure.Storage;

public sealed partial class AppDataLocationService
{
    private const string PendingFileName = "pending-data-root.json";
    private const string MigrationMarkerName = ".taverndesk-migration";
    private sealed record PendingChange(string Id, string Source, string Target, DataRootMigrationMode Mode);

    public string? PendingRoot => ReadPending(_configuration)?.Target;

    public async Task ScheduleRootChangeAsync(
        string requestedRoot,
        DataRootMigrationMode mode,
        CancellationToken cancellationToken = default)
    {
        var (source, target) = ValidateRootChange(requestedRoot);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var pendingPath = Path.Combine(_configuration.ConfigurationDirectory, PendingFileName);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(pendingPath);
            return;
        }
        try
        {
            var existing = ReadPending(_configuration);
            if (existing is not null && existing.Mode == mode
                && string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Target, target, StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch (JsonException) { /* A new valid request can replace a corrupt one. */ }
        catch (InvalidDataException) { }
        if (File.Exists(target) || (mode == DataRootMigrationMode.CopyCurrentData
            && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            throw new IOException($"目标个人资料目录非空，无法安排迁移：{target}");

        var request = new PendingChange(Guid.NewGuid().ToString("N"), source, target, mode);
        Directory.CreateDirectory(_configuration.ConfigurationDirectory);
        var temporary = pendingPath + "." + request.Id + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(request), cancellationToken);
            File.Move(temporary, pendingPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>
    /// Run after acquiring the application instance gate and before constructing
    /// business services. A pending request never switches the running process.
    /// </summary>
    public static async Task<DataRootChangeResult?> ApplyPendingChangeAsync(
        AppDataConfiguration configuration,
        string? explicitRoot = null,
        CancellationToken cancellationToken = default)
    {
        var paths = new AppDataPaths(explicitRoot, configuration);
        if (paths.IsExternalOverride) return null;
        var pending = ReadPending(configuration);
        if (pending is null) return null;
        var pendingPath = Path.Combine(configuration.ConfigurationDirectory, PendingFileName);
        if (string.Equals(paths.RootDirectory, pending.Target, StringComparison.OrdinalIgnoreCase))
        {
            // Configuration was committed before a previous process exited.
            TryFinishPending(pendingPath, pending.Target, pending.Id);
            return null;
        }
        if (!string.Equals(paths.RootDirectory, pending.Source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("待迁移请求的来源目录与当前配置不一致，请重新安排迁移。");

        var service = new AppDataLocationService(configuration, paths, new SqliteDatabase(paths));
        service.ValidateRootChange(pending.Target);
        if (pending.Mode == DataRootMigrationMode.CopyCurrentData && Directory.Exists(pending.Target))
        {
            var marker = Path.Combine(pending.Target, MigrationMarkerName);
            if (File.Exists(marker) && new FileInfo(marker).LinkTarget is null
                && new FileInfo(marker).Length == pending.Id.Length
                && await File.ReadAllTextAsync(marker, cancellationToken) == pending.Id)
            {
                // The old root may have received newer writes after the failure.
                // Preserve the incomplete copy and take a fresh snapshot; never
                // delete files someone may have added to that target meanwhile.
                var target = new DirectoryInfo(pending.Target);
                var preserved = Path.Combine(target.Parent!.FullName,
                    $".{target.Name}.incomplete-{Guid.NewGuid():N}");
                Directory.Move(pending.Target, preserved);
            }
        }
        var result = await service.ChangeRootCoreAsync(
            pending.Target, pending.Mode, pending.Id, cancellationToken);
        TryFinishPending(pendingPath, pending.Target, pending.Id);
        return result;
    }

    private static PendingChange? ReadPending(AppDataConfiguration configuration)
    {
        var path = Path.Combine(configuration.ConfigurationDirectory, PendingFileName);
        if (!File.Exists(path)) return null;
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Length > 16 * 1024)
            throw new InvalidDataException("待迁移请求文件无效，请重新安排迁移。");
        var pending = JsonSerializer.Deserialize<PendingChange>(File.ReadAllText(path));
        if (pending is null || !Guid.TryParseExact(pending.Id, "N", out _)
            || !Path.IsPathFullyQualified(pending.Source) || !Path.IsPathFullyQualified(pending.Target)
            || !Enum.IsDefined(pending.Mode))
            throw new InvalidDataException("待迁移请求内容无效，请重新安排迁移。");
        return pending;
    }

    private static void TryFinishPending(string pendingPath, string target, string id)
    {
        // Cleanup cannot turn a committed switch into a reported failure.
        try
        {
            var marker = Path.Combine(target, MigrationMarkerName);
            if (File.Exists(marker) && new FileInfo(marker).LinkTarget is null
                && new FileInfo(marker).Length == id.Length && File.ReadAllText(marker) == id)
                File.Delete(marker);
            File.Delete(pendingPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
