using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ModManager.Application.Services;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Messages;
using ModManager.Core.Models;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ModManager.Views
{
	public sealed partial class SettingsPage : Page
	{
		private readonly ILocalSettingsService _settingsService;
		private readonly IPathManager _pathManager;
		private bool _isLoading = false;
		private CancellationTokenSource? _bgImageDebounceCts;
		private CancellationTokenSource? _customPathDebounceCts;
		private CancellationTokenSource? _threadCountDebounceCts;
		private const int SliderDebounceMs = 500;
		private const int TextBoxDebounceMs = 400;
		public SettingsPage()
		{
			InitializeComponent();
			_settingsService = App.GetService<ILocalSettingsService>();
			_pathManager = App.GetService<IPathManager>();
		}

		private async void OnLoaded(object sender, RoutedEventArgs e)
		{
			await LoadAllSettingsAsync();
			EntranceStoryboard?.Begin();
		}

		private async Task LoadAllSettingsAsync()
		{
			_isLoading = true;
			try
			{
				// 主题（RadioButton 无事件，直接设置）
				int themeIndex = await _settingsService.ReadSettingAsync<int>(LocalSettingsService.AppThemeKey);
				ThemeLightRadio.IsChecked = (themeIndex == 0);
				ThemeDarkRadio.IsChecked = (themeIndex == 1);
				ThemeDefaultRadio.IsChecked = (themeIndex != 0 && themeIndex != 1);

				// 背景图片透明度
				double bgImageOpacity = await _settingsService.ReadSettingAsync<double?>(LocalSettingsService.GlobalBackgroundImageOpacityKey) ?? 1.0;
				BgImageOpacitySlider.Value = bgImageOpacity;
				BgImageOpacityValue.Text = bgImageOpacity.ToString("P0");

				// 背景开关（临时移除事件）
				bool bgEnabled = await _settingsService.ReadSettingAsync<bool>(LocalSettingsService.IsBackgroundEnabledKey);
				BackgroundEnabledToggle.Toggled -= BackgroundEnabledToggle_Toggled;
				BackgroundEnabledToggle.IsOn = bgEnabled;
				BackgroundEnabledToggle.Toggled += BackgroundEnabledToggle_Toggled;

				// 服务器 ComboBox
				int serverValue = await _settingsService.ReadSettingAsync<int>(LocalSettingsService.BackgroundServerKey);
				ServerComboBox.SelectionChanged -= ServerComboBox_SelectionChanged;
				int selectedIdx = -1;
				for (int i = 0; i < ServerComboBox.Items.Count; i++)
				{
					if (ServerComboBox.Items[i] is ComboBoxItem item && item.Tag is int val && val == serverValue)
					{
						selectedIdx = i;
						break;
					}
				}
				if (selectedIdx >= 0)
					ServerComboBox.SelectedIndex = selectedIdx;
				else if (ServerComboBox.Items.Count > 0)
					ServerComboBox.SelectedIndex = 0; // 无匹配项时回退到第一项
				ServerComboBox.UpdateLayout();
				ServerComboBox.SelectionChanged += ServerComboBox_SelectionChanged;

				// 自定义背景路径（临时移除事件）
				string customPath = await _settingsService.ReadSettingAsync<string>(LocalSettingsService.CustomBackgroundPathKey) ?? "";
				CustomPathTextBox.TextChanged -= CustomPathTextBox_TextChanged;
				CustomPathTextBox.Text = customPath;
				CustomPathTextBox.TextChanged += CustomPathTextBox_TextChanged;

				// 亚克力开关
				bool acrylic = await _settingsService.ReadSettingAsync<bool>(LocalSettingsService.IsAcrylicOverlayEnabledKey);
				AcrylicOverlayToggle.Toggled -= AcrylicOverlayToggle_Toggled;
				AcrylicOverlayToggle.IsOn = acrylic;
				AcrylicOverlayToggle.Toggled += AcrylicOverlayToggle_Toggled;

				// 窗口行为：最小化到托盘
				bool minimizeToTray = await _settingsService.ReadSettingAsync<bool>(LocalSettingsService.MinimizeToTrayKey);
				MinimizeToTrayToggle.Toggled -= MinimizeToTrayToggle_Toggled;
				MinimizeToTrayToggle.IsOn = minimizeToTray;
				MinimizeToTrayToggle.Toggled += MinimizeToTrayToggle_Toggled;

				// 记住窗口大小
				bool saveWindowSize = await _settingsService.ReadSettingAsync<bool>(LocalSettingsService.IsSaveWindowSizeEnabledKey);
				SaveWindowSizeToggle.Toggled -= SaveWindowSizeToggle_Toggled;
				SaveWindowSizeToggle.IsOn = saveWindowSize;
				SaveWindowSizeToggle.Toggled += SaveWindowSizeToggle_Toggled;

				// 下载线程数
				int threadCount = await _settingsService.ReadSettingAsync<int?>("DownloadThreads") ?? 8;
				DownloadThreadsNumberBox.ValueChanged -= DownloadThreadsNumberBox_ValueChanged;
				DownloadThreadsNumberBox.Value = Math.Clamp(threadCount, 1, 32);
				DownloadThreadsNumberBox.ValueChanged += DownloadThreadsNumberBox_ValueChanged;

				// Mod 路径
				await UpdateModsFolderPathDisplay();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"加载设置失败: {ex.Message}");
			}
			finally
			{
				_isLoading = false;
			}
		}

		private async Task UpdateModsFolderPathDisplay()
		{
			if (_pathManager != null)
			{
				await _pathManager.InitializeAsync();
				ModsFolderPathTextBox.Text = _pathManager.ModsFolderPath;
			}
		}

		// 主题切换
		private async void ThemeRadio_Click(object sender, RoutedEventArgs e)
		{
			if (_isLoading) return;
			if (sender is RadioButton rb && int.TryParse(rb.Tag?.ToString(), out int themeValue))
			{
				await _settingsService.SaveSettingAsync(LocalSettingsService.AppThemeKey, themeValue);
				WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(themeValue));
			}
		}

		// 背景图片透明度
		private void BgImageOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
		{
			if (_isLoading) return;
			double value = e.NewValue;
			BgImageOpacityValue.Text = value.ToString("P0");  // 立即更新 UI

			_bgImageDebounceCts?.Cancel();
			_bgImageDebounceCts?.Dispose();
			var cts = new CancellationTokenSource();
			_bgImageDebounceCts = cts;
			DebouncedSave(LocalSettingsService.GlobalBackgroundImageOpacityKey, value,
				() => WeakReferenceMessenger.Default.Send(new BackgroundImageOpacityChangedMessage(value)),
				cts.Token);
		}

		private async void DebouncedSave<T>(string key, T value, Action notify, CancellationToken token)
		{
			try
			{
				await Task.Delay(SliderDebounceMs, token);
				await _settingsService.SaveSettingAsync(key, value);
				notify();
			}
			catch (TaskCanceledException)
			{
				// 被新事件取消，这是正常的防抖行为
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"设置保存失败 ({key}): {ex.Message}");
			}
		}
		// 背景开关
		private async void BackgroundEnabledToggle_Toggled(object sender, RoutedEventArgs e)
		{
			if (_isLoading) return;
			await _settingsService.SaveSettingAsync(LocalSettingsService.IsBackgroundEnabledKey, BackgroundEnabledToggle.IsOn);
			WeakReferenceMessenger.Default.Send(new BackgroundRefreshMessage());
		}

		// 服务器选择
		private async void ServerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (_isLoading) return;
			if (ServerComboBox.SelectedItem is ComboBoxItem selected && selected.Tag is int serverValue)
			{
				await _settingsService.SaveSettingAsync(LocalSettingsService.BackgroundServerKey, serverValue);
				WeakReferenceMessenger.Default.Send(new BackgroundRefreshMessage());
			}
		}

		// 自定义背景路径
		private void CustomPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
		{
			if (_isLoading) return;
			string path = CustomPathTextBox.Text;

			_customPathDebounceCts?.Cancel();
			_customPathDebounceCts?.Dispose();
			var cts = new CancellationTokenSource();
			_customPathDebounceCts = cts;
			DebouncedCustomPathSave(path, cts.Token);
		}

		private async void DebouncedCustomPathSave(string path, CancellationToken token)
		{
			try
			{
				await Task.Delay(TextBoxDebounceMs, token);
				await _settingsService.SaveSettingAsync(LocalSettingsService.CustomBackgroundPathKey, path);
				if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
				{
					WeakReferenceMessenger.Default.Send(new BackgroundRefreshMessage());
				}
			}
			catch (TaskCanceledException)
			{
				// 被后续输入取消，正常防抖行为
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"自定义背景路径保存失败: {ex.Message}");
			}
		}

		private async void BrowseBackgroundButton_Click(object sender, RoutedEventArgs e)
		{
			if (App.MainWindow == null) return;

			var picker = new FileOpenPicker();
			picker.ViewMode = PickerViewMode.Thumbnail;
			picker.FileTypeFilter.Add(".bmp");
			picker.FileTypeFilter.Add(".mp4");
			picker.FileTypeFilter.Add(".webm");

			var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
			InitializeWithWindow.Initialize(picker, hwnd);

			var file = await picker.PickSingleFileAsync();
			if (file != null)
			{
				CustomPathTextBox.Text = file.Path;
			}
		}

		// 亚克力开关
		private async void AcrylicOverlayToggle_Toggled(object sender, RoutedEventArgs e)
		{
			if (_isLoading) return;
			await _settingsService.SaveSettingAsync(LocalSettingsService.IsAcrylicOverlayEnabledKey, AcrylicOverlayToggle.IsOn);
			WeakReferenceMessenger.Default.Send(new OverlayStyleChangedMessage(AcrylicOverlayToggle.IsOn));
		}

		// 窗口行为
		private async void MinimizeToTrayToggle_Toggled(object sender, RoutedEventArgs e)
		{
			if (_isLoading) return;
			await _settingsService.SaveSettingAsync(LocalSettingsService.MinimizeToTrayKey, MinimizeToTrayToggle.IsOn);
			WeakReferenceMessenger.Default.Send(new MinimizeToTrayChangedMessage(MinimizeToTrayToggle.IsOn));
		}

		private async void SaveWindowSizeToggle_Toggled(object sender, RoutedEventArgs e)
		{
			if (_isLoading) return;
			await _settingsService.SaveSettingAsync(LocalSettingsService.IsSaveWindowSizeEnabledKey, SaveWindowSizeToggle.IsOn);
		}

		// 下载线程数
		private void DownloadThreadsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
		{
			if (_isLoading) return;
			if (double.IsNaN(args.NewValue)) return;
			int value = Math.Clamp((int)Math.Round(args.NewValue), 1, 32);

			_threadCountDebounceCts?.Cancel();
			_threadCountDebounceCts?.Dispose();
			var cts = new CancellationTokenSource();
			_threadCountDebounceCts = cts;
			DebouncedSave("DownloadThreads", value, () => { }, cts.Token);
		}

		// Mod 路径相关
		private async void BrowseModsFolderButton_Click(object sender, RoutedEventArgs e)
		{
			if (App.MainWindow == null) return;

			var folderPicker = new FolderPicker();
			folderPicker.FileTypeFilter.Add("*");
			var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
			InitializeWithWindow.Initialize(folderPicker, hwnd);

			var folder = await folderPicker.PickSingleFolderAsync();
			if (folder != null)
			{
				string newPath = folder.Path;
				await _settingsService.SaveSettingAsync(LocalSettingsService.ModsFolderPathKey, newPath);
				if (_pathManager != null)
				{
					await _pathManager.UpdateModsFolderPathAsync(newPath);
					ModManager.Application.Services.ModHttpServer.UpdateModsRoot(newPath);
					await UpdateModsFolderPathDisplay();
						_ = ModManager.Helpers.CharacterAssetDeployer.DeployAsync(newPath,
							App.GetService<INotificationService>());
					}
			}
		}

		private async void DeployAssets_Click(object sender, RoutedEventArgs e)
		{
			var notif = App.GetService<INotificationService>();
			if (_pathManager == null) return;

			// 确保已初始化
			await _pathManager.InitializeAsync();
			var currentPath = _pathManager.ModsFolderPath;
			if (string.IsNullOrWhiteSpace(currentPath))
			{
				notif?.Show("未配置路径", "请先设置 Mods 文件夹路径", NotificationType.Warning);
				return;
			}

			// 合法性检查：路径必须是 Mods 文件夹
			var dirName = System.IO.Path.GetFileName(currentPath);
			if (!string.Equals(dirName, Core.Constants.FileNames.ModsFolder, StringComparison.OrdinalIgnoreCase))
			{
				notif?.Show("路径无效", $"所选文件夹名应为 \"Mods\"（不区分大小写），当前为 \"{dirName}\"", NotificationType.Warning);
				return;
			}

			var result = await new ContentDialog
			{
				Title = "部署角色数据",
				Content = $"将从数据包自动补全 Character/ 下缺失的 info.json 和 Icon.png。\n\n路径: {currentPath}\n\n已有数据不会被覆盖。",
				PrimaryButtonText = "开始部署",
				SecondaryButtonText = "取消",
				DefaultButton = ContentDialogButton.Primary,
				XamlRoot = XamlRoot
			}.ShowAsync();

			if (result != ContentDialogResult.Primary) return;

			DeployAssetsButton.IsEnabled = false;
			DeployAssetsButton.Content = "部署中...";
			try
			{
				ModManager.Core.Helpers.Log.Info($"[部署] 用户触发, 路径={currentPath}");
				await ModManager.Helpers.CharacterAssetDeployer.DeployAsync(currentPath, notif);
			}
			finally
			{
				DeployAssetsButton.IsEnabled = true;
				DeployAssetsButton.Content = "部署角色数据";
			}
		}

		private async void ResetModsFolderButton_Click(object sender, RoutedEventArgs e)
		{
			string defaultPath = Path.Combine(AppContext.BaseDirectory, Core.Constants.FileNames.ModsFolder);
			await _settingsService.SaveSettingAsync(LocalSettingsService.ModsFolderPathKey, defaultPath);
			if (_pathManager != null)
			{
				await _pathManager.UpdateModsFolderPathAsync(defaultPath);
				ModManager.Application.Services.ModHttpServer.UpdateModsRoot(defaultPath);
				await UpdateModsFolderPathDisplay();
				
			}
		}

	
		// 关于
		private async void OnAboutClick(object sender, RoutedEventArgs e)
		{
			var dialog = new ContentDialog
			{
				Title = "关于 ModManager",
				Content = "ModManager 版本 1.0.7\n\n一个用于管理游戏 Mod 的工具。",
				CloseButtonText = "确定",
				XamlRoot = this.XamlRoot
			};
			await dialog.ShowAsync();
		}

		// 导航菜单切换
		private void SettingsNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
		{
			if (args.SelectedItem is NavigationViewItem selectedItem)
			{
				string tag = selectedItem.Tag?.ToString();
				FrameworkElement target = tag switch
				{
					"AppearanceItem" => AppearanceItem,
					"BackgroundItem" => BackgroundItem,
					"WindowBehaviorItem" => WindowBehaviorItem,
					"AboutItem" => AboutItem,
					_ => null
				};
				if (target != null)
				{
					var transform = target.TransformToVisual(SettingsScrollViewer);
					var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
					SettingsScrollViewer.ChangeView(null, point.Y, null);
				}
			}
		}
	}
}