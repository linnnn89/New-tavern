using System.IO;

namespace TavernDesk.App.Services;

/// <summary>Explicit, fresh test workspace; never falls back to personal data.</summary>
public sealed record IsolatedTestStartup(string Root, bool ProbeOnly)
{
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
            else
            {
                throw new ArgumentException("测试模式只接受 --test-root <全新绝对路径> 和可选的 --test-startup-probe。");
            }
        }

        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("测试模式必须指定全新的绝对路径，不能使用个人资料目录。");
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) || File.Exists(root))
            throw new ArgumentException("测试目录已经存在；请使用全新目录，不得复用或复制个人数据库。");

        // Reject linked ancestors before creating any files in a test workspace.
        for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.ResolveLinkTarget(false) is not null)
                throw new ArgumentException("测试目录不能位于目录链接之下。");

        return new IsolatedTestStartup(root, probe);
    }
}
