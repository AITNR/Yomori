// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Yomori.Services;

/// <summary>
/// 更新安装包的清理。
///
/// <para>为什么不能在「点了安装」那一刻就删：安装器正拿着那个 exe 在跑
/// （本程序启动它、然后自己退出），文件被占用，删不掉也不该删 —— 那是安装器的可执行文件本体。
/// 所以走「先记账、下次启动再删」：安装前把路径写进清单，新版本第一次启动时把清单上的文件删掉。
/// 这一步同时覆盖了另一个意外：用户中途取消安装，包还在，下次启动照样会被清掉
/// （他要么已经升级了，要么再点一次更新就会重新下载）。</para>
///
/// <para>另外兜底扫一遍超期残留：装失败、杀进程、断网留下的 <c>*.part</c> 和旧安装包
/// 会一直堆在 <c>updates</c> 目录里，光靠清单清不到。</para>
///
/// <para>⚠️ 纯文件操作、全部吞异常：清理失败最坏就是多占几十 MB，绝不能让启动挂住。</para>
/// </summary>
public static class UpdatePackageCleanup
{
    /// <summary>待删清单的文件名（放在 <c>updates</c> 目录里，跟着它一起没）。</summary>
    public const string MarkerFileName = "pending-cleanup.json";

    /// <summary>超过这么多天的安装包 / 半成品一律当残留。留一个星期足够用户反悔重装。</summary>
    public const int StaleDays = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class MarkerFile
    {
        public List<string> Paths { get; set; } = new();
    }

    /// <summary>
    /// 记一笔「这个文件下次启动删掉」。已有清单时合并，不覆盖其他待删项。
    /// </summary>
    /// <param name="directory">清单所在目录（就是安装包目录）。</param>
    /// <param name="path">待删文件的完整路径。</param>
    public static void MarkPendingDeletion(string directory, string path)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var marker = ReadMarker(directory);

            if (!marker.Paths.Exists(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase)))
            {
                marker.Paths.Add(path);
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, MarkerFileName),
                JsonSerializer.Serialize(marker, JsonOptions),
                new UTF8Encoding(false));
        }
        catch
        {
            // 记不上就只是这一次没清掉 —— 比"为此拦住安装流程"划算得多
        }
    }

    /// <summary>
    /// 清掉清单上记着的安装包，返回（删掉几个，还剩几个删不掉）。
    ///
    /// 删不掉的（正在被别的进程占用）继续留在清单里等下次；清单本身在清空后删除。
    /// </summary>
    public static (int Deleted, int Remaining) RunPendingCleanup(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return (0, 0);
        }

        var markerPath = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return (0, 0);
        }

        var marker = ReadMarker(directory);
        var remaining = new List<string>();
        var deleted = 0;

        foreach (var path in marker.Paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (!File.Exists(path))
                {
                    // 已经不在了（用户自己删了 / 上次删成功但清单没写回去）—— 不算失败
                    deleted++;
                    continue;
                }

                File.Delete(path);
                deleted++;
            }
            catch
            {
                remaining.Add(path);
            }
        }

        try
        {
            if (remaining.Count == 0)
            {
                File.Delete(markerPath);
            }
            else
            {
                File.WriteAllText(
                    markerPath,
                    JsonSerializer.Serialize(new MarkerFile { Paths = remaining }, JsonOptions),
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 清单写不回去也不影响这次已经删掉的那些
        }

        return (deleted, remaining.Count);
    }

    /// <summary>
    /// 扫一遍目录，删掉超期的安装包与半成品（<c>*-setup.exe</c> / <c>*.zip</c> / <c>*.part</c>）。
    /// 只处理这一层的文件，不递归、不碰别的类型。返回删掉几个。
    /// </summary>
    /// <param name="directory">安装包目录。</param>
    /// <param name="days">多少天没动过算残留。</param>
    /// <param name="now">当前时间（注入是为了让冒烟能验边界，不传就是 <see cref="DateTime.UtcNow"/>）。</param>
    public static int SweepStale(string directory, int days = StaleDays, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || days < 0 || !Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = (now ?? DateTime.UtcNow).AddDays(-days);
        var deleted = 0;

        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch
        {
            return 0;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase) || !LooksLikeUpdateFile(name))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff)
                {
                    continue;
                }

                File.Delete(file);
                deleted++;
            }
            catch
            {
                // 占用 / 只读：留给下一次
            }
        }

        return deleted;
    }

    /// <summary>只看「更新包该长什么样」：安装包、绿色包、下载中的半成品。</summary>
    private static bool LooksLikeUpdateFile(string name) =>
        name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);

    private static MarkerFile ReadMarker(string directory)
    {
        try
        {
            var markerPath = Path.Combine(directory, MarkerFileName);
            if (!File.Exists(markerPath))
            {
                return new MarkerFile();
            }

            var marker = JsonSerializer.Deserialize<MarkerFile>(
                File.ReadAllText(markerPath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return marker?.Paths is { } paths ? new MarkerFile { Paths = paths } : new MarkerFile();
        }
        catch
        {
            // 清单坏了就当没有：总比因为一行坏 JSON 拒不清理由好
            return new MarkerFile();
        }
    }
}
