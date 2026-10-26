// Yomori.RdpNative 的 C# P/Invoke 声明（对应 src/Yomori.RdpNative/ycn_rdp.h）
// 纯 .NET，无 WinUI 依赖 —— _smoke 直接链入做「C ABI 签名一致性断言」（计划书 M1 验收）。
//
// 线程模型（见 ycn_rdp.h 头注释）：
//   - on_* 回调全部在原生事件循环线程触发，C# 侧必须自行调度回 UI 线程；
//   - 回调里禁止调用 ycn_rdp_disconnect（死锁）；
//   - ycn_rdp_grab_frame 返回的指针必须立即拷贝，下一次 grab / disconnect 后失效。
using System;
using System.Runtime.InteropServices;

namespace Yomori.Services
{
    /// <summary>连接参数（ycn_rdp_params，平铺布局，与 C 端 StructLayout 对齐）。</summary>
    /// <remarks>
    /// <para>⚠️ 每个 <c>string</c> 字段都必须显式声明 <see cref="UnmanagedType.LPUTF8Str"/>。</para>
    /// <para>C 侧声明是 <c>const char*</c>、注释约定 UTF-8；而 .NET 默认按**ANSI 代码页**封送
    /// 字符串（中文系统上是 GBK）。非 ASCII 的主机名、用户名、域、密码会因此变成另一串字节，
    /// 表现是「认证失败」而不是「编码错误」，极难查（2026-10-26 检查记录 P2）。</para>
    /// <para>声明成 LPUTF8Str 不改变结构体大小（仍是一个指针），所以 ABI 断言不受影响。</para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct YcnRdpParams
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        public string Host;            /* "127.0.0.1" */
        public ushort Port;            /* 0 = 3389 */
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        public string Username;
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        public string Domain;          /* 可 null */
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        public string Password;        /* 来自 RdpCredentialStore.CredRead */
        public uint DesktopWidth;      /* 0 = 1280 */
        public uint DesktopHeight;     /* 0 = 720 */
        public uint ColorDepth;        /* 0 = 32 */
        public int UseNla;             /* -1 自动 / 0 禁 / 1 强制 */
        public int AllowSelfsigned;    /* 1 = 放行自签证书（指纹 UI 是 M6） */
        public int EnableAudio;        /* 1 = 远端声音在本机播放（rdpsnd） */
        public int UseGfx;             /* 1 = GFX 全帧管线（M6，撕裂根治） */
    }

    /// <summary>回调委托（命名空间级，供 interop 与消费方共用；Cdecl 必须与 C 端一致）。</summary>
    /// <remarks>
    /// 带 <c>string</c> 的两个回调同样要写明 UTF-8：原生层给的是 <c>const char*</c>（UTF-8），
    /// 不声明的话中文错误详情会按 ANSI 解出来变成乱码。
    /// </remarks>
    internal static class YcnCallbacks
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void OnConnected(IntPtr user, int session, uint width, uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void OnFrameReady(IntPtr user, int session, uint width, uint height, uint stride);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void OnDesktopResize(IntPtr user, int session, uint width, uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void OnDisconnected(IntPtr user, int session, int reason, [MarshalAs(UnmanagedType.LPUTF8Str)] string detail);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void OnError(IntPtr user, int session, int code, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
    }

    /// <summary>回调表（ycn_rdp_callbacks）。全部可为 null。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct YcnRdpCallbacks
    {
        public YcnCallbacks.OnConnected Connected;
        public YcnCallbacks.OnFrameReady FrameReady;
        public YcnCallbacks.OnDesktopResize DesktopResize;
        public YcnCallbacks.OnDisconnected Disconnected;
        public YcnCallbacks.OnError Error;
    }

    /// <summary>断开原因（on_disconnected 的 reason 参数）。</summary>
    internal static class YcnDisconnectReason
    {
        public const int Local = 1;   // 本端主动 ycn_rdp_disconnect
        public const int Remote = 2;  // 服务器/网络侧断开
        public const int Error = 3;   // 协议层错误
    }

    /// <summary>P/Invoke 入口（签名与 ycn_rdp.h 冻结版一致，勿单独改动）。</summary>
    internal static class RdpNativeInterop
    {
        private const string Lib = "ycn_rdp";
        private const CallingConvention CC = CallingConvention.Cdecl;

        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_connect(ref YcnRdpParams parameters, ref YcnRdpCallbacks callbacks, IntPtr user);
        [DllImport(Lib, CallingConvention = CC)] internal static extern void ycn_rdp_disconnect(int session);
        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_send_mouse(int session, uint flags, ushort x, ushort y);
        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_send_key(int session, int down, int extended, ushort scancode);
        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_send_keyboard_sync(int session, uint flags);
        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_set_muted(int session, int muted);
        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_get_muted(int session);
        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_grab_frame(int session, out uint out_width, out uint out_height, out uint out_stride, out IntPtr out_data);
        [DllImport(Lib, CallingConvention = CC)] internal static extern unsafe int ycn_rdp_copy_frame(int session, byte* dst, uint dst_size, out uint out_width, out uint out_height, out uint out_stride);
        [DllImport(Lib, CallingConvention = CC)] internal static extern void ycn_rdp_last_error(int session, [Out] byte[] buf, UIntPtr buflen);
        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_gfx_state(int session, out uint out_dvc_count);
        [DllImport(Lib, CallingConvention = CC)] internal static extern void ycn_rdp_dvc_names(int session, [Out] byte[] buf, UIntPtr buflen);
        [DllImport(Lib, CallingConvention = CC)] internal static extern void ycn_rdp_diag(int session, [Out] byte[] buf, UIntPtr buflen);
        [DllImport(Lib, CallingConvention = CC)] internal static extern IntPtr ycn_rdp_version(); // const char*

        /// <summary>静音链路自验证（本地构造假设备，不依赖服务器推音频）。返回位掩码，31=全过。</summary>
        [DllImport(Lib, CallingConvention = CC)] internal static extern int ycn_rdp_selftest_mute();
    }
}
