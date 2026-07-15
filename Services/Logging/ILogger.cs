using System;

namespace GithubLauncher.Services.Logging
{
    public interface ILogger
    {
        void Log(LogLevel level, string message, Exception? exception = null);
        bool IsEnabled(LogLevel level);
        void Trace(string message);
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception? exception = null);
        void Fatal(string message, Exception? exception = null);
    }
}
