using System;
using System.Collections.Generic;
using System.Text;
using ModManager.Core.Contracts.Services;

namespace ModManager.Application.Services
{
	public class ModCategoryService : IModCategoryService
	{
		/// <summary>禁用前缀：修改统一使用大写，检测使用 OrdinalIgnoreCase 兼容 "DISABLED" 和 "disabled"</summary>
		public const string DisabledPrefix = "DISABLED";

		private readonly IPathManager _pathManager;
		public ModCategoryService(IPathManager pathManager) => _pathManager = pathManager;

		public bool IsFolderDisabled(string folderPath)//判断指定路径的文件夹是否被禁用
		{
			if (string.IsNullOrEmpty(folderPath)) return false;
			string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			return folderName.StartsWith(DisabledPrefix, StringComparison.OrdinalIgnoreCase);
		}

		public bool IsNameDisabled(string name)//判断指定名称（不包含路径）是否被禁用
		{
			if (string.IsNullOrEmpty(name)) return false;
			return name.StartsWith(DisabledPrefix, StringComparison.OrdinalIgnoreCase);
		}

		public string GetToggledName(string currentName)//获取启用/禁用切换后的新名称
		{
			if (string.IsNullOrEmpty(currentName)) return currentName;
			if (IsNameDisabled(currentName))
			{
				// 去掉禁用前缀，保留剩余部分
				return currentName.Substring(DisabledPrefix.Length);
			}
			else
			{
				// 统一使用大写 DISABLED 前缀
				return DisabledPrefix + currentName;
			}
		}

		public string GetDisplayName(string folderName)
		{
			if (string.IsNullOrEmpty(folderName)) return folderName;
			if (folderName.StartsWith(DisabledPrefix, StringComparison.OrdinalIgnoreCase))
				return folderName.Substring(DisabledPrefix.Length);
			return folderName;
		}//获取显示名称

	}
}
