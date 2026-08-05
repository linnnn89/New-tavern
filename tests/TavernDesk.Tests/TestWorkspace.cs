using Microsoft.Data.Sqlite;

namespace TavernDesk.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        var baseDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "verification-data"));
        Directory.CreateDirectory(baseDirectory);
        Root = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        var resolvedRoot = Path.GetFullPath(Root);
        var allowedBase = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "verification-data"));
        if (!resolvedRoot.StartsWith(
                allowedBase + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("测试目录越出项目构建输出范围，拒绝清理。");
        }

        if (Directory.Exists(resolvedRoot))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}
