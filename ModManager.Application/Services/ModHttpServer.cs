using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using ModManager.Core.Helpers;
using ModManager.Core.Enums;
using ModManager.Core.Models;
using ModManager.Core.Contracts.Services;

namespace ModManager.Application.Services
{
    public class ModHttpServer
    {
        public static readonly ObservableCollection<DownloadTask> Tasks = new();
        public static readonly List<DownloadTask> History = new();
        public static event Action<DownloadTask>? TaskUpdated;
        public static DispatcherQueue? GetUIDispatcher() => _uiDispatcher;

        /// <summary>确认弹窗回调：返回 (确认, 不解压, 最终文件名)。fileName 来自网页原始文件名</summary>
        public static Func<string, string, string, string, bool, Task<(bool confirmed, bool skipExtract, string finalName)>>? ConfirmDownloadAsync;
        private static DispatcherQueue? _uiDispatcher;

        /// <summary>在 UI 线程安全地调度操作。包装执行的 action，使其在 UI 线程上抛出的异常被捕获，避免冒泡至 WinRT 导致进程崩溃</summary>
        private static void EnqueueUi(Action action)
        {
            // 包装 action，确保在 UI 线程执行时内部捕获异常，避免抛出至 WinRT 导致进程崩溃
            void SafeAction()
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    try { Log.Warn($"[ModServer] UI action 异常: {ex}"); } catch { }
                }
            }

            try
            {
                if (_uiDispatcher != null)
                    _uiDispatcher.TryEnqueue(new DispatcherQueueHandler(SafeAction));
                else
                    SafeAction();
            }
            catch (Exception ex)
            {
                try { Log.Warn($"[ModServer] UI 调度失败: {ex.Message}"); } catch { }
                try { SafeAction(); } catch { }
            }
        }

        private static string _modsRoot = "";
        /// <summary>当用户修改 Mods 路径时调用，确保后续下载使用新路径</summary>
        public static void UpdateModsRoot(string newPath) => _modsRoot = newPath;
        private readonly Core.Contracts.Services.INotificationService? _notif;
        private readonly ILocalSettingsService? _settings;
        private readonly Dictionary<string, string> _enToCn = new(StringComparer.OrdinalIgnoreCase);
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        /// <summary>下载线程数默认值</summary>
        private const int DefaultDownloadSegments = 8;
        /// <summary>本地 HTTP 服务端口</summary>
        public const int ListenPort = 9556;

        public ModHttpServer(string modsRoot, Core.Contracts.Services.INotificationService? notif = null, DispatcherQueue? uiDispatcher = null, ILocalSettingsService? settings = null)
        {
            _modsRoot = modsRoot;
            _notif = notif;
            _settings = settings;
            _uiDispatcher = uiDispatcher;
            if (_uiDispatcher == null)
            {
                try { _uiDispatcher = DispatcherQueue.GetForCurrentThread(); } catch { }
            }
            LoadCharacterMap();
        }

        private void LoadCharacterMap()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", Core.Constants.FileNames.CharactersDatabase);
                if (!File.Exists(path)) return;
                var chars = JsonSerializer.Deserialize<List<CharEntry>>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (chars == null) return;
                foreach (var c in chars)
                    if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.EnglishName))
                        _enToCn[c.EnglishName] = c.Name;
                Log.Info($"[ModServer] 角色映射: {_enToCn.Count}");
            }
            catch { }
        }
        private class CharEntry { public string Name { get; set; } = ""; public string? EnglishName { get; set; } }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, ListenPort);
            _listener.Start();
            Log.Info($"[ModServer] 已启动 http://localhost:{ListenPort}/");
            _ = ListenLoopAsync(_cts.Token);
        }

        public void Stop() { _cts?.Cancel(); try { _listener?.Stop(); } catch { } _cts?.Dispose(); _listener = null; }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { var c = await _listener!.AcceptTcpClientAsync(ct); _ = HandleAsync(c); }
                catch when (ct.IsCancellationRequested) { break; } catch { }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                NetworkStream? s = null;
                try
                {
                    s = client.GetStream();
                    var r = new StreamReader(s, Encoding.UTF8);
                    var req = await r.ReadLineAsync();
                    if (string.IsNullOrEmpty(req)) return;
                    var parts = req.Split(' '); if (parts.Length < 2) return;
                    var method = parts[0]; var path = parts[1];
                    int cl = 0; string? line;
                    while (!string.IsNullOrEmpty(line = await r.ReadLineAsync()))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line.Split(':')[1].Trim(), out cl);
                    string? body = null;
                    if (cl > 0) { var buf = new char[cl]; int n = 0; while (n < cl) { int rn = await r.ReadAsync(buf, n, cl - n); if (rn == 0) break; n += rn; } body = new string(buf, 0, n); }
                    else if (method == "POST") body = await r.ReadToEndAsync();

                    if (method == "OPTIONS") await Write(s, 200, "{}");
                    else if (path == "/ping") await Write(s, 200, "{\"status\":\"ok\"}");
                    else if (path.StartsWith("/status") && method == "GET")
                        {
                            var q = path.IndexOf('?');
                            var qs = q >= 0 ? System.Web.HttpUtility.ParseQueryString(path[(q + 1)..]) : null;
                            var id = qs?["id"];
                            var task = id != null ? Tasks.FirstOrDefault(t => t.Id == id) : null;
                            if (task != null)
                                await Write(s, 200, $"{{\"id\":\"{task.Id}\",\"status\":\"{task.Status}\",\"progress\":{task.Progress:F2},\"title\":\"{task.Title}\"}}");
                            else
                                await Write(s, 200, $"{{\"error\":\"not found\"}}");
                        }
                    else if (path == "/install" && method == "POST" && body != null)
                        {
                            var taskId = Guid.NewGuid().ToString("N")[..8];
                            _ = InstallAsync(body, taskId);
                            await Write(s, 200, $"{{\"status\":\"ok\",\"id\":\"{taskId}\"}}");
                        }
                    else await Write(s, 404, $"{{\"error\":\"{path}\"}}");
                }
                catch (Exception ex) { Log.Warn($"[ModServer] 错误: {ex.Message}"); try { await Write(s!, 500, "{}"); } catch { } }
            }
        }

        private async Task InstallAsync(string body, string taskId)
        {
            DownloadTask? task = null;
            string? tmpFile = null;   // ParallelDownloader 创建的临时文件
            string? targetDir = null; // 目标目录（下载完成后才创建）
            try
            {
                // 1. 解析请求
                InstallRequest? req;
                if (body.TrimStart().StartsWith("{"))
                    req = JsonSerializer.Deserialize<InstallRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                else
                {
                    var dict = System.Web.HttpUtility.ParseQueryString(body);
                    req = new InstallRequest { Url = dict["url"], Character = dict["character"], ModName = dict["modName"], FileName = dict["fileName"] };
                }
                if (req == null || string.IsNullOrWhiteSpace(req.Url)) return;

                // 2. 解析 URL（只做一次，避免多次 new Uri）
                Uri uri;
                try { uri = new Uri(req.Url); }
                catch (UriFormatException) { Log.Warn($"[ModServer] URL 不合法: {req.Url}"); return; }

                var enChar = req.Character ?? "Unknown";
                var cnChar = _enToCn.TryGetValue(enChar, out var cn) ? cn : enChar;
                var modName = Sanitize(req.ModName ?? Path.GetFileNameWithoutExtension(uri.AbsolutePath));
                var folderName = Path.GetFileNameWithoutExtension(modName);
                var baseDir = Path.Combine(_modsRoot, Core.Constants.FileNames.CharacterFolder, cnChar, folderName);
                targetDir = baseDir; int suffix = 1;
                while (Directory.Exists(targetDir)) targetDir = baseDir + $" ({++suffix})";
                // 不在此处创建目录 — 等下载完成后再写入 mods 文件夹

                // 0. 弹出确认窗口
                bool skipExtract = false;
                string originalFileName = req.FileName ?? modName;
                // 从原始文件名提取扩展名
                string extFromFile = Path.GetExtension(originalFileName);
                if (string.IsNullOrEmpty(extFromFile)) extFromFile = ".zip";
                // modName 无扩展名时补上
                if (string.IsNullOrEmpty(Path.GetExtension(modName)))
                    modName += extFromFile;
                // 未识别角色 → 禁止自动解压
                bool hasCharacter = !string.IsNullOrWhiteSpace(cnChar) && cnChar != "Unknown";
                if (!hasCharacter) skipExtract = true;

                if (ConfirmDownloadAsync != null)
                {
                    var (confirmed, noExtract, finalName) = await ConfirmDownloadAsync(modName, cnChar, targetDir, originalFileName, hasCharacter);
                    if (!confirmed) return;
                    skipExtract = hasCharacter ? noExtract : true;
                    if (!string.IsNullOrWhiteSpace(finalName))
                    {
                        modName = finalName;
                        if (string.IsNullOrEmpty(Path.GetExtension(modName)))
                            modName += extFromFile;
                        // 更新目标目录（去掉扩展名）
                        var newFolderName = Path.GetFileNameWithoutExtension(modName);
                        var newBaseDir = Path.Combine(_modsRoot, Core.Constants.FileNames.CharacterFolder, cnChar, newFolderName);
                        targetDir = newBaseDir; int s = 1;
                        while (Directory.Exists(targetDir)) targetDir = newBaseDir + $" ({++s})";
                    }
                }

                var ext = ".zip";
                try { ext = Path.GetExtension(uri.AbsolutePath) ?? ".zip"; } catch { }

                var category = StatusGradientProvider.GetCategoryFromUrl(req.Url);
                task = new DownloadTask
                {
                    Id = taskId,
                    Url = req.Url,
                    Title = modName,
                    Character = cnChar,
                    Status = "start",
                    Category = category,
                    FileIcon = StatusGradientProvider.GetCategoryIcon(category),
                    DateAdded = DateTime.Now
                };
                task.RelativeTimeText = "刚刚";

                EnqueueUi(() =>
                {
                    Tasks.Insert(0, task);
                    TaskUpdated?.Invoke(task);
                });
                _notif?.Show($"⬇ 下载中: {modName}", $"{cnChar}/{Path.GetFileName(targetDir)}", NotificationType.Info);

                var cts = new CancellationTokenSource();
                EnqueueUi(() => task.Cts = cts);

                // 3. 下载到临时文件
                EnqueueUi(() => { task.Status = "downloading"; TaskUpdated?.Invoke(task); });

                var segments = await GetDownloadSegmentsAsync();
                tmpFile = await ParallelDownloader.DownloadAsync(req.Url, (downloaded, total, bps) =>
                {
                    EnqueueUi(() =>
                    {
                        task.TotalBytes = total;
                        task.DownloadedBytes = downloaded;
                        task.Progress = total > 0 ? (double)downloaded / total : 0;
                        task.Speed = FormatSpeed(bps);
                        TaskUpdated?.Invoke(task);
                    });
                }, cts.Token, segments);

                // 4. 下载完成 → 解压/移动 或 仅保存
                if (skipExtract)
                {
                    // 仅保存不解压：放到 mods 下载目录
                    var dlDir = Path.Combine(Path.GetDirectoryName(_modsRoot) ?? _modsRoot, "下载");
                    Directory.CreateDirectory(dlDir);
                    var destFile = Path.Combine(dlDir, modName);
                    File.Move(tmpFile, destFile, true);
                    tmpFile = null;
                    EnqueueUi(() => { task.Status = "done"; task.Progress = 1; TaskUpdated?.Invoke(task); });
                    History.Add(task);
                    _notif?.Show($"✅ 已下载: {modName}", $"保存到 下载/{Path.GetFileName(destFile)}", NotificationType.Success);
                    return;
                }

                EnqueueUi(() => { task.Status = "extracting"; task.Speed = ""; TaskUpdated?.Invoke(task); });

                Directory.CreateDirectory(targetDir);

                if (IsZipFile(tmpFile))
                {
                    try { ZipFile.ExtractToDirectory(tmpFile, targetDir, true); }
                    catch (Exception ex)
                    {
                        Log.Warn($"[ModServer] 解压失败: {ex.Message}");
                        // 复制原始文件作为 fallback，但标记警告
                        File.Copy(tmpFile, Path.Combine(targetDir, Path.GetFileName(uri.AbsolutePath)), true);
                        EnqueueUi(() =>
                        {
                            task.SetError($"解压失败，已保留原始文件: {ex.Message}");
                            task.Status = "done";  // fallback 成功
                            task.Progress = 1;
                            TaskUpdated?.Invoke(task);
                        });
                        History.Add(task);
                        _notif?.Show($"⚠ 已安装(未解压): {modName}", ex.Message, NotificationType.Warning);
                        return;
                    }
                }
                else
                    File.Move(tmpFile, Path.Combine(targetDir, Path.GetFileName(uri.AbsolutePath)), true);

                // 清理临时文件
                try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
                tmpFile = null; // 已清理

                EnqueueUi(() => { task.Status = "done"; task.Progress = 1; TaskUpdated?.Invoke(task); });
                History.Add(task);
                _notif?.Show($"✅ 已安装: {modName}", $"{cnChar}/{Path.GetFileName(targetDir)}", NotificationType.Success);
            }
            catch (OperationCanceledException)
            {
                if (task != null)
                {
                    EnqueueUi(() => { task.Status = "cancelled"; task.Speed = ""; TaskUpdated?.Invoke(task); });
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ModServer] 安装失败", ex);
                if (task != null)
                {
                    EnqueueUi(() => { task.Status = "error"; task.SetError(ex.Message); TaskUpdated?.Invoke(task); });
                }
                _notif?.Show("❌ 安装失败", ex.Message, NotificationType.Error);

                // 下载失败 → 清理可能已创建的目录
                if (targetDir != null)
                {
                    try { if (Directory.Exists(targetDir) && !Directory.EnumerateFileSystemEntries(targetDir).Any()) Directory.Delete(targetDir); } catch { }
                }
            }
            finally
            {
                // 确保异常路径下临时文件也被清理
                if (tmpFile != null)
                {
                    try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
                }
            }
        }

        public static void CancelTask(DownloadTask t)
        {
            t.Cts?.Cancel();
            EnqueueUi(() => { t.Status = "cancelled"; t.Speed = ""; TaskUpdated?.Invoke(t); });
        }

        public static void RemoveTask(DownloadTask t)
        {
            EnqueueUi(() => Tasks.Remove(t));
        }

        private static bool IsZipFile(string path)
        { try { using var s = File.OpenRead(path); var sig = new byte[4]; s.Read(sig, 0, 4); return sig[0] == 0x50 && sig[1] == 0x4B; } catch { return false; } }

        private async Task<int> GetDownloadSegmentsAsync()
        {
            try
            {
                if (_settings != null)
                {
                    await _settings.InitializeAsync();
                    var val = await _settings.ReadSettingAsync<int?>("DownloadThreads");
                    if (val is >= 1 and <= 32) return val.Value;
                }
            }
            catch { }
            return DefaultDownloadSegments;
        }

        private static string FormatSpeed(double bps) => bps switch
        {
            >= 1_048_576 => $"{bps / 1_048_576:F1} MB/s",
            >= 1024 => $"{bps / 1024:F0} KB/s",
            _ => $"{bps:F0} B/s"
        };

        private static async Task Write(NetworkStream s, int code, string body)
        {
            var h = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: POST,GET,OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.UTF8.GetBytes(h));
            await s.WriteAsync(Encoding.UTF8.GetBytes(body));
            await s.FlushAsync();
        }

        private static string Sanitize(string n) => Regex.Replace((n ?? "").Replace('\n', ' ').Replace('\r', ' '), @"\s+", " ").Trim();
        private class InstallRequest { public string? Url { get; set; } public string? Character { get; set; } public string? ModName { get; set; } public string? FileName { get; set; } }
    }
}
