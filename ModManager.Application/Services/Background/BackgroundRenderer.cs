using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ModManager.Core.Contracts.Services;   // 引入 IBackgroundRenderer 接口
using ModManager.Core.Models;               // 引入 BackgroundRenderResult
using ModManager.Services.Background;
using Windows.Media.Core;
using Windows.Storage;

namespace ModManager.Application.Services.Background
{
	// 不再定义 BackgroundRenderResult，使用 Core.Models 中的

	// 不再定义 IBackgroundRenderer，使用 Core.Contracts.Services 中的

	public class BackgroundRenderer : IBackgroundRenderer
	{
		private static readonly HttpClient _httpClient;
		private readonly string _cacheFolderPath;
		private BackgroundRenderResult? _cachedBackground;
		private string? _currentBackgroundUrl;
		private BackgroundRenderResult? _cachedCustomBackground;
		private string? _customBackgroundPath;

		private readonly IHoyoverseBackgroundService _hoyoverseService;

		// 依赖注入：IHoyoverseBackgroundService 由外部提供
		public BackgroundRenderer(IHoyoverseBackgroundService hoyoverseService)
		{
			_hoyoverseService = hoyoverseService;
			var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			_cacheFolderPath = Path.Combine(localAppData, "FufuLauncher", "BackgroundCache");
			try
			{
				Directory.CreateDirectory(_cacheFolderPath);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"BackgroundRenderer: 创建缓存目录失败 - {ex.Message}");
			}
		}

		static BackgroundRenderer()
		{
			_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
			_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36");
		}

		public async Task<BackgroundRenderResult?> GetBackgroundAsync(ServerType server, bool preferVideo)
		{
			try
			{
				var backgroundInfo = await _hoyoverseService.GetBackgroundUrlAsync(server, preferVideo);
				Debug.WriteLine($"BackgroundRenderer: 获取到 URL = {backgroundInfo?.Url ?? "null"}, IsVideo = {backgroundInfo?.IsVideo ?? false}");

				if (backgroundInfo == null || string.IsNullOrEmpty(backgroundInfo.Url))
				{
					Debug.WriteLine("BackgroundRenderer: 无法获取在线背景，触发回退机制");
					return GetFallbackBackground();
				}

				if (backgroundInfo.Url == _currentBackgroundUrl && _cachedBackground != null)
				{
					Debug.WriteLine("BackgroundRenderer: 使用缓存媒体");
					return _cachedBackground;
				}

				if (backgroundInfo.IsVideo)
				{
					Debug.WriteLine("BackgroundRenderer: 处理视频背景");
					var videoSource = await ProcessVideoBackground(backgroundInfo.Url);
					_cachedBackground = new BackgroundRenderResult { VideoSource = videoSource, IsVideo = true };
				}
				else
				{
					Debug.WriteLine("BackgroundRenderer: 处理静态背景");
					var imageSource = await ProcessImageBackground(backgroundInfo.Url);
					if (imageSource == null)
					{
						Debug.WriteLine("BackgroundRenderer: 图片处理返回 null，触发回退机制");
						return GetFallbackBackground();
					}
					_cachedBackground = new BackgroundRenderResult { ImageSource = imageSource, IsVideo = false };
				}

				_currentBackgroundUrl = backgroundInfo.Url;
				return _cachedBackground;
			}
			catch (Exception ex)
			{
				ModManager.Core.Helpers.Log.Error("加载在线背景失败", ex);
				return GetFallbackBackground();
			}
		}

		public async Task<BackgroundRenderResult?> GetSpecificOnlineBackgroundAsync(string url, bool isVideo)
		{
			try
			{
				if (isVideo)
				{
					var videoSource = await ProcessVideoBackground(url);
					return new BackgroundRenderResult { VideoSource = videoSource, IsVideo = true };
				}
				else
				{
					var imageSource = await ProcessImageBackground(url);
					if (imageSource == null)
					{
						Debug.WriteLine($"指定背景图片解码失败，触发回退");
						return GetFallbackBackground();
					}
					return new BackgroundRenderResult { ImageSource = imageSource, IsVideo = false };
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"指定背景加载失败: {ex.Message}");
				return GetFallbackBackground();
			}
		}

		public async Task<BackgroundRenderResult?> GetCustomBackgroundAsync(string filePath)
		{
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
				return null;

			if (_cachedCustomBackground != null && filePath == _customBackgroundPath)
				return _cachedCustomBackground;

			try
			{
				var extension = Path.GetExtension(filePath).ToLowerInvariant();
				var videoExtensions = new[] { ".mp4", ".webm", ".mkv", ".avi", ".mov" };
				var isVideo = videoExtensions.Contains(extension);

				BackgroundRenderResult result;
				if (isVideo)
				{
					var file = await StorageFile.GetFileFromPathAsync(filePath);
					result = new BackgroundRenderResult
					{
						VideoSource = MediaSource.CreateFromStorageFile(file),
						IsVideo = true
					};
				}
				else
				{
					var bitmap = new BitmapImage();
					using (var stream = File.OpenRead(filePath))
					{
						await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
					}
					result = new BackgroundRenderResult
					{
						ImageSource = bitmap,
						IsVideo = false
					};
				}

				_cachedCustomBackground = result;
				_customBackgroundPath = filePath;
				return result;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"自定义背景加载失败: {ex.Message}");
				return null;
			}
		}

		private BackgroundRenderResult? GetFallbackBackground()
		{
			try
			{
				Debug.WriteLine("BackgroundRenderer: 正在加载回退背景 Assets/bg.png");
				var bgPath = Path.Combine(AppContext.BaseDirectory, "Assets", "bg.png");
				var bitmap = new BitmapImage(new Uri(bgPath));
				return new BackgroundRenderResult { ImageSource = bitmap, IsVideo = false };
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"BackgroundRenderer: 回退背景失败 - {ex.Message}");
				return null;
			}
		}

		private async Task<MediaSource> ProcessVideoBackground(string videoUrl)
		{
			var fileName = GetCacheFileName(videoUrl);
			var cachedFilePath = Path.Combine(_cacheFolderPath, fileName);

			if (File.Exists(cachedFilePath))
			{
				var fileInfo = new FileInfo(cachedFilePath);
				if (fileInfo.Length > 1024)
				{
					try
					{
						var file = await StorageFile.GetFileFromPathAsync(cachedFilePath);
						return MediaSource.CreateFromStorageFile(file);
					}
					catch
					{
						File.Delete(cachedFilePath);
						Debug.WriteLine($"BackgroundRenderer: 缓存损坏，已删除 {fileName}");
					}
				}
			}

			Debug.WriteLine($"BackgroundRenderer: 开始下载视频: {videoUrl}");
			var data = await _httpClient.GetByteArrayAsync(videoUrl);
			Debug.WriteLine($"BackgroundRenderer: 下载完成，大小 {data.Length} bytes");

			var tempFile = Path.Combine(_cacheFolderPath, $"{fileName}.tmp");
			await File.WriteAllBytesAsync(tempFile, data);
			File.Move(tempFile, cachedFilePath, true);

			var storageFile = await StorageFile.GetFileFromPathAsync(cachedFilePath);
			return MediaSource.CreateFromStorageFile(storageFile);
		}

		private async Task<ImageSource?> ProcessImageBackground(string imageUrl)
		{
			Debug.WriteLine($"BackgroundRenderer: 开始下载图片: {imageUrl}");
			var data = await _httpClient.GetByteArrayAsync(imageUrl);
			Debug.WriteLine($"BackgroundRenderer: 下载完成，大小 {data.Length} bytes");

			var bitmap = new BitmapImage();
			using (var stream = new MemoryStream(data))
			{
				try
				{
					await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"BackgroundRenderer: 图片解码失败(可能缺少 WebP 扩展): {ex.Message}");
					return null;
				}
			}

			Debug.WriteLine("BackgroundRenderer: BitmapImage 从流加载完成");
			return bitmap;
		}

		private string GetCacheFileName(string url)
		{
			var extension = ".mp4";
			try
			{
				var uri = new Uri(url);
				var ext = Path.GetExtension(uri.AbsolutePath);
				if (!string.IsNullOrEmpty(ext))
					extension = ext;
			}
			catch { /* ignore */ }

			using var md5 = MD5.Create();
			var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(url));
			return BitConverter.ToString(hash).Replace("-", "").ToLower() + extension;
		}

		public void ClearBackground()
		{
			Debug.WriteLine("BackgroundRenderer: 清除背景缓存");
			if (Directory.Exists(_cacheFolderPath))
			{
				try
				{
					foreach (var file in Directory.GetFiles(_cacheFolderPath))
						File.Delete(file);
				}
				catch { /* ignored */ }
			}
			_cachedBackground = null;
			_currentBackgroundUrl = null;
		}

		public void ClearCustomBackground()
		{
			Debug.WriteLine("BackgroundRenderer: 清除自定义背景缓存");
			_customBackgroundPath = null;
			_cachedCustomBackground = null;
		}
	}
}