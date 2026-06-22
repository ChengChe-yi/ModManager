using System;

namespace ModManager.Core.Enums
{
    /// <summary>
    /// 下载任务状态枚举。
    /// 等价 ABDM Compose 的 ProgressStatus sealed class 层次结构。
    /// </summary>
    public enum DownloadProgressStatus
    {
        /// <summary>刚刚添加到队列，等待下载</summary>
        Added,

        /// <summary>正在创建输出文件</summary>
        CreatingFile,

        /// <summary>正在恢复下载</summary>
        Resuming,

        /// <summary>正在下载中</summary>
        Downloading,

        /// <summary>正在解压 zip 文件</summary>
        Extracting,

        /// <summary>已暂停</summary>
        Paused,

        /// <summary>下载完成</summary>
        Finished,

        /// <summary>下载出错</summary>
        Error,

        /// <summary>正在重试</summary>
        Retrying,

        /// <summary>已取消</summary>
        Cancelled
    }

    /// <summary>
    /// DownloadProgressStatus 扩展方法。
    /// </summary>
    public static class DownloadProgressStatusExtensions
    {
        /// <summary>获取中文显示名称</summary>
        public static string ToDisplayString(this DownloadProgressStatus status) => status switch
        {
            DownloadProgressStatus.Added => "等待中",
            DownloadProgressStatus.CreatingFile => "创建文件",
            DownloadProgressStatus.Resuming => "恢复中",
            DownloadProgressStatus.Downloading => "下载中",
            DownloadProgressStatus.Extracting => "解压中",
            DownloadProgressStatus.Paused => "已暂停",
            DownloadProgressStatus.Finished => "已完成",
            DownloadProgressStatus.Error => "失败",
            DownloadProgressStatus.Retrying => "重试中",
            DownloadProgressStatus.Cancelled => "已取消",
            _ => status.ToString()
        };

        /// <summary>是否为终态（完成/错误/取消）</summary>
        public static bool IsTerminal(this DownloadProgressStatus status) => status switch
        {
            DownloadProgressStatus.Finished or DownloadProgressStatus.Error or DownloadProgressStatus.Cancelled => true,
            _ => false
        };

        /// <summary>是否为活跃状态（正在下载/解压/恢复/创建文件）</summary>
        public static bool IsActive(this DownloadProgressStatus status) => status switch
        {
            DownloadProgressStatus.Downloading or DownloadProgressStatus.Extracting
                or DownloadProgressStatus.Resuming or DownloadProgressStatus.CreatingFile => true,
            _ => false
        };
    }
}
