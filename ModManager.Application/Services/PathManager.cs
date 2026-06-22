using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ModManager.Core.Contracts.Services;

namespace ModManager.Application.Services
{
	public class PathManager : IPathManager
	{
		private readonly ILocalSettingsService _settingsService;
		private string _modsFolderPath;
		private bool _isInitialized = false;

		public event EventHandler<string> PathChanged;   // 使用 EventHandler<string>

		public string ModsFolderPath
		{
			get => _modsFolderPath;
			private set
			{
				if (_modsFolderPath != value)
				{
					_modsFolderPath = value;
					// 触发事件，传递 sender 和新路径
					PathChanged?.Invoke(this, value);
				}
			}
		}

		public PathManager(ILocalSettingsService settingsService)
		{
			_settingsService = settingsService;
		}

		/// <summary>
		/// 初始化：从配置读取路径，若无效则设置为默认值，并确保目录存在
		/// </summary>
		public async Task InitializeAsync()
		{
			if (_isInitialized) return;

			try
			{
				// 1. 尝试读取已保存的路径
				var savedPathObj = await _settingsService.ReadSettingAsync(LocalSettingsService.ModsFolderPathKey);
				string savedPath = savedPathObj?.ToString();

				if (IsValidPath(savedPath))
				{
					ModsFolderPath = savedPath;
					Debug.WriteLine($"PathManager: 使用已保存的 Mods 路径 -> {ModsFolderPath}");
				}
				else
				{
					// 2. 无效则使用默认路径
					await ResetToDefaultPathAsync(raiseEvent: false);
					Debug.WriteLine($"PathManager: 使用默认 Mods 路径 -> {ModsFolderPath}");
				}

				// 3. 确保目录存在
				EnsureDirectoryExists(ModsFolderPath);
				_isInitialized = true;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"PathManager 初始化失败: {ex.Message}");
				// 降级：使用默认路径
				await ResetToDefaultPathAsync(raiseEvent: false);
				EnsureDirectoryExists(ModsFolderPath);
				_isInitialized = true;
			}
		}

		/// <summary>
		/// 修改 Mods 文件夹路径
		/// </summary>
		public async Task<bool> SetModsFolderPathAsync(string newPath)
		{
			if (string.IsNullOrWhiteSpace(newPath))
				return false;

			// 规范化路径
			newPath = Path.GetFullPath(newPath);

			// 与当前路径相同则无需操作
			if (string.Equals(newPath, ModsFolderPath, StringComparison.OrdinalIgnoreCase))
				return true;

			// 验证新路径是否有效（可创建或存在）
			if (!IsValidPath(newPath, checkWritable: true))
			{
				Debug.WriteLine($"PathManager: 新路径无效或不可写 -> {newPath}");
				return false;
			}

			try
			{
				// 保存到设置
				await _settingsService.SaveSettingAsync(LocalSettingsService.ModsFolderPathKey, newPath);
				// 更新当前路径（会触发事件）
				ModsFolderPath = newPath;
				// 确保目录存在
				EnsureDirectoryExists(newPath);
				Debug.WriteLine($"PathManager: 路径已更改为 {newPath}");
				return true;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"PathManager: 保存新路径失败 - {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// 重置为默认路径（exe 根目录下的 Mods）
		/// </summary>
		public async Task ResetToDefaultPathAsync()
		{
			await ResetToDefaultPathAsync(raiseEvent: true);
		}

		private async Task ResetToDefaultPathAsync(bool raiseEvent)
		{
			string defaultPath = Path.Combine(AppContext.BaseDirectory, Core.Constants.FileNames.ModsFolder);
			try
			{
				await _settingsService.SaveSettingAsync(LocalSettingsService.ModsFolderPathKey, defaultPath);
				if (raiseEvent)
					ModsFolderPath = defaultPath;
				else
					_modsFolderPath = defaultPath; // 不触发事件
				EnsureDirectoryExists(defaultPath);
				Debug.WriteLine($"PathManager: 已重置为默认路径 {defaultPath}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"PathManager: 重置默认路径失败 - {ex.Message}");
			}
		}

		/// <summary>
		/// 验证路径有效性（可选检查是否可写）
		/// </summary>
		private bool IsValidPath(string path, bool checkWritable = false)
		{
			if (string.IsNullOrWhiteSpace(path))
				return false;

			try
			{
				// 确保是绝对路径
				string fullPath = Path.GetFullPath(path);
				// 检查父目录或自身是否可创建
				string dirToCheck = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
				if (string.IsNullOrEmpty(dirToCheck))
					return false;

				if (checkWritable)
				{
					// 测试是否可写入
					string testFile = Path.Combine(dirToCheck, ".writetest");
					File.WriteAllText(testFile, "test");
					File.Delete(testFile);
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// 确保目录存在，若不存在则创建
		/// </summary>
		private void EnsureDirectoryExists(string path)
		{
			if (!Directory.Exists(path))
			{
				try
				{
					Directory.CreateDirectory(path);
					Debug.WriteLine($"PathManager: 创建目录 {path}");
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"PathManager: 创建目录失败 {path} - {ex.Message}");
				}
			}
		}
		public async Task UpdateModsFolderPathAsync(string newPath)
		{
			// 委托给 SetModsFolderPathAsync，确保走完整的事件通知和路径验证流程
			await SetModsFolderPathAsync(newPath);
		}
	}
}
