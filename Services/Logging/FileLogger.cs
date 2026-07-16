using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace GithubLauncher.Services.Logging
{
    /// <summary>
    /// ILogger implementation that writes structured log entries to rotating
    /// files in the application's data directory (logs/ subfolder).
    /// Thread-safe, rolling by date and file size, with 7-day retention cleanup.
    /// </summary>
    public class FileLogger : ILogger, IDisposable
    {
        private static readonly object _lock = new();
        private static readonly HashSet<string> _directoriesCleaned = new();

        private readonly string _category;
        private readonly string _logDir;
        private readonly long _maxFileSize = 10 * 1024 * 1024; // 10 MB
        private readonly int _maxRetentionDays = 7;

        /// <summary>
        /// Creates a FileLogger. Logs are written to <paramref name="logDir"/>
        /// in files named app-YYYY-MM-DD.log, rotating on size.
        /// </summary>
        /// <param name="category">Optional category name shown in log entries.</param>
        /// <param name="logDir">Directory for log files. Defaults to AppPaths.DataDirectory/logs.</param>
        public FileLogger(string category = "", string? logDir = null)
        {
            _category = category;
            _logDir = logDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

            lock (_lock)
            {
                if (!_directoriesCleaned.Contains(_logDir))
                {
                    Directory.CreateDirectory(_logDir);
                    CleanupOldLogs();
                    _directoriesCleaned.Add(_logDir);
                }
            }
        }

        public bool IsEnabled(LogLevel level) => true;

        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var prefix = level switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warn => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Fatal => "FTL",
                _ => "???"
            };

            var cat = string.IsNullOrEmpty(_category) ? "" : $" [{_category}]";
            var logLine = $"[{timestamp}] [{prefix}]{cat} {message}";

            lock (_lock)
            {
                var filePath = GetCurrentLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                // Check size before writing
                if (File.Exists(filePath) && new FileInfo(filePath).Length > _maxFileSize)
                {
                    filePath = RotateLogFile(filePath);
                }

                File.AppendAllText(filePath, logLine + Environment.NewLine);

                if (exception != null)
                {
                    File.AppendAllText(filePath,
                        $"[{timestamp}] [{prefix}]{cat} Exception: {exception.GetType().Name}: {exception.Message}" + Environment.NewLine);
                    File.AppendAllText(filePath,
                        $"[{timestamp}] [{prefix}]{cat} StackTrace: {exception.StackTrace}" + Environment.NewLine);
                }
            }
        }

        public void Trace(string message) => Log(LogLevel.Trace, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
        public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);

        /// <summary>Returns today's log file path: app-YYYY-MM-DD.log</summary>
        private string GetCurrentLogPath()
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            return Path.Combine(_logDir, $"app-{date}.log");
        }

        /// <summary>Renames oversized log file with an increment suffix.</summary>
        private string RotateLogFile(string originalPath)
        {
            var dir = Path.GetDirectoryName(originalPath)!;
            var nameWithoutExt = Path.GetFileNameWithoutExtension(originalPath);
            var ext = Path.GetExtension(originalPath);

            for (int i = 1; ; i++)
            {
                var rotated = Path.Combine(dir, $"{nameWithoutExt}-{i}{ext}");
                if (!File.Exists(rotated))
                {
                    File.Move(originalPath, rotated);
                    return originalPath; // new current file is the original name
                }
            }
        }

        /// <summary>Deletes log files older than maxRetentionDays.</summary>
        private void CleanupOldLogs()
        {
            try
            {
                if (!Directory.Exists(_logDir)) return;
                var cutoff = DateTime.Now.AddDays(-_maxRetentionDays);
                foreach (var file in Directory.GetFiles(_logDir, "app-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                            File.Delete(file);
                    }
                    catch { /* best-effort cleanup */ }
                }
            }
            catch { /* best-effort cleanup */ }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _directoriesCleaned.Remove(_logDir);
            }
        }
    }
}
