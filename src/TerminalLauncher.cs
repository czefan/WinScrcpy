using System;
using System.Diagnostics;
using System.IO;
using System.Text;

internal sealed class TerminalLauncher
{
    readonly AdbClient adb;

    public TerminalLauncher(AdbClient adbClient)
    {
        this.adb = adbClient;
    }

    public bool OpenDeviceTerminal(string activeSerial, string displayTitle, TerminalMode mode, out string launchedWith, out string error)
    {
        error = "";
        launchedWith = "";
        if (string.IsNullOrWhiteSpace(activeSerial))
        {
            error = "设备序列号无效。";
            return false;
        }

        ToolResolutionResult adbRes = adb.ResolveAdbPath();
        if (!adbRes.Success)
        {
            error = adbRes.Error;
            return false;
        }

        string adbExe = adbRes.ResolvedPath;
        string workDir = NormalizeDirForCli(AppContext.BaseDirectory);
        string wtExe = ResolveWtExecutable();
        string pwshExe = ResolvePwshExecutable();
        string psExe = string.IsNullOrEmpty(pwshExe) ? "powershell.exe" : pwshExe;
        string psTag = string.IsNullOrEmpty(pwshExe) ? "powershell" : "pwsh7";

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.UseShellExecute = true;
        psi.WorkingDirectory = Directory.Exists(workDir) ? workDir : AppContext.BaseDirectory;

        if (!string.IsNullOrEmpty(wtExe))
        {
            psi.FileName = wtExe;
            psi.Arguments = BuildWtTerminalArguments(adbExe, psExe, activeSerial, displayTitle, workDir, mode);
            launchedWith = mode == TerminalMode.AdbShell ? "wt" : ("wt · " + psTag);
        }
        else
        {
            psi.FileName = psExe;
            psi.Arguments = BuildStandalonePsTerminalArguments(adbExe, activeSerial, displayTitle, mode);
            launchedWith = psTag;
        }

        try
        {
            using (Process proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    error = "未能启动终端进程。";
                    return false;
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            error = "启动终端失败: " + ex.Message;
            return false;
        }
    }

    internal static string ResolveWtExecutable()
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApp))
        {
            string aliasPath = Path.Combine(localApp, "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(aliasPath)) return aliasPath;
        }
        return ToolPathResolver.FindExecutableInPath("wt.exe");
    }

    internal static string ResolvePwshExecutable()
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApp))
        {
            string aliasPath = Path.Combine(localApp, "Microsoft", "WindowsApps", "pwsh.exe");
            if (File.Exists(aliasPath)) return aliasPath;
        }

        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(progFiles))
        {
            string stdPath = Path.Combine(progFiles, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(stdPath)) return stdPath;
        }
        return ToolPathResolver.FindExecutableInPath("pwsh.exe");
    }

    internal static string BuildWtTerminalArguments(string adbExe, string psExe, string activeSerial, string displayTitle, string workDir, TerminalMode mode)
    {
        string tabTitle = BuildTerminalTitle(activeSerial, displayTitle, mode);
        string cleanDir = NormalizeDirForCli(workDir);
        string safeDir = string.IsNullOrEmpty(cleanDir) ? "." : cleanDir.Replace(";", "\\;");

        string prefix = "-w 0 new-tab --title " + QuoteArg(tabTitle) + " -d " + QuoteArg(safeDir) + " -- ";
        if (mode == TerminalMode.AdbShell)
        {
            return prefix + QuoteArg(adbExe.Replace(";", "\\;")) + " -s " + QuoteArg(activeSerial) + " shell";
        }

        string initScript = BuildLocalPowerShellInitScript(adbExe, activeSerial, tabTitle);
        string shellTarget = string.IsNullOrWhiteSpace(psExe) ? "pwsh.exe" : psExe.Replace(";", "\\;");
        return prefix + QuoteArg(shellTarget) + " -NoLogo -NoExit -EncodedCommand " + EncodePowerShellCommand(initScript);
    }

    internal static string BuildStandalonePsTerminalArguments(string adbExe, string activeSerial, string displayTitle, TerminalMode mode)
    {
        string tabTitle = BuildTerminalTitle(activeSerial, displayTitle, mode);
        if (mode == TerminalMode.AdbShell)
        {
            string script = BuildShellPowerShellScript(adbExe, activeSerial, tabTitle);
            return "-NoLogo -EncodedCommand " + EncodePowerShellCommand(script);
        }

        string initScript = BuildLocalPowerShellInitScript(adbExe, activeSerial, tabTitle);
        return "-NoLogo -NoExit -EncodedCommand " + EncodePowerShellCommand(initScript);
    }

    internal static string BuildLocalPowerShellInitScript(string adbExe, string activeSerial, string windowTitle)
    {
        string cleanSerial = (activeSerial ?? "").Replace("\r", "").Replace("\n", "").Trim();
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("$Host.UI.RawUI.WindowTitle = '" + EscapePsSingleQuoted(windowTitle) + "'");
        sb.AppendLine("$env:ANDROID_SERIAL = '" + EscapePsSingleQuoted(cleanSerial) + "'");
        if (!string.Equals(adbExe, "adb", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(adbExe, "adb.exe", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.GetDirectoryName(adbExe);
            if (!string.IsNullOrEmpty(dir))
            {
                sb.AppendLine("$env:PATH = '" + EscapePsSingleQuoted(dir) + ";' + $env:PATH");
            }
            sb.AppendLine("function global:adb { & '" + EscapePsSingleQuoted(adbExe) + "' @args }");
        }
        sb.AppendLine("Write-Host '[WinScrcpy] 已绑定设备: " + EscapePsSingleQuoted(cleanSerial) + "' -ForegroundColor Cyan");
        return sb.ToString();
    }

    internal static string BuildShellPowerShellScript(string adbExe, string activeSerial, string windowTitle)
    {
        string cleanSerial = (activeSerial ?? "").Replace("\r", "").Replace("\n", "").Trim();
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("$Host.UI.RawUI.WindowTitle = '" + EscapePsSingleQuoted(windowTitle) + "'");
        sb.AppendLine("& '" + EscapePsSingleQuoted(adbExe) + "' -s '" + EscapePsSingleQuoted(cleanSerial) + "' shell");
        sb.AppendLine("if ($LASTEXITCODE -ne 0) { Read-Host 'ADB Shell 已退出，按回车关闭窗口' }");
        return sb.ToString();
    }

    static string BuildTerminalTitle(string activeSerial, string displayTitle, TerminalMode mode)
    {
        string suffix = mode == TerminalMode.AdbShell ? " - ADB Shell" : " - ADB 终端";
        return SanitizeLabelForShell(string.IsNullOrWhiteSpace(displayTitle) ? activeSerial : displayTitle) + suffix;
    }

    static string EncodePowerShellCommand(string script)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script ?? ""));
    }

    static string EscapePsSingleQuoted(string text)
    {
        return (text ?? "").Replace("'", "''");
    }

    static string NormalizeDirForCli(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        string trimmed = dir.Trim();
        if (trimmed.Length > 3 && trimmed.EndsWith("\\", StringComparison.Ordinal))
        {
            trimmed = trimmed.TrimEnd('\\');
            if (trimmed.Length == 2 && trimmed[1] == ':') trimmed = trimmed + "\\";
        }
        return trimmed;
    }

    static string SanitizeLabelForShell(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "ADB";
        char[] invalid = new char[] { ';', '&', '|', '<', '>', '^', '"', '\r', '\n', '%' };
        StringBuilder sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
        }
        string cleaned = sb.ToString().Trim();
        return cleaned.Length > 0 ? cleaned : "ADB";
    }

    public static string QuoteArg(string arg)
    {
        if (arg == null) return "\"\"";
        if (arg.Length == 0) return "\"\"";
        if (arg.IndexOfAny(new char[] { ' ', '\t', '"', '\n', '\r' }) < 0) return arg;

        StringBuilder sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
