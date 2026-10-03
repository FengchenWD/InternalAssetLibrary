using System.Runtime.InteropServices;

namespace InternalAssetLibrary.Updater;

internal static class Program
{
    private const string TransactionOption = "--transaction";

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() ||
            args.Length != 2 ||
            !string.Equals(args[0], TransactionOption, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(args[1]))
        {
            return 2;
        }

        try
        {
            new UpdateWorkflow().RunAsync(args[1]).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            var logPath = UpdateWorkflow.WriteFatalDiagnostic(exception);
            _ = MessageBoxW(
                0,
                $"自动更新未能完成。软件会尽量恢复到可用版本。\n\n诊断文件：\n{logPath}",
                "云汀素材管理工具更新失败",
                0x00000010);
            return 1;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}
