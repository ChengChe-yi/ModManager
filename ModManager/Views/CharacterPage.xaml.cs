using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.Core;
using ModManager.Application.Services;
using ModManager.Application.ViewModels;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Models;
using ModManager.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.System;
using Path = System.IO.Path;

namespace ModManager.Views
{
    public sealed partial class CharacterPage : Page
    {
        private readonly CharacterManageViewModel _vm;
        private readonly PreviewPopupManager _previewManager;
        private readonly INotificationService? _notif;

        public CharacterPage() : this(App.GetService<CharacterManageViewModel>()!)
        { }

        public CharacterPage(CharacterManageViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            _vm.SetDispatcherQueue(DispatcherQueue);
            _previewManager = new PreviewPopupManager(this);
            _notif = App.GetService<INotificationService>();
            Loaded += PageLoaded;
            Unloaded += PageUnloaded;
        }

        // ================================================================
        // 加载 / 卸载
        // ================================================================

        private bool _isLoaded;
        private bool _suppressListAnimations;
        private TypedEventHandler<FrameworkElement, object>? _actualThemeChangedHandler;
        private static int _s_uiSeq; // UI 事件序列号，追踪刷新顺序

        private async void PageLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[CharacterPage] PageLoaded 触发");

            _suppressListAnimations = true;
            try
            {
                if (_isLoaded)
                {
                    Debug.WriteLine("[CharacterPage] _isLoaded=true, 返回重同步 UI");
                    _vm.PropertyChanged += OnViewModelPropertyChanged;
                    try { ModManager.Animations.ImplicitColorAnimation.RegisterForElement(this); } catch { }
                    SyncUIFromViewModel();
                    EntranceStoryboard?.Begin();
                    PreviewEntranceStoryboard?.Begin();
                    return;
                }
                _isLoaded = true;
                Debug.WriteLine($"[CharacterPage] 初始 FilteredCharacters.Count={_vm.FilteredCharacters.Count}");

                ListView_Character.ItemsSource = _vm.FilteredCharacters;
                ListView_Mod.ItemsSource = _vm.CurrentMods;
                DataGrid_ModKeyList.ItemsSource = _vm.ModKeys;
                SetupKeyboardHandlers();
                Debug.WriteLine("[CharacterPage] ItemsSource 绑定完成");

                // === Material Ripple：两个 ListView 统一注册 ===
                RegisterRippleHandler(ListView_Character);
                RegisterRippleHandler(ListView_Mod);

                _vm.PropertyChanged += OnViewModelPropertyChanged;
                try { ModManager.Animations.ImplicitColorAnimation.RegisterForElement(this); } catch { }
                InitializeTagInputPanel();
                _actualThemeChangedHandler = (_, _) => UpdateQuickFilterHighlights();
                ActualThemeChanged += _actualThemeChangedHandler;

                await _vm.InitializeAsync();
                Debug.WriteLine($"[CharacterPage] InitializeAsync 完成");

                string? savedChar = await _vm.GetSavedCharacterAsync();
                string? savedMod = await _vm.GetSavedModAsync();
                Debug.WriteLine($"[CharacterPage] 已保存: char={savedChar ?? "(null)"}, mod={savedMod ?? "(null)"}");

                await _vm.LoadDataAndSelectAsync(savedChar, savedMod);
                Debug.WriteLine($"[CharacterPage] LoadDataAndSelectAsync 完成, FilteredCharacters.Count={_vm.FilteredCharacters.Count}, SelectedCharacter={_vm.SelectedCharacter?.Name ?? "(null)"}");

                _vm.ReloadCurrentModDetail();
                // 启动文件监控（初始加载完成后）
                await _vm.StartFileWatcherAsync();

                if (_vm.SelectedCharacter != null)
                {
                    ListView_Character.SelectedItem = _vm.SelectedCharacter;
                    ListView_Character.UpdateLayout();

                    if (_vm.SelectedMod != null)
                    {
                        ListView_Mod.SelectedItem = _vm.SelectedMod;
                        ListView_Mod.UpdateLayout();
                    }
                }
                else if (_vm.FilteredCharacters.Count > 0)
                {
                    ListView_Character.SelectedItem = _vm.FilteredCharacters[0];
                    ListView_Character.UpdateLayout();
                    Debug.WriteLine("[CharacterPage] 无保存记录，选中第一个");
                }
                else
                {
                    Debug.WriteLine("[CharacterPage] 列表为空，无选中");
                }

                // 先瞬时定位滚动位置，再播放页面入场动画——页面淡入时已在正确位置
                DispatcherQueue.TryEnqueue(() =>
                {
                    ScrollToSelected(ListView_Character, animate: false);
                    if (_vm.SelectedMod != null)
                        ScrollToSelected(ListView_Mod, animate: false);

                    // 滚动就位后播放入场动画 + 预览图动画（同时）
                    EntranceStoryboard?.Begin();
                    PreviewEntranceStoryboard?.Begin();
                    PreviewImageStoryboard?.Stop();
                    PreviewImageStoryboard?.Begin();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPage] Loaded 异常: {ex}");
            }
            finally
            {
                _suppressListAnimations = false;
            }
        }

        private async void PageUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
                if (_actualThemeChangedHandler != null)
                {
                    ActualThemeChanged -= _actualThemeChangedHandler;
                    _actualThemeChangedHandler = null;
                }
                _previewManager.Cleanup();

                // 保存状态（不重置 _isLoaded，防止同一次会话中重复初始化）
                await _vm.SaveSelectionStateAsync(
                    _vm.LastSelectedCharacter,
                    _vm.LastSelectedMod);
                Debug.WriteLine($"[CharacterPage] PageUnloaded 已保存: char={_vm.LastSelectedCharacter ?? "(null)"}, mod={_vm.LastSelectedMod ?? "(null)"}");

                // 停止文件监控 + 释放内存缓存
                _vm.ClearCaches();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CharacterPage Unloaded 异常: {ex.Message}");
            }
        }

        /// <summary>从 VM 恢复 UI 状态（预览图、按键可见性、选中项），用于页面返回时重同步。</summary>
        private void SyncUIFromViewModel()
        {
            var savedChar = _vm.SelectedCharacter;
            var savedMod = _vm.SelectedMod;

            Debug.WriteLine($"[CharacterPage] SyncUI: char={savedChar?.Name}, mod={savedMod?.ModName}, ListView_Char.SelectedItem={((CharacterCategory?)ListView_Character.SelectedItem)?.Name}, ListView_Mod.SelectedItem={((ModItem?)ListView_Mod.SelectedItem)?.ModName}");

            // 重同步角色选中（仅在引用不同时触发，避免多余事件）
            if (savedChar != null && ListView_Character.SelectedItem != savedChar)
                ListView_Character.SelectedItem = savedChar;

            // 强制重新加载 Mod 详情
            _vm.ReloadCurrentModDetail();

            // 先瞬时定位滚动位置，再播放入场动画——页面淡入时已在正确位置
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ListView_Character.UpdateLayout();
                ScrollToSelected(ListView_Character, animate: false);
                if (savedMod != null)
                {
                    ListView_Mod.UpdateLayout();
                    ScrollToSelected(ListView_Mod, animate: false);
                }

                EntranceStoryboard?.Begin();
                PreviewEntranceStoryboard?.Begin();
                PreviewImageStoryboard?.Stop();
                PreviewImageStoryboard?.Begin();
            });

            Debug.WriteLine($"[CharacterPage] SyncUI done: Preview={_vm.HasPreview}, ModKeys={_vm.HasModKeys}");
        }

        /// <summary>供 MainWindow 在应用关闭时调用，确保状态已持久化</summary>
        public async Task SaveCurrentStateAsync()
        {
            try
            {
                await _vm.SaveSelectionStateAsync(
                    _vm.LastSelectedCharacter,
                    _vm.LastSelectedMod);
                Debug.WriteLine($"[CharacterPage] SaveCurrentStateAsync: char={_vm.LastSelectedCharacter ?? "(null)"}, mod={_vm.LastSelectedMod ?? "(null)"}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CharacterPage SaveCurrentState 异常: {ex.Message}");
            }
        }

        // ================================================================
        // ViewModel 属性 → UI 同步
        // ================================================================

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var seq = Interlocked.Increment(ref _s_uiSeq);
            Debug.WriteLine($"[CharacterPage#{seq}] PropertyChanged: {e.PropertyName}");
            switch (e.PropertyName)
            {
                case nameof(CharacterManageViewModel.IsLoading):
                    CharacterLoadingOverlay.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case nameof(CharacterManageViewModel.FilteredCharacters):
                    Debug.WriteLine($"[CharacterPage#{seq}] FilteredCharacters → ItemsSource: count={_vm.FilteredCharacters.Count} modSearchText='{_vm.ModSearchText}' isModSearch={_vm.IsModSearchMode}");
                    ListView_Character.ItemsSource = _vm.FilteredCharacters;
                    CharacterEmptyHint.Visibility = (_vm.FilteredCharacters.Count == 0 && !_vm.IsLoading) ? Visibility.Visible : Visibility.Collapsed;
                    if (!_suppressListAnimations && _vm.FilteredCharacters.Count > 0)
                    {
                        Debug.WriteLine($"[CharacterPage#{seq}] CharacterListStoryboard BEGIN");
                        CharacterListStoryboard?.Begin();
                    }
                    break;

                case nameof(CharacterManageViewModel.FilteredMods):
                    Debug.WriteLine($"[CharacterPage#{seq}] FilteredMods → ItemsSource: count={_vm.FilteredMods.Count} modSearchText='{_vm.ModSearchText}' isModSearch={_vm.IsModSearchMode} currentModsCount={_vm.CurrentMods.Count}");
                    if (!string.IsNullOrEmpty(_vm.ModSearchText) || _vm.IsModSearchMode)
                        ListView_Mod.ItemsSource = _vm.FilteredMods;
                    else
                        ListView_Mod.ItemsSource = _vm.CurrentMods;
                    if (!_suppressListAnimations && _vm.FilteredMods.Count > 0)
                    {
                        Debug.WriteLine($"[CharacterPage#{seq}] ModListStoryboard BEGIN");
                        ModListStoryboard?.Begin();
                    }
                    if (_vm.SelectedMod != null)
                    {
                        Debug.WriteLine($"[CharacterPage#{seq}] 恢复选中 Mod: {_vm.SelectedMod.ModName}");
                        ListView_Mod.SelectedItem = _vm.SelectedMod;
                        _ = DispatcherQueue.TryEnqueue(() => ScrollToSelected(ListView_Mod));
                    }
                    break;

                case nameof(CharacterManageViewModel.CurrentMods):
                    Debug.WriteLine($"[CharacterPage#{seq}] CurrentMods → ItemsSource: count={_vm.CurrentMods.Count} modSearchText='{_vm.ModSearchText}' isModSearch={_vm.IsModSearchMode}");
                    if (string.IsNullOrEmpty(_vm.ModSearchText) && !_vm.IsModSearchMode)
                        ListView_Mod.ItemsSource = _vm.CurrentMods;
                    ModEmptyHint.Visibility = (_vm.CurrentMods.Count == 0 && !_vm.IsLoading) ? Visibility.Visible : Visibility.Collapsed;
                    break;

                case nameof(CharacterManageViewModel.PreviewImage):
                    Debug.WriteLine($"[CharacterPage] PreviewImage 变更: {(_vm.PreviewImage != null ? "有图" : "null")}");
                    ModPreviewImage.Source = _vm.PreviewImage;
                    PreviewImageStoryboard?.Stop();
                    PreviewImageStoryboard?.Begin();
                    break;

                case nameof(CharacterManageViewModel.HasPreview):
                    SetPreviewVisibility(_vm.HasPreview);
                    break;

                case nameof(CharacterManageViewModel.HasModKeys):
                    ModKeyListBorder.Visibility = _vm.HasModKeys ? Visibility.Visible : Visibility.Collapsed;
                    if (_vm.HasModKeys) {
                        if (_presetMode) { _presetMode = false; PresetPanel.Visibility = Visibility.Collapsed; DataGrid_ModKeyList.Visibility = Visibility.Visible; }
                        PresetEditPanel.Visibility = Visibility.Collapsed; PresetListViewPanel.Visibility = Visibility.Visible;
                        PresetToggleButton.Content = "预设 ▶"; KeyListHeaderText.Text = "Mod按键列表"; }
                    break;

                case nameof(CharacterManageViewModel.SelectedCharacter):
                    Debug.WriteLine($"[CharacterPage#{seq}] SelectedCharacter → {(_vm.SelectedCharacter?.Name ?? "null")} (ListView.SelectedItem={((CharacterCategory?)ListView_Character.SelectedItem)?.Name ?? "null"})");
                    if (ListView_Character.SelectedItem != _vm.SelectedCharacter)
                    {
                        ListView_Character.SelectedItem = _vm.SelectedCharacter;
                        // 双重 TryEnqueue：等 ListView 完成布局+虚拟化后再滚动
                        DispatcherQueue.TryEnqueue(() =>
                            DispatcherQueue.TryEnqueue(() => ScrollToSelected(ListView_Character)));
                    }
                    break;

                case nameof(CharacterManageViewModel.SelectedMod):
                    Debug.WriteLine($"[CharacterPage#{seq}] SelectedMod → {(_vm.SelectedMod?.ModName ?? "null")} (ListView.SelectedItem={((ModItem?)ListView_Mod.SelectedItem)?.ModName ?? "null"})");
                    if (ListView_Mod.SelectedItem != _vm.SelectedMod)
                        ListView_Mod.SelectedItem = _vm.SelectedMod;
                    // 切换 Mod 时重载预设 + 加载 INI 值
                    _editingPreset = null;
                    if (_vm.SelectedMod != null) { _ = LoadPresetsAsync(); _ = LoadIniValuesToDataGridAsync();
                        DataGrid_ModKeyList.Visibility = _presetMode ? Visibility.Collapsed : Visibility.Visible; }
                    PresetEditPanel.Visibility = Visibility.Collapsed; PresetListViewPanel.Visibility = Visibility.Visible;
                    break;
            }
        }

        // ================================================================
        // SelectionChanged
        // ================================================================

        private void ListView_Character_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListView_Character.SelectedItem is CharacterCategory character)
            {
                _vm.SelectedCharacter = character;
                _vm.LastSelectedCharacter = character.Name;
                _ = _vm.SaveSelectionStateAsync(character.Name, _vm.LastSelectedMod);
            }
        }

        private void ListView_Mod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModSelectionChanged) return;
            if (ListView_Mod.SelectedItem is ModItem mod)
            {
                Debug.WriteLine($"[CharacterPage] ListView_Mod_SelectionChanged: mod={mod.ModName}");
                _vm.SelectedMod = mod;
                _vm.LastSelectedMod = mod.ModName;
                // 持久化由 OnSelectedModChanged 统一处理（VM 侧已即时保存）

                // Mod 模式 → 左侧自动选中来源角色
                if (!string.IsNullOrEmpty(_vm.ModSearchText))
                {
                    var owner = _vm.GetCharacterForMod(mod);
                    if (owner != null && ListView_Character.SelectedItem != owner)
                        ListView_Character.SelectedItem = owner;
                }
            }
        }

        // ================================================================
        // 快速筛选 + 芯片 + 搜索框
        // ================================================================

        /// <summary>快速筛选元素定义（单选项，图标放 Assets/Elements/）</summary>
        private static readonly (string Tag, string Icon)[] QuickFilters =
        {
            ("火", "Assets/Elements/Pyro.png"),
            ("水", "Assets/Elements/Hydro.png"),
            ("风", "Assets/Elements/Anemo.png"),
            ("雷", "Assets/Elements/Electro.png"),
            ("草", "Assets/Elements/Dendro.png"),
            ("冰", "Assets/Elements/Cryo.png"),
            ("岩", "Assets/Elements/Geo.png"),
        };

        private readonly List<Border> _quickFilterButtons = new();

        /// <summary>快速筛选按钮运行时状态（含动画素材）</summary>
        private sealed class QuickFilterBtnState
        {
            public CompositeTransform Transform = null!;
            public SolidColorBrush BgBrush = null!;
            public SolidColorBrush BorderBrush = null!;

            // 6 个目标状态 Storyboard（一一对应卡片 VisualState）
            public Storyboard ToNormal = null!;
            public Storyboard ToPointerOver = null!;
            public Storyboard ToPressed = null!;
            public Storyboard ToSelected = null!;
            public Storyboard ToPointerOverSelected = null!;
            public Storyboard ToPressedSelected = null!;

            public bool IsHovering;
            public bool IsPressed;
        }

        private readonly Dictionary<Border, QuickFilterBtnState> _quickFilterBtnStates = new();

        // ===== 动画颜色常量 =====
        private const double HoverScale = 1.10;        // 悬停放大（40x40 小按钮用 10%，卡片用 2%）

        private static readonly Windows.UI.Color HoverBgColor =
            Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        private static readonly Windows.UI.Color PressedBgColor =
            Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
        private static readonly Windows.UI.Color SelectedBgColor =
            Windows.UI.Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF);  // 浅白（~88% 不透明）
        private static readonly Windows.UI.Color HoverSelectedBgColor =
            Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF);  // 浅白（~94% 不透明）

        private static readonly Windows.UI.Color TransparentColor =
            Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
        private static readonly Windows.UI.Color HoverSelectedBorderGreen =
            Windows.UI.Color.FromArgb(0xCC, 0x4C, 0xAF, 0x50);  // 绿色边框强调选中+悬停

        /// <summary>过滤掉元素标签（火水风雷草冰岩）后的芯片集合，避免和快速筛选按钮重复显示。</summary>
        private static readonly HashSet<string> ElementTagNames = new(StringComparer.OrdinalIgnoreCase)
            { "火", "水", "风", "雷", "草", "冰", "岩" };

        private readonly ObservableCollection<string> _filteredChips = new();

        private void InitializeTagInputPanel()
        {
            ChipsItemsControl.ItemsSource = _filteredChips;
            ChipsItemsControl.ItemTemplate = (DataTemplate)Resources["TagChipTemplate"];
            _vm.ActiveTagConditions.CollectionChanged += OnActiveTagsChanged;
            _vm.ActiveNameConditions.CollectionChanged += OnActiveTagsChanged;
            SyncFilteredChips();

            // 搜索框事件
            SearchBox.TextChanged += (_, _) =>
            {
                if (!_isEnterProcessing)
                {
                    Debug.WriteLine($"[CharacterPage] ★ TextChanged: text='{SearchBox.Text}' len={SearchBox.Text?.Length ?? -1}");
                    _vm.OnSearchTextChanged(SearchBox.Text ?? "");
                }
                else
                    Debug.WriteLine($"[CharacterPage] TextChanged BLOCKED: text='{SearchBox.Text}' isEnter={_isEnterProcessing}");
            };
            SearchBox.KeyDown += SearchBox_KeyDown;

            // 快速筛选元素图标
            BuildQuickFilterButtons();

            UpdateChipBarState();
        }

        private void BuildQuickFilterButtons()
        {
            _quickFilterButtons.Clear();
            _quickFilterBtnStates.Clear();

            foreach (var (tag, iconPath) in QuickFilters)
            {
                // 光晕椭圆（与卡片相同的径向渐变效果）
                var glowEllipse = new Ellipse
                {
                    Name = "HoverGlow",
                    Width = 56,
                    Height = 56,
                    IsHitTestVisible = false,
                    Opacity = 0,
                    Fill = new RadialGradientBrush
                    {
                        GradientStops =
                        {
                            new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) },
                            new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) }
                        }
                    },
                    RenderTransform = new TranslateTransform { X = -28, Y = -28 }
                };

                var glowCanvas = new Canvas
                {
                    Width = 0,
                    Height = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                };
                glowCanvas.Children.Add(glowEllipse);

                // 内容图标/文字
                FrameworkElement content;
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, iconPath)))
                {
                    content = new Image
                    {
                        Width = 28,
                        Height = 28,
                        Source = new BitmapImage(new Uri("ms-appx:///" + iconPath)),
                        Stretch = Stretch.Uniform,
                    };
                }
                else
                {
                    content = new TextBlock
                    {
                        Text = tag,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                }

                // Grid 包裹内容 + 光晕（光晕放后面 → 渲染在上层）
                var innerGrid = new Grid();
                innerGrid.Children.Add(content);
                innerGrid.Children.Add(glowCanvas);

                // 变换：ScaleX / ScaleY / TranslateY（与卡片 ItemTransform 一致）
                var scaleTransform = new CompositeTransform { ScaleX = 1.0, ScaleY = 1.0, TranslateY = 0 };

                // 专用可动画画刷（初始全透明 → 未选中时与角色卡片一致，不显示边界）
                var bgBrush = new SolidColorBrush(TransparentColor);
                var borderBrush = new SolidColorBrush(TransparentColor);

                var card = new Border
                {
                    Width = 40,
                    Height = 40,
                    Margin = new Thickness(2),   // 预留空间给 1.10 缩放，防止超出容器被裁剪
                    Padding = new Thickness(0),
                    Background = bgBrush,
                    BorderThickness = new Thickness(1),
                    BorderBrush = borderBrush,
                    CornerRadius = new CornerRadius(8),
                    Tag = tag,
                    Child = innerGrid,
                    RenderTransform = scaleTransform,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                };
                ToolTipService.SetToolTip(card, tag);

                // ---- 构建 6 个状态 Storyboard（按 GlassListViewItemStyle 方式：目标=FrameworkElement，属性路径=完整限定） ----

                // ToNormal：回退到全透明 + 透明边框（与角色卡片 Normal 一致，不显示边界）
                var toNormal = new Storyboard();
                AddAnim(toNormal, scaleTransform, "ScaleX", 1.0, 150);
                AddAnim(toNormal, scaleTransform, "ScaleY", 1.0, 150);
                AddAnim(toNormal, scaleTransform, "TranslateY", 0.0, 150);
                AddColorAnim(toNormal, card, "(Border.Background).(SolidColorBrush.Color)", TransparentColor, 150);
                AddColorAnim(toNormal, card, "(Border.BorderBrush).(SolidColorBrush.Color)", TransparentColor, 150);

                // ToPointerOver：放大 1.10 + #30FFFFFF 背景 + 透明边框，150ms
                var toPointerOver = new Storyboard();
                AddAnim(toPointerOver, scaleTransform, "ScaleX", HoverScale, 150);
                AddAnim(toPointerOver, scaleTransform, "ScaleY", HoverScale, 150);
                AddAnim(toPointerOver, scaleTransform, "TranslateY", 0.0, 150);
                AddColorAnim(toPointerOver, card, "(Border.Background).(SolidColorBrush.Color)", HoverBgColor, 150);
                AddColorAnim(toPointerOver, card, "(Border.BorderBrush).(SolidColorBrush.Color)", TransparentColor, 150);

                // ToPressed：缩小 0.96 + 下沉 2px + #10FFFFFF 背景 + 透明边框，50ms
                var toPressed = new Storyboard();
                AddAnim(toPressed, scaleTransform, "ScaleX", 0.96, 50);
                AddAnim(toPressed, scaleTransform, "ScaleY", 0.96, 50);
                AddAnim(toPressed, scaleTransform, "TranslateY", 2.0, 50);
                AddColorAnim(toPressed, card, "(Border.Background).(SolidColorBrush.Color)", PressedBgColor, 50);
                AddColorAnim(toPressed, card, "(Border.BorderBrush).(SolidColorBrush.Color)", TransparentColor, 50);

                // ToSelected：瞬时 #25FFFFFF 背景 + 透明边框 + 无缩放，0ms
                var toSelected = new Storyboard();
                AddAnim(toSelected, scaleTransform, "ScaleX", 1.0, 0);
                AddAnim(toSelected, scaleTransform, "ScaleY", 1.0, 0);
                AddAnim(toSelected, scaleTransform, "TranslateY", 0.0, 0);
                AddColorAnim(toSelected, card, "(Border.Background).(SolidColorBrush.Color)", SelectedBgColor, 0);
                AddColorAnim(toSelected, card, "(Border.BorderBrush).(SolidColorBrush.Color)", TransparentColor, 0);

                // ToPointerOverSelected：放大 1.10 + #35FFFFFF 背景 + 绿色边框（强调选中+悬停），150ms
                var toPointerOverSelected = new Storyboard();
                AddAnim(toPointerOverSelected, scaleTransform, "ScaleX", HoverScale, 150);
                AddAnim(toPointerOverSelected, scaleTransform, "ScaleY", HoverScale, 150);
                AddAnim(toPointerOverSelected, scaleTransform, "TranslateY", 0.0, 150);
                AddColorAnim(toPointerOverSelected, card, "(Border.Background).(SolidColorBrush.Color)", HoverSelectedBgColor, 150);
                AddColorAnim(toPointerOverSelected, card, "(Border.BorderBrush).(SolidColorBrush.Color)", HoverSelectedBorderGreen, 150);

                // ToPressedSelected：缩小 0.96 + 下沉 2px + 背景/边框全透明（模拟按压深度），50ms
                var toPressedSelected = new Storyboard();
                AddAnim(toPressedSelected, scaleTransform, "ScaleX", 0.96, 50);
                AddAnim(toPressedSelected, scaleTransform, "ScaleY", 0.96, 50);
                AddAnim(toPressedSelected, scaleTransform, "TranslateY", 2.0, 50);
                AddColorAnim(toPressedSelected, card, "(Border.Background).(SolidColorBrush.Color)", TransparentColor, 50);
                AddColorAnim(toPressedSelected, card, "(Border.BorderBrush).(SolidColorBrush.Color)", TransparentColor, 50);

                var state = new QuickFilterBtnState
                {
                    Transform = scaleTransform,
                    BgBrush = bgBrush,
                    BorderBrush = borderBrush,
                    ToNormal = toNormal,
                    ToPointerOver = toPointerOver,
                    ToPressed = toPressed,
                    ToSelected = toSelected,
                    ToPointerOverSelected = toPointerOverSelected,
                    ToPressedSelected = toPressedSelected,
                };

                _quickFilterBtnStates[card] = state;

                card.PointerEntered += QuickFilter_PointerEntered;
                card.PointerExited += QuickFilter_PointerExited;
                card.PointerPressed += QuickFilter_PointerPressed;
                card.PointerReleased += QuickFilter_PointerReleased;
                card.PointerMoved += QuickFilter_PointerMoved;
                card.Tapped += QuickFilter_Tapped;
                _quickFilterButtons.Add(card);
                QuickFilterPanel.Children.Add(card);
            }
        }

        /// <summary>根据当前 hover/press/selected 标志切换到正确的视觉状态</summary>
        private void TransitionQuickFilterState(Border card)
        {
            if (!_quickFilterBtnStates.TryGetValue(card, out var state)) return;

            bool isSelected = card.Tag is string tag && _vm.ActiveTagConditions.Contains(tag);

            // 停止所有正在运行的 Storyboard
            StopAllStoryboards(state);

            Storyboard target;
            if (isSelected)
            {
                target = state.IsPressed ? state.ToPressedSelected
                       : state.IsHovering ? state.ToPointerOverSelected
                       : state.ToSelected;
            }
            else
            {
                target = state.IsPressed ? state.ToPressed
                       : state.IsHovering ? state.ToPointerOver
                       : state.ToNormal;
            }
            target.Begin();
        }

        private static void StopAllStoryboards(QuickFilterBtnState state)
        {
            state.ToNormal.Stop();
            state.ToPointerOver.Stop();
            state.ToPressed.Stop();
            state.ToSelected.Stop();
            state.ToPointerOverSelected.Stop();
            state.ToPressedSelected.Stop();
        }

        // ===== 动画构建辅助 =====

        private static void AddAnim(Storyboard sb, DependencyObject target, string property, double to, double durationMs)
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            sb.Children.Add(anim);
        }

        private static void AddColorAnim(Storyboard sb, DependencyObject target, string propertyPath, Windows.UI.Color to, double durationMs)
        {
            var anim = new ColorAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, propertyPath);
            sb.Children.Add(anim);
        }

        // ===== Pointer 事件 =====

        private void QuickFilter_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border card) return;
            if (!_quickFilterBtnStates.TryGetValue(card, out var state)) return;

            state.IsHovering = true;
            state.IsPressed = false;
            TransitionQuickFilterState(card);

            if (card.Child is Grid grid)
                _previewManager.ShowGlow(grid, e);
        }

        private void QuickFilter_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border card) return;
            if (!_quickFilterBtnStates.TryGetValue(card, out var state)) return;

            state.IsHovering = false;
            state.IsPressed = false;
            TransitionQuickFilterState(card);

            _previewManager.HideGlow();
        }

        private void QuickFilter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border card) return;
            if (!_quickFilterBtnStates.TryGetValue(card, out var state)) return;

            state.IsPressed = true;
            TransitionQuickFilterState(card);
        }

        private void QuickFilter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border card) return;
            if (!_quickFilterBtnStates.TryGetValue(card, out var state)) return;

            state.IsPressed = false;
            TransitionQuickFilterState(card);
        }

        private void QuickFilter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card && card.Child is Grid grid)
                _previewManager.MoveGlow(grid, e);
        }

        /// <summary>单选项：同一时间只有一个元素标签，再次点击取消</summary>
        private void QuickFilter_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not Border card || card.Tag is not string tag) return;

            bool isSelected = _vm.ActiveTagConditions.Contains(tag);

            // 先移除所有已知元素标签
            foreach (var (knownTag, _) in QuickFilters)
            {
                if (_vm.ActiveTagConditions.Contains(knownTag))
                    _vm.ActiveTagConditions.Remove(knownTag);
            }

            // 如果之前未选中，则添加当前
            _vm.CancelPendingSearch(); // 取消输入文字的待处理防抖
            if (!isSelected)
                _vm.ActiveTagConditions.Add(tag);

            _vm.ReapplyFilter();
            _vm.CancelPendingSearch(); // 取消标签变更触发的防抖

            // 更新按钮高亮
            UpdateQuickFilterHighlights();
        }

        private void UpdateQuickFilterHighlights()
        {
            foreach (var card in _quickFilterButtons)
            {
                if (!_quickFilterBtnStates.TryGetValue(card, out var state)) continue;

                bool isSelected = card.Tag is string tag && _vm.ActiveTagConditions.Contains(tag);

                // 停止所有正在运行的动画，防止竞态
                StopAllStoryboards(state);

                // 重置变换
                state.Transform.ScaleX = 1.0;
                state.Transform.ScaleY = 1.0;
                state.Transform.TranslateY = 0;

                if (isSelected)
                {
                    // 选中态：白底 + 透明边框
                    state.BgBrush.Color = SelectedBgColor;
                    state.BorderBrush.Color = TransparentColor;
                }
                else
                {
                    // 未选中：全透明背景 + 透明边框（与角色卡片 Normal 状态一致，看不出边界）
                    state.BgBrush.Color = TransparentColor;
                    state.BorderBrush.Color = TransparentColor;
                }

                // 确保 card 的属性与 brush 引用同步（防止 storyboard 动画后引用分离）
                card.Background = state.BgBrush;
                card.BorderBrush = state.BorderBrush;
            }
        }

        private void OnActiveTagsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Debug.WriteLine($"[CharacterPage] OnActiveTagsChanged: count={_vm.ActiveTagConditions.Count}, isEnter={_isEnterProcessing}, text='{SearchBox.Text}'");
            SyncFilteredChips();
            UpdateChipBarState();
            // 仅搜索框有文字时重跑搜索（芯片+文字组合）。空文本时 ApplyFilter 已被快速筛选/Enter 调用。
            if (!_isEnterProcessing && !string.IsNullOrWhiteSpace(SearchBox.Text))
                _vm.OnSearchTextChanged(SearchBox.Text);
        }

        /// <summary>合并标签 + 名称条件到芯片区。名称芯片以"名称:"前缀区分。</summary>
        private void SyncFilteredChips()
        {
            _filteredChips.Clear();
            foreach (var tag in _vm.ActiveTagConditions)
            {
                if (!ElementTagNames.Contains(tag))
                    _filteredChips.Add(tag);
            }
            foreach (var name in _vm.ActiveNameConditions)
                _filteredChips.Add($"名称:{name}");
        }

        private void UpdateChipBarState()
        {
            bool hasChips = _vm.ActiveTagConditions.Count > 0
                         || _vm.ActiveNameConditions.Count > 0
                         || !string.IsNullOrEmpty(_vm.ModSearchText);
            ClearAllButton.Visibility = hasChips ? Visibility.Visible : Visibility.Collapsed;
            UpdateQuickFilterHighlights();
        }

        /// <summary>抑制 ListView_Mod_SelectionChanged 回写 SelectedMod，防止退出搜索/切换角色时震荡</summary>
        private bool _suppressModSelectionChanged;
        private bool _isEnterProcessing;

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    string input = SearchBox.Text.Trim();
                    int pipeIdx = input.IndexOf('|');
                    _isEnterProcessing = true;
                    try
                    {
                        _vm.ProcessSearchInput(input);
                        if (pipeIdx >= 0 && pipeIdx < input.Length - 1)
                            SearchBox.Text = input[pipeIdx..].Trim();
                        else
                            SearchBox.Text = "";
                    }
                    finally
                    {
                        _isEnterProcessing = false;
                    }
                    // 取消 Enter 处理期间 IME 可能触发的后续 TextChanged 防抖
                    // （CJK IME 在文字提交后可能异步再触发 TextChanged，用版本号消除）
                    _vm.CancelPendingSearch();
                }
                e.Handled = true;
            }
        }

        private void ChipRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string text)
            {
                if (text.StartsWith("名称:"))
                    _vm.RemoveNameCondition(text["名称:".Length..]);
                else
                    _vm.RemoveTagCondition(text);
            }
        }

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            _isEnterProcessing = true;
            try
            {
                _vm.ClearAllConditions();
                SearchBox.Text = "";
            }
            finally
            {
                _isEnterProcessing = false;
            }
            _vm.CancelPendingSearch();
        }

        // ================================================================
        // 添加角色
        // ================================================================

        private async void AddCharacterButton_Click(object sender, RoutedEventArgs e)
        {
            // 从当前活跃筛选条件预填充标签
            var presetTags = string.Join(", ", _vm.ActiveTagConditions.Concat(_vm.ActiveNameConditions));
            var tagBox = new TextBox
            {
                Text = presetTags,
                PlaceholderText = "标签, 逗号分隔, 如: 雷, 长枪, 女, 5星",
                Margin = new Thickness(0, 8, 0, 0),
                AcceptsReturn = false
            };
            var nameBox = new TextBox
            {
                PlaceholderText = "角色名称",
                Margin = new Thickness(0, 8, 0, 0)
            };

            var dialog = new ContentDialog
            {
                Title = "添加角色",
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "角色名称" },
                        nameBox,
                        new TextBlock { Text = "标签（逗号分隔）", Margin = new Thickness(0, 12, 0, 0) },
                        new TextBlock { Text = "自动填充当前筛选条件", FontSize = 11,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"] },
                        tagBox,
                    }
                },
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot,
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            string charName = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(charName))
            {
                _notif?.Show("添加失败", "角色名称不能为空", NotificationType.Error);
                return;
            }

            string charDir = Path.Combine(_vm.GetCharacterRootPath(), charName);
            if (Directory.Exists(charDir) || Directory.Exists(Path.Combine(_vm.GetCharacterRootPath(), "DISABLED" + charName)))
            {
                _notif?.Show("添加失败", $"角色 \"{charName}\" 已存在", NotificationType.Error);
                return;
            }

            // 先暂停文件监控，防止创建文件夹/写文件触发自动刷新
            _vm.PauseFileWatcher();
            try
            {
                Directory.CreateDirectory(charDir);

                // 解析标签
                var tags = tagBox.Text
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 写 info.json：根据标签值推断语义键名，与现有格式一致
                var tagDict = new Dictionary<string, string>();
                int unknownIdx = 0;
                foreach (var tag in tags)
                {
                    string key = tag switch
                    {
                        "火" or "水" or "风" or "雷" or "草" or "冰" or "岩" => "element",
                        "单手剑" or "双手剑" or "长枪" or "弓" or "法器" => "weapon",
                        "男" or "女" => "gender",
                        "萝莉" or "少女" or "成女" or "少男" or "成男" => "bodyType",
                        "4星" or "5星" => "rarity",
                        "蒙德" or "璃月" or "稻妻" or "须弥" or "枫丹" or "纳塔" or "至冬" or "坎瑞亚" or "挪德卡莱" => "region",
                        _ => $"tag_{unknownIdx++}"
                    };
                    tagDict[key] = tag;
                }
                var infoJson = System.Text.Json.JsonSerializer.Serialize(tagDict,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                File.WriteAllText(Path.Combine(charDir, Core.Constants.FileNames.CharacterInfo), infoJson);

                // 刷新列表（已在 PauseFileWatcher 保护内）
                await _vm.RefreshCommand.ExecuteAsync(null);

                // 选中新角色
                var newChar = _vm.FilteredCharacters?.FirstOrDefault(c => c.Name == charName);
                if (newChar != null)
                {
                    _vm.SelectedCharacter = newChar;
                }

                _notif?.Show("添加成功", $"角色 \"{charName}\" 已创建", NotificationType.Success);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPage] AddCharacter error: {ex}");
                _notif?.Show("添加失败", ex.Message, NotificationType.Error);
            }
            finally
            {
                _vm.ResumeFileWatcher();
            }
        }

        /// <summary>滚动到 ListView 选中项。</summary>
        /// <param name="animate">true=平滑动画（筛选恢复场景），false=瞬时定位（页面进入场景）</param>
        private static void ScrollToSelected(ListView listView, bool animate = true)
        {
            try
            {
                var selected = listView.SelectedItem;
                Debug.WriteLine($"[CharacterPage] ScrollToSelected: {listView.Name} selected={selected?.GetType().Name ?? "null"} itemsSource={((listView.ItemsSource as System.Collections.ICollection)?.Count ?? -1)}");
                if (selected == null)
                {
                    Debug.WriteLine($"[CharacterPage] ScrollToSelected: {listView.Name} SKIP - SelectedItem is null");
                    return;
                }

                var scrollViewer = FindScrollViewer(listView);
                if (scrollViewer == null)
                {
                    Debug.WriteLine($"[CharacterPage] ScrollToSelected: {listView.Name} no ScrollViewer, fallback ScrollIntoView");
                    listView.ScrollIntoView(selected, ScrollIntoViewAlignment.Leading);
                    return;
                }

                listView.ScrollIntoView(selected, ScrollIntoViewAlignment.Default);
                listView.UpdateLayout();

                var container = listView.ContainerFromItem(selected) as FrameworkElement;
                if (container != null)
                {
                    var transform = container.TransformToVisual(scrollViewer);
                    var itemTop = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                    var desired = scrollViewer.ViewportHeight * 0.4;
                    var target = scrollViewer.VerticalOffset + itemTop - desired;
                    target = Math.Max(0, Math.Min(target, scrollViewer.ScrollableHeight));
                    Debug.WriteLine($"[CharacterPage] ScrollToSelected: {listView.Name} itemTop={itemTop:F0} vp={scrollViewer.ViewportHeight:F0} desired={desired:F0} target={target:F0} animate={animate}");
                    scrollViewer.ChangeView(null, target, null, animate);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPage] ScrollToSelected error: {ex.Message}");
            }
        }

        /// <summary>从 ListView 的可视化树中查找内嵌的 ScrollViewer。</summary>
        private static ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv)
                    return sv;
                var found = FindScrollViewer(child);
                if (found != null)
                    return found;
            }
            return null;
        }

        // ================================================================
        // 搜索（保留旧的纯文本搜索为可选）
        // ================================================================

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // NOTE: 旧版搜索，已由标签筛选替代，保留供将来按需启用
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            // NOTE: 旧版搜索，已由标签筛选替代，保留供将来按需启用
        }

        // ================================================================
        // 右键事件（对齐 ModManagePage：记忆选中 + 精确恢复）
        // ================================================================

        private async void ListView_Character_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var character = GetCharacterFromEvent(e);
            if (character == null) return;

            _vm.PauseFileWatcher();
            try
            {
                // 原地切换，不重排序列表 —— 绑定自动更新图片/名字透明+删除线
                await _vm.ToggleCharacterInPlaceAsync(character);

                // 保持选中右键的角色（对象引用不变，IsEnabled 已翻转）
                ListView_Character.SelectedItem = character;
                _vm.LastSelectedCharacter = character.Name;
                _vm.LastSelectedMod = null;
                await _vm.SaveSelectionStateAsync(character.Name, null);
            }
            catch (UnauthorizedAccessException)
            {
                _notif?.Show("操作失败", $"没有权限修改 \"{character.DisplayName}\"，请检查文件夹权限设置", NotificationType.Error);
            }
            catch (DirectoryNotFoundException)
            {
                _notif?.Show("操作失败", $"角色文件夹 \"{character.DisplayName}\" 不存在，可能已被移动或删除", NotificationType.Error);
            }
            catch (IOException ex)
            {
                string reason = ex.Message.Contains("已存在")
                    ? $"名称冲突：{ex.Message}"
                    : $"文件夹 \"{character.DisplayName}\" 可能正被其他程序占用，请关闭后重试";
                _notif?.Show("操作失败", reason, NotificationType.Error);
            }
            finally
            {
                _vm.ResumeFileWatcher();
            }
        }

        private async void ListView_Mod_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var mod = GetModFromEvent(e);
            if (mod == null) return;

            // 查找 Mod 所属角色（Mod 搜索模式下 SelectedCharacter 可能为 null）
            var owner = _vm.GetCharacterForMod(mod);
            if (owner == null) return;

            string targetName = mod.ModName;
            bool wasEnabled = mod.Enable;

            _vm.PauseFileWatcher();
            try
            {
                // 临时设 SelectedCharacter 供 ToggleModAsync 用。
                // 若当前已是该角色则跳过赋值，避免触发 OnSelectedCharacterChanged 副作用。
                var prevChar = _vm.SelectedCharacter;
                if (!ReferenceEquals(prevChar, owner))
                    _vm.SelectedCharacter = owner;
                try { await _vm.ToggleModCommand.ExecuteAsync(mod); await _vm.FlushUIAsync(); }
                finally
                {
                    if (!ReferenceEquals(prevChar, owner))
                        _vm.SelectedCharacter = prevChar;
                }
            }
            catch (UnauthorizedAccessException)
            {
                _notif?.Show("操作失败", $"没有权限修改 \"{mod.DisplayName}\"，请检查文件夹权限设置", NotificationType.Error);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                _notif?.Show("操作失败", $"Mod 文件夹 \"{mod.DisplayName}\" 不存在，可能已被移动或删除", NotificationType.Error);
                return;
            }
            catch (IOException ex)
            {
                string reason = ex.Message.Contains("已存在")
                    ? $"名称冲突：{ex.Message}"
                    : $"文件夹 \"{mod.DisplayName}\" 可能正被其他程序占用，请关闭后重试";
                _notif?.Show("操作失败", reason, NotificationType.Error);
                return;
            }
            finally
            {
                _vm.ResumeFileWatcher();
            }

            if (!string.IsNullOrEmpty(_vm.ModSearchText))
            {
                var sorted = _vm.FilteredMods
                    .OrderBy(m => m.Enable ? 0 : 1)
                    .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _vm.FilteredMods = new ObservableCollection<ModItem>(sorted);
                _vm.LastSelectedMod = null;
            }
            else if (wasEnabled)
            {
                var firstEnabled = _vm.CurrentMods.FirstOrDefault(m => m.Enable);
                if (firstEnabled != null && ListView_Mod.ItemsSource == _vm.CurrentMods)
                    ListView_Mod.SelectedItem = firstEnabled;
                else if (_vm.CurrentMods.Count > 0)
                    ListView_Mod.SelectedItem = _vm.CurrentMods[0];

                _vm.LastSelectedMod = (ListView_Mod.SelectedItem as ModItem)?.ModName;
                await _vm.SaveSelectionStateAsync(_vm.LastSelectedCharacter, _vm.LastSelectedMod);

                if (ListView_Mod.SelectedItem != null)
                    _ = DispatcherQueue.TryEnqueue(() => ScrollToSelected(ListView_Mod));
            }
            else
            {
                var found = _vm.CurrentMods.FirstOrDefault(m => m.ModName == targetName
                    || m.ModName == _vm.GetToggledName(targetName));
                ListView_Mod.SelectedItem = found;

                _vm.LastSelectedMod = (ListView_Mod.SelectedItem as ModItem)?.ModName;
                await _vm.SaveSelectionStateAsync(_vm.LastSelectedCharacter, _vm.LastSelectedMod);

                if (ListView_Mod.SelectedItem != null)
                    _ = DispatcherQueue.TryEnqueue(() => ScrollToSelected(ListView_Mod));
            }
        }

        // ================================================================
        // 双击 → 资源管理器
        // ================================================================

        private void ListView_Character_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var character = GetCharacterFromEvent(e);
            if (character == null) return;

            string folderPath = Path.Combine(_vm.GetCharacterRootPath(), character.Name);
            if (Directory.Exists(folderPath))
                Process.Start("explorer.exe", folderPath);
        }

        private async void ListView_Mod_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var mod = GetModFromEvent(e);
            if (mod == null) return;

            // Mod 搜索模式（| 语法）：双击 → 退出搜索，定位到该 Mod 所属角色并选中
            if (!string.IsNullOrEmpty(_vm.ModSearchText) || _vm.IsModSearchMode)
            {
                Debug.WriteLine($"[CharacterPage] DoubleTap Mod IN SEARCH: mod={mod.ModName} modSearchText='{_vm.ModSearchText}' isModSearch={_vm.IsModSearchMode}");
                // 设 _isEnterProcessing=true 阻止清空搜索框期间的 TextChanged；
                // 等到整个异步退出完成后再恢复，防止异步 TextChanged 触发
                // ApplyFilter 覆盖退出方法设置的选中状态
                _isEnterProcessing = true;
                try { SearchBox.Text = ""; } catch { }
                _vm.CancelPendingSearch();

                // 抑制 ListView_Mod_SelectionChanged，防止退出搜索过程中
                // CurrentMods.Clear/Add 触发的 WinUI 选中事件回写 SelectedMod 造成震荡
                _suppressModSelectionChanged = true;
                try
                {
                    await _vm.ExitModSearchAndSelectModAsync(mod);
                }
                finally
                {
                    _suppressModSelectionChanged = false;
                    _isEnterProcessing = false;
                }

                Debug.WriteLine($"[CharacterPage] DoubleTap Mod AFTER exit: SelectedChar={_vm.SelectedCharacter?.Name} SelectedMod={_vm.SelectedMod?.ModName}");
                Debug.WriteLine($"[CharacterPage] DoubleTap: CharList.ItemsSource={(_vm.FilteredCharacters?.Count ?? -1)} CharList.SelectedItem={((CharacterCategory?)ListView_Character.SelectedItem)?.Name ?? "null"}");
                Debug.WriteLine($"[CharacterPage] DoubleTap: ModList.ItemsSource count={(ListView_Mod.ItemsSource as System.Collections.ICollection)?.Count ?? -1} ModList.SelectedItem={((ModItem?)ListView_Mod.SelectedItem)?.ModName ?? "null"}");

                // 确保角色列表和 Mod 列表滚动到选中项
                ListView_Character.UpdateLayout();
                if (_vm.SelectedCharacter != null)
                {
                    Debug.WriteLine($"[CharacterPage] DoubleTap: ScrollIntoView character={_vm.SelectedCharacter.Name}");
                    ListView_Character.ScrollIntoView(_vm.SelectedCharacter, ScrollIntoViewAlignment.Leading);
                }
                if (_vm.SelectedMod != null)
                {
                    Debug.WriteLine($"[CharacterPage] DoubleTap: ScrollIntoView mod={_vm.SelectedMod.ModName}");
                    ListView_Mod.ScrollIntoView(_vm.SelectedMod, ScrollIntoViewAlignment.Leading);
                }
                return;
            }

            // 普通角色模式：双击 → 打开资源管理器
            if (_vm.SelectedCharacter == null) return;
            string folderPath = Path.Combine(
                _vm.GetCharacterRootPath(), _vm.SelectedCharacter.Name, mod.ModName);
            if (Directory.Exists(folderPath))
                Process.Start("explorer.exe", folderPath);
        }

        // ================================================================
        // 拖放
        // ================================================================

        private void ListView_Mod_DragOver(object sender, DragEventArgs e)
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

        private async void ListView_Mod_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count == 0) return;

            var character = _vm.SelectedCharacter;

            _vm.PauseFileWatcher();
            try
            {
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFolder folder)
                    {
                        if (character == null) continue;
                        string destPath = Path.Combine(_vm.GetCharacterRootPath(), character.Name, folder.Name);
                        if (!Directory.Exists(destPath))
                            await ModManageViewModel.CopyFolderAsync(folder.Path, destPath);
                    }
                    else if (item is Windows.Storage.StorageFile file)
                    {
                        var ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
                        if (ext != ".zip" && ext != ".rar" && ext != ".7z") continue;

                        var charName = await PickCharacterAsync();
                        if (string.IsNullOrWhiteSpace(charName)) continue;

                        var modName = SanitizeFileName(System.IO.Path.GetFileNameWithoutExtension(file.Name));
                        var targetDir = System.IO.Path.Combine(_vm.GetCharacterRootPath(), charName, modName);
                        Directory.CreateDirectory(targetDir);

                        var destFile = System.IO.Path.Combine(targetDir, file.Name);
                        if (!System.IO.File.Exists(destFile))
                        {
                            System.IO.File.Copy(file.Path, destFile);
                            _notif?.Show("Mod 已安装", $"{charName}/{modName}", NotificationType.Success);
                        }
                    }
                }

                // 刷新
                await _vm.RefreshCommand.ExecuteAsync(null);
            }
            finally
            {
                _vm.ResumeFileWatcher();
            }
        }

        // ================================================================
        // 键盘操作：Ctrl+V 粘贴文件夹, F2 重命名 Mod
        // ================================================================

        // ================================================================
        // 键盘处理
        // ================================================================

        private void SetupKeyboardHandlers()
        {
            this.IsTabStop = true;

            // 1) AddHandler：Page 自身捕获所有 KeyDown
            this.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
            {
                if (e.OriginalSource is TextBox) return;
                if (_renameTextBox != null) return;

                var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
                bool ctrlDown = (ctrl & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

                if (e.Key == VirtualKey.V && ctrlDown)
                {
                    e.Handled = true;
                    _ = PasteFolderAsModAsync();
                }
                else if (e.Key == VirtualKey.F2)
                {
                    e.Handled = true;
                    _ = RenameSelectedModAsync();
                }
            }), true);

            // 2) 点击非 TextBox 区域聚焦 Page
            RootLayoutGrid.PointerPressed += (_, args) =>
            {
                if (args.OriginalSource is not TextBox)
                    this.Focus(FocusState.Programmatic);
            };

            // 3) 仅当焦点被 ListViewItem/ScrollViewer 抢走时才夺回；
            //    Button/Expander/TextBox/DataGrid 等控件完全不受影响
            this.LostFocus += (_, _) =>
            {
                var f = FocusManager.GetFocusedElement(this.XamlRoot);
                if (f is ListViewItem || f is ScrollViewer)
                {
                    this.Focus(FocusState.Programmatic);
                    Debug.WriteLine("[CharacterPage] LostFocus: stole back from " + f.GetType().Name);
                }
            };
        }

        private async Task PasteFolderAsModAsync()
        {
            try
            {
                // Mod 搜索模式下没有选中的角色，静默禁用粘贴
                if (!string.IsNullOrEmpty(_vm.ModSearchText) || _vm.IsModSearchMode)
                    return;

                var character = _vm.SelectedCharacter;
                if (character == null)
                {
                    _notif?.Show("无法粘贴", "请先在左侧选中一个角色", NotificationType.Warning);
                    return;
                }

                var dataPackageView = Clipboard.GetContent();
                if (!dataPackageView.Contains(StandardDataFormats.StorageItems))
                    return;

                var items = await dataPackageView.GetStorageItemsAsync();
                if (items.Count == 0) return;

                string targetDir = Path.Combine(_vm.GetCharacterRootPath(), character.Name);
                Directory.CreateDirectory(targetDir);

                ModItem? lastAdded = null;
                _vm.PauseFileWatcher();
                try
                {
                    foreach (var item in items)
                    {
                        if (item is not Windows.Storage.StorageFolder folder)
                            continue;

                        string destPath = Path.Combine(targetDir, folder.Name);
                        if (Directory.Exists(destPath))
                        {
                            _notif?.Show("粘贴失败", $"文件夹 \"{folder.Name}\" 已存在", NotificationType.Error);
                            continue;
                        }

                        await ModManageViewModel.CopyFolderAsync(folder.Path, destPath);

                        // 增量添加：只扫描新文件夹，不全量刷新
                        lastAdded = _vm.AddModToCharacter(character.Name, destPath);
                    }
                }
                finally
                {
                    _vm.ResumeFileWatcher();
                }

                // 选中最后添加的 Mod 并加载预览
                if (lastAdded != null)
                {
                    await _vm.FlushUIAsync();
                    _vm.SelectedMod = lastAdded;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPage] PasteFolderAsMod error: {ex.Message}");
                _notif?.Show("粘贴失败", ex.Message, NotificationType.Error);
            }
        }

        private TextBox? _renameTextBox;
        private ModItem? _renameTarget;

        private async Task RenameSelectedModAsync()
        {
            try
            {
                var mod = ListView_Mod.SelectedItem as ModItem;
                var character = _vm.SelectedCharacter;
                if (mod == null || character == null) return;

                // 查找选中的 ListViewItem 容器
                var container = ListView_Mod.ContainerFromItem(mod) as ListViewItem;
                if (container == null) return;

                // 查找模板内的 TextBlock 和 TextBox
                var textBlock = FindChildByName<TextBlock>(container, "ModNameTextBlock");
                var textBox = FindChildByName<TextBox>(container, "ModNameTextBox");
                if (textBlock == null || textBox == null) return;

                // 进入编辑模式
                textBlock.Visibility = Visibility.Collapsed;
                textBox.Visibility = Visibility.Visible;
                textBox.Text = mod.DisplayName;
                textBox.SelectAll();
                textBox.Focus(FocusState.Programmatic);

                _renameTextBox = textBox;
                _renameTarget = mod;

                // Enter=确认, Escape=取消
                textBox.KeyDown += RenameTextBox_KeyDown;
                textBox.LostFocus += RenameTextBox_LostFocus;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPage] RenameSelectedMod error: {ex.Message}");
            }
        }

        private async void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                textBox.KeyDown -= RenameTextBox_KeyDown;
                textBox.LostFocus -= RenameTextBox_LostFocus;
                await CommitRenameAsync(textBox);
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CancelRename(textBox);
            }
        }

        private async void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox textBox) return;
            textBox.KeyDown -= RenameTextBox_KeyDown;
            textBox.LostFocus -= RenameTextBox_LostFocus;
            await CommitRenameAsync(textBox);
        }

        private async Task CommitRenameAsync(TextBox textBox)
        {
            var mod = _renameTarget;
            _renameTarget = null;
            _renameTextBox = null;

            if (mod == null) { ExitEditMode(textBox); return; }

            try
            {
                var character = _vm.SelectedCharacter;
                if (character == null) { ExitEditMode(textBox); return; }

                string newDisplayName = textBox.Text.Trim();
                if (string.IsNullOrEmpty(newDisplayName) || newDisplayName == mod.DisplayName)
                {
                    ExitEditMode(textBox);
                    return;
                }

                // 统一规则：禁止用户输入 DISABLED 前缀（无论当前是否禁用，应用内只能通过右键切换状态）
                if (newDisplayName.StartsWith("DISABLED", StringComparison.OrdinalIgnoreCase))
                {
                    _notif?.Show("重命名失败", "请勿在名称中使用 DISABLED 前缀，使用右键菜单切换启用/禁用状态", NotificationType.Error);
                    ExitEditMode(textBox);
                    return;
                }

                string charDir = Path.Combine(_vm.GetCharacterRootPath(), character.Name);
                string oldPath = Path.Combine(charDir, mod.ModName);
                bool oldDisabled = mod.ModName.StartsWith("DISABLED", StringComparison.OrdinalIgnoreCase);
                // 禁用 Mod 自动保留 DISABLED 前缀（DisplayName 不显示它）
                string newDirName = oldDisabled ? "DISABLED" + newDisplayName : newDisplayName;
                string newPath = Path.Combine(charDir, newDirName);

                if (!Directory.Exists(oldPath))
                {
                    _notif?.Show("重命名失败", $"文件夹 \"{mod.DisplayName}\" 不存在", NotificationType.Error);
                    ExitEditMode(textBox);
                    return;
                }
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    // 仅大小写变化
                }
                else if (Directory.Exists(newPath))
                {
                    _notif?.Show("重命名失败", $"目标名称 \"{newDisplayName}\" 已存在", NotificationType.Error);
                    ExitEditMode(textBox);
                    return;
                }

                _vm.PauseFileWatcher();
                try
                {
                    if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                        Directory.Move(oldPath, newPath);

                    // 原地更新内存数据
                    mod.ModName = newDirName;
                    mod.DisplayName = newDisplayName;
                    mod.PreviewImage = _vm.GetBestPreviewImagePath(newPath);

                    _vm.ReloadCurrentModDetail();
                }
                finally
                {
                    _vm.ResumeFileWatcher();
                }

                // 重命名成功不需要通知（内联编辑反馈已足够）
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CharacterPage] CommitRename error: {ex.Message}");
                _notif?.Show("重命名失败", ex.Message, NotificationType.Error);
            }
            finally
            {
                ExitEditMode(textBox);
            }
        }

        private void CancelRename(TextBox textBox)
        {
            _renameTarget = null;
            _renameTextBox = null;
            ExitEditMode(textBox);
        }

        private static void ExitEditMode(TextBox textBox)
        {
            textBox.Visibility = Visibility.Collapsed;
            if (textBox.Parent is Grid grid)
            {
                foreach (var child in grid.Children)
                {
                    if (child is TextBlock tb && tb.Name == "ModNameTextBlock")
                    {
                        tb.Visibility = Visibility.Visible;
                        break;
                    }
                }
            }
        }

        // ================================================================
        // 预览图操作
        // ================================================================

        private async void Button_PasteClipboardImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var character = _vm.SelectedCharacter;
                var mod = _vm.SelectedMod;
                if (character == null || mod == null) return;

                string modFolder = Path.Combine(
                    _vm.GetCharacterRootPath(), character.Name, mod.ModName);
                if (!Directory.Exists(modFolder)) return;

                var dataPackageView = Clipboard.GetContent();
                if (!dataPackageView.Contains(StandardDataFormats.Bitmap)) return;

                var bitmapReference = await dataPackageView.GetBitmapAsync();
                if (bitmapReference == null) return;

                using var imageStream = await bitmapReference.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(imageStream);
                var frame = await decoder.GetFrameAsync(0);
                var pixelData = await frame.GetPixelDataAsync();
                var pixels = pixelData.DetachPixelData();

                await SaveClipboardImageToModFolderAsync(modFolder, pixels,
                    frame.PixelWidth, frame.PixelHeight, decoder.DpiX, decoder.DpiY,
                    frame.BitmapPixelFormat, frame.BitmapAlphaMode);

                // 重新加载预览图
                var bitmap = await _vm.LoadPreviewImageAsync(modFolder);
                ModPreviewImage.Source = bitmap;
                SetPreviewVisibility(bitmap != null);
                PreviewImageStoryboard?.Stop();
                PreviewImageStoryboard?.Begin();
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
                var character = _vm.SelectedCharacter;
                var mod = _vm.SelectedMod;
                if (character == null || mod == null) return;

                string modFolder = Path.Combine(
                    _vm.GetCharacterRootPath(), character.Name, mod.ModName);
                if (!Directory.Exists(modFolder)) return;

                // 重命名预览文件为 .back
                DeletePreviewImage(modFolder);

                var bitmap = await _vm.LoadPreviewImageAsync(modFolder);
                ModPreviewImage.Source = bitmap;
                SetPreviewVisibility(bitmap != null);
                PreviewImageStoryboard?.Stop();
                PreviewImageStoryboard?.Begin();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"删除预览图失败: {ex.Message}");
            }
        }

        // ================================================================
        // Pointer 事件（光晕 + 悬停预览）
        // ================================================================

        private void ModItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not ModItem modItem) return;

            _previewManager.ShowGlow(element, e);

            if (string.IsNullOrEmpty(modItem.PreviewImage))
            {
                var character = _vm.SelectedCharacter ?? _vm.GetCharacterForMod(modItem);
                if (character != null)
                    _vm.EnsureModPreviewPath(modItem, character.Name);
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

        private void Character_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not CharacterCategory character) return;

            _previewManager.ShowGlow(element, e);

            ModItem? firstEnabled = character.Mods.FirstOrDefault(m => m.Enable);
            if (firstEnabled != null && string.IsNullOrEmpty(firstEnabled.PreviewImage))
                _vm.EnsureModPreviewPath(firstEnabled, character.Name);

            string? preview = character.Mods
                .FirstOrDefault(m => m.Enable && !string.IsNullOrEmpty(m.PreviewImage))
                ?.PreviewImage;

            if (string.IsNullOrEmpty(preview))
            {
                _previewManager.HidePreview(character);
                return;
            }
            _previewManager.ShowPreview(preview, e, character);
        }

        private void Character_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _previewManager.HideGlow();
            _previewManager.ForceHidePreview();
        }

        private void Character_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            _previewManager.MoveGlow(element, e);
            _previewManager.MovePreview(e);
        }

        // ================================================================
        // 预设
        // ================================================================
        private IModPresetService? _presetService;
        private List<ModPreset> _allPresets = new();
        private readonly ObservableCollection<ModPreset> _presetList = new();
        private ModPreset? _editingPreset;
        private bool _presetMode;

        private async Task EnsurePresetServiceAsync()
        {
            _presetService ??= App.GetService<IModPresetService>();
            if (PresetListView.ItemsSource == null)
                PresetListView.ItemsSource = _presetList;
        }

        private async Task LoadPresetsAsync()
        {
            await EnsurePresetServiceAsync();
            var character = _vm.SelectedCharacter;
            var mod = _vm.SelectedMod;
            if (_presetService == null || character == null || mod == null) return;
            _allPresets = await _presetService.LoadPresetsAsync(character.Name);
            _presetList.Clear();
            foreach (var p in _allPresets.Where(p => p.ModFolderName == mod.ModName))
                _presetList.Add(p);
        }

        private async Task SavePresetsAsync()
        {
            if (_presetService == null) return;
            var character = _vm.SelectedCharacter;
            var mod = _vm.SelectedMod;
            if (character == null || mod == null) return;
            _allPresets.RemoveAll(p => p.ModFolderName == mod.ModName);
            _allPresets.AddRange(_presetList);
            await _presetService.SavePresetsAsync(character.Name, _allPresets);
        }

        private async Task LoadIniValuesToDataGridAsync()
        {
            await EnsurePresetServiceAsync();
            var modsRoot = _vm.GetModsFolderPath();
            var parentDir = Path.GetDirectoryName(modsRoot);
            if (parentDir == null) return;
            var userIni = Path.Combine(parentDir, Core.Constants.FileNames.D3dxUserIni);
            if (!File.Exists(userIni)) return;

            // 直接解析 d3dx_user.ini，按变量名最后一段匹配 ModKey.VariableName
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in await File.ReadAllLinesAsync(userIni))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(";") || trimmed.StartsWith("[")) continue;
                var eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                var key = trimmed.Substring(0, eq).Trim();
                var val = trimmed.Substring(eq + 1).Trim();
                // 提取变量名：最后一个 \ 之后的部分
                var lastSlash = key.LastIndexOf('\\');
                var varName = lastSlash >= 0 ? key.Substring(lastSlash + 1) : key;
                values[varName] = val;
            }

            foreach (var k in _vm.ModKeys)
            {
                if (!string.IsNullOrWhiteSpace(k.VariableName) && values.TryGetValue(k.VariableName, out var v))
                    k.CurrentIniValue = v;
                else
                    k.CurrentIniValue = "";
            }
        }

        private void ApplyPresetToDataGrid()
        {
            if (_editingPreset == null) return;
            foreach (var key in _vm.ModKeys)
            {
                var m = _editingPreset.Variables.FirstOrDefault(v =>
                    v.VariableName.Equals(key.VariableName, StringComparison.OrdinalIgnoreCase));
                key.CurrentIniValue = m?.TargetValue.ToString("G") ?? "";
            }
        }

        private void PresetToggle_Click(object sender, RoutedEventArgs e)
        {
            _presetMode = !_presetMode;
            PresetPanel.Visibility = _presetMode ? Visibility.Visible : Visibility.Collapsed;
            DataGrid_ModKeyList.Visibility = _presetMode ? Visibility.Collapsed : Visibility.Visible;
            PresetToggleButton.Content = _presetMode ? "按键 ◀" : "预设 ▶";
            KeyListHeaderText.Text = _presetMode ? "预设管理" : "Mod按键列表";
            if (!_presetMode) { PresetEditPanel.Visibility = Visibility.Collapsed; PresetListViewPanel.Visibility = Visibility.Visible; }
        }

        private void PresetEdit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ModPreset preset) return;
            _editingPreset = preset;
            PresetEditNameBox.Text = preset.Name;
            PresetListViewPanel.Visibility = Visibility.Collapsed;
            PresetEditPanel.Visibility = Visibility.Visible;
            DataGrid_ModKeyList.Visibility = Visibility.Visible;
            DataGrid_ModKeyList.IsReadOnly = false;
            ApplyPresetToDataGrid();
        }

        private void PresetEditDone_Click(object sender, RoutedEventArgs e)
        {
            if (_editingPreset != null)
            {
                var newName = PresetEditNameBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(newName)) return;
                // 重名检查（排除自身）
                if (_presetList.Any(p => p != _editingPreset && p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                { _notif?.Show("名称冲突", $"预设 \"{newName}\" 已存在", NotificationType.Warning); return; }
                _editingPreset.Name = newName;
                foreach (var key in _vm.ModKeys)
                {
                    var m = _editingPreset.Variables.FirstOrDefault(v =>
                        v.VariableName.Equals(key.VariableName, StringComparison.OrdinalIgnoreCase));
                    if (m != null && double.TryParse(key.CurrentIniValue, out var tv))
                        m.TargetValue = tv;
                }
                var idx = _presetList.IndexOf(_editingPreset);
                if (idx >= 0) _presetList[idx] = _editingPreset;
                _ = SavePresetsAsync();
            }
            _editingPreset = null;
            PresetEditPanel.Visibility = Visibility.Collapsed;
            PresetListViewPanel.Visibility = Visibility.Visible;
            DataGrid_ModKeyList.Visibility = Visibility.Collapsed;
            DataGrid_ModKeyList.IsReadOnly = true;
        }

        private async void PresetApply_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ModPreset preset) return;
            if (preset.Variables.Count == 0) return;
            await _presetService!.WritePresetAsync(preset.Variables);
        }

        private async void PresetDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ModPreset preset) return;
            var dlg = new ContentDialog
            {
                Title = "确认删除",
                Content = $"删除预设 \"{preset.Name}\"?",
                PrimaryButtonText = "删除",
                SecondaryButtonText = "取消",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            if (_editingPreset == preset) { _editingPreset = null; PresetEditPanel.Visibility = Visibility.Collapsed; PresetListViewPanel.Visibility = Visibility.Visible; DataGrid_ModKeyList.Visibility = Visibility.Collapsed; }
            _presetList.Remove(preset);
            await SavePresetsAsync();
        }

        private async void PresetNew_Click(object sender, RoutedEventArgs e)
        {
            await EnsurePresetServiceAsync();
            var character = _vm.SelectedCharacter;
            var mod = _vm.SelectedMod;
            if (_presetService == null || character == null || mod == null) return;
            var vars = await _presetService.ScanModVarsAsync(character.Name);
            vars = vars.Where(v => v.ModFolderName.Equals(mod.ModName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (vars.Count == 0) return;
            // 自动去重命名
            var baseName = "新预设";
            var name = baseName; int n = 1;
            while (_presetList.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = $"{baseName} ({++n})";
            var preset = new ModPreset { Name = name, ModFolderName = mod.ModName, Variables = vars };
            _presetList.Add(preset);
            await SavePresetsAsync();
            _editingPreset = preset;
            PresetEditNameBox.Text = preset.Name;
            PresetListViewPanel.Visibility = Visibility.Collapsed;
            PresetEditPanel.Visibility = Visibility.Visible;
            DataGrid_ModKeyList.Visibility = Visibility.Visible;
            DataGrid_ModKeyList.IsReadOnly = false;
            ApplyPresetToDataGrid();
        }

        private async void PresetReadCreate_Click(object sender, RoutedEventArgs e)
        {
            await EnsurePresetServiceAsync();
            var character = _vm.SelectedCharacter;
            var mod = _vm.SelectedMod;
            if (_presetService == null || character == null || mod == null) return;
            var vars = await _presetService.ScanModVarsAsync(character.Name);
            vars = vars.Where(v => v.ModFolderName.Equals(mod.ModName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (vars.Count == 0) return;
            var tempPresets = new List<ModPreset> { new ModPreset { Name = "temp", ModFolderName = mod.ModName, Variables = vars } };
            await _presetService.ReadCurrentValuesAsync(character.Name, tempPresets);
            var preset = tempPresets[0];
            // 自动去重命名
            var baseName = "INI读取";
            var name = baseName; int n = 1;
            while (_presetList.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = $"{baseName} ({++n})";
            preset.Name = name;
            _presetList.Add(preset);
            await SavePresetsAsync();
            _editingPreset = preset;
            PresetEditNameBox.Text = preset.Name;
            PresetListViewPanel.Visibility = Visibility.Collapsed;
            PresetEditPanel.Visibility = Visibility.Visible;
            DataGrid_ModKeyList.Visibility = Visibility.Visible;
            DataGrid_ModKeyList.IsReadOnly = false;
            ApplyPresetToDataGrid();
        }

        // ================================================================
        // 内部辅助
        // ================================================================

        private async Task<string?> PickCharacterAsync()
        {
            var chars = _vm.FilteredCharacters.Count > 0
                ? _vm.FilteredCharacters.Select(c => c.Name).ToList()
                : new List<string>();
            if (chars.Count == 0) return _vm.SelectedCharacter?.Name;

            var combo = new ComboBox { MinWidth = 200, MaxDropDownHeight = 300 };
            foreach (var c in chars) combo.Items.Add(c);
            if (_vm.SelectedCharacter != null)
                combo.SelectedItem = _vm.SelectedCharacter.Name;

            var dlg = new ContentDialog
            {
                Title = "选择目标角色",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "未找到元数据文件。请选择将 Mod 安装到哪个角色:" },
                        combo
                    }
                },
                PrimaryButtonText = "安装",
                SecondaryButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            return await dlg.ShowAsync() == ContentDialogResult.Primary
                ? combo.SelectedItem?.ToString()
                : null;
        }

        private static string SanitizeFileName(string name)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                (name ?? "").Replace('\n', ' ').Replace('\r', ' '),
                @"\s+", " ").Trim();
        }

        private void SetPreviewVisibility(bool hasImages)
        {
            ImageViewerGrid.Visibility = hasImages ? Visibility.Visible : Visibility.Collapsed;
            AddImageButton.Visibility = hasImages ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>预览图加载完成时淡入显示。同时供 Source 赋值后主动调用以覆盖同步加载场景。</summary>
        private void ModPreviewImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            PreviewImageStoryboard?.Stop();
            PreviewImageStoryboard?.Begin();
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null && parent is not T)
                parent = VisualTreeHelper.GetParent(parent);
            return parent as T;
        }

        // ================================================================
        // Material Ripple（与 DownloadsPage 统一）
        // ================================================================

        private static void RegisterRippleHandler(ListView listView)
        {
            listView.ContainerContentChanging += (_, args) =>
            {
                if (args.InRecycleQueue) return;
                if (args.ItemContainer is ListViewItem lvi)
                    lvi.AddHandler(UIElement.PointerPressedEvent,
                        new PointerEventHandler(MaterialRipple_PointerPressed), true);
            };
        }

        private static void MaterialRipple_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 编辑 Mod 名称时，点击输入框不应触发卡片动画
            if (e.OriginalSource is TextBox) return;

            var host = FindChildByName((DependencyObject)sender, "RippleHost") as Canvas;
            if (host == null || host.ActualWidth <= 0) return;

            var pt = e.GetCurrentPoint(host).Position;
            double w = host.ActualWidth, h = host.ActualHeight;
            double x = pt.X, y = pt.Y;

            double targetR = new[]
            {
                Math.Sqrt(x * x + y * y),
                Math.Sqrt((w - x) * (w - x) + y * y),
                Math.Sqrt(x * x + (h - y) * (h - y)),
                Math.Sqrt((w - x) * (w - x) + (h - y) * (h - y))
            }.Max();

            var ripple = new Ellipse
            {
                Width = targetR * 2, Height = targetR * 2,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(38, 255, 255, 255)),
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = 0, ScaleY = 0 }
            };
            Canvas.SetLeft(ripple, x - targetR);
            Canvas.SetTop(ripple, y - targetR);
            host.Children.Add(ripple);

            var sb = new Storyboard();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var scaleX = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
            var scaleY = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = ease };
            var fade = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(550) };

            Storyboard.SetTarget(scaleX, ripple);
            Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
            Storyboard.SetTarget(scaleY, ripple);
            Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
            Storyboard.SetTarget(fade, ripple);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var task = ripple;
            sb.Completed += (_, _) => { try { host.Children.Remove(task); } catch { } };
            sb.Children.Add(scaleX);
            sb.Children.Add(scaleY);
            sb.Children.Add(fade);
            sb.Begin();
        }

        private static DependencyObject? FindChildByName(DependencyObject parent, string name)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && fe.Name == name) return fe;
                var found = FindChildByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>在可视化树中按名称查找子元素。</summary>
        private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && element.Name == name)
                    return element;
                var found = FindChildByName<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>从路由事件中提取点击位置的 ListViewItem.Content。</summary>
        private static T? GetItemFromEvent<T>(RoutedEventArgs e) where T : class
        {
            if (e.OriginalSource is not FrameworkElement source) return null;
            var container = FindParent<ListViewItem>(source);
            return container?.Content as T;
        }

        private static CharacterCategory? GetCharacterFromEvent(RoutedEventArgs e) => GetItemFromEvent<CharacterCategory>(e);
        private static ModItem? GetModFromEvent(RoutedEventArgs e) => GetItemFromEvent<ModItem>(e);

        private static async Task SaveClipboardImageToModFolderAsync(
            string modFolder, byte[] pixels,
            uint width, uint height, double dpiX, double dpiY,
            BitmapPixelFormat pixelFormat, BitmapAlphaMode alphaMode)
        {
            if (!Directory.Exists(modFolder))
                Directory.CreateDirectory(modFolder);

            string previewFilePath = Path.Combine(modFolder, "preview.png");
            using var memStream = new MemoryStream();
            var encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId, memStream.AsRandomAccessStream());
            encoder.SetPixelData(pixelFormat, alphaMode, width, height, dpiX, dpiY, pixels);
            await encoder.FlushAsync();
            File.WriteAllBytes(previewFilePath, memStream.ToArray());
        }

        private const int MaxPreviewBackupCount = 10;

        private static void DeletePreviewImage(string modFolder)
        {
            string previewPath = Path.Combine(modFolder, "preview.png");
            if (!File.Exists(previewPath)) return;

            string dir = Path.GetDirectoryName(previewPath)!;
            string baseName = Path.GetFileNameWithoutExtension(previewPath) + Path.GetExtension(previewPath) + ".back";
            string backupPath = Path.Combine(dir, baseName);

            if (!File.Exists(backupPath))
            {
                File.Move(previewPath, backupPath);
                return;
            }

            for (int i = 1; i <= MaxPreviewBackupCount; i++)
            {
                string candidate = Path.Combine(dir, baseName + i);
                if (!File.Exists(candidate))
                {
                    File.Move(previewPath, candidate);
                    return;
                }
            }
            Debug.WriteLine($"[CharacterPage] DeletePreviewImage: 达到最大备份数 {MaxPreviewBackupCount}，无法重命名 {previewPath}");
        }
    }
}
