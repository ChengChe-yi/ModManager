using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using ModManager.Application.Services;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Enums;
using ModManager.Core.Models;

namespace ModManager.Application.ViewModels
{
    public class ModManageViewModel : System.ComponentModel.INotifyPropertyChanged, IDisposable
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(nameof(IsLoading)); } } }

        // ----- 服务 -----
        private readonly IPathManager _pathManager;
        private readonly ILocalSettingsService _settingsService;
        private readonly IModCategoryService _categoryService;
        private readonly IModDataService _dataService;

        // ----- 数据源 -----
        public ObservableCollection<ModCategory> CategoryPrimaryList { get; } = new();
        public ObservableCollection<ModCategory> CategorySecondaryList { get; } = new();
        public ObservableCollection<ModItem> ModItemList { get; } = new();
        public ObservableCollection<ModKey> ModKeyList { get; } = new();

        // ----- 筛选模式 -----
        private FilterMode _secondaryFilter = FilterMode.None;
        private FilterMode _modFilter = FilterMode.None;
        public FilterMode SecondaryFilter => _secondaryFilter;
        public FilterMode ModFilter => _modFilter;

        // ----- 记忆选中的标识 -----
        public string LastSelectedPrimary { get; set; } = string.Empty;
        public string? LastSelectedSecondary { get; set; }
        public string LastSelectedMod { get; set; } = string.Empty;

        // ----- 当前 Mod INI 文件 -----
        public string CurrentModIniFile { get; set; } = string.Empty;

        // ----- 最新加载的预览图（供 Page 取用）-----
        public BitmapImage? LatestPreviewImage { get; private set; }
        public bool HasPreviewImage => LatestPreviewImage != null;

        // ----- 扫描结果缓存（消除筛选切换时的冗余磁盘扫描）-----
        private readonly Dictionary<string, List<ModCategory>> _secondaryCategoryCache
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ModItem>> _modItemCache
            = new(StringComparer.OrdinalIgnoreCase);

        // ----- 文件监控 -----
        private ModFileWatcher? _modFileWatcher;
        private FileSystemWatcher? _d3dxIniWatcher;
        private bool _isSavingKeys;
        private DateTime _lastD3dxIniChange;
        private DispatcherQueue? _dispatcherQueue;

        public ModManageViewModel(IPathManager pm, ILocalSettingsService ss,
                                  IModCategoryService cs, IModDataService ds)
        {
            _pathManager = pm;
            _settingsService = ss;
            _categoryService = cs;
            _dataService = ds;
        }

        /// <summary>设置 UI 调度器，由 Page 在构造后调用。</summary>
        public void SetDispatcherQueue(DispatcherQueue dq)
        {
            _dispatcherQueue = dq;
        }

        // ======================= 初始化 =======================
        public async Task InitializeAsync()
        {
            await _pathManager.InitializeAsync();

            var secStr = await _settingsService.ReadSettingAsync<string>(LocalSettingsService.SecondaryFilterModeKey);
            if (!string.IsNullOrEmpty(secStr) && Enum.TryParse<FilterMode>(secStr, out var secMode))
                _secondaryFilter = secMode;

            var modStr = await _settingsService.ReadSettingAsync<string>(LocalSettingsService.ModFilterModeKey);
            if (!string.IsNullOrEmpty(modStr) && Enum.TryParse<FilterMode>(modStr, out var modMode))
                _modFilter = modMode;

            LastSelectedSecondary = await _settingsService.ReadSettingAsync<string>(LocalSettingsService.LastSelectedSecondaryCategoryKey);
            LastSelectedMod = await _settingsService.ReadSettingAsync<string>(LocalSettingsService.LastSelectedModItemKey);
        }

        // ======================= 数据加载 =======================
        public void MakeSureModRepoExists()
        {
            if (!Directory.Exists(_pathManager.ModsFolderPath))
                Directory.CreateDirectory(_pathManager.ModsFolderPath);
        }

        public void LoadPrimaryCategories()
        {
            CategoryPrimaryList.Clear();
            var sorted = _dataService.GetPrimaryCategories();
            foreach (var cat in sorted)
                CategoryPrimaryList.Add(cat);
        }

        public void LoadSecondaryCategories(string primaryName)
        {
            CategorySecondaryList.Clear();
            var sorted = _dataService.GetSecondaryCategories(primaryName);
            foreach (var cat in sorted)
                CategorySecondaryList.Add(cat);
        }

        public void LoadModItems(string primaryName, string secondaryName)
        {
            ModItemList.Clear();
            var items = _dataService.GetModItems(primaryName, secondaryName);
            foreach (var m in items)
                ModItemList.Add(m);
        }

        // ======================= 筛选逻辑 =======================
        public void ApplySecondaryFilter(string primaryName)
        {
            if (!_secondaryCategoryCache.TryGetValue(primaryName, out var all))
            {
                all = _dataService.GetSecondaryCategories(primaryName);
                _secondaryCategoryCache[primaryName] = all;
            }

            IEnumerable<ModCategory> result = _secondaryFilter switch
            {
                FilterMode.EnabledOnly => all.Where(c => !c.NotEnable),
                FilterMode.DisabledOnly => all.Where(c => c.NotEnable),
                FilterMode.EnabledFirst => all.OrderBy(c => c.NotEnable ? 1 : 0)
                                               .ThenBy(c => c.DisplayName ?? c.Name, StringComparer.OrdinalIgnoreCase),
                _ => all.OrderBy(c => c.DisplayName ?? c.Name, StringComparer.OrdinalIgnoreCase)
            };

            CategorySecondaryList.Clear();
            foreach (var cat in result)
                CategorySecondaryList.Add(cat);
        }

        public void ApplyModFilter(string primaryName, string secondaryName)
        {
            string cacheKey = $"{primaryName}|{secondaryName}";
            if (!_modItemCache.TryGetValue(cacheKey, out var allMods))
            {
                allMods = _dataService.GetModItems(primaryName, secondaryName);
                _modItemCache[cacheKey] = allMods;
            }

            IEnumerable<ModItem> result = _modFilter switch
            {
                FilterMode.EnabledOnly => allMods.Where(m => m.Enable),
                FilterMode.DisabledOnly => allMods.Where(m => !m.Enable),
                FilterMode.EnabledFirst => allMods.OrderByDescending(m => m.Enable)
                                                  .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase),
                _ => allMods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            };

            ModItemList.Clear();
            foreach (var m in result)
                ModItemList.Add(m);
        }

        public void ToggleSecondaryFilter(FilterMode mode)
        {
            _secondaryFilter = (_secondaryFilter == mode) ? FilterMode.None : mode;
            _ = _settingsService.SaveSettingAsync(LocalSettingsService.SecondaryFilterModeKey, _secondaryFilter.ToString());
        }

        public void ToggleModFilter(FilterMode mode)
        {
            _modFilter = (_modFilter == mode) ? FilterMode.None : mode;
            _ = _settingsService.SaveSettingAsync(LocalSettingsService.ModFilterModeKey, _modFilter.ToString());
        }

        // ======================= 缓存管理 =======================

        /// <summary>失效指定一级分类下的二级分类缓存（重命名后调用）</summary>
        public void InvalidateSecondaryCache(string primaryName)
            => _secondaryCategoryCache.Remove(primaryName);

        /// <summary>失效指定一对分类下的 Mod 列表缓存（重命名/拖放后调用）</summary>
        public void InvalidateModItemCache(string primaryName, string secondaryName)
            => _modItemCache.Remove($"{primaryName}|{secondaryName}");

        /// <summary>清空全部扫描缓存（文件监控触发 / 页面离开时释放内存）</summary>
        public void ClearCaches()
        {
            _secondaryCategoryCache.Clear();
            _modItemCache.Clear();
        }

        // ======================= 选中记忆持久化 =======================
        public async Task SaveSelectionStateAsync(string? primary, string? secondary, string? mod)
        {
            if (!string.IsNullOrEmpty(primary))
                await _settingsService.SaveSettingAsync(LocalSettingsService.LastSelectedPrimaryCategoryKey, primary);
            else
                await _settingsService.RemoveSettingAsync(LocalSettingsService.LastSelectedPrimaryCategoryKey);

            if (!string.IsNullOrEmpty(secondary))
                await _settingsService.SaveSettingAsync(LocalSettingsService.LastSelectedSecondaryCategoryKey, secondary);
            else
                await _settingsService.RemoveSettingAsync(LocalSettingsService.LastSelectedSecondaryCategoryKey);

            if (!string.IsNullOrEmpty(mod))
                await _settingsService.SaveSettingAsync(LocalSettingsService.LastSelectedModItemKey, mod);
            else
                await _settingsService.RemoveSettingAsync(LocalSettingsService.LastSelectedModItemKey);
        }

        public async Task<string?> GetSavedPrimaryAsync()
            => await _settingsService.ReadSettingAsync<string>(LocalSettingsService.LastSelectedPrimaryCategoryKey);

        /// <summary>保存筛选模式（Page 卸载时调用）。</summary>
        public async Task SaveFilterModesAsync()
        {
            await _settingsService.SaveSettingAsync(LocalSettingsService.SecondaryFilterModeKey, _secondaryFilter.ToString());
            await _settingsService.SaveSettingAsync(LocalSettingsService.ModFilterModeKey, _modFilter.ToString());
        }

        // ======================= 路径辅助 =======================
        public string GetModsFolderPath() => _pathManager.ModsFolderPath;

        public string? GetSelectedModFolderPath(string primary, string secondary, string modName)
        {
            if (string.IsNullOrEmpty(primary) || string.IsNullOrEmpty(secondary) || string.IsNullOrEmpty(modName))
                return null;
            return Path.Combine(_pathManager.ModsFolderPath, primary, secondary, modName);
        }

        // ======================= 预览图 =======================
        public async Task<BitmapImage?> LoadPreviewImageAsync(string modFolderPath)
        {
            LatestPreviewImage = await _dataService.LoadPreviewImageAsync(modFolderPath);
            return LatestPreviewImage;
        }

        public string? GetBestPreviewImagePath(string modFolderPath)
            => _dataService.GetBestPreviewImagePath(modFolderPath);

        /// <summary>延迟加载 Mod 预览图路径。首次悬停时调用，缓存到 ModItem 上。</summary>
        public string? EnsureModPreviewPath(ModItem mod, string primary, string secondary)
        {
            if (!string.IsNullOrEmpty(mod.PreviewImage))
                return mod.PreviewImage;
            string modPath = Path.Combine(_pathManager.ModsFolderPath, primary, secondary, mod.ModName);
            mod.PreviewImage = _dataService.GetBestPreviewImagePath(modPath);
            return mod.PreviewImage;
        }

        /// <summary>为二级分类找到第一个可用 MOD 的预览图路径（用于悬停预览）。</summary>
        public string? GetFirstModPreviewPath(string primaryName, string secondaryName)
        {
            string categoryPath = Path.Combine(_pathManager.ModsFolderPath, primaryName, secondaryName);
            if (!Directory.Exists(categoryPath))
                return null;

            var firstEnabledMod = Directory.GetDirectories(categoryPath)
                .Select(Path.GetFileName)
                .Where(n => n != null && !n.StartsWith(ModCategoryService.DisabledPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (firstEnabledMod == null)
                return null;

            string modFolder = Path.Combine(categoryPath, firstEnabledMod);
            return _dataService.GetBestPreviewImagePath(modFolder);
        }

        // ======================= 按键解析 =======================
        public List<ModKey> ParseModKeys(string modFolder)
            => ModKeyParser.ParseAllFromFolder(modFolder);

        public void LoadModKeys(string modFolder)
        {
            var keys = ModKeyParser.ParseAllFromFolder(modFolder);
            ModKeyList.Clear();
            foreach (var k in keys)
                ModKeyList.Add(k);
            CurrentModIniFile = ModKeyParser.FindKeyIniFile(modFolder) ?? string.Empty;
        }

        // ======================= 重命名（启用/禁用） =======================
        public string GetToggledName(string original) => _categoryService.GetToggledName(original);

        public async Task<bool> TryRenameFolderAsync(string oldPath, string newPath)
        {
            bool moved = false;
            for (int retry = 0; retry < 3 && !moved; retry++)
            {
                try
                {
                    Directory.Move(oldPath, newPath);
                    moved = true;
                }
                catch (IOException) when (retry < 2)
                {
                    await Task.Delay(300);
                }
            }
            return moved;
        }

        // ======================= 粘贴预览图 =======================
        public async Task<string?> SaveClipboardImageToModFolderAsync(string modFolder, byte[] pixels,
            uint width, uint height, double dpiX, double dpiY,
            Windows.Graphics.Imaging.BitmapPixelFormat pixelFormat,
            Windows.Graphics.Imaging.BitmapAlphaMode alphaMode)
        {
            if (!Directory.Exists(modFolder))
                Directory.CreateDirectory(modFolder);

            string previewFilePath = Path.Combine(modFolder, "preview.png");

            using (new FileWatcherScope(_modFileWatcher))
            {
                using var memStream = new MemoryStream();
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                    memStream.AsRandomAccessStream());
                encoder.SetPixelData(pixelFormat, alphaMode, width, height, dpiX, dpiY, pixels);
                await encoder.FlushAsync();

                File.WriteAllBytes(previewFilePath, memStream.ToArray());
            }

            return previewFilePath;
        }

        // ======================= 删除预览图 =======================
        public string? DeletePreviewImage(string modFolder)
        {
            string previewFilePath = Path.Combine(modFolder, "preview.png");
            if (!File.Exists(previewFilePath))
                return null;

            using (new FileWatcherScope(_modFileWatcher))
            {
                return RenameFileToBackup(previewFilePath);
            }
        }

        private static string? RenameFileToBackup(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            string directory = Path.GetDirectoryName(filePath)!;
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            string extension = Path.GetExtension(filePath);
            string baseNewName = fileNameWithoutExtension + extension + ".back";
            string newFilePath = Path.Combine(directory, baseNewName);

            if (!File.Exists(newFilePath))
            {
                try
                {
                    File.Move(filePath, newFilePath);
                    return newFilePath;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"重命名文件失败: {filePath} -> {newFilePath}, 错误: {ex.Message}");
                    return null;
                }
            }

            int counter = 1;
            while (counter <= 10)
            {
                string candidatePath = Path.Combine(directory, baseNewName + counter.ToString());
                if (!File.Exists(candidatePath))
                {
                    try
                    {
                        File.Move(filePath, candidatePath);
                        return candidatePath;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"重命名文件失败: {filePath} -> {candidatePath}, 错误: {ex.Message}");
                        return null;
                    }
                }
                counter++;
            }

            Debug.WriteLine($"无法为重命名找到可用名称，已达上限: {filePath}");
            return null;
        }

        // ======================= 拖放复制文件夹 =======================
        public static async Task CopyFolderAsync(string sourcePath, string destPath)
        {
            Directory.CreateDirectory(destPath);
            foreach (string file in Directory.GetFiles(sourcePath))
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destPath, fileName);
                File.Copy(file, destFile, true);
            }
            foreach (string subDir in Directory.GetDirectories(sourcePath))
            {
                string dirName = Path.GetFileName(subDir);
                string destSubDir = Path.Combine(destPath, dirName);
                await CopyFolderAsync(subDir, destSubDir);
            }
        }

        // ======================= 文件监控 =======================
        public async Task StartFileWatcherAsync(Func<Task> refreshCallback)
        {
            if (_dispatcherQueue == null)
                throw new InvalidOperationException("DispatcherQueue 未设置，请先调用 SetDispatcherQueue()。");

            _modFileWatcher = new ModFileWatcher(
                _pathManager.ModsFolderPath,
                _dispatcherQueue,
                refreshCallback);
            await _modFileWatcher.StartAsync();

            // 启动 d3dx_user.ini 监控
            string? parentDir = Path.GetDirectoryName(_pathManager.ModsFolderPath);
            if (parentDir != null)
            {
                string globalIniPath = Path.Combine(parentDir, Core.Constants.FileNames.D3dxUserIni);
                if (File.Exists(globalIniPath))
                {
                    _d3dxIniWatcher = new FileSystemWatcher(parentDir, Core.Constants.FileNames.D3dxUserIni)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    _d3dxIniWatcher.Changed += OnD3dxUserIniChanged;
                }
            }
        }

        public void StopFileWatchers()
        {
            _modFileWatcher?.Stop();

            if (_d3dxIniWatcher != null)
            {
                _d3dxIniWatcher.EnableRaisingEvents = false;
                _d3dxIniWatcher.Changed -= OnD3dxUserIniChanged;
                _d3dxIniWatcher.Dispose();
                _d3dxIniWatcher = null;
            }
        }

        public void PauseFileWatcher() => _modFileWatcher?.Pause();
        public void ResumeFileWatcher() => _modFileWatcher?.Resume();

        private async void OnD3dxUserIniChanged(object sender, FileSystemEventArgs e)
        {
            if (_isSavingKeys) return;

            var now = DateTime.UtcNow;
            if ((now - _lastD3dxIniChange).TotalMilliseconds < 2000)
                return;
            _lastD3dxIniChange = now;

            await Task.Delay(500);

            if (_dispatcherQueue == null) return;

            await _dispatcherQueue.EnqueueAsync(async () =>
            {
                _isSavingKeys = true;
                try
                {
                    _modFileWatcher?.Pause();
                    await ProcessD3dxIniChangesAsync();
                }
                finally
                {
                    _modFileWatcher?.Resume();
                    _isSavingKeys = false;
                }
            });
        }

        // ======================= 按键保存 =======================
        public async Task ProcessD3dxIniChangesAsync()
        {
            string? modsDir = Path.GetDirectoryName(_pathManager.ModsFolderPath);
            if (modsDir == null) return;
            string globalIni = Path.Combine(modsDir, Core.Constants.FileNames.D3dxUserIni);
            if (!File.Exists(globalIni)) return;

            var updates = ParseGlobalIniToUpdates(globalIni, _pathManager.ModsFolderPath,
                line => line.StartsWith("$\\mods\\", StringComparison.OrdinalIgnoreCase));

            foreach (var kvp in updates)
                WriteKeyValuesToIni(kvp.Key, kvp.Value);
        }

        public async Task SaveModKeysAsync(string primary, string secondary, string modName)
        {
            string? modFolder = GetSelectedModFolderPath(primary, secondary, modName);
            if (string.IsNullOrEmpty(modFolder)) return;

            string? modsDir = Path.GetDirectoryName(_pathManager.ModsFolderPath);
            if (modsDir == null) return;
            string globalIni = Path.Combine(modsDir, Core.Constants.FileNames.D3dxUserIni);
            if (!File.Exists(globalIni)) return;

            var modIniFiles = new HashSet<string>(
                Directory.GetFiles(modFolder, Core.Constants.FileNames.IniExtension, SearchOption.AllDirectories),
                StringComparer.OrdinalIgnoreCase);

            var updates = ParseGlobalIniToUpdates(globalIni, _pathManager.ModsFolderPath,
                line => line.StartsWith("$\\mods\\", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("$\\", StringComparison.OrdinalIgnoreCase));

            foreach (var kvp in updates)
            {
                if (modIniFiles.Contains(kvp.Key))
                    WriteKeyValuesToIni(kvp.Key, kvp.Value);
            }
        }

        // ======================= INI 解析辅助 =======================
        private static Dictionary<string, Dictionary<string, string?>> ParseGlobalIniToUpdates(
            string globalIniPath, string modsRoot, Func<string, bool> lineFilter)
        {
            var fileUpdates = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(globalIniPath))
                return fileUpdates;

            var allLines = File.ReadAllLines(globalIniPath, Encoding.UTF8);
            foreach (var rawLine in allLines)
            {
                var trimmed = rawLine.TrimStart();
                if (!lineFilter(trimmed))
                    continue;

                int eqIndex = trimmed.IndexOf('=');
                if (eqIndex < 0) continue;

                string pathAndKey = trimmed.Substring(0, eqIndex).TrimEnd();
                string newValue = trimmed.Substring(eqIndex + 1).Trim();

                int lastSlash = pathAndKey.LastIndexOf('\\');
                if (lastSlash < 0) continue;

                string relativeFilePath = pathAndKey.Substring(0, lastSlash);
                string keyName = pathAndKey.Substring(lastSlash + 1);

                string fullIniPath;
                if (relativeFilePath.StartsWith("$\\mods\\", StringComparison.OrdinalIgnoreCase))
                {
                    string relative = relativeFilePath.Substring("$\\mods\\".Length);
                    fullIniPath = Path.Combine(modsRoot, relative);
                }
                else if (relativeFilePath.StartsWith("$\\", StringComparison.OrdinalIgnoreCase))
                {
                    string absolutePath = relativeFilePath.Substring(2);
                    fullIniPath = Path.GetFullPath(absolutePath);
                }
                else
                {
                    continue;
                }

                if (!File.Exists(fullIniPath))
                    continue;

                if (!fileUpdates.TryGetValue(fullIniPath, out var keyDict))
                {
                    keyDict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    fileUpdates[fullIniPath] = keyDict;
                }
                keyDict[keyName] = newValue;
            }

            return fileUpdates;
        }

        private static void WriteKeyValuesToIni(string iniPath, Dictionary<string, string?> keyValues)
        {
            if (!File.Exists(iniPath) || keyValues.Count == 0)
                return;

            var lines = File.ReadAllLines(iniPath, Encoding.UTF8).ToList();
            var output = new List<string>(lines.Count);

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("global persist $", StringComparison.OrdinalIgnoreCase))
                {
                    int dollarIdx = trimmed.IndexOf('$');
                    int eqIdx = trimmed.IndexOf('=', dollarIdx);
                    if (dollarIdx >= 0 && eqIdx > dollarIdx)
                    {
                        string key = trimmed.Substring(dollarIdx + 1, eqIdx - dollarIdx - 1).Trim();
                        if (keyValues.TryGetValue(key, out var newValue))
                        {
                            string beforeEq = line.Substring(0, line.IndexOf('='));
                            output.Add($"{beforeEq}= {newValue ?? "0"}");
                            continue;
                        }
                    }
                }
                output.Add(line);
            }
            File.WriteAllLines(iniPath, output, Encoding.UTF8);
        }

        public static string? GetIniNamespace(string iniPath)
        {
            try
            {
                foreach (string line in File.ReadLines(iniPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("namespace", StringComparison.OrdinalIgnoreCase))
                    {
                        int eqIdx = trimmed.IndexOf('=');
                        if (eqIdx > 0)
                            return trimmed.Substring(eqIdx + 1).Trim();
                    }
                }
            }
            catch { /* 忽略 */ }
            return null;
        }

        // ======================= 内部辅助类 =======================
        private sealed class FileWatcherScope : IDisposable
        {
            private readonly ModFileWatcher? _watcher;
            public FileWatcherScope(ModFileWatcher? watcher)
            {
                _watcher = watcher;
                _watcher?.Pause();
            }
            public void Dispose() => _watcher?.Resume();
        }

        public void Dispose()
        {
            StopFileWatchers();
        }
    }
}
