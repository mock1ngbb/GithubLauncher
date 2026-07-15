using System;

namespace GithubLauncher.Services.Logging
{
    public class DebugLogger : ILogger
    {
        private readonly string _category;

        public DebugLogger(string category = "")
        {
            _category = category;
        }

        public bool IsEnabled(LogLevel level) => true;

        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            var prefix = level switch
            {
                LogLevel.Trace => "[TRC]",
                LogLevel.Debug => "[DBG]",
                LogLevel.Info => "[INF]",
                LogLevel.Warn => "[WRN]",
                LogLevel.Error => "[ERR]",
                LogLevel.Fatal => "[FTL]",
                _ => "[?]"
            };

            var cat = string.IsNullOrEmpty(_category) ? "" : $" [{_category}]";
            System.Diagnostics.Debug.WriteLine($"{prefix}{cat} {message}");

            if (exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"{prefix}{cat} Exception: {exception.GetType().Name}: {exception.Message}");
                System.Diagnostics.Debug.WriteLine($"{prefix}{cat} StackTrace: {exception.StackTrace}");
            }
        }

        public void Trace(string message) => Log(LogLevel.Trace, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
        public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
    }
}
