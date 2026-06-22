using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ModManager.Core.Helpers;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using ModManager.Application.ViewModels;
using ModManager.Application.Services;
using ModManager.Core.Contracts.Services;
using ModManager.Services;
using ModManager.Core.Messages;
using ModManager.Core.Models;
using Windows.Graphics;
using Windows.Media.Playback;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIEx;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace ModManager.Views;

public sealed partial class MainWindow : WindowEx
{

	#region
	// 常量
	private const string SettingsViewModelTag = "ModManager.ViewModels.SettingsViewModel";
			private const int DefaultWindowWidth = 1360;
	private const int DefaultWindowHeight = 768;

	// 依赖服务
	private readonly ILocalSettingsService _localSettingsService;
	private readonly IBackgroundRenderer _backgroundRenderer;
	private readonly MainWindowViewModel _viewModel;

	// 状态字段
	private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
		private NotificationService? _notificationService;
	private UISettings _settings;
	private bool _isOverlayShown;
	private bool _isNavigating;
	private bool _minimizeToTray;
	private bool _isExit;
	private bool _isClosing;
	private bool _isMainUiLoaded;
	private bool _isVideoBackground;
		private bool _isAcrylicOverlayEnabled;
	private MediaPlayer? _globalBackgroundPlayer;
	#endregion

	#region
	// 命令
	public IRelayCommand ExitApplicationCommand { get; }

	// 构造函数（依赖注入）
	public MainWindow(ILocalSettingsService localSettingsService, IBackgroundRenderer backgroundRenderer)
	{
		InitializeComponent();

		_localSettingsService = localSettingsService;
		_backgroundRenderer = backgroundRenderer;
		_viewModel = new MainWindowViewModel(localSettingsService, backgroundRenderer);
		_dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
		_settings = new UISettings();


		// 初始化通知服务
		_notificationService = App.GetService<INotificationService>() as NotificationService;
		_notificationService?.Initialize(NotificationHost, (FrameworkElement)Content);
		// 窗口基本设置
		var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LanYan.ico");
		TitleBarIcon.Source = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
		AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/LanYan.ico"));
		Title = "ModManager";
		ExtendsContentIntoTitleBar = true;
			SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };

		// 生命周期事件
		AppWindow.Closing += AppWindow_Closing;
		SizeChanged += MainWindow_SizeChanged;
		_settings.ColorValuesChanged += OnColorValuesChanged;

		// 命令
		ExitApplicationCommand = new RelayCommand(ExitApplication);

		// 注册消息（关闭时统一解注册）
		WeakReferenceMessenger.Default.Register<OverlayStyleChangedMessage>(this, OnOverlayStyleChanged);
		WeakReferenceMessenger.Default.Register<BackgroundRefreshMessage>(this, OnBackgroundRefresh);
		WeakReferenceMessenger.Default.Register<ThemeChangedMessage>(this, OnThemeChanged);
		WeakReferenceMessenger.Default.Register<BackgroundImageOpacityChangedMessage>(this, OnBackgroundImageOpacityChanged);
			WeakReferenceMessenger.Default.Register<MinimizeToTrayChangedMessage>(this, OnMinimizeToTrayChanged);
	}

	// 消息处理方法（调度到 UI 线程）

	private void OnOverlayStyleChanged(object recipient, OverlayStyleChangedMessage m)
	{
		_isAcrylicOverlayEnabled = m.Value;
		_dispatcherQueue.TryEnqueue(UpdateBackgroundOverlayTheme);
	}

	private void OnBackgroundRefresh(object recipient, BackgroundRefreshMessage _)
		=> _dispatcherQueue.TryEnqueue(async () => await LoadGlobalBackgroundAsync());

	private void OnThemeChanged(object recipient, ThemeChangedMessage m)
	{
		var theme = m.Value switch
		{
			0 => ElementTheme.Light,
			1 => ElementTheme.Dark,
			_ => ElementTheme.Default
		};
		_dispatcherQueue.TryEnqueue(() =>
		{
			if (Content is FrameworkElement root)
				root.RequestedTheme = theme;
			UpdateBackgroundOverlayTheme();
				_notificationService?.UpdateTheme();
		});
	}


	private void OnBackgroundImageOpacityChanged(object recipient, BackgroundImageOpacityChangedMessage m)
		=> _dispatcherQueue.TryEnqueue(() => ApplyBackgroundImageOpacity(m.Value));

	private void OnMinimizeToTrayChanged(object recipient, MinimizeToTrayChangedMessage m)
		=> _dispatcherQueue.TryEnqueue(() => _minimizeToTray = m.Value);

	#endregion

	#region 窗口生命周期与尺寸管理

	private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
	{
		if (_isExit) return;
		args.Cancel = true;
		_isClosing = true;

		UnregisterAll();

		if (_minimizeToTray)
		{
			AppWindow.Hide();
		}
		else
		{
			await SaveCurrentPageStateAsync();
				await SaveWindowSizeAsync();

			DisposeResources();
			_isExit = true;
			Close();
		}
	}

	private async void ExitApplication()
	{
		try
		{
			_isClosing = true;
			UnregisterAll();
			await SaveCurrentPageStateAsync();
			await SaveWindowSizeAsync();
			DisposeResources();
			_isExit = true;
			Close();
		}
		catch (Exception ex)
		{
			Log.Error("退出应用程序时发生异常", ex);
		}
	}

	private void UnregisterAll()
	{
		WeakReferenceMessenger.Default.Unregister<OverlayStyleChangedMessage>(this);
		WeakReferenceMessenger.Default.Unregister<BackgroundRefreshMessage>(this);
		WeakReferenceMessenger.Default.Unregister<ThemeChangedMessage>(this);
		WeakReferenceMessenger.Default.Unregister<BackgroundImageOpacityChangedMessage>(this);
			WeakReferenceMessenger.Default.Unregister<MinimizeToTrayChangedMessage>(this);
		if (_settings != null)
			_settings.ColorValuesChanged -= OnColorValuesChanged;
	}

	private void DisposeResources()
	{
		_globalBackgroundPlayer?.Dispose();
		_globalBackgroundPlayer = null;
	}

	private async Task SaveWindowSizeAsync()
	{
		try
		{
			bool saveEnabled = await _localSettingsService.ReadSettingAsync<bool>(LocalSettingsService.IsSaveWindowSizeEnabledKey);
			if (saveEnabled)
			{
				await _localSettingsService.SaveSettingAsync(LocalSettingsService.SavedWindowWidthKey, Width);
				await _localSettingsService.SaveSettingAsync(LocalSettingsService.SavedWindowHeightKey, Height);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"保存窗口尺寸失败: {ex.Message}");
		}
	}

			/// <summary>保存当前页面状态（角色/Mod 选中），防止退出时丢失</summary>

		private async Task SaveCurrentPageStateAsync()

		{

			try

			{

				if (ContentFrame.Content is CharacterPage charPage)

				{

					await charPage.SaveCurrentStateAsync();

				}

			}

			catch (Exception ex)

			{

				Debug.WriteLine($"保存页面状态失败: {ex.Message}");

			}

		}



public async Task InitializeWindowSizeAsync()
	{
		try
		{
			bool saveEnabled = await _localSettingsService.ReadSettingAsync<bool>(LocalSettingsService.IsSaveWindowSizeEnabledKey);
			if (saveEnabled)
			{
				double w = await _localSettingsService.ReadSettingAsync<double>(LocalSettingsService.SavedWindowWidthKey);
				double h = await _localSettingsService.ReadSettingAsync<double>(LocalSettingsService.SavedWindowHeightKey);
				if (w > 0 && h > 0)
				{
					Width = w;
					Height = h;
					CenterWindowOnScreen();
					return;
				}
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"加载窗口尺寸失败: {ex.Message}");
		}

		Width = DefaultWindowWidth;
		Height = DefaultWindowHeight;
		CenterWindowOnScreen();
	}

	private void CenterWindowOnScreen()
	{
		try
		{
			var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
			if (displayArea == null) return;
			var workArea = displayArea.WorkArea;
			var currentSize = AppWindow.Size;
			if (currentSize.Width <= 0 || currentSize.Height <= 0)
				currentSize = new SizeInt32((int)Math.Round(Width), (int)Math.Round(Height));

			var targetX = workArea.X + Math.Max(0, (workArea.Width - currentSize.Width) / 2);
			var targetY = workArea.Y + Math.Max(0, (workArea.Height - currentSize.Height) / 2);
			AppWindow.Move(new PointInt32(targetX, targetY));
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"居中窗口失败: {ex.Message}");
		}
	}

	private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
	{
	}

	private void OnColorValuesChanged(UISettings sender, object args)
	{
		_dispatcherQueue.TryEnqueue(UpdateBackgroundOverlayTheme);
	}

	#endregion

	#region 背景与视觉效果

	private async Task LoadGlobalBackgroundAsync()
	{
		try
		{
			bool isCustomEnabled = await _viewModel.LoadBackgroundEnabledAsync();
			string customPath = await _viewModel.LoadCustomBackgroundPathAsync();
			int serverValue = await _viewModel.LoadServerAsync();
			var server = (ServerType)serverValue;

			if (isCustomEnabled && !string.IsNullOrEmpty(customPath) && File.Exists(customPath))
			{
				var customResult = await _backgroundRenderer.GetCustomBackgroundAsync(customPath);
				await ApplyGlobalBackgroundAsync(customResult);
				return;
			}

			var result = await _backgroundRenderer.GetBackgroundAsync(server, false);
			await ApplyGlobalBackgroundAsync(result);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"加载全局背景失败: {ex.Message}");
			await ClearGlobalBackgroundAsync();
		}
	}

	private Task ApplyGlobalBackgroundAsync(BackgroundRenderResult? result)
	{
		return RunOnUIThreadAsync(() =>
		{
			if (result == null)
			{
				_ = ClearGlobalBackgroundAsync();
				return;
			}

			if (result.IsVideo)
			{
				_isVideoBackground = true;
				if (GlobalBackgroundImage != null)
					GlobalBackgroundImage.Visibility = Visibility.Collapsed;
				_globalBackgroundPlayer?.Pause();
				_globalBackgroundPlayer = new MediaPlayer
				{
					Source = result.VideoSource,
					IsMuted = true,
					IsLoopingEnabled = true,
					AutoPlay = true
				};
				if (GlobalBackgroundVideo != null)
					GlobalBackgroundVideo.SetMediaPlayer(_globalBackgroundPlayer);
				GlobalBackgroundVideo.Visibility = Visibility.Visible;
			}
			else
			{
				_isVideoBackground = false;
				_globalBackgroundPlayer?.Pause();
				if (GlobalBackgroundVideo != null)
					GlobalBackgroundVideo.Visibility = Visibility.Collapsed;

				if (GlobalBackgroundImage != null)
				{
					double targetOpacity = GlobalBackgroundImage.Opacity > 0 ? GlobalBackgroundImage.Opacity : 1.0;
					GlobalBackgroundImage.Opacity = 0;
					GlobalBackgroundImage.Source = result.ImageSource;
					GlobalBackgroundImage.Visibility = Visibility.Visible;

					var fadeIn = new DoubleAnimation
					{
						From = 0,
						To = targetOpacity,
						Duration = TimeSpan.FromMilliseconds(1000),
						EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
					};
					Storyboard.SetTarget(fadeIn, GlobalBackgroundImage);
					Storyboard.SetTargetProperty(fadeIn, "Opacity");
					var sb = new Storyboard();
					sb.Children.Add(fadeIn);
					sb.Begin();
				}
			}
			UpdateBackgroundOverlayTheme();
				_notificationService?.UpdateTheme();
		});
	}

	private async Task ClearGlobalBackgroundAsync()
	{
		await RunOnUIThreadAsync(() =>
		{
			if (GlobalBackgroundImage != null)
			{
				GlobalBackgroundImage.Source = null;
				GlobalBackgroundImage.Visibility = Visibility.Collapsed;
			}
			if (GlobalBackgroundVideo != null)
			{
				GlobalBackgroundVideo.Source = null;
				GlobalBackgroundVideo.Visibility = Visibility.Collapsed;
			}
			_globalBackgroundPlayer?.Pause();
			_globalBackgroundPlayer?.Dispose();
			_globalBackgroundPlayer = null;
		});
	}

	private ElementTheme GetCurrentElementTheme()
	{
		if (Content is FrameworkElement root)
		{
			var theme = root.ActualTheme;
			if (theme == ElementTheme.Default)
				theme = WinUIApplication.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
			return theme;
		}
		return ElementTheme.Default;
	}

	private void UpdateBackgroundOverlayTheme()
	{
		var currentTheme = GetCurrentElementTheme();
		var themeBgColor = currentTheme == ElementTheme.Dark
				? Color.FromArgb(255, 32, 32, 32)
				: Color.FromArgb(255, 243, 243, 243);


		if (_isAcrylicOverlayEnabled && !_isVideoBackground)
		{
			PageBackgroundOverlay.Background = new AcrylicBrush
			{
				TintColor = themeBgColor,
				TintOpacity = 0.6,
				FallbackColor = themeBgColor
			};
		}
		else
		{
			PageBackgroundOverlay.Background = new SolidColorBrush(themeBgColor);
		}

			}

	private void ApplyBackgroundImageOpacity(double value)
	{
		if (GlobalBackgroundImage != null)
			GlobalBackgroundImage.Opacity = Math.Clamp(value, 0.0, 1.0);
	}

	private Task RunOnUIThreadAsync(Action action)
	{
		var tcs = new TaskCompletionSource();
		var enqueued = _dispatcherQueue.TryEnqueue(() =>
		{
			try
			{
				action();
				tcs.SetResult();
			}
			catch (Exception ex)
			{
				tcs.SetException(ex);
			}
		});

		if (!enqueued)
		{
			tcs.SetException(new InvalidOperationException("DispatcherQueue 调度失败"));
		}

		return tcs.Task;
	}

	#endregion

	#region 导航与动画

	private async void NavigationView_Loaded(object sender, RoutedEventArgs e)
	{
		try
		{
			foreach (var item in NavigationView.MenuItems)
			{
				if (item is FrameworkElement uiItem) SetupSpringAnimation(uiItem);
			}
			foreach (var item in NavigationView.FooterMenuItems)
			{
				if (item is FrameworkElement uiItem) SetupSpringAnimation(uiItem);
			}
			// 1. 加载主题并应用到 UI
			ElementTheme theme = await _viewModel.LoadThemeAsync();
			if (Content is FrameworkElement root)
				root.RequestedTheme = theme;

			// 2. 并行加载其他设置
			double bgImageOpacity = await _viewModel.LoadBackgroundImageOpacityAsync();
			bool acrylic = await _viewModel.LoadAcrylicOverlayAsync();
			_minimizeToTray = await _viewModel.LoadMinimizeToTrayAsync();

			// 3. 应用透明度与开关
			ApplyBackgroundImageOpacity(bgImageOpacity);
			_isAcrylicOverlayEnabled = acrylic;

			// 4. 加载背景
			await LoadGlobalBackgroundAsync();

			// 5. 等待 UI 刷新后最终更新遮罩
			await Task.Delay(100);
			UpdateBackgroundOverlayTheme();
				_notificationService?.UpdateTheme();

			await InitializeWindowSizeAsync();
			ShowMainContent();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"NavigationView 加载失败: {ex.Message}");
			ShowMainContent();
		}
	}

	private void ShowMainContent()
	{
		NavigationView.Visibility = Visibility.Visible;
		NavigationView.SelectedItem = NavigationView.MenuItems[0];

		if (ContentFrame.CurrentSourcePageType != typeof(MainPage))
			ContentFrame.Navigate(typeof(MainPage));

		UpdatePageOverlayState(true);
		_isMainUiLoaded = true;
	}

	private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
	{
		if (args.SelectedItem is not NavigationViewItem selectedItem) return;
		var tag = selectedItem.Tag?.ToString();

		// 下载页面 — 走统一导航（有入场/退场动画）
			if (tag == "downloads")
			{
				ContentFrame.Navigate(typeof(DownloadsPage));
				UpdatePageOverlayState(false);
				return;
			}

			if (tag == SettingsViewModelTag && SettingsIconRotation != null)
		{
			var anim = new DoubleAnimation
			{
				From = 0,
				To = 360,
				Duration = new Duration(TimeSpan.FromSeconds(0.7)),
				EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
			};
			Storyboard.SetTarget(anim, SettingsIconRotation);
			Storyboard.SetTargetProperty(anim, "Angle");
			var sb = new Storyboard();
			sb.Children.Add(anim);
			sb.Begin();
		}

		if (!string.IsNullOrEmpty(tag))
			NavigateToPage(tag);
	}

	private async void NavigateToPage(string viewModelTag)
	{
		if (_isNavigating) return;
		_isNavigating = true;

		try
		{
			var pageType = viewModelTag switch
			{
				"ModManager.ViewModels.MainViewModel" => typeof(MainPage),
				"ModManager.ViewModels.ModManagerViewModel" => typeof(ModManagePage),
				"ModManager.ViewModels.CharacterViewModel" => typeof(CharacterPage),
				"ModManager.ViewModels.SettingsViewModel" => typeof(SettingsPage),
				_ => null
			};
			if (pageType == null) return;
			if (ContentFrame.CurrentSourcePageType == pageType) return;

			if (ContentFrame.Content is Page currentPage)
			{
				var exitStoryboard = currentPage.FindName("ExitStoryboard") as Storyboard;
				if (exitStoryboard != null)
				{
					exitStoryboard.Stop();
					exitStoryboard.Begin();
					var duration = exitStoryboard.Duration.HasTimeSpan
						? exitStoryboard.Duration.TimeSpan
						: TimeSpan.FromMilliseconds(300);
					await Task.Delay(duration);
				}
			}

			ContentFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
			UpdatePageOverlayState(pageType == typeof(MainPage));
		}
		catch (Exception ex)
		{
			Log.Error("导航失败", ex);
		}
		finally
		{
			_isNavigating = false;
		}
	}

		private void UpdatePageOverlayState(bool isMainPage)
		{
			try
			{
				if (isMainPage && _isOverlayShown)
				{
					// 返回主页：遮罩淡出，背景缩回
					var scaleXAnim = new DoubleAnimation
					{
						To = 1.0,
						Duration = TimeSpan.FromMilliseconds(350),
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
					};
					var scaleYAnim = new DoubleAnimation
					{
						To = 1.0,
						Duration = TimeSpan.FromMilliseconds(350),
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
					};
					var opacityAnim = new DoubleAnimation
					{
						To = 0.0,
						Duration = TimeSpan.FromMilliseconds(350),
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
					};
					var sb = new Storyboard();
					Storyboard.SetTarget(scaleXAnim, BackgroundScale);
					Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
					Storyboard.SetTarget(scaleYAnim, BackgroundScale);
					Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
					Storyboard.SetTarget(opacityAnim, PageBackgroundOverlay);
					Storyboard.SetTargetProperty(opacityAnim, "Opacity");
					sb.Children.Add(scaleXAnim);
					sb.Children.Add(scaleYAnim);
					sb.Children.Add(opacityAnim);
					sb.Begin();
					_isOverlayShown = false;
				}
				else if (!isMainPage && !_isOverlayShown)
				{
					// 离开主页：遮罩淡入，背景放大
					var scaleXAnim = new DoubleAnimation
					{
						To = 1.05,
						Duration = TimeSpan.FromMilliseconds(400),
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
					};
					var scaleYAnim = new DoubleAnimation
					{
						To = 1.05,
						Duration = TimeSpan.FromMilliseconds(400),
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
					};
					var opacityAnim = new DoubleAnimation
					{
						To = 1.0,
						Duration = TimeSpan.FromMilliseconds(400),
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
					};
					var sb = new Storyboard();
					Storyboard.SetTarget(scaleXAnim, BackgroundScale);
					Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
					Storyboard.SetTarget(scaleYAnim, BackgroundScale);
					Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
					Storyboard.SetTarget(opacityAnim, PageBackgroundOverlay);
					Storyboard.SetTargetProperty(opacityAnim, "Opacity");
					sb.Children.Add(scaleXAnim);
					sb.Children.Add(scaleYAnim);
					sb.Children.Add(opacityAnim);
					sb.Begin();
					_isOverlayShown = true;
				}
				else if (!isMainPage && _isOverlayShown)
				{
					// 非主页之间切换：保持遮罩状态
					BackgroundScale.ScaleX = 1.05;
					BackgroundScale.ScaleY = 1.05;
					PageBackgroundOverlay.Opacity = 1.0;
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[MainWindow] 遮罩动画异常: {ex.Message}");
			}
		}

	private void SetupSpringAnimation(FrameworkElement element)
	{
		var visual = ElementCompositionPreview.GetElementVisual(element);
		var compositor = visual.Compositor;

		element.SizeChanged += (_, e) =>
		{
			visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2f, (float)e.NewSize.Height / 2f, 0f);
		};

		element.PointerPressed += (_, _) =>
		{
			var anim = compositor.CreateSpringVector3Animation();
			anim.Target = "Scale";
			anim.FinalValue = new Vector3(0.92f, 0.92f, 1f);

			anim.Period = TimeSpan.FromMilliseconds(20);
			anim.DampingRatio = 0.6f;

			visual.StartAnimation("Scale", anim);
		};

		void ResetScale()
		{
			var anim = compositor.CreateSpringVector3Animation();
			anim.Target = "Scale";
			anim.FinalValue = new Vector3(1f, 1f, 1f);

			anim.Period = TimeSpan.FromMilliseconds(60);
			anim.DampingRatio = 0.5f;

			visual.StartAnimation("Scale", anim);
		}

		element.PointerReleased += (_, _) => ResetScale();
		element.PointerExited += (_, _) => ResetScale();
	}

	#endregion
}