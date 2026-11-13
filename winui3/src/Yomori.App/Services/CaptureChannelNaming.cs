// S1 第七批：通道 → 共享内存映射名的唯一约定（采集端与主控端共用）。
// 纯 .NET、无平台 API：ACL / Global\ 权限属 NC3 真机项，由部署侧处理。
using System;
using System.Security.Cryptography;
using System.Text;

namespace Yomori.Services
{
    public static class CaptureChannelNaming
    {
        public const string GlobalPrefix = @"Global\";
        public const string LocalPrefix = @"Local\";
        public const string MapPrefix = "YomoriCapture_";
        public const int MaxTokenLength = 48;
        public const int MaxNameLength = 240;

        /// <summary>
        /// 一条通道对应一段映射。默认 Global\ 才能跨 Windows 会话；
        /// 单进程自测可传 global:false 用 Local\，避免需要创建全局对象的权限。
        /// </summary>
        public static string ForChannel(string channelId, bool global = true)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("channelId 不能为空", nameof(channelId));
            foreach (char c in channelId)
            {
                if (char.IsControl(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                    throw new ArgumentException("channelId 含内核对象名非法字符", nameof(channelId));
            }

            string prefix = global ? GlobalPrefix : LocalPrefix;
            string token = channelId.Length <= MaxTokenLength ? channelId : Hash(channelId);
            string name = prefix + MapPrefix + token;
            if (name.Length > MaxNameLength)
                name = prefix + MapPrefix + Hash(channelId);
            return name;
        }

        public static bool IsValidMapName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return false;
            string prefix = name.StartsWith(GlobalPrefix, StringComparison.Ordinal) ? GlobalPrefix
                          : name.StartsWith(LocalPrefix, StringComparison.Ordinal) ? LocalPrefix : null!;
            if (prefix is null) return false;
            string rest = name.Substring(prefix.Length);
            if (!rest.StartsWith(MapPrefix, StringComparison.Ordinal)) return false;
            string token = rest.Substring(MapPrefix.Length);
            if (token.Length == 0) return false;
            foreach (char c in token)
                if (char.IsControl(c) || c is '\\' or '/') return false;
            return true;
        }

        private static string Hash(string s)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Substring(0, 32);
        }
    }
}
