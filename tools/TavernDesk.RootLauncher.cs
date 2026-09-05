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
            "app",
            "TavernDesk.App.exe");

        if (args.Length == 1
            && string.Equals(
                args[0],
                "--probe",
                StringComparison.OrdinalIgnoreCase))
        {
            // File-presence check only. This does not launch or initialize the app.
            // Use scripts/Start-IsolatedTest.ps1 -StartupProbe for source startup QA.
            return File.Exists(target) ? 0 : 2;
        }

        if (!File.Exists(target))
        {
            MessageBox.Show(
                "尚未找到 TavernDesk 自包含发布程序。\r\n\r\n"
                + "请先生成 app 发布目录，或重新解压完整发布包。",
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
