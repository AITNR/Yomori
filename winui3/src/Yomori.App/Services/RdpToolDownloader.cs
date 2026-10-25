// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Yomori.Models;

namespace Yomori.Services;

/// <summary>PE 文件的结构信息（架构与是否带签名目录）。</summary>
public readonly record struct RdpPeInspection(bool IsPe, ushort Machine, bool Is64Bit, bool HasSignatureDirectory)
{
    public string MachineName => Machine switch
    {
        0x8664 => "x64",
        0x014C => "x86",
        0xAA64 => "ARM64",
        0 => "未知",
        _ => $"0x{Machine:X4}",
    };
}

/// <summary>
/// 下载并校验上游工具。
/// </summary>
/// <remarks>
/// <para>计划书 4.1 / 4.2 的落点：</para>
/// <list type="bullet">
/// <item>下到受保护的 staging 目录，用临时扩展名，完整性通过后才改名为 <c>.exe</c>。</item>
/// <item>校验顺序：大小 → 官方 digest（有才算）→ PE 架构 → 本地 SHA-256 记录。</item>
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
                return (null, $"{asset.Name} 不是有效的 PE 文件");
            }

            if (!pe.Is64Bit)
            {
                // 本程序只有 x64 构建，跑 32 位安装器会写出 32 位载荷
                TryDelete(stagingPath);
                return (null, $"{asset.Name} 是 {pe.MachineName} 架构，与预期的 x64 不符");
            }

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
                : "上游未提供 SHA-256 摘要，本次只能校验大小、PE 架构与来源 HTTPS —— 这不构成密码学意义上的发布者认证。");

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
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
            {
                return new RdpPeInspection(false, 0, false, false);
            }

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();

            if (peOffset <= 0 || peOffset + 24 > stream.Length)
            {
                return new RdpPeInspection(false, 0, false, false);
            }

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x00004550)   // "PE\0\0"
            {
                return new RdpPeInspection(false, 0, false, false);
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

            if (sizeOfOptionalHeader >= 70 && optionalHeaderOffset + sizeOfOptionalHeader <= stream.Length)
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
        }
        catch
        {
            return new RdpPeInspection(false, 0, false, false);
        }
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
