// S1 第八批：主控端 ↔ 会话代理之间的「抓帧通路」开关与布局参数。
//
// 为什么走桥目录里的小文件，而不是新开 TCP / 命名管道：
//   · 桥目录本来就是两端唯一约定好、且按账户隔离的共享位置（RdpChannelPaths
//     与 RdpBridge.ChannelTag 两端算出同一个目录）；
//   · Agent 常驻循环本来就在 1 Hz 轮询桥，读一个 json 不增加任何常驻连接；
//   · 文件里同时带 width/height/slots，保证两端用**完全相同**的
//     SharedFrameRingLayout 与映射名（TryReadLatest 会逐项校验，差一个字节都读不出）。
//
// 尺寸变更 → 映射名带尺寸后缀 → 自然切到新映射，不必删除旧映射。
using System;
using System.IO;
using System.Text.Json;
using Yomori.Models;

namespace Yomori.Services
{
    /// <summary>桥目录里的抓帧开关。文件不存在 = 关闭（默认安全）。</summary>
    public sealed class CaptureEnableFlag
    {
        public const string FileName = "capture-enable.json";

        public bool Enabled { get; set; }
        public uint Width { get; set; } = 1920;
        public uint Height { get; set; } = 1080;
        public int Slots { get; set; } = SharedFrameRingLayout.DefaultSlotCount;
        public string ChannelTag { get; set; } = string.Empty;
        public long WrittenAtUnixMs { get; set; }

        public static string PathFor(string bridgeDir)
            => Path.Combine(bridgeDir ?? throw new ArgumentNullException(nameof(bridgeDir)), FileName);

        /// <summary>读开关；文件缺失/损坏/JSON 不合法一律返回「关闭」，不让代理误开采集。</summary>
        public static CaptureEnableFlag Read(string bridgeDir)
        {
            try
            {
                string path = PathFor(bridgeDir);
                if (!File.Exists(path)) return Disabled();
                string json = File.ReadAllText(path);
                var flag = JsonSerializer.Deserialize<CaptureEnableFlag>(json);
                if (flag is null) return Disabled();
                // 防御：坏值会让 SharedFrameRingLayout 构造抛异常，进而让采集循环反复重建。
                if (flag.Width == 0 || flag.Height == 0 ||
                    flag.Width > SharedFrameRingLayout.MaxWidth ||
                    flag.Height > SharedFrameRingLayout.MaxHeight) return Disabled();
                if (flag.Slots < 1 || flag.Slots > SharedFrameRingLayout.MaxSlotCount) return Disabled();
                return flag;
            }
            catch
            {
                return Disabled();
            }
        }

        /// <summary>原子写：先写 .tmp 再替换，避免代理读到半截 JSON。</summary>
        public static bool TryWrite(string bridgeDir, CaptureEnableFlag flag, out string error)
        {
            error = string.Empty;
            try
            {
                string dir = bridgeDir ?? throw new ArgumentNullException(nameof(bridgeDir));
                Directory.CreateDirectory(dir);
                string path = PathFor(dir);
                string tmp = path + ".tmp";
                string json = JsonSerializer.Serialize(flag);
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static CaptureEnableFlag Disabled() => new() { Enabled = false };
    }
}
