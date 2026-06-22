namespace ModManager.Core.Models
{
    /// <summary>通知类型，决定图标和默认持续时间</summary>
    public enum NotificationType
    {
        /// <summary>信息通知 — 5 秒自动关闭</summary>
        Info,

        /// <summary>成功通知 — 5 秒自动关闭，绿色图标</summary>
        Success,

        /// <summary>警告通知 — 8 秒自动关闭，橙色图标</summary>
        Warning,

        /// <summary>错误通知 — 不自动关闭，需手动确认</summary>
        Error
    }
}
