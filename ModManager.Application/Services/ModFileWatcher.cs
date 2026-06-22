using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;

namespace ModManager.Application.Services
{
	public class ModFileWatcher : IDisposable
	{
		private readonly string _modsFolder;
		private readonly DispatcherQueue _dispatcherQueue;
		private readonly Func<Task> _refreshAsync;
		private readonly int _ignoreDepth;
		private FileSystemWatcher? _watcher;
		private Timer? _debounceTimer;
		private bool _isRefreshing;
		private int _pauseCount;
		private readonly object _pauseLock = new object();

		public ModFileWatcher(string modsFolder, DispatcherQueue dispatcherQueue, Func<Task> refreshAsync, int ignoreDepth = 4)
		{
			_modsFolder = modsFolder;
			_dispatcherQueue = dispatcherQueue;
			_refreshAsync = refreshAsync;
			_ignoreDepth = ignoreDepth;
		}

		public async Task StartAsync()
		{
			if (!Directory.Exists(_modsFolder))
				Directory.CreateDirectory(_modsFolder);

			_watcher = new FileSystemWatcher
			{
				Path = _modsFolder,
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
				InternalBufferSize = 16384
			};

			_watcher.Created += OnFileSystemChanged;
			_watcher.Deleted += OnFileSystemChanged;
			_watcher.Renamed += OnFileSystemChanged;
			_watcher.Error += OnError;

			_watcher.EnableRaisingEvents = true;
			Debug.WriteLine("文件监控已启动");
			await Task.CompletedTask;
		}

		public void Stop()
		{
			lock (_pauseLock)
			{
				_pauseCount = 0; // 重置暂停计数
			}

			if (_watcher != null)
			{
				_watcher.EnableRaisingEvents = false;
				_watcher.Created -= OnFileSystemChanged;
				_watcher.Deleted -= OnFileSystemChanged;
				_watcher.Renamed -= OnFileSystemChanged;
				_watcher.Error -= OnError;
				_watcher.Dispose();
				_watcher = null;
			}

			_debounceTimer?.Dispose();
			_debounceTimer = null;
			Debug.WriteLine("文件监控已完全停止并释放");
		}

		private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
		{
			string fileName = Path.GetFileName(e.Name);
			if (fileName.StartsWith("~") || fileName.EndsWith(".tmp"))
				return;

			if (IsInsideModFolder(e.FullPath))
				return;

			// 防抖
			_debounceTimer?.Dispose();
			_debounceTimer = new Timer(async _ => await OnDebouncedRefresh(), null, 500, Timeout.Infinite);
		}

		private async Task OnDebouncedRefresh()
		{
			_dispatcherQueue.TryEnqueue(async () =>
			{
				if (_isRefreshing) return;
				_isRefreshing = true;
				try
				{
					Debug.WriteLine("检测到文件变化，刷新列表");
					await _refreshAsync();
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"刷新列表失败: {ex.Message}");
				}
				finally
				{
					_isRefreshing = false;
				}
			});
		}

		private void OnError(object sender, ErrorEventArgs e)
		{
			Debug.WriteLine($"文件监控出错: {e.GetException().Message}");
		}

		private bool IsInsideModFolder(string fullPath)
		{
			string modsRoot = _modsFolder.TrimEnd(Path.DirectorySeparatorChar);
			if (string.IsNullOrEmpty(modsRoot)) return false;

			// 防御：路径不以 modsRoot 开头（大小写差异、junction 等），直接返回 false
			if (!fullPath.StartsWith(modsRoot, StringComparison.OrdinalIgnoreCase))
				return false;

			string relativePath = fullPath.Substring(modsRoot.Length).TrimStart(Path.DirectorySeparatorChar);
			string[] parts = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
			return parts.Length >= _ignoreDepth;
		}

		public void Dispose()
		{
			Stop();
		}
		public void Pause()
		{
			lock (_pauseLock)
			{
				if (_pauseCount == 0)
				{
					if (_watcher != null)
						_watcher.EnableRaisingEvents = false;
					_debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
				}
				_pauseCount++;
			}
		}

		public void Resume()
		{
			lock (_pauseLock)
			{
				if (_pauseCount > 0)
				{
					_pauseCount--;
					if (_pauseCount == 0)
					{
						if (_watcher != null)
							_watcher.EnableRaisingEvents = true;
						// 注意：Resume 后不需要手动恢复防抖定时器，
						// 因为下一次文件变化会新建一个定时器
					}
				}
			}
		}


	}
}