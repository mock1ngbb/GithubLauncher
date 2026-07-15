using System;

namespace GithubLauncher.Services.Logging
{
    public static class Log
    {
        private static ILogger _instance = new DebugLogger("Global");

        public static ILogger Instance => _instance;

        public static void SetLogger(ILogger logger)
        {
            _instance = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static void Trace(string message) => _instance.Trace(message);
        public static void Debug(string message) => _instance.Debug(message);
        public static void Info(string message) => _instance.Info(message);
        public static void Warn(string message) => _instance.Warn(message);
        public static void Error(string message, Exception? exception = null) => _instance.Error(message, exception);
        public static void Fatal(string message, Exception? exception = null) => _instance.Fatal(message, exception);
    }
}
