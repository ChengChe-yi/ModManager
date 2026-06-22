using System;
using ModManager.Core.Contracts.Services;

namespace ModManager.Core.Helpers
{
    /// <summary>静态日志门面，无需 DI 即可使用</summary>
    public static class Log
    {
        private static IAppLogger? _instance;
        public static void Init(IAppLogger instance) => _instance = instance;

        public static void Error(string message, Exception? ex = null, bool notify = true)
            => _instance?.Error(message, ex, notify);

        public static void Warn(string message, bool notify = false)
            => _instance?.Warn(message, notify);

        public static void Info(string message)
            => _instance?.Info(message);
    }
}
