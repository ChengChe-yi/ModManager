using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ModManager.Application.Extensions;
using ModManager.Application.Services;
using ModManager.Application.Services.Background;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Models;
using ModManager.Services;
using ModManager.Services.Background;
using ModManager.Views;
using WinRT.Interop;

using AppInstance = Microsoft.Windows.AppLifecycle.AppInstance;
using AppUnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;
using DomainUnhandledExceptionEventArgs = System.UnhandledExceptionEventArgs;

namespace ModManager
{
	public partial class App : Microsoft.UI.Xaml.Application
	{
		private IHost? _host;
		// 重命名自定义 Host 属性，避免与 Microsoft.Extensions.Hosting.Host 冲突

		private static IHost? AppHost => ((App)Current)?._host;
		public static XamlRoot? MainXamlRoot { get; private set; }
		private MainWindow? _mainWindow;
		private ModManager.Application.Services.ModHttpServer? _httpServer;
		public static MainWindow? MainWindow => Current is App app ? app._mainWindow : null;

		public App()
		{
			InitializeComponent();

			BuildHost();

			AppInstance.GetCurrent().Activated += App_Activated;

			// 注册全局未处理异常
			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
			TaskScheduler.UnobservedTaskException += OnTaskUnobservedException;
			Current.UnhandledException += OnAppUnhandledException;
		}

		private void BuildHost()
		{
			try
			{
				_host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
					.UseContentRoot(AppContext.BaseDirectory)
					.ConfigureServices((context, services) =>
					{
						services.AddSingleton<IHoyoverseBackgroundService, HoyoverseBackgroundService>();
						services.AddSingleton<IBackgroundRenderer, BackgroundRenderer>();
						services.AddSingleton<IDialogService, DialogService>();
						services.AddSingleton<INotificationService, NotificationService>();
						services.AddSingleton<ModManager.Core.Contracts.Services.IAppLogger, ModManager.Application.Services.AppLogger>();
						services.AddApplicationServices();
					})
					.Build();
				// 尽早初始化日志，确保 Host 构建完成后即可记录
				ModManager.Core.Helpers.Log.Init(_host.Services.GetRequiredService<ModManager.Core.Contracts.Services.IAppLogger>());
				ModManager.Core.Helpers.Log.Info("Host 构建完成");
			}
			catch (Exception ex)
			{
				FatalError("Host 构建失败", ex);
			}
		}

		protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
		{
			base.OnLaunched(args);
			try
			{
				ModManager.Core.Helpers.Log.Info("App 启动");
				await _host!.StartAsync();

				await SetDefaultThemeAsync();
				var localSettingsService = _host.Services.GetRequiredService<ILocalSettingsService>();
				var backgroundRenderer = _host.Services.GetRequiredService<IBackgroundRenderer>();
				_mainWindow = new MainWindow(localSettingsService, backgroundRenderer);
				await _mainWindow.InitializeWindowSizeAsync();
				_mainWindow.Activate();
				MainXamlRoot = _mainWindow.Content?.XamlRoot;
			// 启动本地 HTTP 服务（供浏览器扩展调用）
			try
			{
				var pm = _host.Services.GetRequiredService<IPathManager>();
				await pm.InitializeAsync();
				var notif = _host.Services.GetRequiredService<INotificationService>();
				_httpServer = new ModManager.Application.Services.ModHttpServer(pm.ModsFolderPath, notif, settings: localSettingsService);
				ModManager.Application.Services.ModHttpServer.ConfirmDownloadAsync = async (modName, character, targetDir, fileName, hasCharacter) =>
					await ModManager.Views.DownloadConfirmWindow.ShowAsync(modName, character, targetDir, fileName, hasCharacter);
				_httpServer.Start();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[App] HTTP服务启动失败: {ex.Message}");
			}
			}
			catch (Exception ex)
			{
				FatalError("应用启动失败", ex);
			}
		}

		/// <summary>致命错误：写日志 + 显示系统对话框 + 退出</summary>
		private static void FatalError(string msg, Exception ex)
		{
			try
			{
				// 兜底文件日志（即使 Log.Init 未执行）
				var fallbackLog = Path.Combine(AppContext.BaseDirectory, "Logs", Core.Constants.FileNames.CrashLog);
				Directory.CreateDirectory(Path.GetDirectoryName(fallbackLog)!);
				File.AppendAllText(fallbackLog,
					$"[FATAL] {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}");
			}
			catch { }
			try
			{
				ModManager.Core.Helpers.Log.Error(msg, ex);
			}
			catch { }
			Debug.WriteLine($"{msg}: {ex}");
			Environment.Exit(-1);
		}

		private async Task SetDefaultThemeAsync()
		{
			try
			{
				var localSettings = GetService<ILocalSettingsService>();
				if (localSettings == null) return;

				var isThemeInitialized = await localSettings.ReadSettingAsync(LocalSettingsService.IsThemeInitializedKey);
				if (isThemeInitialized == null)
				{
					Debug.WriteLine("Initializing default theme to Dark.");
					var themeService = GetService<IThemeSelectorService>();
					if (themeService != null)
					{
						await themeService.SetThemeAsync(ElementTheme.Dark);
						await localSettings.SaveSettingAsync(LocalSettingsService.IsThemeInitializedKey, true);
					}
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Failed to set default theme: {ex.Message}");
			}
		}

		public static T? GetService<T>() where T : class
		{
			try
			{
				return AppHost?.Services.GetService<T>();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"GetService<{typeof(T)}> 失败: {ex.Message}");
				return null;
			}
		}

		#region 窗口前置逻辑

		private void App_Activated(object sender, AppActivationArguments args)
		{
			Debug.WriteLine("App_Activated 被触发");
			var dispatcherQueue = MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
			dispatcherQueue?.TryEnqueue(BringWindowToFront);
		}

		private void BringWindowToFront()
		{
			if (MainWindow == null) return;
			try
			{
				var hwnd = WindowNative.GetWindowHandle(MainWindow);
				ShowWindow(hwnd, SW_RESTORE);
				SetForegroundWindow(hwnd);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"前置窗口失败: {ex}");
			}
		}

		[DllImport("user32.dll")]
		private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

		[DllImport("user32.dll")]
		private static extern bool SetForegroundWindow(IntPtr hWnd);

		private const int SW_RESTORE = 9;

		#endregion

		#region 异常处理

		private async Task ShowCrashDialogAsync(XamlRoot xamlRoot, string source, Exception? ex)
		{
			if (ex == null) return;

			var message = $"程序遇到了一个错误\n\n" +
						  $"错误来源: {source}\n" +
						  $"错误信息: {ex.Message}\n\n" +
						  $"堆栈信息:\n{ex.StackTrace}";

			var dialogService = GetService<IDialogService>();
			if (dialogService != null)
			{
				await dialogService.ShowInfoAsync(xamlRoot, "发生了异常", message, "确定");
			}
			else
			{
				Debug.WriteLine($"严重错误: {message}");
			}
		}

		private void OnUnhandledException(object sender, DomainUnhandledExceptionEventArgs e)
		{
			var ex = e.ExceptionObject as Exception;
			FallbackCrashLog($"AppDomain未处理异常: {ex?.Message}", ex);
			Debug.WriteLine($"致命错误: {ex?.Message}");
		}

		private void OnTaskUnobservedException(object? sender, UnobservedTaskExceptionEventArgs e)
		{
			FallbackCrashLog($"Task未观察异常: {e.Exception.Message}", e.Exception);
			Debug.WriteLine($"Task异常: {e.Exception.Message}");
			e.SetObserved();
		}

		private void OnAppUnhandledException(object sender, AppUnhandledExceptionEventArgs e)
		{
			FallbackCrashLog($"UI未处理异常: {e.Exception.Message}", e.Exception);
			Debug.WriteLine($"UI异常: {e.Exception.Message}");
			e.Handled = true;
		}

		private static void FallbackCrashLog(string msg, Exception? ex)
		{
			try
			{
				var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
				Directory.CreateDirectory(dir);
				var log = $"[FATAL] {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
				if (ex != null) log += $"{Environment.NewLine}{ex}";
				File.AppendAllText(Path.Combine(dir, Core.Constants.FileNames.CrashLog), log + Environment.NewLine);
			}
			catch { }
		}

		#endregion
	}
}