namespace TavernDesk.Infrastructure.Storage;

public sealed class AppDataPaths
{
    public const string DataRootEnvironmentVariable = "TAVERNDESK_DATA_ROOT";
    public const string AttachmentsDirectoryName = "attachments";
    public const string CharacterCardsDirectoryName = "character-cards";
    public const string CampaignScenarioCardsDirectoryName =
        "campaign-scenario-cards";
    public const string SecretsDirectoryName = "secrets";
    public const string GrokCliRuntimeDirectoryName = "grok-cli-runtime";
    public const string ExportsDirectoryName = "exports";

    private static readonly string[] ManagedDirectoryNames =
    [
        AttachmentsDirectoryName,
        CharacterCardsDirectoryName,
        CampaignScenarioCardsDirectoryName,
        SecretsDirectoryName,
        GrokCliRuntimeDirectoryName,
        ExportsDirectoryName
    ];

    public AppDataPaths(
        string? explicitRoot = null,
        AppDataConfiguration? configuration = null)
    {
        configuration ??= new AppDataConfiguration();
        RootDirectory = configuration.ResolveRoot(
            explicitRoot,
            out var isExternalOverride);
        IsExternalOverride = isExternalOverride;
        ConfigurationPath = configuration.ConfigurationPath;

        DatabasePath = Path.Combine(RootDirectory, "taverndesk.db");
        AttachmentsDirectory = Path.Combine(
            RootDirectory,
            AttachmentsDirectoryName);
        CharacterCardsDirectory = Path.Combine(
            RootDirectory,
            CharacterCardsDirectoryName);
        CampaignScenarioCardsDirectory = Path.Combine(
            RootDirectory,
            CampaignScenarioCardsDirectoryName);
        SecretsDirectory = Path.Combine(RootDirectory, SecretsDirectoryName);
        GrokCliRuntimeDirectory = Path.Combine(
            RootDirectory,
            GrokCliRuntimeDirectoryName);
        ExportsDirectory = Path.Combine(RootDirectory, ExportsDirectoryName);
    }

    public string RootDirectory { get; }

    public string ConfigurationPath { get; }

    public bool IsExternalOverride { get; }

    public string DatabasePath { get; }

    public string AttachmentsDirectory { get; }

    public string CharacterCardsDirectory { get; }

    public string CampaignScenarioCardsDirectory { get; }

    public string SecretsDirectory { get; }

    public string GrokCliRuntimeDirectory { get; }

    public string ExportsDirectory { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(CharacterCardsDirectory);
        Directory.CreateDirectory(CampaignScenarioCardsDirectory);
        Directory.CreateDirectory(SecretsDirectory);
        Directory.CreateDirectory(GrokCliRuntimeDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }

    /// <summary>
    /// Converts a persisted path into an absolute path for the current data
    /// root. Legacy absolute paths are rebased when they contain one of the
    /// TavernDesk-managed directory segments.
    /// </summary>
    public string ResolveStoredPath(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return string.Empty;
        }

        if (TryResolveManagedPath(storedPath, out var managedPath))
        {
            return managedPath;
        }

        try
        {
            return Path.IsPathRooted(storedPath)
                ? Path.GetFullPath(storedPath)
                : Path.GetFullPath(Path.Combine(RootDirectory, storedPath));
        }
        catch (ArgumentException)
        {
            return storedPath;
        }
    }

    public string ResolveManagedPath(
        string? storedPath,
        string directoryName,
        string entityId)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return string.Empty;
        }

        var resolved = ResolveStoredPath(storedPath);
        if (IsWithinDirectory(resolved, Path.Combine(RootDirectory, directoryName)))
        {
            return resolved;
        }

        var fileName = Path.GetFileName(storedPath);
        if (string.IsNullOrWhiteSpace(fileName)
            || string.IsNullOrWhiteSpace(entityId)
            || !IsSafePathPart(entityId)
            || !IsSafePathPart(fileName))
        {
            return resolved;
        }

        var fallback = Path.GetFullPath(
            Path.Combine(RootDirectory, directoryName, entityId, fileName));
        return IsWithinDirectory(fallback, Path.Combine(RootDirectory, directoryName))
            ? fallback
            : resolved;
    }

    /// <summary>
    /// Persists data-root-owned paths relative to the current root. External
    /// user-selected paths remain absolute because they cannot be safely
    /// reconstructed after a machine change.
    /// </summary>
    public string ToStoredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var resolved = ResolveStoredPath(path);
        return IsWithinRoot(resolved)
            ? Path.GetRelativePath(RootDirectory, resolved)
            : path;
    }

    public string ToManagedStoredPath(
        string? path,
        string directoryName,
        string entityId)
    {
        var resolved = ResolveManagedPath(path, directoryName, entityId);
        return ToStoredPath(resolved);
    }

    public bool IsWithinRoot(string path) =>
        IsWithinDirectory(path, RootDirectory);

    private bool TryResolveManagedPath(
        string storedPath,
        out string resolvedPath)
    {
        foreach (var directoryName in ManagedDirectoryNames)
        {
            if (!TryGetManagedRelativePath(
                    storedPath,
                    directoryName,
                    out var relativePath))
            {
                continue;
            }

            var candidate = Path.GetFullPath(
                Path.Combine(RootDirectory, directoryName, relativePath));
            if (IsWithinDirectory(
                    candidate,
                    Path.Combine(RootDirectory, directoryName)))
            {
                resolvedPath = candidate;
                return true;
            }
        }

        resolvedPath = string.Empty;
        return false;
    }

    private static bool TryGetManagedRelativePath(
        string path,
        string directoryName,
        out string relativePath)
    {
        var normalized = path.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        var marker = directoryName.Trim(
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        var start = 0;
        while (start < normalized.Length)
        {
            var index = normalized.IndexOf(
                marker,
                start,
                StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            var isSegmentStart = index == 0
                                 || normalized[index - 1]
                                    == Path.DirectorySeparatorChar;
            if (isSegmentStart)
            {
                var candidate = normalized[(index + marker.Length)..];
                if (!string.IsNullOrWhiteSpace(candidate)
                    && !candidate.Split(
                            Path.DirectorySeparatorChar,
                            StringSplitOptions.RemoveEmptyEntries)
                        .Any(part => part is "." or ".."))
                {
                    relativePath = candidate;
                    return true;
                }
            }

            start = index + marker.Length;
        }

        relativePath = string.Empty;
        return false;
    }

    private static bool IsSafePathPart(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOfAny(
            [
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar,
                Path.VolumeSeparatorChar
            ]) < 0
        && value is not "." and not "..";

    private static bool IsWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(
                fullPath,
                fullDirectory,
                StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                fullDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
