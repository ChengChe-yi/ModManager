using System.Diagnostics;
using System.Text.Json;
using ModManager.Core.Contracts.Services;

namespace ModManager.Application.Services
{
	public class LocalSettingsService : ILocalSettingsService
	{
		private const string SettingsFolderName = "Setting";
		private const string SettingsFileName = "settings.json";

		// ======== 所有设置 Key 常量 ========
		public const string AppThemeKey = "AppTheme";
		public const string IsThemeInitializedKey = "IsThemeInitialized";
		public const string BackgroundServerKey = "BackgroundServer";
		public const string IsBackgroundEnabledKey = "IsBackgroundEnabled";
		public const string IsAcrylicOverlayEnabledKey = "IsAcrylicOverlayEnabled";
		public const string GlobalBackgroundImageOpacityKey = "GlobalBackgroundImageOpacity";
		public const string SecondaryFilterModeKey = "SecondaryFilterMode";
		public const string ModFilterModeKey = "ModFilterMode";
		public const string LastSelectedPrimaryCategoryKey = "LastSelectedPrimaryCategory";
		public const string LastSelectedSecondaryCategoryKey = "LastSelectedSecondaryCategory";
		public const string LastSelectedModItemKey = "LastSelectedModItem";
		public const string LastSelectedCharacterKey = "LastSelectedCharacter";
		public const string LastSelectedCharacterModKey = "LastSelectedCharacterMod";
		public const string SavedWindowWidthKey = "SavedWindowWidth";
		public const string SavedWindowHeightKey = "SavedWindowHeight";
		public const string IsSaveWindowSizeEnabledKey = "IsSaveWindowSizeEnabled";
		public const string MinimizeToTrayKey = "MinimizeToTray";
		public const string ModsFolderPathKey = "ModsFolderPath";
		public const string UserPreferVideoBackgroundKey = "UserPreferVideoBackground";
		public const string CustomBackgroundPathKey = "CustomBackgroundPath";
		public const string BackgroundJsonHashKey = "BackgroundJsonHash";
		public const string SelectedOnlineBackgroundUrlKey = "SelectedOnlineBackgroundUrl";
		public const string SelectedOnlineBackgroundIsVideoKey = "SelectedOnlineBackgroundIsVideo";

		private readonly string _settingsFilePath;
		private Dictionary<string, object?> _settings;
		private bool _isInitialized = false;
		private readonly JsonSerializerOptions _jsonOptions;

		public LocalSettingsService()
		{
			string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
			string settingsFolder = Path.Combine(baseDirectory, SettingsFolderName);
			_settingsFilePath = Path.Combine(settingsFolder, SettingsFileName);

			_settings = new Dictionary<string, object?>();
			_jsonOptions = new JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			};
		}
		public async Task<T?> ReadSettingAsync<T>(string key)
		{
			var obj = await ReadSettingAsync(key);
			if (obj is T val) return val;
			if (obj is JsonElement je)
			{
				try
				{
					return je.Deserialize<T>();
				}
				catch { /* 忽略，返回默认值 */ }
			}
			return default;
		}
		public async Task InitializeAsync()
		{
			if (_isInitialized) return;

			try
			{
				string? directory = Path.GetDirectoryName(_settingsFilePath);
				if (!string.IsNullOrEmpty(directory))
				{
					Directory.CreateDirectory(directory);
				}

				if (File.Exists(_settingsFilePath))
				{
					string json = await File.ReadAllTextAsync(_settingsFilePath);
					var loaded = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, _jsonOptions);
					if (loaded != null)
					{
						_settings = loaded;
					}
				}
				else
				{
					_settings = new Dictionary<string, object?>();
				}

				_isInitialized = true;
				Debug.WriteLine($"LocalSettingsService: 初始化完成，加载 {_settings.Count} 项设置");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"LocalSettingsService: 初始化失败 - {ex.Message}");
				_settings = new Dictionary<string, object?>();
				_isInitialized = true;
			}
		}

		public async Task<object?> ReadSettingAsync(string key)
		{
			if (!_isInitialized) await InitializeAsync();

			if (_settings.TryGetValue(key, out object? value))
			{
				return value;
			}
			return null;
		}

		public async Task SaveSettingAsync<T>(string key, T value)
		{
			if (!_isInitialized) await InitializeAsync();

			_settings[key] = value;
			await SaveToFileAsync();
			Debug.WriteLine($"LocalSettingsService: 已保存 {key}");
		}

		public async Task RemoveSettingAsync(string key)
		{
			if (!_isInitialized) await InitializeAsync();

			if (_settings.Remove(key))
			{
				await SaveToFileAsync();
				Debug.WriteLine($"LocalSettingsService: 已删除 {key}");
			}
		}

		private async Task SaveToFileAsync()
		{
			try
			{
				string json = JsonSerializer.Serialize(_settings, _jsonOptions);
				await File.WriteAllTextAsync(_settingsFilePath, json);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"LocalSettingsService: 保存文件失败 - {ex.Message}");
			}
		}
	}
}
