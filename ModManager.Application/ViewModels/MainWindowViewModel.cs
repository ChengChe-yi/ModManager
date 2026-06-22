using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using ModManager.Application.Services;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Messages;
using ModManager.Core.Models;

namespace ModManager.Application.ViewModels
{
	/// <summary>
	/// MainWindow 的 ViewModel，负责设置管理、背景协调和消息发送。
	/// 所有非 UI 的业务逻辑集中在此处。
	/// </summary>
	public class MainWindowViewModel
	{
		private readonly ILocalSettingsService _localSettingsService;
		private readonly IBackgroundRenderer _backgroundRenderer;

		public MainWindowViewModel(ILocalSettingsService localSettingsService,
								   IBackgroundRenderer backgroundRenderer)
		{
			_localSettingsService = localSettingsService;
			_backgroundRenderer = backgroundRenderer;
		}

		// ======================== 主题 ========================
		public async Task<ElementTheme> LoadThemeAsync()
		{
			try
			{
				int themeIndex = await _localSettingsService.ReadSettingAsync<int>(LocalSettingsService.AppThemeKey);
				return themeIndex switch
				{
					0 => ElementTheme.Light,
					1 => ElementTheme.Dark,
					_ => ElementTheme.Default
				};
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[VM] 加载主题失败: {ex.Message}");
				return ElementTheme.Default;
			}
		}

		public async Task SaveThemeAsync(int themeIndex)
		{
			await _localSettingsService.SaveSettingAsync(LocalSettingsService.AppThemeKey, themeIndex);
			WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(themeIndex));
		}

		// ======================== 背景透明度 ========================
		// 背景图片透明度（遮罩和内容区已废弃，固定为0）
		public async Task<double> LoadBackgroundImageOpacityAsync()
		{
			const double defaultOpacity = 1.0;
			try
			{
				double opacity = await _localSettingsService.ReadSettingAsync<double?>(
					LocalSettingsService.GlobalBackgroundImageOpacityKey) ?? defaultOpacity;
				return opacity;
			}
			catch
			{
				return defaultOpacity;
			}
		}

		public async Task SaveBackgroundImageOpacityAsync(double value)
		{
			await _localSettingsService.SaveSettingAsync(
				LocalSettingsService.GlobalBackgroundImageOpacityKey, value);
			WeakReferenceMessenger.Default.Send(new BackgroundImageOpacityChangedMessage(value));
		}

		// 内容区域透明度
		// ======================== 背景开关 ========================
		public async Task<bool> LoadBackgroundEnabledAsync()
		{
			try
			{
				return await _localSettingsService.ReadSettingAsync<bool>(
					LocalSettingsService.IsBackgroundEnabledKey);
			}
			catch
			{
				return true; // 默认启用
			}
		}

		public async Task SaveBackgroundEnabledAsync(bool enabled)
		{
			await _localSettingsService.SaveSettingAsync(
				LocalSettingsService.IsBackgroundEnabledKey, enabled);
			WeakReferenceMessenger.Default.Send(new BackgroundRefreshMessage());
		}

		// ======================== 服务器 ========================
		public async Task<int> LoadServerAsync()
		{
			try
			{
				return await _localSettingsService.ReadSettingAsync<int>(
					LocalSettingsService.BackgroundServerKey);
			}
			catch
			{
				return 0; // 默认国服
			}
		}

		public async Task SaveServerAsync(int serverValue)
		{
			await _localSettingsService.SaveSettingAsync(
				LocalSettingsService.BackgroundServerKey, serverValue);
			WeakReferenceMessenger.Default.Send(new BackgroundRefreshMessage());
		}

		// ======================== 视频优先 ========================
		public async Task<bool> LoadPreferVideoAsync()
		{
			try
			{
				return await _localSettingsService.ReadSettingAsync<bool>(LocalSettingsService.UserPreferVideoBackgroundKey);
			}
			catch
			{
				return false;
			}
		}

		public async Task SavePreferVideoAsync(bool prefer)
		{
			await _localSettingsService.SaveSettingAsync(LocalSettingsService.UserPreferVideoBackgroundKey, prefer);
			WeakReferenceMessenger.Default.Send(new BackgroundRefreshMessage());
		}

		// ======================== 亚克力覆盖 ========================
		public async Task<bool> LoadAcrylicOverlayAsync()
		{
			try
			{
				return await _localSettingsService.ReadSettingAsync<bool>(
					LocalSettingsService.IsAcrylicOverlayEnabledKey);
			}
			catch
			{
				return false;
			}
		}

		public async Task SaveAcrylicOverlayAsync(bool enabled)
		{
			await _localSettingsService.SaveSettingAsync(
				LocalSettingsService.IsAcrylicOverlayEnabledKey, enabled);
			WeakReferenceMessenger.Default.Send(new OverlayStyleChangedMessage(enabled));
		}

		
		public async Task<bool> LoadMinimizeToTrayAsync()
		{
			try
			{
				return await _localSettingsService.ReadSettingAsync<bool>(LocalSettingsService.MinimizeToTrayKey);
			}
			catch
			{
				return false;
			}
		}

		public async Task SaveMinimizeToTrayAsync(bool enabled)
		{
			await _localSettingsService.SaveSettingAsync(LocalSettingsService.MinimizeToTrayKey, enabled);
			WeakReferenceMessenger.Default.Send(new MinimizeToTrayChangedMessage(enabled));
		}

		// ======================== 自定义背景路径 ========================
		public async Task<string> LoadCustomBackgroundPathAsync()
		{
			try
			{
				return await _localSettingsService.ReadSettingAsync<string>(LocalSettingsService.CustomBackgroundPathKey) ?? "";
			}
			catch
			{
				return "";
			}
		}

		public async Task SaveCustomBackgroundPathAsync(string path)
		{
			await _localSettingsService.SaveSettingAsync(LocalSettingsService.CustomBackgroundPathKey, path);
			if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
			{
				WeakReferenceMessenger.Default.Send(new BackgroundRefreshMessage());
			}
		}


		public async Task<BackgroundRenderResult?> GetBackgroundAsync(ServerType server, bool preferVideo)
		{
			return await _backgroundRenderer.GetBackgroundAsync(server, preferVideo);
		}

		public async Task<BackgroundRenderResult?> GetCustomBackgroundAsync(string path)
		{
			return await _backgroundRenderer.GetCustomBackgroundAsync(path);
		}
	}
}