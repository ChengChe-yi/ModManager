using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Models;
using ModManager.Views;
using Windows.UI;

namespace ModManager.Services
{
    /// <summary>
    /// Win11 风格通知中心。卡片嵌入右下角容器堆叠，最多 3 条。
    /// </summary>
    public class NotificationService : INotificationService
    {
        private FrameworkElement? _rootElement;
        private DispatcherQueue? _dispatcherQueue;
        private StackPanel? _notificationPanel;
        private Grid? _notificationContainer;
        private Border? _containerBorder;
        private Storyboard? _containerEntranceSb;
        private Storyboard? _containerExitSb;

        /// <summary>活跃通知列表</summary>
        private readonly List<ActiveNotification> _active = new();

        private const int MaxVisible = 3;
        private const double CardSpacing = 8;

        // ---- INotificationService ----

        public void Show(string title, string message, NotificationType type = NotificationType.Info)
        {
            Show(new NotificationItem
            {
                Title = title,
                Message = message,
                Type = type,
                Duration = GetDefaultDuration(type)
            });
        }

        public void Show(NotificationItem item)
        {
            _dispatcherQueue?.TryEnqueue(() => ShowInternal(item));
        }

        public void Initialize(Grid hostGrid, FrameworkElement rootElement)
        {
            _rootElement = rootElement;
            _notificationContainer = hostGrid;
            _dispatcherQueue = hostGrid.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

            // 从容器内查找子元素
            if (hostGrid.FindName("NotificationPanel") is StackPanel panel)
                _notificationPanel = panel;

            if (hostGrid.FindName("ContainerBorder") is Border border)
                _containerBorder = border;

            if (hostGrid.FindName("ClearAllButton") is Button clearBtn)
                clearBtn.Click += (_, _) => ClearAll();

            // 构建容器的显隐动画
            BuildContainerAnimations();
            UpdateContainerTheme();

            Debug.WriteLine("[NotificationService] 通知中心已初始化");
        }

        public void UpdateTheme()
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                UpdateContainerTheme();
                foreach (var an in _active)
                    an.Control.UpdateTheme(_rootElement);
            });
        }

        private void ClearAll()
        {
            foreach (var an in _active.ToList())
                Dismiss(an);
        }

        // ---- 容器显隐动画 ----

        private void BuildContainerAnimations()
        {
            if (_notificationContainer == null) return;
            var transform = new CompositeTransform();
            _notificationContainer.RenderTransform = transform;
            _notificationContainer.RenderTransformOrigin = new Windows.Foundation.Point(1, 1);

            // 入场：Y 60→0, Opacity 0→1, 300ms EaseOut
            _containerEntranceSb = new Storyboard();
            {
                var ay = new DoubleAnimation
                {
                    From = 60, To = 0,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(ay, _notificationContainer);
                Storyboard.SetTargetProperty(ay, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
                _containerEntranceSb.Children.Add(ay);

                var ao = new DoubleAnimation
                {
                    From = 0, To = 1,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(ao, _notificationContainer);
                Storyboard.SetTargetProperty(ao, "Opacity");
                _containerEntranceSb.Children.Add(ao);
            }

            // 退场：Y 0→40, Opacity 1→0, 200ms EaseIn，完成后隐藏
            _containerExitSb = new Storyboard();
            {
                var ay = new DoubleAnimation
                {
                    From = 0, To = 40,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                Storyboard.SetTarget(ay, _notificationContainer);
                Storyboard.SetTargetProperty(ay, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
                _containerExitSb.Children.Add(ay);

                var ao = new DoubleAnimation
                {
                    From = 1, To = 0,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                Storyboard.SetTarget(ao, _notificationContainer);
                Storyboard.SetTargetProperty(ao, "Opacity");
                _containerExitSb.Children.Add(ao);

                _containerExitSb.Completed += (_, _) =>
                {
                    if (_notificationContainer != null)
                        _notificationContainer.Visibility = Visibility.Collapsed;
                };
            }
        }

        /// <summary>根据当前主题刷新容器 Acrylic 背景（对齐页面遮罩风格）。</summary>
        private void UpdateContainerTheme()
        {
            if (_containerBorder == null || _rootElement == null) return;

            var theme = _rootElement.ActualTheme;
            if (theme == ElementTheme.Default)
                theme = Microsoft.UI.Xaml.Application.Current.RequestedTheme == ApplicationTheme.Dark
                    ? ElementTheme.Dark : ElementTheme.Light;

            var tintColor = theme == ElementTheme.Dark
                ? Color.FromArgb(255, 32, 32, 32)   // #202020
                : Color.FromArgb(255, 243, 243, 243); // #F3F3F3

            _containerBorder.Background = new AcrylicBrush
            {
                TintColor = tintColor,
                TintOpacity = 0.7,
                FallbackColor = tintColor
            };
        }

        // ---- 内部实现 ----

        private void ShowInternal(NotificationItem item)
        {
            if (_notificationPanel == null || _notificationContainer == null)
            {
                Debug.WriteLine("[NotificationService] 通知容器未就绪");
                return;
            }

            // 超出上限：先关闭最旧的（跳过已在退场的）
            while (_active.Count(a => !a.IsDismissing) >= MaxVisible)
            {
                var oldest = _active.FirstOrDefault(a => !a.IsDismissing);
                if (oldest == null) break;
                Dismiss(oldest);
            }

            var control = new NotificationPopup(_rootElement);
            control.SetContent(item);

            var active = new ActiveNotification
            {
                Control = control,
                Item = item
            };

            item.DismissCallback = () => Dismiss(active);
            item.PauseTimerCallback = () => active.IsPaused = true;
            item.ResumeTimerCallback = () =>
            {
                active.IsPaused = false;
                active.DismissTimer?.Stop();
                active.DismissTimer?.Start();
            };

            control.CloseRequested += (_, _) => Dismiss(active);

            // 首个通知：播放入场动画
            bool isFirst = _active.Count == 0;
            _notificationPanel.Children.Add(control);
            _active.Add(active);

            if (isFirst)
            {
                _notificationContainer.Visibility = Visibility.Visible;
                _containerEntranceSb?.Stop();
                _containerEntranceSb?.Begin();
            }

            control.PlayEntranceAnimation();

            // 所有类型都自动消失
            if (item.Duration > TimeSpan.Zero)
            {
                var timer = _dispatcherQueue!.CreateTimer();
                timer.Interval = item.Duration;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (!active.IsPaused)
                        Dismiss(active);
                };
                timer.Start();
                active.DismissTimer = timer;
            }

            Debug.WriteLine($"[NotificationService] 通知: {item.Title}, 活跃={_active.Count}");
        }

        private void Dismiss(ActiveNotification active)
        {
            if (!_active.Contains(active) || active.IsDismissing) return;

            active.IsDismissing = true;
            active.DismissTimer?.Stop();
            active.DismissTimer = null;

            active.Control.PlayExitAnimation(() =>
            {
                _notificationPanel?.Children.Remove(active.Control);
                _active.Remove(active);

                // 全部清完后播放退场动画
                if (_active.Count == 0 && _notificationContainer != null)
                {
                    _containerExitSb?.Stop();
                    _containerExitSb?.Begin();
                }

                Debug.WriteLine($"[NotificationService] 关闭, 剩余={_active.Count}");
            });
        }

        private static TimeSpan GetDefaultDuration(NotificationType type) => type switch
        {
            NotificationType.Error => TimeSpan.FromSeconds(2),
            NotificationType.Warning => TimeSpan.FromSeconds(2),
            _ => TimeSpan.FromSeconds(2)
        };

        private class ActiveNotification
        {
            public NotificationPopup Control { get; set; } = null!;
            public NotificationItem Item { get; set; } = null!;
            public DispatcherQueueTimer? DismissTimer { get; set; }
            public bool IsPaused { get; set; }
            public bool IsDismissing { get; set; }
        }
    }
}
