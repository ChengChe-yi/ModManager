using System;
using ModManager.Core.Enums;
using Windows.UI;

namespace ModManager.Core.Helpers
{
    /// <summary>
    /// 根据下载状态返回对应的渐变起止色。
    /// 等价 Compose MyColors.xxxGradient 系列。
    /// </summary>
    public static class StatusGradientProvider
    {
        // Primary 蓝紫渐变 — Downloading
        public static readonly (Color Start, Color End) PrimaryGradient = (
            Color.FromArgb(255, 71, 145, 191),   // #4791BF
            Color.FromArgb(255, 184, 93, 255)    // #B85DFF
        );

        // Success 绿色渐变 — Finished
        public static readonly (Color Start, Color End) SuccessGradient = (
            Color.FromArgb(255, 69, 179, 107),   // #45B36B
            Color.FromArgb(255, 45, 143, 76)     // #2D8F4C
        );

        // Error 红色渐变 — Error / Retrying / Cancelled
        public static readonly (Color Start, Color End) ErrorGradient = (
            Color.FromArgb(255, 234, 76, 60),    // #EA4C3C
            Color.FromArgb(255, 192, 57, 43)     // #C0392B
        );

        // Warning 黄色渐变 — Paused / Added
        public static readonly (Color Start, Color End) WarningGradient = (
            Color.FromArgb(255, 246, 194, 68),    // #F6C244
            Color.FromArgb(255, 212, 160, 23)     // #D4A017
        );

        // Info 蓝色渐变 — CreatingFile / Resuming
        public static readonly (Color Start, Color End) InfoGradient = (
            Color.FromArgb(255, 64, 169, 243),    // #40A9F3
            Color.FromArgb(255, 46, 134, 193)     // #2E86C1
        );

        /// <summary>
        /// 根据状态返回对应的渐变起止色。
        /// 等价 Compose ProgressAndPercent 中 when(status) 的 gradient 分支。
        /// </summary>
        public static (Color Start, Color End) GetStatusGradient(DownloadProgressStatus status) => status switch
        {
            DownloadProgressStatus.Error or DownloadProgressStatus.Retrying or DownloadProgressStatus.Cancelled
                => ErrorGradient,
            DownloadProgressStatus.Paused or DownloadProgressStatus.Added
                => WarningGradient,
            DownloadProgressStatus.CreatingFile or DownloadProgressStatus.Resuming
                => InfoGradient,
            DownloadProgressStatus.Downloading
                => PrimaryGradient,
            DownloadProgressStatus.Finished
                => SuccessGradient,
            _ => PrimaryGradient
        };

        /// <summary>根据状态返回状态文字颜色（Windows.UI.Color）</summary>
        public static Color GetStatusTextColor(DownloadProgressStatus status) => status switch
        {
            DownloadProgressStatus.Error or DownloadProgressStatus.Retrying or DownloadProgressStatus.Cancelled
                => Color.FromArgb(255, 234, 76, 60),   // errorRed
            DownloadProgressStatus.Paused or DownloadProgressStatus.Added
                => Color.FromArgb(255, 246, 194, 68),  // warningYellow
            DownloadProgressStatus.Finished
                => Color.FromArgb(255, 69, 179, 107),  // successGreen
            _ => Color.FromArgb(255, 255, 255, 255)    // defaultWhite
        };

        /// <summary>根据文件扩展名推断类别</summary>
        public static string GetCategoryFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "Unknown";
            var ext = ".zip";
            try { ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath)?.ToLowerInvariant() ?? ".zip"; }
            catch { return "Unknown"; }

            return ext switch
            {
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst" => "压缩包",
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tiff" => "图片",
                ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" => "视频",
                ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".wma" => "音频",
                ".ini" or ".cfg" or ".json" or ".xml" or ".yaml" or ".yml" or ".toml" => "配置文件",
                ".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" => "程序",
                ".txt" or ".md" or ".log" or ".csv" => "文本",
                _ => "其他"
            };
        }

        /// <summary>根据类别返回文件图标 emoji</summary>
        public static string GetCategoryIcon(string category) => category switch
        {
            "压缩包" => "\U0001F4E6",  // 📦
            "图片" => "\U0001F5BC",    // 🖼
            "视频" => "\U0001F3AC",    // 🎬
            "音频" => "\U0001F3B5",    // 🎵
            "配置文件" => "⚙️",  // ⚙️
            "程序" => "\U0001F4BB",    // 💻
            "文本" => "\U0001F4C4",    // 📄
            _ => "\U0001F4C1"          // 📁
        };
    }
}
