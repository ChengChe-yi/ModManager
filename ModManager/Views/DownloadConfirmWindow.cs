using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.Graphics;

namespace ModManager.Views
{
    public static class DownloadConfirmWindow
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        public static async Task<(bool confirmed, bool skipExtract, string finalName)> ShowAsync(
            string modName, string character, string targetDir, string fileName, bool hasCharacter = true)
        {
            var tcs = new TaskCompletionSource<(bool, bool, string)>();

            // 确保在 UI 线程创建窗口
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null)
            {
                // 不在 UI 线程 — 等待 ModHttpServer._uiDispatcher 调度
                await Task.Delay(100); // 给 UI 线程一点时间
                var uiDisp = ModManager.Application.Services.ModHttpServer.GetUIDispatcher();
                if (uiDisp != null)
                {
                    uiDisp.TryEnqueue(() => ShowOnUIThread(modName, character, targetDir, fileName, hasCharacter, tcs));
                    return await tcs.Task;
                }
                tcs.TrySetResult((false, false, ""));
                return await tcs.Task;
            }

            ShowOnUIThread(modName, character, targetDir, fileName, hasCharacter, tcs);
            return await tcs.Task;
        }

        private static void ShowOnUIThread(string modName, string character, string targetDir, string fileName, bool hasCharacter,
            TaskCompletionSource<(bool, bool, string)> tcs)
        {
            var window = new Window();
            window.Title = "ModManager";
            try { window.SystemBackdrop = new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt }; } catch { }
            window.ExtendsContentIntoTitleBar = true;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 标题栏
            var titleBar = new Grid { Margin = new Thickness(160, 0, 16, 0) };
            var titleText = new TextBlock
            {
                Text = "确认下载", FontSize = 13,
                Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBar.Children.Add(titleText);
            Grid.SetRow(titleBar, 0);
            root.Children.Add(titleBar);

            // 内容卡片
            var card = new Border
            {
                Margin = new Thickness(16, 0, 16, 16),
                Padding = new Thickness(16),
                Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1)
            };
            Grid.SetRow(card, 1);
            root.Children.Add(card);

            var hwnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                var presenter = appWindow.Presenter as OverlappedPresenter;
                if (presenter != null)
                {
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsResizable = false;
                }
                appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 560, Height = 550 });
            }

            var panel = new StackPanel { Spacing = 8 };
            var secondaryBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
            var strokeBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"];
            var accentBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];

            // 标题：Mod 名
            panel.Children.Add(new TextBlock
            {
                Text = modName, FontSize = 16, MaxWidth = 460,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            // 角色 + 路径（可选中复制，不编辑）
            panel.Children.Add(new TextBlock
            {
                Text = $"角色: {character}  →  {System.IO.Path.GetFileNameWithoutExtension(targetDir)}",
                FontSize = 12, Foreground = secondaryBrush,
                IsTextSelectionEnabled = true
            });

            // 文件名下拉：原名 / 标题，可编辑
            var nameCombo = new ComboBox { IsEditable = true, FontSize = 13 };
            nameCombo.Items.Add(fileName);
            nameCombo.Items.Add(modName);
            nameCombo.SelectedIndex = 0;
            panel.Children.Add(new TextBlock { Text = "保存为", FontSize = 11, Foreground = secondaryBrush });
            panel.Children.Add(nameCombo);

            // 分隔线
            panel.Children.Add(new Border { Height = 1, Background = strokeBrush });

            // 解压开关（未识别角色时禁用，强制仅下载）
            var extractToggle = new ToggleSwitch
            {
                Header = "下载后自动解压并安装",
                OnContent = "自动解压到角色目录",
                OffContent = hasCharacter ? "仅下载不解压" : "未识别角色，仅下载",
                IsOn = hasCharacter,
                IsEnabled = hasCharacter,
                FontSize = 13
            };
            if (!hasCharacter)
                panel.Children.Add(new TextBlock
                {
                    Text = "⚠ 未识别角色，无法自动解压到角色目录",
                    FontSize = 11, Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCautionBrush"],
                    Margin = new Thickness(0, -4, 0, 0)
                });
            panel.Children.Add(extractToggle);

            // 按钮行
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 10, Margin = new Thickness(0, 6, 0, 0)
            };
            var downloadBtn = new Button { Content = "下载", Padding = new Thickness(20, 6, 20, 6), FontSize = 13 };
            var cancelBtn = new Button { Content = "取消", Padding = new Thickness(20, 6, 20, 6), FontSize = 13 };
            btnPanel.Children.Add(downloadBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            downloadBtn.Click += (_, _) => { tcs.TrySetResult((true, !extractToggle.IsOn, (nameCombo.SelectedItem as string) ?? nameCombo.Text)); window.Close(); };
            cancelBtn.Click += (_, _) => { tcs.TrySetResult((false, false, "")); window.Close(); };
            window.Closed += (_, _) => tcs.TrySetResult((false, false, ""));

            card.Child = panel;
            window.Content = root;
            window.Activate();

            // 置顶 (SetWindowPos HWND_TOPMOST)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW);
        }
    }
}
