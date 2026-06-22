using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ModManager.Core.Models;

namespace ModManager.Application.Services
{
	public static class ModKeyParser
	{
		public static bool ContainsKeySection(string iniPath)
		{
			try
			{
				var lines = File.ReadLines(iniPath);
				foreach (var line in lines)
				{
					string trimmed = line.Trim();
					if (trimmed.StartsWith("[") && trimmed.EndsWith("]") &&
						trimmed.Length > 2 && trimmed.Substring(1, 3).Equals("Key", StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Debug.WriteLine($"读取 ini 文件失败: {iniPath}");
				return false;
			}
			return false;
		}

		public static List<ModKey> ParseFromIni(string iniFilePath)
		{
			var result = new List<ModKey>();
			if (!File.Exists(iniFilePath)) return result;

			var lines = File.ReadAllLines(iniFilePath);

			// 1. 第一步：提取 [Constants] 节中的初始值
			var initialValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			bool inConstants = false;
			foreach (var line in lines)
			{
				var trimmed = line.Trim();
				if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
				{
					var section = trimmed.TrimStart('[').TrimEnd(']');
					inConstants = section.Equals("Constants", StringComparison.OrdinalIgnoreCase);
					continue;
				}
				if (inConstants && !string.IsNullOrWhiteSpace(trimmed))
				{
					// 匹配 "global persist $xxx = yyy" 或 "global persist $xxx=yyy"
					if (trimmed.StartsWith("global persist $", StringComparison.OrdinalIgnoreCase))
					{
						var eqIdx = trimmed.IndexOf('=');
						if (eqIdx > 0)
						{
							var left = trimmed.Substring(0, eqIdx).Trim();   // "global persist $hair"
							var value = trimmed.Substring(eqIdx + 1).Trim(); // "0"
																			 // 提取 $ 后的变量名
							var dollarIdx = left.IndexOf('$');
							if (dollarIdx >= 0 && dollarIdx < left.Length - 1)
							{
								var varName = left.Substring(dollarIdx + 1).Trim();
								initialValues[varName] = value;
							}
						}
					}
				}
			}

			// 2. 第二步：解析各个 [Key...] 节
			ModKey? currentKey = null;
			var keyNameList = new List<string>();
			string? currentVarName = null;  // 当前节发现的变量名（第一个 $ 开头的变量）

			foreach (var line in lines)
			{
				var trimmed = line.Trim();
				if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
				{
					// 结束上一个 Key
					if (currentKey != null && keyNameList.Count > 0)
					{
						currentKey.KeyName = string.Join(" 或 ", keyNameList);
						currentKey.VariableName = currentVarName ?? "";
						currentKey.InitialValue = currentVarName != null && initialValues.TryGetValue(currentVarName, out var init) ? init : "";
						result.Add(currentKey);
					}
					currentKey = null;
					keyNameList.Clear();
					currentVarName = null;

					var sectionName = trimmed.TrimStart('[').TrimEnd(']');
					if (sectionName.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
					{
						currentKey = new ModKey();
					}
				}
				else if (currentKey != null && !string.IsNullOrWhiteSpace(trimmed))
				{
					var eqIdx = trimmed.IndexOf('=');
					if (eqIdx > 0)
					{
						var key = trimmed.Substring(0, eqIdx).Trim();
						var value = trimmed.Substring(eqIdx + 1).Trim();

						if (key.Equals("key", StringComparison.OrdinalIgnoreCase))
						{
							keyNameList.Add(value);
						}
						else if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
						{
							currentKey.KeyType = value;
						}
						else if (key.StartsWith("$"))
						{
							// 变量值，如 "$hair = 0,1,2,3"
							currentKey.KeyValue = value;
							// 记录第一个变量名
							if (currentVarName == null)
							{
								currentVarName = key.TrimStart('$');
							}
						}
					}
				}
			}
			// 处理最后一个 Key
			if (currentKey != null && keyNameList.Count > 0)
			{
				currentKey.KeyName = string.Join(" 或 ", keyNameList);
				currentKey.VariableName = currentVarName ?? "";
				currentKey.InitialValue = currentVarName != null && initialValues.TryGetValue(currentVarName, out var init) ? init : "";
				result.Add(currentKey);
			}

			return result;
		}

		public static List<ModKey> ParseAllFromFolder(string modFolderPath)
		{
			var allKeys = new List<ModKey>();
			if (!Directory.Exists(modFolderPath))
				return allKeys;

			string[] iniFiles;
			try
			{
				iniFiles = Directory.GetFiles(modFolderPath, Core.Constants.FileNames.IniExtension, SearchOption.AllDirectories);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Debug.WriteLine($"搜索 ini 文件失败: {modFolderPath}, {ex.Message}");
				return allKeys;
			}

			foreach (var iniFile in iniFiles)
			{
				// 跳过父目录名以 "disabled" 开头的文件夹中的文件
				string? parentDir = Path.GetDirectoryName(iniFile);
				string? parentDirName = parentDir != null ? Path.GetFileName(parentDir) : null;

				// 只有当父文件夹不是 modFolderPath 本身，并且父文件夹名以 "disabled" 开头时，才跳过
				if (parentDir != null &&
					!parentDir.Equals(modFolderPath, StringComparison.OrdinalIgnoreCase) &&
					!string.IsNullOrEmpty(parentDirName) &&
					parentDirName.StartsWith(ModCategoryService.DisabledPrefix, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				// 跳过文件名以 "disabled" 开头的 ini 文件
				string fileName = Path.GetFileName(iniFile);
				if (fileName.StartsWith(ModCategoryService.DisabledPrefix, StringComparison.OrdinalIgnoreCase))
					continue;

				if (ContainsKeySection(iniFile))
				{
					var keys = ParseFromIni(iniFile);
					allKeys.AddRange(keys);
				}
			}

			// 过滤掉值为空或仅含空格的按键
			allKeys = allKeys.Where(k => !string.IsNullOrWhiteSpace(k.KeyValue)).ToList();
			return allKeys;
		}

		public static string? FindKeyIniFile(string modFolderPath)
		{
			// 先检查常见名称（不区分子目录）
			string[] commonNames = { "mod.ini", "config.ini", "settings.ini", "keys.ini" };
			foreach (var name in commonNames)
			{
				string testPath = Path.Combine(modFolderPath, name);
				if (File.Exists(testPath) && ContainsKeySection(testPath))
					return testPath;
			}

			// 递归搜索所有 *.ini 文件（包括子文件夹）
			try
			{
				var iniFiles = Directory.GetFiles(modFolderPath, Core.Constants.FileNames.IniExtension, SearchOption.AllDirectories);
				foreach (var file in iniFiles)
				{
					if (ContainsKeySection(file))
						return file;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Debug.WriteLine($"搜索 ini 文件时出错: {modFolderPath}, {ex.Message}");
			}

			return null;
		}
	}
}