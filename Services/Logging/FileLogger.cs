using System;
using System.IO;
using System.Collections.Generic;

namespace GithubLauncher.Services.Logging
{
    public class FileLogger : ILogger, IDisposable
    {
        private static readonly object _lock = new();
        private static readonly HashSet<string> _directoriesCleaned = new();
        private readonly string _category;
        private readonly string _logDir;
        private readonly long _maxFileSize = 10 * 1024 * 1024;
        private readonly int _maxRetentionDays = 7;

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
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var pfx = level switch { LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warn => "WRN", LogLevel.Error => "ERR", LogLevel.Fatal => "FTL", _ => "???" };
            var cat = string.IsNullOrEmpty(_category) ? "" : $" [{_category}]";
            lock (_lock)
            {
                var fp = GetCurrentLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
                if (File.Exists(fp) && new FileInfo(fp).Length > _maxFileSize) fp = RotateLogFile(fp);
                File.AppendAllText(fp, $"[{ts}] [{pfx}]{cat} {message}\n");
                if (exception != null)
                {
                    File.AppendAllText(fp, $"[{ts}] [{pfx}]{cat} Exception: {exception.GetType().Name}: {exception.Message}\n");
                    File.AppendAllText(fp, $"[{ts}] [{pfx}]{cat} StackTrace: {exception.StackTrace}\n");
                }
            }
        }
        public void Trace(string message) => Log(LogLevel.Trace, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
        public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
        private string GetCurrentLogPath() => Path.Combine(_logDir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
        private string RotateLogFile(string orig)
        {
            var dir = Path.GetDirectoryName(orig)!;
            var baseName = Path.GetFileNameWithoutExtension(orig);
            var ext = Path.GetExtension(orig);
            for (int i = 1; ; i++) { var r = Path.Combine(dir, $"{baseName}-{i}{ext}"); if (!File.Exists(r)) { File.Move(orig, r); return orig; } }
        }
        private void CleanupOldLogs()
        {
            try { if (!Directory.Exists(_logDir)) return; var cutoff = DateTime.Now.AddDays(-_maxRetentionDays); foreach (var f in Directory.GetFiles(_logDir, "app-*.log")) { try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { } } } catch { }
        }
        public void Dispose() { lock (_lock) { _directoriesCleaned.Remove(_logDir); } }
    }
}
