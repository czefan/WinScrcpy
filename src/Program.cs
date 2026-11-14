using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);

    const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    static int Main(string[] args)
    {
        try { SetProcessDPIAware(); }
        catch (Exception) { }

        if (HasArg(args, "--selftest"))
        {
            if (!Console.IsOutputRedirected)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
            return SelfTests.Run();
        }

        string previewDir = GetArgValue(args, "--preview") ?? GetArgValue(args, "--preview-dialogs");
        if (!string.IsNullOrWhiteSpace(previewDir))
        {
            return SelfTests.RenderPreview(previewDir);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            string message = e.Exception != null ? e.Exception.Message : "未知界面异常";
            MessageBox.Show("操作未能完成：\n\n" + message, "WinScrcpy 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        ConfigManager manager = new ConfigManager(ConfigManager.DefaultConfigPath);
        string warning;
        AppConfig config = manager.LoadOrCreate(out warning);

        using (MainForm form = new MainForm(manager, config, warning, true))
        {
            Application.Run(form);
        }
        return 0;
    }

    static bool HasArg(string[] args, string target)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    static string GetArgValue(string[] args, string flag)
    {
        if (args == null) return null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
