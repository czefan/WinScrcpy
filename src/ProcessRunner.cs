using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class ToolResolutionResult
{
    public bool Success;
    public string ResolvedPath;
    public string Error;

    public static ToolResolutionResult Ok(string path)
    {
        return new ToolResolutionResult
        {
            Success = true,
            ResolvedPath = path ?? "",
            Error = ""
        };
    }

    public static ToolResolutionResult Fail(string error)
    {
        return new ToolResolutionResult
        {
            Success = false,
            ResolvedPath = "",
            Error = error ?? "工具路径解析失败。"
        };
    }
}

internal static class ToolPathResolver
{
    public static ToolResolutionResult ResolveTool(string configuredPath, string defaultToolName, string appDir = null, string pathEnv = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(appDir) ? AppContext.BaseDirectory : appDir;
        string trimmed = (configuredPath ?? "").Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith("\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
        }

        string exeName = defaultToolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? defaultToolName
            : defaultToolName + ".exe";

        bool isDefaultName = string.IsNullOrEmpty(trimmed)
            || string.Equals(trimmed, defaultToolName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, exeName, StringComparison.OrdinalIgnoreCase);

        if (!isDefaultName)
        {
            // 显式配置了路径或自定义名称时，只校验该目标，不静默回退到其他位置的同名工具。
            string resolvedExplicit;
            try
            {
                string combined = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(baseDir, trimmed);
                resolvedExplicit = Path.GetFullPath(combined);
            }
            catch (Exception ex)
            {
                return ToolResolutionResult.Fail(string.Format("配置的 {0} 路径无效 ({1}): {2}", defaultToolName, trimmed, ex.Message));
            }

            if (File.Exists(resolvedExplicit))
            {
                return ToolResolutionResult.Ok(resolvedExplicit);
            }
            if (!Path.HasExtension(resolvedExplicit) && File.Exists(resolvedExplicit + ".exe"))
            {
                return ToolResolutionResult.Ok(resolvedExplicit + ".exe");
            }
            return ToolResolutionResult.Fail(string.Format("配置的 {0} 路径不存在: {1}", defaultToolName, resolvedExplicit));
        }

        // 默认名称按「应用目录 -> PATH」顺序查找并解析为绝对路径。
        try
        {
            string localCandidate = Path.GetFullPath(Path.Combine(baseDir, exeName));
            if (File.Exists(localCandidate))
            {
                return ToolResolutionResult.Ok(localCandidate);
            }
        }
        catch (Exception) { }

        string fromPath = FindExecutableInPath(exeName, pathEnv);
        if (!string.IsNullOrEmpty(fromPath))
        {
            return ToolResolutionResult.Ok(fromPath);
        }

        return ToolResolutionResult.Fail(string.Format("未在应用目录或系统 PATH 中找到 {0}，请在 winscrcpy.json 中配置有效路径。", exeName));
    }

    public static string FindExecutableInPath(string exeName, string pathEnvOverride = null)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return null;
        string trimmedName = exeName.Trim();
        bool hasExt = Path.HasExtension(trimmedName);
        string pathEnv = pathEnvOverride ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] dirs = pathEnv.Split(new char[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < dirs.Length; i++)
        {
            try
            {
                string dir = dirs[i].Trim().Trim('"');
                if (dir.Length == 0) continue;
                string candidate = Path.GetFullPath(Path.Combine(dir, trimmedName));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                if (!hasExt && File.Exists(candidate + ".exe"))
                {
                    return candidate + ".exe";
                }
            }
            catch (Exception) { }
        }
        return null;
    }
}

internal static class ProcessRunner
{
    public static ProcessStartInfo BuildStartInfo(
        string fileName,
        IList<string> args,
        IDictionary<string, string> environment = null,
        string workingDirectory = null,
        bool redirectOutput = true,
        bool binaryStdout = false)
    {
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = fileName;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (args != null)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] != null)
                {
                    psi.ArgumentList.Add(args[i]);
                }
            }
        }

        if (environment != null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                psi.Environment[pair.Key] = pair.Value ?? "";
            }
        }

        if (redirectOutput)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardErrorEncoding = Encoding.UTF8;
            if (!binaryStdout)
            {
                psi.StandardOutputEncoding = Encoding.UTF8;
            }
        }

        return psi;
    }

    public static CommandOutput RunText(
        string fileName,
        IList<string> args,
        int timeoutMs,
        IDictionary<string, string> environment = null,
        string workingDirectory = null,
        CancellationToken cancellationToken = default,
        int drainTimeoutMs = -1)
    {
        return RunCore(fileName, args, false, timeoutMs, environment, workingDirectory, cancellationToken, drainTimeoutMs);
    }

    public static CommandOutput RunBinary(
        string fileName,
        IList<string> args,
        int timeoutMs,
        IDictionary<string, string> environment = null,
        string workingDirectory = null,
        CancellationToken cancellationToken = default,
        int drainTimeoutMs = -1)
    {
        return RunCore(fileName, args, true, timeoutMs, environment, workingDirectory, cancellationToken, drainTimeoutMs);
    }

    static CommandOutput RunCore(
        string fileName,
        IList<string> args,
        bool binaryStdout,
        int timeoutMs,
        IDictionary<string, string> environment,
        string workingDirectory,
        CancellationToken cancellationToken,
        int drainTimeoutMs)
    {
        CommandOutput result = new CommandOutput();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            result.ExitCode = -1;
            result.LaunchError = "未指定可执行文件路径。";
            return result;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            result.ExitCode = -1;
            result.Cancelled = true;
            result.LaunchError = "操作已取消。";
            return result;
        }

        ProcessStartInfo psi = BuildStartInfo(fileName, args, environment, workingDirectory, true, binaryStdout);

        using (Process proc = new Process())
        {
            proc.StartInfo = psi;
            try
            {
                if (!proc.Start())
                {
                    result.ExitCode = -1;
                    result.LaunchError = "未能启动进程。";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.LaunchError = ex.Message;
                return result;
            }

            int effectiveDrainMs = drainTimeoutMs > 0
                ? drainTimeoutMs
                : (binaryStdout ? 3000 : 1000);

            StringBuilder stderrBuilder = new StringBuilder();
            object errSync = new object();
            Exception errReadError = null;
            Task errTask = DrainTextStreamAsync(proc.StandardError, stderrBuilder, errSync, delegate(Exception ex) { errReadError = ex; });

            StringBuilder stdoutBuilder = binaryStdout ? null : new StringBuilder();
            MemoryStream stdoutBinaryStream = binaryStdout ? new MemoryStream() : null;
            object outSync = new object();
            Exception outReadError = null;
            Task outTask = binaryStdout
                ? DrainBinaryStreamAsync(proc.StandardOutput.BaseStream, stdoutBinaryStream, outSync, delegate(Exception ex) { outReadError = ex; })
                : DrainTextStreamAsync(proc.StandardOutput, stdoutBuilder, outSync, delegate(Exception ex) { outReadError = ex; });

            bool exitedNormally = WaitForExitWithTimeoutAndCancel(proc, timeoutMs, cancellationToken, result);
            if (!exitedNormally)
            {
                // 仅结束本次启动的进程实例（不杀整个进程树），避免误杀共用的 adb server 后台进程。
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(false);
                    }
                }
                catch (Exception) { }

                try { proc.WaitForExit(500); }
                catch (Exception) { }

                if (drainTimeoutMs <= 0)
                {
                    effectiveDrainMs = 300;
                }
            }

            // adb start-server 等命令拉起后台守护进程时可能短暂继承管道，限定排空等待并在未读完整时标记状态。
            FinalizeStreamDrain(
                outTask,
                errTask,
                effectiveDrainMs,
                exitedNormally,
                binaryStdout,
                stdoutBuilder,
                stdoutBinaryStream,
                stderrBuilder,
                outSync,
                errSync,
                delegate { return outReadError; },
                delegate { return errReadError; },
                result);
        }

        return result;
    }

    internal static void CaptureStreamsForTest(
        Stream stdoutStream,
        Stream stderrStream,
        bool binaryStdout,
        int drainTimeoutMs,
        bool exitedNormally,
        CommandOutput result)
    {
        StringBuilder stderrBuilder = new StringBuilder();
        object errSync = new object();
        Exception errReadError = null;
        Task errTask = DrainTextStreamAsync(new StreamReader(stderrStream, Encoding.UTF8), stderrBuilder, errSync, delegate(Exception ex) { errReadError = ex; });

        StringBuilder stdoutBuilder = binaryStdout ? null : new StringBuilder();
        MemoryStream stdoutBinaryStream = binaryStdout ? new MemoryStream() : null;
        object outSync = new object();
        Exception outReadError = null;
        Task outTask = binaryStdout
            ? DrainBinaryStreamAsync(stdoutStream, stdoutBinaryStream, outSync, delegate(Exception ex) { outReadError = ex; })
            : DrainTextStreamAsync(new StreamReader(stdoutStream, Encoding.UTF8), stdoutBuilder, outSync, delegate(Exception ex) { outReadError = ex; });

        FinalizeStreamDrain(
            outTask,
            errTask,
            drainTimeoutMs,
            exitedNormally,
            binaryStdout,
            stdoutBuilder,
            stdoutBinaryStream,
            stderrBuilder,
            outSync,
            errSync,
            delegate { return outReadError; },
            delegate { return errReadError; },
            result);
    }

    static void FinalizeStreamDrain(
        Task outTask,
        Task errTask,
        int drainTimeoutMs,
        bool exitedNormally,
        bool binaryStdout,
        StringBuilder stdoutBuilder,
        MemoryStream stdoutBinaryStream,
        StringBuilder stderrBuilder,
        object outSync,
        object errSync,
        Func<Exception> getOutReadError,
        Func<Exception> getErrReadError,
        CommandOutput result)
    {
        bool allDrained = false;
        Exception waitException = null;
        try
        {
            allDrained = Task.WaitAll(new Task[] { outTask, errTask }, drainTimeoutMs);
        }
        catch (Exception ex)
        {
            waitException = ex;
        }

        if (binaryStdout)
        {
            lock (outSync)
            {
                result.StdOutBytes = stdoutBinaryStream.ToArray();
            }
        }
        else
        {
            lock (outSync)
            {
                result.StdOut = stdoutBuilder.ToString();
            }
        }

        lock (errSync)
        {
            result.StdErr = stderrBuilder.ToString();
        }

        Exception readError = getOutReadError() ?? getErrReadError();
        if (readError == null && waitException != null)
        {
            AggregateException agg = waitException as AggregateException;
            readError = agg != null ? agg.GetBaseException() : waitException;
        }
        if (readError == null && outTask.IsFaulted && outTask.Exception != null)
        {
            readError = outTask.Exception.GetBaseException();
        }
        if (readError == null && errTask.IsFaulted && errTask.Exception != null)
        {
            readError = errTask.Exception.GetBaseException();
        }

        if (readError != null)
        {
            result.OutputError = "读取命令输出失败: " + readError.Message;
        }
        else if (exitedNormally && (!allDrained || !outTask.IsCompletedSuccessfully || !errTask.IsCompletedSuccessfully))
        {
            result.OutputTimedOut = true;
        }
    }

    static bool WaitForExitWithTimeoutAndCancel(Process proc, int timeoutMs, CancellationToken cancellationToken, CommandOutput result)
    {
        using (CancellationTokenSource timeoutCts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : new CancellationTokenSource())
        using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
        {
            try
            {
                proc.WaitForExitAsync(linkedCts.Token).GetAwaiter().GetResult();
                result.ExitCode = proc.ExitCode;
                return true;
            }
            catch (OperationCanceledException)
            {
                result.ExitCode = -1;
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Cancelled = true;
                }
                else
                {
                    result.TimedOut = true;
                }
                return false;
            }
        }
    }

    static Task DrainTextStreamAsync(StreamReader reader, StringBuilder sb, object sync, Action<Exception> onError)
    {
        return Task.Run(async delegate()
        {
            try
            {
                char[] buf = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    lock (sync)
                    {
                        sb.Append(buf, 0, read);
                    }
                }
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        });
    }

    static Task DrainBinaryStreamAsync(Stream stream, MemoryStream target, object sync, Action<Exception> onError)
    {
        return Task.Run(async delegate()
        {
            try
            {
                byte[] buf = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    lock (sync)
                    {
                        target.Write(buf, 0, read);
                    }
                }
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        });
    }
}
