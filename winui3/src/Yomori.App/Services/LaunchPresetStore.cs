// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>
/// 启动方式预设库：读嵌入资源 <c>launch_presets.json</c>，并按脚本路径生成目录画像。
///
/// <para>为什么嵌入而不是外部文件：与 <see cref="ThirdPartyNotices"/> 同理 ——
/// 开发期 <see cref="AppPaths.BaseDir"/> 会回溯到仓库根，外部文件在开发机上读不到；
/// 而预设是编译期常量级的信息，跟 exe 走才不会出现"程序换了、参数还是旧的"。</para>
///
/// <para>读盘只发生在这一层（<see cref="BuildProfile"/>），识别与合并全在
/// <see cref="LaunchPresetResolver"/> 的纯函数里，方便冒烟直接断言。</para>
/// </summary>
public static class LaunchPresetStore
{
    /// <summary>csproj 里给这份嵌入资源定死的名字（改一处必须两处一起改）。</summary>
    private const string ResourceName = "Yomori.Presets.launch_presets.json";

    private static readonly Lazy<LaunchPresetLibrary> Cached = new(Load);

    /// <summary>预设库（读失败时是一份空库，界面退化为"只能手填"，不抛异常）。</summary>
    public static LaunchPresetLibrary Library => Cached.Value;

    /// <summary>识别结果 + 目录画像的会话内缓存：同一路径不重复读盘。</summary>
    private static readonly Dictionary<string, (LaunchResolution Resolution, LauncherProfile Profile)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>清缓存（测试用；正常运行不需要）。</summary>
    public static void InvalidateCache() => Cache.Clear();

    /// <summary>
    /// 识别一个脚本：先查缓存，未命中则构建画像再交给纯判定层。
    /// 任何 IO 异常都在这里吃掉 —— 选个脚本不该因为目录不可读而报错。
    /// </summary>
    public static (LaunchResolution Resolution, LauncherProfile Profile) Resolve(string? scriptPath)
    {
        var path = (scriptPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
        {
            var empty = BuildProfile(string.Empty);
            return (LaunchPresetResolver.Resolve(Library, path, empty), empty);
        }

        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var profile = BuildProfile(path);
        var resolution = LaunchPresetResolver.Resolve(Library, path, profile);
        Cache[path] = (resolution, profile);
        return (resolution, profile);
    }

    /// <summary>
    /// 读目录画像：<c>interface.json</c> 只取三个字段，
    /// 外加 <c>config/maa_pi_config.json</c> 与日志目录的存在性。
    /// 全部是毫秒级的小操作，且失败即降级，不抛。
    /// </summary>
    public static LauncherProfile BuildProfile(string? scriptPath)
    {
        var profile = new LauncherProfile();
        var directory = DirectoryOf(scriptPath);
        if (directory.Length == 0)
        {
            return profile;
        }

        profile.ExeDirectory = directory;

        try
        {
            // interface.json 一般在 exe 同目录；少数项目把 exe 放在 bin/ 下，所以再看一层父目录。
            foreach (var candidate in new[] { directory, Path.GetDirectoryName(directory) ?? string.Empty })
            {
                if (candidate.Length == 0)
                {
                    continue;
                }

                var jsonPath = Path.Combine(candidate, "interface.json");
                if (!File.Exists(jsonPath))
                {
                    continue;
                }

                profile.HasInterfaceJson = true;
                ReadInterfaceJson(jsonPath, profile);
                break;
            }

            profile.HasPiConfig = File.Exists(Path.Combine(directory, "config", "maa_pi_config.json"));
            if (profile.HasPiConfig)
            {
                profile.PresentRelativeFiles.Add("config/maa_pi_config.json");
            }

            // 日志目录候选：命中即记绝对路径，一个都没有就留空（不启用外部日志）
            foreach (var name in new[] { "debug", "logs", "log" })
            {
                var logDir = Path.Combine(directory, name);
                if (Directory.Exists(logDir))
                {
                    profile.LogDirectories.Add(logDir);
                }
            }

            // 预设里出现过的所有相对目录一次探完，交给判定层按各自顺序挑 ——
            // 判定层不能读盘，否则 Models 就进不了冒烟工程。
            foreach (var name in CandidateLogDirs(Library))
            {
                var full = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(full))
                {
                    profile.ExistingRelativeDirs.Add(LauncherProfile.NormalizeRelative(name));
                }
            }
        }
        catch (Exception)
        {
            // 目录不可读 / JSON 损坏 / 路径非法：都退化成"什么都没探测到"，让用户手填。
        }

        return profile;
    }

    /// <summary>
    /// 预设里出现过的全部日志目录（去重、保留原写法）。
    ///
    /// <para>为什么要先收集再一次性探测：判定层（Models）不能读盘，只能查
    /// <see cref="LauncherProfile.ExistingRelativeDirs"/>；而每个项目想看的目录不同
    /// （MFAAvalonia 先 logs 后 debug、三月七只要 logs），所以存在性必须按"全集"预先探好，
    /// 由判定层按各自顺序挑。</para>
    /// </summary>
    private static HashSet<string> CandidateLogDirs(LaunchPresetLibrary library)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(LaunchLogPreset? preset)
        {
            if (preset is null)
            {
                return;
            }

            foreach (var dir in preset.Dirs)
            {
                var text = (dir ?? string.Empty).Trim();
                if (text.Length > 0)
                {
                    result.Add(text);
                }
            }
        }

        foreach (var frontend in library.Frontends)
        {
            Add(frontend.Log);
        }

        foreach (var project in library.Projects)
        {
            Add(project.Log);
        }

        foreach (var entry in library.LogsByEntry)
        {
            Add(entry.Log);
        }

        return result;
    }

    private static void ReadInterfaceJson(string jsonPath, LauncherProfile profile)    {
        try
        {
            var info = new FileInfo(jsonPath);
            if (info.Length > 256 * 1024)
            {
                return;     // 不对劲就跳过：正常 interface.json 只有几 KB
            }

            using var stream = File.OpenRead(jsonPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("mirrorchyan_rid", out var rid) && rid.ValueKind == JsonValueKind.String)
            {
                profile.MirrorchyanRid = (rid.GetString() ?? string.Empty).Trim();
            }

            if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                profile.InterfaceName = (name.GetString() ?? string.Empty).Trim();
            }
        }
        catch (Exception)
        {
            // 解析失败 = 当作没有 interface.json
        }
    }

    private static string DirectoryOf(string? scriptPath)
    {
        var path = (scriptPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            return dir is null ? string.Empty : Path.GetFullPath(dir);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static LaunchPresetLibrary Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return new LaunchPresetLibrary();
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var library = JsonSerializer.Deserialize<LaunchPresetLibrary>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return library ?? new LaunchPresetLibrary();
        }
        catch (Exception)
        {
            return new LaunchPresetLibrary();
        }
    }
}
