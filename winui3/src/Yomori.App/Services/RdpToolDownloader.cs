// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>PE 文件的结构信息（架构与是否带签名目录）。</summary>
/// <remarks>
/// <para><see cref="Unreadable"/> 与 <see cref="IsPe"/>=false 是**两件事**，必须分开：</para>
/// <list type="bullet">
/// <item>读不出来（文件被占用 / 权限不足）：说明不了文件好坏，多半是安全软件正在扫描刚下完的文件；</item>
/// <item>读出来了但不是 PE：内容确实不对（网页、压缩包、截断）。</item>
/// </list>
/// <para>2026-10-26 真机把这两件事混成一句「不是有效的 PE 文件」，
/// 结果是文件其实是好的、只是被杀软握着，却报成「文件无效」—— 用户拿着这条信息无从下手。</para>
/// </remarks>
public readonly record struct RdpPeInspection(
    bool IsPe,
    ushort Machine,
    bool Is64Bit,
    bool HasSignatureDirectory,
    string Detail = "",
    bool Unreadable = false)
{
    public string MachineName => Machine switch
    {
        0x8664 => "x64",
        0x014C => "x86",
        0xAA64 => "ARM64",
        0 => "未知",
        _ => $"0x{Machine:X4}",
    };

    /// <summary>给用户看的一句话。空串表示没有异常。</summary>
    public string ProblemText => IsPe
        ? ""
        : Unreadable
            ? $"读不出来（{Detail}）—— 这不能说明文件坏了，通常是安全软件正在扫描刚下载的文件。请稍等几秒重试；一直被占用就把该目录加入信任。"
            : $"不是可执行文件（{Detail}）";
}

/// <summary>
/// 下载并校验上游工具。
/// </summary>
/// <remarks>
/// <para>计划书 4.1 / 4.2 的落点：</para>
/// <list type="bullet">
/// <item>下到受保护的 staging 目录，用临时扩展名，完整性通过后才改名为 <c>.exe</c>。</item>
/// <item>校验顺序：大小 → 官方 digest（有才算）→ PE 有效性 → 本地 SHA-256 记录。
/// <b>架构只记录不拦截</b>：上游安装器/卸载器本身就是 32 位程序。</item>
/// <item>官方没给 digest 时**不编造通过**：<see cref="RdpDownloadedTool.Limits"/> 如实写出来。</item>
/// <item>执行前二次核对（<see cref="VerifyExisting"/>）：从下载完成到真正执行之间，
/// 普通用户理论上能替换那个文件。</item>
/// </list>
/// </remarks>
public static class RdpToolDownloader
{
    /// <summary>下载中的临时扩展名。不是 <c>.exe</c>，所以不会被误双击，也不会被执行链误认。</summary>
    public const string PartialExtension = ".download";

    public static string StagingDir(long releaseId) =>
        Path.Combine(RdpTransactionStore.ToolsDir, releaseId.ToString());

    /// <summary>
    /// 校验一个用户自己给的安装包：仍然做 PE 校验与哈希记录，只是不与官方摘要比对。
    /// </summary>
    public static (RdpDownloadedTool? Tool, string Error) InspectLocalTool(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (null, $"文件不存在：{path}");
            }

            var info = new FileInfo(path);
            if (info.Length == 0)
            {
                return (null, $"{Path.GetFileName(path)} 是空文件");
            }

            var pe = RdpToolDownloader.InspectPe(path);
            if (!pe.IsPe)
            {
                return (null, $"{Path.GetFileName(path)} {pe.ProblemText}（{info.Length} 字节）");
            }

            return (new RdpDownloadedTool
            {
                Asset = new RdpAsset { Name = Path.GetFileName(path), Size = info.Length },
                LocalPath = path,
                Sha256 = RdpTransactionStore.Sha256File(path),
                SizeBytes = info.Length,
                VerifiedAgainstOfficialDigest = false,
                Limits = "来源为手动指定，只校验了 PE 有效性并记录哈希；无法与官方摘要比对。",
            }, "");
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 这次失败是不是「内容/读取」类问题（值得原样再下一次）。
    /// </summary>
    /// <remarks>
    /// 判据来自 <see cref="RdpPeInspection.ProblemText"/> 的两个开头，和它在同一个文件里维护。
    /// 网络层断流、被代理塞了网页、杀软扫描占文件，这三件事都会随机失败一次就好，
    /// 而「拒绝 HTTPS」「没有下载地址」这类失败重试多少次都一样，不在此列。
    /// </remarks>
    public static bool LooksRetryable(string error) =>
        error.Contains("不是可执行文件", StringComparison.Ordinal)
        || error.Contains("读不出来", StringComparison.Ordinal);

    /// <summary>
    /// 下载一个资产并校验。成功返回本地路径与哈希；失败返回原因（不含异常）。
    /// </summary>
    public static async Task<(RdpDownloadedTool? Tool, string Error)> DownloadAsync(
        RdpAsset asset,
        long releaseId,
        UpdateNetworkSettings network,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (asset.DownloadUrl.Length == 0)
        {
            return (null, $"{asset.Name} 没有下载地址");
        }

        if (!asset.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 只走 HTTPS（计划书 4.1）。上游是 GitHub，20 年来没有非 HTTPS 资产
            return (null, $"{asset.Name} 的下载地址不是 HTTPS，已拒绝");
        }

        var dir = StagingDir(releaseId);
        var finalPath = Path.Combine(dir, asset.Name);
        var stagingPath = finalPath + PartialExtension;

        try
        {
            Directory.CreateDirectory(dir);
            RdpTransactionStore.TryHardenDirectory(dir);
        }
        catch (Exception ex)
        {
            return (null, $"无法创建下载目录：{ex.GetType().Name}: {ex.Message}");
        }

        var (ok, message) = await UpdateService
            .DownloadAsync(asset.DownloadUrl, stagingPath, network, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            return (null, message);
        }

        try
        {
            var fileInfo = new FileInfo(stagingPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                return (null, $"{asset.Name} 下载后为空文件");
            }

            if (asset.Size > 0 && fileInfo.Length != asset.Size)
            {
                TryDelete(stagingPath);
                return (null, $"{asset.Name} 大小不符：期望 {asset.Size} 字节，实际 {fileInfo.Length} 字节");
            }

            var sha = RdpTransactionStore.Sha256File(stagingPath);
            var officialSha = asset.Sha256FromDigest;
            var verifiedAgainstOfficial = false;

            if (officialSha.Length > 0)
            {
                if (!string.Equals(officialSha, sha, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(stagingPath);
                    return (null, $"{asset.Name} SHA-256 与官方摘要不符：官方 {officialSha}，本地 {sha}");
                }

                verifiedAgainstOfficial = true;
            }

            var pe = InspectPe(stagingPath);
            if (!pe.IsPe)
            {
                TryDelete(stagingPath);
                return (null, $"{asset.Name} {pe.ProblemText}（实际 {fileInfo.Length} 字节）");
            }

            // ⚠️ 这里**不**按架构拦截。计划书 4.2 写的是「错误架构时安全失败」，但实测上游
            // RDPW_Installer.exe / RDPW_Uninstaller.exe 本身就是 **32 位**（Delphi 程序），
            // 它负责装的是 64 位的 rdpwrap.dll。按 x64 拦一道等于永远装不上（2026-10-26 真机反馈）。
            // 架构只作为诊断信息记下来：真拿到一个陌生的可执行文件时，写错了架构仍会在
            // 大小/摘要/签名三项上暴露。

            // 校验全过之后才改名成 .exe —— 中途失败的文件永远带着 .download 后缀
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(stagingPath, finalPath);

            var limits = new System.Collections.Generic.List<string>
            {
                "校验能力说明：",
            };

            limits.Add(verifiedAgainstOfficial
                ? "已按官方提供的 SHA-256 摘要校验。"
                : "上游未提供 SHA-256 摘要，本次只能校验大小、PE 有效性与来源 HTTPS —— 这不构成密码学意义上的发布者认证。");

            // 架构只报告不拦截：上游工具本身就是 32 位（见上面的说明）
            limits.Add($"工具架构：{pe.MachineName}（上游安装器为 32 位程序，属正常）。");

            limits.Add(pe.HasSignatureDirectory
                ? "文件带 Authenticode 签名目录（Yomori 未对证书链做信任校验）。"
                : "文件没有 Authenticode 签名。");

            return (new RdpDownloadedTool
            {
                Asset = asset,
                LocalPath = finalPath,
                Sha256 = sha,
                SizeBytes = fileInfo.Length,
                VerifiedAgainstOfficialDigest = verifiedAgainstOfficial,
                Limits = string.Join(" ", limits),
            }, "");
        }
        catch (Exception ex)
        {
            TryDelete(stagingPath);
            return (null, $"校验 {asset.Name} 时出错：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 执行前二次核对：文件还在、大小一致、哈希没变。
    /// </summary>
    /// <remarks>
    /// 计划书 4.1 的最后一句要求「实际执行前二次核对文件，避免普通用户替换」。
    /// 从下载完成到点击执行之间，程序并没有独占那个目录。
    /// </remarks>
    public static (bool Ok, string Message) VerifyExisting(string path, string expectedSha256, long expectedSize)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (false, "文件不存在");
        }

        var info = new FileInfo(path);
        if (expectedSize > 0 && info.Length != expectedSize)
        {
            return (false, $"文件大小已变化（期望 {expectedSize}，实际 {info.Length}）");
        }

        if (expectedSha256.Length > 0)
        {
            var sha = RdpTransactionStore.Sha256File(path);
            if (!string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "文件哈希已变化");
            }
        }

        return (true, "");
    }

    /// <summary>
    /// 读 PE 头：架构与是否有签名目录。
    /// </summary>
    /// <remarks>
    /// 只读最小必要字段，不引第三方 PE 库：
    /// <c>e_lfanew</c> 在 0x3C，COFF 的 <c>Machine</c> 在 <c>e_lfanew + 4</c>；
    /// 数据目录在可选头里：PE32 偏移 96、PE32+ 偏移 112，每项 8 字节，
    /// 第 4 项（索引 4）是 <c>IMAGE_DIRECTORY_ENTRY_SECURITY</c>。
    /// </remarks>
    public static RdpPeInspection InspectPe(string path)
    {
        // 刚下载完的文件常被杀软实时扫描短暂独占（FileShare.None），
        // 这时打不开**不代表文件无效**。重试几次，仍然不行才报「读不出来」。
        RdpPeInspection last = default;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            last = InspectPeOnce(path);
            if (last.IsPe || !last.Unreadable)
            {
                return last;
            }

            Thread.Sleep(attempt == 0 ? 80 : 150);
        }

        return last;
    }

    /// <summary>读一次 PE 头。失败时区分「读不出来」与「内容不是 PE」。</summary>
    private static RdpPeInspection InspectPeOnce(string path)
    {
        try
        {
            // 用 FileShare.ReadWrite | Delete：杀软扫描时通常以共享读打开，
            // 我们这里退一步，避免为了一个只读检查去和它对撞。
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);

            var length = stream.Length;
            var head = reader.ReadBytes((int)Math.Min(64, length));

            if (length < 0x40)
            {
                // 太小也要说清是什么：48 字节的 HTML 页面比「文件只有 48 字节」有用得多
                return NotPe($"{SniffHead(head)}（{length} 字节）");
            }

            if (head.Length < 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
            {
                return NotPe(SniffHead(head));
            }

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();

            if (peOffset <= 0 || peOffset + 24 > length)
            {
                return NotPe($"PE 头偏移不合法（{peOffset}），文件可能没下完");
            }

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x00004550)   // "PE\0\0"
            {
                return NotPe("有 MZ 头但 PE 签名不符，文件可能没下完或被改过");
            }

            var machine = reader.ReadUInt16();
            var numberOfSections = reader.ReadUInt16();
            _ = reader.ReadUInt32();   // TimeDateStamp
            _ = reader.ReadUInt32();   // PointerToSymbolTable
            _ = reader.ReadUInt32();   // NumberOfSymbols
            var sizeOfOptionalHeader = reader.ReadUInt16();
            var characteristics = reader.ReadUInt16();

            var is64 = (characteristics & 0x0020) != 0;   // IMAGE_FILE_LARGE_ADDRESS_AWARE 只是暗示，下面用 Magic 判

            var optionalHeaderOffset = peOffset + 24;
            var signed = false;

            if (sizeOfOptionalHeader >= 70 && optionalHeaderOffset + sizeOfOptionalHeader <= length)
            {
                stream.Seek(optionalHeaderOffset, SeekOrigin.Begin);
                var magic = reader.ReadUInt16();
                is64 = magic == 0x20B;   // PE32+ = 0x20B，PE32 = 0x10B

                var dataDirectoryOffset = is64 ? 112 : 96;
                if (sizeOfOptionalHeader >= dataDirectoryOffset + (5 * 8))
                {
                    // 索引 4 = 安全目录（Authenticode）。值为 0 表示没有签名
                    stream.Seek(optionalHeaderOffset + dataDirectoryOffset + (4 * 8), SeekOrigin.Begin);
                    var securityVirtualAddress = reader.ReadUInt32();
                    signed = securityVirtualAddress != 0;
                }
            }

            _ = numberOfSections;
            return new RdpPeInspection(true, machine, is64, signed);

            RdpPeInspection NotPe(string detail) => new(false, 0, false, false, detail);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new RdpPeInspection(false, 0, false, false,
                $"拒绝访问：{ex.Message}", Unreadable: true);
        }
        catch (IOException ex)
        {
            return new RdpPeInspection(false, 0, false, false,
                ex.GetType().Name, Unreadable: true);
        }
        catch (Exception ex)
        {
            return new RdpPeInspection(false, 0, false, false,
                $"{ex.GetType().Name}: {ex.Message}", Unreadable: true);
        }
    }

    /// <summary>
    /// 猜一下「这到底是什么文件」。判错时的日志价值全在这一句上 ——
    /// 只写「不是 PE」，下次还是查不出是网络拦了还是文件被换了。
    /// </summary>
    private static string SniffHead(byte[] head)
    {
        if (head.Length == 0)
        {
            return "文件是空的";
        }

        if (head[0] == (byte)'<')
        {
            return "内容是网页/XML（多半是下载被网络或代理拦截了）";
        }

        if (head.Length >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
        {
            return "内容是 ZIP 压缩包";
        }

        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            return "内容是 gzip 压缩包";
        }

        var hex = string.Join(" ", head.Take(Math.Min(8, head.Length)).Select(b => b.ToString("X2")));
        return $"开头是 {hex}（不是 MZ）";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就留着，反正 .download 不会被当成可执行工具
        }
    }
}
