using Microsoft.Data.Sqlite;

namespace TavernDesk.Infrastructure.Storage;

public enum DataRootMigrationMode
{
    KeepTargetAsIs,
    CopyCurrentData
}

public sealed record DataRootChangeResult(
    string PreviousRoot,
    string NewRoot,
    bool Migrated,
    int CopiedFiles,
    long CopiedBytes);

/// <summary>
/// Owns the user-selectable personal-data root and the one-time compatibility
/// repair for paths written by older TavernDesk builds.
/// </summary>
public sealed class AppDataLocationService
{
    private readonly AppDataConfiguration _configuration;
    private readonly SqliteDatabase _database;
    private readonly AppDataPaths _paths;
    private readonly Func<string, string, CancellationToken, Task> _copyFile;

    public AppDataLocationService(
        AppDataConfiguration configuration,
        AppDataPaths paths,
        SqliteDatabase database,
        Func<string, string, CancellationToken, Task>? copyFile = null)
    {
        _configuration = configuration;
        _paths = paths;
        _database = database;
        _copyFile = copyFile ?? CopyFileAsync;
    }

    public string CurrentRoot => _paths.RootDirectory;

    public string DefaultRoot => _configuration.DefaultDataRoot;

    public string ConfigurationPath => _configuration.ConfigurationPath;

    public bool IsExternallyOverridden => _paths.IsExternalOverride;

    public async Task<DataRootChangeResult> ChangeRootAsync(
        string requestedRoot,
        DataRootMigrationMode migrationMode,
        CancellationToken cancellationToken = default)
    {
        if (IsExternallyOverridden)
        {
            throw new InvalidOperationException(
                "当前数据根由 --data-root 或 TAVERNDESK_DATA_ROOT 指定，"
                + "请先移除覆盖后再从设置页修改。");
        }

        var newRoot = Path.GetFullPath(requestedRoot);
        var oldRoot = Path.GetFullPath(CurrentRoot);
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new DataRootChangeResult(oldRoot, newRoot, false, 0, 0);
        }

        if (IsNestedRoot(oldRoot, newRoot) || IsNestedRoot(newRoot, oldRoot))
        {
            throw new InvalidOperationException(
                "新的个人资料目录不能位于当前个人资料目录内部，也不能反过来包含当前目录。"
                + "请选择同级或其他位置的目录。");
        }

        var copiedFiles = 0;
        long copiedBytes = 0;
        if (migrationMode == DataRootMigrationMode.CopyCurrentData)
        {
            (copiedFiles, copiedBytes) =
                await CopyCurrentDataAsync(oldRoot, newRoot, cancellationToken);
        }
        else
        {
            Directory.CreateDirectory(newRoot);
        }

        await _configuration.SaveDataRootAsync(newRoot, cancellationToken);
        return new DataRootChangeResult(
            oldRoot,
            newRoot,
            migrationMode == DataRootMigrationMode.CopyCurrentData,
            copiedFiles,
            copiedBytes);
    }

    /// <summary>
    /// Rebases known managed file paths and stores them relative to the active
    /// data root. This repairs older databases without changing card content.
    /// </summary>
    public async Task<int> RepairDatabasePathsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        try
        {
            var changed = 0;
            changed += await RepairCharactersAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken);
            changed += await RepairCampaignScenariosAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken);
            changed += await RepairWorldbooksAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<(int Files, long Bytes)> CopyCurrentDataAsync(
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(
                $"当前个人资料目录不存在：{sourceRoot}");
        }

        if (Directory.Exists(targetRoot))
        {
            if (Directory.EnumerateFileSystemEntries(targetRoot).Any())
            {
                throw new IOException(
                    $"目标个人资料目录非空，为避免覆盖已有资料而停止迁移：{targetRoot}");
            }
        }
        var targetInfo = new DirectoryInfo(targetRoot);
        var targetParent = targetInfo.Parent?.FullName
                           ?? throw new InvalidOperationException(
                               "目标个人资料目录缺少父目录。");
        Directory.CreateDirectory(targetParent);
        var stagingRoot = Path.Combine(
            targetParent,
            $".{targetInfo.Name}.migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        var files = 0;
        long bytes = 0;
        var sourceDatabase = Path.Combine(sourceRoot, "taverndesk.db");
        var targetDatabase = Path.Combine(stagingRoot, "taverndesk.db");
        try
        {
            if (File.Exists(sourceDatabase))
            {
                await BackupDatabaseAsync(
                    sourceDatabase,
                    targetDatabase,
                    cancellationToken);
                files++;
                bytes += new FileInfo(targetDatabase).Length;
            }

            foreach (var directory in Directory.EnumerateDirectories(
                         sourceRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceRoot, directory);
                Directory.CreateDirectory(Path.Combine(stagingRoot, relative));
            }

            foreach (var sourceFile in Directory.EnumerateFiles(
                         sourceRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(
                        Path.GetFullPath(sourceFile),
                        Path.GetFullPath(sourceDatabase),
                        StringComparison.OrdinalIgnoreCase)
                    || IsSqliteSidecar(sourceFile))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(sourceRoot, sourceFile);
                var targetFile = Path.Combine(stagingRoot, relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(targetFile)
                    ?? throw new InvalidOperationException(
                        "个人资料文件缺少目标父目录。"));
                await _copyFile(sourceFile, targetFile, cancellationToken);
                files++;
                bytes += new FileInfo(targetFile).Length;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(targetRoot))
            {
                if (Directory.EnumerateFileSystemEntries(targetRoot).Any())
                {
                    throw new IOException(
                        $"目标个人资料目录在迁移期间变为非空，已停止切换：{targetRoot}");
                }

                Directory.Delete(targetRoot);
            }

            Directory.Move(stagingRoot, targetRoot);
            return (files, bytes);
        }
        catch
        {
            TryDeleteStagingDirectory(stagingRoot);
            throw;
        }
    }

    private static void TryDeleteStagingDirectory(string stagingRoot)
    {
        try
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
        catch
        {
            // Preserve the original migration error. The staging path uses a
            // unique internal name and is never accepted as an active root.
        }
    }

    private async Task<int> RepairCharactersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Avatar, string Source)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, avatar_path, source_card_path
                FROM characters;
                """;
            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var changed = 0;
        foreach (var row in rows)
        {
            var avatar = _paths.ToManagedStoredPath(
                row.Avatar,
                AppDataPaths.CharacterCardsDirectoryName,
                row.Id);
            var source = _paths.ToManagedStoredPath(
                row.Source,
                AppDataPaths.CharacterCardsDirectoryName,
                row.Id);
            if (string.Equals(avatar, row.Avatar, StringComparison.Ordinal)
                && string.Equals(source, row.Source, StringComparison.Ordinal))
            {
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE characters
                SET avatar_path = $avatarPath,
                    source_card_path = $sourceCardPath
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$avatarPath", avatar);
            update.Parameters.AddWithValue("$sourceCardPath", source);
            update.Parameters.AddWithValue("$id", row.Id);
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return changed;
    }

    private async Task<int> RepairCampaignScenariosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Cover)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, cover_path
                FROM campaign_scenarios;
                """;
            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var changed = 0;
        foreach (var row in rows)
        {
            var cover = _paths.ToManagedStoredPath(
                row.Cover,
                AppDataPaths.CampaignScenarioCardsDirectoryName,
                row.Id);
            if (string.Equals(cover, row.Cover, StringComparison.Ordinal))
            {
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE campaign_scenarios
                SET cover_path = $coverPath
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$coverPath", cover);
            update.Parameters.AddWithValue("$id", row.Id);
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return changed;
    }

    private async Task<int> RepairWorldbooksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Source)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, source_path
                FROM worldbooks;
                """;
            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var changed = 0;
        foreach (var row in rows)
        {
            var source = _paths.ToStoredPath(row.Source);
            if (string.Equals(source, row.Source, StringComparison.Ordinal))
            {
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE worldbooks
                SET source_path = $sourcePath
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$sourcePath", source);
            update.Parameters.AddWithValue("$id", row.Id);
            changed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return changed;
    }

    private static async Task BackupDatabaseAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(
            Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(
                "数据库备份缺少目标父目录。"));
        var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString());
        var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = targetPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        }.ToString());
        await using (source)
        await using (target)
        {
            await source.OpenAsync(cancellationToken);
            await target.OpenAsync(cancellationToken);
            source.BackupDatabase(target);
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            useAsync: true);
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static bool IsSqliteSidecar(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("taverndesk.db-wal", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("taverndesk.db-shm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNestedRoot(string possibleChild, string possibleParent)
    {
        var child = Path.GetFullPath(possibleChild)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(possibleParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)
               && child.StartsWith(
                   parent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}
