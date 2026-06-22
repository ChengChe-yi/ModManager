using ModManager.Core.Models;

namespace ModManager.Core.Contracts.Services
{
    /// <summary>
    /// Win11 风格应用内通知服务接口。
    /// 通知从屏幕右下角滑入，支持自动消失和手动关闭。
    /// 要使用 UI 初始化功能，请通过 DI 解析后转换为 NotificationService 具体类。
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// 显示一条通知。使用默认持续时间和图标。
        /// </summary>
        /// <param name="title">标题</param>
        /// <param name="message">正文</param>
        /// <param name="type">类型（决定图标和自动关闭时长）</param>
        void Show(string title, string message, NotificationType type = NotificationType.Info);

        /// <summary>
        /// 显示一条自定义通知。
        /// </summary>
        /// <param name="item">完整的通知数据</param>
        void Show(NotificationItem item);
    }
}
