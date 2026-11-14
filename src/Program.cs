using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main()
    {
        try { SetProcessDPIAware(); }
        catch (Exception) { }

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
}
