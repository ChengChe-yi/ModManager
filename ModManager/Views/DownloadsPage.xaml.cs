using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ModManager.Animations;
using ModManager.Application.Services;
using ModManager.Core.Enums;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Helpers;
using ModManager.Core.Models;

namespace ModManager.Views
{
    public sealed partial class DownloadsPage : Page
    {
        // taskId → controller（for ApplyProgressState 快速查找）
        private readonly Dictionary<string, ProgressBarController> _progressBars = new();
        // Grid host → taskId（for ListView 容器复用时清理旧 controller）
        private readonly Dictionary<Grid, string> _hostTaskMap = new();
        private readonly Dictionary<string, PropertyChangedEventHandler> _subscriptions = new();
        private DispatcherTimer? _relativeTimeTimer;

        public DownloadsPage()
        {
            InitializeComponent();

            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;

            TaskList.ItemsSource = ModHttpServer.Tasks;

            ModHttpServer.Tasks.CollectionChanged += (_, _) =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    RefreshVisibility();
                    RefreshBatchToolbar();
                });

            ModHttpServer.TaskUpdated += OnTaskUpdated;

            // Material Ripple：用 handledEventsToo 穿透 ListViewItem 的事件拦截
            TaskList.ContainerContentChanging += (_, args) =>
            {
                if (args.InRecycleQueue) return;
                if (args.ItemContainer is ListViewItem lvi)
                    lvi.AddHandler(UIElement.PointerPressedEvent,
                        new Microsoft.UI.Xaml.Input.PointerEventHandler(Item_PointerPressed), true);
            };
        }

        // ================================================================
        // 页面生命周期
        // ================================================================

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshVisibility();

                // 隐式颜色动画：所有 Brush 颜色变化自动 500ms 过渡
                try { ImplicitColorAnimation.RegisterForElement(this); } catch { }

                InitializeExistingTasks();
                StartRelativeTimeTimer();

            }
            catch { /* 页面加载异常不传播 */ }
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            StopRelativeTimeTimer();
            CleanupAllAnimations();
        }

        // ================================================================
        // 入场动画：新行 Loaded 时渐入 + 上移
        // ================================================================

        private void TaskList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is not ListViewItem container) return;

            // 只对第一次显示的项播放入场动画
            if (container.Tag is not null) return;
            container.Tag = "shown";

            try
            {
                container.Opacity = 0;
                container.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform { Y = 8 };

                var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var opacityAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    To = 1, Duration = TimeSpan.FromSeconds(0.28)
                };
                var transAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    To = 0, Duration = TimeSpan.FromSeconds(0.28)
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opacityAnim, container);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opacityAnim, "Opacity");
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(transAnim, container);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(transAnim,
                    "(UIElement.RenderTransform).(TranslateTransform.Y)");
                sb.Children.Add(opacityAnim);
                sb.Children.Add(transAnim);
                sb.Begin();
            }
            catch { /* 动画失败不影响功能 */ }
        }

        // ================================================================
        // 光晕（随鼠标移动的径向渐变，统一 CharacterPage 风格）
        // ================================================================

        private Ellipse? _activeGlow;
        private TranslateTransform? _activeGlowTransform;

        private void DownloadRow_GlowEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            _activeGlow = FindChildByName(grid, "HoverGlow") as Ellipse;
            _activeGlowTransform = _activeGlow?.RenderTransform as TranslateTransform;
            if (_activeGlow == null) return;
            _activeGlow.Opacity = 1;
            UpdateGlowPosition(grid, e);
        }

        private void DownloadRow_GlowExited(object sender, PointerRoutedEventArgs e)
        {
            if (_activeGlow != null) _activeGlow.Opacity = 0;
            _activeGlow = null;
            _activeGlowTransform = null;
        }

        private void DownloadRow_GlowMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_activeGlow != null && sender is Grid grid)
                UpdateGlowPosition(grid, e);
        }

        private void UpdateGlowPosition(Grid grid, PointerRoutedEventArgs e)
        {
            if (_activeGlowTransform == null) return;
            var pt = e.GetCurrentPoint(grid);
            _activeGlowTransform.X = pt.Position.X;
            _activeGlowTransform.Y = pt.Position.Y;
        }

        // ================================================================
        // 点击波纹（Material Ripple：从触点扩散的圆 + 淡出）
        // ================================================================

        /// <summary>
        /// Material Ripple：从触点向最远角扩散的圆，半径 0→对角线，400ms 减速，10% 白。
        /// </summary>
        private void Item_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var host = FindChildByName((DependencyObject)sender, "RippleHost") as Canvas;
            if (host == null || host.ActualWidth <= 0) return;

            var pt = e.GetCurrentPoint(host).Position;
            double w = host.ActualWidth, h = host.ActualHeight;
            double x = pt.X, y = pt.Y;

            double targetR = new[]
            {
                Math.Sqrt(x * x + y * y),
                Math.Sqrt((w - x) * (w - x) + y * y),
                Math.Sqrt(x * x + (h - y) * (h - y)),
                Math.Sqrt((w - x) * (w - x) + (h - y) * (h - y))
            }.Max();

            var ripple = new Ellipse
            {
                Width = targetR * 2, Height = targetR * 2,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(38, 255, 255, 255)),
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = 0, ScaleY = 0 }
            };
            Canvas.SetLeft(ripple, x - targetR);
            Canvas.SetTop(ripple, y - targetR);
            host.Children.Add(ripple);

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var ease = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };

            var scaleX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
            var scaleY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
            var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(550) };

            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleX, ripple);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleY, ripple);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, ripple);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");

            var task = ripple;
            sb.Completed += (_, _) => { try { host.Children.Remove(task); } catch { } };
            sb.Children.Add(scaleX);
            sb.Children.Add(scaleY);
            sb.Children.Add(fade);
            sb.Begin();
        }

        private static DependencyObject? FindChildByName(DependencyObject parent, string name)
        {
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && fe.Name == name) return fe;
                var found = FindChildByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // ================================================================
        // 批量选择
        // ================================================================

        private void RefreshBatchToolbar()
        {
            int selected = ModHttpServer.Tasks.Count(t => t.IsSelected);
            int total = ModHttpServer.Tasks.Count;
            // 避免事件重入
            SelectAllCheckBox.Checked -= SelectAll_Checked;
            SelectAllCheckBox.Unchecked -= SelectAll_Unchecked;
            SelectAllCheckBox.IsChecked = total > 0 && selected == total;
            SelectAllCheckBox.Checked += SelectAll_Checked;
            SelectAllCheckBox.Unchecked += SelectAll_Unchecked;
        }

        private void SelectAll_Checked(object sender, RoutedEventArgs e)
        {
            foreach (var t in ModHttpServer.Tasks)
                t.IsSelected = true;
        }

        private void SelectAll_Unchecked(object sender, RoutedEventArgs e)
        {
            foreach (var t in ModHttpServer.Tasks)
                t.IsSelected = false;
        }

        private void BatchClear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in ModHttpServer.Tasks)
                t.IsSelected = false;
            RefreshBatchToolbar();
        }

        // ================================================================
        // 动画 1+2: 进度条控制器（问题 3 修复）
        // ================================================================

        /// <summary>
        /// ProgressBarHost 加载时创建控制器。
        /// 处理 ListView 虚拟化容器复用：同一 Grid 可能被不同 task 复用，
        /// 此时先清理旧 Controller 再建新的。
        /// </summary>
        private void ProgressBarHost_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid host) return;
            if (host.DataContext is not DownloadTask task) return;

            try
            {
                // ListView 虚拟化：如果此 Grid 之前绑过其他 task，先销毁旧 controller
                if (_hostTaskMap.TryGetValue(host, out var oldTaskId) && oldTaskId != task.Id)
                {
                    if (_progressBars.TryGetValue(oldTaskId, out var oldCtrl))
                    {
                        try { oldCtrl.Dispose(); } catch { }
                    }
                    _progressBars.Remove(oldTaskId);
                    _hostTaskMap.Remove(host);
                }

                if (_progressBars.ContainsKey(task.Id)) return;

                var controller = new ProgressBarController(host);
                _progressBars[task.Id] = controller;
                _hostTaskMap[host] = task.Id;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_progressBars.TryGetValue(task.Id, out var ctrl))
                        ApplyProgressState(ctrl, task);
                });

                SubscribeToTaskChanges(task);
            }
            catch { /* 动画初始化失败不影响下载功能 */ }
        }

        private void InitializeExistingTasks()
        {
            foreach (var task in ModHttpServer.Tasks)
            {
                if (!_subscriptions.ContainsKey(task.Id))
                    SubscribeToTaskChanges(task);
            }
        }

        private void SubscribeToTaskChanges(DownloadTask task)
        {
            void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (sender is not DownloadTask t) return;

                switch (e.PropertyName)
                {
                    case nameof(DownloadTask.Progress):
                    case nameof(DownloadTask.ProgressStatus):
                    case nameof(DownloadTask.Percent):
                    case nameof(DownloadTask.IsIndeterminate):
                    case nameof(DownloadTask.GradientStartColor):
                    case nameof(DownloadTask.GradientEndColor):
                    case nameof(DownloadTask.IsComplete):
                    case nameof(DownloadTask.HasProgressBar):
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            try
                            {
                                if (_progressBars.TryGetValue(t.Id, out var ctrl))
                                    ApplyProgressState(ctrl, t);
                            }
                            catch { }
                        });
                        break;

                    case nameof(DownloadTask.IsSelected):
                        DispatcherQueue.TryEnqueue(RefreshBatchToolbar);
                        break;

                    case nameof(DownloadTask.Speed):
                    case nameof(DownloadTask.DownloadedBytes):
                    case nameof(DownloadTask.TotalBytes):
                        // 更新 ETA 等显示文字（触发 Eta/TimeLeftText 刷新）
                        t.TimeLeftText = t.IsDownloading ? t.Eta : "";
                        break;
                }
            }

            _subscriptions[task.Id] = OnTaskPropertyChanged;
            task.PropertyChanged += OnTaskPropertyChanged;
        }

        private void ApplyProgressState(ProgressBarController controller, DownloadTask task)
        {
            var (gradStart, gradEnd) = StatusGradientProvider.GetStatusGradient(task.ProgressStatus);

            if (!task.HasProgressBar)
            {
                // Added / 未开始 → 隐藏填充
                controller.Hide();
            }
            else if (task.IsIndeterminate)
            {
                // 动画 2: 不确定 bouncing bar
                controller.StartIndeterminate(gradStart, gradEnd);
            }
            else
            {
                // 动画 1: 确定进度条
                var percent = Math.Clamp(task.Progress, 0.0, 1.0);
                controller.UpdateProgress(percent, gradStart, gradEnd);
            }
        }

        // ================================================================
        // 动画 8: 相对时间定时器
        // ================================================================

        private void StartRelativeTimeTimer()
        {
            if (_relativeTimeTimer != null) return;
            _relativeTimeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _relativeTimeTimer.Tick += OnRelativeTimeTick;
            _relativeTimeTimer.Start();
            RefreshRelativeTimes();
        }

        private void StopRelativeTimeTimer()
        {
            if (_relativeTimeTimer == null) return;
            _relativeTimeTimer.Stop();
            _relativeTimeTimer.Tick -= OnRelativeTimeTick;
            _relativeTimeTimer = null;
        }

        private void OnRelativeTimeTick(object? sender, object e) => RefreshRelativeTimes();

        private void RefreshRelativeTimes()
        {
            if (ModHttpServer.Tasks.Count == 0) return;
            foreach (var task in ModHttpServer.Tasks)
            {
                task.RelativeTimeText = PrettifyRelativeTime(task.DateAdded);
                if (task.IsDownloading)
                    task.TimeLeftText = task.Eta;
            }
        }

        private static string PrettifyRelativeTime(DateTime dateAdded)
        {
            var span = DateTime.Now - dateAdded;
            if (span.TotalSeconds < 10) return "刚刚";
            if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}秒前";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}小时前";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays}天前";
            return dateAdded.ToString("MM-dd");
        }

        // ================================================================
        // TaskUpdated（来自 ModHttpServer）
        // ================================================================

        private void OnTaskUpdated(DownloadTask task)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 确保已订阅 PropertyChanged（x:Bind 自动响应更新）
                if (!_subscriptions.ContainsKey(task.Id))
                    SubscribeToTaskChanges(task);

                if (task.IsComplete)
                    PlayCompletionCheckForTask(task);

                RefreshVisibility();
                RefreshBatchToolbar();
            });
        }

        /// <summary>完成 ✓ 弹入动画（纯 XAML Storyboard）</summary>
        private void PlayCompletionCheckForTask(DownloadTask task)
        {
            try
            {
                foreach (var item in TaskList.Items)
                {
                    if (item is not DownloadTask t || t.Id != task.Id) continue;
                    var container = TaskList.ContainerFromItem(item) as ListViewItem;
                    if (container == null) break;

                    // 搜索 DataTemplate 中的 ✅ TextBlock（已知结构：StackPanel → StackPanel → TextBlock）
                    var checkIcon = FindCompletionCheckIcon(container);
                    if (checkIcon == null) break;

                    // 简单的弹性缩放 Storyboard（替代 Composition SpringAnimation）
                    var scaleTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = 0, ScaleY = 0 };
                    checkIcon.RenderTransform = scaleTransform;
                    checkIcon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                    checkIcon.Opacity = 0;

                    var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                    var scaleX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(350),
                        EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
                    };
                    var scaleY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(350),
                        EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
                    };
                    var opacity = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220)
                    };
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleX, checkIcon);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleY, checkIcon);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opacity, checkIcon);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opacity, "Opacity");
                    sb.Children.Add(scaleX);
                    sb.Children.Add(scaleY);
                    sb.Children.Add(opacity);
                    sb.Begin();
                    break;
                }
            }
            catch { /* 动画失败不影响功能 */ }
        }

        /// <summary>在 ListViewItem 的可视树中查找 ✅ 图标</summary>
        private static TextBlock? FindCompletionCheckIcon(DependencyObject parent)
        {
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock tb && tb.Text == "✅")
                    return tb;
                var found = FindCompletionCheckIcon(child);
                if (found != null) return found;
            }
            return null;
        }

        // ================================================================
        // 按钮事件
        // ================================================================

        private void RefreshVisibility()
        {
            EmptyHint.Visibility = ModHttpServer.Tasks.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ResumeAll_Click(object sender, RoutedEventArgs e)
        {
            // TODO: 实现断点续传后启用
        }

        private void PauseAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in ModHttpServer.Tasks.Where(t => t.IsSelected && !t.IsTerminal).ToList())
            {
                ModHttpServer.CancelTask(t);
            }
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in ModHttpServer.Tasks.Where(t => t.IsSelected).ToList())
            {
                if (!t.IsTerminal) ModHttpServer.CancelTask(t);
                ModHttpServer.RemoveTask(t);
                CleanupTaskAnimations(t);
            }
            RefreshBatchToolbar();
        }

        private void CancelAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in ModHttpServer.Tasks.Where(t => !t.IsTerminal).ToList())
            {
                ModHttpServer.CancelTask(t);
                ModHttpServer.RemoveTask(t);
                CleanupTaskAnimations(t);
            }
            RefreshBatchToolbar();
        }

        private void ClearDone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var t in ModHttpServer.Tasks
                         .Where(t => t.ProgressStatus is DownloadProgressStatus.Finished
                             or DownloadProgressStatus.Error
                             or DownloadProgressStatus.Cancelled).ToList())
            {
                ModHttpServer.RemoveTask(t);
                CleanupTaskAnimations(t);
            }
            RefreshBatchToolbar();
        }

        // ================================================================
        // 清理
        // ================================================================

        private void CleanupTaskAnimations(DownloadTask task)
        {
            if (_progressBars.TryGetValue(task.Id, out var controller))
            {
                controller.Dispose();
                _progressBars.Remove(task.Id);
            }
            // 从 host→task 映射中清除（找到对应的 Grid 条目）
            var hostKey = _hostTaskMap.FirstOrDefault(kv => kv.Value == task.Id).Key;
            if (hostKey != null) _hostTaskMap.Remove(hostKey);

            if (_subscriptions.TryGetValue(task.Id, out var handler))
            {
                task.PropertyChanged -= handler;
                _subscriptions.Remove(task.Id);
            }
        }

        private void CleanupAllAnimations()
        {
            foreach (var kvp in _progressBars)
                kvp.Value.Dispose();
            _progressBars.Clear();
            _hostTaskMap.Clear();

            foreach (var kvp in _subscriptions)
            {
                var task = ModHttpServer.Tasks.FirstOrDefault(t => t.Id == kvp.Key);
                if (task != null)
                    task.PropertyChanged -= kvp.Value;
            }
            _subscriptions.Clear();
        }

    }
}
