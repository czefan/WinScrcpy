using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal enum DetachedMonitorStatus
{
    MatchedReady,
    TimedOutAlive,
    Exited,
    Cancelled,
    StartFailed
}

internal sealed class DetachedMonitorResult
{
    public DetachedMonitorStatus Status;
    public bool ProcessStarted;
    public int ProcessId;
    public int ExitCode;
    public string LogFilePath;
    public string CapturedLog;
    public string Error;

    public DetachedMonitorResult()
    {
        ExitCode = -1;
        LogFilePath = "";
        CapturedLog = "";
        Error = "";
    }
}

/// <summary>
/// Launches independent child processes via Win32 CreateProcessW using an inheritable
/// log-file write handle so child processes never depend on pipes owned by the GUI.
/// </summary>
internal static class DetachedProcessLauncher
{
    static readonly object InheritableSpawnLock = new object();

    const uint HANDLE_FLAG_INHERIT = 1;
    const int STARTF_USESHOWWINDOW = 0x00000001;
    const int STARTF_USESTDHANDLES = 0x00000100;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_NO_WINDOW = 0x08000000;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    const uint WAIT_OBJECT_0 = 0x00000000;
    const int ERROR_INSUFFICIENT_BUFFER = 122;
    const short SW_HIDE = 0;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x00020002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(SafeHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(
        string lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out int lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    public static DetachedMonitorResult LaunchWithLogAndMonitor(
        ProcessStartInfo psi,
        string logFilePath,
        int monitorTimeoutMs,
        Func<string, bool> isReadyLine,
        CancellationToken cancellationToken = default)
    {
        DetachedMonitorResult result = new DetachedMonitorResult();
        result.LogFilePath = logFilePath ?? "";

        if (cancellationToken.IsCancellationRequested)
        {
            result.Status = DetachedMonitorStatus.Cancelled;
            result.Error = "启动请求在创建进程前已取消。";
            return result;
        }

        if (psi == null || string.IsNullOrWhiteSpace(psi.FileName))
        {
            result.Status = DetachedMonitorStatus.StartFailed;
            result.Error = "未指定可执行文件路径。";
            return result;
        }

        List<string> args = new List<string>();
        for (int i = 0; i < psi.ArgumentList.Count; i++)
        {
            args.Add(psi.ArgumentList[i]);
        }

        string cmdLine = BuildWin32CommandLine(psi.FileName, args);
        byte[] envBytes = Encoding.Unicode.GetBytes(BuildUnicodeEnvironmentBlock(psi.Environment));
        string workDir = string.IsNullOrWhiteSpace(psi.WorkingDirectory) ? AppContext.BaseDirectory : psi.WorkingDirectory;

        SafeProcessHandle procHandle = null;
        try
        {
            string logDir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrWhiteSpace(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            // 写句柄仅允许 FileShare.Read（禁止 FileShare.Delete），防止仍在运行的会话日志被清理删掉。
            lock (InheritableSpawnLock)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Status = DetachedMonitorStatus.Cancelled;
                    result.Error = "启动请求在创建进程前已取消。";
                    return result;
                }

                using (SafeFileHandle logWriteHandle = File.OpenHandle(
                    logFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    FileOptions.None))
                using (SafeFileHandle nulReadHandle = File.OpenHandle(
                    "NUL",
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    FileOptions.None))
                {
                    if (!SetHandleInformation(logWriteHandle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "设置日志文件句柄可继承属性失败");
                    }
                    if (!SetHandleInformation(nulReadHandle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "设置 NUL 输入句柄可继承属性失败");
                    }

                    bool logRefAdded = false;
                    bool nulRefAdded = false;
                    bool attrInitialized = false;
                    IntPtr attrList = IntPtr.Zero;
                    GCHandle envPin = default(GCHandle);
                    GCHandle handlesPin = default(GCHandle);
                    try
                    {
                        logWriteHandle.DangerousAddRef(ref logRefAdded);
                        nulReadHandle.DangerousAddRef(ref nulRefAdded);

                        IntPtr logHandle = logWriteHandle.DangerousGetHandle();
                        IntPtr nulHandle = nulReadHandle.DangerousGetHandle();
                        IntPtr[] inheritHandles = new IntPtr[] { nulHandle, logHandle };

                        IntPtr attrSize = IntPtr.Zero;
                        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
                        int initErr = Marshal.GetLastWin32Error();
                        if (attrSize == IntPtr.Zero || (initErr != 0 && initErr != ERROR_INSUFFICIENT_BUFFER))
                        {
                            throw new Win32Exception(initErr, "获取进程属性列表大小失败");
                        }

                        attrList = Marshal.AllocHGlobal(attrSize);
                        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "初始化进程属性列表失败");
                        }
                        attrInitialized = true;

                        handlesPin = GCHandle.Alloc(inheritHandles, GCHandleType.Pinned);
                        if (!UpdateProcThreadAttribute(
                            attrList,
                            0,
                            PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                            handlesPin.AddrOfPinnedObject(),
                            (IntPtr)(inheritHandles.Length * IntPtr.Size),
                            IntPtr.Zero,
                            IntPtr.Zero))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "设置进程可继承句柄白名单失败");
                        }

                        envPin = GCHandle.Alloc(envBytes, GCHandleType.Pinned);

                        STARTUPINFOEXW siEx = new STARTUPINFOEXW();
                        siEx.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEXW));
                        siEx.StartupInfo.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
                        siEx.StartupInfo.wShowWindow = SW_HIDE;
                        siEx.StartupInfo.hStdInput = nulHandle;
                        siEx.StartupInfo.hStdOutput = logHandle;
                        siEx.StartupInfo.hStdError = logHandle;
                        siEx.lpAttributeList = attrList;

                        PROCESS_INFORMATION pi;
                        bool created = CreateProcessW(
                            psi.FileName,
                            new StringBuilder(cmdLine),
                            IntPtr.Zero,
                            IntPtr.Zero,
                            true,
                            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,
                            envPin.AddrOfPinnedObject(),
                            workDir,
                            ref siEx,
                            out pi);

                        if (!created)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }

                        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                        procHandle = new SafeProcessHandle(pi.hProcess, true);
                        result.ProcessStarted = true;
                        result.ProcessId = unchecked((int)pi.dwProcessId);
                    }
                    finally
                    {
                        if (attrInitialized) DeleteProcThreadAttributeList(attrList);
                        if (attrList != IntPtr.Zero) Marshal.FreeHGlobal(attrList);
                        if (handlesPin.IsAllocated) handlesPin.Free();
                        if (logRefAdded) logWriteHandle.DangerousRelease();
                        if (nulRefAdded) nulReadHandle.DangerousRelease();
                        if (envPin.IsAllocated) envPin.Free();
                    }
                }
            }

            MonitorProcessLog(procHandle, logFilePath, monitorTimeoutMs, isReadyLine, cancellationToken, result);
            return result;
        }
        catch (Exception ex)
        {
            result.Status = DetachedMonitorStatus.StartFailed;
            result.Error = ex.Message;
            return result;
        }
        finally
        {
            if (procHandle != null) procHandle.Dispose();
        }
    }

    public static void PruneOldLogs(string dir, string searchPattern, int keepCount)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        try
        {
            string[] files = Directory.GetFiles(dir, searchPattern);
            if (files.Length <= keepCount) return;
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            int removeCount = files.Length - keepCount;
            for (int i = 0; i < removeCount; i++)
            {
                // 仍在运行中的会话写句柄禁用了 FileShare.Delete，遇到占用直接跳过。
                try { File.Delete(files[i]); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    static void MonitorProcessLog(
        SafeProcessHandle procHandle,
        string logFilePath,
        int monitorTimeoutMs,
        Func<string, bool> isReadyLine,
        CancellationToken cancellationToken,
        DetachedMonitorResult result)
    {
        StringBuilder allLog = new StringBuilder();
        StringBuilder pendingLine = new StringBuilder();
        Stopwatch sw = Stopwatch.StartNew();

        using (FileStream fs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new StreamReader(fs, new UTF8Encoding(false), true))
        {
            while (true)
            {
                if (DrainLogLines(reader, allLog, pendingLine, isReadyLine))
                {
                    if (WaitForSingleObject(procHandle, 0) != WAIT_OBJECT_0)
                    {
                        result.Status = DetachedMonitorStatus.MatchedReady;
                        result.CapturedLog = allLog.ToString();
                        return;
                    }
                }

                uint waitState = WaitForSingleObject(procHandle, 35);
                if (waitState == WAIT_OBJECT_0)
                {
                    Thread.Sleep(35);
                    DrainLogLines(reader, allLog, pendingLine, null);
                    FlushPendingLine(allLog, pendingLine);
                    int exitCode;
                    result.ExitCode = GetExitCodeProcess(procHandle, out exitCode) ? exitCode : -1;
                    result.Status = DetachedMonitorStatus.Exited;
                    result.CapturedLog = allLog.ToString();
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    DrainLogLines(reader, allLog, pendingLine, null);
                    FlushPendingLine(allLog, pendingLine);
                    result.Status = DetachedMonitorStatus.Cancelled;
                    result.CapturedLog = allLog.ToString();
                    result.Error = "进程已启动，已停止继续等待就绪信号。";
                    return;
                }

                if (monitorTimeoutMs >= 0 && sw.ElapsedMilliseconds >= monitorTimeoutMs)
                {
                    bool matchedAtEdge = DrainLogLines(reader, allLog, pendingLine, isReadyLine);
                    FlushPendingLine(allLog, pendingLine);
                    if (WaitForSingleObject(procHandle, 0) == WAIT_OBJECT_0)
                    {
                        int exitCode;
                        result.ExitCode = GetExitCodeProcess(procHandle, out exitCode) ? exitCode : -1;
                        result.Status = DetachedMonitorStatus.Exited;
                    }
                    else
                    {
                        result.Status = matchedAtEdge
                            ? DetachedMonitorStatus.MatchedReady
                            : DetachedMonitorStatus.TimedOutAlive;
                    }
                    result.CapturedLog = allLog.ToString();
                    return;
                }
            }
        }
    }

    static bool DrainLogLines(StreamReader reader, StringBuilder allLog, StringBuilder pendingLine, Func<string, bool> isReadyLine)
    {
        char[] buf = new char[4096];
        bool matched = false;
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char ch = buf[i];
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    string line = pendingLine.ToString();
                    pendingLine.Clear();
                    AppendCapturedLogLine(allLog, line);
                    if (isReadyLine != null && isReadyLine(line))
                    {
                        matched = true;
                    }
                }
                else
                {
                    pendingLine.Append(ch);
                }
            }
        }
        return matched;
    }

    static void FlushPendingLine(StringBuilder allLog, StringBuilder pendingLine)
    {
        if (pendingLine.Length > 0)
        {
            AppendCapturedLogLine(allLog, pendingLine.ToString());
            pendingLine.Clear();
        }
    }

    static void AppendCapturedLogLine(StringBuilder allLog, string line)
    {
        if (allLog.Length > 65536) return;
        allLog.AppendLine(line);
    }

    internal static string BuildWin32CommandLine(string fileName, IList<string> args)
    {
        StringBuilder sb = new StringBuilder();
        AppendWin32Arg(sb, fileName ?? "");
        if (args != null)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == null) continue;
                sb.Append(' ');
                AppendWin32Arg(sb, args[i]);
            }
        }
        return sb.ToString();
    }

    static void AppendWin32Arg(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && !NeedsWin32Quoting(arg))
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        int i = 0;
        while (i < arg.Length)
        {
            char c = arg[i++];
            if (c == '\\')
            {
                int backslashes = 1;
                while (i < arg.Length && arg[i] == '\\')
                {
                    i++;
                    backslashes++;
                }

                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                }
                else if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    i++;
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
            }
            else if (c == '"')
            {
                sb.Append("\\\"");
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('"');
    }

    static bool NeedsWin32Quoting(string arg)
    {
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (char.IsWhiteSpace(c) || c == '"') return true;
        }
        return false;
    }

    static string BuildUnicodeEnvironmentBlock(IDictionary<string, string> environment)
    {
        SortedDictionary<string, string> sorted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (environment != null && environment.Count > 0)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    sorted[pair.Key] = pair.Value ?? "";
                }
            }
        }
        else
        {
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = entry.Key as string;
                if (!string.IsNullOrEmpty(key))
                {
                    sorted[key] = entry.Value as string ?? "";
                }
            }
        }

        StringBuilder sb = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in sorted)
        {
            sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }
}
