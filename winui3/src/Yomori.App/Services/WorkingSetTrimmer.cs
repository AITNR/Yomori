// WorkingSetTrimmer：主动回收本进程的常驻页（等价于 Mem Reduct / EmptyWorkingSet）。
//
// 为什么这个项目需要它（2026-11-03 实测）：
//   双路 1080p + AVC444 硬解时，进程提交量约 970 MiB，但满帧播视频时
//   专用工作集只有约 164 MiB —— 也就是说约 800 MiB 是"连接期一次性提交、
//   之后再也不碰"的冷页。这批页留在工作集里只会让任务管理器难看，
//   刷出去不会引起缺页风暴（用户实测播放视频不掉速）。
//
// 纪律：
//   · 只在**空闲**时调用（任务执行中刷工作集 = 给正在跑的脚本添缺页）；
//   · 每个连接周期只做一次，不做定时反复刷（反复刷会把刚用热的页也赶走）；
//   · 必须记前后数值，否则无法判断有没有效果、有没有副作用。
using System;
using System.Runtime.InteropServices;

namespace Yomori.Services
{
    internal static class WorkingSetTrimmer
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        /// <summary>本进程当前工作集（MiB）。</summary>
        public static long CurrentWorkingSetMiB()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.WorkingSet64 / 1048576;
        }

        /// <summary>
        /// 回收本进程工作集。返回 (回收前 MiB, 回收后 MiB)；失败返回 null。
        ///
        /// <para>⚠️ 只影响常驻页，**不改变提交量** —— 提交量要降只能减少分配。</para>
        /// </summary>
        public static (long Before, long After)? TryTrim()
        {
            var before = CurrentWorkingSetMiB();
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                if (!EmptyWorkingSet(process.Handle))
                {
                    return null;
                }
            }
            catch (Exception)
            {
                return null;
            }

            return (before, CurrentWorkingSetMiB());
        }
    }
}
