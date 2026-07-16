using System;

namespace GithubLauncher.Services.Logging
{
    /// <summary>
    /// ILogger implementation that writes log entries to Console.Error.
    /// Useful when running from the terminal via `dotnet exec` or `dotnet run`.
    /// </summary>
    public class ConsoleLogger : ILogger
    {
        private readonly string _category;

        public ConsoleLogger(string category = "")
        {
            _category = category;
        }

        public bool IsEnabled(LogLevel level) => true;

        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
            Console.Error.WriteLine($"[{timestamp}] [{prefix}]{cat} {message}");

            if (exception != null)
            {
                Console.Error.WriteLine($"[{timestamp}] [{prefix}]{cat} Exception: {exception.GetType().Name}: {exception.Message}");
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
