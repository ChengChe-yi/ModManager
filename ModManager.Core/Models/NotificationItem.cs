using System;

namespace ModManager.Core.Models
{
    /// <summary>
    /// 单条通知的完整数据，供 NotificationService 和 NotificationPopup 使用。
    /// </summary>
    public class NotificationItem
    {
        /// <summary>通知标题（粗体）</summary>
        public string Title { get; set; } = "";

        /// <summary>通知正文</summary>
        public string Message { get; set; } = "";

        /// <summary>通知类型</summary>
        public NotificationType Type { get; set; } = NotificationType.Info;

        /// <summary>自动消失时长。Error 类型为 TimeSpan.Zero 时永不自动关闭。</summary>
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>关闭回调（供 NotificationService 使用）</summary>
        public Action? DismissCallback { get; set; }

        /// <summary>悬停时暂停自动关闭回调</summary>
        public Action? PauseTimerCallback { get; set; }

        /// <summary>离开悬停时恢复自动关闭回调</summary>
        public Action? ResumeTimerCallback { get; set; }

        /// <summary>通知优先级，用于排序（数字越小越靠前显示）</summary>
        public int Priority =>
            Type switch
            {
                NotificationType.Error => 0,
                NotificationType.Warning => 1,
                NotificationType.Success => 2,
                _ => 3
            };
    }
}
