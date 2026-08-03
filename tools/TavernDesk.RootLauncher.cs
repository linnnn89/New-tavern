using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class TavernDeskRootLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        var target = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "src",
            "TavernDesk.App",
            "bin",
            "Release",
            "net10.0-windows",
            "TavernDesk.App.exe");

        if (args.Length == 1
            && string.Equals(
                args[0],
                "--probe",
                StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(target) ? 0 : 2;
        }

        if (!File.Exists(target))
        {
            MessageBox.Show(
                "尚未找到 TavernDesk Release 程序。\r\n\r\n"
                + "请先在项目根目录执行：\r\n"
                + "dotnet build TavernDesk.sln -c Release --no-restore",
                "TavernDesk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 2;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                WorkingDirectory = Path.GetDirectoryName(target),
                UseShellExecute = true
            });
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "TavernDesk 启动失败：\r\n" + exception.Message,
                "TavernDesk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
