using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModManager.Core.Contracts.Services;
using ModManager.Core.Helpers;
using ModManager.Core.Models;

namespace ModManager.Helpers
{
    public static class CharacterAssetDeployer
    {
        public static async Task<(int tags, int icons, int folders)> DeployAsync(string modsFolderPath, INotificationService? notif = null)
        {
            int tags = 0, icons = 0, folders = 0;
            try
            {
                var charRoot = Path.Combine(modsFolderPath, Core.Constants.FileNames.CharacterFolder);
                Debug.WriteLine($"[Deploy] modsPath={modsFolderPath}, charRoot={charRoot}");
                Directory.CreateDirectory(charRoot);

                var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
                var dbPath = Path.Combine(assetsDir, Core.Constants.FileNames.CharactersDatabase);
                var iconDir = Path.Combine(assetsDir, "CharacterIcons");
                Debug.WriteLine($"[Deploy] baseDir={AppContext.BaseDirectory}, assetsDir={assetsDir}");

                if (!File.Exists(dbPath))
                {
                    Log.Error($"[Deploy] 数据包缺失: {dbPath}");
                    notif?.Show("部署失败", $"角色数据库不存在:\n{dbPath}", NotificationType.Error);
                    return (0, 0, 0);
                }

                if (!Directory.Exists(iconDir))
                {
                    Log.Warn($"[Deploy] 头像目录不存在: {iconDir}");
                    Debug.WriteLine($"[Deploy] 头像目录不存在: {iconDir}");
                }

                List<CharacterDbEntry>? db;
                try
                {
                    var json = await File.ReadAllTextAsync(dbPath);
                    db = JsonSerializer.Deserialize<List<CharacterDbEntry>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    Debug.WriteLine($"[Deploy] 数据库加载: {db?.Count ?? 0} 个角色");
                }
                catch (Exception ex)
                {
                    Log.Error("[Deploy] 角色数据库解析失败", ex);
                    notif?.Show("部署失败", $"角色数据库损坏: {ex.Message}", NotificationType.Error);
                    return (0, 0, 0);
                }

                if (db == null || db.Count == 0)
                {
                    notif?.Show("部署失败", "角色数据库为空", NotificationType.Warning);
                    return (0, 0, 0);
                }

                foreach (var entry in db)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                    var charDir = Path.Combine(charRoot, entry.Name);

                    // 创建文件夹
                    if (!Directory.Exists(charDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(charDir);
                            folders++;
                            Debug.WriteLine($"[Deploy] 创建文件夹: {entry.Name}");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[Deploy] 创建文件夹失败: {charDir}", ex);
                            notif?.Show("部署失败", $"无法创建 {entry.Name} 文件夹: {ex.Message}", NotificationType.Error);
                            continue;
                        }
                    }

                    // 写入 info.json
                    var infoPath = Path.Combine(charDir, Core.Constants.FileNames.CharacterInfo);
                    if (!File.Exists(infoPath) && entry.Tags?.Count > 0)
                    {
                        try
                        {
                            var opts = new JsonSerializerOptions { WriteIndented = true,
                                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                            await File.WriteAllTextAsync(infoPath, JsonSerializer.Serialize(entry.Tags, opts));
                            tags++;
                        }
                        catch (Exception ex) { Log.Error($"[Deploy] 写入 info.json 失败: {entry.Name}", ex); }
                    }

                    // 复制头像
                    var iconPath = Path.Combine(charDir, "Icon.png");
                    if (!File.Exists(iconPath))
                    {
                        if (Directory.Exists(iconDir))
                        {
                            var src = Path.Combine(iconDir, $"{entry.Name}.png");
                            if (File.Exists(src))
                            {
                                try { File.Copy(src, iconPath); icons++; }
                                catch (Exception ex) { Log.Error($"[Deploy] 复制头像失败: {entry.Name}", ex); }
                            }
                        }
                    }
                }

                Debug.WriteLine($"[Deploy] 完成: folders={folders} tags={tags} icons={icons}");
                var msg = folders > 0 || tags > 0 || icons > 0
                    ? $"已创建 {folders} 个文件夹、{tags} 个标签、{icons} 个头像"
                    : "所有角色数据已就绪，无需部署";
                Log.Info($"[Deploy] {msg}");
                notif?.Show("角色数据部署完成", msg, NotificationType.Success);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Deploy] 异常: {ex}");
                Log.Error("[Deploy] 未预期错误", ex);
                notif?.Show("部署失败", ex.Message, NotificationType.Error);
            }
            return (tags, icons, folders);
        }

        private class CharacterDbEntry
        {
            public string Name { get; set; } = "";
            public Dictionary<string, string>? Tags { get; set; }
        }
    }
}
