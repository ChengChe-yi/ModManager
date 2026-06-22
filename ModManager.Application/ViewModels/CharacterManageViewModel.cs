using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using ModManager.Application.Services;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Models;
using ModManager.Core.Search;

namespace ModManager.Application.ViewModels
{
    public partial class CharacterManageViewModel : ObservableObject, IDisposable
    {
        // ================================================================
        // 常量
        // ================================================================
        private const string CharacterModsRoot = "Character";
        private const int SearchDebounceMs = 250;

        // ================================================================
        // 服务
        // ================================================================
        private readonly IPathManager _pathManager;
        private readonly IModDataService _dataService;
        private readonly IModCategoryService _categoryService;
        private readonly ILocalSettingsService _settingsService;
        private readonly INotificationService? _notif;

        // ================================================================
        // 内部状态
        // ================================================================
        private readonly List<CharacterCategory> _allCharacters = new();
        private HashSet<string> _knownTags = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _searchCts;
        private int _debounceVersion;
        private DateTime _lastApplyFilter = DateTime.MinValue;
        private string _lastExecutedSearchText = ""; // 防抖后实际执行的搜索文本，去重避免相同输入重复刷新
        private static int _s_vmSeq; // VM 侧事件序列号
        private DispatcherQueue? _dispatcherQueue;
        private bool _isDataLoaded;
        private bool _isLoadingModsForCharacter;
        private string? _loadingModsForCharacterName; // 防重入：正在为哪个角色加载 Mod

        // ================================================================
        // 文件监控（仅监控文件夹变更，500ms 防抖）
        // ================================================================
        private ModFileWatcher? _characterWatcher;

        // ================================================================
        // 可绑定集合
        // ================================================================
        /// <summary>筛选后的角色列表（整体替换，避免逐项刷UI）</summary>
        [ObservableProperty]
        private ObservableCollection<CharacterCategory> _filteredCharacters = new();

        /// <summary>当前选中角色下的 Mod 列表（角色模式，更换角色时整套替换以消除逐项 Add 的 UI 抖动）</summary>
        [ObservableProperty]
        private ObservableCollection<ModItem> _currentMods = new();

        /// <summary>全局 Mod 搜索结果（整体替换）</summary>
        [ObservableProperty]
        private ObservableCollection<ModItem> _filteredMods = new();

        /// <summary>当前选中 Mod 的按键列表</summary>
        public ObservableCollection<ModKey> ModKeys { get; } = new();

        /// <summary>角色筛选 — 已提交的标签条件（Enter 后生成芯片，按 Tag 匹配）</summary>
        public ObservableCollection<string> ActiveTagConditions { get; } = new();

        /// <summary>角色筛选 — 已提交的名称条件（n=前缀 Enter 后生成芯片，按角色名匹配）</summary>
        public ObservableCollection<string> ActiveNameConditions { get; } = new();

        // ================================================================
        // 可绑定属性（CommunityToolkit.Mvvm 源生成器）
        // ================================================================

        /// <summary>搜索查询文本</summary>
        [ObservableProperty]
        private string _searchQuery = "";

        /// <summary>当前选中的角色</summary>
        [ObservableProperty]
        private CharacterCategory? _selectedCharacter;

        /// <summary>当前选中的 Mod</summary>
        [ObservableProperty]
        private ModItem? _selectedMod;

        /// <summary>是否正在加载数据</summary>
        [ObservableProperty]
        private bool _isLoading;

        /// <summary>预览图</summary>
        [ObservableProperty]
        private BitmapImage? _previewImage;

        /// <summary>状态栏文本</summary>
        [ObservableProperty]
        private string _statusMessage = "";

        /// <summary>是否显示预览图区域</summary>
        [ObservableProperty]
        private bool _hasPreview;

        /// <summary>是否显示标签详情卡片</summary>
        [ObservableProperty]
        private bool _hasDetailCard;

        /// <summary>是否显示按键列表</summary>
        [ObservableProperty]
        private bool _hasModKeys;

        /// <summary>Mod 搜索关键词（| 后的部分），非空时只搜索 Mod 名</summary>
        [ObservableProperty]
        private string _modSearchText = "";

        /// <summary>是否处于 Mod 搜索模式（| 语法触发）。| 后为空时仍为 true，供 Page 层判断双击行为。</summary>
        [ObservableProperty]
        private bool _isModSearchMode;

        // ================================================================
        // 构造
        // ================================================================
        public CharacterManageViewModel(
            IPathManager pathManager,
            IModDataService dataService,
            IModCategoryService categoryService,
            ILocalSettingsService settingsService,
            INotificationService? notificationService = null)
        {
            _pathManager = pathManager;
            _dataService = dataService;
            _categoryService = categoryService;
            _settingsService = settingsService;
            _notif = notificationService;
        }

        /// <summary>设置 UI 调度器，由 Page 在构造后调用。</summary>
        public void SetDispatcherQueue(DispatcherQueue dq)
        {
            _dispatcherQueue = dq;
            Debug.WriteLine($"[CharacterVM] SetDispatcherQueue: dq={(dq != null ? "set" : "NULL")}");
        }

        // ================================================================
        // 属性变更钩子（CommunityToolkit.Mvvm 源生成器）
        // ================================================================

        /// <summary>搜索文本变化 → 防抖搜索</summary>
        partial void OnSearchQueryChanged(string value)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            _ = DebouncedSearchAsync(value, _searchCts.Token);
        }

        /// <summary>选中角色 → 加载 Mod 详情。</summary>
        partial void OnSelectedCharacterChanged(CharacterCategory? value)
        {
            var seq = Interlocked.Increment(ref _s_vmSeq);
            Debug.WriteLine($"[VM#{seq}] OnSelCharChanged: {value?.Name ?? "null"} loadingFor={_loadingModsForCharacterName ?? "null"} isModSearch={_isModSearchMode}");
            if (value == null) { Debug.WriteLine($"[VM#{seq}] →ClearModArea"); ClearModArea(); HasDetailCard = false; return; }
            if (_loadingModsForCharacterName == value.Name) { Debug.WriteLine($"[VM#{seq}] →SKIP(guarded)"); return; }
            SelectedMod = null;
            _ = LoadModsForCharacterAsync(value);
        }

        /// <summary>选中 Mod 变化 → 加载预览图和按键，并即时保存选中状态</summary>
        partial void OnSelectedModChanged(ModItem? value)
        {
            var seq = Interlocked.Increment(ref _s_vmSeq);
            Debug.WriteLine($"[VM#{seq}] OnSelModChanged: {value?.ModName ?? "null"} isModSearch={_isModSearchMode}");
            if (value == null)
            {
                ClearModDetail();
                return;
            }
            _ = LoadModDetailAsync(value);

            // 即时保存选中状态（不等 PageUnloaded，防止退出时丢失）
            if (SelectedCharacter != null)
            {
                LastSelectedMod = value.ModName;
                _ = SaveSelectionStateAsync(SelectedCharacter.Name, value.ModName);
            }
        }

        // ================================================================
        // 命令
        // ================================================================

        /// <summary>加载全部数据。forceReload=true 时忽略 _isDataLoaded 标志强制重新扫描磁盘。</summary>
        [RelayCommand]
        private async Task LoadDataAsync(bool forceReload = false)
        {
            Debug.WriteLine($"[CharacterVM] LoadDataAsync 开始, IsLoading={IsLoading}, _isDataLoaded={_isDataLoaded}, forceReload={forceReload}");
            if (IsLoading) { Debug.WriteLine("[CharacterVM] 已在加载中，跳过"); return; }
            if (_isDataLoaded && !forceReload) { Debug.WriteLine("[CharacterVM] 数据已加载，跳过重扫"); return; }
            IsLoading = true;
            StatusMessage = "正在加载角色数据...";

            try
            {
                string rootPath = GetCharacterRootPath();
                Debug.WriteLine($"[CharacterVM] 角色根路径: {rootPath}");
                Directory.CreateDirectory(rootPath);

                // 1. 加载标签全集
                _knownTags = await _dataService.GetAllTagsAsync(rootPath);
                Debug.WriteLine($"[CharacterVM] 标签全集 ({_knownTags.Count}): {string.Join(", ", _knownTags)}");

                // 2. 扫描角色文件夹
                _allCharacters.Clear();

                if (Directory.Exists(rootPath))
                {
                    var dirs = Directory.GetDirectories(rootPath);
                    Debug.WriteLine($"[CharacterVM] 找到 {dirs.Length} 个子目录");
                    foreach (string charDir in dirs)
                    {
                        string folderName = Path.GetFileName(charDir);
                        if (string.IsNullOrEmpty(folderName)) continue;
                        // 跳过名为 "disabled" 的目录（不是角色）
                        if (string.Equals(folderName, "disabled", StringComparison.OrdinalIgnoreCase)) continue;

                        bool isEnabled = !_categoryService.IsNameDisabled(folderName);
                        string displayName = _categoryService.GetDisplayName(folderName);
                        string iconPath = Path.Combine(charDir, "Icon.png");

                        var character = new CharacterCategory
                        {
                            Name = folderName,
                            DisplayName = displayName,
                            ImagePath = File.Exists(iconPath) ? iconPath : null,
                            IsEnabled = isEnabled,
                            Tags = await _dataService.GetTagsForCharacterAsync(charDir),
                            Mods = ScanModsForCharacter(charDir)
                        };

                        _allCharacters.Add(character);
                    }
                }

                // 3. 应用筛选并显示
                Debug.WriteLine($"[CharacterVM] 扫描完成, _allCharacters.Count={_allCharacters.Count}");
                ApplyFilter();
                _isDataLoaded = true;
                Debug.WriteLine($"[CharacterVM] LoadDataAsync 结束, FilteredCharacters.Count={FilteredCharacters.Count}");
			ModManager.Core.Helpers.Log.Info($"角色数据加载完成: {_allCharacters.Count} 个角色, {_knownTags.Count} 个标签");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterVM] 加载失败: {ex}");
                StatusMessage = $"加载失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>刷新数据（保留当前选中项）。强制重新扫描磁盘以反映外部变更。</summary>
        [RelayCommand]
        private async Task RefreshAsync()
        {
            string? lastCharName = SelectedCharacter?.Name;
            await LoadDataAsync(forceReload: true);

            if (lastCharName != null)
            {
                var found = FilteredCharacters.FirstOrDefault(c => c.Name == lastCharName);
                if (found != null)
                    SelectedCharacter = found;
            }
        }

        /// <summary>切换角色启用/禁用状态</summary>
        [RelayCommand]
        private async Task ToggleCharacterAsync(CharacterCategory? character)
        {
            if (character == null) return;

            string rootPath = GetCharacterRootPath();
            string oldPath = Path.Combine(rootPath, character.Name);
            string newName = _categoryService.GetToggledName(character.Name);
            string newPath = Path.Combine(rootPath, newName);

            if (!Directory.Exists(oldPath))
            {
                StatusMessage = "角色文件夹不存在";
                return;
            }
            if (Directory.Exists(newPath))
            {
                StatusMessage = "目标名称已存在";
                return;
            }

            try
            {
                await Task.Run(() => Directory.Move(oldPath, newPath));

                // 原地更新内存数据，避免全量重扫描
                character.Name = newName;
                character.DisplayName = _categoryService.GetDisplayName(newName);
                character.IsEnabled = !character.IsEnabled;
                string newIconPath = Path.Combine(newPath, "Icon.png");
                character.ImagePath = File.Exists(newIconPath) ? newIconPath : null;
                StatusMessage = character.IsEnabled ? $"已启用: {character.DisplayName}" : $"已禁用: {character.DisplayName}";
                ApplyFilter();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterVM] 重命名失败: {ex.Message}");
                StatusMessage = $"操作失败: {ex.Message}";
            }
        }

        /// <summary>原地切换角色启用/禁用状态，不重排列表（右键性能优化）</summary>
        /// <exception cref="DirectoryNotFoundException">角色文件夹不存在</exception>
        /// <exception cref="IOException">目标名称已存在、文件被占用或跨卷操作</exception>
        /// <exception cref="UnauthorizedAccessException">权限不足</exception>
        public async Task ToggleCharacterInPlaceAsync(CharacterCategory? character)
        {
            if (character == null) return;

            string rootPath = GetCharacterRootPath();
            string oldPath = Path.Combine(rootPath, character.Name);
            string newName = _categoryService.GetToggledName(character.Name);
            string newPath = Path.Combine(rootPath, newName);

            if (!Directory.Exists(oldPath))
            {
                throw new DirectoryNotFoundException($"角色文件夹不存在: {character.DisplayName}");
            }
            if (Directory.Exists(newPath))
            {
                throw new IOException($"目标名称已存在: {newName}");
            }

            // I/O 异步化，避免阻塞 UI 线程（异常直接传播给调用方处理）
            await Task.Run(() => Directory.Move(oldPath, newPath));

            // UI 线程更新内存数据 — INPC 触发 x:Bind 刷新
            character.Name = newName;
            character.DisplayName = _categoryService.GetDisplayName(newName);
            character.IsEnabled = !character.IsEnabled;
            string newIconPath = Path.Combine(newPath, "Icon.png");
            character.ImagePath = File.Exists(newIconPath) ? newIconPath : null;
            StatusMessage = character.IsEnabled ? $"已启用: {character.DisplayName}" : $"已禁用: {character.DisplayName}";
        }

        /// <summary>切换 Mod 启用/禁用状态。异常直接传播给调用方处理。</summary>
        /// <exception cref="DirectoryNotFoundException">Mod 文件夹不存在</exception>
        /// <exception cref="IOException">目标名称已存在、文件被占用或跨卷操作</exception>
        /// <exception cref="UnauthorizedAccessException">权限不足</exception>
        [RelayCommand]
        private async Task ToggleModAsync(ModItem? mod)
        {
            if (mod == null || SelectedCharacter == null) return;

            string charDir = Path.Combine(GetCharacterRootPath(), SelectedCharacter.Name);
            string oldPath = Path.Combine(charDir, mod.ModName);
            string newModName = _categoryService.GetToggledName(mod.ModName);
            string newPath = Path.Combine(charDir, newModName);

            if (!Directory.Exists(oldPath))
            {
                throw new DirectoryNotFoundException($"Mod 文件夹不存在: {mod.DisplayName}");
            }
            if (Directory.Exists(newPath))
            {
                throw new IOException($"目标名称已存在: {newModName}");
            }

            // I/O 异步化，避免阻塞 UI 线程（与 ToggleCharacterInPlaceAsync 对齐）
            await Task.Run(() => Directory.Move(oldPath, newPath));

            // 原地更新内存数据
            mod.ModName = newModName;
            mod.DisplayName = _categoryService.GetDisplayName(newModName);
            mod.Enable = !mod.Enable;
            mod.PreviewImage = _dataService.GetBestPreviewImagePath(newPath);
            StatusMessage = mod.Enable ? $"已启用: {mod.DisplayName}" : $"已禁用: {mod.DisplayName}";

            // 从缓存重建当前 Mod 列表
            RebuildCurrentModsFromCache(SelectedCharacter);
        }

        private void RebuildCurrentModsFromCache(CharacterCategory character)
        {
            var sorted = character.Mods
                .OrderBy(m => m.Enable ? 0 : 1)
                .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RunOnUI(() =>
            {
                CurrentMods.Clear();
                foreach (var m in sorted)
                    CurrentMods.Add(m);
            });
        }

        // ================================================================
        // 自动部署（数据包 → Character/ 文件夹）
        // ================================================================

        private static async Task AutoSetupCharacterDataAsync(string charRoot)
        {
            try
            {
                var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
                var dbPath = Path.Combine(assetsDir, Core.Constants.FileNames.CharactersDatabase);
                var iconDir = Path.Combine(assetsDir, "CharacterIcons");
                if (!File.Exists(dbPath)) return;

                var db = System.Text.Json.JsonSerializer.Deserialize<List<CharacterDbEntry>>(
                    await File.ReadAllTextAsync(dbPath));
                if (db == null) return;

                foreach (var entry in db)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                    var charDir = Path.Combine(charRoot, entry.Name);
                    if (!Directory.Exists(charDir)) continue;

                    // info.json
                    var infoPath = Path.Combine(charDir, Core.Constants.FileNames.CharacterInfo);
                    if (!File.Exists(infoPath) && entry.Tags?.Count > 0)
                    {
                        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                        var json = System.Text.Json.JsonSerializer.Serialize(entry.Tags, opts);
                        await File.WriteAllTextAsync(infoPath, json);
                        Debug.WriteLine($"[AutoSetup] 写入 info.json: {entry.Name}");
                    }

                    // Icon.png
                    var iconPath = Path.Combine(charDir, "Icon.png");
                    if (!File.Exists(iconPath) && Directory.Exists(iconDir))
                    {
                        var srcIcon = Path.Combine(iconDir, $"{entry.Name}.png");
                        if (File.Exists(srcIcon))
                        {
                            File.Copy(srcIcon, iconPath);
                            Debug.WriteLine($"[AutoSetup] 复制头像: {entry.Name}");
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[AutoSetup] 失败: {ex.Message}"); }
        }

        private class CharacterDbEntry
        {
            public string Name { get; set; } = "";
            public Dictionary<string, string>? Tags { get; set; }
        }

        // ================================================================
        // 路径
        // ================================================================

        public string GetCharacterRootPath()
            => Path.Combine(_pathManager.ModsFolderPath, CharacterModsRoot);

        public string GetModsFolderPath()
            => _pathManager.ModsFolderPath;

        public string GetToggledName(string name)
            => _categoryService.GetToggledName(name);

        /// <summary>根据 Mod 反查其所属角色。用于 Mod 搜索模式下右键操作。</summary>
        public CharacterCategory? GetCharacterForMod(ModItem mod)
        {
            return _allCharacters.FirstOrDefault(c =>
                c.Mods.Any(m => m.ModName == mod.ModName));
        }

        // ================================================================
        // 选中记忆
        // ================================================================

        public string? LastSelectedCharacter { get; set; }
        public string? LastSelectedMod { get; set; }

        public async Task SaveSelectionStateAsync(string? character, string? mod)
        {
            if (!string.IsNullOrEmpty(character))
                await _settingsService.SaveSettingAsync(LocalSettingsService.LastSelectedCharacterKey, character);
            else
                await _settingsService.RemoveSettingAsync(LocalSettingsService.LastSelectedCharacterKey);

            if (!string.IsNullOrEmpty(mod))
                await _settingsService.SaveSettingAsync(LocalSettingsService.LastSelectedCharacterModKey, mod);
            else
                await _settingsService.RemoveSettingAsync(LocalSettingsService.LastSelectedCharacterModKey);
        }

        public async Task<string?> GetSavedCharacterAsync()
            => await _settingsService.ReadSettingAsync<string>(LocalSettingsService.LastSelectedCharacterKey);

        public async Task<string?> GetSavedModAsync()
            => await _settingsService.ReadSettingAsync<string>(LocalSettingsService.LastSelectedCharacterModKey);

        // ================================================================
        // 搜索
        // ================================================================

        private async Task DebouncedSearchAsync(string query, CancellationToken ct)
        {
            try
            {
                await Task.Delay(SearchDebounceMs, ct);

                if (string.IsNullOrWhiteSpace(query))
                {
                    // 空查询 → 显示全部
                    ApplyFilter(null);
                    StatusMessage = $"共 {FilteredCharacters.Count} 个角色";
                    return;
                }

                var expr = TagSearchParser.Parse(query, _knownTags);
                Debug.WriteLine($"[CharacterVM] 搜索: \"{query}\" → {expr}");
                ApplyFilter(expr);
                StatusMessage = $"搜索 \"{query}\": {FilteredCharacters.Count} 个结果";
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterVM] 搜索异常: {ex.Message}");
            }
        }

        private void ApplyFilter(SearchExpression? expr)
        {
            IEnumerable<CharacterCategory> source = _allCharacters;

            if (expr != null && !expr.IsEmpty)
                source = source.Where(c => TagSearchParser.IsMatch(c.DisplayName, c.Tags, expr));

            var sorted = source
                .OrderBy(c => c.IsEnabled ? 0 : 1)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RunOnUI(() =>
            {
                var previousSelection = SelectedCharacter;
                // 替换整个集合（1 次 PropertyChanged + ItemsSource 重绑），
                // 而非 Clear + N 次 Add（N+1 次 CollectionChanged + 逐项布局）
                FilteredCharacters = new ObservableCollection<CharacterCategory>(sorted);

                // 尝试恢复选中
                if (previousSelection != null)
                {
                    var found = FilteredCharacters.FirstOrDefault(c => c.Name == previousSelection.Name);
                    SelectedCharacter = found;
                }
            });
        }

        // ================================================================
        // 搜索处理
        // ================================================================

        /// <summary>Enter 提交：n=xxx→名称芯片，|后→Mod搜索，否则→标签芯片。</summary>
        public void ProcessSearchInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;
            var v = Interlocked.Increment(ref _debounceVersion);
            Debug.WriteLine($"[CharacterVM] ProcessSearchInput: cancel debounce → ver={v}");
            input = input.Trim();

            int pipeIdx = input.IndexOf('|');
            string condPart = pipeIdx >= 0 ? input[..pipeIdx].Trim() : input;
            string? modPart = pipeIdx >= 0 ? input[(pipeIdx + 1)..].Trim() : null;
            bool added = false;

            foreach (string t in condPart.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (t.StartsWith("n=", StringComparison.OrdinalIgnoreCase))
                {
                    string name = t[2..].Trim();
                    if (!string.IsNullOrEmpty(name) && !ActiveNameConditions.Contains(name, StringComparer.OrdinalIgnoreCase))
                    { ActiveNameConditions.Add(name); added = true; }
                }
                else if (!ActiveTagConditions.Contains(t, StringComparer.OrdinalIgnoreCase))
                { ActiveTagConditions.Add(t); added = true; }
            }

            // | 出现即进入 Mod 搜索模式，后面为空时显示全部 Mod
            if (pipeIdx >= 0)
            {
                _isModSearchMode = true;
                ModSearchText = modPart ?? "";
                added = true;
            }

            if (added) { Debug.WriteLine("[CharacterVM] ProcessSearchInput → ApplyFilter"); ApplyFilter(); }
        }

        public void RemoveTagCondition(string tag)
        { Debug.WriteLine($"[CharacterVM] RemoveTagCondition({tag})"); if (ActiveTagConditions.Remove(tag)) ApplyFilter(); }

        public void RemoveNameCondition(string name)
        { Debug.WriteLine($"[CharacterVM] RemoveNameCondition({name})"); if (ActiveNameConditions.Remove(name)) ApplyFilter(); }

        public void ClearModSearch()
        { Debug.WriteLine("[CharacterVM] ClearModSearch"); if (!string.IsNullOrEmpty(ModSearchText) || _isModSearchMode) { _isModSearchMode = false; ModSearchText = ""; ApplyFilter(); } }

        public void ReapplyFilter() { Debug.WriteLine("[CharacterVM] ReapplyFilter → ApplyFilter"); ApplyFilter(); }

        /// <summary>退出 Mod 搜索模式，恢复角色列表，选中目标 Mod 所属角色和该 Mod。
        /// 供 Page 层在用户双击搜索结果 Mod 时调用。返回 Task 等待 UI 同步完成。
        ///
        /// 关键：先填充 CurrentMods 再更新 FilteredCharacters，避免模列表经历"空→有数据"的视觉跳动。</summary>
        public async Task ExitModSearchAndSelectModAsync(ModItem mod)
        {
            Debug.WriteLine($"[CharacterVM] ExitModSearchAndSelectMod START: mod={mod.ModName} isModSearch={_isModSearchMode} modText='{ModSearchText}' curMods={CurrentMods.Count} filtMods={FilteredMods.Count}");
            _isModSearchMode = false;
            ModSearchText = "";
            _lastExecutedSearchText = "";

            // 查找所属角色
            var owner = _allCharacters.FirstOrDefault(c =>
                c.Mods.Any(m => m.ModName == mod.ModName));
            if (owner == null) { Debug.WriteLine("[CharacterVM] ExitModSearch: owner not found"); return; }
            Debug.WriteLine($"[CharacterVM] ExitModSearch: owner={owner.Name}");

            // 1) 预构建目标角色的 Mod 列表
            var targetMods = owner.Mods
                .OrderBy(m => m.Enable ? 0 : 1)
                .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Debug.WriteLine($"[CharacterVM] ExitModSearch: targetMods={targetMods.Count}");

            // 2) 构建筛选后的角色列表
            var hasChips = ActiveTagConditions.Count > 0 || ActiveNameConditions.Count > 0;
            var source = hasChips ? ApplyChipFilter(_allCharacters) : _allCharacters;
            var chars = source
                .OrderBy(c => c.IsEnabled ? 0 : 1)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Debug.WriteLine($"[CharacterVM] ExitModSearch: chars={chars.Count}");

            // 3) 强制清除旧选中 + 绕过 ApplyFilter 100ms 去重
            SelectedCharacter = null;
            _lastApplyFilter = DateTime.MinValue;

            // 4) 在 CurrentMods 中找到同名 Mod
            var match = targetMods.FirstOrDefault(m => m.ModName == mod.ModName);
            Debug.WriteLine($"[CharacterVM] ExitModSearch: match={match?.ModName ?? "NOT FOUND"} among {targetMods.Count} targetMods");

            // 5) 设 _loadingModsForCharacterName 防重入，防止 SelectedCharacter 赋值
            //    触发 LoadModsForCharacterAsync 再次 Clear+Add CurrentMods
            _loadingModsForCharacterName = owner.Name;

            // 6) 一次 RunOnUI 完成全部 UI 更新：角色列表、Mod列表、选中角色、选中Mod
            RunOnUI(() =>
            {
                Debug.WriteLine($"[CharacterVM] ExitModSearch RunOnUI: chars={chars.Count} mods={targetMods.Count} match={match?.ModName ?? "null"} owner={owner.Name}");
                Debug.WriteLine($"[CharacterVM] ExitModSearch RunOnUI: ownerInChars={chars.Contains(owner)}");
                // 先清 SelectedMod（防止 FilteredMods 变更时恢复旧选中）
                SelectedMod = null;
                // 恢复角色列表
                FilteredCharacters = new ObservableCollection<CharacterCategory>(chars);
                // 清掉搜索结果
                FilteredMods = new ObservableCollection<ModItem>();
                // 填充 Mod 列表
                CurrentMods.Clear();
                foreach (var m in targetMods)
                    CurrentMods.Add(m);
                // 选中角色（已在 RunOnUI 外设了 guard，OnSelectedCharacterChanged 会 skip）
                SelectedCharacter = owner;
                HasDetailCard = true;
                StatusMessage = $"已加载 {chars.Count} 个角色";
            });
            await FlushUIAsync();
            Debug.WriteLine($"[CharacterVM] ExitModSearch: after FlushUI CurrentMods={CurrentMods.Count} SelectedChar={SelectedCharacter?.Name}");

            // 7) 选中 Mod（放在 RunOnUI 之后，确保 CurrentMods 已就绪 + Page 已切换 ItemsSource）
            if (match != null)
            {
                SelectedMod = match;
                await FlushUIAsync();
            }

            // 8) 释放防重入守卫
            _loadingModsForCharacterName = null;
            Debug.WriteLine($"[CharacterVM] ExitModSearchAndSelectMod DONE: SelectedMod={SelectedMod?.ModName ?? "null"}");
        }

/// <summary>加载数据并恢复选中角色（一次渲染，避免闪烁）</summary>
        public async Task InitializeAsync()
        {
            await _pathManager.InitializeAsync();
            await _settingsService.InitializeAsync();
            Debug.WriteLine($"[CharacterVM] InitializeAsync 完成, ModsFolderPath={_pathManager.ModsFolderPath}");
        }

        /// <summary>释放内存缓存（PageUnloaded 时调用）。</summary>
        public void ClearCaches()
        {
            StopFileWatcher();
            _allCharacters.Clear();
            _knownTags.Clear();
            _isDataLoaded = false;
            _isModSearchMode = false;
            ModSearchText = "";
            _lastExecutedSearchText = "";
            Debug.WriteLine("[CharacterVM] ClearCaches: 已释放全部内存缓存");
        }

        // ================================================================
        // 文件监控（仅监控文件夹增删改，忽略文件变更，500ms 防抖）
        // ================================================================

        /// <summary>启动 Character 目录文件监控。由 Page.Loaded 调用。</summary>
        public async Task StartFileWatcherAsync()
        {
            string rootPath = GetCharacterRootPath();
            Directory.CreateDirectory(rootPath);

            if (_dispatcherQueue == null)
                throw new InvalidOperationException("DispatcherQueue 未设置，请先调用 SetDispatcherQueue()。");
            _characterWatcher = new ModFileWatcher(rootPath, _dispatcherQueue, OnCharacterRefreshAsync, ignoreDepth: 3);
            await _characterWatcher.StartAsync();
            Debug.WriteLine("[CharacterVM] 文件监控已启动（复用 ModFileWatcher）");
        }

        /// <summary>停止并释放文件监控。由 Page.Unloaded 调用。</summary>
        public void StopFileWatcher()
        {
            _characterWatcher?.Stop();
            _characterWatcher = null;
            Debug.WriteLine("[CharacterVM] 文件监控已停止");
        }

        /// <summary>暂停文件监控（右键操作前调用）。支持嵌套。</summary>
        public void PauseFileWatcher()
        {
            _characterWatcher?.Pause();
        }

        /// <summary>恢复文件监控（右键操作后调用）。支持嵌套。</summary>
        public void ResumeFileWatcher()
        {
            _characterWatcher?.Resume();
        }

        /// <summary>过滤：Character/ 下 ≥3 层的变更忽略（即 Mod 内部文件变更不触发刷新）。
        /// 层级：Character/{角色名}/ = 1 层, Character/{角色名}/{Mod}/ = 2 层。</summary>
        private async Task OnCharacterRefreshAsync()
        {
            Debug.WriteLine("[CharacterVM] 检测到 Character 目录变更，刷新列表");
            await RefreshCommand.ExecuteAsync(null);
            _notif?.Show(new NotificationItem
            {
                Title = "角色列表已刷新",
                Message = "检测到文件变更，列表已自动更新",
                Type = NotificationType.Info,
                Duration = TimeSpan.FromSeconds(2)
            });
        }

        public async Task LoadDataAndSelectAsync(string? savedCharacterName, string? savedModName = null)
        {
            Debug.WriteLine($"[CharacterVM] LoadDataAndSelectAsync 开始, savedChar={savedCharacterName ?? "(null)"}, savedMod={savedModName ?? "(null)"}, ModsFolderPath={_pathManager.ModsFolderPath}");
            await LoadDataAsync();

            await FlushUIAsync();

            Debug.WriteLine($"[CharacterVM] FlushUI 后, FilteredCharacters.Count={FilteredCharacters.Count}, _allCharacters.Count={_allCharacters.Count}");

            if (!string.IsNullOrEmpty(savedCharacterName))
            {
                var found = FilteredCharacters.FirstOrDefault(c => c.Name == savedCharacterName);
                Debug.WriteLine($"[CharacterVM] 查找保存角色 '{savedCharacterName}': {(found != null ? "找到" : "未找到")}");
                if (found != null)
                {
                    SelectedCharacter = found;
                    await FlushUIAsync(); // 等待 CurrentMods 填充

                    // 恢复 Mod 选中 — 从 CurrentMods 中查找（确保引用一致）
                    if (!string.IsNullOrEmpty(savedModName))
                    {
                        var savedMod = CurrentMods.FirstOrDefault(m => m.ModName == savedModName);
                        if (savedMod != null)
                        {
                            SelectedMod = savedMod;
                            Debug.WriteLine($"[CharacterVM] Mod 选中恢复: {savedModName} (from CurrentMods)");
                        }
                        else
                        {
                            Debug.WriteLine($"[CharacterVM] Mod 选中恢复失败: {savedModName} 不在 CurrentMods 中");
                        }
                    }
                }
            }
        }

        /// <summary>清除全部（标签 + 名称 + Mod搜索），显示所有角色</summary>
        public void ClearAllConditions()
        {
            bool hadAny = ActiveTagConditions.Count > 0 || ActiveNameConditions.Count > 0 || !string.IsNullOrEmpty(ModSearchText) || _isModSearchMode;
            if (!hadAny) return;
            Interlocked.Increment(ref _debounceVersion);
            Debug.WriteLine("[CharacterVM] ClearAllConditions → ApplyFilter");
            ActiveTagConditions.Clear();
            ActiveNameConditions.Clear();
            _isModSearchMode = false;
            ModSearchText = "";
            _lastExecutedSearchText = "";
            ApplyFilter();
        }

        /// <summary>实时输入 → 无|搜角色列表，有|搜符合条件的Mod总体。</summary>
        public void OnSearchTextChanged(string text)
        {
            var seq = Interlocked.Increment(ref _s_vmSeq);
            Debug.WriteLine($"[VM#{seq}] OnSearchTextChanged: text='{text}' len={text?.Length ?? -1} chips={ActiveTagConditions.Count}+{ActiveNameConditions.Count} isModSearch={_isModSearchMode} lastExec='{_lastExecutedSearchText}'");
            bool hasChips = ActiveTagConditions.Count > 0 || ActiveNameConditions.Count > 0;
            if (hasChips && string.IsNullOrWhiteSpace(text))
            {
                // 去重：退出 Mod 搜索后 ExitModSearchAndSelectModAsync 已设 _lastExecutedSearchText=""
                // 且已正确构建了芯片筛选列表，此处重复调用 ApplyFilter 会覆盖退出方法的选中状态
                if (text == _lastExecutedSearchText && FilteredCharacters.Count > 0)
                {
                    Debug.WriteLine($"[VM#{seq}] EARLY-RETURN SKIP: chips+empty but FilteredChars={FilteredCharacters.Count} already built");
                    return;
                }
                Debug.WriteLine($"[VM#{seq}] EARLY-RETURN: chips+empty→ApplyFilter");
                _isModSearchMode = false;
                ModSearchText = "";
                _lastExecutedSearchText = "";
                CancelPendingSearch();
                ApplyFilter();
                return;
            }
            int ver = Interlocked.Increment(ref _debounceVersion);
            Debug.WriteLine($"[VM#{seq}] →queue DebouncedPreview ver={ver}");
            _ = DebouncedPreviewAsync(text, ver);
        }

        /// <summary>取消所有待处理的防抖（版本号+1）。</summary>
        public void CancelPendingSearch() { var v = Interlocked.Increment(ref _debounceVersion); Debug.WriteLine($"[CharacterVM] CancelPendingSearch → ver={v}"); }

        private async Task DebouncedPreviewAsync(string input, int ver)
        {
            await Task.Delay(SearchDebounceMs);
            var cur = Volatile.Read(ref _debounceVersion);
            if (ver != cur) { Debug.WriteLine($"[VM] Debounced SKIP-ver: ver={ver} cur={cur}"); return; }

            input = input.Trim();
            if (input == _lastExecutedSearchText) { Debug.WriteLine($"[VM] Debounced SKIP-dup: '{input}'"); return; }
            _lastExecutedSearchText = input;

            var seq = Interlocked.Increment(ref _s_vmSeq);
            Debug.WriteLine($"[VM#{seq}] Debounced EXEC: input='{input}' chips={ActiveTagConditions.Count}+{ActiveNameConditions.Count} ver={ver}");
            if (string.IsNullOrWhiteSpace(input)) { _isModSearchMode = false; ModSearchText = ""; Debug.WriteLine($"[VM#{seq}] Debounced→ApplyFilter(empty)"); ApplyFilter(); return; }
            int pipeIdx = input.IndexOf('|');
            if (pipeIdx >= 0)
            {
                Debug.WriteLine($"[VM#{seq}] Debounced→pipe: pipeIdx={pipeIdx} modKw='{input[(pipeIdx + 1)..].Trim()}'");
                _isModSearchMode = true;
                string modKw = input[(pipeIdx + 1)..].Trim();
                ModSearchText = modKw;
                SearchMods(modKw);
            }
            else
            {
                Debug.WriteLine($"[VM#{seq}] Debounced→chars: '{input}'");
                _isModSearchMode = false;
                ModSearchText = "";
                SearchChars(input);
            }
        }

        private void SearchChars(string keyword)
        {
            var source = ApplyChipFilter(_allCharacters);
            var m = source.Where(c =>
                    c.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    c.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => c.IsEnabled ? 0 : 1).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            RunOnUI(() =>
            {
                FilteredCharacters = new ObservableCollection<CharacterCategory>(m);
                FilteredMods = new ObservableCollection<ModItem>();
                StatusMessage = $"\"{keyword}\" → {m.Count} 个角色";
                EnsureSelectedInList(m);
            });
        }

        private void RebuildAggregateMods()
        {
            var matched = ApplyChipFilter(_allCharacters).ToList();
            var mods = new List<ModItem>();
            foreach (var ch in matched)
                foreach (var m in ch.Mods) mods.Add(m);
            FilteredMods = new ObservableCollection<ModItem>(
                mods.OrderBy(m => m.Enable ? 0 : 1).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase));
        }

        private void SearchMods(string keyword)
        {
            var seq = Interlocked.Increment(ref _s_vmSeq);
            Debug.WriteLine($"[VM#{seq}] SearchMods IN: keyword='{keyword}' chips={ActiveTagConditions.Count}+{ActiveNameConditions.Count} allChars={_allCharacters.Count}");
            var source = ApplyChipFilter(_allCharacters);
            var mods = new List<ModItem>();
            bool matchAll = string.IsNullOrEmpty(keyword);
            int charsWithMods = 0;
            foreach (var ch in source)
            {
                bool added = false;
                foreach (var m in ch.Mods)
                {
                    if (matchAll || m.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        mods.Add(m);
                        added = true;
                    }
                }
                if (added) charsWithMods++;
            }
            var sm = mods.OrderBy(m => m.Enable ? 0 : 1).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            Debug.WriteLine($"[VM#{seq}] SearchMods RESULT: matchAll={matchAll} sourceChars={source.Count()} mods={sm.Count}");

            RunOnUI(() =>
            {
                Debug.WriteLine($"[VM#{seq}] SearchMods RunOnUI: FilteredChars=0 FilteredMods={sm.Count}");
                FilteredCharacters = new ObservableCollection<CharacterCategory>();
                // 顺序很关键：
                // 1) SelectedMod=null —— 防止下面 FilteredMods 变更时 Page 恢复旧选中
                // 2) FilteredMods=296 —— Page 将 ItemsSource 切换到 FilteredMods
                // 3) SelectedCharacter=null —— ClearModArea→CurrentMods.Clear()，但此时
                //    ListView 的 ItemsSource 已是 FilteredMods，不受影响
                SelectedMod = null;
                FilteredMods = new ObservableCollection<ModItem>(sm);
                SelectedCharacter = null;
                StatusMessage = matchAll
                    ? $"全部 Mod → {sm.Count} 个"
                    : $"\"{keyword}\" → {sm.Count} 个 Mod";
            });
        }

        private IEnumerable<CharacterCategory> ApplyChipFilter(IEnumerable<CharacterCategory> source)
        {
            if (ActiveTagConditions.Count > 0)
            {
                source = source.Where(c =>
                    ActiveTagConditions.All(tag =>
                        c.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))));
            }
            if (ActiveNameConditions.Count > 0)
            {
                source = source.Where(c =>
                    ActiveNameConditions.All(name =>
                        c.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)));
            }
            return source;
        }

        /// <summary>应用芯片筛选。100ms 内去重防止重复刷新。</summary>
        private void ApplyFilter()
        {
            var seq = Interlocked.Increment(ref _s_vmSeq);
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastApplyFilter).TotalMilliseconds;
            if (elapsed < 100) { Debug.WriteLine($"[VM#{seq}] ApplyFilter SKIP-dedup: {elapsed:F0}ms<100ms isModSearch={_isModSearchMode}"); return; }
            _lastApplyFilter = now;
            Debug.WriteLine($"[VM#{seq}] ApplyFilter EXEC: chips={ActiveTagConditions.Count}+{ActiveNameConditions.Count} modText='{ModSearchText}' isModSearch={_isModSearchMode} allChars={_allCharacters.Count}");
            bool hasChips = ActiveTagConditions.Count > 0 || ActiveNameConditions.Count > 0;

            // Mod 搜索模式（| 出现，_isModSearchMode=true）：清除角色列表，跨角色搜索 Mod
            if (_isModSearchMode)
            {
                bool matchAll = string.IsNullOrWhiteSpace(ModSearchText);
                var source = hasChips ? ApplyChipFilter(_allCharacters) : _allCharacters;
                var mods = new List<ModItem>();
                foreach (var ch in source)
                {
                    foreach (var m in ch.Mods)
                    {
                        if (matchAll || m.DisplayName.Contains(ModSearchText, StringComparison.OrdinalIgnoreCase))
                            mods.Add(m);
                    }
                }
                var sm = mods.OrderBy(m => m.Enable ? 0 : 1).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

                RunOnUI(() =>
                {
                    FilteredCharacters = new ObservableCollection<CharacterCategory>(); // 清空角色列表
                    // 顺序：SelectedMod=null→FilteredMods→SelectedCharacter=null
                    // 防止旧 SelectedMod 恢复到新列表，且 CurrentMods.Clear() 不闪烁
                    SelectedMod = null;
                    FilteredMods = new ObservableCollection<ModItem>(sm);
                    SelectedCharacter = null;
                    StatusMessage = matchAll
                        ? $"\"{DescribeConditions(ActiveTagConditions, ActiveNameConditions)}\" → {sm.Count} 个 Mod"
                        : $"\"{DescribeConditions(ActiveTagConditions, ActiveNameConditions)}\" + Mod \"{ModSearchText}\" → {sm.Count} 个 Mod";
                });
                return;
            }

            if (!hasChips)
            {
                ModSearchText = "";
                var sorted = _allCharacters
                    .OrderBy(c => c.IsEnabled ? 0 : 1)
                    .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var prevChar = SelectedCharacter;
                var prevModNC = SelectedMod;
                RunOnUI(() =>
                {
                    FilteredCharacters = new ObservableCollection<CharacterCategory>(sorted);
                    FilteredMods = new ObservableCollection<ModItem>();
                    CurrentMods.Clear();
                    StatusMessage = $"已加载 {sorted.Count} 个角色，{_knownTags.Count} 个标签";
                    EnsureSelectedInList(sorted);
                    if (prevChar != null && sorted.Contains(prevChar))
                    {
                        foreach (var m in prevChar.Mods)
                            CurrentMods.Add(m);
                        HasDetailCard = true;
                        if (prevModNC != null && prevChar.Mods.Contains(prevModNC))
                            SelectedMod = prevModNC;
                        else
                            SelectedMod = prevChar.Mods.FirstOrDefault(m => m.Enable) ?? prevChar.Mods.FirstOrDefault();
                        OnPropertyChanged(nameof(SelectedCharacter));
                    }
                });
                return;
            }

            var matched = ApplyChipFilter(_allCharacters)
                .OrderBy(c => c.IsEnabled ? 0 : 1)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sortedMods = new List<ModItem>();
            foreach (var ch in matched)
                foreach (var m in ch.Mods) sortedMods.Add(m);
            sortedMods = sortedMods.OrderBy(m => m.Enable ? 0 : 1).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            var prevChar2 = SelectedCharacter;
            var prevMod = SelectedMod;
            RunOnUI(() =>
            {
                FilteredCharacters = new ObservableCollection<CharacterCategory>(matched);
                FilteredMods = new ObservableCollection<ModItem>(sortedMods);
                StatusMessage = $"\"{DescribeConditions(ActiveTagConditions, ActiveNameConditions)}\" → {matched.Count} 个角色, {sortedMods.Count} 个 Mod";
                EnsureSelectedInList(matched);
                if (prevChar2 != null && matched.Contains(prevChar2))
                    OnPropertyChanged(nameof(SelectedCharacter));
                if (prevMod != null)
                    OnPropertyChanged(nameof(SelectedMod));
            });
        }

        /// <summary>选中角色不在列表中时清空 Mod 区域。</summary>
        private void EnsureSelectedInList(List<CharacterCategory> list)
        {
            if (SelectedCharacter != null && !list.Contains(SelectedCharacter))
            {
                SelectedCharacter = null;
                ClearModArea();
            }
        }

        private string GetConditionSummary()
            => DescribeConditions(ActiveTagConditions, new List<string>());

        private static string DescribeConditions(
            IReadOnlyList<string> tags, IReadOnlyList<string> names)
        {
            var parts = new List<string>();
            parts.AddRange(tags);
            parts.AddRange(names.Select(n => $"名称:{n}"));
            return string.Join(" + ", parts);
        }

        // ================================================================
        // 角色 Mod 加载
        // ================================================================

        private List<ModItem> ScanModsForCharacter(string characterDir)
        {
            var mods = new List<ModItem>();
            if (!Directory.Exists(characterDir)) return mods;

            foreach (string modDir in Directory.GetDirectories(characterDir))
            {
                string modName = Path.GetFileName(modDir);
                if (string.IsNullOrEmpty(modName)) continue;

                bool isEnabled = !_categoryService.IsNameDisabled(modName);
                string displayName = _categoryService.GetDisplayName(modName);
                mods.Add(new ModItem
                {
                    ModName = modName,
                    DisplayName = displayName,
                    Enable = isEnabled,
                    PreviewImage = null
                });
            }

            return mods.OrderBy(m => m.Enable ? 0 : 1)
                       .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }


        private async Task LoadModsForCharacterAsync(CharacterCategory character)
        {
            if (_isLoadingModsForCharacter) return;
            _isLoadingModsForCharacter = true;
            _loadingModsForCharacterName = character.Name;

            try
            {
            // 原地更新集合内容（保留同一 ObservableCollection 引用），避免 ItemsSource 重绑
            // 导致的 ListView 全部容器销毁重建（GlassListViewItemStyle 模板开销极大）。
            RunOnUI(() =>
            {
                var sw = Stopwatch.StartNew();

                CurrentMods.Clear();
                foreach (var m in character.Mods)
                    CurrentMods.Add(m);
                Debug.WriteLine($"[Perf] LoadModsForCharacter RunOnUI: Clear+Add {character.Mods.Count} items, elapsed={sw.ElapsedMilliseconds}ms");

                HasDetailCard = true;

                // 自动选中第一个启用的 Mod（除非 LoadDataAndSelectAsync 已预设）
                bool presetStillThere = SelectedMod != null && CurrentMods.Any(m =>
                    m.ModName == SelectedMod.ModName ||
                    m.ModName == _categoryService.GetToggledName(SelectedMod.ModName));
                if (presetStillThere)
                {
                    Debug.WriteLine($"[CharacterVM] 保持预设Mod: {SelectedMod!.ModName}");
                    var match = CurrentMods.FirstOrDefault(m =>
                        m.ModName == SelectedMod.ModName ||
                        m.ModName == _categoryService.GetToggledName(SelectedMod.ModName));
                    if (match != null && !ReferenceEquals(match, SelectedMod))
                    {
                        Debug.WriteLine($"[CharacterVM] 同步 SelectedMod 引用到 CurrentMods 中的新对象");
                        SelectedMod = match;
                    }
                    LastSelectedMod = SelectedMod!.ModName;
                }
                else
                {
                    Debug.WriteLine($"[CharacterVM] 自动选中Mod: SelectedMod={SelectedMod?.ModName ?? "(null)"}, CurrentMods.Count={CurrentMods.Count}");
                    var firstEnabled = CurrentMods.FirstOrDefault(m => m.Enable);
                    if (firstEnabled != null)
                    {
                        if (SelectedMod?.ModName != firstEnabled.ModName)
                        {
                            SelectedMod = firstEnabled;
                            LastSelectedMod = firstEnabled.ModName;
                        }
                    }
                    else if (CurrentMods.Count > 0)
                    {
                        SelectedMod = CurrentMods[0];
                        LastSelectedMod = CurrentMods[0].ModName;
                    }
                    else
                    {
                        SelectedMod = null;
                        LastSelectedMod = null;
                        ClearModDetail();
                    }
                }
            });

            await Task.CompletedTask;
            }
            finally
            {
                _isLoadingModsForCharacter = false;
                _loadingModsForCharacterName = null;
            }
        }

        private async Task LoadModDetailAsync(ModItem mod)
        {
            // Mod 搜索模式下 SelectedCharacter 为 null，从 _allCharacters 反查所属角色
            var character = SelectedCharacter
                ?? _allCharacters.FirstOrDefault(c => c.Mods.Any(m => m.ModName == mod.ModName));
            Debug.WriteLine($"[CharacterVM] LoadModDetailAsync 开始: mod={mod.ModName}, char={character?.Name ?? "(null)"}");
            if (character == null)
            {
                Debug.WriteLine("[CharacterVM] LoadModDetailAsync: character not found, skip");
                return;
            }

            string modFolder = Path.Combine(
                GetCharacterRootPath(), character.Name, mod.ModName);
            Debug.WriteLine($"[CharacterVM] LoadModDetailAsync modFolder={modFolder}, exists={Directory.Exists(modFolder)}");

            if (Directory.Exists(modFolder))
            {
                Debug.WriteLine("[CharacterVM] LoadModDetailAsync: 开始加载预览图...");
                var bitmap = await _dataService.LoadPreviewImageAsync(modFolder);
                Debug.WriteLine($"[CharacterVM] LoadModDetailAsync: 预览图={(bitmap != null ? "有图" : "null")}, 入队UI更新...");
                RunOnUI(() =>
                {
                    Debug.WriteLine($"[CharacterVM] LoadModDetailAsync.RunOnUI: 设置 PreviewImage={(bitmap != null ? "有图" : "null")}");
                    PreviewImage = bitmap;
                    HasPreview = bitmap != null;
                });

                // 加载按键
                var keys = ModKeyParser.ParseAllFromFolder(modFolder);
                Debug.WriteLine($"[CharacterVM] LoadModDetailAsync: keys={keys.Count}, 入队UI更新...");
                RunOnUI(() =>
                {
                    Debug.WriteLine($"[CharacterVM] LoadModDetailAsync.RunOnUI: 设置 ModKeys, count={keys.Count}");
                    ModKeys.Clear();
                    foreach (var k in keys)
                        ModKeys.Add(k);
                    HasModKeys = ModKeys.Count > 0;
                });
            }
            else
            {
                Debug.WriteLine("[CharacterVM] LoadModDetailAsync: 文件夹不存在，入队 ClearModDetail");
                RunOnUI(() => ClearModDetail());
            }
        }

        // ================================================================
        // 预览图操作
        // ================================================================

        public string? GetBestPreviewImagePath(string modFolderPath)
            => _dataService.GetBestPreviewImagePath(modFolderPath);

        /// <summary>延迟加载 Mod 预览图路径。</summary>
        public string? EnsureModPreviewPath(ModItem mod, string characterName)
        {
            if (!string.IsNullOrEmpty(mod.PreviewImage)) return mod.PreviewImage;
            string modPath = Path.Combine(GetCharacterRootPath(), characterName, mod.ModName);
            mod.PreviewImage = GetBestPreviewImagePath(modPath);
            return mod.PreviewImage;
        }

        public async Task<BitmapImage?> LoadPreviewImageAsync(string modFolderPath)
            => await _dataService.LoadPreviewImageAsync(modFolderPath);

        /// <summary>增量添加 Mod 到内存和 UI，避免全量刷新磁盘扫描。</summary>
        /// <returns>新创建的 ModItem，失败返回 null。</returns>
        public ModItem? AddModToCharacter(string characterName, string modFolderPath)
        {
            var character = _allCharacters.FirstOrDefault(c => c.Name == characterName);
            if (character == null) return null;

            string modName = Path.GetFileName(modFolderPath);
            if (string.IsNullOrEmpty(modName)) return null;

            bool isEnabled = !_categoryService.IsNameDisabled(modName);
            string displayName = _categoryService.GetDisplayName(modName);
            string? preview = _dataService.GetBestPreviewImagePath(modFolderPath);

            var newMod = new ModItem
            {
                ModName = modName,
                DisplayName = displayName,
                Enable = isEnabled,
                PreviewImage = preview
            };

            // 添加到角色的 Mods 列表
            character.Mods.Add(newMod);

            // 如果该角色当前被选中，更新 CurrentMods
            if (SelectedCharacter?.Name == characterName)
            {
                RebuildCurrentModsFromCache(character);
            }

            return newMod;
        }

        // ================================================================
        // UI 辅助
        // ================================================================

        private void RunOnUI(Action action)
        {
            if (_dispatcherQueue == null)
            {
                Debug.WriteLine("[CharacterVM] RunOnUI: dispatcherQueue is null, invoking synchronously");
                action();
                return;
            }
            bool enqueued = _dispatcherQueue.TryEnqueue(() => action());
            if (!enqueued)
                Debug.WriteLine("[CharacterVM] RunOnUI: TryEnqueue FAILED, falling back to synchronous invoke");
            if (!enqueued)
                action();
        }

        /// <summary>等待 DispatcherQueue 中所有待处理项完成（供 Page 层同步）</summary>
        public async Task FlushUIAsync()
        {
            if (_dispatcherQueue == null) return;
            var tcs = new TaskCompletionSource();
            _ = _dispatcherQueue.TryEnqueue(() => tcs.SetResult());
            await tcs.Task;
        }

        private void ClearModArea()
        {
            SelectedMod = null;
            CurrentMods.Clear();
            ClearModDetail();
        }

        /// <summary>强制重新加载当前选中 Mod 的预览图和按键。
        /// 用于页面重新创建或返回时，VM 的 SelectedMod 引用未变导致 source-generator
        /// setter 跳过 OnSelectedModChanged，UI 控件又已丢失渲染状态，必须显式刷新。</summary>
        public void ReloadCurrentModDetail()
        {
            if (SelectedMod == null)
            {
                Debug.WriteLine("[CharacterVM] ReloadCurrentModDetail: SelectedMod is null, skip");
                return;
            }
            Debug.WriteLine($"[CharacterVM] ReloadCurrentModDetail: force reload for {SelectedMod.ModName}");
            ClearModDetail();
            _ = LoadModDetailAsync(SelectedMod);
        }

        private void ClearModDetail()
        {
            Debug.WriteLine("[CharacterVM] ClearModDetail: 清空预览图和按键");
            ModKeys.Clear();
            PreviewImage = null;
            HasPreview = false;
            HasModKeys = false;
        }

        public void Dispose()
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
        }
    }
}
