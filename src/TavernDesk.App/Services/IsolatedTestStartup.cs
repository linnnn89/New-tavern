using System.IO;

namespace TavernDesk.App.Services;

/// <summary>Explicit test workspace; reuse requires a matching dedicated marker.</summary>
public sealed record IsolatedTestStartup(string Root, bool ProbeOnly, string? CharacterCardPath = null, bool ReuseWorkspace = false)
{
    public const string MarkerFileName = ".taverndesk-test-workspace";
    public string DataRoot => Path.Combine(Root, "data");
    public string ConfigurationRoot => Path.Combine(Root, "config");
    public string LogRoot => Path.Combine(Root, "logs");
    public string ReceiptPath => Path.Combine(Root, "startup-result.json");

    public static IsolatedTestStartup? Parse(IReadOnlyList<string> args)
    {
        var testRequested = args.Any(arg =>
            arg.StartsWith("--test-", StringComparison.OrdinalIgnoreCase));
        if (!testRequested) return null;

        string? root = null;
        var probe = false;
        var reuse = false;
        string? characterCard = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--test-root", StringComparison.OrdinalIgnoreCase)
                && root is null && index + 1 < args.Count)
            {
                root = args[++index];
            }
            else if (string.Equals(args[index], "--test-startup-probe", StringComparison.OrdinalIgnoreCase)
                     && !probe)
            {
                probe = true;
            }
            else if (string.Equals(args[index], "--test-reuse", StringComparison.OrdinalIgnoreCase) && !reuse)
            {
                reuse = true;
            }
            else if (string.Equals(args[index], "--test-character-card", StringComparison.OrdinalIgnoreCase)
                     && characterCard is null && index + 1 < args.Count)
            {
                characterCard = args[++index];
            }
            else
            {
                throw new ArgumentException("Test mode accepts --test-root <absolute-path>, --test-reuse, --test-startup-probe, and --test-character-card <absolute-file-path>.");
            }
        }

        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Test mode requires an absolute path to a dedicated test workspace, never personal data.");
        root = Path.GetFullPath(root);
        if (File.Exists(root) || (Directory.Exists(root) && !reuse))
            throw new ArgumentException("The test directory already exists. Use a fresh directory; never reuse or copy a personal database.");

        // Reject linked ancestors before creating any files in a test workspace.
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.ResolveLinkTarget(false) is not null)
                throw new ArgumentException("The test directory must not be under a directory link.");

        if (Directory.Exists(root))
        {
            var marker = Path.Combine(root, MarkerFileName);
            if (!File.Exists(marker) || new FileInfo(marker).LinkTarget is not null ||
                File.ReadAllText(marker) != "TavernDesk.TestWorkspace.v1\n" + root)
                throw new ArgumentException("Only a dedicated test directory with a matching marker can be reused.");
            // Check before descending: a marked profile must not redirect any later reads/writes.
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.TryPop(out var directory))
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("The test directory must not contain file or directory links.");
                    if (entry is DirectoryInfo child) pending.Push(child);
                }
        }

        if (characterCard is not null)
        {
            if (!Path.IsPathFullyQualified(characterCard) || !File.Exists(characterCard))
                throw new ArgumentException("The test character card must be an absolute path to an existing file.");
            characterCard = Path.GetFullPath(characterCard);
        }
        return new IsolatedTestStartup(root, probe, characterCard, reuse);
    }

    public void EnsureWorkspace()
    {
        // Revalidate if the directory appeared or changed after argument parsing.
        _ = Parse(ReuseWorkspace ? ["--test-root", Root, "--test-reuse"] : ["--test-root", Root]);
        if (Directory.Exists(Root)) return;
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, MarkerFileName), "TavernDesk.TestWorkspace.v1\n" + Root);
    }
}
