using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ModManager.Application.ViewModels;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Enums;
using ModManager.Core.Models;
using ModManager.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Path = System.IO.Path;

using WinUIApp = Microsoft.UI.Xaml.Application;

namespace ModManager.Views
{
    public sealed partial class ModManagePage : Page
    {
        private readonly ModManageViewModel _vm;
        private readonly PreviewPopupManager _previewManager;
        private readonly INotificationService? _notif;

        public ModManagePage() : this(App.GetService<ModManageViewModel>()!)
        { }

        public ModManagePage(ModManageViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            _vm.SetDispatcherQueue(DispatcherQueue);
            _previewManager = new PreviewPopupManager(this);
            _notif = App.GetService<INotificationService>();
            this.Loaded += PageLoaded;
            this.Unloaded += PageUnloaded;
        }

        #region 加载卸载

        private async void PageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _vm.IsLoading = true;
                await _vm.InitializeAsync();

                // 启动文件监控
                await _vm.StartFileWatcherAsync(OnFileWatcherRefreshAsync);

                // 恢复筛选模式
                UpdateFilterButtons(_vm.SecondaryFilter, BtnSecondaryEnabledFirst, BtnSecondaryEnabled, BtnSecondaryDisabled);
                UpdateFilterButtons(_vm.ModFilter, BtnModEnabledFirst, BtnModEnabled, BtnModDisabled);

                // 绑定数据源
                if (ListView_CategoryPrimary.ItemsSource == null)
                {
                    ListView_CategoryPrimary.ItemsSource = _vm.CategoryPrimaryList;
                    ListView_CategorySecondary.ItemsSource = _vm.CategorySecondaryList;
                    ListView_ModItem.ItemsSource = _vm.ModItemList;
                    DataGrid_ModKeyList.ItemsSource = _vm.ModKeyList;
                }

                await ReloadCategoryPrimaryAsync();
                _vm.IsLoading = false;
                ModEmptyHint.Visibility = _vm.CategoryPrimaryList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PageLoaded 异常: {ex.Message}");
            }
        }

        /// <summary>文件监控回调：刷新一级分类后恢复选中状态。</summary>
        private async Task OnFileWatcherRefreshAsync()
        {
            _vm.IsLoading = true;
            _vm.ClearCaches();  // 外部文件变更可能影响任意路径
            _vm.MakeSureModRepoExists();
            _vm.LoadPrimaryCategories();
            ModEmptyHint.Visibility = _vm.CategoryPrimaryList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _vm.IsLoading = false;
            if (_vm.CategoryPrimaryList.Count == 0) return;

            string? lastName = await _vm.GetSavedPrimaryAsync();
            int index = string.IsNullOrEmpty(lastName) ? -1 :
                        _vm.CategoryPrimaryList.ToList().FindIndex(c => c.Name == lastName);
            ListView_CategoryPrimary.SelectedIndex = index >= 0 ? index : 0;

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ScrollListViewToSelectedItem(ListView_CategoryPrimary, PrimaryScrollViewer);
            });
        }

        private async void PageUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _vm.ClearCaches();  // 释放 VM 内存缓存（导航离开）
                _previewManager.Cleanup();
                _vm.StopFileWatchers();

                await _vm.SaveFilterModesAsync();
                await _vm.SaveSelectionStateAsync(
                    _vm.LastSelectedPrimary,
                    _vm.LastSelectedSecondary,
                    _vm.LastSelectedMod);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PageUnloaded 异常: {ex.Message}");
            }
        }

        #endregion

        #region 数据刷新

        private async Task ReloadCategoryPrimaryAsync()
        {
            _vm.ClearCaches();  // 导航回到页面，FS 可能已变化
            _vm.MakeSureModRepoExists();
            _vm.LoadPrimaryCategories();
            ClearDownstreamUI();

            if (_vm.CategoryPrimaryList.Count == 0) return;

            string? lastName = await _vm.GetSavedPrimaryAsync();
            int index = string.IsNullOrEmpty(lastName) ? -1 :
                        _vm.CategoryPrimaryList.ToList().FindIndex(c => c.Name == lastName);
            ListView_CategoryPrimary.SelectedIndex = index >= 0 ? index : 0;

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ScrollListViewToSelectedItem(ListView_CategoryPrimary, PrimaryScrollViewer);
            });
        }

        private async Task RefreshCurrentModDetails()
        {
            var primary = GetSelectedCategoryPrimary();
            var secondary = GetSelectedCategorySecondary();
            var mod = GetSelectedModItem();
            if (primary == null || secondary == null || mod == null)
            {
                ModPreviewImage.Source = null;
                SetPreviewVisibility(false);
                _vm.ModKeyList.Clear();
                UpdateModKeyListVisibility();
                return;
            }

            string? modFolder = _vm.GetSelectedModFolderPath(primary.Name, secondary.Name, mod.ModName);
            if (modFolder != null && Directory.Exists(modFolder))
            {
                var bitmap = await _vm.LoadPreviewImageAsync(modFolder);
                ModPreviewImage.Source = bitmap;
                SetPreviewVisibility(bitmap != null);
                _vm.LoadModKeys(modFolder);
            }
            else
            {
                ModPreviewImage.Source = null;
                SetPreviewVisibility(false);
                _vm.ModKeyList.Clear();
            }
            UpdateModKeyListVisibility();
        }

        #endregion

        #region UI 辅助

        private void SetPreviewVisibility(bool hasImages)
        {
            ImageViewerGrid.Visibility = hasImages ? Visibility.Visible : Visibility.Collapsed;
            AddImageButton.Visibility = hasImages ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ClearDownstreamUI()
        {
            _vm.CategorySecondaryList.Clear();
            _vm.ModItemList.Clear();
            _vm.ModKeyList.Clear();
            UpdateModKeyListVisibility();
            ModPreviewImage.Source = null;
            SetPreviewVisibility(false);
        }

        private void ClearModRelatedUI()
        {
            _vm.ModItemList.Clear();
            _vm.ModKeyList.Clear();
            UpdateModKeyListVisibility();
            ModPreviewImage.Source = null;
            SetPreviewVisibility(false);
        }

        private void UpdateModKeyListVisibility()
        {
            ModKeyListBorder.Visibility = _vm.ModKeyList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFilterButtons(FilterMode currentMode, Button btnEnabledFirst, Button btnEnabled, Button btnDisabled)
        {
            var accent = (SolidColorBrush)WinUIApp.Current.Resources["AccentFillColorDefaultBrush"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            btnEnabledFirst.Background = currentMode == FilterMode.EnabledFirst ? accent : transparent;
            btnEnabled.Background = currentMode == FilterMode.EnabledOnly ? accent : transparent;
            btnDisabled.Background = currentMode == FilterMode.DisabledOnly ? accent : transparent;
        }

        private void ScrollListViewToSelectedItem(ListView listView, ScrollViewer scrollViewer)
        {
            if (listView.SelectedItem == null) return;

            listView.ScrollIntoView(listView.SelectedItem, ScrollIntoViewAlignment.Default);
            listView.UpdateLayout();

            var container = listView.ContainerFromItem(listView.SelectedItem) as FrameworkElement;
            if (container != null)
            {
                var transform = container.TransformToVisual(scrollViewer);
                var itemTop = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                var desired = scrollViewer.ViewportHeight * 0.4;
                var target = scrollViewer.VerticalOffset + itemTop - desired;
                target = Math.Max(0, Math.Min(target, scrollViewer.ScrollableHeight));
                scrollViewer.ChangeView(null, target, null, false);
            }
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null && !(parent is T))
                parent = VisualTreeHelper.GetParent(parent);
            return parent as T;
        }

        #endregion

        #region 获取选中项（从 ListView 读取）

        private ModCategory? GetSelectedCategoryPrimary()
        {
            if (ListView_CategoryPrimary.SelectedIndex < 0 ||
                ListView_CategoryPrimary.SelectedIndex >= _vm.CategoryPrimaryList.Count)
                return null;
            return _vm.CategoryPrimaryList[ListView_CategoryPrimary.SelectedIndex];
        }

        private ModCategory? GetSelectedCategorySecondary()
        {
            if (ListView_CategorySecondary.SelectedIndex < 0 ||
                ListView_CategorySecondary.SelectedIndex >= _vm.CategorySecondaryList.Count)
                return null;
            return _vm.CategorySecondaryList[ListView_CategorySecondary.SelectedIndex];
        }

        private ModItem? GetSelectedModItem()
        {
            if (ListView_ModItem.SelectedIndex < 0 ||
                ListView_ModItem.SelectedIndex >= _vm.ModItemList.Count)
                return null;
            return _vm.ModItemList[ListView_ModItem.SelectedIndex];
        }

        #endregion

        #region SelectionChanged

        private async void ListView_CategoryPrimary_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_vm.CategoryPrimaryList.Count == 0)
            {
                ClearDownstreamUI();
                return;
            }

            var primary = GetSelectedCategoryPrimary();
            if (primary == null)
            {
                ClearDownstreamUI();
                return;
            }

            _vm.LastSelectedPrimary = primary.Name;
            string primaryPath = Path.Combine(_vm.GetModsFolderPath(), primary.Name);
            if (!Directory.Exists(primaryPath))
            {
                Debug.WriteLine($"一级分类路径不存在: {primaryPath}");
                ClearDownstreamUI();
                return;
            }

            ClearDownstreamUI();
            await ApplySecondaryFilter();
        }

        private async void ListView_CategorySecondary_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_vm.CategorySecondaryList.Count == 0) { ClearModRelatedUI(); return; }
            var selected = GetSelectedCategorySecondary();
            if (selected == null) { ClearModRelatedUI(); return; }

            _vm.LastSelectedSecondary = selected.Name;
            ClearModRelatedUI();
            await ApplyModFilter();
        }

        private async void ListView_ModItem_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_vm.ModItemList.Count == 0) return;
            var selected = GetSelectedModItem();
            if (selected == null) return;

            _vm.LastSelectedMod = selected.ModName;

            var primary = GetSelectedCategoryPrimary();
            var secondary = GetSelectedCategorySecondary();
            if (primary == null || secondary == null) return;

            string? modFolder = _vm.GetSelectedModFolderPath(primary.Name, secondary.Name, selected.ModName);
            if (modFolder == null) return;

            var bitmap = await _vm.LoadPreviewImageAsync(modFolder);
            ModPreviewImage.Source = bitmap;
            SetPreviewVisibility(bitmap != null);

            _vm.LoadModKeys(modFolder);
            UpdateModKeyListVisibility();
        }

        #endregion

        #region 筛选

        // ========== 二级分类筛选按钮 ==========
        private async void FilterSecondaryEnabledFirst_Click(object sender, RoutedEventArgs e) => await ToggleSecondaryFilterAsync(FilterMode.EnabledFirst);
        private async void FilterSecondaryEnabled_Click(object sender, RoutedEventArgs e) => await ToggleSecondaryFilterAsync(FilterMode.EnabledOnly);
        private async void FilterSecondaryDisabled_Click(object sender, RoutedEventArgs e) => await ToggleSecondaryFilterAsync(FilterMode.DisabledOnly);

        private async Task ToggleSecondaryFilterAsync(FilterMode mode)
        {
            _vm.ToggleSecondaryFilter(mode);
            UpdateFilterButtons(_vm.SecondaryFilter, BtnSecondaryEnabledFirst, BtnSecondaryEnabled, BtnSecondaryDisabled);
            await ApplySecondaryFilter();
        }

        // ========== Mod 筛选按钮 ==========
        private async void FilterModEnabledFirst_Click(object sender, RoutedEventArgs e) => await ToggleModFilterAsync(FilterMode.EnabledFirst);
        private async void FilterModEnabled_Click(object sender, RoutedEventArgs e) => await ToggleModFilterAsync(FilterMode.EnabledOnly);
        private async void FilterModDisabled_Click(object sender, RoutedEventArgs e) => await ToggleModFilterAsync(FilterMode.DisabledOnly);

        private async Task ToggleModFilterAsync(FilterMode mode)
        {
            _vm.ToggleModFilter(mode);
            UpdateFilterButtons(_vm.ModFilter, BtnModEnabledFirst, BtnModEnabled, BtnModDisabled);
            await ApplyModFilter();
        }

        private async Task ApplySecondaryFilter()
        {
            if (string.IsNullOrEmpty(_vm.LastSelectedPrimary)) return;

            _vm.ApplySecondaryFilter(_vm.LastSelectedPrimary);

            string? targetSecondary = _vm.LastSelectedSecondary;

            if (!string.IsNullOrEmpty(targetSecondary))
            {
                int index = _vm.CategorySecondaryList.ToList().FindIndex(c => c.Name == targetSecondary);
                if (index >= 0)
                {
                    ListView_CategorySecondary.SelectedIndex = index;
                }
                else if (_vm.CategorySecondaryList.Count > 0)
                {
                    ListView_CategorySecondary.SelectedIndex = 0;
                    _vm.LastSelectedSecondary = _vm.CategorySecondaryList[0].Name;
                }
            }
            else if (_vm.CategorySecondaryList.Count > 0)
            {
                ListView_CategorySecondary.SelectedIndex = 0;
                _vm.LastSelectedSecondary = _vm.CategorySecondaryList[0].Name;
            }

            await Task.Yield();
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ScrollListViewToSelectedItem(ListView_CategorySecondary, SecondaryScrollViewer);
            });
        }

        private async Task ApplyModFilter()
        {
            if (string.IsNullOrEmpty(_vm.LastSelectedPrimary) || string.IsNullOrEmpty(_vm.LastSelectedSecondary))
                return;

            _vm.ApplyModFilter(_vm.LastSelectedPrimary, _vm.LastSelectedSecondary);

            string? targetMod = _vm.LastSelectedMod;

            if (!string.IsNullOrEmpty(targetMod))
            {
                int idx = _vm.ModItemList.ToList().FindIndex(m => m.ModName == targetMod);
                if (idx >= 0)
                {
                    ListView_ModItem.SelectedIndex = idx;
                }
                else
                {
                    SelectFirstEnabledOrDefault();
                }
            }
            else if (_vm.ModItemList.Count > 0)
            {
                SelectFirstEnabledOrDefault();
            }

            await Task.Yield();
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ScrollListViewToSelectedItem(ListView_ModItem, ModScrollViewer);
            });
        }

        private void SelectFirstEnabledOrDefault()
        {
            var firstEnabled = _vm.ModItemList.FirstOrDefault(m => m.Enable);
            if (firstEnabled != null)
            {
                int enabledIdx = _vm.ModItemList.IndexOf(firstEnabled);
                ListView_ModItem.SelectedIndex = enabledIdx;
                _vm.LastSelectedMod = firstEnabled.ModName;
            }
            else if (_vm.ModItemList.Count > 0)
            {
                ListView_ModItem.SelectedIndex = 0;
                _vm.LastSelectedMod = _vm.ModItemList[0].ModName;
            }
        }

        #endregion

        #region 右键事件

        private async void ListView_CategoryPrimary_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var listView = sender as ListView;
            var originalSource = e.OriginalSource as FrameworkElement;
            if (originalSource == null) return;
            var listViewItem = FindParent<ListViewItem>(originalSource);
            if (listViewItem == null) return;

            var category = listView!.ItemFromContainer(listViewItem) as ModCategory;
            if (category == null) return;

            string targetName = category.Name;
            bool wasDisabled = category.NotEnable;

            string? currentPrimary = GetSelectedCategoryPrimary()?.Name;
            string? currentSecondary = GetSelectedCategorySecondary()?.Name;
            string? currentMod = GetSelectedModItem()?.ModName;

            string newName = _vm.GetToggledName(targetName);
            string oldPath = Path.Combine(_vm.GetModsFolderPath(), targetName);
            string newPath = Path.Combine(_vm.GetModsFolderPath(), newName);

            if (!Directory.Exists(oldPath)) { _notif?.Show("操作失败", $"类别文件夹 \"{category.DisplayName}\" 不存在", NotificationType.Error); await ReloadCategoryPrimaryAsync(); return; }
            if (Directory.Exists(newPath)) { _notif?.Show("操作失败", $"名称冲突：\"{newName}\" 已存在", NotificationType.Warning); return; }

            ModPreviewImage.Source = null;
            SetPreviewVisibility(false);

            // 暂停文件监控，防止重命名触发的文件变更事件打断手动刷新
            _vm.PauseFileWatcher();
            try
            {
                if (!await _vm.TryRenameFolderAsync(oldPath, newPath))
                {
                    _notif?.Show("操作失败", $"无法重命名 \"{category.DisplayName}\"，请检查权限或文件夹是否被占用", NotificationType.Error);
                    return;
                }

                // 重建一级分类列表（仅列表，不触发完整级联刷新）
                _vm.LoadPrimaryCategories();

                if (wasDisabled)
                {
                    // 禁用→启用：选中刚启用的卡片
                    int idx = _vm.CategoryPrimaryList.ToList().FindIndex(c => c.Name == newName);
                    if (idx >= 0)
                    {
                        ListView_CategoryPrimary.SelectedIndex = idx;
                        _vm.LastSelectedPrimary = newName;
                        await _vm.SaveSelectionStateAsync(newName, currentSecondary, currentMod);
                    }
                }
                else
                {
                    // 启用→禁用：优先选中首个启用的卡片
                    await _vm.SaveSelectionStateAsync(
                        currentPrimary != null && !currentPrimary.Equals(targetName, StringComparison.OrdinalIgnoreCase) ? currentPrimary : null,
                        currentSecondary, currentMod);

                    if (_vm.CategoryPrimaryList.Count > 0)
                    {
                        var firstEnabled = _vm.CategoryPrimaryList.FirstOrDefault(c => !c.NotEnable);
                        ListView_CategoryPrimary.SelectedIndex = firstEnabled != null
                            ? _vm.CategoryPrimaryList.IndexOf(firstEnabled) : 0;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                _notif?.Show("操作失败", $"没有权限修改 \"{category.DisplayName}\"，请检查文件夹权限设置", NotificationType.Error);
            }
            catch (IOException)
            {
                _notif?.Show("操作失败", $"文件夹 \"{category.DisplayName}\" 可能正被其他程序占用，请关闭后重试", NotificationType.Error);
            }
            catch (Exception ex) { Debug.WriteLine($"切换一级分类失败: {ex.Message}"); _notif?.Show("操作失败", ex.Message, NotificationType.Error); }
            finally
            {
                _vm.ResumeFileWatcher();
                _ = DispatcherQueue.TryEnqueue(() =>
                    ScrollListViewToSelectedItem(ListView_CategoryPrimary, PrimaryScrollViewer));
            }
        }

        private async void ListView_CategorySecondary_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var listView = sender as ListView;
            var originalSource = e.OriginalSource as FrameworkElement;
            if (originalSource == null) return;
            var listViewItem = FindParent<ListViewItem>(originalSource);
            if (listViewItem == null) return;

            var category = listView!.ItemFromContainer(listViewItem) as ModCategory;
            if (category == null) return;

            string targetName = category.Name;
            bool wasDisabled = category.NotEnable;

            string? currentPrimary = GetSelectedCategoryPrimary()?.Name;
            string? currentSecondary = GetSelectedCategorySecondary()?.Name;
            string? currentMod = GetSelectedModItem()?.ModName;

            if (string.IsNullOrEmpty(currentPrimary)) return;

            string newName = _vm.GetToggledName(targetName);
            string oldPath = Path.Combine(_vm.GetModsFolderPath(), currentPrimary, targetName);
            string newPath = Path.Combine(_vm.GetModsFolderPath(), currentPrimary, newName);

            if (!Directory.Exists(oldPath)) { _notif?.Show("操作失败", $"类别文件夹 \"{category.DisplayName}\" 不存在", NotificationType.Error); await ReloadCategoryPrimaryAsync(); return; }
            if (Directory.Exists(newPath)) { _notif?.Show("操作失败", $"名称冲突：\"{newName}\" 已存在", NotificationType.Warning); return; }

            ModPreviewImage.Source = null;
            SetPreviewVisibility(false);

            // 暂停文件监控，防止重命名触发的文件变更事件打断手动刷新
            _vm.PauseFileWatcher();
            try
            {
                if (!await _vm.TryRenameFolderAsync(oldPath, newPath))
                {
                    _notif?.Show("操作失败", $"无法重命名 \"{category.DisplayName}\"，请检查权限或文件夹是否被占用", NotificationType.Error);
                    return;
                }

                // 失效缓存：文件夹名已变更，磁盘状态已不同
                _vm.InvalidateSecondaryCache(currentPrimary);

                // 重建二级分类列表（仅列表，不触发完整级联刷新）
                _vm.ApplySecondaryFilter(currentPrimary);

                if (wasDisabled)
                {
                    // 禁用→启用：选中刚启用的卡片
                    int idx = _vm.CategorySecondaryList.ToList().FindIndex(c => c.Name == newName);
                    if (idx >= 0)
                    {
                        ListView_CategorySecondary.SelectedIndex = idx;
                        _vm.LastSelectedSecondary = newName;
                        await _vm.SaveSelectionStateAsync(currentPrimary, newName, currentMod);
                    }
                }
                else
                {
                    // 启用→禁用：优先选中首个启用的卡片
                    await _vm.SaveSelectionStateAsync(currentPrimary,
                        currentSecondary != null && !currentSecondary.Equals(targetName, StringComparison.OrdinalIgnoreCase) ? currentSecondary : null,
                        currentMod);

                    if (_vm.CategorySecondaryList.Count > 0)
                    {
                        var firstEnabled = _vm.CategorySecondaryList.FirstOrDefault(c => !c.NotEnable);
                        ListView_CategorySecondary.SelectedIndex = firstEnabled != null
                            ? _vm.CategorySecondaryList.IndexOf(firstEnabled) : 0;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                _notif?.Show("操作失败", $"没有权限修改 \"{category.DisplayName}\"，请检查文件夹权限设置", NotificationType.Error);
            }
            catch (IOException)
            {
                _notif?.Show("操作失败", $"文件夹 \"{category.DisplayName}\" 可能正被其他程序占用，请关闭后重试", NotificationType.Error);
            }
            catch (Exception ex) { Debug.WriteLine($"切换二级分类失败: {ex.Message}"); _notif?.Show("操作失败", ex.Message, NotificationType.Error); }
            finally
            {
                _vm.ResumeFileWatcher();
                _ = DispatcherQueue.TryEnqueue(() =>
                    ScrollListViewToSelectedItem(ListView_CategorySecondary, SecondaryScrollViewer));
            }
        }

        private async void ListView_ModItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var listView = sender as ListView;
            var originalSource = e.OriginalSource as FrameworkElement;
            if (originalSource == null) return;
            var listViewItem = FindParent<ListViewItem>(originalSource);
            if (listViewItem == null) return;

            var mod = listView!.ItemFromContainer(listViewItem) as ModItem;
            if (mod == null) return;

            string targetName = mod.ModName;
            bool wasDisabled = !mod.Enable;
            var primary = GetSelectedCategoryPrimary();
            var secondary = GetSelectedCategorySecondary();
            if (primary == null || secondary == null) return;

            string newModName = _vm.GetToggledName(targetName);
            string oldPath = Path.Combine(_vm.GetModsFolderPath(), primary.Name, secondary.Name, targetName);
            string newPath = Path.Combine(_vm.GetModsFolderPath(), primary.Name, secondary.Name, newModName);

            if (!Directory.Exists(oldPath))
            {
                _notif?.Show("操作失败", $"Mod 文件夹 \"{mod.DisplayName}\" 不存在", NotificationType.Error);
                await ApplyModFilter();
                return;
            }
            if (Directory.Exists(newPath)) { _notif?.Show("操作失败", $"名称冲突：\"{newModName}\" 已存在", NotificationType.Warning); return; }

            SetPreviewVisibility(false);

            // 暂停文件监控，防止重命名触发的文件变更事件打断手动刷新
            _vm.PauseFileWatcher();
            try
            {
                if (!await _vm.TryRenameFolderAsync(oldPath, newPath))
                {
                    _notif?.Show("操作失败", $"无法重命名 \"{mod.DisplayName}\"，请检查权限或文件夹是否被占用", NotificationType.Error);
                    return;
                }

                // 失效缓存：Mod 文件夹名已变更
                _vm.InvalidateModItemCache(primary.Name, secondary.Name);

                // 重建 Mod 列表（仅列表，走 ViewModel 方法不触发 Page 层选中逻辑）
                _vm.ApplyModFilter(primary.Name, secondary.Name);

                if (wasDisabled)
                {
                    // 禁用→启用：选中刚启用的卡片
                    int idx = _vm.ModItemList.ToList().FindIndex(m => m.ModName == newModName);
                    if (idx >= 0)
                    {
                        ListView_ModItem.SelectedIndex = idx;
                        _vm.LastSelectedMod = newModName;
                    }
                }
                else if (_vm.ModItemList.Count > 0)
                {
                    // 启用→禁用：优先选中首个启用的卡片
                    var firstEnabled = _vm.ModItemList.FirstOrDefault(m => m.Enable);
                    ListView_ModItem.SelectedIndex = firstEnabled != null
                        ? _vm.ModItemList.IndexOf(firstEnabled) : 0;
                }
            }
            catch (UnauthorizedAccessException)
            {
                _notif?.Show("操作失败", $"没有权限修改 \"{mod.DisplayName}\"，请检查文件夹权限设置", NotificationType.Error);
            }
            catch (IOException)
            {
                _notif?.Show("操作失败", $"文件夹 \"{mod.DisplayName}\" 可能正被其他程序占用，请关闭后重试", NotificationType.Error);
            }
            catch (Exception ex) { Debug.WriteLine($"切换Mod状态失败: {ex.Message}"); _notif?.Show("操作失败", ex.Message, NotificationType.Error); }
            finally
            {
                _vm.ResumeFileWatcher();
                _ = DispatcherQueue.TryEnqueue(() =>
                    ScrollListViewToSelectedItem(ListView_ModItem, ModScrollViewer));
            }
        }

        #endregion

        #region 双击事件

        private void ListView_CategoryPrimary_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var listView = sender as ListView;
            var originalSource = e.OriginalSource as FrameworkElement;
            if (originalSource == null) return;
            var listViewItem = FindParent<ListViewItem>(originalSource);
            if (listViewItem == null) return;

            var category = listView!.ItemFromContainer(listViewItem) as ModCategory;
            if (category == null) return;

            listView.SelectedItem = category;
            string folderPath = Path.Combine(_vm.GetModsFolderPath(), category.Name);
            if (Directory.Exists(folderPath))
                Process.Start("explorer.exe", folderPath);
        }

        private void ListView_CategorySecondary_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var listView = sender as ListView;
            var originalSource = e.OriginalSource as FrameworkElement;
            if (originalSource == null) return;
            var listViewItem = FindParent<ListViewItem>(originalSource);
            if (listViewItem == null) return;

            var category = listView!.ItemFromContainer(listViewItem) as ModCategory;
            if (category == null) return;

            string? primaryName = GetSelectedCategoryPrimary()?.Name;
            if (string.IsNullOrEmpty(primaryName)) return;

            listView.SelectedItem = category;
            string folderPath = Path.Combine(_vm.GetModsFolderPath(), primaryName, category.Name);
            if (Directory.Exists(folderPath))
                Process.Start("explorer.exe", folderPath);
        }

        private void ListView_ModItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var listView = sender as ListView;
            var originalSource = e.OriginalSource as FrameworkElement;
            if (originalSource == null) return;
            var listViewItem = FindParent<ListViewItem>(originalSource);
            if (listViewItem == null) return;

            var mod = listView!.ItemFromContainer(listViewItem) as ModItem;
            if (mod == null) return;

            var primary = GetSelectedCategoryPrimary();
            var secondary = GetSelectedCategorySecondary();
            if (primary == null || secondary == null) return;

            listView.SelectedItem = mod;
            string folderPath = Path.Combine(_vm.GetModsFolderPath(), primary.Name, secondary.Name, mod.ModName);
            if (Directory.Exists(folderPath))
                Process.Start("explorer.exe", folderPath);
        }

        #endregion

        #region 拖放

        private void ListView_ModItem_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "添加 Mod";
                e.DragUIOverride.IsCaptionVisible = true;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private async void ListView_ModItem_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count == 0) return;

            var primary = GetSelectedCategoryPrimary();
            var secondary = GetSelectedCategorySecondary();
            if (primary == null || secondary == null)
            {
                Debug.WriteLine("提示: 请先选中一个二级分类");
                return;
            }

            string targetDir = Path.Combine(_vm.GetModsFolderPath(), primary.Name, secondary.Name);
            Directory.CreateDirectory(targetDir);

            foreach (var item in items)
            {
                if (item is Windows.Storage.StorageFolder folder)
                {
                    string destPath = Path.Combine(targetDir, folder.Name);
                    if (!Directory.Exists(destPath))
                        await ModManageViewModel.CopyFolderAsync(folder.Path, destPath);
                }
            }

            // 失效缓存：新文件夹已复制到磁盘
            if (primary != null && secondary != null)
                _vm.InvalidateModItemCache(primary.Name, secondary.Name);

            await ApplyModFilter();
        }

        #endregion

        #region 预览图操作

        private async void Button_PasteClipboardImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var primary = GetSelectedCategoryPrimary();
                var secondary = GetSelectedCategorySecondary();
                var mod = GetSelectedModItem();
                if (primary == null || secondary == null || mod == null)
                {
                    Debug.WriteLine("提示: 请先选中一个 Mod 项目");
                    return;
                }

                string? modFolder = _vm.GetSelectedModFolderPath(primary.Name, secondary.Name, mod.ModName);
                if (modFolder == null) return;

                var dataPackageView = Clipboard.GetContent();
                if (!dataPackageView.Contains(StandardDataFormats.Bitmap))
                {
                    Debug.WriteLine("提示: 剪贴板中没有图片");
                    return;
                }

                var bitmapReference = await dataPackageView.GetBitmapAsync();
                if (bitmapReference == null) return;

                using var imageStream = await bitmapReference.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(imageStream);
                var frame = await decoder.GetFrameAsync(0);
                var pixelData = await frame.GetPixelDataAsync();
                var pixels = pixelData.DetachPixelData();

                await _vm.SaveClipboardImageToModFolderAsync(modFolder, pixels,
                    frame.PixelWidth, frame.PixelHeight, decoder.DpiX, decoder.DpiY,
                    frame.BitmapPixelFormat, frame.BitmapAlphaMode);

                ModPreviewImage.Source = null;
                var bitmap = await _vm.LoadPreviewImageAsync(modFolder);
                ModPreviewImage.Source = bitmap;
                SetPreviewVisibility(bitmap != null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"粘贴剪贴板图片失败: {ex.Message}");
            }
        }

        private async void MenuFlyoutItem_DeleteThisModPreviewPicture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var primary = GetSelectedCategoryPrimary();
                var secondary = GetSelectedCategorySecondary();
                var mod = GetSelectedModItem();
                if (primary == null || secondary == null || mod == null)
                {
                    Debug.WriteLine("提示: 请先选中一个 Mod 项目");
                    return;
                }

                string? modFolder = _vm.GetSelectedModFolderPath(primary.Name, secondary.Name, mod.ModName);
                if (modFolder == null) return;

                string? result = _vm.DeletePreviewImage(modFolder);
                if (result == null)
                {
                    Debug.WriteLine("提示: 当前没有预览图可删除");
                    return;
                }

                var bitmap = await _vm.LoadPreviewImageAsync(modFolder);
                ModPreviewImage.Source = bitmap;
                SetPreviewVisibility(bitmap != null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"删除预览图失败: {ex.Message}");
            }
        }

        #endregion

        #region Pointer 事件

        private void ModItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not ModItem modItem) return;

            _previewManager.ShowGlow(element, e);

            // 延迟加载预览图路径（首次悬停时扫描一次，后续命中缓存）
            if (string.IsNullOrEmpty(modItem.PreviewImage))
            {
                var primary = GetSelectedCategoryPrimary();
                var secondary = GetSelectedCategorySecondary();
                if (primary != null && secondary != null)
                    _vm.EnsureModPreviewPath(modItem, primary.Name, secondary.Name);
            }

            if (string.IsNullOrEmpty(modItem.PreviewImage))
            {
                _previewManager.HidePreview(modItem);
                return;
            }

            _previewManager.ShowPreview(modItem.PreviewImage, e, modItem);
        }

        private void ModItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _previewManager.HideGlow();
            _previewManager.ForceHidePreview();
        }

        private void ModItem_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            _previewManager.MoveGlow(element, e);
            _previewManager.MovePreview(e);
        }

        private void CategorySecondary_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not ModCategory category) return;

            _previewManager.ShowGlow(element, e);

            string? imagePath = null;
            string? primaryName = GetSelectedCategoryPrimary()?.Name;
            if (!string.IsNullOrEmpty(primaryName))
            {
                imagePath = _vm.GetFirstModPreviewPath(primaryName, category.Name);
            }

            if (string.IsNullOrEmpty(imagePath))
            {
                _previewManager.HidePreview(category);
                return;
            }

            _previewManager.ShowPreview(imagePath, e, category);
        }

        private void CategorySecondary_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _previewManager.HideGlow();
            _previewManager.ForceHidePreview();
        }

        private void CategorySecondary_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            _previewManager.MoveGlow(element, e);
            _previewManager.MovePreview(e);
        }

        #endregion
    }
}
