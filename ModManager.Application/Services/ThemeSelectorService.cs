using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using ModManager.Core.Contracts.Services;

namespace ModManager.Application.Services 
{
	public class ThemeSelectorService : IThemeSelectorService
	{
		private const string SettingsKey = "AppBackgroundRequestedTheme";
		public ElementTheme Theme { get; set; } = ElementTheme.Default;
		private readonly ILocalSettingsService _localSettingsService;

		public ThemeSelectorService(ILocalSettingsService localSettingsService)
		{
			_localSettingsService = localSettingsService;
		}

		public async Task InitializeAsync()
		{
			Theme = await LoadThemeFromSettingsAsync();
		}

		public async Task SetThemeAsync(ElementTheme theme)
		{
			Theme = theme;
			await SaveThemeInSettingsAsync(Theme);
		}

		public async Task SetRequestedThemeAsync(FrameworkElement rootElement)
		{
			if (rootElement != null)
			{
				rootElement.RequestedTheme = Theme;
				Debug.WriteLine($"Theme applied: {Theme}");
			}
			await Task.CompletedTask;
		}

		private async Task<ElementTheme> LoadThemeFromSettingsAsync()
		{
			var themeObj = await _localSettingsService.ReadSettingAsync(SettingsKey);
			if (themeObj != null && Enum.TryParse(themeObj.ToString(), out ElementTheme cacheTheme))
				return cacheTheme;
			return ElementTheme.Default;
		}

		private async Task SaveThemeInSettingsAsync(ElementTheme theme)
		{
			await _localSettingsService.SaveSettingAsync(SettingsKey, theme.ToString());
		}
	}
}