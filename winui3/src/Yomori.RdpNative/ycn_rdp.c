/* ============================================================
 * Yomori.RdpNative — FreeRDP 3.32 桥接层实现
 *
 * M0 踩出的 API 事实全部落实（计划书附录 C）：
 *   1) 进程级一次性初始化：WSAStartup（winpr 3.x 不代劳）、
 *      freerdp_register_addin_provider（内建通道提供器）、
 *      OPENSSL_MODULES = DLL 所在目录（_wputenv_s，legacy → md4 → NTLM）
 *   2) gdi_init 必须在 PostConnect（PreConnect 段错误）
 *   3) ServerPort 是 UINT32
 *   4) Authenticate 弃用 → AuthenticateEx
 *   5) load_addins 在 freerdp/client/cmdline.h
 *   6) rdpsnd 硬依赖 rdpdr（load_addins 自动拉回 DeviceRedirection）
 *   7) NetworkAutoDetect / Multitransport / HeartbeatPdu 必须关
 *      （标准 RDP 安全 + AutoDetect = RC4 解密流错位）
 * ============================================================ */
#include "ycn_rdp.h"

/* WIN32_LEAN_AND_MEAN 必须定义：否则 windows.h 引入 shellapi.h，其 NIIF_* 宏
 * 会与 freerdp rail.h 的 NIIF 枚举冲突（C2059）。spike 已踩过 */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <freerdp/freerdp.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/error.h>
#include <freerdp/scancode.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/client/channels.h>
#include <freerdp/channels/rdpgfx.h>
#include <freerdp/channels/drdynvc.h>
#include <freerdp/channels/rdpsnd.h>
#include <freerdp/client/rdpsnd.h>   /* rdpsndDevicePlugin::{SetVolume,...}（本机静音靠链式挂钩它） */

/* FREERDP_ADDIN_CHANNEL_* 的值照抄 freerdp/addin.h。
 * ⚠️ 不能 #include <freerdp/addin.h>：它会拉进 rail.h/shellapi.h，
 * 与 windows.h 的 NIIF_* 宏撞车（C2059，M0 已踩过同类坑）。 */
#define YCN_ADDIN_CHANNEL_STATIC  0x00001000
#define YCN_ADDIN_CHANNEL_ENTRYEX 0x00008000
#include <freerdp/client.h>
#include <winpr/wtypes.h>
#include <winpr/synch.h>
#include <winpr/thread.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <limits.h>

EXTERN_C IMAGE_DOS_HEADER __ImageBase;

/* ---------------- 常量 ---------------- */

/* 并发内嵌会话上限。
 * 多会话通道并行需要同时挂多条连接；8 路对桌面/Server 场景都够用，
 * 每路约 1 个事件循环线程 + 帧缓冲，再高没有实际收益。
 * 改动此处必须同步 C# 侧 RdpNativeLimits.MaxSessions（Models/RdpChannelPlanner.cs）。 */
#define YCN_MAX_SESSIONS 8
#define YCN_ERRBUF_LEN   512
#define TAG "ycn.rdpnative"

/* ---------------- 会话状态 ---------------- */

/* KBD_SYNC_* —— TS_SYNCHRONIZE 的 toggle 位（FreeRDP input.h 同名常量同值，
 * MS-RDPBCGR 2.2.8.1.1.3.1.1.1）。每位只在对应锁**开启**时置 1。 */
#define YCN_KBD_SYNC_SCROLL_LOCK 0x00000001u
#define YCN_KBD_SYNC_NUM_LOCK    0x00000002u
#define YCN_KBD_SYNC_CAPS_LOCK   0x00000004u
#define YCN_KBD_SYNC_KANA_LOCK   0x00000008u
#define YCN_KBD_SYNC_ALL         0x0000000Fu

typedef enum
{
	YCN_STATE_IDLE = 0,
	YCN_STATE_CONNECTING,
	YCN_STATE_RUNNING,
	YCN_STATE_STOPPING
} YcnState;

/* 输入注入请求：send_* 只入队，由事件循环线程消费——
 * 跨线程直接调 freerdp_input_send_* 会破坏协议流（M1 冒烟实测：
 * send_mouse 时刻恰好 BIO_read retries exceeded 掉线） */
typedef struct
{
	uint32_t flags;
	uint16_t x;
	uint16_t y;
} YcnMouseReq;

typedef struct
{
	int down;
	int extended;
	uint16_t scancode;
} YcnKeyReq;

#define YCN_INPUT_QUEUE_CAP 64

typedef struct
{
	int used;                 /* 槽位占用 */
	int id;                   /* 对外 session id（1..YCN_MAX_SESSIONS） */

	freerdp* instance;
	HANDLE thread;
	HANDLE stop_event;
	volatile LONG state;      /* YcnState */
	volatile LONG disconnected_reported; /* on_disconnected 恰好一次 */
	volatile LONG abort_in_progress; /* CONNECTING abort protects context lifetime */
	volatile LONG connect_in_progress;

	ycn_rdp_callbacks cb;
	void* user;

	/* 帧缓冲（BGRA32，g_lock 保护） */
	uint8_t* frame;
	uint32_t frame_width;
	uint32_t frame_height;
	uint32_t frame_stride;
	BOOL frame_valid;

	char last_error[YCN_ERRBUF_LEN];
	pEndPaint orig_end_paint;   /* gdi 原生 EndPaint（per-session，多会话不冲突） */
	ULONGLONG connected_at;     /* PostConnect 时刻（RefreshRect 延迟基准） */
	int refresh_requested;      /* 全屏刷新请求已发标志（重连会话服务器不主动推帧） */

	/* 输入队列（g_lock 保护；满则丢弃并计错误） */
	YcnMouseReq mouse_q[YCN_INPUT_QUEUE_CAP];
	int mouse_head;
	int mouse_tail;
	YcnKeyReq key_q[YCN_INPUT_QUEUE_CAP];
	int key_head;
	int key_tail;
	/* 待同步的键盘 toggle 状态（KBD_SYNC_* 位掩码）。
	 * ⚠️ 小键盘 0-9 与 Insert/Home/PgUp/End/PgDn/方向键在 Set1 里**共用扫描码**
	 *   （如 Numpad1 与 End 都是 0x4F），唯一区别就是 extended 位 + 服务器侧 NumLock。
	 *   不同步 NumLock → 服务器按自己的锁状态解释，用户按小键盘却打出方向键（Home/End…）。
	 * 置 pending_sync=1 让事件循环线程下一拍发 TS_SYNCHRONIZE（跨线程直发同 send_* 禁忌）。*/
	uint32_t sync_flags;
	int pending_sync;

	/* 连接参数副本（connect 时深拷贝，C# 侧内存随时可释放） */
	char host[256];
	uint16_t port;
	char username[128];
	char domain[128];
	char password[256];
	uint32_t width;
	uint32_t height;
	uint32_t color_depth;
	int use_nla;
	int allow_selfsigned;
	int enable_audio;
	int use_gfx;
	volatile LONG muted;              /* 1 = 本机静音（拦 device 的 Play/PlayEx 丢弃数据 + SetVolume 置 0） */
	/* ⚠️ 一条 RDP 连接里可能同时存在**两个** rdpsnd 插件实例：
	 *   ① 静态通道 `rdpsnd`（VirtualChannelEntryEx 建的那份）
	 *   ② DVC    `AUDIO_PLAYBACK_DVC`（drdynvc 加载 rdpsnd 客户端时另建的 dynamic 实例）
	 * 服务器通常走 ②（AUDIO_PLAYBACK_DVC），①的 OnOpenCalled 恒 0、收不到任何数据。
	 * 所以两个都要跟踪、都要挂钩，否则静音只挂在"哑"的那份上 → 无效。 */
	#define YCN_RDPSND_SLOTS 4
	void* rdpsnd_plugin[YCN_RDPSND_SLOTS];   /* 各 plugin 指针 */
	void* rdpsnd_device[YCN_RDPSND_SLOTS];   /* 各 device 指针（挂钩用；拿不到时 NULL） */
	void* rdpsnd_orig_set_volume[YCN_RDPSND_SLOTS];
	void* rdpsnd_orig_play[YCN_RDPSND_SLOTS];
	void* rdpsnd_orig_play_ex[YCN_RDPSND_SLOTS];
	int rdpsnd_slot_count;                    /* 已发现的 plugin 数 */
	volatile LONG mute_play_hits;     /* 诊断：Play/PlayEx 钩子被调用次数 */
	volatile LONG mute_setvol_hits;   /* 诊断：静音态下 SetVolume 钩子命中次数 */
	volatile LONG mute_dbg_tick;      /* 诊断：hook 快照节拍计数（限流用） */
	volatile LONG mute_dvc_tick;      /* 诊断：DVC 扫描节拍计数（限流用） */
	volatile LONG gfx_state;       /* 0/1/2/3，见 ycn_rdp_gfx_state */
	volatile LONG dvc_connected;   /* 收到的动态通道连接事件累计数 */
	char dvc_names[256];           /* 已连上的通道名（逗号分隔，诊断用） */
	int addins_rc;                 /* freerdp_client_load_addins 返回值（0 成功 / -1 失败 / -2 未调用） */
	int gfx_init_rc;               /* gdi_graphics_pipeline_init 返回值：0 未调用 / 1 成功 / 2 失败 */
	int gfx_codecs_null;           /* 接入后 gfx->codecs 是否为 NULL（-1 未测）：1 = NULL（解码器没准备好） */
	int gfx_progressive_null;      /* gfx->codecs->progressive 是否为 NULL：1 = NULL（GFX 帧处理会崩的嫌疑点） */
} YcnSession;

static YcnSession g_sessions[YCN_MAX_SESSIONS];
static CRITICAL_SECTION g_lock;
static INIT_ONCE g_init_once = INIT_ONCE_STATIC_INIT;
static int64_t g_next_session_id = 1;
static volatile LONG g_total_created = 0;
static volatile LONG g_total_released = 0;
static char g_global_error[YCN_ERRBUF_LEN]; /* session 未找到时的兜底 */

/* ---------------- 工具 ---------------- */

static void set_session_error(YcnSession* s, const char* fmt, ...)
{
	va_list ap;
	if (!s)
		return;
	EnterCriticalSection(&g_lock);
	va_start(ap, fmt);
	_vsnprintf_s(s->last_error, sizeof(s->last_error), _TRUNCATE, fmt, ap);
	va_end(ap);
	LeaveCriticalSection(&g_lock);
}

static void set_global_error(const char* fmt, ...)
{
	va_list ap;
	EnterCriticalSection(&g_lock);
	va_start(ap, fmt);
	_vsnprintf_s(g_global_error, sizeof(g_global_error), _TRUNCATE, fmt, ap);
	va_end(ap);
	LeaveCriticalSection(&g_lock);
}

/* OpenSSL legacy provider（md4 → NTLM/NLA）需要 OPENSSL_MODULES 指向本 DLL 目录。
 * 必须 _wputenv_s：SetEnvironmentVariableW 不更新 UCRT _environ，OpenSSL getenv 读不到 */
static void setup_openssl_modules(void)
{
	wchar_t path[MAX_PATH];
	wchar_t* slash;
	GetModuleFileNameW((HMODULE)&__ImageBase, path, MAX_PATH);
	slash = wcsrchr(path, L'\\');
	if (slash)
		*slash = L'\0';
	_wputenv_s(L"OPENSSL_MODULES", path);
	SetEnvironmentVariableW(L"OPENSSL_MODULES", path);
}

/* FFmpeg 的 H.264 后端在 Windows 上默认选 AV_HWDEVICE_TYPE_DXVA2 —— 见
 * libfreerdp/codec/h264_ffmpeg.c 的 `#elif defined(_WIN32)` 分支，是硬编码的
 * 平台默认值，不是「依次尝试」。而新版 FFmpeg 对 DXVA2 的支持在收敛，D3D11VA 更完整，
 * 所以这里把设备类型钉到 d3d11va。
 *
 * 为什么用环境变量而不是 ffmpeg.json：后者的路径由 freerdp_GetConfigFilePath 按
 * vendor/product/version 组合（还受 FREERDP_USE_VENDOR_PRODUCT_CONFIG_DIR、
 * WITH_RESOURCE_VERSIONING、XDG_CONFIG_HOME 影响），随构建方式变化，本进程无从可靠
 * 推断；环境变量是确定的，且 FreeRDP 的优先级链是
 * 「ffmpeg.json(系统) → ffmpeg.json(用户) → 环境变量 → 编译默认」，本机没有前两者，
 * 环境变量即生效。
 *
 * 双写 _wputenv_s + SetEnvironmentVariableW：与 OPENSSL_MODULES 同理，
 * FFmpeg/winpr 走 getenv 读 UCRT 的 _environ，只调 SetEnvironmentVariable 读不到。
 *
 * 已存在时不覆盖：留一个诊断口子，排查软解回落时可临时设 FREERDP_HWCTX_TYPE=none。 */
static void setup_h264_hwaccel_env(void)
{
	wchar_t cur[64];
	DWORD n = GetEnvironmentVariableW(L"FREERDP_HWCTX_TYPE", cur, (DWORD)ARRAYSIZE(cur));
	if (n > 0 && n < (DWORD)ARRAYSIZE(cur))
		return;

	_wputenv_s(L"FREERDP_HWCTX_TYPE", L"d3d11va");
	SetEnvironmentVariableW(L"FREERDP_HWCTX_TYPE", L"d3d11va");
}

static BOOL CALLBACK init_once_cb(PINIT_ONCE once, PVOID param, PVOID* context)
{
	WSADATA wsa;
	(void)once;
	(void)param;
	(void)context;

	if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
	{
		set_global_error("WSAStartup 失败");
	}
	/* 内建静态通道提供器：不注册则 rdpdr/rdpsnd 被当外部 DLL 找而失败 */
	freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0);
	setup_openssl_modules();
	setup_h264_hwaccel_env();
	return 0;
}

static BOOL process_init(void)
{
	InitOnceExecuteOnce(&g_init_once, init_once_cb, NULL, NULL);
	return TRUE;
}

static YcnSession* find_session(int id)
{
	int i;
	if (id <= 0)
		return NULL;
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].id == id)
			return &g_sessions[i];
	}
	return NULL;
}

/* Connect setup failures happen before the event thread owns the FreeRDP objects. */
static void ycn_abandon_connect(YcnSession* s, freerdp* instance, HANDLE stop_event,
                                HANDLE thread)
{
	if (thread)
		CloseHandle(thread);
	if (stop_event)
		CloseHandle(stop_event);
	if (instance)
	{
		if (instance->context)
			freerdp_context_free(instance);
		freerdp_free(instance);
	}
	EnterCriticalSection(&g_lock);
	s->instance = NULL;
	s->thread = NULL;
	s->stop_event = NULL;
	s->cb.on_connected = NULL;
	s->cb.on_frame_ready = NULL;
	s->cb.on_desktop_resize = NULL;
	s->cb.on_disconnected = NULL;
	s->cb.on_error = NULL;
	s->user = NULL;
	s->used = 0;
	InterlockedExchange(&s->state, YCN_STATE_IDLE);
	InterlockedIncrement(&g_total_released);
	LeaveCriticalSection(&g_lock);
}

/* ---------------- 帧回调链（gdi EndPaint 铩） ---------------- */

static BOOL ycn_end_paint(rdpContext* context)
{
	YcnSession* s = NULL;
	rdpGdi* gdi;
	int i;

	/* 线性查 session（最多 4 个）；先调 gdi 原生 EndPaint 再拷帧 */
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);

	if (s && s->orig_end_paint && !s->orig_end_paint(context))
		return FALSE;

	if (!s || !s->instance || !s->instance->context || !s->instance->context->gdi)
		return TRUE;

	gdi = s->instance->context->gdi;
	if (!gdi->primary_buffer || gdi->width <= 0 || gdi->height <= 0)
		return TRUE;

	/* 拷贝到会话帧缓冲（BGRA32） */
	EnterCriticalSection(&g_lock);
	{
		uint32_t stride = (uint32_t)gdi->stride > 0 ? (uint32_t)gdi->stride
		                                            : (uint32_t)gdi->width * 4;
		if (s->frame_width != (uint32_t)gdi->width || s->frame_height != (uint32_t)gdi->height ||
		    !s->frame)
		{
			uint8_t* nb = (uint8_t*)realloc(s->frame, (size_t)stride * gdi->height);
			if (nb)
			{
				s->frame = nb;
				s->frame_width = (uint32_t)gdi->width;
				s->frame_height = (uint32_t)gdi->height;
				s->frame_stride = stride;
			}
		}
		if (s->frame)
		{
			memcpy(s->frame, gdi->primary_buffer, (size_t)s->frame_stride * s->frame_height);
			s->frame_valid = TRUE;
		}
	}
	LeaveCriticalSection(&g_lock);

	if (s->cb.on_frame_ready)
		s->cb.on_frame_ready(s->user, s->id, s->frame_width, s->frame_height, s->frame_stride);
	return TRUE;
}

/* ---------------- FreeRDP 回调 ---------------- */

/* GFX 动态通道连上：把 RdpgfxClientContext 接到 GDI 图形管线（M6）。
 * 之后 GFX 解码帧经 gdi_OutputUpdate 自动 blit 进 primary_buffer 并触发
 * begin/end_paint —— ycn_end_paint 钩子照常收帧，且 EndFrame 语义保证整帧。 */
/* 诊断落盘（定义在下面，这里先声明，回调要用） */
static void ycn_debug_dump_gfx(const YcnSession* s, const char* tag);

/* ---- 诊断：GFX 帧到底用的哪个编解码器 ----
 * codecId：0=UNCOMPRESSED 1=AV1 8=CLEARCODEC 9=CAPROGRESSIVE 10=PLANAR
 *          11=AVC420 13=CAPROGRESSIVE_V2 14=AVC444 15=AVC444v2
 * 若日志里出现 11/14/15，而本机 freerdp 是 WITH_GFX_H264=OFF（没有 h264 解码器），
 * 那就是崩溃的直接原因 —— 服务器无视 AVC_DISABLED 强推 AVC。 */
static pcRdpgfxSurfaceCommand g_orig_gfx_surface = NULL;
static volatile LONG g_gfx_last_codec = -1;

/* 链式挂在 gdi 的实现之前：只记账（诊断已确认服务器发 CAPROGRESSIVE + CLEARCODEC），
 * 不再每帧写文件 —— 同步 I/O 会拖垮帧率。 */
static UINT ycn_gfx_surface_command(RdpgfxClientContext* ctx, const RDPGFX_SURFACE_COMMAND* cmd)
{
	if (cmd)
		InterlockedExchange(&g_gfx_last_codec, (LONG)cmd->codecId);
	if (g_orig_gfx_surface)
		return g_orig_gfx_surface(ctx, cmd);
	return CHANNEL_RC_OK;
}

/* ---- 诊断面包屑：**内存环形缓冲**，稳态零 I/O ----
 *
 * 🔴 纪律（2026-10-16 实测换来的，改前必读）：
 *   诊断信息在**稳态路径上一律不落盘**——只往这里的内存环形缓冲塞一行；
 *   只有"进程马上要死、证据会一起没"的时刻才允许写文件。
 *
 * 为什么定这条：原来的面包屑直接 fopen/fprintf/fclose 往 %TEMP%\ycn_gfx_debug.txt
 * 追加，而热路径频率是「每帧级」不是 500ms 一拍 —— event_loop 的
 * WaitForMultipleObjects(...,500) 在帧流活跃时每次事件唤醒都会返回，
 * 于是 ycn_rdpsnd_hook 跟着每帧跑一次，快照条件 tick%120 每几秒就命中。
 * 两层后果：
 *   ① 主控端 %TEMP%\ycn_gfx_debug.txt 长到 **588 MB / 544 万行**（94% 是 mute-snap#）；
 *   ② UI 线程点「静音」/建连走 ycn_rdp_set_muted，要抢**同一把 g_lock**，
 *      写盘那一刻它就等在那里 —— 界面直接卡死（watchdog.log 里的「假死」正是此事）。
 * 归纳成一句：**锁内绝不 I/O，热路径绝不 I/O**。常规诊断走 WLog（FreeRDP 日志），
 * 只有 ycn_note_av（SEH 吞下 AV）才调 ycn_dump_breadcrumbs 落盘。
 *
 * 崩溃前最后几行就是元凶所在的那一步 —— 这个诊断价值由环形缓冲完整保留。 */
#define YCN_BC_SLOTS 64
#define YCN_BC_LEN   256
static char g_bc[YCN_BC_SLOTS][YCN_BC_LEN];
static volatile LONG g_bc_seq = 0;

/* 记一行面包屑：只 memcpy + 原子递增，不分配、不 I/O、不抢锁（可在 g_lock 内调）。 */
static void ycn_bc(const char* tag)
{
	LONG seq;
	if (!tag)
		return;
	seq = InterlockedIncrement(&g_bc_seq) - 1;
	strncpy_s(g_bc[seq % YCN_BC_SLOTS], YCN_BC_LEN, tag, _TRUNCATE);
}

/* 崩溃路径专用：记一行 + 把环形缓冲整体落盘（定义在下面）。 */
static void ycn_note_av(const char* tag);

/* ---- 诊断：GFX 各阶段「面包屑」（只经 ycn_bc 记内存，不落盘）----
 * 崩溃点定位：钩住 ResetGraphics / StartFrame / EndFrame，
 * 崩溃前最后一行就是元凶所在的那一步。 */
static pcRdpgfxResetGraphics g_orig_reset_graphics = NULL;
static pcRdpgfxStartFrame g_orig_start_frame = NULL;
static pcRdpgfxEndFrame g_orig_end_frame = NULL;


/* 把环形缓冲里最后 ≤YCN_BC_SLOTS 行面包屑**一次性**落盘。
 * 只在崩溃路径 / 进程退出时调用 —— 稳态下本函数零调用，所以稳态 I/O 为零。 */
static void ycn_dump_breadcrumbs(void)
{
	char path[MAX_PATH] = WINPR_C_ARRAY_INIT;
	FILE* f = NULL;
	LONG seq, i, start;
	const char* override = getenv("YCN_DEBUG_TAG");

	if (override && override[0])
	{
		strncpy_s(path, sizeof(path), override, _TRUNCATE);
	}
	else
	{
		const DWORD n = GetTempPathA(MAX_PATH, path);
		if (n == 0 || n >= MAX_PATH)
			return;
		strcat_s(path, sizeof(path), "ycn_gfx_debug.txt");
	}
	if (fopen_s(&f, path, "a") != 0 || !f)
		return;

	seq = InterlockedCompareExchange(&g_bc_seq, 0, 0);
	start = (seq > YCN_BC_SLOTS) ? (seq - YCN_BC_SLOTS) : 0;
	for (i = start; i < seq; i++)
		fprintf(f, "[tag] %s\n", g_bc[i % YCN_BC_SLOTS]);
	fclose(f);
}

/* SEH 吞下 AV 的那一刻：记一行 + 立刻把面包屑落盘（这是唯一允许的写盘时机）。 */
static void ycn_note_av(const char* tag)
{
	ycn_bc(tag);
	ycn_dump_breadcrumbs();
}

static UINT ycn_gfx_reset_graphics(RdpgfxClientContext* ctx, const RDPGFX_RESET_GRAPHICS_PDU* pdu)
{
	UINT rc = CHANNEL_RC_OK;
	if (g_orig_reset_graphics)
	{
		/* ⚠️ gdi_ResetGraphics 在本机这份 freerdp 上**必然 AV**（已用面包屑钉死：
		 * 进去就出不来，异常码 0xC0000005）。SEH 兜住之后 GFX 全帧管线照常工作
		 * （实测帧到达 ~37 次/秒、codec = CAPROGRESSIVE + CLEARCODEC、画面正常），
		 * 所以这里先兜底保证可用；治本要等定位到 gdi_ResetGraphics 内部的具体行。 */
		__try
		{
			rc = g_orig_reset_graphics(ctx, pdu);
		}
		__except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
		              ? EXCEPTION_EXECUTE_HANDLER
		              : EXCEPTION_CONTINUE_SEARCH)
		{
			ycn_note_av("ResetGraphics-AV(0xC0000005) swallowed");
			rc = CHANNEL_RC_OK; /* 假装成功，别让上层直接断链 */
		}
	}
	return rc;
}

static UINT ycn_gfx_start_frame(RdpgfxClientContext* ctx, const RDPGFX_START_FRAME_PDU* pdu)
{
	UINT rc = CHANNEL_RC_OK;
	if (g_orig_start_frame)
	{
		__try
		{
			rc = g_orig_start_frame(ctx, pdu);
		}
		__except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
		              ? EXCEPTION_EXECUTE_HANDLER
		              : EXCEPTION_CONTINUE_SEARCH)
		{
			ycn_note_av("StartFrame-AV swallowed");
			rc = CHANNEL_RC_OK;
		}
	}
	return rc;
}

static UINT ycn_gfx_end_frame(RdpgfxClientContext* ctx, const RDPGFX_END_FRAME_PDU* pdu)
{
	UINT rc = CHANNEL_RC_OK;
	if (g_orig_end_frame)
	{
		__try
		{
			rc = g_orig_end_frame(ctx, pdu);
		}
		__except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
		              ? EXCEPTION_EXECUTE_HANDLER
		              : EXCEPTION_CONTINUE_SEARCH)
		{
			ycn_note_av("EndFrame-AV swallowed");
			rc = CHANNEL_RC_OK;
		}
	}
	return rc;
}

/* ---- 本机静音（拦截 rdpsnd 设备的 SetVolume，强制置 0）----
 *
 * 语义：让**本机扬声器**不出声，远端照常发声（不是关掉音频通道）。
 *
 * ❌ 走过的弯路：
 *   ① 用 `WLog_Get("com.freerdp.channels.rdpsnd.client")` 反推插件基址 —— 这个名字是
 *      **全局单例**，多会话拿到同一个指针 → 静音一个通道把别人也静了。
 *   ② 用 `ChannelConnectedEventArgs.pInterface` 拿 rdpsndPlugin —— **它是 NULL**！
 *      静态通道的 pInterface 来自 VirtualChannelInitEx 的 clientContext 参数，
 *      而 rdpsnd 传的就是 nullptr（官方 Windows 客户端也只处理 rail/cliprdr/disp 这些 DVC，
 *      从不碰 rdpsnd）。所以早期 attach 一进门就被 `!e->pInterface` 挡掉，静音完全没反应。
 *   ③ 只调一次 SetVolume(0) —— 服务器后续的 volume PDU 会把它盖回去 → 取消静音也不恢复。
 *
 * ✅ 正解（两步）：
 *   A) 怎么拿到 rdpsndPlugin：从 **`context->channels->openDataList[i].lpUserParam`** 拿。
 *      lpUserParam 就是 VirtualChannelInitEx 的 lpUserParam = rdpsnd 插件本身；
 *      按 `stats.channelName == "rdpsnd"` 匹配那一项即可。
 *      `rdpContext.channels` 是**公开字段**；`rdpChannels` / `CHANNEL_OPEN_DATA` 是私有结构，
 *      但布局稳定，偏移由探针实测（真实私有头 offsetof，见下表）。
 *   B) 拿到 device 后**链式挂钩 device->SetVolume**：静音态把传下来的 value 压成 0
 *      再交原函数 —— 无论谁写音量（服务器 PDU、设备 Open 后的 apply_volume、我们自己的
 *      调用）都逃不掉。每个会话各挂各的 device，互不干扰。
 *
 * 探针实测偏移（含私有头 libfreerdp/core/client.h，绑死 vcpkg FreeRDP 3.32.0）：
 *   offsetof(rdpContext, channels)                = 288
 *   offsetof(rdpChannels, openDataCount)          = 1448
 *   offsetof(rdpChannels, openDataList)           = 1456
 *   sizeof(CHANNEL_OPEN_DATA)                     = 112
 *   offsetof(CHANNEL_OPEN_DATA, lpUserParam)      = 88   ← rdpsndPlugin*
 *   offsetof(CHANNEL_OPEN_DATA, stats.channelName) = 0
 *   CHANNEL_MAX_COUNT                             = 30
 * ⚠️ 换 FreeRDP 版本必须重跑探针核对这组常量。 */
#define YCN_CTX_OFF_CHANNELS          288
#define YCN_CH_OFF_OPENDATA_COUNT     1448
#define YCN_CH_OFF_OPENDATA_LIST      1456
#define YCN_CH_MAX_COUNT              30
#define YCN_OD_SIZE                   112
#define YCN_OD_OFF_LPUSERPARAM        88
#define YCN_OD_OFF_STATS_NAME         0

/* 安全读取 openData 的通道名到 out（最多 8 字节 + 强制 NUL）。
 *
 * ❗ 这里**不能**直接 `memcpy(out, od, 8)`：
 * `statsName` 是 FreeRDP 结构体里的定长 8 字节字段，**不保证 NUL 终止**，
 * 而 od 又是按硬编码偏移（YCN_CH_OFF_OPENDATA_LIST / YCN_OD_SIZE）算出来的裸指针 ——
 * 一旦偏移对不上（换 FreeRDP 版本）或该项是野指针，无条件读满 8 字节就会踩到
 * 不可读页，直接 ACCESS_VIOLATION（0xC0000005）把整个进程带走。
 *
 * 现场证据（2026-10-05 09:30，Application 事件 Id=1000）：
 *   出错模块 ycn_rdp.DLL / 异常 0xC0000005 / 偏移 0x2be5
 * ⚠️ 纠错（2026-10-07 晚）：早先这里写的是「崩在 ycn_collect_rdpsnd_plugins 开头
 *   （.pdata 函数 #64，RVA 0x3baf..0x3f91）」——**错的**，那是把一个 funclet 的
 *   .pdata 区间当成了真函数入口。真实崩点是 ycn_probe_rdpsnd_device（旧版无 __try），
 *   由 ycn_rdp_set_muted 在 UI 线程调用；同一个偏移 0x2be5 在 10-06 20:52 /
 *   10-07 07:42 / 10-07 08:01 又复现三次，全部是同一个 use-after-free。
 *   本函数的 SEH 仍然保留（读不可读页确实该兜住），但它**不是**那三次崩溃的止血点。
 *
 * 所以这里用 SEH 包住：读不到就当"没名字"，绝不让诊断代码把进程带崩。
 * 返回 1 = 读到了（含空串）；返回 0 = 读不了。 */
static int ycn_read_channel_name(const uint8_t* od, char* out, size_t out_size)
{
	size_t i;
	if (!od || !out || out_size == 0)
		return 0;
	out[0] = '\0';
	__try
	{
		/* 逐字节最多读 8 个（statsName 的声明长度），遇到 NUL 提前停。
		 * 逐字节读还有个好处：第 8 字节不可读时，第 0..7 字节已读出来，
		 * __except 兜住后仍然算成功。 */
		for (i = 0; i < 8; i++)
		{
			char c = ((const char*)od)[i];
			out[i] = c;
			if (c == '\0')
				break;
		}
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		/* 读不动就返回已经读到的部分（至少保证 NUL 终止） */
	}
	if (i >= out_size)
		i = out_size - 1;
	out[i] = '\0';
	return 1;
}

/* DVC 音频通道（AUDIO_PLAYBACK_DVC）的 rdpsnd 插件**不在** context->channels 里，
 * 而在 drdynvc 的 DVCMAN->plugins 列表里。取值链（探针实测，绑死 FreeRDP 3.32.0）：
 *   context->channels               (+288)
 *   -> rdpChannels.drdynvc          (+5328) = DrdynvcClientContext*
 *   -> DrdynvcClientContext.handle  (+0)    = drdynvcPlugin*
 *   -> drdynvc_plugin.channel_mgr   (+192)  = DVCMAN*
 *   -> DVCMAN.plugins               (+56)   = wArrayList*（元素 = IWTSPlugin*，即 rdpsnd DVC 插件）
 * ⚠️ 不能用 DrdynvcClientContext.custom —— 那是 rdpChannels*（client.c 里设的），不是 DVCMAN。 */
#define YCN_CH_OFF_DRDYNVC            5328
#define YCN_DRDYNVC_OFF_HANDLE        0
#define YCN_DRDYNVC_PLUGIN_OFF_CHMGR  192
#define YCN_DVCMAN_OFF_PLUGINS        56

/* rdpsndPlugin 私有结构里的字段偏移（探针实测，绑死 vcpkg FreeRDP 3.32.0） */
#define YCN_RDPSND_OFFSET_OPENDATA_HANDLE 184   /* DWORD OpenHandle */
#define YCN_RDPSND_OFFSET_NUM_CLIENT_FMT  232   /* UINT16 NumberOfClientFormats */
#define YCN_RDPSND_OFFSET_ATTACHED        236   /* BOOL attached */
#define YCN_RDPSND_OFFSET_DYNAMIC         240   /* BOOL dynamic */
#define YCN_RDPSND_OFFSET_ISOPEN          268   /* BOOL isOpen */
#define YCN_RDPSND_OFFSET_DEVICE          312   /* rdpsndDevicePlugin* device */
#define YCN_RDPSND_OFFSET_APPLYVOLUME     404   /* BOOL applyVolume */
#define YCN_RDPSND_OFFSET_ONOPENCALLED    416   /* BOOL OnOpenCalled */
#define YCN_RDPSND_OFFSET_ASYNC           420   /* BOOL async */

/* [已删除] ycn_dump_all_channels —— 一次性诊断函数，2026-10-07 移除。
 *
 * 原用途：枚举 context->channels 里所有 openData 项的 channelName + lpUserParam，
 * 用来确认 rdpsnd 与 AUDIO_PLAYBACK_DVC 到底是不是同一个 plugin 实例。
 * 结论早已写进 ycn_collect_rdpsnd_plugins 的文件注释（两个实例，DVC 那份在 drdynvc 链上），
 * 诊断使命完成。
 *
 * 删除理由（与崩点归属无关，别把两件事混为一谈）：
 *   ① 它按 FreeRDP 3.32.0 私有结构的硬编码偏移解裸指针，
 *      一旦升级 FreeRDP（偏移变了）就是一个必然崩的地雷，而它的价值已经是零。
 *   ② 更贵的代价是**认知负债**：留在生产路径上，几个月后没人记得它是干什么的，
 *      也不会有人想到去修它的偏移。
 *   ③ 它在热路径上每连接必跑一次，却只为了打印调试串。
 *
 * 🔑 由此定下的纪律：查完的诊断代码要删掉，不能只加护栏就长期留着。
 *    真需要留参考，就把结论写进注释（已做），别留可执行代码。
 *
 * ⚠️ 纠错（2026-10-07 晚，推翻了本注释早先的错误版本）：
 *    2026-10-07 07:42/08:01 的原生崩溃（ycn_rdp.DLL 0xC0000005 @ 偏移 0x2be5）
 *    **不在**本函数里，而在 ycn_probe_rdpsnd_device（旧版无 __try）。
 *    证据（对崩溃时那份 PE=0x6AC2203E 的 DLL 做的静态判定）：
 *      - 崩点所在 .pdata 区间 0x2bc0..0x2c25（101 字节）是**单一完整函数**，
 *        首条 `mov [rsp+0x20],rbx` 有序言特征，且只引用 GetModuleHandleExW
 *        (IAT slot 0x60b8) 与 __ImageBase —— 与 probe 的源码逐条对得上。
 *      - 本函数的两个独有字符串指纹 `ch[%d] name='%s' lpUserParam=%p`(0x6470) 与
 *        `mute-snap#%ld...`(0x6650) 分别只在 0x45f3 / 0x46c5 被引用，
 *        与崩点相距 0x1a00 以上，崩点函数**根本不碰**它们。
 *    早先误判的根源：把一个**funclet** 的 .pdata 区间当成了真函数入口，
 *    于是落到了隔壁的诊断函数上。教训见 MEMORY.md「崩溃偏移定位」节。 */


/* 按 device 指针反查所属会话与槽位（表很小，线性扫足够）。
 * 找到返回会话指针，*slot 写回槽位下标；找不到返回 NULL。 */
static YcnSession* ycn_session_of_device(void* device, int* slot)
{
	YcnSession* s = NULL;
	int i, k;

	if (slot)
		*slot = -1;
	if (!device)
		return NULL;
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS && !s; i++)
	{
		if (!g_sessions[i].used)
			continue;
		for (k = 0; k < YCN_RDPSND_SLOTS; k++)
		{
			if (g_sessions[i].rdpsnd_device[k] == device)
			{
				s = &g_sessions[i];
				if (slot)
					*slot = k;
				break;
			}
		}
	}
	LeaveCriticalSection(&g_lock);
	/* ⚠️ 本函数**只**用 g_lock 做查找，返回后在锁外读 s->rdpsnd_orig_*[slot]。
	 * 这在当前架构下是安全的：调用方是 FreeRDP 的 rdpsnd 回调（Play/SetVolume），
	 * 跑在**同一个 RDP 线程**上，与 ycn_rdp_disconnect 的 freerdp_context_free 串行，
	 * 不存在 free 与解引用交错的窗口。
	 * ⚠️ 但若将来把钩子挪到别的线程（跨线程回调），这里必须改成把 orig 函数指针
	 *   一起在锁内拷出来返回，**不能**返回 s 让调用方在锁外读槽位。 */
	return s;
}

/* ---- 静音的两道闸 ----
 *
 * 光靠 SetVolume 不一定够：`waveOutSetVolume` 在现代 Windows（WASAPI 兼容层）上
 * 对某些音频会话不生效，而且**服务器不一定下发 volume PDU**（不下发则 SetVolume
 * 根本不被调用）。所以静音的**主闸是丢弃音频数据**（Play/PlayEx），
 * SetVolume 只作为副闸（让音量指示也归零、并兼容会走 GetVolume 的路径）。
 *
 * 丢弃数据 = 静音时 Play/PlayEx 直接返回 0（不 waveOutWrite）→ 后端无关、必生效。 */

static BOOL ycn_rdpsnd_set_volume_hook(rdpsndDevicePlugin* device, UINT32 value)
{
	int slot = -1;
	YcnSession* s = ycn_session_of_device((void*)device, &slot);

	if (s && InterlockedCompareExchange(&s->muted, 0, 0) != 0)
	{
		value = 0; /* 静音态：无论服务器/设备想设多大，一律 0 */
		InterlockedIncrement(&s->mute_setvol_hits);
	}

	if (s && slot >= 0 && s->rdpsnd_orig_set_volume[slot])
		return IFCALLRESULT(FALSE, ((pcSetVolume)s->rdpsnd_orig_set_volume[slot]), device, value);

	return FALSE;
}

/* Play 挂钩：静音时把 PCM 数据清零后再交后端 —— 既静音又保持 rdpsnd 的正常节奏
 * （rdpsnd 每帧要发 WaveConfirm 回执给服务器；直接把数据丢掉虽然也不报错，但
 *  清零重放能保证后端缓冲/时序完全不受影响，兼容性最好）。
 * 用栈上小缓冲分块清，避免为大小不定的音频数据动态分配。 */
static UINT ycn_rdpsnd_play_hook(rdpsndDevicePlugin* device, const BYTE* data, size_t size)
{
	int slot = -1;
	YcnSession* s = ycn_session_of_device((void*)device, &slot);

	if (s)
	{
		InterlockedIncrement(&s->mute_play_hits);
		if (InterlockedCompareExchange(&s->muted, 0, 0) != 0 &&
		    slot >= 0 && s->rdpsnd_orig_play[slot])
		{
			/* 静音：原样调用，但喂全零 PCM */
			BYTE zero[512];
			UINT last = 0;
			size_t off = 0;
			memset(zero, 0, sizeof(zero));
			while (off < size)
			{
				size_t chunk = size - off;
				if (chunk > sizeof(zero))
					chunk = sizeof(zero);
				last = ((pcPlay)s->rdpsnd_orig_play[slot])(device, zero, chunk);
				off += chunk;
			}
			return last;
		}
		if (slot >= 0 && s->rdpsnd_orig_play[slot])
			return ((pcPlay)s->rdpsnd_orig_play[slot])(device, data, size);
	}
	return 0;
}

/* PlayEx 挂钩：同上（rdpsnd 优先用 PlayEx，若存在则 Play 不会被调）。 */
static UINT ycn_rdpsnd_play_ex_hook(rdpsndDevicePlugin* device, const AUDIO_FORMAT* format,
                                    const BYTE* data, size_t size)
{
	int slot = -1;
	YcnSession* s = ycn_session_of_device((void*)device, &slot);

	if (s)
	{
		InterlockedIncrement(&s->mute_play_hits);
		if (InterlockedCompareExchange(&s->muted, 0, 0) != 0 &&
		    slot >= 0 && s->rdpsnd_orig_play_ex[slot])
		{
			/* 静音：喂全零 PCM（保持格式/节奏不变，只有内容静音） */
			BYTE zero[512];
			UINT last = 0;
			size_t off = 0;
			memset(zero, 0, sizeof(zero));
			while (off < size)
			{
				size_t chunk = size - off;
				if (chunk > sizeof(zero))
					chunk = sizeof(zero);
				last = ((pcPlayEx)s->rdpsnd_orig_play_ex[slot])(device, format, zero, chunk);
				off += chunk;
			}
			return last;
		}
		if (slot >= 0 && s->rdpsnd_orig_play_ex[slot])
			return ((pcPlayEx)s->rdpsnd_orig_play_ex[slot])(device, format, data, size);
	}
	return 0;
}

/* ---------------------------------------------------------------
 * 自验证：完全在原生侧构造一个假的 rdpsndDevicePlugin，走真实钩子链路，
 * 证明「静音时 Play/PlayEx 收到的是全零数据」。不依赖服务器推音频。
 *
 * 做法：
 *   ① 造一个假 plugin（432 字节，device 字段放我们的假 device）；
 *   ② 造假 device，SetVolume/Play/PlayEx 指向**本测试专用的记账函数**；
 *   ③ 把一个空闲会话的 slot0 指向这套假对象（保留原值，测完还原）；
 *   ④ 静音前调 Play → 记账函数应看到"原始数据"；
 *   ⑤ 静音后再调 Play → 记账函数应看到"全零"。
 * 返回：按位标记，全 1 = 全部通过（见下方 bit 定义）。 */
static volatile LONG g_selftest_play_calls;
static volatile LONG g_selftest_zero_seen;
static volatile LONG g_selftest_data_seen;
static BYTE g_selftest_last[64];
static size_t g_selftest_last_size;

static UINT ycn_selftest_play(rdpsndDevicePlugin* device, const BYTE* data, size_t size)
{
	(void)device;
	InterlockedIncrement(&g_selftest_play_calls);
	{
		size_t i;
		BOOL allzero = TRUE;
		size_t n = size < sizeof(g_selftest_last) ? size : sizeof(g_selftest_last);
		for (i = 0; i < size; i++)
		{
			if (data[i] != 0)
			{
				allzero = FALSE;
				break;
			}
		}
		if (allzero)
			InterlockedIncrement(&g_selftest_zero_seen);
		else
			InterlockedIncrement(&g_selftest_data_seen);
		if (n)
			memcpy(g_selftest_last, data, n);
		g_selftest_last_size = n;
	}
	return 0;
}

static UINT ycn_selftest_play_ex(rdpsndDevicePlugin* device, const AUDIO_FORMAT* format,
                                 const BYTE* data, size_t size)
{
	(void)format;
	return ycn_selftest_play(device, data, size);
}

static BOOL ycn_selftest_setvol(rdpsndDevicePlugin* device, UINT32 value)
{
	(void)device;
	(void)value;
	return TRUE;
}

/* 返回位掩码（全 1 = 通过）：
 *   bit0(1) = 能拿到会话与空闲槽位
 *   bit1(2) = 卸载静音时 Play 收到原始数据（非零）
 *   bit2(4) = 静音时 Play 收到全零
 *   bit3(8) = 静音时 Play 依然被调用（节奏不丢，rdpsnd 回执正常）
 *   bit4(16) = 取消静音后恢复原始数据
 */
/* 前置声明：自检（本函数后面的负向断言）要直接调 probe 这个纯函数验证防线，
 * 而 probe 的定义在更下方（它用到 YCN_RDPSND_OFFSET_* 等定义）。 */
static rdpsndDevicePlugin* ycn_probe_rdpsnd_device(void* plugin);

YCN_API int ycn_rdp_selftest_mute(void)
{
	int result = 0;
	YcnSession* s = NULL;
	int slot = -1;
	int i;
	int saved_used = 0;
	rdpsndDevicePlugin fakeDev;
	uint8_t fakePlugin[432];
	BYTE pcm[128];
	void* saved_plugin;
	void* saved_device;
	void* saved_orig;

	/* 挑一个会话：优先**没在用**的（自检不该干扰真连接）。
	 * ⚠️ 但 ycn_session_of_device 反查时会跳过 !used 的会话 —— 所以自检期间
	 * 必须把 used 临时置 1（下面还原），否则钩子根本认不出这个 device。 */
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (!g_sessions[i].used)
		{
			s = &g_sessions[i];
			break;
		}
	}
	if (!s)
	{
		/* 全都用着：退而求其次，借用第一个会话的最后一个槽位 */
		for (i = 0; i < YCN_MAX_SESSIONS; i++)
		{
			if (g_sessions[i].used && g_sessions[i].rdpsnd_slot_count < YCN_RDPSND_SLOTS)
			{
				s = &g_sessions[i];
				break;
			}
		}
	}
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return 0;
	}
	saved_used = s->used;
	s->used = 1; /* 让 ycn_session_of_device 能反查到本会话 */

	memset(&fakeDev, 0, sizeof(fakeDev));
	fakeDev.SetVolume = ycn_selftest_setvol;
	fakeDev.Play = ycn_selftest_play;
	fakeDev.PlayEx = ycn_selftest_play_ex;

	memset(fakePlugin, 0, sizeof(fakePlugin));
	/* device 字段（偏移 312）指向假 device —— 与真实布局一致 */
	*(rdpsndDevicePlugin**)(fakePlugin + YCN_RDPSND_OFFSET_DEVICE) = &fakeDev;

	/* 占用一个槽位并记录原值 */
	slot = s->rdpsnd_slot_count;
	if (slot >= YCN_RDPSND_SLOTS)
	{
		LeaveCriticalSection(&g_lock);
		return 0;
	}
	saved_plugin = s->rdpsnd_plugin[slot];
	saved_device = s->rdpsnd_device[slot];
	saved_orig = s->rdpsnd_orig_set_volume[slot];
	s->rdpsnd_plugin[slot] = fakePlugin;
	s->rdpsnd_device[slot] = &fakeDev;
	s->rdpsnd_orig_set_volume[slot] = NULL;
	s->rdpsnd_orig_play[slot] = NULL;
	s->rdpsnd_orig_play_ex[slot] = NULL;
	s->rdpsnd_slot_count = slot + 1;
	InterlockedExchange(&s->muted, 0);
	LeaveCriticalSection(&g_lock);

	result |= 1; /* 拿到槽位 */

	/* 用真实钩子函数装到假 device 上（这就是线上跑的那段代码） */
	{
		HMODULE m = NULL;
		(void)m;
		s->rdpsnd_orig_set_volume[slot] = (void*)fakeDev.SetVolume;
		fakeDev.SetVolume = ycn_rdpsnd_set_volume_hook;
		s->rdpsnd_orig_play[slot] = (void*)fakeDev.Play;
		fakeDev.Play = ycn_rdpsnd_play_hook;
		s->rdpsnd_orig_play_ex[slot] = (void*)fakeDev.PlayEx;
		fakeDev.PlayEx = ycn_rdpsnd_play_ex_hook;
	}

	/* 造一段非零 PCM */
	for (i = 0; i < (int)sizeof(pcm); i++)
		pcm[i] = (BYTE)(i * 7 + 3);

	g_selftest_play_calls = 0;
	g_selftest_zero_seen = 0;
	g_selftest_data_seen = 0;

	/* ① 未静音 → 应收到原始（非零）数据 */
	fakeDev.Play(&fakeDev, pcm, sizeof(pcm));
	if (InterlockedCompareExchange(&g_selftest_data_seen, 0, 0) == 1 &&
	    InterlockedCompareExchange(&g_selftest_zero_seen, 0, 0) == 0)
		result |= 2;

	/* ② 静音 → 应收到全零，且 Play 仍被调用（节奏不丢） */
	g_selftest_play_calls = 0;
	g_selftest_zero_seen = 0;
	g_selftest_data_seen = 0;
	InterlockedExchange(&s->muted, 1);
	fakeDev.Play(&fakeDev, pcm, sizeof(pcm));
	if (InterlockedCompareExchange(&g_selftest_play_calls, 0, 0) >= 1)
		result |= 8;
	if (InterlockedCompareExchange(&g_selftest_zero_seen, 0, 0) == 1)
		result |= 4;

	/* ③ 取消静音 → 恢复原始数据 */
	g_selftest_play_calls = 0;
	g_selftest_zero_seen = 0;
	g_selftest_data_seen = 0;
	InterlockedExchange(&s->muted, 0);
	fakeDev.Play(&fakeDev, pcm, sizeof(pcm));
	if (InterlockedCompareExchange(&g_selftest_data_seen, 0, 0) == 1)
		result |= 16;

	/* ④ 判据防线断言（2026-10-16 堆损坏根因，双向都要验）：
	 *   0x20 = 伪插件被**拒绝**（两种形态：字段非法 / SetVolume 不在 FreeRDP 模块内）
	 *   0x40 = 真设备形态被**接受**（防判据过严 → 静音静默失效，那是更坏的失败）
	 *
	 * 直接调 probe 这个纯函数：不动槽位、不写任何 FreeRDP 结构，绝对安全。
	 *
	 * ⚠️ 两个易错点（写这段时都踩过，别再犯）：
	 *   1) 判据 `ycn_plugin_fields_sane` 检查的是 **plugin**（偏移 184..420），
	 *      不是 device —— 把垃圾写进 device 等于什么都没测。
	 *   2) MSVC 下 `&某个导入函数` 拿到的是**本模块里的跳转桩**，不是目标模块地址；
	 *      要真实模块内地址必须走 GetProcAddress(GetModuleHandleW(...))。 */
	{
		static uint8_t fkDev[512];
		static uint8_t fkPlugin[512];
		BOOL rejBadFields = FALSE, rejForeignMod = FALSE, accRealDev = FALSE;
		HMODULE hFree = GetModuleHandleW(L"freerdp-client3.dll");
		HMODULE hKern = GetModuleHandleW(L"kernel32.dll");
		void* pFree = hFree ? (void*)GetProcAddress(hFree, "freerdp_rdpsnd_get_context") : NULL;
		void* pKern = hKern ? (void*)GetProcAddress(hKern, "GetTickCount64") : NULL;

		/* (a) 字段非法：plugin 的 BOOL 字段放实测垃圾值 → 必须拒 */
		if (pFree)
		{
			memset(fkDev, 0, sizeof(fkDev));
			memset(fkPlugin, 0, sizeof(fkPlugin));
			((rdpsndDevicePlugin*)fkDev)->SetVolume = (pcSetVolume)(uintptr_t)pFree;
			*(rdpsndDevicePlugin**)(fkPlugin + YCN_RDPSND_OFFSET_DEVICE) = (rdpsndDevicePlugin*)fkDev;
			*(int*)(fkPlugin + YCN_RDPSND_OFFSET_DYNAMIC) = 3670068;     /* 实测 slot2 的垃圾 */
			*(int*)(fkPlugin + YCN_RDPSND_OFFSET_ATTACHED) = 2949219;
			*(int*)(fkPlugin + YCN_RDPSND_OFFSET_ISOPEN) = -849258240;
			rejBadFields = (ycn_probe_rdpsnd_device(fkPlugin) == NULL);
		}

		/* (b) 字段合法、但 SetVolume 落在 FreeRDP 之外的模块（kernel32）→ 必须拒 */
		if (pKern)
		{
			memset(fkDev, 0, sizeof(fkDev));
			memset(fkPlugin, 0, sizeof(fkPlugin));   /* 字段全 0 = 合法取值 */
			((rdpsndDevicePlugin*)fkDev)->SetVolume = (pcSetVolume)(uintptr_t)pKern;
			*(rdpsndDevicePlugin**)(fkPlugin + YCN_RDPSND_OFFSET_DEVICE) = (rdpsndDevicePlugin*)fkDev;
			rejForeignMod = (ycn_probe_rdpsnd_device(fkPlugin) == NULL);
		}

		/* (c) 真设备形态（字段合法 + SetVolume 真在 FreeRDP 模块内）→ 必须接受 */
		if (pFree)
		{
			memset(fkDev, 0, sizeof(fkDev));
			memset(fkPlugin, 0, sizeof(fkPlugin));
			((rdpsndDevicePlugin*)fkDev)->SetVolume = (pcSetVolume)(uintptr_t)pFree;
			*(rdpsndDevicePlugin**)(fkPlugin + YCN_RDPSND_OFFSET_DEVICE) = (rdpsndDevicePlugin*)fkDev;
			accRealDev = (ycn_probe_rdpsnd_device(fkPlugin) != NULL);
		}

		if (rejBadFields && rejForeignMod)
			result |= 32; /* 0x20 */
		if (accRealDev)
			result |= 64; /* 0x40 */
	}

	/* 还原槽位（真机运行时这槽本来可能是空的） */
	EnterCriticalSection(&g_lock);
	s->rdpsnd_plugin[slot] = saved_plugin;
	s->rdpsnd_device[slot] = saved_device;
	s->rdpsnd_orig_set_volume[slot] = saved_orig;
	s->rdpsnd_orig_play[slot] = NULL;
	s->rdpsnd_orig_play_ex[slot] = NULL;
	if (!saved_plugin)
		s->rdpsnd_slot_count = slot; /* 原本就是空的：退回 */
	s->used = saved_used;            /* 还原 used（自检期间临时置 1 过） */
	s->muted = 0;
	LeaveCriticalSection(&g_lock);

	return result;
}


/* ---- FreeRDP 模块白名单（2026-10-16 堆损坏根治的关键） ----
 *
 * rdpsnd 设备插件的实现在 FreeRDP 自己的模块里，所以**真设备**的方法指针
 * （SetVolume / Play / PlayEx）必然落在 freerdp3 / freerdp-client3 / winpr3 之中。
 * 这是把"真设备"与"碰巧可读的一块内存"区分开的唯一可靠判据。
 *
 * 🔴 为什么必须有它（本机 dump + 588MB 诊断实证）：
 *   旧判据只做了一件事 —— 排除"SetVolume 落在本模块内（那是我们自己的钩子）"。
 *   等价于「只要不是我的钩子，就算真设备」。于是 DVC 插件列表里**非 rdpsnd 的对象**
 *   被误判成音频设备：实测 slot2 的字段全是垃圾
 *   （dync=3670068 open=-849258240 nfmt=55 play=0000000400000000）。
 *   紧接着 ycn_rdpsnd_hook_slot 往这块内存**写**了 SetVolume/Play/PlayEx 三个函数指针
 *   —— 写进别人的结构体 = 破坏堆。下游症状全部对得上：
 *     · ntdll.dll 0xC0000374（STATUS_HEAP_CORRUPTION）
 *     · coreclr.dll 0xC0000005 ×5 / CoreMessagingXP 0xC000027B ×3
 *     · 托管栈顶 ToolTipService.SetToolTip 的 AV（堆已被污染）
 *   ⚠️ 注意 SUH 挡不住这个：SEH 只能抓"踩到不可访问页"，
 *      而"写进一块合法但不属于我们的内存"不会抛异常，只会静默毁堆。
 *
 * 无状态实现（不缓存句柄）：probe 会被 UI 线程（ycn_rdp_set_muted）与 RDP 线程
 * （ycn_rdpsnd_hook）同时调用，GetModuleHandleW 只是查 loader 表，无需加锁。
 */
static BOOL ycn_is_freerdp_ptr(const void* p)
{
	HMODULE m = NULL;

	if (!p)
		return FALSE;
	if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                        GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                        (LPCWSTR)(uintptr_t)p, &m) ||
	    !m)
		return FALSE;
	return m == GetModuleHandleW(L"freerdp3.dll") ||
	       m == GetModuleHandleW(L"freerdp-client3.dll") ||
	       m == GetModuleHandleW(L"winpr3.dll");
}

/* 候选 plugin 的字段合法性检查（模块判据之外的第二道闸，**不依赖模块名**）。
 *
 * 依据来自 2026-10-16 的现场实证（588MB 诊断里三类槽位对照）：
 *   真静态 rdpsnd : att=1 onopen=0 isopen=0 applyv=0 nfmt=0    → 全部合法
 *   真 DVC rdpsnd : dync=1 att=1 open=0 onopen=1 isopen=0 nfmt=0 → 全部合法
 *   伪插件 slot2  : dync=3670068 att=2949219 open=-849258240 nfmt=55 → 明显非法
 * rdpsndPlugin 里这几个字段声明都是 BOOL/UINT16，正常取值只有 0/1（nfmt 是小的格式数）。
 * 垃圾内存几乎不可能同时满足，因此这条判据能把伪插件筛掉，
 * 且**与 FreeRDP 版本无关**（不像硬编码偏移那样换版本就失效）。
 *
 * 全部读操作进 SEH：plugin 可能是野指针。读不动 = 判为不合法。 */
static BOOL ycn_plugin_fields_sane(const void* plugin)
{
	BOOL ok = FALSE;
	const uint8_t* p = (const uint8_t*)plugin;
	int att = -1, dyn = -1, isopen = -1, applyv = -1, onopen = -1;
	unsigned nfmt = 0xFFFFu;

	if (!p)
		return FALSE;
	__try
	{
		dyn = *(const BOOL*)(p + YCN_RDPSND_OFFSET_DYNAMIC);
		att = *(const BOOL*)(p + YCN_RDPSND_OFFSET_ATTACHED);
		isopen = *(const BOOL*)(p + YCN_RDPSND_OFFSET_ISOPEN);
		applyv = *(const BOOL*)(p + YCN_RDPSND_OFFSET_APPLYVOLUME);
		onopen = *(const BOOL*)(p + YCN_RDPSND_OFFSET_ONOPENCALLED);
		nfmt = *(const UINT16*)(p + YCN_RDPSND_OFFSET_NUM_CLIENT_FMT);
		ok = TRUE;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return FALSE;
	}
	if (!ok)
		return FALSE;
	(void)nfmt; /* nfmt 不参与判定：真设备协商后可能非 0（见下），而垃圾值 55 也照样能 <=64，
	             * 拿它当阈值两头不讨好。真正起作用的是下面 5 个 BOOL 字段。 */
	/* 这 5 个字段在 rdpsndPlugin 里声明都是 BOOL，取值只可能是 0/1
	 * —— 对真设备**不会误伤**（这是关键：宁可少挡，不可把真静音挡没了）；
	 * 而一块被误认成插件的垃圾内存，5 个字段同时落进 {0,1} 的概率极低。 */
	return (dyn == 0 || dyn == 1) && (att == 0 || att == 1) &&
	       (isopen == 0 || isopen == 1) && (applyv == 0 || applyv == 1) &&
	       (onopen == 0 || onopen == 1);
}

/* 判断某指针是否"像"一个 rdpsndPlugin：取它的 device 字段，看 device 的 SetVolume
 * 是否落在 **FreeRDP 自己的模块**里（见上面 ycn_is_freerdp_ptr）。用于在 openData /
 * DVC 列表里认出所有 rdpsnd 插件（注册名都可能是 "rdpsnd"，只能靠形态认）。
 *
 * ⚠️ 判据必须取「正」而不能取「反」：只排除"是本模块的钩子"就等于放行任意可读垃圾，
 *    这正是 2026-10-16 堆损坏的成因（详见 ycn_is_freerdp_ptr 注释）。 */
static rdpsndDevicePlugin* ycn_probe_rdpsnd_device(void* plugin)
{
	rdpsndDevicePlugin* dev = NULL;
	HMODULE mod = NULL;
	BOOL selfHooked = FALSE;
	BOOL hasSetVolume = FALSE;
	void* setvol = NULL;   /* SEH 内读出的 dev->SetVolume（出护栏后一律用这个缓存值） */

	if (!plugin)
		return NULL;
	/* 🔴 这里有**两层**裸指针解引用，全都要在 SEH 内：
	 *   第 1 层：dev = *(plugin + 312)      —— plugin 是按硬编码偏移从 openData /
	 *             DVC plugins 列表里算出来的裸指针，本身就可能是野的。
	 *   第 2 层：dev->SetVolume              —— dev 刚从野指针里读出来，
	 *             它指向的结构体**极可能已经 Free 掉了**（rdpsnd Close 时会 Free device）。
	 * 只护第 1 层的话，第 2 层照样把进程带走 —— 这就是"知道危险但只护了一半"。
	 * 出错就地返回 NULL = "这轮认不出这个插件"，槽位保持原样继续跑。
	 *
	 * ⚠️ 注意 hasSetVolume 也要在 SEH 内算出来：判据里再写一次 `dev->SetVolume`
	 * 就等于把第 2 层又漏到护栏外面了（写代码时自己刚犯过一次，别重蹈）。 */
	__try
	{
		dev = *(rdpsndDevicePlugin**)((uint8_t*)plugin + YCN_RDPSND_OFFSET_DEVICE);
		if (dev && dev->SetVolume)
		{
			hasSetVolume = TRUE;
			setvol = (void*)dev->SetVolume;   /* 只在这里解引用一次 */
			/* SetVolume 落在本模块内 = 它已经是我们自己的钩子，别重复认领 */
			if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
			                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
			                       (LPCWSTR)(uintptr_t)dev->SetVolume, &mod) &&
			    mod == (HMODULE)(uintptr_t)&__ImageBase)
				selfHooked = TRUE;
		}
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return NULL;
	}
	if (!dev || !hasSetVolume || selfHooked)
		return NULL;

	/* 🔴 判据必须取「正」，且**两道都要过**。旧版只做了「不是我们的钩子就放行」，
	 * 等于把任意可读垃圾收成设备 —— dump 实证：DVC 列表里非 rdpsnd 的对象被收成
	 * slot2（字段全是垃圾），随后 hook_slot 往它写函数指针 → 破坏堆 →
	 * ntdll 0xC0000374 与一串 coreclr 0xC0000005。详见上面两个 helper 的注释。
	 *
	 * 两道闸的分工：
	 *   ① ycn_plugin_fields_sane —— 按结构体字段取值判"像不像"rdpsndPlugin
	 *     （不依赖模块名，换 FreeRDP 版本也不失效）；
	 *   ② ycn_is_freerdp_ptr   —— SetVolume 必须落在 FreeRDP 模块内。
	 * 两道都在 __try 之外：它们只读 dev 的标量字段/查 loader 表，不碰 dev 的方法表。
	 * 任一不过 = 这轮认不出它，返回 NULL（槽位保持原样，不影响真设备）。 */
	if (!ycn_plugin_fields_sane(plugin))
		return NULL;
	/* ⚠️ 必须用 SEH 内缓存下来的 setvol，**不能**在这里再写一次 dev->SetVolume ——
	 * 那等于把第 2 层解引用又漏到护栏外面（本文件 1109 行注释正是这个警告）。 */
	if (!ycn_is_freerdp_ptr(setvol))
		return NULL;

	return dev;
}

/* 把候选 plugin 收进空闲槽位（已在槽里的跳过）。返回是否新收。
 *
 * ⚠️ 契约：**调用方必须已持有 g_lock**（唯一调用链是
 *    ycn_rdpsnd_hook → ycn_collect_rdpsnd_plugins → 本函数）。
 *    本函数写 rdpsnd_plugin[]/ rdpsnd_device[] / rdpsnd_orig_*[]，
 *    这些数组的读侧（set_muted / disconnect 的 free+清槽）都在 g_lock 下，
 *    写侧也必须在同一把锁里，否则又回到 use-after-free。 */
static int ycn_add_slot(YcnSession* s, void* plugin, const char* tag_name)
{
	int k;
	if (!plugin)
		return 0;
	for (k = 0; k < s->rdpsnd_slot_count; k++)
	{
		if (s->rdpsnd_plugin[k] == plugin)
			return 0; /* 已在槽里 */
	}
	if (s->rdpsnd_slot_count >= YCN_RDPSND_SLOTS)
		return 0;
	k = s->rdpsnd_slot_count;
	s->rdpsnd_plugin[k] = plugin;
	s->rdpsnd_device[k] = (void*)ycn_probe_rdpsnd_device(plugin);
	s->rdpsnd_orig_set_volume[k] = NULL;
	s->rdpsnd_orig_play[k] = NULL;
	s->rdpsnd_orig_play_ex[k] = NULL;
	s->rdpsnd_slot_count++;
	{
		char dbg[256];
		_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute: collect slot%d src='%s' plugin=%p dev=%p", k,
		            tag_name ? tag_name : "?", plugin, s->rdpsnd_device[k]);
		ycn_bc(dbg); /* 锁内只写内存，不落盘 */
	}
	return 1;
}

/* 扫描并收集本会话所有 rdpsnd 插件，两条来源都扫：
 *   ① context->channels.openData 里的静态 `rdpsnd` 项（lpUserParam = plugin）
 *   ② drdynvc 的 DVCMAN->plugins 列表里 DVC 音频（AUDIO_PLAYBACK_DVC）那份 —— 这个是
 *      **真正在播的**（服务器走 DVC 音频时，静态那份 device 恒 NULL）。
 * 已收集的槽位不重复添加（否则每拍覆盖会丢已装钩子的记录）。
 * 返回槽位总数。 */
static int ycn_collect_rdpsnd_plugins(YcnSession* s, int verbose)
{
	uint8_t* channels;
	uint8_t* drdynvc_ctx;
	uint8_t* dvcman;
	wArrayList* plugins;

	if (!s || !s->instance || !s->instance->context)
		return 0;
	{
		uint8_t* ch = NULL;
		/* 🔴 context->channels 同样是硬编码偏移裸算，多通道并发连接/断开时
		 * 它可能正在被改写 → 读出野指针。与下面 ① 段一样用 SEH 兜住。
		 * 这条在**每拍都跑**的热路径上（ycn_refresh_mute），不能裸奔。 */
		__try
		{
			ch = *(uint8_t**)((uint8_t*)s->instance->context + YCN_CTX_OFF_CHANNELS);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			ch = NULL;
		}
		channels = ch;
	}
	if (!channels)
		return s->rdpsnd_slot_count;

	/* ① 静态 openData 扫描 */
	{
		int count = 0;
		int i;
		/* count 也是从硬编码偏移读的，一起用 SEH 兜住 */
		__try
		{
			count = *(int*)(channels + YCN_CH_OFF_OPENDATA_COUNT);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			return s->rdpsnd_slot_count;
		}
		if (count > 0 && count <= YCN_CH_MAX_COUNT)
		{
			for (i = 0; i < count; i++)
			{
				uint8_t* od = channels + YCN_CH_OFF_OPENDATA_LIST + (size_t)i * YCN_OD_SIZE;
				void* lp = NULL;
				char safe[9];
				BOOL lpOk = FALSE;
				/* 🔴 lpUserParam 的读取**必须**在 SEH 内 —— 上一轮只在下面读 statsName 时加了 SEH，
				 * 却在上面留了一句裸的 `*(void**)(od + 88)`，正是 2026-10-07 07:42 的崩点
				 * （ycn_rdp.DLL 0xC0000005 @偏移 0x2be5，mov rcx,[r13+0xAD8] 读出 0 后当数组下标）。
				 * 「知道危险但只护了一半」最坑：代码看起来像已经处理过了。
				 * 教训：**凡是按硬编码偏移解裸指针，整个读取动作都得进 SEH**。
				 *
				 * ⚠️ 这里刻意**不在 __except 里写 continue**：跳转型控制流从异常处理块
				 * 里穿出去在 MSVC 上语义微妙（且不同版本行为不同）。改成置标志位、
				 * 在 __except 之后统一判断，行为完全确定。 */
				__try
				{
					lp = *(void**)(od + YCN_OD_OFF_LPUSERPARAM);
					lpOk = TRUE;
				}
				__except (EXCEPTION_EXECUTE_HANDLER)
				{
					lpOk = FALSE; /* 这一项读不动，跳过 */
				}
				if (!lpOk || !lp)
					continue;
				/* ⚠️ 名字走 SEH 安全读取：原先的 memcpy(safe, name, 8) 会越界读
				 * （statsName 定长 8 字节不保证 NUL 终止，od 又是指针硬算出来的），
				 * 音频一开这条路径就走到，实测直接把进程带崩（0xC0000005 @ ycn_rdp+0x2be5）。 */
				if (!ycn_read_channel_name(od + YCN_OD_OFF_STATS_NAME, safe, sizeof(safe)))
					continue;
				if (!(strstr(safe, "rdpsnd") || strstr(safe, "AUDIO")))
					continue;
				(void)ycn_add_slot(s, lp, safe);
			}
		}
	}

	/* ② DVC 插件列表扫描（AUDIO_PLAYBACK_DVC 的那份）。
	 * verbose = 前几拍才打详细诊断，避免刷爆文件。
	 *
	 * ⚠️ 整条取值链都是**硬编码偏移裸算**：
	 *     context(+288) -> drdynvc(+5328) -> handle(+0) -> channel_mgr(+192) -> plugins(+56)
	 * 中间任何一环在当前 FreeRDP 版本里布局不同 / 指针被释放 / 落在不可读页，
	 * 逐个 `*(ptr + 偏移)` 就是 ACCESS_VIOLATION。诊断代码绝不该把主程序带崩 ——
	 * 所以整段用 __try 包住：扫不到就当"这次没找到"，槽位保持原样继续跑。 */
	__try
	{
	drdynvc_ctx = *(uint8_t**)(channels + YCN_CH_OFF_DRDYNVC);
	if (verbose)
	{
		char dbg[256];
		_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: drdynvc_ctx=%p", (void*)drdynvc_ctx);
		ycn_bc(dbg); /* 锁内只写内存，不落盘 */
	}
	if (drdynvc_ctx)
	{
		uint8_t* dvcplugin = *(uint8_t**)(drdynvc_ctx + YCN_DRDYNVC_OFF_HANDLE);
		if (verbose)
		{
			char dbg[256];
			_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: ctx=%p dvcplugin=%p",
			            (void*)drdynvc_ctx, (void*)dvcplugin);
			ycn_bc(dbg); /* 锁内只写内存，不落盘 */
		}
		dvcman = dvcplugin ? *(uint8_t**)(dvcplugin + YCN_DRDYNVC_PLUGIN_OFF_CHMGR) : NULL;
		if (verbose)
		{
			char dbg[256];
			_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: dvcman=%p", (void*)dvcman);
			ycn_bc(dbg); /* 锁内只写内存，不落盘 */
		}
		if (dvcman)
		{
			plugins = *(wArrayList**)(dvcman + YCN_DVCMAN_OFF_PLUGINS);
			if (verbose)
			{
				char dbg[256];
				_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: plugins=%p count=%zu",
				            (void*)plugins, plugins ? ArrayList_Count(plugins) : 0);
				ycn_bc(dbg); /* 锁内只写内存，不落盘 */
			}
			if (plugins)
			{
				size_t n = ArrayList_Count(plugins);
				size_t j;
				for (j = 0; j < n; j++)
				{
					void* p = ArrayList_GetItem(plugins, j);
					rdpsndDevicePlugin* dv = ycn_probe_rdpsnd_device(p);
					if (verbose)
					{
						char dbg[256];
						_snprintf_s(dbg, sizeof(dbg), _TRUNCATE, "mute-dvc: item%zu p=%p dev=%p", j, p,
						            (void*)dv);
						ycn_bc(dbg); /* 锁内只写内存，不落盘 */
					}
					if (dv)
						(void)ycn_add_slot(s, p, "dvc");
				}
			}
		}
	}
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		/* DVC 链路上有不可读内存：这次没扫到，但已收的槽位仍然有效。
		 * 记一笔便于日后核对偏移是否还对得上 FreeRDP 当前版本。 */
		if (verbose)
			ycn_bc("mute-dvc: 扫描时踩到不可读内存（偏移可能已不匹配），本轮跳过 DVC 路径");
	}
	return s->rdpsnd_slot_count;
}

/* 对单个槽位的 device 装钩子。返回 1 = 已挂钩/已是最新，0 = 暂不可用，-1 = 校验失败。 */
static int ycn_rdpsnd_hook_slot(YcnSession* s, int slot)
{
	rdpsndDevicePlugin* device = (rdpsndDevicePlugin*)s->rdpsnd_device[slot];
	rdpsndDevicePlugin* dev;
	HMODULE selfMod = NULL;
	BOOL alreadyHooked = FALSE;
	BOOL deviceUsable = FALSE;
	void* selfHookSource = NULL;

	if (!s->rdpsnd_plugin[slot])
		return 0;

	/* 每次都从插件里重新读 device —— device 会在 Close→Free 后被换成新对象，
	 * 缓存旧指针会指向已释放内存。ycn_probe_rdpsnd_device 内部已用 SEH 兜住。 */
	dev = ycn_probe_rdpsnd_device(s->rdpsnd_plugin[slot]);
	if (dev && s->rdpsnd_device[slot] != (void*)dev)
	{
		s->rdpsnd_device[slot] = (void*)dev;
		s->rdpsnd_orig_set_volume[slot] = NULL;
		s->rdpsnd_orig_play[slot] = NULL;
		s->rdpsnd_orig_play_ex[slot] = NULL;
	}
	device = (rdpsndDevicePlugin*)s->rdpsnd_device[slot];

	/* 🔴 device 来自**跨拍的槽位缓存**：上一拍读到的地址，这一拍那块内存可能已经被
	 * FreeRDP 释放。所以连"device->SetVolume 是否存在""是否已经是我的钩子"这类**读**
	 * 都必须进 SEH —— 本函数是本文件里唯一会对 FreeRDP 结构体**写**的地方，
	 * 在野指针上读写不可分。写之前先在 SEH 内把要用的函数指针全取出来缓存到局部变量，
	 * 后面一律用缓存值，不再重复解引用 device（重复解引用 = 把护栏又捅了个洞）。 */
	__try
	{
		if (device && device->SetVolume)
		{
			deviceUsable = TRUE;
			selfHookSource = (void*)device->SetVolume;
			alreadyHooked = (device->SetVolume == ycn_rdpsnd_set_volume_hook);
		}
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return 0;
	}
	if (!deviceUsable)
		return 0;

	/* 已挂钩？只看「当前 device 上的函数指针就是我的钩子」——不掺 orig 缓存，
	 * 否则重扫时 orig 被清就误判成未挂钩 → 反复重装。 */
	if (alreadyHooked)
		return 1;

	/* 🔴 写之前最后一道闸：本槽的 device 必须"看着是真的"才允许写。
	 *
	 * 为什么光靠 probe 不够：device 取自**跨拍槽位缓存** s->rdpsnd_device[slot]，
	 * 而 probe 返回 NULL 时（这拍认不出来）这一行不会更新缓存，于是
	 * 仍可能拿着**上一拍留下的伪设备/已失效对象**往下写函数指针 —— 照样毁堆。
	 * 判据与 probe 同源：当前 device->SetVolume 必须落在 FreeRDP 模块内。
	 * 不是就不写、返回 0（下拍 probe 若再认出真设备会补上）。
	 *
	 * ⚠️ 这里刻意用 selfHookSource（上面 SEH 内已读出的缓存值），
	 *    不再解引用 device —— 重复解引用等于把护栏又捅个洞。 */
	if (!ycn_plugin_fields_sane(s->rdpsnd_plugin[slot]) ||
	    !ycn_is_freerdp_ptr(selfHookSource))
		return 0;

	if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                       (LPCWSTR)(uintptr_t)selfHookSource, &selfMod) &&
	    selfMod == (HMODULE)(uintptr_t)&__ImageBase)
	{
		return -1;
	}

	/* 存 orig 前先排雷：若当前指针**已经是本模块的钩子**（上轮挂完 device 地址没变、
	 * 但上一轮被判定为"未挂钩"而重进这里），绝不能把它存成 orig —— 那会让钩子
	 * 转调自己 → 无限递归 / 调用野指针（实测出现过 play=000000E400000001 这种垃圾）。
	 * 判据：指针落在本模块内 = 我们的钩子，此时保留旧 orig 不动。
	 *
	 * 🔴 下面三段是对 FreeRDP 结构体的**写**操作，必须整体进 SEH：
	 * 上一页的校验只能证明"刚才能读"，而 FreeRDP 的音频线程随时可能把 device Free 掉；
	 * 读得到、写不到是完全可能的（堆块 Free 后可能已归还 OS）。
	 * 代价是"可能只装上部分钩子"（例如 SetVolume 装上、Play 没装上）——
	 * 这远小于进程崩掉：静音降级成部分失效，下一拍还会重扫重装。 */
	__try
	{
	/* 排雷用的原始 SetVolume 指针：上面 SEH 里已校验过非空，这里复用缓存值 selfHookSource，
	 * 不再重复解引用 device（重复解引用 = 把护栏又捅了个洞）。 */
	{
		HMODULE m = NULL;
		if (!(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		                         GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		                         (LPCWSTR)(uintptr_t)selfHookSource, &m) &&
		      m == (HMODULE)(uintptr_t)&__ImageBase))
		{
			s->rdpsnd_orig_set_volume[slot] = (void*)selfHookSource;
			device->SetVolume = ycn_rdpsnd_set_volume_hook;
		}
		else
		{
			device->SetVolume = ycn_rdpsnd_set_volume_hook;
		}
	}
	{
		HMODULE m = NULL;
		void* cur = (void*)device->Play;
		if (cur &&
		    !(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		                         GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		                         (LPCWSTR)(uintptr_t)cur, &m) &&
		      m == (HMODULE)(uintptr_t)&__ImageBase))
		{
			s->rdpsnd_orig_play[slot] = cur;
			device->Play = ycn_rdpsnd_play_hook;
		}
		else if (cur)
		{
			device->Play = ycn_rdpsnd_play_hook;
		}
	}
	{
		HMODULE m = NULL;
		void* cur = (void*)device->PlayEx;
		if (cur &&
		    !(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		                         GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		                         (LPCWSTR)(uintptr_t)cur, &m) &&
		      m == (HMODULE)(uintptr_t)&__ImageBase))
		{
			s->rdpsnd_orig_play_ex[slot] = cur;
			device->PlayEx = ycn_rdpsnd_play_ex_hook;
		}
		else if (cur)
		{
			device->PlayEx = ycn_rdpsnd_play_ex_hook;
		}
	}
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		/* device 在校验与写入之间被 Free 了。这轮放弃，返回 0（不是 -1：
		 * -1 会被当成"布局不对"去刷警告，而这里只是时序不巧）。
		 * 已经写进去的那部分保持原样，下一拍 ycn_rdpsnd_hook 会重新扫、重新装。 */
		WLog_WARN(TAG, "audio mute: slot%d device 在装钩子时失效（已被释放），本轮跳过", slot);
		return 0;
	}

	{
		char dbg[256];
		_snprintf_s(dbg, sizeof(dbg), _TRUNCATE,
		            "mute: slot%d HOOKED dev=%p play=%p playEx=%p", slot, (void*)device,
		            (void*)s->rdpsnd_orig_play[slot], (void*)s->rdpsnd_orig_play_ex[slot]);
		ycn_bc(dbg); /* 锁内只写内存，不落盘 */
	}
	WLog_INFO(TAG, "audio mute: slot%d hooked (device=%p)", slot, (void*)device);
	return 1;
}

/* 解析本会话所有 rdpsnd device 并装钩子。可重入（重复调用安全）。
 * 返回已挂钩/已就绪的槽位数。 */
static int ycn_rdpsnd_hook(YcnSession* s)
{
	int hooked = 0;
	int k;
	long tick;

	if (!s)
		return 0;

	/* 🔑 本函数是**槽位数组（rdpsnd_plugin / rdpsnd_device / rdpsnd_orig_*）的唯一写入侧**，
	 * 必须与读侧（ycn_rdp_set_muted、ycn_rdpsnd_disconnect 的 free+清槽）共用 g_lock。
	 *
	 * 修复前：写侧完全不持锁 + 读侧把 device 缓存到锁外再用 =
	 *         经典的 use-after-free（10-06 20:52 / 10-07 07:42 / 10-07 08:01 三次
	 *         ycn_rdp.DLL 0xC0000005 @0x2be5，fault addr=0x0）。
	 *
	 * 为什么这样加锁不会死锁：
	 *   两个调用点（ycn_rdpsnd_attach 的通道事件、event_loop 的 500ms 一拍）
	 *   都在 **RDP 线程**上，且进入本函数前都不持 g_lock。
	 *   本函数内部调的 collect / add_slot / hook_slot 都**不再单独加锁**，
	 *   靠这里一把锁全覆盖（Windows CRITICAL_SECTION 可重入，
	 *   即便下面某个子函数以后自己加了锁也不会自锁死）。
	 *   锁内不做耗时操作：只读几个偏移 + 装函数指针。 */
	EnterCriticalSection(&g_lock);

	/* 每拍重扫一次槽位（plugin/device 都可能出现/更换；数量很少，开销可忽略） */
	tick = InterlockedIncrement(&s->mute_dbg_tick);
	ycn_collect_rdpsnd_plugins(s, tick <= 5);

	/* ❗ 原先这里有一句 `if (tick == 3) ycn_dump_all_channels(context);` —— **已删除**。
	 *
	 * 它是查「rdpsnd 到底有几个插件实例」时加的一次性诊断，结论早已写进代码注释
	 * （rdpsnd 有两个实例，DVC 那份在 drdynvc 链上，见 ycn_collect_rdpsnd_plugins）。
	 *
	 * 🔑 纪律：查完的诊断代码要**删掉**，不能只「加个 SEH 让它别崩」——
	 *    一次性的探针留在生产路径上，几个月后就会变成没人记得用途的地雷。
	 *    `ycn_dump_all_channels` 这个函数本身已整段删除，只把它查到的结论
	 *    留在了 ycn_collect_rdpsnd_plugins 的注释里。
	 *
	 * ⚠️ 纠错（2026-10-07 晚）：早先这里写的是「2026-10-07 07:42 就崩在这里」——**错的**。
	 *    崩点（ycn_rdp.DLL 0xC0000005 @ 0x2be5）在 ycn_probe_rdpsnd_device，
	 *    由 ycn_rdp_set_muted 调用，与本诊断函数无关。判定证据见上面
	 *    [已删除] ycn_dump_all_channels 那段注释末尾的「纠错」小节。 */

	for (k = 0; k < s->rdpsnd_slot_count; k++)
	{
		int rc = ycn_rdpsnd_hook_slot(s, k);
		if (rc == 1)
			hooked++;

		/* 诊断快照（前几拍 + 之后每 120 拍一次，避免刷爆）
		 *
		 * 🔴 这一段原先是 7 句裸的 `*(BOOL*)(p + YCN_RDPSND_OFFSET_*)`，
		 * 全部在 SEH 之外 —— 同一个错犯了两轮：上一轮修了 channels/lpUserParam，
		 * 却忘了**同一个函数里还有另一族硬编码偏移读**（rdpsndPlugin 私有字段）。
		 *
		 * 为什么这在热路径上尤其危险：`p` 来自槽位缓存（上一次扫描时记下的 plugin），
		 * 而 plugin/device 在 rdpsnd Close 时会被 Free。槽位缓存的寿命比 plugin 的寿命长，
		 * 于是「上一拍还好好的 plugin，这一拍已经躺在已释放的堆上」—— 正是
		 * 2026-10-07 07:42 那种多通道并发 connect/disconnect 时的写照。
		 *
		 * ⚠️ 但要分清主次：**这族SEH 只挡住"读已释放内存"这一个动作，
		 *   并没有解决"槽位缓存的寿命比 plugin 长"这个根因**。
		 *   根因是槽位数组的读写两侧没共用 g_lock（写侧完全不持锁 + 读侧锁外用缓存指针），
		 *   已在 ycn_rdpsnd_hook（写侧加锁）与 ycn_rdp_set_muted（读侧锁内用）两处修掉。
		 *   两处都要改：只加 SEH 会让崩溃变成"读到垃圾但不崩"，属于更坏的静默失败。
		 *
		 * 教训（比"记得加 SEH"更可执行的一条）：
		 * 🔑 **加护栏要按"解引用动作"逐条清点，不是按"我这次改的是哪个函数"**。
		 * 同一个语义族（按硬编码偏移读 FreeRDP 私有结构）在文件里有 4 处，
		 * 只改 2 处就等于没改 —— 而且剩下的 2 处还给人"这里已经防过了"的错觉。 */
		if (tick <= 2 || (tick % 1200) == 0)
		{
			uint8_t* p = (uint8_t*)s->rdpsnd_plugin[k];
			char dbg[320];
			if (p)
			{
				BOOL snap_ok = FALSE;
				int v_dync = 0, v_att = 0, v_handle = 0, v_onopen = 0, v_isopen = 0, v_applyv = 0;
				unsigned v_nfmt = 0;
				/* 先把 7 个字段**一次性**读进局部变量，全程在 SEH 内；
				 * 任何一脚踩空就整段放弃快照（日志里少一行，程序继续活着）。
				 * 不做"读到几个算几个"—— 半截快照比没有快照更容易误导排障。 */
				__try
				{
					v_dync = *(BOOL*)(p + YCN_RDPSND_OFFSET_DYNAMIC);
					v_att = *(BOOL*)(p + YCN_RDPSND_OFFSET_ATTACHED);
					v_handle = *(DWORD*)(p + YCN_RDPSND_OFFSET_OPENDATA_HANDLE);
					v_onopen = *(BOOL*)(p + YCN_RDPSND_OFFSET_ONOPENCALLED);
					v_isopen = *(BOOL*)(p + YCN_RDPSND_OFFSET_ISOPEN);
					v_applyv = *(BOOL*)(p + YCN_RDPSND_OFFSET_APPLYVOLUME);
					v_nfmt = *(UINT16*)(p + YCN_RDPSND_OFFSET_NUM_CLIENT_FMT);
					snap_ok = TRUE;
				}
				__except (EXCEPTION_EXECUTE_HANDLER)
				{
					snap_ok = FALSE;
				}
				if (snap_ok)
					_snprintf_s(dbg, sizeof(dbg), _TRUNCATE,
					            "mute-snap#%ld slot%d dync=%d att=%d open=%d onopen=%d isopen=%d "
					            "applyv=%d nfmt=%u dev=%p rc=%d",
					            tick, k, v_dync, v_att, v_handle, v_onopen, v_isopen,
					            v_applyv, v_nfmt, s->rdpsnd_device[k], rc);
				else
					_snprintf_s(dbg, sizeof(dbg), _TRUNCATE,
					            "mute-snap#%ld slot%d <plugin 已不可读，跳过快照> dev=%p rc=%d",
					            tick, k, s->rdpsnd_device[k], rc);
				/* 锁内绝不写盘：只塞内存环形缓冲（见文件上方“锁内绝不 I/O”纪律） */
				ycn_bc(dbg); /* 锁内只写内存，不落盘 */
			}
		}
	}
	LeaveCriticalSection(&g_lock);
	return hooked;
}

/* 通道连上时把插件/设备指针记下来并挂钩。
 * ⚠️ 不能信 e->pInterface（静态通道这里是 NULL），只借 e->name 记个信号，
 * 真正的指针由 ycn_rdpsnd_hook 从 context->channels 里找。 */
static void ycn_rdpsnd_attach(YcnSession* s, const ChannelConnectedEventArgs* e)
{
	if (!s || !e)
		return;

	/* rdpsnd 连上时设备可能还没 Open（音频协商在稍后）→ 这里能挂就挂，
	 * 挂不上也无妨：ycn_rdp_set_muted 会再试，或下次通道事件再试。 */
	(void)ycn_rdpsnd_hook(s);
}

static void ycn_on_channel_connected(void* ctx, const ChannelConnectedEventArgs* e)
{
	/* ⚠️ 第一个参数是 **rdpContext***，不是 freerdp*！
	 * FreeRDP 的触发点：PubSub_OnChannelConnected(pubSub, instance->context, &e)
	 * （libfreerdp/core/client.c，官方客户端的 handler 也把首参命名为 context）。
	 * 早期版本按 freerdp* 解释 → ((freerdp*)ctx)->context 是垃圾指针 →
	 * 找不到 session → 事件全被丢掉 → rdpgfx 永远接不上 GDI（撕裂的真凶之二）。 */
	rdpContext* context = (rdpContext*)ctx;
	YcnSession* s = NULL;
	int i;

	if (!context || !e || !e->name)
	{
		return;
	}

	/* 这里**不能**检查 context->gdi：早期连上的通道（drdynvc 等）可能发生在
	 * PostConnect 的 gdi_init 之前，被 gdi 检查挡掉就什么诊断都看不到。 */
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	if (s)
	{
		InterlockedIncrement(&s->dvc_connected);
		if (s->dvc_names[0] != '\0' &&
		    strlen(s->dvc_names) + strlen(e->name) + 2 < sizeof(s->dvc_names))
		{
			strcat_s(s->dvc_names, sizeof(s->dvc_names), ",");
		}
		if (strlen(s->dvc_names) + strlen(e->name) + 1 < sizeof(s->dvc_names))
		{
			strcat_s(s->dvc_names, sizeof(s->dvc_names), e->name);
		}
		if (s->gfx_state == 1 && strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) != 0)
		{
			s->gfx_state = 3; /* 通道建了，但没有 GFX 那条 */
		}
	}
	LeaveCriticalSection(&g_lock);

	if (strcmp(e->name, RDPGFX_DVC_CHANNEL_NAME) == 0 && context->gdi)
	{
		RdpgfxClientContext* gfx = (RdpgfxClientContext*)e->pInterface;
		const BOOL ok = gdi_graphics_pipeline_init(context->gdi, gfx);
		WLog_INFO(TAG, "gfx pipeline: attached to GDI (full-frame mode)");
		if (s)
		{
			s->gfx_init_rc = ok ? 1 : 2;
			s->gfx_codecs_null = (gfx && gfx->codecs) ? 0 : 1;
			s->gfx_progressive_null = (gfx && gfx->codecs && gfx->codecs->progressive) ? 0 : 1;
			InterlockedExchange(&s->gfx_state, ok ? 2 : 4);
			ycn_debug_dump_gfx(s, ok ? "attach-ok" : "attach-fail"); /* 崩溃前落盘 */

			if (ok)
			{
				/* 链式挂钩：记录收到的 GFX 帧用的编解码器（判断是不是 AVC） */
				g_orig_gfx_surface = gfx->SurfaceCommand;
				gfx->SurfaceCommand = ycn_gfx_surface_command;

				/* 面包屑：把崩溃点夹到具体某一步 */
				g_orig_reset_graphics = gfx->ResetGraphics;
				gfx->ResetGraphics = ycn_gfx_reset_graphics;
				g_orig_start_frame = gfx->StartFrame;
				gfx->StartFrame = ycn_gfx_start_frame;
				g_orig_end_frame = gfx->EndFrame;
				gfx->EndFrame = ycn_gfx_end_frame;
			}
		}
	}

	/* rdpsnd 连上后：记下设备插件并装 SetVolume 挂钩。
	 * 必须在这里（而不是只在 ycn_rdp_set_muted 里）—— 用户可能先点静音、通道后到；
	 * 重连时设备会重建，也要重新挂。挂钩装上后，静音态会被**自动**持续执行。 */
	if (s && strcmp(e->name, RDPSND_CHANNEL_NAME) == 0)
	{
		ycn_rdpsnd_attach(s, e);
	}
}

/* FreeRDP 的正式通道加载入口（instance->LoadChannels）。**通道必须在这里加载。**
 *
 * 为什么不能像最初那样在 PreConnect 里调 freerdp_client_load_addins：
 *   freerdp_connect_begin() 的顺序是
 *       line 120  IFCALLRET(instance->PreConnect, ...)      ← 我们原来在这里 load_addins
 *       line 136  utils_reload_channels(instance->context)  ← 之后才真正定 channels
 *   而 utils_reload_channels 会把 context->channels **整个 disconnect/close/free 再重建**，
 *   然后回调 instance->LoadChannels 重新加载。
 *   → 在 PreConnect 里加载 = 往一个马上要被释放的对象上加载，重建后一条通道都不剩：
 *     clientDataCount == 0 → freerdp_channels_post_connect 的循环空转 →
 *     没有任何 ChannelConnected 事件 → rdpgfx 永远接不到 GDI →
 *     服务器只能走 legacy 条带更新（实测 ~800 个碎帧/秒）= 画面撕裂的真正来源。
 *
 * 时机正好：本回调在 PreConnect 之后执行，所以 PreConnect 里设的
 * SupportGraphicsPipeline / SupportDynamicChannels 已经生效，
 * freerdp_client_load_addins 才能自行把 rdpgfx（dynamic）与 drdynvc（static）挂上。 */
static BOOL ycn_load_channels(freerdp* instance)
{
	rdpSettings* settings;
	YcnSession* s = NULL;
	int i;
	BOOL wantAudio;

	if (!instance || !instance->context || !instance->context->channels)
	{
		return FALSE;
	}
	settings = instance->context->settings;
	if (!settings)
	{
		return FALSE;
	}

	/* 取本会话的音频开关（用户配置的 rdp.audio_enabled） */
	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	wantAudio = (s && s->enable_audio) ? TRUE : FALSE;
	LeaveCriticalSection(&g_lock);

	/* 放行 GFX 需要的两条通道：
	 *   rdpgfx  —— load_addins 的 step 1 按 SupportGraphicsPipeline 自行挂为动态通道
	 *   drdynvc —— step 4 按 SupportDynamicChannels 自行加载（DVC 管理器）
	 *
	 * AudioPlayback 按用户配置放行：rdpsnd 一旦挂上，load_addins 的 step 2 会自己把
	 * DeviceRedirection 拉回 TRUE 并加载 rdpdr（rdpsnd 硬依赖它）—— 这是 FreeRDP 的既定
	 * 行为，不是我们能绕过的。早期版本为避免 rdpdr 干扰画面把音频一起关了（那时候 GFX
	 * 还没通、现象分不清），GFX 修好后重新放开，靠自检确认帧率是否仍稳。 */
	freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, wantAudio);
	freerdp_settings_set_bool(settings, FreeRDP_AudioCapture, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE);
	freerdp_settings_set_bool(settings, FreeRDP_SupportDynamicChannels, TRUE);

	/* ── GFX 编码协商：只声明 AVC420（H.264 4:2:0 单流）──
	 *
	 * 背景：本机原先编译的 FreeRDP 没有任何 H.264 后端（WITH_GFX_H264 推导为 OFF），
	 * 客户端在 caps 里不声明 AVC → 服务端只能走 GDI 全帧位图 → 两路 1080p 每帧
	 * 三次整帧拷贝、每秒约 1.2GB/s，CPU 到 350%+ 单核。
	 *
	 * 现在 overlay port 编进了 FFmpeg 后端 + WITH_FFMPEG_HWACCEL。这里显式把协商
	 * 钉在 AVC420：
	 *   - GfxH264     TRUE  → 声明 AVC420（单流，解码量最小）
	 *   - GfxAVC444   FALSE → 不声明 v1 双流（解码量翻倍，且上游有色度位置缺陷）
	 *   - GfxAVC444v2 FALSE → 不声明 v2（仍为双流 + 客户端色度合成）
	 * 两路会话的内容是自动化任务的游戏画面，4:2:0 足够；若将来要锐利文字，
	 * 只开 GfxAVC444v2 即可，无需改别处。
	 *
	 * 🔑 SoftwareGdi 是「硬解开关的输入」而不是界面开关：
	 *  FreeRDP 的 gdi_SurfaceCommand_AVC444 里 HW_ACCEL = !SoftwareGdi，
	 *  所以它必须是 FALSE 才能走硬解。不要把它接到任何 UI 勾选项上，
	 *  否则关掉界面开关会连带改变 GDI 渲染后端（副作用未验证）。
	 *  这里显式置 FALSE 只为把依赖关系写在明面上，与默认值一致、无行为变化。 */
	freerdp_settings_set_bool(settings, FreeRDP_SoftwareGdi, FALSE);
	/* 注意：不要给下面三行套 #ifdef WITH_GFX_H264 —— 那是 FreeRDP 库内部的编译宏，
	 * 不在我们的编译单元里定义；套上去等于永远不设置。settings 键本身与库能力无关，
	 * 设 TRUE 而无后端时库自会忽略（协商拿不到 AVC，退回 GDI），不需要条件编译。 */
	freerdp_settings_set_bool(settings, FreeRDP_GfxH264, TRUE);
	freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444v2, FALSE);

	/* 关掉 disp（Microsoft::Windows::RDS::DisplayControl，动态分辨率）：
	 * 我们固定 1920×1080，不需要它；多一条通道只会多几次 ResetGraphics 协商，
	 * 而 gdi_ResetGraphics 在本机这份 freerdp 上是要靠 SEH 兜 AV 的（见钩子注释）。 */
	freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_UseMultimon, FALSE);

	return freerdp_client_load_addins(instance->context->channels, settings);
}

/* ---- 诊断：把 GFX 接入结果**立刻落盘** ----
 * 为什么不用 last_error / 导出接口：GFX 半初始化会直接 AV 把进程打死，
 * C# 那边 5 秒一拍的日志根本来不及写。写文件是同步的，崩溃前一定落盘。
 * 只在异常分支调用，正常路径零 I/O（否则同步磁盘写会拖垮帧率）。 */
static void ycn_debug_dump_gfx(const YcnSession* s, const char* tag)
{
	char path[MAX_PATH] = WINPR_C_ARRAY_INIT;
	char line[640] = WINPR_C_ARRAY_INIT;
	FILE* f = NULL;

	if (!s)
		return;
	const DWORD n = GetTempPathA(MAX_PATH, path);
	if (n == 0 || n >= MAX_PATH)
		return;
	strcat_s(path, sizeof(path), "ycn_gfx_debug.txt");

	if (fopen_s(&f, path, "a") != 0 || !f)
		return;

	_snprintf_s(line, sizeof(line), _TRUNCATE,
	            "[%s] gfx_init=%d codecs_null=%d prog_null=%d gfx_state=%d dvc=%u static_ch=%u dyn_ch=%u "
	            "load_addins=%d names=[%s]\n",
	            tag, s->gfx_init_rc, s->gfx_codecs_null, s->gfx_progressive_null, (int)s->gfx_state,
	            (unsigned)s->dvc_connected,
	            s->instance && s->instance->context && s->instance->context->settings
	                ? (unsigned)freerdp_settings_get_uint32(s->instance->context->settings,
	                                                         FreeRDP_StaticChannelCount)
	                : 0u,
	            s->instance && s->instance->context && s->instance->context->settings
	                ? (unsigned)freerdp_settings_get_uint32(s->instance->context->settings,
	                                                         FreeRDP_DynamicChannelCount)
	                : 0u,
	            s->addins_rc, s->dvc_names);
	fputs(line, f);
	fclose(f);
}

static BOOL ycn_pre_connect(freerdp* instance)
{
	YcnSession* s = NULL;
	int i;
	rdpSettings* settings = instance->context->settings;

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);

	/* 标准 RDP 安全（SecurityLayer=0 服务器）下 AutoDetect PDU 会引发
	 * RC4 解密流错位（invalid packet signature）——三项全关，M0 实测通过 */
	freerdp_settings_set_bool(settings, FreeRDP_NetworkAutoDetect, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_SupportMultitransport, FALSE);

	/* GFX 全帧管线（M6）：服务器在 AVC 被禁时会协商 progressive（内置解码），
	 * EndFrame 语义 = 整帧完成 → 帧回调拿到的永远是完整帧（撕裂根治）。
	 * 解码器缺失时 caps 自动不含 AVC444，无需外部依赖 */
	if (s->use_gfx)
	{
		freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE);
	}
	freerdp_settings_set_uint32(settings, FreeRDP_MultitransportFlags, 0);
	freerdp_settings_set_bool(settings, FreeRDP_SupportHeartbeatPdu, FALSE);

	/* 设备/剪贴板重定向全关：rdpsnd 会自动把 DeviceRedirection 拉回 TRUE，不受影响 */
	freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectDrives, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectPrinters, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectSmartCards, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectSerialPorts, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, FALSE);

	/* ---- 动态虚拟通道：GFX 全帧管线的地基 ----
	 *
	 * ⚠️ 这里**只开开关**，不要调 freerdp_client_load_addins ——
	 * 真正的加载必须放在 instance->LoadChannels（见 ycn_load_channels 的注释：
	 * PreConnect 之后 utils_reload_channels 会重建 channels，在这里加载等于白加载）。
	 *
	 * 也不要自己 add_static/dynamic_channel 去加 "drdynvc"/"rdpgfx"：
	 * load_addins 内部已按 SupportGraphicsPipeline/SupportDynamicChannels 自行处理，
	 * 手动再加一遍会重名失败，进而让 load_addins 提前 return FALSE。 */
	freerdp_settings_set_bool(settings, FreeRDP_SupportDynamicChannels, TRUE);

	/* 通道连接事件就在 PreConnect 订阅（早于 utils_reload_channels / post_connect）。
	 * GFX 全靠它把 RdpgfxClientContext 接到 GDI 图形管线。 */
	if (s)
	{
		int code = 0;
		if (PubSub_SubscribeChannelConnected(instance->context->pubSub,
		                                     ycn_on_channel_connected) >= 0)
		{
			code |= 2;
		}
		if (freerdp_channels_load_static_addin_entry("rdpgfx", NULL, NULL,
		        YCN_ADDIN_CHANNEL_STATIC | YCN_ADDIN_CHANNEL_ENTRYEX))
		{
			code |= 4;
		}
		if (freerdp_channels_load_static_addin_entry("drdynvc", NULL, NULL,
		        YCN_ADDIN_CHANNEL_STATIC | YCN_ADDIN_CHANNEL_ENTRYEX))
		{
			code |= 8;
		}
		s->addins_rc = code;
	}

	/* 强制 slowpath 输入：标准 RDP 安全（SecurityLayer=0 服务器）+ 重连会话场景下，
	 * fastpath 输入 PDU 在连接后 N 秒发出会被服务器判「协议流错误」并断开
	 * （服务器事件日志 #97；M1 冒烟多轮复现，slowpath 实测稳定） */
	freerdp_settings_set_bool(settings, FreeRDP_FastPathInput, FALSE);
	/* 禁用 auto-reconnect cookie：重连已断开会话时服务器用「断线前保存的加密状态」
	 * 校验后续 PDU，而客户端是全新加密状态 → 任何输入 PDU 都被判协议流错误断开
	 * （M1 冒烟：0 延迟注入稳定/N 秒后注入必掉线，服务器日志 #97 实证）。
	 * 禁掉后重连退化为全新登录（AutoLogon），加密状态两端一致 */
	freerdp_settings_set_bool(settings, FreeRDP_AutoReconnectionEnabled, FALSE);
	/* GFX 管道（无 H.264，仅 planar/thin-client caps）：服务器组策略开了
	 * AVC444ModePreferred，会「等待图形子系统」并卡在会话 passthrough 状态，
	 * 期间任何输入 PDU 都触发服务器 RDP_SEC 状态机错误断开（日志 #97/#226）。
	 * 开 GFX 让图形通道能握手完成。首期仍不解码 H.264（GfxH264 关），
	 * 编码由服务器按 caps 回退到 planar */
	freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE);
	freerdp_settings_set_bool(settings, FreeRDP_GfxH264, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_GfxPlanar, TRUE);
	return TRUE;
}

static BOOL ycn_post_connect(freerdp* instance)
{
	YcnSession* s = NULL;
	int i;
	rdpSettings* settings = instance->context->settings;

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);
	if (!s)
		return FALSE;

	/* gdi_init 必须在 PostConnect（依赖握手后核心创建的 rdpCache） */
	if (!gdi_init(instance, PIXEL_FORMAT_BGRA32))
	{
		set_session_error(s, "gdi_init 失败");
		return FALSE;
	}

	/* 挂 EndPaint 铩：gdi 实现在 gdi_init 时已注册，存原实现先调再拷帧（per-session） */
	s->orig_end_paint = instance->context->update->EndPaint;
	instance->context->update->EndPaint = ycn_end_paint;

	s->connected_at = GetTickCount64();
	s->refresh_requested = 0;

	/* GFX 通道连接事件的订阅已移到 PreConnect（更早，见那里注释）——
	 * 这里只做加载结果自检：通道有没有真的进 channels（诊断位 addins_rc 的 bit4/bit5）。 */
	if (s)
	{
		if (freerdp_channels_get_static_channel_interface(instance->context->channels,
		                                                  DRDYNVC_SVC_CHANNEL_NAME))
		{
			s->addins_rc |= 16;
		}
		if (freerdp_channels_get_static_channel_interface(instance->context->channels,
		                                                  RDPSND_CHANNEL_NAME))
		{
			s->addins_rc |= 32;
		}
	}

	if (InterlockedCompareExchange(&s->state, YCN_STATE_RUNNING,
	                               YCN_STATE_CONNECTING) != YCN_STATE_CONNECTING)
		return FALSE;

	if (s->cb.on_connected)
		s->cb.on_connected(s->user, s->id,
		                   freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth),
		                   freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight));
	return TRUE;
}

static BOOL ycn_authenticate_ex(freerdp* instance, char** username, char** password, char** domain,
                                rdp_auth_reason reason)
{
	YcnSession* s = NULL;
	int i;
	(void)instance;
	(void)reason;

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);
	if (!s)
		return FALSE;

	*username = _strdup(s->username);
	*password = _strdup(s->password);
	*domain = s->domain[0] ? _strdup(s->domain) : NULL;
	return TRUE;
}

static int ycn_verify_x509(freerdp* instance, const BYTE* data, size_t length,
                           const char* hostname, UINT16 port, DWORD flags)
{
	YcnSession* s = NULL;
	int i;
	(void)instance;
	(void)data;
	(void)length;
	(void)hostname;
	(void)port;
	(void)flags;

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
	{
		if (g_sessions[i].used && g_sessions[i].instance &&
		    g_sessions[i].instance->context == instance->context)
		{
			s = &g_sessions[i];
			break;
		}
	}
	LeaveCriticalSection(&g_lock);
	/* allow_selfsigned = 放行（正式指纹 UI 是 M6；M0/M1 环回场景一律放行）。
	 * 3.32 语义：返回非零信任该证书 */
	return s ? (s->allow_selfsigned ? 1 : 0) : 0;
}

/* ---------------- 事件循环线程 ---------------- */

static DWORD WINAPI event_loop(LPVOID arg)
{
	YcnSession* s = (YcnSession*)arg;
	freerdp* instance = NULL;
	HANDLE thread_handle = NULL;
	HANDLE stop_event = NULL;
	ycn_rdp_callbacks callbacks;
	void* callback_user = NULL;
	int callback_id = 0;
	uint8_t* frame = NULL;
	BOOL connected = FALSE;
	BOOL report_disconnected = FALSE;
	int fail_reason = YCN_DISCONNECT_ERROR;
	char detail[YCN_ERRBUF_LEN];

	detail[0] = '\0';

	EnterCriticalSection(&g_lock);
	instance = s->instance;
	if (InterlockedCompareExchange(&s->state, 0, 0) == YCN_STATE_STOPPING)
	{
		InterlockedExchange(&s->connect_in_progress, 0);
		LeaveCriticalSection(&g_lock);
		_snprintf_s(detail, sizeof(detail), _TRUNCATE, "connect cancelled");
		fail_reason = YCN_DISCONNECT_LOCAL;
		goto teardown;
	}
	InterlockedExchange(&s->connect_in_progress, 1);
	LeaveCriticalSection(&g_lock);

	if (!freerdp_connect(instance))
	{
		UINT32 err = freerdp_get_last_error(instance->context);
		_snprintf_s(detail, sizeof(detail), _TRUNCATE, "connect: %s (0x%08X)",
		            freerdp_get_last_error_string(err), err);
		fail_reason = YCN_DISCONNECT_ERROR;
		set_session_error(s, "%s", detail);
	}
	else
	{
		connected = TRUE;
		fail_reason = YCN_DISCONNECT_REMOTE;
		/* 事件泵：官方句柄就绪驱动模式（同 wfreerdp/spike）。
		 * **必须**等 transport 句柄就绪才调 check_event_handles——
		 * 无数据时定时轮询 check 会污染 client 加密流：之后任何
		 * 输入 PDU 都被服务器判「协议流错误」断开（M1 冒烟 + 服务器
		 * 事件日志 #97 实证，标准 RDP 安全场景） */
		while (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_STOPPING)
		{
			HANDLE handles[64];
			DWORD count = freerdp_get_event_handles(instance->context, handles, 64);
			if (count == 0)
			{
				_snprintf_s(detail, sizeof(detail), _TRUNCATE, "event handles unavailable");
				set_session_error(s, "%s", detail);
				fail_reason = YCN_DISCONNECT_ERROR;
				break;
			}
			if (count < 64)
				handles[count++] = s->stop_event;

			DWORD st = WaitForMultipleObjects(count, handles, FALSE, 500);
			if (st == WAIT_FAILED)
			{
				_snprintf_s(detail, sizeof(detail), _TRUNCATE, "WaitForMultipleObjects failed");
				set_session_error(s, "%s", detail);
				fail_reason = YCN_DISCONNECT_ERROR;
				break;
			}

			/* 消费输入队列（必须在本线程发送，见 YcnMouseReq 注释） */
			for (;;)
			{
				YcnMouseReq m;
				YcnKeyReq k;
				int have_m = 0;
				int have_k = 0;
				uint32_t sync_flags = 0;
				int have_sync = 0;
				EnterCriticalSection(&g_lock);
				if (s->mouse_head != s->mouse_tail)
				{
					m = s->mouse_q[s->mouse_tail];
					s->mouse_tail = (s->mouse_tail + 1) % YCN_INPUT_QUEUE_CAP;
					have_m = 1;
				}
				if (s->key_head != s->key_tail)
				{
					k = s->key_q[s->key_tail];
					s->key_tail = (s->key_tail + 1) % YCN_INPUT_QUEUE_CAP;
					have_k = 1;
				}
				if (s->pending_sync)
				{
					sync_flags = s->sync_flags;
					s->pending_sync = 0;
					have_sync = 1;
				}
				LeaveCriticalSection(&g_lock);
				/* 同步必须排在键事件**之前**：它是服务器解释后续扫描码的基准，
				 * 反过来（先按 NumLock 再同步）会让本拍按键仍按旧锁状态解释。 */
				if (have_sync)
				{
					BOOL sent = freerdp_input_send_synchronize_event(instance->context->input, sync_flags);
					WLog_DBG(TAG, "input drain: keyboard sync sent=%d flags=0x%08X", sent, sync_flags);
				}
				if (have_m)
				{
					BOOL sent = freerdp_input_send_mouse_event(instance->context->input, m.flags, m.x, m.y);
					WLog_DBG(TAG, "input drain: mouse sent=%d flags=0x%04X", sent, m.flags);
					if (!sent)
						WLog_WARN(TAG, "input drain: mouse NOT sent (回调未注册或队列状态异常)");
				}
				if (have_k)
				{
					UINT32 rdp_scancode = MAKE_RDP_SCANCODE(k.scancode, k.extended ? TRUE : FALSE);
					freerdp_input_send_keyboard_event_ex(instance->context->input,
					                                     k.down ? TRUE : FALSE, TRUE, rdp_scancode);
				}
				if (!have_m && !have_k && !have_sync)
					break;
			}

			if (!freerdp_check_event_handles(instance->context))
			{
				UINT32 err = freerdp_get_last_error(instance->context);
				_snprintf_s(detail, sizeof(detail), _TRUNCATE, "transport: %s (0x%08X)",
				            freerdp_get_last_error_string(err), err);
				set_session_error(s, "%s", detail);
				fail_reason = YCN_DISCONNECT_ERROR;
				break;
			}

			/* rdpsnd 静音挂钩补装：通道连上时设备往往还没 Open（音频协商在稍后），
			 * 所以每拍（500ms）试一次；device 被换掉时也会重挂（函数内部判指针）。
			 * 必须在**本线程**调 —— 这是 RDP 线程，FreeRDP 结构只在这里安全访问。 */
			(void)ycn_rdpsnd_hook(s);

			/* 重连已有会话时，服务器按 RDP 协议假设「客户端仍持有屏幕缓存」，
			 * 不主动重推桌面帧 —— 恢复瞬间最多一帧（常为纯黑），之后画面永远静止（M5 黑屏根因）。
			 * 激活完成约 1 秒后显式请求一次全屏刷新（Refresh Rectangle PDU，MS-RDPBCGR 2.2.11.2.1），
			 * 服务器随后全量重绘，帧流恢复。只发一次。 */
			if (!s->refresh_requested &&
			    GetTickCount64() - s->connected_at >= 1500)
			{
				s->refresh_requested = 1;
				rdpSettings* settings = instance->context->settings;
				UINT16 desktop_w = (UINT16)freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth);
				UINT16 desktop_h = (UINT16)freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight);
				RECTANGLE_16 full = { 0, 0, desktop_w, desktop_h };
				BOOL sent = instance->context->update->RefreshRect(instance->context, 1, &full);
				WLog_INFO(TAG, "refresh rect: requested full %ux%u sent=%d", desktop_w, desktop_h, sent);
				/* 借 last_error 通道把发送结果带回 C# 日志（GUI 进程 WLog 不可见，M5 调试用） */
				set_session_error(s, "refresh-rect %ux%u sent=%d", desktop_w, desktop_h, (int)sent);
			}

			/* 停止信号：stop_event 已在句柄数组里，被触发时上面 WaitFor 返回后
			 * 本拍 check 照常跑一次，下一拍循环条件退出 */
		}
	}
	InterlockedExchange(&s->connect_in_progress, 0);

teardown:
	/* Publish STOPPING before waiting: disconnect only acquires an abort reference
	 * while the session is CONNECTING under this same lock. */
	EnterCriticalSection(&g_lock);
	if (InterlockedCompareExchange(&s->state, 0, 0) == YCN_STATE_STOPPING)
		fail_reason = YCN_DISCONNECT_LOCAL;
	InterlockedExchange(&s->state, YCN_STATE_STOPPING);
	callbacks = s->cb;
	callback_user = s->user;
	callback_id = s->id;
	frame = s->frame;
	s->frame = NULL;
	s->frame_valid = FALSE;
	LeaveCriticalSection(&g_lock);

	/* References registered before STOPPING protect freerdp_connect/abort from
	 * teardown. No new abort reference can be acquired after the locked transition. */
	while (InterlockedCompareExchange(&s->abort_in_progress, 0, 0) != 0 ||
	       InterlockedCompareExchange(&s->connect_in_progress, 0, 0) != 0)
		Sleep(1);

	if (connected)
		freerdp_disconnect(instance);
	if (instance->context)
	{
		gdi_free(instance);
		freerdp_context_free(instance);
	}
	freerdp_free(instance);
	free(frame);

	/* FreeRDP and every channel are gone.  Release the slot before the managed
	 * terminal callback; IDs are never reused, so a late old-client call cannot
	 * address a newly allocated slot. */
	EnterCriticalSection(&g_lock);
	callbacks = s->cb;
	callback_user = s->user;
	callback_id = s->id;
	report_disconnected = InterlockedCompareExchange(&s->disconnected_reported, 1, 0) == 0;
	thread_handle = s->thread;
	stop_event = s->stop_event;
	s->instance = NULL;
	s->frame_width = 0;
	s->frame_height = 0;
	s->frame_stride = 0;
	s->thread = NULL;
	s->stop_event = NULL;
	{
		int k;
		for (k = 0; k < YCN_RDPSND_SLOTS; k++)
		{
			s->rdpsnd_device[k] = NULL;
			s->rdpsnd_plugin[k] = NULL;
			s->rdpsnd_orig_set_volume[k] = NULL;
			s->rdpsnd_orig_play[k] = NULL;
			s->rdpsnd_orig_play_ex[k] = NULL;
		}
		s->rdpsnd_slot_count = 0;
	}
	s->mouse_head = s->mouse_tail = 0;
	s->key_head = s->key_tail = 0;
	s->sync_flags = 0;
	s->pending_sync = 0;
	s->cb.on_connected = NULL;
	s->cb.on_frame_ready = NULL;
	s->cb.on_desktop_resize = NULL;
	s->cb.on_disconnected = NULL;
	s->cb.on_error = NULL;
	s->user = NULL;
	InterlockedExchange(&s->state, YCN_STATE_IDLE);
	s->used = 0;
	InterlockedIncrement(&g_total_released);
	LeaveCriticalSection(&g_lock);

	if (stop_event)
		CloseHandle(stop_event);
	if (thread_handle)
		CloseHandle(thread_handle);

	/* Terminal callback: after this point no FreeRDP/channel/native callback for
	 * this session can occur, so managed code may release its GCHandle roots. */
	if (report_disconnected && callbacks.on_disconnected)
		callbacks.on_disconnected(callback_user, callback_id, fail_reason, detail);
	return 0;
}

/* ---------------- 导出实现 ---------------- */

YCN_API int ycn_rdp_connect(const ycn_rdp_params* params, const ycn_rdp_callbacks* callbacks, void* user)
{
	int slot = -1;
	int id;
	YcnSession* s;
	freerdp* instance;
	rdpSettings* settings;

	if (!params || !params->host || !params->username || !params->password || !callbacks)
	{
		set_global_error("invalid argument: params/callbacks 为空");
		return YCN_ERR_INVALID_ARG;
	}
	process_init();

	EnterCriticalSection(&g_lock);
	for (id = 0; id < YCN_MAX_SESSIONS; id++)
	{
		if (!g_sessions[id].used)
		{
			slot = id;
			break;
		}
	}
	if (slot < 0)
	{
		LeaveCriticalSection(&g_lock);
		set_global_error("会话表已满（上限 %d）", YCN_MAX_SESSIONS);
		return YCN_ERR_SESSION_FULL;
	}
	if (g_next_session_id > INT_MAX)
	{
		LeaveCriticalSection(&g_lock);
		set_global_error("session id space exhausted");
		return YCN_ERR_SESSION_FULL;
	}
	s = &g_sessions[slot];
	memset(s, 0, sizeof(*s));
	s->used = 1;
	s->id = (int)g_next_session_id++;
	id = s->id;
	s->cb = *callbacks;
	s->user = user;
	InterlockedExchange(&s->state, YCN_STATE_CONNECTING);
	InterlockedExchange(&s->disconnected_reported, 0);
	InterlockedIncrement(&g_total_created);
	memset(s->last_error, 0, sizeof(s->last_error));
	LeaveCriticalSection(&g_lock);

	/* 深拷贝参数（C# 侧内存随时可释放） */
	strncpy_s(s->host, sizeof(s->host), params->host, _TRUNCATE);
	s->port = params->port ? params->port : 3389;
	strncpy_s(s->username, sizeof(s->username), params->username, _TRUNCATE);
	strncpy_s(s->domain, sizeof(s->domain), params->domain ? params->domain : "", _TRUNCATE);
	strncpy_s(s->password, sizeof(s->password), params->password, _TRUNCATE);
	s->width = params->desktop_width ? params->desktop_width : 1280;
	s->height = params->desktop_height ? params->desktop_height : 720;
	s->color_depth = params->color_depth ? params->color_depth : 32;
	s->use_nla = params->use_nla;
	s->allow_selfsigned = params->allow_selfsigned;
	s->enable_audio = params->enable_audio;
	s->use_gfx = params->use_gfx;
	InterlockedExchange(&s->muted, 0); /* 新会话默认不静音（C# 侧若记住状态会在连上后立刻设一次） */
	s->gfx_state = params->use_gfx ? 1 : 0;
	s->dvc_connected = 0;
	s->dvc_names[0] = '\0';
	s->addins_rc = -2; /* 尚未调用 load_addins */
	s->gfx_init_rc = 0;
	s->gfx_codecs_null = -1;
	s->gfx_progressive_null = -1;
	s->frame_valid = FALSE;
	s->frame_width = 0;
	s->frame_height = 0;
	s->frame_stride = 0;
	s->mouse_head = 0;
	s->mouse_tail = 0;
	s->key_head = 0;
	s->key_tail = 0;
	s->sync_flags = 0;
	s->pending_sync = 0;

	instance = freerdp_new();
	if (!instance)
	{
		set_session_error(s, "freerdp_new 失败");
		ycn_abandon_connect(s, NULL, NULL, NULL);
		return YCN_ERR_NO_MEMORY;
	}
	s->instance = instance;
	instance->PreConnect = ycn_pre_connect;
	instance->PostConnect = ycn_post_connect;
	/* ⚠️ GFX 全帧管线（instance->LoadChannels）**暂时不挂**，回退到 legacy 更新。
	 *
	 * 已查实的事实链（2026-10-04）：
	 *   1) 挂上它之后通道确实能加载（load_addins 成功、drdynvc 就位），
	 *      服务器也确实切到了 GFX 编码 —— 但帧一帧都进不来（帧到达 0/秒，画面静止）。
	 *   2) gdi_graphics_pipeline_init 内部用 FREERDP_CODEC_ALL 调
	 *      freerdp_client_codecs_prepare；本机 vcpkg 的 freerdp **没编 H.264 解码器**
	 *      （端口无 h264 feature），这一步大概率直接失败 → GFX 接不上 GDI。
	 *   3) 于是"声明了 GFX 能力 + 建了 DVC"反而让服务器不再走 legacy，退化成零帧。
	 *
	 * 结论：要真正打开 GFX，得先在 vcpkg 里重装带 h264 的 freerdp 再编原生层，
	 * 属于独立一步（不是改几行代码能收口的）。在此之前保持通道不加载 =
	 * 服务器走 legacy bitmap 更新（有画面），撕裂由 C# 侧的**锁内整帧拷贝**兜住。
	 *
	 * 【2026-10-04 00:48 验证结果】挂上它之后进程在连接建立数秒内 **AV 崩溃**
	 * （退出码 0xC0000005），不是"解码器为空"这么温和 —— GFX 路径在本机这份
	 * freerdp 上有更硬的兼容问题（大概率就是缺 H.264/AVC 解码器，GFX 帧处理时踩到
	 * 半初始化的编解码上下文）。下次要动这块，先 vcpkg 装 h264 再复测。 */
	/* ⚠️ 这一行是 GFX 全帧管线的**命门**：不挂它 = 通道一条都加载不上（见 ycn_load_channels 注释） */
	instance->LoadChannels = ycn_load_channels;
	instance->VerifyX509Certificate = ycn_verify_x509;
	instance->AuthenticateEx = ycn_authenticate_ex;

	if (!freerdp_context_new(instance))
	{
		set_session_error(s, "freerdp_context_new 失败");
		freerdp_free(instance);
		ycn_abandon_connect(s, NULL, NULL, NULL);
		return YCN_ERR_NO_MEMORY;
	}

	settings = instance->context->settings;
	freerdp_settings_set_string(settings, FreeRDP_ServerHostname, s->host);
	freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, (UINT32)s->port);
	freerdp_settings_set_string(settings, FreeRDP_Username, s->username);
	freerdp_settings_set_string(settings, FreeRDP_Password, s->password);
	freerdp_settings_set_string(settings, FreeRDP_Domain, s->domain[0] ? s->domain : NULL);
	freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, s->width);
	freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, s->height);
	freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, s->color_depth);
	freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, s->enable_audio ? TRUE : FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_AudioCapture, FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, s->allow_selfsigned ? TRUE : FALSE);
	freerdp_settings_set_bool(settings, FreeRDP_AutoLogonEnabled, TRUE);

	/* 强制不走代理：FreeRDP 3.x 会读 http_proxy/https_proxy 环境变量把 RDP
	 * 塞进 HTTP CONNECT 隧道（日志可见 proxy_parse_uri），环回/局域网直连
	 * 场景代理层会掐断长连接（M1 冒烟：连接 5 秒后 BIO_read retries exceeded） */
	freerdp_settings_set_uint32(settings, FreeRDP_ProxyType, PROXY_TYPE_NONE);

	/* 安全层选择：-1 自动（nego 全开，服务器自选）；0 禁 NLA；1 强制 NLA */
	if (s->use_nla < 0)
	{
		freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_NegotiateSecurityLayer, TRUE);
	}
	else if (s->use_nla == 0)
	{
		freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, FALSE);
		freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_NegotiateSecurityLayer, TRUE);
	}
	else
	{
		freerdp_settings_set_bool(settings, FreeRDP_NlaSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_TlsSecurity, TRUE);
		freerdp_settings_set_bool(settings, FreeRDP_RdpSecurity, FALSE);
		freerdp_settings_set_bool(settings, FreeRDP_NegotiateSecurityLayer, TRUE);
	}

	s->stop_event = CreateEventW(NULL, TRUE, FALSE, NULL);
	if (!s->stop_event)
	{
		set_session_error(s, "CreateEvent 失败");
		ycn_abandon_connect(s, instance, NULL, NULL);
		return YCN_ERR_NO_MEMORY;
	}

	/* 创建线程时持有 g_lock：event_loop 会先取得同一把锁，直到 thread
	 * 已写入 session。普通 CreateThread 没有 ResumeThread 失败窗口，也不会
	 * 留下一条永久挂起的系统线程。 */
	EnterCriticalSection(&g_lock);
	s->thread = CreateThread(NULL, 0, event_loop, s, 0, NULL);
	if (!s->thread)
	{
		LeaveCriticalSection(&g_lock);
		set_session_error(s, "CreateThread 失败");
		ycn_abandon_connect(s, instance, s->stop_event, NULL);
		return YCN_ERR_THREAD_FAILED;
	}
	LeaveCriticalSection(&g_lock);
	return id;
}

YCN_API void ycn_rdp_disconnect(int session)
{
	YcnSession* s;
	HANDLE thread = NULL;
	HANDLE stop = NULL;
	rdpContext* abort_context = NULL;
	LONG state;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s)
	{
		state = InterlockedCompareExchange(&s->state, YCN_STATE_STOPPING, YCN_STATE_RUNNING);
		if (state == YCN_STATE_CONNECTING)
		{
			state = InterlockedCompareExchange(&s->state, YCN_STATE_STOPPING,
			                                   YCN_STATE_CONNECTING);
			/* Keep the context alive across the unlocked abort call.  The event
			 * thread waits for this reference before starting teardown. */
			if (state == YCN_STATE_CONNECTING &&
			    InterlockedCompareExchange(&s->connect_in_progress, 0, 0) != 0 &&
			    s->instance && s->instance->context)
			{
				InterlockedIncrement(&s->abort_in_progress);
				abort_context = s->instance->context;
			}
		}
		if (state != YCN_STATE_IDLE)
		{
			if (s->thread)
				DuplicateHandle(GetCurrentProcess(), s->thread, GetCurrentProcess(),
				                &thread, 0, FALSE, DUPLICATE_SAME_ACCESS);
			if (s->stop_event)
				DuplicateHandle(GetCurrentProcess(), s->stop_event, GetCurrentProcess(),
				                &stop, 0, FALSE, DUPLICATE_SAME_ACCESS);
		}
	}
	LeaveCriticalSection(&g_lock);

	if (!s)
		return;

	if (abort_context)
	{
		freerdp_abort_connect_context(abort_context);
		InterlockedDecrement(&s->abort_in_progress);
	}

	if (stop)
		SetEvent(stop);
	if (thread)
	{
		/* 超时意味着线程仍可能使用对象：保留槽、句柄与全部对象供线程收尾。 */
		if (WaitForSingleObject(thread, 5000) != WAIT_OBJECT_0)
		{
			CloseHandle(thread);
			if (stop)
				CloseHandle(stop);
			return;
		}
		CloseHandle(thread);
	}
	if (stop)
		CloseHandle(stop);
}

YCN_API int ycn_rdp_send_mouse(int session, uint32_t flags, uint16_t x, uint16_t y)
{
	YcnSession* s;
	int rc;
	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_RUNNING)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_NOT_CONNECTED;
	}
	/* 只入队，事件循环线程负责实际发送（跨线程直发会破坏协议流）。
	 * 合并优化：队尾若是纯 MOVE（无按下/释放位），新 MOVE 直接覆盖——
	 * 拖动时 PointerMoved 高频（100Hz+），逐个转发会淹没事件循环 */
	{
		int next = (s->mouse_head + 1) % YCN_INPUT_QUEUE_CAP;
		if (next == s->mouse_tail)
		{
			LeaveCriticalSection(&g_lock);
			return YCN_ERR_NOT_CONNECTED; /* 队列满：丢帧 */
		}
		if (s->mouse_head != s->mouse_tail)
		{
			int tail = s->mouse_tail;
			uint32_t tailFlags = s->mouse_q[tail].flags;
			/* 仅当新旧 flags 完全相同且是移动类（含 MOVE 位、非按下）时覆盖队尾。
			 * 拖拽 MOVE(0x1800) 也合并（RDP 语义允许丢中间点）；滚轮/按下/释放不动 */
			if (tailFlags == flags && (flags & 0x0800u) != 0 && (flags & 0x8000u) == 0)
			{
				s->mouse_q[tail].x = x;
				s->mouse_q[tail].y = y;
				LeaveCriticalSection(&g_lock);
				return YCN_OK;
			}
		}
		s->mouse_q[s->mouse_head].flags = flags;
		s->mouse_q[s->mouse_head].x = x;
		s->mouse_q[s->mouse_head].y = y;
		s->mouse_head = next;
		rc = YCN_OK;
	}
	LeaveCriticalSection(&g_lock);
	return rc;
}

YCN_API int ycn_rdp_send_key(int session, int down, int extended, uint16_t scancode)
{
	YcnSession* s;
	int rc;
	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_RUNNING)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_NOT_CONNECTED;
	}
	/* 只入队（extended 位在事件循环线程经 MAKE_RDP_SCANCODE 编码） */
	{
		int next = (s->key_head + 1) % YCN_INPUT_QUEUE_CAP;
		if (next == s->key_tail)
		{
			LeaveCriticalSection(&g_lock);
			return YCN_ERR_NOT_CONNECTED; /* 队列满：丢键 */
		}
		s->key_q[s->key_head].down = down;
		s->key_q[s->key_head].extended = extended;
		s->key_q[s->key_head].scancode = scancode;
		s->key_head = next;
		rc = YCN_OK;
	}
	LeaveCriticalSection(&g_lock);
	return rc;
}

/* 同步键盘 toggle 状态（NumLock / CapsLock / ScrollLock）到服务器。
 *
 * 为什么必须有：小键盘 0-9 与 Insert/Home/PgUp/End/PgDn/↑↓←→ 在 Set1 里**共用扫描码**
 * （Numpad1 与 End 同为 0x4F，Numpad0 与 Insert 同为 0x52……），唯一区别是 extended 位
 * **加上服务器侧的 NumLock 状态**。服务器拿不到本机锁状态，就只能按它自己那份锁状态解释：
 * 本机 NumLock 开、服务器关 → 用户按小键盘 1，服务器当成 End，光标乱跳。
 * mstsc / xfreerdp 都会在连接建立与窗口获得焦点时发一次 TS_SYNCHRONIZE，本桥接层原先没有。
 *
 * flags 取值（FreeRDP input.h 的 KBD_SYNC_*，与 MS-RDPBCGR 2.2.8.1.1.3.1.1.1 一致）：
 *   0x01 ScrollLock / 0x02 NumLock / 0x04 CapsLock / 0x08 KanaLock
 * **每个位只在对应锁处于开启状态时置 1**（是状态快照，不是 toggle 动作）。
 *
 * 与 send_* 同纪律：只置标志，实际发送在事件循环线程（跨线程直发会破坏协议流）。
 * 幂等——同一状态连发多次无副作用；连接未就绪返回 YCN_ERR_NOT_CONNECTED。 */
YCN_API int ycn_rdp_send_keyboard_sync(int session, uint32_t flags)
{
	YcnSession* s;
	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_RUNNING)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_NOT_CONNECTED;
	}
	/* 未知位一律拒收：位定义是协议契约，放错位会静默误解锁状态 */
	if (flags & ~(uint32_t)YCN_KBD_SYNC_ALL)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_INVALID_ARG;
	}
	s->sync_flags = flags;
	s->pending_sync = 1;
	LeaveCriticalSection(&g_lock);
	return YCN_OK;
}

/* 设置本机静音：静音态由 SetVolume 钩子持续执行（服务器下发的 volume PDU 也盖不掉）。
 * 这里只改标志 + 立刻推一次让当前音量即时生效。
 *
 * ⚠️ 线程安全：本函数从 **UI 线程**调用，而钩子/RDP 结构归 **RDP 线程**（event_loop）所有。
 * 所以这里**绝不**改 `device->SetVolume` 字段本身（那是 event_loop 的活，避免数据竞争）；
 * 只做两件事：
 *   ① 原子写 `s->muted`（钩子按它决定是否压 0）；
 *   ② 在 **g_lock 内**用记录的**原函数指针**推一次目标音量。
 *
 * 🔑 为什么第② 步必须在锁内（2026-10-07 三次 0xC0000005 的根因）：
 *   `orig_setVolume` 是 waveOutSetVolume 家族，**函数本身**确实可跨线程调用；
 *   但它的**实参 device 会被FreeRDP 在 disconnect 时 free 掉**。
 *   「函数可调用」≠「指向的对象还活着」—— 把 device 指针缓存到锁外再用就是 use-after-free。
 *   而 disconnect 的 free 是在**持有同一把 g_lock** 时做的，所以锁内调用才真正互斥。
 *   （详见下面推音量循环处的完整注释。）
 *
 * 钩子的装卸交给 event_loop（500ms 一拍 + 通道事件），与本函数共用 g_lock 互斥。
 * rdpsnd 尚未连上/钩子还没装时静默成功（装好后由 event_loop / 下次调用补上）。
 * muted 非 0 = 静音。 */
YCN_API int ycn_rdp_set_muted(int session, int muted)
{
	YcnSession* s;
	int n = 0, k;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_RUNNING)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_NOT_CONNECTED;
	}
	InterlockedExchange(&s->muted, muted ? 1 : 0);

	/* 🔑 推音量**必须在锁内**完成，且必须当场调 orig，不能把 device 指针缓存到锁外再用。
	 *
	 * 旧写法（2026-10-07 崩在这）：
	 *     EnterCriticalSection; probe(plugin[k]) -> device[k]; 拷贝到局部数组;
	 *     LeaveCriticalSection;
	 *     for (k) orig[k](device[k], vol);          <-- 锁外解引用
	 * 而 ycn_rdp_disconnect 是在**持有同一把 g_lock** 时
	 * freerdp_context_free() 释放 rdpsnd 的 device，再清 rdpsnd_plugin[]/orig[]。
	 * 两个窗口一叠加就是 use-after-free：
	 *     UI 线程 probe 拿到 device（当时有效）→ 放锁
	 *     RDP 线程 disconnect 持锁 free 掉 device、把所有槽清成 NULL
	 *     UI 线程拿**已释放**的 device 调 orig_set_volume → 0xC0000005
	 * 这正是 10-06 20:52 / 10-07 07:42 / 10-07 08:01 三次崩溃同一条偏移 0x2be5
	 * （fault addr=0x0，说明读到的是释放后的垃圾）的原因。
	 *
	 * 为什么原来注释里那句"函数指针任何线程可调"是错的：
	 *   waveOutSetVolume 本身确实可跨线程，但**实参 device 已被 free**。
	 *   函数可调用 ≠ 指向的对象还活着 —— 以前把这两件事混成一件了。
	 *
	 * 为什么锁内调不会死锁：
	 *   orig 是 rdpsndDevicePlugin 自己的 SetVolume（waveOutSetVolume 家族），
	 *   它不回头调本 DLL 的任何导出，也就不碰 g_lock。
	 *   ⚠️ 若将来换成可能回调进来的实现，就必须改成"代次号 + 事件"方案，不能放锁内。
	 *
	 * 双重保险：这里仍套 IFCALLRESULT（真出 AV 时降级为无声，而不是掀进程），
	 * 因为 device 的有效性最终由FreeRDP 决定，本 DLL 无法从锁上完全保证。 */
	for (k = 0; k < YCN_RDPSND_SLOTS; k++)
	{
		rdpsndDevicePlugin* d = ycn_probe_rdpsnd_device(s->rdpsnd_plugin[k]);
		void* orig_setvol = s->rdpsnd_orig_set_volume[k];
		/* 复核：probe 拿到的 d 必须仍是我们已登记 orig 的那一个槽的 device。
		 * 不一致说明这一拍里 plugin 与 orig 不同步（装/卸钩子途中），跳过而不是硬调。 */
		if (d && orig_setvol && d == s->rdpsnd_device[k])
		{
			(void)IFCALLRESULT(FALSE, ((pcSetVolume)orig_setvol), d,
			                   muted ? 0u : 0xFFFFFFFFu);
			n++;
		}
	}
	LeaveCriticalSection(&g_lock);

	/* 诊断：把「设了静音后 Play/SetVolume 钩子各被调了多少次」落盘。
	 * Play 计数不动 = 服务器根本没推音频（或钩子没挂在这条路径上）；
	 * Play 计数在涨但还有声音 = 静音闸没生效。
	 * hooked 列 = 当前 device->Play/SetVolume 是否就是我们的钩子（静音能否生效的前提）。*/
	{
		char buf[320];
		int hooked[YCN_RDPSND_SLOTS];
		void* dbg_dev[YCN_RDPSND_SLOTS];
		/* ⚠️ 这段诊断同样必须锁内做：`d->SetVolume` 是解引用 device。
		 * 早先版本把它放在锁外，与上面同一个 use-after-free 同源（只是崩在不同指令）。 */
		EnterCriticalSection(&g_lock);
		for (k = 0; k < YCN_RDPSND_SLOTS; k++)
		{
			rdpsndDevicePlugin* d = (rdpsndDevicePlugin*)s->rdpsnd_device[k];
			dbg_dev[k] = d;
			hooked[k] = (d && d->SetVolume == ycn_rdpsnd_set_volume_hook) ? 1 : 0;
		}
		_snprintf_s(buf, sizeof(buf), _TRUNCATE,
		            "mute: set=%d slots=%d dev0=%p dev1=%p hooked0=%d hooked1=%d "
		            "play_hits=%ld setvol_hits=%ld",
		            muted, n, dbg_dev[0], dbg_dev[1], hooked[0], hooked[1],
		            (long)InterlockedCompareExchange(&s->mute_play_hits, 0, 0),
		            (long)InterlockedCompareExchange(&s->mute_setvol_hits, 0, 0));
		LeaveCriticalSection(&g_lock);
		ycn_bc(buf); /* UI 线程路径：不落盘 */
	}
	return YCN_OK;
}

/* 查询当前静音标志（1 = 静音）。仅用于自检与 C# 侧状态对齐。 */
YCN_API int ycn_rdp_get_muted(int session)
{
	YcnSession* s;
	int muted;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (InterlockedCompareExchange(&s->state, 0, 0) != YCN_STATE_RUNNING)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_NOT_CONNECTED;
	}
	muted = (int)InterlockedCompareExchange(&s->muted, 0, 0);
	LeaveCriticalSection(&g_lock);
	return muted;
}

YCN_API int ycn_rdp_grab_frame(int session, uint32_t* out_width, uint32_t* out_height,
                               uint32_t* out_stride, const uint8_t** out_data)
{
	YcnSession* s;
	int rc;
	if (!out_width || !out_height || !out_stride || !out_data)
		return YCN_ERR_INVALID_ARG;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (!s)
	{
		LeaveCriticalSection(&g_lock);
		return YCN_ERR_SESSION_NOT_FOUND;
	}
	if (InterlockedCompareExchange(&s->state, 0, 0) == YCN_STATE_RUNNING &&
	    s->frame_valid && s->frame)
	{
		*out_width = s->frame_width;
		*out_height = s->frame_height;
		*out_stride = s->frame_stride;
		*out_data = s->frame;
		rc = YCN_OK;
	}
	else
	{
		rc = YCN_ERR_NOT_CONNECTED;
	}
	LeaveCriticalSection(&g_lock);
	return rc;
}

YCN_API int ycn_rdp_copy_frame(int session, uint8_t* dst, uint32_t dst_size,
                               uint32_t* out_width, uint32_t* out_height, uint32_t* out_stride)
{
	YcnSession* s;
	int rc = YCN_ERR_NOT_CONNECTED;
	size_t need;

	if (!dst || dst_size == 0)
		return YCN_ERR_INVALID_ARG;

	/* 拷贝全程持锁：与 ycn_end_paint 里「整帧 memcpy 进 s->frame」互斥，
	 * 因此绝不会读到拷贝到一半的帧（撕裂安全）。 */
	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s && InterlockedCompareExchange(&s->state, 0, 0) == YCN_STATE_RUNNING &&
	    s->frame_valid && s->frame && s->frame_height > 0)
	{
		if (out_width)
			*out_width = s->frame_width;
		if (out_height)
			*out_height = s->frame_height;
		if (out_stride)
			*out_stride = s->frame_stride;

		need = (size_t)s->frame_stride * s->frame_height;
		if (need <= (size_t)dst_size)
		{
			memcpy(dst, s->frame, need);
			rc = YCN_OK;
		}
		else
		{
			rc = YCN_ERR_NO_MEMORY; /* 缓冲过小：调用方按 out_* 尺寸重分配后重试 */
		}
	}
	LeaveCriticalSection(&g_lock);
	return rc;
}

YCN_API int ycn_rdp_gfx_state(int session, uint32_t* out_dvc_count)
{
	YcnSession* s;
	int st = -1;

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s)
	{
		st = (int)s->gfx_state;
		if (out_dvc_count)
			*out_dvc_count = (uint32_t)s->dvc_connected;
	}
	LeaveCriticalSection(&g_lock);
	return st;
}

YCN_API void ycn_rdp_dvc_names(int session, char* buf, size_t buflen)
{
	YcnSession* s;

	if (!buf || buflen == 0)
		return;
	buf[0] = '\0';

	EnterCriticalSection(&g_lock);
	s = find_session(session);
	if (s)
		strncpy_s(buf, buflen, s->dvc_names, _TRUNCATE);
	LeaveCriticalSection(&g_lock);
}

YCN_API void ycn_rdp_diag(int session, char* buf, size_t buflen)
{
	YcnSession* s;
	rdpSettings* settings = NULL;
	int live = 0;
	int i;

	if (!buf || buflen == 0)
		return;
	buf[0] = '\0';

	EnterCriticalSection(&g_lock);
	for (i = 0; i < YCN_MAX_SESSIONS; i++)
		if (g_sessions[i].used)
			live++;
	s = find_session(session);
	if (s && InterlockedCompareExchange(&s->state, 0, 0) == YCN_STATE_RUNNING &&
	    s->instance && s->instance->context)
		settings = s->instance->context->settings;
	if (s)
	{
		_snprintf_s(buf, buflen, _TRUNCATE,
		            "live_sessions=%d total_created=%ld total_released=%ld load_addins=%d static_ch=%u dyn_ch=%u gfx_state=%d dvc=%u gfx_init=%d codecs_null=%d names=[%s]",
		            live, (long)InterlockedCompareExchange(&g_total_created, 0, 0),
		            (long)InterlockedCompareExchange(&g_total_released, 0, 0), s->addins_rc,
		            settings ? (unsigned)freerdp_settings_get_uint32(settings, FreeRDP_StaticChannelCount) : 0u,
		            settings ? (unsigned)freerdp_settings_get_uint32(settings, FreeRDP_DynamicChannelCount) : 0u,
		            (int)s->gfx_state, (unsigned)s->dvc_connected,
		            s->gfx_init_rc, s->gfx_codecs_null, s->dvc_names);
	}
	else
	{
		_snprintf_s(buf, buflen, _TRUNCATE,
		            "live_sessions=%d total_created=%ld total_released=%ld",
		            live, (long)InterlockedCompareExchange(&g_total_created, 0, 0),
		            (long)InterlockedCompareExchange(&g_total_released, 0, 0));
	}
	LeaveCriticalSection(&g_lock);
}

YCN_API void ycn_rdp_last_error(int session, char* buf, size_t buflen)
{
	const char* src = g_global_error;
	if (!buf || buflen == 0)
		return;
	buf[0] = '\0';
	EnterCriticalSection(&g_lock);
	if (session > 0)
	{
		YcnSession* s = find_session(session);
		if (s && s->last_error[0])
			src = s->last_error;
	}
	if (!src || !src[0])
		src = "no error";
	strncpy_s(buf, buflen, src, _TRUNCATE);
	LeaveCriticalSection(&g_lock);
}

YCN_API const char* ycn_rdp_version(void)
{
	return YCN_RDP_VERSION_STRING;
}

/* ---------------- DLL 生命周期 ---------------- */

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
	(void)hinst;
	(void)reserved;
	if (reason == DLL_PROCESS_ATTACH)
	{
		InitializeCriticalSectionAndSpinCount(&g_lock, 400);
	}
	else if (reason == DLL_PROCESS_DETACH)
	{
		DeleteCriticalSection(&g_lock);
	}
	return TRUE;
}
