using System;
using System.IO;
using System.Text;
using ModManager.Core.Constants;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Models;

namespace ModManager.Application.Services
{
    public class AppLogger : IAppLogger
    {
        private readonly string _logDir;
        private readonly INotificationService? _notif;

        public AppLogger(INotificationService? notif = null)
        {
            _logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(_logDir);
            _notif = notif;
            CleanOldLogs(7);
        }

        private void CleanOldLogs(int keepDays)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-keepDays);
                foreach (var f in Directory.GetFiles(_logDir, $"{FileNames.AppLogPrefix}*{FileNames.AppLogExtension}"))
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
            catch { /* 清理失败不影响正常日志 */ }
        }

        public void Error(string message, Exception? ex = null, bool notify = true)
        {
            var sb = new StringBuilder();
            sb.Append($"[ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}");
            if (ex != null) sb.Append($" | {ex.GetType().Name}: {ex.Message}");
            Write(sb.ToString());
            if (notify)
                _notif?.Show("错误", message, NotificationType.Error);
        }

        public void Warn(string message, bool notify = false)
        {
            Write($"[WARN ] {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}");
            if (notify)
                _notif?.Show("警告", message, NotificationType.Warning);
        }

        public void Info(string message)
        {
            Write($"[INFO ] {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}");
        }

        private void Write(string line)
        {
            try
            {
                var path = Path.Combine(_logDir, $"{FileNames.AppLogPrefix}{DateTime.Now:yyyyMMdd}{FileNames.AppLogExtension}");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* 日志写入失败不抛异常 */ }
        }
    }
}
