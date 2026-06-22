using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Models;

namespace ModManager.Application.Services
{
    public class ModPresetService : IModPresetService
    {
        private readonly IPathManager _pathManager;
        private readonly string _presetDir;

        public ModPresetService(IPathManager pathManager)
        {
            _pathManager = pathManager;
            _presetDir = Path.Combine(AppContext.BaseDirectory, "Setting", Core.Constants.FileNames.PresetsFolder);
            Directory.CreateDirectory(_presetDir);
        }

        // ==================== JSON 持久化 ====================

        public async Task<List<ModPreset>> LoadPresetsAsync(string characterName)
        {
            var path = GetPresetPath(characterName);
            if (!File.Exists(path)) return new List<ModPreset>();
            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<List<ModPreset>>(json) ?? new List<ModPreset>();
            }
            catch (Exception ex)
            {
                ModManager.Core.Helpers.Log.Error("加载预设失败", ex);
                return new List<ModPreset>();
            }
        }

        public async Task SavePresetsAsync(string characterName, List<ModPreset> presets)
        {
            Directory.CreateDirectory(_presetDir);
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var json = JsonSerializer.Serialize(presets, options);
            await File.WriteAllTextAsync(GetPresetPath(characterName), json);
        }

        private string GetPresetPath(string characterName)
        {
            var safe = string.Join("_", characterName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_presetDir, $"{safe}.json");
        }

        // ==================== 扫描 Mod 变量 ====================

        public async Task<List<PresetVar>> ScanModVarsAsync(string characterName)
        {
            var vars = new List<PresetVar>();
            var charDir = Path.Combine(_pathManager.ModsFolderPath, Core.Constants.FileNames.CharacterFolder, characterName);
            Debug.WriteLine($"[Preset] ScanModVars: charDir={charDir}");
            if (!Directory.Exists(charDir)) { Debug.WriteLine("[Preset] ScanModVars: charDir not found"); return vars; }

            var modsRoot = _pathManager.ModsFolderPath;

            foreach (var modDir in Directory.GetDirectories(charDir))
            {
                var modFolderName = Path.GetFileName(modDir);
                // DISABLED mod 没有键名，跳过
                if (modFolderName.StartsWith("DISABLED", StringComparison.OrdinalIgnoreCase))
                    continue;
                var iniFiles = Directory.GetFiles(modDir, Core.Constants.FileNames.IniExtension, SearchOption.AllDirectories);
                Debug.WriteLine($"[Preset] ScanModVars: mod={modFolderName}, iniFiles={iniFiles.Length}");
                foreach (var iniPath in iniFiles)
                {
                    // 先解析 namespace：声明优先，否则用文件路径
                    var declaredNs = ParseNamespaceDeclaration(iniPath);
                    string ns;
                    if (declaredNs != null)
                    {
                        ns = declaredNs.Replace('/', '\\');
                        Debug.WriteLine($"[Preset]   ini={Path.GetFileName(iniPath)}, namespace(declared)={ns}");
                    }
                    else
                    {
                        ns = GetRelativeIniPath(modsRoot, iniPath).Replace('/', '\\');
                        ns = StripDisabledPrefix(ns);
                        Debug.WriteLine($"[Preset]   ini={Path.GetFileName(iniPath)}, namespace(path)={ns}");
                    }

                    var persistVars = ParseGlobalPersistVars(iniPath);
                    foreach (var (varName, initialValue) in persistVars)
                    {
                        var key = $"$\\{LowerAscii(ns)}\\{varName.ToLowerInvariant()}";
                        Debug.WriteLine($"[Preset]     $ {varName}={initialValue} → {key}");
                        vars.Add(new PresetVar
                        {
                            VariableName = varName,
                            GlobalKey = key,
                            ModFolderName = modFolderName,
                            CurrentValue = initialValue,
                            TargetValue = initialValue
                        });
                    }
                }
            }
            Debug.WriteLine($"[Preset] ScanModVars: total vars={vars.Count}");
            return vars;
        }

        // ==================== 读取 d3dx_user.ini ====================

        public async Task ReadCurrentValuesAsync(string characterName, List<ModPreset> presets)
        {
            var userIni = GetD3dxUserIniPath();
            Debug.WriteLine($"[Preset] ReadCurrentValues: userIni={userIni}, exists={File.Exists(userIni)}");
            if (!File.Exists(userIni)) return;

            var lines = await File.ReadAllLinesAsync(userIni);
            var currentValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(";") || trimmed.StartsWith("[")) continue;
                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                var key = trimmed.Substring(0, eq).Trim();
                if (double.TryParse(trimmed.Substring(eq + 1).Trim(), out var val))
                    currentValues[key] = val;
            }
            Debug.WriteLine($"[Preset] ReadCurrentValues: parsed {currentValues.Count} keys from user ini");

            int matched = 0;
            foreach (var preset in presets)
            {
                foreach (var v in preset.Variables)
                {
                    if (currentValues.TryGetValue(v.GlobalKey, out var cv))
                    {
                        v.CurrentValue = cv;
                        matched++;
                    }
                }
                foreach (var v in preset.Variables.Where(v => v.TargetValue == 0 && v.CurrentValue != 0))
                    v.TargetValue = v.CurrentValue;
            }
            Debug.WriteLine($"[Preset] ReadCurrentValues: matched {matched} vars");

            await Task.CompletedTask;
        }

        // ==================== 写入 d3dx_preset.ini ====================

        public async Task WritePresetAsync(List<PresetVar> entries)
        {
            if (entries.Count == 0) return;

            var presetPath = GetD3dxPresetIniPath();
            Debug.WriteLine($"[Preset] WritePreset: {entries.Count} entries → {presetPath}");
            var sb = new StringBuilder();
            sb.AppendLine("[Constants]");
            foreach (var e in entries)
            {
                Debug.WriteLine($"[Preset]   {e.GlobalKey} = {e.TargetValue}");
                sb.AppendLine($"{e.GlobalKey} = {e.TargetValue}");
            }

            await File.WriteAllTextAsync(presetPath, sb.ToString());
            Debug.WriteLine($"[Preset] 写入完成");
			ModManager.Core.Helpers.Log.Info($"已写入 {entries.Count} 条预设到 d3dx_preset.ini");
        }

        // ==================== GlobalKey 构造 ====================

        public string BuildGlobalKey(string modsRoot, string relativePath, string varName)
        {
            var ns = relativePath.TrimStart(Path.DirectorySeparatorChar, '/').Replace('/', '\\');
            ns = StripDisabledPrefix(ns);
            return $"$\\{LowerAscii(ns)}\\{varName.ToLowerInvariant()}";
        }

        /// <summary>去掉路径中每个文件夹名的 DISABLED 前缀（DLL namespace 不带此前缀）</summary>
        private static string StripDisabledPrefix(string path)
        {
            var parts = path.Split('\\');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("DISABLED", StringComparison.OrdinalIgnoreCase))
                    parts[i] = parts[i]["DISABLED".Length..];
            }
            return string.Join("\\", parts);
        }

        // ==================== 辅助方法 ====================

        private string GetRelativeIniPath(string modsRoot, string iniPath)
        {
            // 从 modsRoot 的 PARENT 开始计算相对路径
            var parent = Path.GetDirectoryName(modsRoot); // e.g. D:\GIMI
            if (parent == null) return iniPath;
            var fullModsRoot = Path.GetFullPath(modsRoot);
            var fullIniPath = Path.GetFullPath(iniPath);
            if (!fullIniPath.StartsWith(fullModsRoot, StringComparison.OrdinalIgnoreCase))
                return iniPath;
            // 相对路径包含 "Mods\" 前缀
            var modsName = Path.GetFileName(fullModsRoot);
            return modsName + fullIniPath.Substring(fullModsRoot.Length);
        }

        /// <summary>解析 INI 文件头的 namespace = xxx 声明（第一个 [Section] 之前）。无声明返回 null。</summary>
        private static string? ParseNamespaceDeclaration(string iniPath)
        {
            if (!File.Exists(iniPath)) return null;
            foreach (var line in File.ReadLines(iniPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(";") || trimmed.Length == 0) continue;
                if (trimmed.StartsWith("[")) break; // 遇到第一个 Section，停止
                if (trimmed.StartsWith("namespace", StringComparison.OrdinalIgnoreCase))
                {
                    var eq = trimmed.IndexOf('=');
                    if (eq > 0) return trimmed.Substring(eq + 1).Trim();
                }
            }
            return null;
        }

        private static List<(string varName, double initialValue)> ParseGlobalPersistVars(string iniPath)
        {
            var result = new List<(string, double)>();
            if (!File.Exists(iniPath)) return result;

            bool inConstants = false;
            foreach (var line in File.ReadLines(iniPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    var section = trimmed.TrimStart('[').TrimEnd(']');
                    inConstants = section.Equals("Constants", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inConstants) continue;
                if (!trimmed.StartsWith("global persist $", StringComparison.OrdinalIgnoreCase)) continue;

                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                var left = trimmed.Substring(0, eq).Trim();   // "global persist $hair"
                var right = trimmed.Substring(eq + 1).Trim(); // "0"
                var dollarIdx = left.IndexOf('$');
                if (dollarIdx < 0 || dollarIdx >= left.Length - 1) continue;
                var varName = left.Substring(dollarIdx + 1).Trim();
                if (double.TryParse(right, out var val))
                    result.Add((varName, val));
            }
            return result;
        }

        private static string LowerAscii(string input)
        {
            var chars = input.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] >= 'A' && chars[i] <= 'Z')
                    chars[i] = (char)(chars[i] + 32);
            return new string(chars);
        }

        private string GetD3dxUserIniPath()
        {
            var parent = Path.GetDirectoryName(_pathManager.ModsFolderPath);
            return parent != null ? Path.Combine(parent, Core.Constants.FileNames.D3dxUserIni) : Core.Constants.FileNames.D3dxUserIni;
        }

        private string GetD3dxPresetIniPath()
        {
            var parent = Path.GetDirectoryName(_pathManager.ModsFolderPath);
            return parent != null ? Path.Combine(parent, Core.Constants.FileNames.D3dxPresetIni) : Core.Constants.FileNames.D3dxPresetIni;
        }
    }
}
