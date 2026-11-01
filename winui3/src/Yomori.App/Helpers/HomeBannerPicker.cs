// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Yomori.Helpers;

/// <summary>
/// 首页图库的挑选逻辑：从一个文件夹里随机抽一张图显示。
///
/// <para>目录枚举结果只保留一个很小的有界快照：目录新增/删除/重命名文件时，
/// 目录的 <see cref="Directory.GetLastWriteTimeUtc(string)"/> 会变化，下次调用就重新枚举。
/// 文件内容是否变化不在这里判断；候选文件真正读取前还会核对长度与最后写入时间。</para>
///
/// <para>⚠️ 不要在这里引入任何 <c>Microsoft.UI.Xaml</c> 类型 ——
/// 该类由纯 .NET 的 HomeBannerPickerChecks 直接链接编译。</para>
/// </summary>
public static class HomeBannerPicker
{
    /// <summary>
    /// 允许出现在图库里的扩展名。与「更换图片…」的过滤器、以及
    /// <c>BitmapImage.SetSourceAsync</c> 实际能吃下的格式保持一致
    /// （webp 支持取决于系统 WIC 解码器，读不出来时页面会写清原因，不会静默空白）。
    /// </summary>
    public static readonly string[] SupportedExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
    };

    /// <summary>
    /// 目录快照最多留几个。生产只访问首页图库一处；留少量余量是为了以后
    /// 再加一个入口时不立刻退化成完全无缓存，而不是为了长期缓存大量目录。
    /// </summary>
    private const int CatalogCacheCapacity = 4;

    private static readonly object CatalogCacheGate = new();
    private static readonly Dictionary<string, CatalogCacheEntry> CatalogCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _catalogUseSequence;

    private sealed class CatalogCacheEntry
    {
        public CatalogCacheEntry(
            string directory,
            DateTime directoryLastWriteTimeUtc,
            IReadOnlyList<string> images,
            long lastUse)
        {
            Directory = directory;
            DirectoryLastWriteTimeUtc = directoryLastWriteTimeUtc;
            Images = images;
            LastUse = lastUse;
        }

        public string Directory { get; }
        public DateTime DirectoryLastWriteTimeUtc { get; }
        public IReadOnlyList<string> Images { get; }
        public long LastUse { get; set; }
    }

    /// <summary>该扩展名是否是图库认的图片。大小写不敏感（<c>.JPG</c> 也认）。</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0
               && SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 列出目录里所有图片，按文件名排序。
    ///
    /// <para>排序不是为了显示好看，是为了**确定性**：枚举顺序取决于文件系统，
    /// 不排序的话“抽中的是哪一张”连复现路径都没有。目录不存在/没权限一律返回空列表
    /// （页面对空的处理是显示占位与提示，不该因为一个坏目录把首页搞崩）。</para>
    ///
    /// <para>同一目录且目录时间戳未变时复用上次的快照。返回的是只读包装，
    /// 调用方不能修改缓存内容；快照本身也会在目录新增/删除文件后失效。</para>
    /// </summary>
    public static IReadOnlyList<string> EnumerateImages(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Array.Empty<string>();
        }

        string fullDirectory;
        DateTime directoryLastWriteTimeUtc;
        try
        {
            fullDirectory = Path.GetFullPath(directory);
            if (!Directory.Exists(fullDirectory))
            {
                RemoveCachedCatalog(fullDirectory);
                return Array.Empty<string>();
            }

            // 先取时间戳再枚举：如果枚举过程中目录刚好变化，这次快照会带着
            // 变化前的时间戳，下一次调用仍会因时间戳不一致而重新枚举。
            directoryLastWriteTimeUtc = Directory.GetLastWriteTimeUtc(fullDirectory);
        }
        catch (Exception)
        {
            // 目录被删/被占用/没权限：当作没有图处理（有占位提示兜底），不往上抛。
            return Array.Empty<string>();
        }

        lock (CatalogCacheGate)
        {
            if (CatalogCache.TryGetValue(fullDirectory, out var cached)
                && cached.DirectoryLastWriteTimeUtc == directoryLastWriteTimeUtc)
            {
                cached.LastUse = ++_catalogUseSequence;
                return cached.Images;
            }
        }

        var images = EnumerateImagesUncached(fullDirectory);
        if (images is null)
        {
            // 枚举失败不能被当成“这个目录一直是空的”缓存起来；
            // 否则一次临时占用/权限抖动会一直显示占位，直到目录时间戳再变化。
            RemoveCachedCatalog(fullDirectory);
            return Array.Empty<string>();
        }

        lock (CatalogCacheGate)
        {
            var entry = new CatalogCacheEntry(
                fullDirectory,
                directoryLastWriteTimeUtc,
                images,
                ++_catalogUseSequence);

            CatalogCache[fullDirectory] = entry;
            TrimCatalogCache();
            return entry.Images;
        }
    }

    /// <summary>
    /// 随机抽一张。
    ///
    /// <paramref name="avoidPath"/> 是上一次显示的那张：目录里还有别的图时把它排除掉，
    /// 否则三张图里总有一次「切回来还是同一张」，看起来像随机没生效。
    /// 只剩一张时照样返回它（宁可重复，也不能没图）。
    /// </summary>
    /// <param name="random">注入用（测试里给固定种子）；生产传 null 用 <see cref="Random.Shared"/>。</param>
    /// <returns>选中的绝对路径；目录里没有可用图片时返回 null。</returns>
    public static string? PickRandom(string? directory, string? avoidPath = null, Random? random = null)
    {
        var all = EnumerateImages(directory);
        if (all.Count == 0)
        {
            return null;
        }

        IReadOnlyList<string> pool = all;
        if (all.Count > 1 && !string.IsNullOrWhiteSpace(avoidPath))
        {
            var filtered = all
                .Where(p => !string.Equals(p, avoidPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (filtered.Length > 0)
            {
                pool = filtered;
            }
        }

        var rng = random ?? Random.Shared;
        return pool[rng.Next(pool.Count)];
    }

    private static IReadOnlyList<string>? EnumerateImagesUncached(string directory)
    {
        try
        {
            var images = Directory.EnumerateFiles(directory)
                .Where(IsSupported)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Array.AsReadOnly(images);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void RemoveCachedCatalog(string directory)
    {
        lock (CatalogCacheGate)
        {
            CatalogCache.Remove(directory);
        }
    }

    private static void TrimCatalogCache()
    {
        while (CatalogCache.Count > CatalogCacheCapacity)
        {
            var oldest = CatalogCache.Values
                .OrderBy(entry => entry.LastUse)
                .First();
            CatalogCache.Remove(oldest.Directory);
        }
    }
}
