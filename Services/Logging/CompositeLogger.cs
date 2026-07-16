using System;
using System.Collections.Generic;

namespace GithubLauncher.Services.Logging
{
    /// <summary>
    /// ILogger that fans out to multiple inner loggers.
    /// Routes every log call to all registered loggers.
    /// </summary>
    public class CompositeLogger : ILogger
    {
        private readonly List<ILogger> _loggers = new();

        public CompositeLogger(params ILogger[] loggers)
        {
            _loggers.AddRange(loggers);
        }

        public void Add(ILogger logger)
        {
            lock (_loggers)
            {
                _loggers.Add(logger);
            }
        }

        public bool IsEnabled(LogLevel level)
        {
            lock (_loggers)
            {
                foreach (var logger in _loggers)
                    if (logger.IsEnabled(level)) return true;
            }
            return false;
        }

        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            lock (_loggers)
            {
                foreach (var logger in _loggers)
                {
                    try { logger.Log(level, message, exception); }
                    catch { /* isolated logger failure */ }
                }
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
