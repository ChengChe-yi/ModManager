using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Models;

namespace ModManager.Application.Services
{
	public class ModDataService : IModDataService
	{
		private readonly IPathManager _pathManager;

		public ModDataService(IPathManager pathManager)
		{
			_pathManager = pathManager;
		}

		// ========== 分类列表 ==========

		public List<ModCategory> GetPrimaryCategories()
		{
			var cats = BuildCategoryListFromPath(_pathManager.ModsFolderPath);
			ModManager.Core.Helpers.Log.Info($"一级分类: {cats.Count} 个");
			return cats;
		}

		public List<ModCategory> GetSecondaryCategories(string primaryName)
		{
			string path = Path.Combine(_pathManager.ModsFolderPath, primaryName);
			return BuildCategoryListFromPath(path);
		}

		// ========== Mod 列表 ==========

		public List<ModItem> GetModItems(string primaryName, string secondaryName, bool includePreview = false)
		{
			string folder = Path.Combine(_pathManager.ModsFolderPath, primaryName, secondaryName);
			if (!Directory.Exists(folder))
				return new List<ModItem>();

			var mods = new List<ModItem>();
			foreach (string modDir in Directory.GetDirectories(folder))
			{
				string modName = Path.GetFileName(modDir);
				if (string.IsNullOrEmpty(modName)) continue;

				bool isEnabled = !modName.StartsWith(ModCategoryService.DisabledPrefix, StringComparison.OrdinalIgnoreCase);
				string displayName = isEnabled ? modName : modName[ModCategoryService.DisabledPrefix.Length..];
				string? preview = includePreview ? GetBestPreviewImagePath(modDir) : null;

				mods.Add(new ModItem
				{
					ModName = modName,
					DisplayName = displayName,
					Enable = isEnabled,
					PreviewImage = preview
				});
			}

			return mods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
		}

		// ========== 预览图路径 ==========

		public string? GetBestPreviewImagePath(string modFolderPath)
		{
			if (!Directory.Exists(modFolderPath)) return null;

			string[] supportedExtensions = Core.Constants.FileNames.ImageExtensions;
			var imageFiles = Directory.GetFiles(modFolderPath, "*.*")
				.Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower()))
				.ToList();

			if (imageFiles.Count == 0) return null;

			// 优先取 preview.png，否则取第一个匹配文件
			return imageFiles.FirstOrDefault(f =>
				Path.GetFileName(f).Equals("preview.png", StringComparison.OrdinalIgnoreCase)) ?? imageFiles.First();
		}

		// ========== 加载预览图（不涉及 UI） ==========

		public async Task<BitmapImage?> LoadPreviewImageAsync(string modFolderPath)
		{
			if (!Directory.Exists(modFolderPath))
			{
				Debug.WriteLine($"[ModDataService] LoadPreview: folder not found: {modFolderPath}");
				return null;
			}

			string[] supportedExtensions = Core.Constants.FileNames.ImageExtensions;
			var imageFiles = Directory.GetFiles(modFolderPath, "*.*")
				.Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower()))
				.ToList();

			if (imageFiles.Count == 0)
			{
				Debug.WriteLine($"[ModDataService] LoadPreview: no image in {modFolderPath}");
				return null;
			}

			// 优先选取文件名中包含 "preview" 的图片
			var previewFiles = imageFiles
				.Where(f => Path.GetFileNameWithoutExtension(f).Contains("preview", StringComparison.OrdinalIgnoreCase))
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.ToList();

			string? previewFile = previewFiles.FirstOrDefault() ?? imageFiles.FirstOrDefault();
			if (previewFile == null)
				return null;

			try
			{
				string uriString = "file:///" + previewFile.Replace('\\', '/');
				var bitmap = new BitmapImage
				{
					DecodePixelType = DecodePixelType.Logical,
					DecodePixelWidth = 800,
					UriSource = new Uri(uriString)
				};
				// 不涉及 UI 赋值，直接返回
				return bitmap;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"加载预览图失败: {previewFile}, {ex.Message}");
				return null;
			}
		}

		// ========== 内部方法 ==========

		private List<ModCategory> BuildCategoryListFromPath(string folderPath)
		{
			var list = new List<ModCategory>();
			if (!Directory.Exists(folderPath)) return list;

			foreach (string subDir in Directory.GetDirectories(folderPath))
			{
				string name = Path.GetFileName(subDir);
				if (string.Equals(name, "disabled", StringComparison.OrdinalIgnoreCase)) continue;
				if (string.Equals(name, Core.Constants.FileNames.CharacterFolder, StringComparison.OrdinalIgnoreCase)) continue;  // 角色管理页专用目录

				bool isDisabled = name.StartsWith(ModCategoryService.DisabledPrefix, StringComparison.OrdinalIgnoreCase);
				string displayName = isDisabled ? name[ModCategoryService.DisabledPrefix.Length..] : name;
				string iconPath = Path.Combine(subDir, "Icon.png");
				int modCount = Directory.GetDirectories(subDir).Length;

				list.Add(new ModCategory
				{
					Name = name,
					DisplayName = displayName,
					BackgroundImage = iconPath,
					ModNumber = modCount,
					NotEnable = isDisabled
				});
			}

			return SortAndFilterCategories(list);
		}

		private List<ModCategory> SortAndFilterCategories(List<ModCategory> categories)
		{
			if (categories == null) return new List<ModCategory>();

			var filtered = categories
				.Where(c => !string.Equals(c.Name, "disabled", StringComparison.OrdinalIgnoreCase))
				.ToList();

			var sorted = filtered
	.OrderBy(c => c.DisplayName ?? c.Name, StringComparer.OrdinalIgnoreCase)
	.ToList();

			return sorted;
		}

		public List<ModItem> GetModItemsSortedByEnabled(string primaryName, string secondaryName)
		{
			var mods = GetModItems(primaryName, secondaryName);
			// 启用在前，禁用在后，组内按 DisplayName 排序
			return mods
				.OrderByDescending(m => m.Enable)   // true（启用）在前，false（禁用）在后
				.ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		public List<ModItem> GetEnabledModItems(string primaryName, string secondaryName)
		{
			return GetModItems(primaryName, secondaryName)
				.Where(m => m.Enable)
				.ToList();
		}

		public List<ModItem> GetDisabledModItems(string primaryName, string secondaryName)
		{
			return GetModItems(primaryName, secondaryName)
				.Where(m => !m.Enable)
				.ToList();
		}

		// ========== 角色标签 ==========

		/// <summary>
		/// 从角色文件夹下的 info.json 中读取标签列表。
		/// JSON 格式为扁平的键值对：{ "element": "雷", "weapon": "长枪", "gender": "女", ... }
		/// 返回所有值的列表（如 ["雷", "长枪", "女", ...]）。
		/// 若文件不存在或解析失败则返回空列表。
		/// </summary>
		public async Task<List<string>> GetTagsForCharacterAsync(string characterFolderPath)
		{
			if (!Directory.Exists(characterFolderPath))
				return new List<string>();

			string infoJsonPath = Path.Combine(characterFolderPath, Core.Constants.FileNames.CharacterInfo);
			if (!File.Exists(infoJsonPath))
				return new List<string>();

			try
			{
				string json = await File.ReadAllTextAsync(infoJsonPath);
				var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
				if (dict == null)
					return new List<string>();

				var tags = new List<string>();
				foreach (var kvp in dict)
				{
					if (kvp.Value != null)
					{
						string tagValue = kvp.Value.ToString() ?? "";
						if (!string.IsNullOrWhiteSpace(tagValue))
							tags.Add(tagValue);
					}
				}
				return tags;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"读取角色标签失败 [{infoJsonPath}]: {ex.Message}");
				return new List<string>();
			}
		}

		/// <summary>
		/// 扫描指定角色根目录下所有角色的 info.json，收集去重后的标签全集。
		/// 扫描失败的角色文件夹会被静默跳过。
		/// </summary>
		public async Task<HashSet<string>> GetAllTagsAsync(string characterRootPath)
		{
			var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			if (!Directory.Exists(characterRootPath))
				return allTags;

			foreach (string charDir in Directory.GetDirectories(characterRootPath))
			{
				var tags = await GetTagsForCharacterAsync(charDir);
				foreach (string tag in tags)
					allTags.Add(tag);
			}

			return allTags;
		}
	}
}