using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ModManager.Application.Services
{
    public static class ParallelDownloader
    {
        /// <summary>下载进度共享状态（避免 async 方法的 ref 参数限制）</summary>
        private sealed class DownloadState
        {
            public long TotalDownloaded;
            public DateTime LastReport = DateTime.UtcNow;
            public long LastBytes;
            public readonly object ReportLock = new();
        }
        /// <summary>分片重试次数</summary>
        private const int MaxRetries = 3;
        /// <summary>最小分片大小 128KB，防止 HTTP 请求过多</summary>
        private const int MinChunkSize = 131072;
        /// <summary>每个线程对应的小段倍数（4x = 快线程自动多干）</summary>
        private const int ChunkMultiplier = 4;
        /// <summary>进度上报间隔</summary>
        private const double ReportIntervalSec = 0.5;

        /// <summary>
        /// 并行分段下载。采用工作窃取（work-stealing）策略：
        /// 创建 segments×4 个小段放入 ConcurrentQueue，
        /// segments 个 Worker 竞争出队，快线程自动领取更多段 → 天然负载均衡。
        ///
        /// 成功时返回临时文件路径，失败/取消时清理临时文件并抛出。
        /// </summary>
        /// <param name="segments">并行线程数，默认 8，允许范围 1-32</param>
        public static async Task<string> DownloadAsync(
            string url,
            Action<long, long, double>? onProgress,
            CancellationToken ct,
            int segments = 8)
        {
            string? tmpFile = null;
            try
            {
                // ================================================================
                // 1. 共享 HttpClient（连接池复用，避免每分片重复 TCP/TLS 握手）
                // ================================================================
                using var handler = new SocketsHttpHandler
                {
                    MaxConnectionsPerServer = Math.Max(segments, 8),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    EnableMultipleHttp2Connections = true
                };
                using var client = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                client.DefaultRequestHeaders.Add("User-Agent", "ModManager/1.0");
                // 禁用透明压缩，确保 Range 请求字节精确
                client.DefaultRequestHeaders.AcceptEncoding.Clear();
                client.DefaultRequestHeaders.AcceptEncoding.Add(
                    new System.Net.Http.Headers.StringWithQualityHeaderValue("identity"));

                // ================================================================
                // 2. HEAD 预检：文件大小、断点续传支持
                // ================================================================
                var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                var headResp = await client.SendAsync(headReq, ct);
                headResp.EnsureSuccessStatusCode();
                bool supportsRange = headResp.Headers.AcceptRanges?.Any(
                    r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase)) == true;
                long totalSize = headResp.Content.Headers.ContentLength ?? 0;
                headResp.Dispose();

                tmpFile = Path.GetTempFileName();

                // ================================================================
                // 3. 降级：不支持 Range / 文件 ≤ 1MB / 大小未知 → 单线程流式
                // ================================================================
                if (!supportsRange || totalSize <= 1024 * 1024 || totalSize == 0)
                {
                    using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    using var src = await resp.Content.ReadAsStreamAsync(ct);
                    using var dst = File.Create(tmpFile);
                    var buf = new byte[8192]; int read; long downloaded = 0;
                    var stLastReport = DateTime.UtcNow;
                    long stLastBytes = 0;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        downloaded += read;

                        var now = DateTime.UtcNow;
                        var elapsed = (now - stLastReport).TotalSeconds;
                        if (elapsed >= ReportIntervalSec)
                        {
                            var speed = (downloaded - stLastBytes) / elapsed;
                            onProgress?.Invoke(downloaded, totalSize, speed);
                            stLastBytes = downloaded;
                            stLastReport = now;
                        }
                    }
                    onProgress?.Invoke(downloaded, totalSize, 0);
                    return tmpFile;
                }

                // ================================================================
                // 4. 工作窃取并行下载
                // ================================================================

                // 4a. 预分配文件（SetLength 一次扩展，防止分片并发写入时的碎片化）
                using var fileHandle = File.OpenHandle(
                    tmpFile,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    FileOptions.Asynchronous);
                RandomAccess.SetLength(fileHandle, totalSize);

                // 4b. 构建小段队列（segments × 4，最小 128KB/段）
                int totalChunks = segments * ChunkMultiplier;
                long chunkSize = totalSize / totalChunks;
                if (chunkSize < MinChunkSize)
                {
                    totalChunks = Math.Max(1, (int)(totalSize / MinChunkSize));
                    chunkSize = totalSize / totalChunks;
                }

                var chunkQueue = new ConcurrentQueue<(long start, long end)>();
                for (int i = 0; i < totalChunks; i++)
                {
                    long start = i * chunkSize;
                    long end = (i == totalChunks - 1) ? totalSize - 1 : start + chunkSize - 1;
                    chunkQueue.Enqueue((start, end));
                }

                // 4c. 共享状态
                var state = new DownloadState();
                int failedChunks = 0;

                // 4d. 启动 segments 个工作线程，各自从队列窃取小段
                var workerTasks = new List<Task>();
                for (int w = 0; w < segments; w++)
                {
                    workerTasks.Add(Task.Run(async () =>
                    {
                        while (chunkQueue.TryDequeue(out var chunk))
                        {
                            try
                            {
                                await DownloadChunkAsync(
                                    client, url, chunk.start, chunk.end,
                                    fileHandle, ct,
                                    state, totalSize, onProgress);
                            }
                            catch (OperationCanceledException)
                            {
                                Interlocked.Increment(ref failedChunks);
                                return;
                            }
                            catch
                            {
                                Interlocked.Increment(ref failedChunks);
                            }
                        }
                    }, ct));
                }

                await Task.WhenAll(workerTasks);

                if (failedChunks > 0)
                    throw new IOException($"下载失败: {failedChunks} 个分片出错");

                // 最终 100% 上报
                onProgress?.Invoke(totalSize, totalSize, 0);
                return tmpFile;
            }
            catch
            {
                if (tmpFile != null)
                {
                    try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
                }
                throw;
            }
        }

        /// <summary>
        /// 下载单个分片，带指数退避重试。
        /// 失败后从原始位置重新下载，覆盖之前部分写入的数据。
        /// 使用 RandomAccess 直接 Seek+Write，无需全局锁。
        /// </summary>
        private static async Task DownloadChunkAsync(
            HttpClient client,
            string url,
            long start,
            long end,
            Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
            CancellationToken ct,
            DownloadState state,
            long totalSize,
            Action<long, long, double>? onProgress)
        {
            var buffer = new byte[8192];
            long chunkSize = end - start + 1;

            for (int retry = 0; retry <= MaxRetries; retry++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

                    using var resp = await client.SendAsync(
                        req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    long writePos = start;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        await RandomAccess.WriteAsync(fileHandle,
                            buffer.AsMemory(0, read), writePos, ct);
                        writePos += read;

                        // 累计总进度（无锁原子操作）
                        Interlocked.Add(ref state.TotalDownloaded, read);

                        // 节流上报
                        var now = DateTime.UtcNow;
                        bool report = false;
                        double speed = 0;
                        lock (state.ReportLock)
                        {
                            var elapsed = (now - state.LastReport).TotalSeconds;
                            if (elapsed >= ReportIntervalSec)
                            {
                                var currentTotal = Interlocked.Read(ref state.TotalDownloaded);
                                speed = (currentTotal - state.LastBytes) / elapsed;
                                state.LastBytes = currentTotal;
                                state.LastReport = now;
                                report = true;
                            }
                        }
                        if (report)
                            onProgress?.Invoke(
                                Interlocked.Read(ref state.TotalDownloaded),
                                totalSize, speed);
                    }

                    // 完整性校验
                    long actualWritten = writePos - start;
                    if (actualWritten < chunkSize)
                        throw new IOException(
                            $"分片不完整: 预期 {chunkSize}B, 实际 {actualWritten}B");

                    return; // 成功
                }
                catch (OperationCanceledException)
                {
                    throw; // 取消不重试
                }
                catch when (retry < MaxRetries && !ct.IsCancellationRequested)
                {
                    // 指数退避: 1s → 2s → 4s
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Pow(2, retry)), ct);
                }
            }

            throw new IOException(
                $"分片 [{start}-{end}] 重试 {MaxRetries} 次后仍失败");
        }
    }
}
