using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using ModManager.Core.Models;
namespace ModManager.Core.Contracts.Services
{
	public interface IModDataService
	{
		List<ModCategory> GetPrimaryCategories();
		List<ModCategory> GetSecondaryCategories(string primaryName);
		/// <summary>获取 Mod 列表。includePreview=false 时跳过预览图扫描以消除 N+1 磁盘 I/O。</summary>
		List<ModItem> GetModItems(string primaryName, string secondaryName, bool includePreview = false);
		string? GetBestPreviewImagePath(string modFolderPath);
		Task<BitmapImage?> LoadPreviewImageAsync(string modFolderPath);

		// 启用在前，禁用在后，组内按显示名称排序
		List<ModItem> GetModItemsSortedByEnabled(string primaryName, string secondaryName);

		// 仅获取启用的 Mod
		List<ModItem> GetEnabledModItems(string primaryName, string secondaryName);

		// 仅获取禁用的 Mod
		List<ModItem> GetDisabledModItems(string primaryName, string secondaryName);

		/// <summary>
		/// 从角色文件夹下的 info.json 中读取标签列表。
		/// JSON 格式：{ "element": "雷", "weapon": "长枪", ... }
		/// 返回所有标签值的扁平列表（如 ["雷", "长枪", "女", ...]）。
		/// 若文件不存在或解析失败则返回空列表。
		/// </summary>
		Task<List<string>> GetTagsForCharacterAsync(string characterFolderPath);

		/// <summary>
		/// 扫描指定角色根目录下所有角色的 info.json，收集去重后的标签全集。
		/// 用于 TagSearchParser 的 knownTags 参数。
		/// 扫描失败的角色文件夹会被静默跳过。
		/// </summary>
		Task<HashSet<string>> GetAllTagsAsync(string characterRootPath);
	}
}