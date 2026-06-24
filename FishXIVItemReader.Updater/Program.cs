using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FishXIVItemReader.Updater
{
    internal static class Program
    {
        private const int WaitTimeoutMilliseconds = 300000;

        private static int Main(string[] args)
        {
            var options = ParseArguments(args);
            var logPath = GetOption(options, "log");

            try
            {
                var actProcessId = ParseInt(GetOption(options, "act-pid"));
                var pluginDirectory = GetOption(options, "plugin-dir");
                var stagingDirectory = GetOption(options, "staging-dir");
                var actExecutablePath = GetOption(options, "act-exe");

                ValidateDirectory(stagingDirectory, "更新暂存目录不存在。");
                Directory.CreateDirectory(pluginDirectory);

                WriteLog(logPath, "Waiting for ACT to exit.");
                WaitForProcessExit(actProcessId, logPath);
                Thread.Sleep(800);

                CopyPreparedFiles(stagingDirectory, pluginDirectory, logPath);
                TryDeleteFile(Path.Combine(pluginDirectory, "FishXIVItemReader.pdb"));
                TryDeleteDirectory(stagingDirectory);

                WriteLog(logPath, "Update files copied.");
                RestartAct(actExecutablePath, logPath);
                return 0;
            }
            catch (Exception ex)
            {
                WriteLog(logPath, ex.ToString());
                return 1;
            }
        }

        private static Dictionary<string, string> ParseArguments(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    continue;
                }

                options[key.Substring(2)] = args[++i];
            }

            return options;
        }

        private static string GetOption(Dictionary<string, string> options, string name)
        {
            string value;
            if (!options.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("缺少更新参数：" + name);
            }

            return value;
        }

        private static int ParseInt(string value)
        {
            int result;
            if (!int.TryParse(value, out result))
            {
                throw new ArgumentException("更新参数格式异常。");
            }

            return result;
        }

        private static void ValidateDirectory(string directory, string message)
        {
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(message);
            }
        }

        private static void WaitForProcessExit(int processId, string logPath)
        {
            if (processId <= 0)
            {
                return;
            }

            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch
            {
                return;
            }

            using (process)
            {
                if (!process.WaitForExit(WaitTimeoutMilliseconds))
                {
                    WriteLog(logPath, "ACT did not exit before timeout.");
                    throw new TimeoutException("等待 ACT 退出超时。");
                }
            }
        }

        private static void CopyPreparedFiles(string stagingDirectory, string pluginDirectory, string logPath)
        {
            var sourceRoot = NormalizeDirectoryPath(stagingDirectory);
            var targetRoot = NormalizeDirectoryPath(pluginDirectory);
            foreach (var sourceFile in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetExtension(sourceFile), ".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = GetRelativePath(sourceRoot, sourceFile);
                var targetFile = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
                if (!IsPathInside(targetFile, targetRoot))
                {
                    throw new InvalidOperationException("更新包路径异常。");
                }

                var parent = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                CopyFileWithRetry(sourceFile, targetFile, logPath);
            }
        }

        private static void CopyFileWithRetry(string sourceFile, string targetFile, string logPath)
        {
            Exception lastException = null;
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    File.Copy(sourceFile, targetFile, true);
                    return;
                }
                catch (IOException ex)
                {
                    lastException = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastException = ex;
                }

                WriteLog(logPath, "Retry copy: " + targetFile);
                Thread.Sleep(500);
            }

            throw new IOException("无法覆盖插件文件。", lastException);
        }

        private static void RestartAct(string actExecutablePath, string logPath)
        {
            if (string.IsNullOrWhiteSpace(actExecutablePath) || !File.Exists(actExecutablePath))
            {
                WriteLog(logPath, "ACT executable path is empty or missing.");
                return;
            }

            Process.Start(new ProcessStartInfo(actExecutablePath)
            {
                UseShellExecute = true
            });
        }

        private static string GetRelativePath(string baseDirectory, string path)
        {
            var normalizedBaseDirectory = NormalizeDirectoryPath(baseDirectory);
            var normalizedPath = Path.GetFullPath(path);
            var prefix = normalizedBaseDirectory + Path.DirectorySeparatorChar;
            if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("路径不在预期目录中。");
            }

            return normalizedPath.Substring(prefix.Length);
        }

        private static bool IsPathInside(string path, string root)
        {
            var normalizedPath = Path.GetFullPath(path);
            var normalizedRoot = NormalizeDirectoryPath(root);

            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(
                       normalizedRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private static void WriteLog(string logPath, string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(logPath))
                {
                    return;
                }

                var parent = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.AppendAllText(
                    logPath,
                    DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
