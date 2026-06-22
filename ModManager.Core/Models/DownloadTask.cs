using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using ModManager.Core.Enums;
using ModManager.Core.Helpers;

namespace ModManager.Core.Models
{
    public class DownloadTask : INotifyPropertyChanged
    {
        private string _status = "";
        private string _title = "";
        private string _speed = "";
        private double _progress;
        private long _downloaded;
        private long _total = 1;
        private DownloadProgressStatus _progressStatus;
        private string? _errorReason;
        private bool _isSelected;

        public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
        public string Url { get; init; } = "";
        public string Character { get; init; } = "";
        public CancellationTokenSource? Cts { get; set; }

        /// <summary>行选中状态</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropChanged(); }
        }

        /// <summary>
        /// Percent as double for binding to ProgressBar.Value
        /// </summary>
        public double PercentDouble => Percent.HasValue ? (double)Percent.Value : 0.0;

        // === 基础属性 (保持与旧版兼容) ===

        public string Title { get => _title; set { _title = value; OnPropChanged(); } }

        /// <summary>字符串状态（保持向后兼容）</summary>
        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                // 自动同步枚举状态
                _progressStatus = value switch
                {
                    "start" => DownloadProgressStatus.Added,
                    "downloading" => DownloadProgressStatus.Downloading,
                    "extracting" => DownloadProgressStatus.Extracting,
                    "done" => DownloadProgressStatus.Finished,
                    "error" => DownloadProgressStatus.Error,
                    "cancelled" => DownloadProgressStatus.Cancelled,
                    "paused" => DownloadProgressStatus.Paused,
                    "retrying" => DownloadProgressStatus.Retrying,
                    "resuming" => DownloadProgressStatus.Resuming,
                    _ => DownloadProgressStatus.Added
                };
                _statusBrush = null; _progressBrush = null;
                OnPropChanged();
                OnPropChanged(nameof(ProgressStatus));
                OnPropChanged(nameof(Icon));
                OnPropChanged(nameof(StatusText));
                OnPropChanged(nameof(StatusColor));
                OnPropChanged(nameof(StatusDisplayText));
                OnPropChanged(nameof(IsIndeterminate));
                OnPropChanged(nameof(HasProgressBar));
                OnPropChanged(nameof(IsComplete));
                OnPropChanged(nameof(StatusTextColor));
                OnPropChanged(nameof(StatusTextBrush));
                OnPropChanged(nameof(ProgressGradientBrush));
                OnPropChanged(nameof(GradientStartColor));
                OnPropChanged(nameof(GradientEndColor));
            }
        }

        /// <summary>枚举状态</summary>
        public DownloadProgressStatus ProgressStatus
        {
            get => _progressStatus;
            set
            {
                _progressStatus = value;
                _status = value.ToDisplayString();
                _statusBrush = null; _progressBrush = null;
                OnPropChanged();
                OnPropChanged(nameof(Status));
                OnPropChanged(nameof(Icon));
                OnPropChanged(nameof(StatusText));
                OnPropChanged(nameof(StatusColor));
                OnPropChanged(nameof(StatusDisplayText));
                OnPropChanged(nameof(IsIndeterminate));
                OnPropChanged(nameof(HasProgressBar));
                OnPropChanged(nameof(IsComplete));
                OnPropChanged(nameof(StatusTextColor));
                OnPropChanged(nameof(StatusTextBrush));
                OnPropChanged(nameof(ProgressGradientBrush));
                OnPropChanged(nameof(GradientStartColor));
                OnPropChanged(nameof(GradientEndColor));
            }
        }

        public string Speed { get => _speed; set { _speed = value; OnPropChanged(); OnPropChanged(nameof(HasSpeed)); OnPropChanged(nameof(SpeedDisplayText)); OnPropChanged(nameof(Eta)); OnPropChanged(nameof(EtaDisplayText)); } }
        public double Progress { get => _progress; set { _progress = value; OnPropChanged(); OnPropChanged(nameof(Percent)); OnPropChanged(nameof(PercentDouble)); OnPropChanged(nameof(ProgressText)); OnPropChanged(nameof(IsComplete)); OnPropChanged(nameof(IsIndeterminate)); OnPropChanged(nameof(HasProgressBar)); OnPropChanged(nameof(StatusDisplayText)); } }
        public long DownloadedBytes { get => _downloaded; set { _downloaded = value; OnPropChanged(); OnPropChanged(nameof(Percent)); OnPropChanged(nameof(PercentDouble)); OnPropChanged(nameof(ProgressText)); OnPropChanged(nameof(SizeDisplayText)); OnPropChanged(nameof(Eta)); OnPropChanged(nameof(EtaDisplayText)); OnPropChanged(nameof(StatusDisplayText)); OnPropChanged(nameof(IsIndeterminate)); OnPropChanged(nameof(HasProgressBar)); } }
        public long TotalBytes { get => _total; set { _total = value; OnPropChanged(); OnPropChanged(nameof(Percent)); OnPropChanged(nameof(PercentDouble)); OnPropChanged(nameof(ProgressText)); OnPropChanged(nameof(SizeDisplayText)); OnPropChanged(nameof(Eta)); OnPropChanged(nameof(EtaDisplayText)); OnPropChanged(nameof(StatusDisplayText)); OnPropChanged(nameof(IsIndeterminate)); OnPropChanged(nameof(HasProgressBar)); } }

        // === 错误信息 ===

        private string? _statusOverride;
        public void SetError(string msg) { _errorReason = msg; _statusOverride = msg; OnPropChanged(nameof(StatusText)); OnPropChanged(nameof(StatusDisplayText)); }

        // === 新增属性 ===

        /// <summary>文件类别（压缩包/图片/视频/...)</summary>
        public string Category { get; set; } = "未知";

        /// <summary>文件图标 emoji</summary>
        public string FileIcon { get; set; } = "\U0001F4C1";

        /// <summary>任务添加时间</summary>
        public DateTime DateAdded { get; init; } = DateTime.Now;

        /// <summary>相对时间文本（如 "2分钟前"），由 DispatcherTimer 刷新</summary>
        private string _relativeTimeText = "";
        public string RelativeTimeText
        {
            get => _relativeTimeText;
            set { _relativeTimeText = value; OnPropChanged(); }
        }

        private string _timeLeftText = "";
        /// <summary>预估剩余时间文本</summary>
        public string TimeLeftText
        {
            get => _timeLeftText;
            set { _timeLeftText = value; OnPropChanged(); }
        }

        // === 派生属性（状态驱动） ===

        /// <summary>百分比 (0-100, null = 不确定)</summary>
        public int? Percent
        {
            get
            {
                if (TotalBytes <= 0) return null;
                if (ProgressStatus == DownloadProgressStatus.Added) return null;
                var pct = (int)Math.Round(Progress * 100);
                return Math.Clamp(pct, 0, 100);
            }
        }

        /// <summary>文件图标 emoji (状态感知)</summary>
        public string Icon => ProgressStatus switch
        {
            DownloadProgressStatus.Finished => "✅",   // ✅
            DownloadProgressStatus.Error or DownloadProgressStatus.Cancelled => "❌", // ❌
            DownloadProgressStatus.Extracting => "\U0001F4E6", // 📦
            _ => "⬇"   // ⬇
        };

        /// <summary>是否正在下载（活跃中）</summary>
        public bool IsDownloading => ProgressStatus.IsActive();

        /// <summary>是否为终态</summary>
        public bool IsTerminal => ProgressStatus.IsTerminal();

        /// <summary>是否是不确定进度（percent==null 且正在下载）</summary>
        public bool IsIndeterminate => Percent == null
            && (ProgressStatus == DownloadProgressStatus.Downloading
                || ProgressStatus == DownloadProgressStatus.Resuming
                || ProgressStatus == DownloadProgressStatus.CreatingFile);

        /// <summary>是否显示进度条</summary>
        public bool HasProgressBar => ProgressStatus != DownloadProgressStatus.Added;

        /// <summary>下载完成</summary>
        public bool IsComplete => Percent >= 100 || ProgressStatus == DownloadProgressStatus.Finished;

        /// <summary>是否有速度数据</summary>
        public bool HasSpeed => !string.IsNullOrEmpty(Speed) && Speed != "0 B/s";

        // === 状态文字 ===

        /// <summary>状态文字: "45% 下载中" 或 "失败: timeout"</summary>
        public string StatusDisplayText
        {
            get
            {
                if (_statusOverride != null) return _statusOverride;
                if (ProgressStatus == DownloadProgressStatus.Error && !string.IsNullOrEmpty(_errorReason))
                    return _errorReason;
                if (Percent != null && ProgressStatus.IsActive())
                    return $"{Percent}% {ProgressStatus.ToDisplayString()}";
                return ProgressStatus.ToDisplayString();
            }
        }

        /// <summary>简化状态文本（向后兼容）</summary>
        public string StatusText =>
            _statusOverride
            ?? (ProgressStatus == DownloadProgressStatus.Finished ? "已完成"
                : ProgressStatus == DownloadProgressStatus.Extracting ? "解压中"
                : ProgressStatus == DownloadProgressStatus.Error ? "失败"
                : ProgressStatus == DownloadProgressStatus.Cancelled ? "已取消"
                : ProgressStatus.ToDisplayString());

        public string StatusColor =>
            ProgressStatus == DownloadProgressStatus.Finished ? "#00CC66"
            : ProgressStatus == DownloadProgressStatus.Error || ProgressStatus == DownloadProgressStatus.Cancelled ? "#DC2626"
            : "#FFFFFF";

        /// <summary>状态文字前景色（Windows.UI.Color）</summary>
        public Windows.UI.Color StatusTextColor => ProgressStatus switch
        {
            DownloadProgressStatus.Error or DownloadProgressStatus.Retrying or DownloadProgressStatus.Cancelled
                => Windows.UI.Color.FromArgb(255, 234, 76, 60),
            DownloadProgressStatus.Paused or DownloadProgressStatus.Added
                => Windows.UI.Color.FromArgb(255, 246, 194, 68),
            DownloadProgressStatus.Finished
                => Windows.UI.Color.FromArgb(255, 69, 179, 107),
            _ => Windows.UI.Color.FromArgb(255, 255, 255, 255)
        };

        /// <summary>状态文字画刷（缓存复用，避免每次 x:Bind 计算时 new）</summary>
        private Microsoft.UI.Xaml.Media.SolidColorBrush? _statusBrush;
        public Microsoft.UI.Xaml.Media.SolidColorBrush StatusTextBrush
        {
            get
            {
                _statusBrush ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(StatusTextColor);
                return _statusBrush;
            }
        }

        /// <summary>进度条渐变色画刷（缓存复用）</summary>
        private Microsoft.UI.Xaml.Media.LinearGradientBrush? _progressBrush;
        public Microsoft.UI.Xaml.Media.LinearGradientBrush ProgressGradientBrush
        {
            get
            {
                if (_progressBrush == null)
                {
                    var (start, end) = StatusGradientProvider.GetStatusGradient(ProgressStatus);
                    _progressBrush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0),
                        EndPoint = new Windows.Foundation.Point(1, 0)
                    };
                    _progressBrush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = start, Offset = 0 });
                    _progressBrush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = end, Offset = 1 });
                }
                return _progressBrush;
            }
        }

        /// <summary>进度条渐变起始色</summary>
        public Windows.UI.Color GradientStartColor
        {
            get
            {
                var (start, _) = StatusGradientProvider.GetStatusGradient(ProgressStatus);
                return start;
            }
        }

        /// <summary>进度条渐变结束色</summary>
        public Windows.UI.Color GradientEndColor
        {
            get
            {
                var (_, end) = StatusGradientProvider.GetStatusGradient(ProgressStatus);
                return end;
            }
        }

        // === 显示文字 ===

        public string ProgressText => TotalBytes > 0
            ? $"{FormatSize(DownloadedBytes)} / {FormatSize(TotalBytes)}"
            : "";

        public string SizeDisplayText => TotalBytes > 0 ? FormatSize(TotalBytes) : "";

        public string SpeedDisplayText => HasSpeed ? Speed : "";

        public string EtaDisplayText
        {
            get
            {
                if (string.IsNullOrEmpty(TimeLeftText)) return "";
                return $"剩余 {TimeLeftText}";
            }
        }

        /// <summary>预估剩余时间 (向后兼容)</summary>
        public string Eta => TotalBytes > 0 && DownloadedBytes > 0 && !string.IsNullOrEmpty(Speed)
            ? TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / Math.Max(DownloadedBytes, 1)).ToString(@"mm\:ss")
            : "";

        /// <summary>绝对时间 (向后兼容)</summary>
        public string Time { get; init; } = DateTime.Now.ToString("HH:mm");

        /// <summary>日期时间显示文本</summary>
        public string DateAddedDisplayText => DateAdded.ToString("MM-dd HH:mm");

        private static string FormatSize(long bytes) => bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F0} KB",
            _ => $"{bytes} B"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
