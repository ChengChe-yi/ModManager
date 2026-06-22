using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ModManager.Core.Models;
using Windows.UI;

namespace ModManager.Views
{
    public sealed partial class NotificationPopup : UserControl
    {
        private Storyboard? _entranceSb;
        private Storyboard? _exitSb;

        public event EventHandler? CloseRequested;

        private static readonly string InfoGlyph = "";
        private static readonly string SuccessGlyph = "";
        private static readonly string WarningGlyph = "";
        private static readonly string ErrorGlyph = "";

        public NotificationPopup(FrameworkElement? rootElement)
        {
            InitializeComponent();
            BuildAnimations();
            // 卡片背景直接用 ThemeResource，自动跟随主题，无需手动设置
        }

        public void SetContent(NotificationItem item)
        {
            TitleBlock.Text = item.Title;
            MessageBlock.Text = item.Message;

            TypeIcon.Glyph = item.Type switch
            {
                NotificationType.Success => SuccessGlyph,
                NotificationType.Warning => WarningGlyph,
                NotificationType.Error => ErrorGlyph,
                _ => InfoGlyph
            };

            TypeIcon.Foreground = item.Type switch
            {
                NotificationType.Success => new SolidColorBrush(Color.FromArgb(255, 108, 203, 95)),
                NotificationType.Warning => new SolidColorBrush(Color.FromArgb(255, 252, 180, 0)),
                NotificationType.Error => new SolidColorBrush(Color.FromArgb(255, 255, 69, 68)),
                _ => (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"]
            };

            PointerEntered += (_, _) => item.PauseTimerCallback?.Invoke();
            PointerExited += (_, _) => item.ResumeTimerCallback?.Invoke();
        }

        /// <summary>供 NotificationService.UpdateTheme 调用（容器背景更新用，卡片自身无需操作）。</summary>
        public void UpdateTheme(FrameworkElement? root)
        {
            // 卡片背景使用 ThemeResource，自动跟随系统主题
        }

        public void PlayEntranceAnimation()
        {
            if (_entranceSb == null) return;
            CardTransform.TranslateX = 40;
            Opacity = 0;
            _entranceSb.Stop();
            _entranceSb.Begin();
        }

        public void PlayExitAnimation(Action? onCompleted)
        {
            if (_exitSb == null)
            {
                onCompleted?.Invoke();
                return;
            }

            void OnCompleted(object? s, object e)
            {
                _exitSb.Completed -= OnCompleted;
                onCompleted?.Invoke();
            }

            _exitSb.Completed += OnCompleted;
            _exitSb.Stop();
            _exitSb.Begin();
        }

        private void BuildAnimations()
        {
            // 入场：X 40→0, Opacity 0→1, 250ms EaseOut
            _entranceSb = new Storyboard();
            AddAnim(_entranceSb, CardTransform, "TranslateX", 0, 250, new CubicEase { EasingMode = EasingMode.EaseOut });
            AddAnim(_entranceSb, this, "Opacity", 1, 250, new CubicEase { EasingMode = EasingMode.EaseOut });

            // 退场：X 0→40, Opacity 1→0, 200ms EaseIn
            _exitSb = new Storyboard();
            AddAnim(_exitSb, CardTransform, "TranslateX", 40, 200, new CubicEase { EasingMode = EasingMode.EaseIn });
            AddAnim(_exitSb, this, "Opacity", 0, 200, new CubicEase { EasingMode = EasingMode.EaseIn });
        }

        private static void AddAnim(Storyboard sb, DependencyObject target, string prop,
            double to, double ms, EasingFunctionBase ease)
        {
            var a = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = ease
            };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
