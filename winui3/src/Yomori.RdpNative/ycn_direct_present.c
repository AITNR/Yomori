/* ============================================================
 * Yomori.RdpNative — AVC 直显呈现层（S1）
 *
 * 一条链路只允许一个 D3D11 device：
 *   解码器（FFmpeg D3D11VA）→ NV12 纹理 → ID3D11VideoProcessor → 交换链
 * 因此 device 由本文件创建并在解码上下文建立前注入 FreeRDP，而不是让 FFmpeg
 * 自己建一个再想办法跨 device 共享（S0 已证明该驱动栈不给共享句柄）。
 *
 * 分工：
 *   ycn_dp_publish()  解码线程调用，只存指针，不碰 D3D；
 *   ycn_dp_present()  UI 线程调用，做 VideoProcessorBlt + Present；
 *   ycn_dp_enable()   必须在建连前调用，因为解码后端在建连时就被定死。
 *
 * 租约：一帧 = 一个 AVFrame 引用。present 用完立刻释放；新帧到达时上一帧若还
 * 没被 present（UI 卡顿/窗口不可见），直接丢弃并计数，绝不让解码线程等待。
 * ============================================================ */
#include "ycn_direct_present.h"
#include "ycn_present_layout.h"
#include <dxgi1_3.h>

#include <stdlib.h>
#include <string.h>

#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>

#define YCN_DP_MAX_PRESENTERS 8

/* 与 UI 侧 ISwapChainPanelNative 的 vtable 布局一致：IUnknown 3 个槽之后是
 * SetSwapChain（WinUI3 的 ISwapChainPanelNative 直接继承 IUnknown）。 */
static const GUID YCN_IID_ISwapChainPanelNative = {
	0x63aad0b8, 0x7c24, 0x40ff, { 0x85, 0xa8, 0x64, 0x0d, 0x94, 0x4c, 0xc3, 0x25 }
};

typedef struct YcnSwapChainPanelNativeVtbl
{
	HRESULT(__stdcall* QueryInterface)(void* self, const GUID* iid, void** out);
	ULONG(__stdcall* AddRef)(void* self);
	ULONG(__stdcall* Release)(void* self);
	HRESULT(__stdcall* SetSwapChain)(void* self, IDXGISwapChain* swapChain);
} YcnSwapChainPanelNativeVtbl;

typedef struct YcnSwapChainPanelNative
{
	const YcnSwapChainPanelNativeVtbl* lpVtbl;
} YcnSwapChainPanelNative;

#define YCN_DP_FORMAT_NV12 103u /* DXGI_FORMAT_NV12 */

typedef struct YcnFrameSlot
{
	int used;
	uint64_t seq;
	uint64_t published_tick;
	ycn_dp_frame info;
} YcnFrameSlot;

typedef struct YcnPresenter
{
	int used;
	int session;
	int enabled;
	/* 会话已结束、但 D3D 资源还没拆（拆分点见 ycn_dp_release_session 的注释）。 */
	int orphaned;

	IUnknown* panel;

	IDXGISwapChain1* swapchain;
	ID3D11Texture2D* backbuffer;
	uint32_t chain_width;
	uint32_t chain_height;

	ID3D11VideoProcessorEnumerator* vp_enum;
	ID3D11VideoProcessor* vp;
	ID3D11VideoProcessorOutputView* out_view;
	uint32_t vp_width;
	uint32_t vp_height;
	uint32_t vp_matrix;
	uint32_t vp_range;

	ID3D11VideoProcessorInputView* in_view;
	ID3D11Texture2D* in_texture;
	uint32_t in_subresource;

	/* Frame hand-off. pending->ready happens at the RDPGFX EndFrame boundary so
	 * a half composed frame is never presented. */
	YcnFrameSlot pending;
	YcnFrameSlot ready;
	uint64_t next_seq;

	/* S3：增量直显。
	 * canonical = 累积的 BGRA 目标面（交换链是 flip 模型，Present 之后内容不保证
	 * 保留，所以必须自己维护一份可累积的面）；每帧只把 dirty 矩形转到 canonical，
	 * 再整面 CopyResource 到后备缓冲。转换成本按 dirty 面积走。 */
	ID3D11Texture2D* canonical;
	ID3D11RenderTargetView* canonical_rtv;
	uint32_t canonical_width;
	uint32_t canonical_height;
	int canonical_valid;
	/* 增量中转贴片（scratch）。
	 *
	 * 为什么必须有它：D3D11 视频处理器每次 VideoProcessorBlt 都会先把**整个**
	 * 输出面填成背景色再合成 stream，`VideoProcessorSetStreamDestRect` 只决定
	 * stream 画在哪，管不了面外那片背景。所以直接把小矩形打到 canonical 的
	 * 全幅输出视图上，会把之前累积的其余部分整片擦黑 —— 画面看起来像
	 * 「只有最后一次改动的区域是活的」。正确做法：先转到与矩形同尺寸的贴片，
	 * 再 CopySubresourceRegion 贴回 canonical。
	 *
	 * 贴片尺寸按 64px 粒度向上取整，避免光标/caret 的矩形每帧抖动 1px 就
	 * 重建一整套 (enumerator, processor, output view)。 */
	ID3D11Texture2D* scratch;
	ID3D11VideoProcessorEnumerator* scratch_enum;
	ID3D11VideoProcessor* scratch_vp;
	ID3D11VideoProcessorOutputView* scratch_out_view;
	uint32_t scratch_w;
	uint32_t scratch_h;
	YcnDirtySet dirty;
	/* 增量是否启用。**默认关**：实测每帧只省约 9%（见
	 * docs/rdp-avc-direct-present-s3-2026-11-02.md §2.3），收益不足以承担多一条
	 * 合成路径的风险。开关保留，判据保留，开启由调用方显式决定。 */
	int incremental;
	/* 本帧的 dirty 集合是否已经开过：present 成功后置 0，本帧第一条矩形到达时置 1
	 * 并清空集合。没有这个标志，present 后置的 full=1 会让后续矩形永远加不进来。 */
	int dirty_frame_open;
	uint32_t fit_mode;
	double fit_epsilon;
	float dpi_scale;
	YcnLayoutRect fit_rect;
	uint64_t frames_full;
	uint64_t frames_incremental;
	uint32_t last_dirty_pct;

	uint64_t frames_presented;
	uint64_t frames_dropped;
	uint64_t leases_outstanding;
	int32_t last_hresult;
	char detail[96];
} YcnPresenter;

static ID3D11Device* g_device;
static ID3D11DeviceContext* g_context;
static ID3D11VideoDevice* g_video_device;
static ID3D11VideoContext* g_video_context;
static LARGE_INTEGER g_qpc_freq;
static INIT_ONCE g_device_once = INIT_ONCE_STATIC_INIT;
static int g_device_status; /* 0 = not tried, 1 = ok, -1 = failed */

static YcnPresenter g_presenters[YCN_DP_MAX_PRESENTERS];
static CRITICAL_SECTION g_dp_lock;
static INIT_ONCE g_dp_lock_once = INIT_ONCE_STATIC_INIT;

static void (*g_release_frame)(void* frame);

#define YCN_DP_RELEASE(p)            \
	do                               \
	{                                \
		if (p)                       \
		{                            \
			(p)->lpVtbl->Release(p); \
			(p) = NULL;              \
		}                            \
	} while (0)

static void ycn_dp_set_text(char* dst, size_t size, const char* src)
{
	size_t len;
	if (!dst || (size == 0))
		return;
	if (!src)
		src = "";
	len = strlen(src);
	if (len >= size)
		len = size - 1;
	memcpy(dst, src, len);
	dst[len] = '\0';
}

static uint64_t ycn_dp_now(void)
{
	LARGE_INTEGER value;
	if (!QueryPerformanceCounter(&value))
		return 0;
	return (uint64_t)value.QuadPart;
}

static uint64_t ycn_dp_ticks_to_us(uint64_t ticks)
{
	if (g_qpc_freq.QuadPart <= 0)
		return 0;
	return (ticks * 1000000ull) / (uint64_t)g_qpc_freq.QuadPart;
}

static uint64_t ycn_dp_ticks_to_ns(uint64_t ticks)
{
	if (g_qpc_freq.QuadPart <= 0)
		return 0;
	return (ticks * 1000000000ull) / (uint64_t)g_qpc_freq.QuadPart;
}

static void ycn_dp_ensure_lock(void)
{
	InitializeCriticalSection(&g_dp_lock);
}

static BOOL CALLBACK ycn_dp_lock_init(PINIT_ONCE once, PVOID param, PVOID* ctx)
{
	(void)once;
	(void)param;
	(void)ctx;
	QueryPerformanceFrequency(&g_qpc_freq);
	ycn_dp_ensure_lock();
	return TRUE;
}

/* ---------------------------------------------------------------- device ---- */

static BOOL CALLBACK ycn_dp_device_init(PINIT_ONCE once, PVOID param, PVOID* ctx)
{
	static const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1,
		                                         D3D_FEATURE_LEVEL_11_0,
		                                         D3D_FEATURE_LEVEL_10_1 };
	D3D_FEATURE_LEVEL level = 0;
	HRESULT hr;

	(void)once;
	(void)param;
	(void)ctx;

	if (!g_qpc_freq.QuadPart)
		QueryPerformanceFrequency(&g_qpc_freq);

	/* VIDEO_SUPPORT 是 VideoProcessor 的前提；BGRA 是交换链的前提。两者都只在
	 * 创建时能给，事后补不上，所以这里一次建到位。 */
	hr = D3D11CreateDevice(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL,
	                       D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
	                       levels, (UINT)(sizeof(levels) / sizeof(levels[0])), D3D11_SDK_VERSION,
	                       &g_device, &level, &g_context);
	if (FAILED(hr) || !g_device || !g_context)
	{
		g_device_status = -1;
		return TRUE;
	}

	if (FAILED(g_device->lpVtbl->QueryInterface(g_device, &IID_ID3D11VideoDevice,
	                                            (void**)&g_video_device)))
	{
		g_device_status = -1;
		return TRUE;
	}
	if (FAILED(g_context->lpVtbl->QueryInterface(g_context, &IID_ID3D11VideoContext,
	                                             (void**)&g_video_context)))
	{
		g_device_status = -1;
		return TRUE;
	}

	/* 线程模型：解码在 FreeRDP 事件循环线程，blit/present 在 UI 线程，同一个
	 * device 被两个线程使用。D3D11 默认就为 device/immediate context 加了内部
	 * 互斥（只有显式 D3D11_CREATE_DEVICE_SINGLETHREADED 才会去掉），所以这里
	 * 不需要 ID3D11Multithread；反过来也绝不能改成 SINGLETHREADED。 */

	g_device_status = 1;
	return TRUE;
}

void* ycn_dp_device(void)
{
	InitOnceExecuteOnce(&g_device_once, ycn_dp_device_init, NULL, NULL);
	return (g_device_status == 1) ? (void*)g_device : NULL;
}

void ycn_dp_set_release(void (*release)(void* frame))
{
	g_release_frame = release;
}

static YcnPresenter* ycn_dp_find(int session)
{
	size_t i;
	for (i = 0; i < YCN_DP_MAX_PRESENTERS; i++)
	{
		if (g_presenters[i].used && (g_presenters[i].session == session))
			return &g_presenters[i];
	}
	return NULL;
}

/* 前向声明：槽位回收要用到下面定义的拆解助手（C 里隐式声明会被当成 int）。 */
static void ycn_dp_drop_slot(YcnFrameSlot* slot);
static void ycn_dp_release_chain_views(YcnPresenter* p);
static void ycn_dp_release_scratch(YcnPresenter* p);
static void ycn_dp_reset_processor(YcnPresenter* p);

static YcnPresenter* ycn_dp_find_or_create(int session)
{
	size_t i;
	YcnPresenter* p = ycn_dp_find(session);
	if (p)
		return p;

	for (i = 0; i < YCN_DP_MAX_PRESENTERS; i++)
	{
		if (!g_presenters[i].used)
		{
			memset(&g_presenters[i], 0, sizeof(g_presenters[i]));
			g_presenters[i].used = 1;
			g_presenters[i].fit_mode = (uint32_t)YCN_FIT_LETTERBOX;
			g_presenters[i].fit_epsilon = 1.0;
			g_presenters[i].dpi_scale = 1.0f;
			g_presenters[i].session = session;
			return &g_presenters[i];
		}
	}
	/* 表满：回收一个「会话已结束」的槽位。它们 present 只会返回 DISABLED，
	 * 所以这里拆资源是安全的；活跃槽位绝不回收。 */
	for (i = 0; i < YCN_DP_MAX_PRESENTERS; i++)
	{
		YcnPresenter* stale = &g_presenters[i];
		if (!stale->used || !stale->orphaned || stale->enabled)
			continue;
		ycn_dp_drop_slot(&stale->pending);
		ycn_dp_drop_slot(&stale->ready);
		ycn_dp_reset_processor(stale);
		ycn_dp_release_chain_views(stale);
		YCN_DP_RELEASE(stale->swapchain);
		if (stale->panel)
			stale->panel->lpVtbl->Release(stale->panel);
		memset(stale, 0, sizeof(*stale));
		stale->used = 1;
		stale->session = session;
		return stale;
	}
	return NULL;
}

static void ycn_dp_drop_slot(YcnFrameSlot* slot)
{
	if (!slot)
		return;
	if (slot->used && slot->info.frame && g_release_frame)
	{
		g_release_frame(slot->info.frame);
		EnterCriticalSection(&g_dp_lock);
		{
			YcnPresenter* owner = NULL;
			size_t i;
			for (i = 0; i < YCN_DP_MAX_PRESENTERS; i++)
			{
				if (g_presenters[i].used &&
				    ((&g_presenters[i].pending == slot) || (&g_presenters[i].ready == slot)))
					owner = &g_presenters[i];
			}
			if (owner && (owner->leases_outstanding > 0))
				owner->leases_outstanding--;
		}
		LeaveCriticalSection(&g_dp_lock);
	}
	slot->used = 0;
	slot->info.frame = NULL;
	slot->info.texture = NULL;
}

/* ------------------------------------------------------------------ API ----- */

int ycn_dp_enable(int session)
{
	YcnPresenter* p;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	if (!ycn_dp_device())
		return YCN_DP_STATUS_FAILED;

	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find_or_create(session);
	if (p)
	{
		p->enabled = 1;
		ycn_dp_set_text(p->detail, sizeof(p->detail), "enabled");
	}
	LeaveCriticalSection(&g_dp_lock);
	return p ? YCN_DP_STATUS_OK : YCN_DP_STATUS_FAILED;
}

int ycn_dp_is_enabled(int session)
{
	YcnPresenter* p;
	int enabled = 0;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (p)
		enabled = p->enabled;
	LeaveCriticalSection(&g_dp_lock);
	return enabled;
}

void ycn_dp_disable(int session)
{
	YcnPresenter* p;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (p)
		p->enabled = 0;
	LeaveCriticalSection(&g_dp_lock);

	if (p)
	{
		ycn_dp_drop_slot(&p->pending);
		ycn_dp_drop_slot(&p->ready);
	}
}

int ycn_dp_attach_panel(int session, void* panel)
{
	YcnPresenter* p;
	IUnknown* unknown = (IUnknown*)panel;

	if (!unknown)
		return YCN_DP_STATUS_FAILED;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find_or_create(session);
	if (p)
	{
		if (p->panel)
			p->panel->lpVtbl->Release(p->panel);
		p->panel = unknown;
		unknown->lpVtbl->AddRef(unknown);
	}
	LeaveCriticalSection(&g_dp_lock);
	return p ? YCN_DP_STATUS_OK : YCN_DP_STATUS_FAILED;
}

int ycn_dp_set_layout(int session, uint32_t fit_mode, double epsilon)
{
	YcnPresenter* p;

	if (fit_mode > (uint32_t)YCN_FIT_CROP)
		return -1;
	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (!p)
	{
		LeaveCriticalSection(&g_dp_lock);
		return -1;
	}
	if ((p->fit_mode != fit_mode) || (p->fit_epsilon != epsilon))
	{
		p->fit_mode = fit_mode;
		p->fit_epsilon = epsilon;
		/* 版面变了：下一帧必须整面重画。 */
		p->canonical_valid = 0;
	}
	LeaveCriticalSection(&g_dp_lock);
	return 0;
}

int ycn_dp_set_scale(int session, double composition_scale)
{
	YcnPresenter* p;
	HRESULT hr = S_OK;

	if (!(composition_scale > 0.0))
		composition_scale = 1.0;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (!p)
	{
		LeaveCriticalSection(&g_dp_lock);
		return -1;
	}
	if ((float)composition_scale != p->dpi_scale)
	{
		p->dpi_scale = (float)composition_scale;
		p->canonical_valid = 0;
		if (p->swapchain)
		{
			/* UI 线程才会调到这里；矩阵让 96 DPI 的逻辑像素映射到物理像素。
			 * SetMatrixTransform 在 IDXGISwapChain2 上，必须 QueryInterface。 */
			IDXGISwapChain2* chain2 = NULL;
			float matrix[6];
			DXGI_MATRIX_3X2_F m;
			if (SUCCEEDED(p->swapchain->lpVtbl->QueryInterface(p->swapchain, &IID_IDXGISwapChain2,
			                                                  (void**)&chain2)) &&
			    chain2)
			{
				ycn_layout_dpi_matrix(composition_scale, matrix);
				m._11 = matrix[0];
				m._12 = matrix[1];
				m._21 = matrix[2];
				m._22 = matrix[3];
				m._31 = matrix[4];
				m._32 = matrix[5];
				hr = chain2->lpVtbl->SetMatrixTransform(chain2, &m);
				if (FAILED(hr))
					p->last_hresult = (int32_t)hr;
				chain2->lpVtbl->Release(chain2);
			}
		}
	}
	LeaveCriticalSection(&g_dp_lock);
	return SUCCEEDED(hr) ? 0 : -1;
}

/* 增量直显开关。默认关，理由见 struct 里 incremental 的注释。
 * 开启后 present 会在 dirty 面积 ≤ 60% 且累积面有效时只转 dirty 矩形；
 * 任何一条前置条件不成立都退回整幅，画错的风险只落在「多转」而不是「少画」。 */
int ycn_dp_set_incremental(int session, uint32_t enable)
{
	YcnPresenter* p;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (!p)
	{
		LeaveCriticalSection(&g_dp_lock);
		return -1;
	}
	if (p->incremental != (enable ? 1 : 0))
	{
		p->incremental = enable ? 1 : 0;
		/* 开关状态变化时累积面内容来源可能不同，下一帧整面重画一次。 */
		p->canonical_valid = 0;
		p->dirty.full = 1;
		p->dirty.count = 0;
		p->dirty_frame_open = 0;
	}
	LeaveCriticalSection(&g_dp_lock);
	return 0;
}

int ycn_dp_set_frame_rects(int session, const int32_t* xywh, uint32_t count)
{
	YcnPresenter* p;
	uint32_t i;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (!p)
	{
		LeaveCriticalSection(&g_dp_lock);
		return -1;
	}
	if (!xywh || (count == 0))
	{
		p->dirty.full = 1;
		p->dirty.count = 0;
		LeaveCriticalSection(&g_dp_lock);
		return 0;
	}
	if (count > YCN_LAYOUT_MAX_DIRTY)
		count = YCN_LAYOUT_MAX_DIRTY;
	/* 本帧第一条矩形：开一个新的 dirty 集合。同一帧后续命令继续累加，
	 * 中途因溢出置 full=1 也不会被下一条命令清掉。 */
	if (!p->dirty_frame_open)
	{
		p->dirty_frame_open = 1;
		p->dirty.full = 0;
		p->dirty.count = 0;
	}
	for (i = 0; i < count; i++)
	{
		const YcnLayoutRect r = { xywh[i * 4 + 0], xywh[i * 4 + 1], xywh[i * 4 + 2],
			                      xywh[i * 4 + 3] };
		if (p->dirty.full)
			break;
		if (!ycn_dirty_add(&p->dirty, r, (int32_t)p->chain_width, (int32_t)p->chain_height))
		{
			p->dirty.full = 1;
			p->dirty.count = 0;
		}
	}
	LeaveCriticalSection(&g_dp_lock);
	return 0;
}

int ycn_dp_publish(int session, void* freerdp_frame)
{
	const ycn_dp_frame* frame = (const ycn_dp_frame*)freerdp_frame;
	YcnPresenter* p;
	int taken = 0;

	if (!frame || !frame->frame || !frame->texture)
		return 0;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	/* 只收 NV12：P010/其他格式走 CPU 回读，不做“猜色域”的兼容。 */
	if (p && p->enabled && (frame->sw_format != 5u) && (frame->width > 0) &&
	    (frame->height > 0))
	{
		/* 上一帧还没被 present：丢弃它，解码线程绝不等 UI。 */
		if (p->pending.used)
		{
			if (p->pending.info.frame && g_release_frame)
				g_release_frame(p->pending.info.frame);
			p->frames_dropped++;
			if (p->leases_outstanding > 0)
				p->leases_outstanding--;
		}
		memset(&p->pending, 0, sizeof(p->pending));
		p->pending.used = 1;
		p->pending.seq = ++p->next_seq;
		p->pending.published_tick = ycn_dp_now();
		memcpy(&p->pending.info, frame, sizeof(ycn_dp_frame));
		p->leases_outstanding++;
		taken = 1;
	}
	LeaveCriticalSection(&g_dp_lock);
	return taken;
}

void ycn_dp_end_frame(int session)
{
	YcnPresenter* p;
	void* displaced = NULL;
	uint64_t displaced_leases = 0;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (p && p->pending.used)
	{
		if (p->ready.used)
		{
			displaced = p->ready.info.frame;
			displaced_leases = 1;
			p->frames_dropped++;
		}
		p->ready = p->pending;
		memset(&p->pending, 0, sizeof(p->pending));
	}
	LeaveCriticalSection(&g_dp_lock);

	if (displaced && g_release_frame)
	{
		g_release_frame(displaced);
		EnterCriticalSection(&g_dp_lock);
		if (p && (p->leases_outstanding >= displaced_leases))
			p->leases_outstanding -= displaced_leases;
		LeaveCriticalSection(&g_dp_lock);
	}
}

/* ------------------------------------------------------------- swap chain --- */

static void ycn_dp_release_canonical(YcnPresenter* p)
{
	if (!p)
		return;
	YCN_DP_RELEASE(p->canonical_rtv);
	YCN_DP_RELEASE(p->canonical);
	p->canonical_width = 0;
	p->canonical_height = 0;
	p->canonical_valid = 0;
	ycn_dirty_reset(&p->dirty);
}

static void ycn_dp_release_chain_views(YcnPresenter* p)
{
	YCN_DP_RELEASE(p->out_view);
	YCN_DP_RELEASE(p->backbuffer);
	/* 贴片受 canonical 尺寸约束（ensure_scratch 会按 canonical 钳位），
	 * 累积面一换就必须一起丢掉，避免留着按旧尺寸建的输出视图。 */
	ycn_dp_release_scratch(p);
	ycn_dp_release_canonical(p);
}

static int ycn_dp_ensure_swapchain(YcnPresenter* p, uint32_t width, uint32_t height)
{
	DXGI_SWAP_CHAIN_DESC1 desc;
	IDXGIDevice* dxgi_device = NULL;
	IDXGIAdapter* adapter = NULL;
	IDXGIFactory2* factory = NULL;
	YcnSwapChainPanelNative* panel = NULL;
	HRESULT hr;
	int status = YCN_DP_STATUS_FAILED;

	if (p->swapchain && (p->chain_width == width) && (p->chain_height == height))
		return YCN_DP_STATUS_OK;

	ycn_dp_release_chain_views(p);
	/* 视频处理器的输出尺寸是 enumerator 的静态属性，面板尺寸一变就必须重建整套
	 * processor：否则拿旧 enumerator 去给新尺寸的 canonical 建输出视图会被驱动
	 * 判 E_INVALIDARG，表现为「改窗口大小后画面全黑」。 */
	ycn_dp_reset_processor(p);

	if (p->swapchain)
	{
		hr = p->swapchain->lpVtbl->ResizeBuffers(p->swapchain, 0, width, height,
		                                        DXGI_FORMAT_B8G8R8A8_UNORM, 0);
		if (FAILED(hr))
		{
			YCN_DP_RELEASE(p->swapchain);
			p->last_hresult = (int32_t)hr;
		}
	}

	if (!p->swapchain)
	{
		if (FAILED(g_device->lpVtbl->QueryInterface(g_device, &IID_IDXGIDevice,
		                                            (void**)&dxgi_device)))
			return status;
		if (FAILED(dxgi_device->lpVtbl->GetAdapter(dxgi_device, &adapter)))
			goto done;
		if (FAILED(adapter->lpVtbl->GetParent(adapter, &IID_IDXGIFactory2, (void**)&factory)))
			goto done;

		memset(&desc, 0, sizeof(desc));
		desc.Width = width;
		desc.Height = height;
		desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
		desc.SampleDesc.Count = 1;
		desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		desc.BufferCount = 2;
		desc.Scaling = DXGI_SCALING_STRETCH;
		desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
		desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

		hr = factory->lpVtbl->CreateSwapChainForComposition(factory, (IUnknown*)g_device, &desc,
		                                                   NULL, &p->swapchain);
		if (FAILED(hr) || !p->swapchain)
		{
			p->last_hresult = (int32_t)hr;
			ycn_dp_set_text(p->detail, sizeof(p->detail), "create-swapchain-failed");
			p->swapchain = NULL;
			goto done;
		}

		/* SetSwapChain 必须在 UI 线程，调用方保证。 */
		hr = p->panel->lpVtbl->QueryInterface(p->panel, &YCN_IID_ISwapChainPanelNative,
		                                     (void**)&panel);
		if (FAILED(hr) || !panel)
		{
			p->last_hresult = (int32_t)hr;
			ycn_dp_set_text(p->detail, sizeof(p->detail), "no-swapchain-panel-native");
			YCN_DP_RELEASE(p->swapchain);
			goto done;
		}
		hr = panel->lpVtbl->SetSwapChain(panel, (IDXGISwapChain*)p->swapchain);
		panel->lpVtbl->Release(panel);
		if (FAILED(hr))
		{
			p->last_hresult = (int32_t)hr;
			ycn_dp_set_text(p->detail, sizeof(p->detail), "set-swapchain-failed");
			YCN_DP_RELEASE(p->swapchain);
			goto done;
		}
	}

	if (FAILED(p->swapchain->lpVtbl->GetBuffer(p->swapchain, 0, &IID_ID3D11Texture2D,
	                                           (void**)&p->backbuffer)))
	{
		ycn_dp_set_text(p->detail, sizeof(p->detail), "no-backbuffer");
		goto done;
	}

	p->chain_width = width;
	p->chain_height = height;
	status = YCN_DP_STATUS_OK;

done:
	YCN_DP_RELEASE(factory);
	YCN_DP_RELEASE(adapter);
	YCN_DP_RELEASE(dxgi_device);
	return status;
}

/* --------------------------------------------------- video processor ---- */

static void ycn_dp_release_scratch(YcnPresenter* p)
{
	if (!p)
		return;
	YCN_DP_RELEASE(p->scratch_out_view);
	YCN_DP_RELEASE(p->scratch_vp);
	YCN_DP_RELEASE(p->scratch_enum);
	YCN_DP_RELEASE(p->scratch);
	p->scratch_w = 0;
	p->scratch_h = 0;
}

static void ycn_dp_reset_processor(YcnPresenter* p)
{
	YCN_DP_RELEASE(p->in_view);
	YCN_DP_RELEASE(p->vp_enum);
	YCN_DP_RELEASE(p->vp);
	ycn_dp_release_scratch(p);
	p->in_texture = NULL;
	p->in_subresource = 0xFFFFFFFFu;
	p->vp_width = 0;
	p->vp_height = 0;
	p->vp_matrix = YCN_DP_MATRIX_UNKNOWN;
	p->vp_range = YCN_DP_RANGE_UNKNOWN;
}

static HRESULT ycn_dp_create_processor(YcnPresenter* p, uint32_t width, uint32_t height)
{
	D3D11_VIDEO_PROCESSOR_CONTENT_DESC content;
	HRESULT hr;

	ycn_dp_reset_processor(p);

	memset(&content, 0, sizeof(content));
	content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
	content.InputWidth = width;
	content.InputHeight = height;
	content.OutputWidth = p->chain_width ? p->chain_width : width;
	content.OutputHeight = p->chain_height ? p->chain_height : height;
	content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
	/* 帧率必须是合法有理数：全零的 0/0 会被驱动判为 E_INVALIDARG。 */
	content.InputFrameRate.Numerator = 60;
	content.InputFrameRate.Denominator = 1;
	content.OutputFrameRate.Numerator = 60;
	content.OutputFrameRate.Denominator = 1;

	hr = g_video_device->lpVtbl->CreateVideoProcessorEnumerator(g_video_device, &content,
	                                                           &p->vp_enum);
	if (FAILED(hr) || !p->vp_enum)
		return FAILED(hr) ? hr : E_FAIL;

	hr = g_video_device->lpVtbl->CreateVideoProcessor(g_video_device, p->vp_enum, 0, &p->vp);
	if (FAILED(hr) || !p->vp)
		return FAILED(hr) ? hr : E_FAIL;

	p->vp_width = width;
	p->vp_height = height;
	return S_OK;
}

/* 色彩/帧格式设置与具体 processor 无关：主 processor 与 scratch processor
 * 必须看到完全相同的色彩解释，否则增量帧和整幅帧会出现色偏跳变。 */
static void ycn_dp_set_color_space(ID3D11VideoProcessor* vp, uint32_t matrix, uint32_t range)
{
	D3D11_VIDEO_PROCESSOR_COLOR_SPACE in_space;
	D3D11_VIDEO_PROCESSOR_COLOR_SPACE out_space;

	if (!vp || !g_video_context)
		return;

	memset(&in_space, 0, sizeof(in_space));
	/* YCbCr_Matrix: 0 = BT.601, 1 = BT.709. Nominal_Range: 0 = 0-255, 1 = 16-235. */
	in_space.YCbCr_Matrix = (matrix == YCN_DP_MATRIX_BT709) ? 1 : 0;
	in_space.Nominal_Range = (range == YCN_DP_RANGE_LIMITED) ? 1 : 0;
	g_video_context->lpVtbl->VideoProcessorSetStreamColorSpace(g_video_context, vp, 0, &in_space);

	memset(&out_space, 0, sizeof(out_space));
	out_space.RGB_Range = 0;      /* 输出满量程 RGB */
	out_space.YCbCr_Matrix = 1;   /* 与输出无关，给个合法值 */
	out_space.Nominal_Range = 0;
	g_video_context->lpVtbl->VideoProcessorSetOutputColorSpace(g_video_context, vp, &out_space);

	g_video_context->lpVtbl->VideoProcessorSetStreamFrameFormat(
	    g_video_context, vp, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
	g_video_context->lpVtbl->VideoProcessorSetStreamAutoProcessingMode(g_video_context, vp, 0,
	                                                                  FALSE);
}

static HRESULT ycn_dp_apply_color(YcnPresenter* p, uint32_t matrix, uint32_t range)
{
	RECT target;

	if (!p->vp || !g_video_context)
		return E_FAIL;

	ycn_dp_set_color_space(p->vp, matrix, range);
	ycn_dp_set_color_space(p->scratch_vp, matrix, range);

	if (p->backbuffer && p->chain_width && p->chain_height)
	{
		target.left = 0;
		target.top = 0;
		target.right = (LONG)p->chain_width;
		target.bottom = (LONG)p->chain_height;
		g_video_context->lpVtbl->VideoProcessorSetOutputTargetRect(g_video_context, p->vp, TRUE,
		                                                          &target);
	}

	p->vp_matrix = matrix;
	p->vp_range = range;
	return S_OK;
}

static HRESULT ycn_dp_input_view(YcnPresenter* p, ID3D11Texture2D* texture, uint32_t subresource)
{
	D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC desc;
	HRESULT hr;

	if (p->in_view && (p->in_texture == texture) && (p->in_subresource == subresource))
		return S_OK;

	YCN_DP_RELEASE(p->in_view);
	p->in_texture = texture;
	p->in_subresource = subresource;

	memset(&desc, 0, sizeof(desc));
	desc.FourCC = 0;
	desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
	desc.Texture2D.MipSlice = 0;
	desc.Texture2D.ArraySlice = subresource;

	hr = g_video_device->lpVtbl->CreateVideoProcessorInputView(g_video_device, (ID3D11Resource*)texture,
	                                                          p->vp_enum, &desc, &p->in_view);
	return hr;
}

static HRESULT ycn_dp_output_view(YcnPresenter* p)
{
	D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC desc;
	HRESULT hr;

	if (p->out_view && p->canonical)
		return S_OK;
	if (!p->canonical)
		return E_FAIL;

	memset(&desc, 0, sizeof(desc));
	desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
	desc.Texture2D.MipSlice = 0;
	hr = g_video_device->lpVtbl->CreateVideoProcessorOutputView(
	    g_video_device, (ID3D11Resource*)p->canonical, p->vp_enum, &desc, &p->out_view);
	return hr;
}

static HRESULT ycn_dp_blt(YcnPresenter* p)
{
	D3D11_VIDEO_PROCESSOR_STREAM stream;
	memset(&stream, 0, sizeof(stream));
	stream.Enable = TRUE;
	stream.OutputIndex = 0;
	stream.InputFrameOrField = 0;
	stream.PastFrames = 0;
	stream.FutureFrames = 0;
	stream.pInputSurface = p->in_view;

	return g_video_context->lpVtbl->VideoProcessorBlt(g_video_context, p->vp, p->out_view, 0, 1,
	                                                  &stream);
}

static HRESULT ycn_dp_ensure_canonical(YcnPresenter* p)
{
	D3D11_TEXTURE2D_DESC desc;
	HRESULT hr;

	if (!p->backbuffer || !p->chain_width || !p->chain_height)
		return E_FAIL;
	if (p->canonical && (p->canonical_width == p->chain_width) &&
	    (p->canonical_height == p->chain_height))
		return S_OK;

	ycn_dp_release_canonical(p);
	memset(&desc, 0, sizeof(desc));
	desc.Width = p->chain_width;
	desc.Height = p->chain_height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	/* 视频处理器的输出视图要求资源可做渲染目标；黑边靠 ClearRenderTargetView 画。 */
	desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &p->canonical);
	if (FAILED(hr) || !p->canonical)
	{
		p->canonical = NULL;
		return FAILED(hr) ? hr : E_FAIL;
	}
	hr = g_device->lpVtbl->CreateRenderTargetView(g_device, (ID3D11Resource*)p->canonical, NULL,
	                                             &p->canonical_rtv);
	if (FAILED(hr))
	{
		ycn_dp_release_canonical(p);
		return hr;
	}
	p->canonical_width = p->chain_width;
	p->canonical_height = p->chain_height;
	p->canonical_valid = 0;
	return S_OK;
}

/* 一次只转一个矩形：源矩形在解码纹理里，目标矩形在 canonical 里。
 * VideoProcessor 的 stream source/dest rect 正好表达这件事，不需要额外着色器。 */
static HRESULT ycn_dp_blt_rect(YcnPresenter* p, const RECT* src, const RECT* dst)
{
	/* VideoProcessorSetStreamXXXRect 返回 void：失败只可能来自 Blt 本身。 */
	if (src)
		g_video_context->lpVtbl->VideoProcessorSetStreamSourceRect(g_video_context, p->vp, 0, TRUE,
		                                                          src);
	else
		g_video_context->lpVtbl->VideoProcessorSetStreamSourceRect(g_video_context, p->vp, 0, FALSE,
		                                                          NULL);

	if (dst)
		g_video_context->lpVtbl->VideoProcessorSetStreamDestRect(g_video_context, p->vp, 0, TRUE,
		                                                        dst);
	else
		g_video_context->lpVtbl->VideoProcessorSetStreamDestRect(g_video_context, p->vp, 0, FALSE,
		                                                        NULL);

	return ycn_dp_blt(p);
}

/* 贴片尺寸粒度：矩形按 64px 向上取整。
 * 取 64 而不是 16/32：小贴片的固定开销（一次 Blt + 一次 Subresource 复制）与
 * 面积关系不大，贴片太小会被固定开销吃掉收益；取 64 又能让「光标 + caret +
 * 小窗口边」这类常见脏区落在同一档，避免每帧重建。 */
#define YCN_DP_SCRATCH_GRAIN 64u

static HRESULT ycn_dp_ensure_scratch(YcnPresenter* p, uint32_t need_w, uint32_t need_h)
{
	D3D11_TEXTURE2D_DESC desc;
	D3D11_VIDEO_PROCESSOR_CONTENT_DESC content;
	D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ov_desc;
	uint32_t w, h;
	HRESULT hr;

	if (!p->vp || !p->canonical)
		return E_FAIL;

	w = ((need_w + YCN_DP_SCRATCH_GRAIN - 1u) / YCN_DP_SCRATCH_GRAIN) * YCN_DP_SCRATCH_GRAIN;
	h = ((need_h + YCN_DP_SCRATCH_GRAIN - 1u) / YCN_DP_SCRATCH_GRAIN) * YCN_DP_SCRATCH_GRAIN;
	if (w > p->canonical_width)
		w = p->canonical_width;
	if (h > p->canonical_height)
		h = p->canonical_height;
	if ((w == 0) || (h == 0))
		return E_INVALIDARG;

	if (p->scratch && p->scratch_out_view && (p->scratch_w >= w) && (p->scratch_h >= h))
		return S_OK;

	ycn_dp_release_scratch(p);

	memset(&desc, 0, sizeof(desc));
	desc.Width = w;
	desc.Height = h;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &p->scratch);
	if (FAILED(hr) || !p->scratch)
	{
		p->scratch = NULL;
		return FAILED(hr) ? hr : E_FAIL;
	}

	/* 贴片需要自己的 enumerator：输出尺寸是 enumerator 的静态属性，
	 * 用主 enumerator（输出 = 交换链尺寸）去建一个更小的输出视图会被驱动拒绝。 */
	memset(&content, 0, sizeof(content));
	content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
	content.InputWidth = p->vp_width;
	content.InputHeight = p->vp_height;
	content.OutputWidth = w;
	content.OutputHeight = h;
	content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
	content.InputFrameRate.Numerator = 60;
	content.InputFrameRate.Denominator = 1;
	content.OutputFrameRate.Numerator = 60;
	content.OutputFrameRate.Denominator = 1;
	hr = g_video_device->lpVtbl->CreateVideoProcessorEnumerator(g_video_device, &content,
	                                                           &p->scratch_enum);
	if (FAILED(hr) || !p->scratch_enum)
		goto fail;
	hr = g_video_device->lpVtbl->CreateVideoProcessor(g_video_device, p->scratch_enum, 0,
	                                                  &p->scratch_vp);
	if (FAILED(hr) || !p->scratch_vp)
		goto fail;

	memset(&ov_desc, 0, sizeof(ov_desc));
	ov_desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
	ov_desc.Texture2D.MipSlice = 0;
	hr = g_video_device->lpVtbl->CreateVideoProcessorOutputView(
	    g_video_device, (ID3D11Resource*)p->scratch, p->scratch_enum, &ov_desc,
	    &p->scratch_out_view);
	if (FAILED(hr) || !p->scratch_out_view)
		goto fail;

	/* 色彩解释必须与主 processor 完全一致，否则增量帧与整幅帧会出现色偏跳变。 */
	ycn_dp_set_color_space(p->scratch_vp, p->vp_matrix, p->vp_range);

	p->scratch_w = w;
	p->scratch_h = h;
	return S_OK;

fail:
	ycn_dp_release_scratch(p);
	return FAILED(hr) ? hr : E_FAIL;
}

/* 把一个目标矩形（canonical 坐标）转好并贴回 canonical。
 *
 * 必须两步（转到贴片再复制回去），不能直接打 canonical 的输出视图，
 * 原因见 struct 里 scratch 那段注释。 */
static HRESULT ycn_dp_convert_rect(YcnPresenter* p, const RECT* src, const YcnLayoutRect dst)
{
	D3D11_VIDEO_PROCESSOR_STREAM stream;
	D3D11_BOX box;
	RECT dst_rect;
	HRESULT hr;

	if ((dst.w <= 0) || (dst.h <= 0))
		return S_OK;

	hr = ycn_dp_ensure_scratch(p, (uint32_t)dst.w, (uint32_t)dst.h);
	if (FAILED(hr))
		return hr;

	if (src)
		g_video_context->lpVtbl->VideoProcessorSetStreamSourceRect(
		    g_video_context, p->scratch_vp, 0, TRUE, src);
	else
		g_video_context->lpVtbl->VideoProcessorSetStreamSourceRect(
		    g_video_context, p->scratch_vp, 0, FALSE, NULL);

	dst_rect.left = 0;
	dst_rect.top = 0;
	dst_rect.right = (LONG)dst.w;
	dst_rect.bottom = (LONG)dst.h;
	g_video_context->lpVtbl->VideoProcessorSetStreamDestRect(g_video_context, p->scratch_vp, 0,
	                                                        TRUE, &dst_rect);

	memset(&stream, 0, sizeof(stream));
	stream.Enable = TRUE;
	stream.pInputSurface = p->in_view;
	hr = g_video_context->lpVtbl->VideoProcessorBlt(g_video_context, p->scratch_vp,
	                                               p->scratch_out_view, 0, 1, &stream);
	if (FAILED(hr))
		return hr;

	box.left = 0;
	box.top = 0;
	box.front = 0;
	box.right = (UINT)dst.w;
	box.bottom = (UINT)dst.h;
	box.back = 1;
	g_context->lpVtbl->CopySubresourceRegion(g_context, (ID3D11Resource*)p->canonical, 0,
	                                         (UINT)dst.x, (UINT)dst.y, 0,
	                                         (ID3D11Resource*)p->scratch, 0, &box);
	return S_OK;
}
/* 目标矩形 -> 源矩形（fit 变换的逆映射）。整数化时两端各放宽 1px，
 * 宁可多转一行也不留缝。 */
static RECT ycn_dp_src_rect_for(const YcnLayoutRect fit, int32_t src_w, int32_t src_h,
                                const YcnLayoutRect dst)
{
	RECT r;
	const double sx = (double)src_w / (double)((fit.w > 0) ? fit.w : 1);
	const double sy = (double)src_h / (double)((fit.h > 0) ? fit.h : 1);
	int32_t x0 = (int32_t)((double)(dst.x - fit.x) * sx) - 1;
	int32_t y0 = (int32_t)((double)(dst.y - fit.y) * sy) - 1;
	int32_t x1 = (int32_t)((double)(dst.x - fit.x + dst.w) * sx + 0.5) + 1;
	int32_t y1 = (int32_t)((double)(dst.y - fit.y + dst.h) * sy + 0.5) + 1;

	if (x0 < 0)
		x0 = 0;
	if (y0 < 0)
		y0 = 0;
	if (x1 > src_w)
		x1 = src_w;
	if (y1 > src_h)
		y1 = src_h;
	if (x1 <= x0)
		x1 = x0 + 1;
	if (y1 <= y0)
		y1 = y0 + 1;

	r.left = x0;
	r.top = y0;
	r.right = x1;
	r.bottom = y1;
	return r;
}

/* --------------------------------------------------------------- present ---- */

int ycn_dp_present(int session, ycn_direct_present_result* out)
{
	YcnPresenter* p;
	YcnFrameSlot slot;
	D3D11_TEXTURE2D_DESC tex_desc;
	ID3D11Device* texture_device = NULL;
	HRESULT hr = S_OK;
	uint64_t start, convert_start, convert_ticks = 0, present_ticks = 0, idle_ticks = 0;
	uint32_t color_inferred = 0;
	const uint32_t out_size = (out && (out->size >= sizeof(*out))) ? out->size : 0;
	int status;

	if (out_size)
		memset(out, 0, sizeof(*out));

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (!p || !p->enabled)
	{
		LeaveCriticalSection(&g_dp_lock);
		if (out_size)
		{
			out->size = out_size;
			out->version = YCN_DP_API_VERSION;
			out->status = YCN_DP_STATUS_DISABLED;
		}
		return YCN_DP_STATUS_DISABLED;
	}
	if (!p->ready.used)
	{
		LeaveCriticalSection(&g_dp_lock);
		if (out_size)
		{
			out->size = out_size;
			out->version = YCN_DP_API_VERSION;
			out->status = YCN_DP_STATUS_NO_FRAME;
		}
		return YCN_DP_STATUS_NO_FRAME;
	}
	slot = p->ready;
	memset(&p->ready, 0, sizeof(p->ready));
	LeaveCriticalSection(&g_dp_lock);

	start = ycn_dp_now();
	if (slot.published_tick && start > slot.published_tick)
		idle_ticks = start - slot.published_tick;

	status = YCN_DP_STATUS_FAILED;
	ycn_dp_set_text(p->detail, sizeof(p->detail), "");

	if (!g_device || !g_video_device || !g_video_context)
	{
		ycn_dp_set_text(p->detail, sizeof(p->detail), "device-unavailable");
		goto finish;
	}
	if (!p->panel)
	{
		ycn_dp_set_text(p->detail, sizeof(p->detail), "panel-not-attached");
		status = YCN_DP_STATUS_NEED_PANEL;
		goto finish;
	}
	if (!slot.info.texture)
	{
		ycn_dp_set_text(p->detail, sizeof(p->detail), "no-texture");
		goto finish;
	}

	/* 纹理必须属于同一个 device，否则一切“零拷贝”都是自欺。 */
	((ID3D11Texture2D*)slot.info.texture)->lpVtbl->GetDevice((ID3D11Texture2D*)slot.info.texture,
	                                                       &texture_device);
	if (texture_device != g_device)
	{
		if (texture_device)
			texture_device->lpVtbl->Release(texture_device);
		ycn_dp_set_text(p->detail, sizeof(p->detail), "texture-device-mismatch");
		goto finish;
	}
	texture_device->lpVtbl->Release(texture_device);

	memset(&tex_desc, 0, sizeof(tex_desc));
	((ID3D11Texture2D*)slot.info.texture)->lpVtbl->GetDesc((ID3D11Texture2D*)slot.info.texture,
	                                                      &tex_desc);
	if (tex_desc.Format != (DXGI_FORMAT)YCN_DP_FORMAT_NV12)
	{
		ycn_dp_set_text(p->detail, sizeof(p->detail), "unsupported-texture-format");
		goto finish;
	}

	{
		const uint32_t width = slot.info.width ? slot.info.width : tex_desc.Width;
		const uint32_t height = slot.info.height ? slot.info.height : tex_desc.Height;

		if (!width || !height)
		{
			ycn_dp_set_text(p->detail, sizeof(p->detail), "no-dimensions");
			goto finish;
		}

		hr = (HRESULT)ycn_dp_ensure_swapchain(p, width, height);
		if (hr != S_OK)
			goto finish;

		if (!p->vp || (p->vp_width != width) || (p->vp_height != height))
		{
			hr = ycn_dp_create_processor(p, width, height);
			if (FAILED(hr))
			{
				p->last_hresult = (int32_t)hr;
				ycn_dp_set_text(p->detail, sizeof(p->detail), "no-video-processor");
				goto finish;
			}
			p->vp_matrix = YCN_DP_MATRIX_UNKNOWN;
			p->vp_range = YCN_DP_RANGE_UNKNOWN;
		}

		/* 色域/量化范围优先用码流的 VUI；**未标注时按分辨率推断**，不拒绝整帧。
		 *
		 * 为什么不拒绝：RDP 的 AVC420 流普遍不带 VUI 色彩信息，拒绝就等于
		 * 这条会话永远上不了屏（直显恒 NoFrame → 看门狗回退托管），收益归零。
		 * 推断规则是行业惯例：≥720 行按 BT.709，否则 BT.601；量化范围缺省按
		 * limited（MPEG）。这是**推断**，不是读到的事实 —— 结果里
		 * color_inferred=1，诊断能看出区别。BT.2020 仍不猜（没有 10bit 源
		 * 时走到这里是上游换了编码器），继续回退 GDI。 */
		{
			const uint32_t src_matrix = slot.info.color_matrix;
			const uint32_t src_range = slot.info.color_range;
			uint32_t matrix = src_matrix;
			uint32_t range = src_range;

			if (matrix == YCN_DP_MATRIX_UNKNOWN)
			{
				matrix = (height >= 720u) ? YCN_DP_MATRIX_BT709 : YCN_DP_MATRIX_BT601;
				color_inferred = 1u;
			}
			if (range == YCN_DP_RANGE_UNKNOWN)
			{
				range = YCN_DP_RANGE_LIMITED;
				color_inferred = 1u;
			}

			if (matrix == YCN_DP_MATRIX_BT2020)
			{
				ycn_dp_set_text(p->detail, sizeof(p->detail), "bt2020-not-supported");
				goto finish;
			}

			if ((p->vp_matrix != matrix) || (p->vp_range != range))
			{
				hr = ycn_dp_apply_color(p, matrix, range);
				if (FAILED(hr))
				{
					p->last_hresult = (int32_t)hr;
					ycn_dp_set_text(p->detail, sizeof(p->detail), "color-space-failed");
					goto finish;
				}
			}

		}

		hr = ycn_dp_ensure_canonical(p);
		if (FAILED(hr))
		{
			p->last_hresult = (int32_t)hr;
			ycn_dp_set_text(p->detail, sizeof(p->detail), "no-canonical");
			goto finish;
		}

		hr = ycn_dp_input_view(p, (ID3D11Texture2D*)slot.info.texture, slot.info.subresource);
		if (FAILED(hr))
		{
			p->last_hresult = (int32_t)hr;
			ycn_dp_set_text(p->detail, sizeof(p->detail), "no-input-view");
			goto finish;
		}

		hr = ycn_dp_output_view(p);
		if (FAILED(hr))
		{
			p->last_hresult = (int32_t)hr;
			ycn_dp_set_text(p->detail, sizeof(p->detail), "no-output-view");
			goto finish;
		}

		/* 版面：源纹理按 fit 模式映射到交换链。版面变了（面板尺寸/DPI/源尺寸）
		 * 一律整面重画 —— 否则会留下上一版面的残留。 */
		{
			const YcnLayoutRect fit =
			    ycn_layout_fit((int32_t)width, (int32_t)height, (int32_t)p->chain_width,
			                   (int32_t)p->chain_height, (YcnFitMode)p->fit_mode, p->fit_epsilon);
			if (ycn_layout_is_empty(fit))
			{
				ycn_dp_set_text(p->detail, sizeof(p->detail), "empty-fit-rect");
				goto finish;
			}
			if ((fit.x != p->fit_rect.x) || (fit.y != p->fit_rect.y) ||
			    (fit.w != p->fit_rect.w) || (fit.h != p->fit_rect.h) || !p->canonical_valid)
			{
				const FLOAT black[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
				g_context->lpVtbl->ClearRenderTargetView(g_context, p->canonical_rtv, black);
				p->fit_rect = fit;
				p->canonical_valid = 0;
				p->dirty.full = 1;
				p->dirty.count = 0;
			}

			p->last_dirty_pct = ycn_dirty_area_percent(&p->dirty, (int32_t)p->chain_width,
			                                          (int32_t)p->chain_height);

			/* 增量条件：有累积面、有 dirty 信息、dirty 面积不大。
			 * 任一条不成立就整面转一次 —— 计算错误只会浪费性能，不会画错。 */
			const int incremental =
			    p->incremental && p->canonical_valid && !p->dirty.full &&
			    ycn_dirty_is_incremental_worthwhile(&p->dirty, (int32_t)p->chain_width,
			                                        (int32_t)p->chain_height, 60u);

			convert_start = ycn_dp_now();
			if (incremental)
			{
				for (uint32_t i = 0; i < p->dirty.count; i++)
				{
					/* dirty 只在 fit 区域内才需要转；黑边部分本来就是黑的。 */
					const YcnLayoutRect rect =
					    ycn_layout_intersect(p->dirty.rects[i], fit);
					if (ycn_layout_is_empty(rect))
						continue;
					const RECT src_rect = ycn_dp_src_rect_for(fit, (int32_t)width, (int32_t)height,
					                                          rect);
					hr = ycn_dp_convert_rect(p, &src_rect, rect);
					if (FAILED(hr))
						break;
				}
				/* dirty 全落在黑边上时一个矩形都不转，也是合法增量帧：
				 * canonical 仍有效，这一帧只是没有内容变化。 */
				if (SUCCEEDED(hr))
					p->frames_incremental++;
			}
			else
			{
				const RECT dst_rect = { (LONG)fit.x, (LONG)fit.y, (LONG)(fit.x + fit.w),
					                    (LONG)(fit.y + fit.h) };
				hr = ycn_dp_blt_rect(p, NULL, &dst_rect);
				if (SUCCEEDED(hr))
					p->frames_full++;
			}
			convert_ticks = ycn_dp_now() - convert_start;

			if (FAILED(hr))
			{
				p->last_hresult = (int32_t)hr;
				ycn_dp_set_text(p->detail, sizeof(p->detail), "videoprocessor-blt-failed");
				/* 视图可能已失效，下一帧重建。 */
				YCN_DP_RELEASE(p->in_view);
				p->in_texture = NULL;
				/* 累积面已不可信：下一帧必须整面重画。 */
				p->canonical_valid = 0;
				goto finish;
			}

			p->canonical_valid = 1;
			/* 下一帧的 dirty 集合从零开始累积；没有新矩形到达就按整幅处理。 */
			p->dirty.full = 1;
			p->dirty.count = 0;
			p->dirty_frame_open = 0;

			/* flip 模型的后备缓冲内容不跨帧保留，所以每帧仍要把累积面整面
			 * 拷过去。这一步是 GPU 显存到显存的复制（无格式转换），
			 * 比每帧重转整幅便宜得多。 */
			g_context->lpVtbl->CopyResource(g_context, (ID3D11Resource*)p->backbuffer,
			                                (ID3D11Resource*)p->canonical);
		}

		{
			const uint64_t present_start = ycn_dp_now();
			hr = p->swapchain->lpVtbl->Present(p->swapchain, 0, 0);
			present_ticks = ycn_dp_now() - present_start;
		}
		if (FAILED(hr))
		{
			p->last_hresult = (int32_t)hr;
			ycn_dp_set_text(p->detail, sizeof(p->detail), "present-failed");
			YCN_DP_RELEASE(p->out_view);
			goto finish;
		}

		p->frames_presented++;
		status = YCN_DP_STATUS_OK;
	}

finish:
	if (slot.used && slot.info.frame && g_release_frame)
		g_release_frame(slot.info.frame);
	EnterCriticalSection(&g_dp_lock);
	if (p->leases_outstanding > 0)
		p->leases_outstanding--;
	LeaveCriticalSection(&g_dp_lock);

	if (out_size)
	{
		out->size = out_size;
		out->version = YCN_DP_API_VERSION;
		out->status = (uint32_t)status;
		out->width = slot.info.width;
		out->height = slot.info.height;
		/* 报的是**实际下发给 VideoProcessor 的**配置，不是码流里的原始标注。 */
		out->color_matrix = (p->vp_matrix != YCN_DP_MATRIX_UNKNOWN) ? p->vp_matrix
		                                                            : slot.info.color_matrix;
		out->color_range =
		    (p->vp_range != YCN_DP_RANGE_UNKNOWN) ? p->vp_range : slot.info.color_range;
		out->color_inferred = color_inferred;
		out->converter = p->vp ? 1u : 0u;
		out->dxgi_format = YCN_DP_FORMAT_NV12;
		out->frame_seq = slot.seq;
		out->frames_presented = p->frames_presented;
		out->frames_dropped = p->frames_dropped;
		out->leases_outstanding = p->leases_outstanding;
		out->lease_idle_ticks = ycn_dp_ticks_to_us(idle_ticks);
		out->convert_ticks = ycn_dp_ticks_to_us(convert_ticks);
		/* 增量直显统计：dirty 面积占比 / 增量帧数 / 整帧帧数。 */
		out->reserved[0] = p->last_dirty_pct;
		out->reserved[1] = (uint32_t)(p->frames_incremental & 0xFFFFFFFFu);
		out->reserved[2] = (uint32_t)(p->frames_full & 0xFFFFFFFFu);
		out->present_ticks = ycn_dp_ticks_to_us(present_ticks);
		out->last_hresult = p->last_hresult;
		ycn_dp_set_text(out->detail, sizeof(out->detail), p->detail);
	}
	return status;
}

/* ----------------------------------------------------------- colour test --- */

/* 从目标 RGB 反推 YUV，供自检用：VideoProcessor 必须把它还原回同一个 RGB。 */
static void ycn_dp_rgb_to_yuv(uint32_t matrix, uint32_t range, const double rgb[3],
                              uint8_t yuv[3])
{
	double y, cb, cr;
	if (matrix == YCN_DP_MATRIX_BT709)
	{
		y = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
		cb = -0.114572 * rgb[0] - 0.385428 * rgb[1] + 0.5 * rgb[2] + 128.0;
		cr = 0.5 * rgb[0] - 0.454153 * rgb[1] - 0.045847 * rgb[2] + 128.0;
	}
	else
	{
		y = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2];
		cb = -0.168736 * rgb[0] - 0.331264 * rgb[1] + 0.5 * rgb[2] + 128.0;
		cr = 0.5 * rgb[0] - 0.418688 * rgb[1] - 0.081312 * rgb[2] + 128.0;
	}

	if (range == YCN_DP_RANGE_LIMITED)
	{
		y = 16.0 + (y * 219.0 / 255.0);
		cb = 16.0 + (cb * 224.0 / 255.0);
		cr = 16.0 + (cr * 224.0 / 255.0);
	}

	if (y < 0) y = 0;
	if (y > 255) y = 255;
	if (cb < 0) cb = 0;
	if (cb > 255) cb = 255;
	if (cr < 0) cr = 0;
	if (cr > 255) cr = 255;

	yuv[0] = (uint8_t)(y + 0.5);
	yuv[1] = (uint8_t)(cb + 0.5);
	yuv[2] = (uint8_t)(cr + 0.5);
}

int ycn_dp_color_test(uint32_t matrix, uint32_t range, uint32_t width, uint32_t height,
                      ycn_direct_color_result* out)
{
	static const double patches[4][3] = {
		{ 0.0, 0.0, 0.0 },     /* black */
		{ 255.0, 255.0, 255.0 }, /* white */
		{ 255.0, 0.0, 0.0 },   /* red */
		{ 128.0, 128.0, 128.0 } /* mid grey */
	};
	ID3D11Texture2D* nv12 = NULL;
	ID3D11Texture2D* target = NULL;
	ID3D11Texture2D* staging = NULL;
	D3D11_TEXTURE2D_DESC desc;
	D3D11_VIDEO_PROCESSOR_CONTENT_DESC content;
	D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in_desc;
	D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out_desc;
	D3D11_VIDEO_PROCESSOR_COLOR_SPACE in_space;
	D3D11_VIDEO_PROCESSOR_COLOR_SPACE out_space;
	D3D11_VIDEO_PROCESSOR_STREAM stream;
	ID3D11VideoProcessorEnumerator* vp_enum = NULL;
	ID3D11VideoProcessor* vp = NULL;
	ID3D11VideoProcessorInputView* in_view = NULL;
	ID3D11VideoProcessorOutputView* out_view = NULL;
	uint8_t* planes = NULL;
	uint8_t yuv[4][3];
	size_t y_pitch;
	size_t uv_offset;
	size_t total;
	D3D11_MAPPED_SUBRESOURCE mapped;
	int status = YCN_DP_STATUS_FAILED;
	HRESULT hr = S_OK;
	uint32_t stage = 0;
	uint32_t i;

	const uint32_t out_size = (out && (out->size >= sizeof(*out))) ? out->size : 0;

	if (out_size)
		memset(out, 0, sizeof(*out));
	if (!ycn_dp_device() || !g_video_device || !g_video_context)
	{
		if (out_size)
		{
			out->size = out_size;
			out->version = YCN_DP_API_VERSION;
			out->status = YCN_DP_STATUS_FAILED;
		}
		return YCN_DP_STATUS_FAILED;
	}
	if (!width || !height || (width < 8) || (height < 8))
		return YCN_DP_STATUS_FAILED;
	if ((matrix != YCN_DP_MATRIX_BT601) && (matrix != YCN_DP_MATRIX_BT709))
		return YCN_DP_STATUS_FAILED;
	if ((range != YCN_DP_RANGE_LIMITED) && (range != YCN_DP_RANGE_FULL))
		return YCN_DP_STATUS_FAILED;

	for (i = 0; i < 4; i++)
		ycn_dp_rgb_to_yuv(matrix, range, patches[i], yuv[i]);

	y_pitch = (size_t)width;
	uv_offset = y_pitch * height;
	total = uv_offset + y_pitch * (height / 2);
	planes = (uint8_t*)calloc(1, total);
	if (!planes)
		return YCN_DP_STATUS_FAILED;

	for (i = 0; i < 4; i++)
	{
		const uint32_t row_start = (height / 4) * i;
		const uint32_t row_end = (i == 3) ? height : ((height / 4) * (i + 1));
		uint32_t y;
		for (y = row_start; y < row_end; y++)
			memset(planes + y_pitch * y, yuv[i][0], y_pitch);
		/* 4:2:0 色度：每两行共享一行 UV，两列共享一个样本。 */
		for (y = row_start / 2; y < ((row_end + 1) / 2); y++)
		{
			uint8_t* line = planes + uv_offset + y_pitch * y;
			uint32_t x;
			for (x = 0; x + 1 < width; x += 2)
			{
				line[x] = yuv[i][1];
				line[x + 1] = yuv[i][2];
			}
		}
	}

	memset(&desc, 0, sizeof(desc));
	desc.Width = width;
	desc.Height = height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = (DXGI_FORMAT)YCN_DP_FORMAT_NV12;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	/* NV12 不是 shader-resource 格式；视频处理器输入视图要求资源带
	 * RENDER_TARGET（解码器纹理用 DECODER），故按这两者依次尝试。 */
	desc.BindFlags = D3D11_BIND_RENDER_TARGET;
	stage = 1;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &nv12);
	if (FAILED(hr) || !nv12)
	{
		nv12 = NULL;
		desc.BindFlags = D3D11_BIND_DECODER;
		hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &nv12);
	}
	if (FAILED(hr) || !nv12)
		goto done;

	stage = 2;
	g_context->lpVtbl->UpdateSubresource(g_context, (ID3D11Resource*)nv12, 0, NULL, planes,
	                                    (UINT)y_pitch, (UINT)total);

	memset(&desc, 0, sizeof(desc));
	desc.Width = width;
	desc.Height = height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_RENDER_TARGET;
	stage = 3;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &target);
	if (FAILED(hr) || !target)
		goto done;

	memset(&content, 0, sizeof(content));
	content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
	content.InputWidth = width;
	content.InputHeight = height;
	content.OutputWidth = width;
	content.OutputHeight = height;
	content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
	/* 帧率必须是合法有理数：全零的 0/0 会被驱动判为 E_INVALIDARG。 */
	content.InputFrameRate.Numerator = 60;
	content.InputFrameRate.Denominator = 1;
	content.OutputFrameRate.Numerator = 60;
	content.OutputFrameRate.Denominator = 1;
	stage = 4;
	hr = g_video_device->lpVtbl->CreateVideoProcessorEnumerator(g_video_device, &content,
	                                                           &vp_enum);
	if (FAILED(hr) || !vp_enum)
		goto done;
	stage = 5;
	hr = g_video_device->lpVtbl->CreateVideoProcessor(g_video_device, vp_enum, 0, &vp);
	if (FAILED(hr) || !vp)
		goto done;

	memset(&in_desc, 0, sizeof(in_desc));
	in_desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
	stage = 6;
	hr = g_video_device->lpVtbl->CreateVideoProcessorInputView(g_video_device,
	                                                          (ID3D11Resource*)nv12, vp_enum,
	                                                          &in_desc, &in_view);
	if (FAILED(hr) || !in_view)
		goto done;

	memset(&out_desc, 0, sizeof(out_desc));
	out_desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
	stage = 7;
	hr = g_video_device->lpVtbl->CreateVideoProcessorOutputView(
	    g_video_device, (ID3D11Resource*)target, vp_enum, &out_desc, &out_view);
	if (FAILED(hr) || !out_view)
		goto done;

	memset(&in_space, 0, sizeof(in_space));
	in_space.YCbCr_Matrix = (matrix == YCN_DP_MATRIX_BT709) ? 1 : 0;
	in_space.Nominal_Range = (range == YCN_DP_RANGE_LIMITED) ? 1 : 0;
	g_video_context->lpVtbl->VideoProcessorSetStreamColorSpace(g_video_context, vp, 0, &in_space);

	memset(&out_space, 0, sizeof(out_space));
	out_space.RGB_Range = 0;
	out_space.YCbCr_Matrix = 1;
	out_space.Nominal_Range = 0;
	g_video_context->lpVtbl->VideoProcessorSetOutputColorSpace(g_video_context, vp, &out_space);

	memset(&stream, 0, sizeof(stream));
	stream.Enable = TRUE;
	stream.pInputSurface = in_view;
	stage = 8;
	hr = g_video_context->lpVtbl->VideoProcessorBlt(g_video_context, vp, out_view, 0, 1, &stream);
	if (FAILED(hr))
		goto done;

	memset(&desc, 0, sizeof(desc));
	desc.Width = width;
	desc.Height = height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_STAGING;
	desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
	stage = 9;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &staging);
	if (FAILED(hr) || !staging)
		goto done;

	stage = 10;
	g_context->lpVtbl->CopyResource(g_context, (ID3D11Resource*)staging,
	                               (ID3D11Resource*)target);
	memset(&mapped, 0, sizeof(mapped));
	stage = 11;
	hr = g_context->lpVtbl->Map(g_context, (ID3D11Resource*)staging, 0, D3D11_MAP_READ, 0,
	                           &mapped);
	if (FAILED(hr))
		goto done;

	for (i = 0; i < 4; i++)
	{
		const uint32_t row = ((height / 4) * i) + (height / 8);
		const uint8_t* pixel =
		    (const uint8_t*)mapped.pData + (size_t)(mapped.RowPitch > 0 ? mapped.RowPitch : 0) * row +
		    ((size_t)width / 2) * 4;
		/* BGRA */
		uint32_t err = 0;
		const uint8_t expect[4] = { (uint8_t)patches[i][2], (uint8_t)patches[i][1],
			                        (uint8_t)patches[i][0], 255 };
		uint32_t c;
		for (c = 0; c < 4; c++)
		{
			int diff = (int)pixel[c] - (int)expect[c];
			if (diff < 0)
				diff = -diff;
			if ((uint32_t)diff > err)
				err = (uint32_t)diff;
			if (out_size)
				out->samples[i][c] = pixel[c];
		}
		if (out_size)
			out->error[i] = err;
	}
	g_context->lpVtbl->Unmap(g_context, (ID3D11Resource*)staging, 0);
	status = YCN_DP_STATUS_OK;

done:
	YCN_DP_RELEASE(out_view);
	YCN_DP_RELEASE(in_view);
	YCN_DP_RELEASE(vp);
	YCN_DP_RELEASE(vp_enum);
	YCN_DP_RELEASE(staging);
	YCN_DP_RELEASE(target);
	YCN_DP_RELEASE(nv12);
	free(planes);

	if (out_size)
	{
		out->size = out_size;
		out->version = YCN_DP_API_VERSION;
		out->status = (uint32_t)status;
		out->matrix = matrix;
		out->range = range;
		out->width = width;
		out->height = height;
		out->last_hresult = (int32_t)hr;
		out->stage = (status == YCN_DP_STATUS_OK) ? 0u : stage;
	}
	return status;
}

/* ------------------------------------------------------------ benchmark --- */

static int ycn_dp_cmp_u64(const void* a, const void* b)
{
	const uint64_t x = *(const uint64_t*)a;
	const uint64_t y = *(const uint64_t*)b;
	return (x < y) ? -1 : ((x > y) ? 1 : 0);
}

/* 量一帧真实转色（VideoProcessorBlt）在给定尺寸下要多久。
 *
 * 为什么单独做一个导出而不是复用 ycn_dp_present：present 需要面板、需要真实
 * 解码纹理，而这是**离线**可测的成本项 —— 计划书要的「每帧 CPU 微秒」里，
 * GPU 转色那一段在没有联机会话时也必须能拿到数。
 *
 * 只计 Blt：不含解码、不含 Present、不含拷贝回读（实时路径本身也不回读）。
 * 结果放到 reserved[0..2]（微秒）：最小 / p50 / p99，iterations 放在
 * frames_presented。字段复用见 ycn_direct_present.h 的说明。 */
static int ycn_dp_bench_impl(uint32_t width, uint32_t height, uint32_t rect_w, uint32_t rect_h,
                            uint32_t iterations, ycn_direct_present_result* out)
{
	ID3D11Texture2D* nv12 = NULL;
	ID3D11Texture2D* target = NULL;
	D3D11_TEXTURE2D_DESC desc;
	D3D11_VIDEO_PROCESSOR_CONTENT_DESC content;
	D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in_desc;
	D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out_desc;
	D3D11_VIDEO_PROCESSOR_COLOR_SPACE in_space;
	D3D11_VIDEO_PROCESSOR_COLOR_SPACE out_space;
	D3D11_VIDEO_PROCESSOR_STREAM stream;
	ID3D11VideoProcessorEnumerator* vp_enum = NULL;
	ID3D11VideoProcessor* vp = NULL;
	ID3D11VideoProcessorInputView* in_view = NULL;
	ID3D11VideoProcessorOutputView* out_view = NULL;
	ID3D11Texture2D* conv = NULL;
	D3D11_QUERY_DESC qdesc;
	ID3D11Query* q_event = NULL;
	uint64_t gpu_start = 0;
	uint64_t gpu_per_frame_ns = 0;
	char gpu_note[48];
	uint64_t* samples = NULL;
	uint8_t* planes = NULL;
	size_t y_pitch, uv_offset, total;
	int status = YCN_DP_STATUS_FAILED;
	HRESULT hr = S_OK;
	uint32_t i;

	const uint32_t out_size = (out && (out->size >= sizeof(*out))) ? out->size : 0;
	/* rect 模式：只转 rect_w x rect_h 这一块。 */
	const int rect_mode = (rect_w > 0) && (rect_h > 0) && (rect_w <= width) && (rect_h <= height) &&
	                      ((rect_w < width) || (rect_h < height));
	const uint32_t conv_w = rect_mode ? rect_w : width;
	const uint32_t conv_h = rect_mode ? rect_h : height;

	memset(gpu_note, 0, sizeof(gpu_note));
	if (out_size)
		memset(out, 0, sizeof(*out));
	if (!g_device || !g_video_device || !g_video_context || !g_device)
		return YCN_DP_STATUS_FAILED;
	if ((width < 64) || (height < 64) || (iterations == 0) || (iterations > 10000))
		return YCN_DP_STATUS_FAILED;
	if (rect_mode)
	{
		if (out_size)
		{
			out->bench_rect_w = rect_w;
			out->bench_rect_h = rect_h;
		}
	}

	/* 尺寸对齐到偶数，NV12 的色度平面按 2 采样。 */
	{
		y_pitch = (size_t)width;
		uv_offset = y_pitch * height;
		total = uv_offset + y_pitch * (height / 2);
	}
	planes = (uint8_t*)calloc(1, total);
	samples = (uint64_t*)calloc(iterations, sizeof(uint64_t));
	if (!planes || !samples)
		goto done;
	/* 中灰：色度平面保持 128，亮度 126（limited 中灰附近），内容不影响耗时。 */
	memset(planes, 126, uv_offset);
	memset(planes + uv_offset, 128, total - uv_offset);

	memset(&desc, 0, sizeof(desc));
	desc.Width = width;
	desc.Height = height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = (DXGI_FORMAT)YCN_DP_FORMAT_NV12;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_RENDER_TARGET;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &nv12);
	if (FAILED(hr) || !nv12)
	{
		nv12 = NULL;
		desc.BindFlags = D3D11_BIND_DECODER;
		hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &nv12);
	}
	if (FAILED(hr) || !nv12)
		goto done;
	g_context->lpVtbl->UpdateSubresource(g_context, (ID3D11Resource*)nv12, 0, NULL, planes,
	                                    (UINT)y_pitch, (UINT)total);

	memset(&desc, 0, sizeof(desc));
	desc.Width = width;
	desc.Height = height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_RENDER_TARGET;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &target);
	if (FAILED(hr) || !target)
		goto done;

	/* 与在线路径对齐：整幅帧 = 转到全幅转换面 + CopyResource 到累积面；
	 * 子矩形帧 = 转到矩形大小的贴片 + 把矩形复制回去。
	 * 两个口径都包含「转换 + 落到目标面」这两步，比值才是真实的每帧 GPU 成本比，
	 * 而不是只比转换那一段（那样会低估增量收益，因为增量省下的复制也是真省的）。 */
	{
		desc.Width = conv_w;
		desc.Height = conv_h;
		hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, &conv);
		if (FAILED(hr) || !conv)
			goto done;
	}

	memset(&content, 0, sizeof(content));
	content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
	content.InputWidth = width;
	content.InputHeight = height;
	/* 输出尺寸 = 本次真正要写的面：整幅帧是 width x height，子矩形帧是矩形大小。
	 * 两个口径输出面不同，才谈得上「只转这一块省了多少」。 */
	content.OutputWidth = conv_w;
	content.OutputHeight = conv_h;
	/* 与在线路径一致用 PLAYBACK_NORMAL。OPTIMAL_SPEED 实测对耗时没有影响
	 * （2026-11-02 对照：1080p 全幅 286 µs vs 290 µs，差异在噪声内）。 */
	content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
	content.InputFrameRate.Numerator = 60;
	content.InputFrameRate.Denominator = 1;
	content.OutputFrameRate.Numerator = 60;
	content.OutputFrameRate.Denominator = 1;
	hr = g_video_device->lpVtbl->CreateVideoProcessorEnumerator(g_video_device, &content,
	                                                           &vp_enum);
	if (FAILED(hr) || !vp_enum)
		goto done;
	hr = g_video_device->lpVtbl->CreateVideoProcessor(g_video_device, vp_enum, 0, &vp);
	if (FAILED(hr) || !vp)
		goto done;

	memset(&in_desc, 0, sizeof(in_desc));
	in_desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
	hr = g_video_device->lpVtbl->CreateVideoProcessorInputView(g_video_device,
	                                                          (ID3D11Resource*)nv12, vp_enum,
	                                                          &in_desc, &in_view);
	if (FAILED(hr) || !in_view)
		goto done;

	memset(&out_desc, 0, sizeof(out_desc));
	out_desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
	hr = g_video_device->lpVtbl->CreateVideoProcessorOutputView(
	    g_video_device, (ID3D11Resource*)conv, vp_enum, &out_desc, &out_view);
	if (FAILED(hr) || !out_view)
		goto done;

	memset(&in_space, 0, sizeof(in_space));
	in_space.YCbCr_Matrix = 1; /* BT.709 */
	in_space.Nominal_Range = 1; /* limited */
	g_video_context->lpVtbl->VideoProcessorSetStreamColorSpace(g_video_context, vp, 0, &in_space);
	memset(&out_space, 0, sizeof(out_space));
	g_video_context->lpVtbl->VideoProcessorSetOutputColorSpace(g_video_context, vp, &out_space);
	g_video_context->lpVtbl->VideoProcessorSetStreamFrameFormat(
	    g_video_context, vp, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
	g_video_context->lpVtbl->VideoProcessorSetStreamAutoProcessingMode(g_video_context, vp, 0,
	                                                                  FALSE);

	memset(&stream, 0, sizeof(stream));
	stream.Enable = TRUE;
	stream.pInputSurface = in_view;

	/* 增量场景：只转一个矩形，与在线路径的贴片转换同构。
	 * 源矩形取输入纹理的同一块，目标矩形铺满 conv（conv 就是矩形大小）。 */
	if (rect_mode)
	{
		RECT src_rect = { 0, 0, (LONG)rect_w, (LONG)rect_h };
		RECT dst_rect = { 0, 0, (LONG)conv_w, (LONG)conv_h };
		g_video_context->lpVtbl->VideoProcessorSetStreamSourceRect(g_video_context, vp, 0, TRUE,
		                                                          &src_rect);
		g_video_context->lpVtbl->VideoProcessorSetStreamDestRect(g_video_context, vp, 0, TRUE,
		                                                        &dst_rect);
	}

	/* 一次只量**转换**本身，不把「落到目标面」的复制算进来：
	 * 复制是纯字节搬运，成本随面尺寸线性走，混在一起会把转换的真实缩放关系糊掉。
	 * 在线路径的复制成本单独看 convert_ticks / present_ticks。 */
	#define YCN_DP_BENCH_FRAME() \
		do \
		{ \
			hr = g_video_context->lpVtbl->VideoProcessorBlt(g_video_context, vp, out_view, 0, 1, \
			                                               &stream); \
			if (FAILED(hr)) \
				goto done; \
		} while (0)

	/* 预热两次：第一次 Blt 会包含驱动的管线延迟/着色器编译，不计入统计。 */
	for (i = 0; i < 2; i++)
		YCN_DP_BENCH_FRAME();

	/* 两段时间必须分开量：
	 *   · CPU 侧 = 提交一次 Blt 的同步耗时（UI 线程每帧真正付的代价）；
	 *   · GPU 侧 = 这批 Blt 从第一次提交到 GPU 真正跑完的墙钟时间。
	 *
	 * GPU 侧不用 D3D11_QUERY_TIMESTAMP：本机驱动上两个时间戳都恒为 0，
	 * 差值看起来像「0 帧时间」，这正是上一版得出 gpu_per_frame_us=0 的原因。
	 * EVENT 查询是驱动一等公民：End 一次，等它被 GPU 执行到，就是批次完成点。 */
	memset(&qdesc, 0, sizeof(qdesc));
	qdesc.Query = D3D11_QUERY_EVENT;
	if (FAILED(g_device->lpVtbl->CreateQuery(g_device, &qdesc, &q_event)))
		goto measure_cpu;

	gpu_start = ycn_dp_now();
measure_cpu:
	for (i = 0; i < iterations; i++)
	{
		const uint64_t start = ycn_dp_now();
		YCN_DP_BENCH_FRAME();
		samples[i] = ycn_dp_now() - start;
	}
#undef YCN_DP_BENCH_FRAME

	if (q_event)
	{
		UINT32 spins;
		BOOL done_event = FALSE;
		/* GPU 侧：End 排在所有 Blt 之后，Flush 逼驱动提交，再等它跑到。 */
		g_context->lpVtbl->End(g_context, (ID3D11Asynchronous*)q_event);
		g_context->lpVtbl->Flush(g_context);
		for (spins = 0; spins < 5000; spins++)
		{
			/* GetData 未就绪返回 S_FALSE，不是失败码；必须判 S_OK。 */
			if (g_context->lpVtbl->GetData(g_context, (ID3D11Asynchronous*)q_event, NULL, 0,
			                               0) == S_OK)
			{
				done_event = TRUE;
				break;
			}
			/* 先让出时间片再退到 Sleep(1)：Sleep(1) 的实际粒度是 1~2 ms，
			 * 一整批 1080p Blt 才 230 µs/帧 × 60 ≈ 14 ms，子矩形批次可能只有
			 * 1 ms 量级 —— 直接 Sleep 会让固定的 1 ms 量化误差压过真实差异，
			 * 得出「子矩形不比整幅便宜」的假结论。 */
			if (spins < 4000)
				SwitchToThread();
			else
				Sleep(1);
		}
		if (!done_event)
			ycn_dp_set_text(gpu_note, sizeof(gpu_note), "gpu-timing-timeout");
		else
		{
			/* 保留纳秒：1080p NV12→BGRA 每帧只有几十微秒，先除到微秒会丢精度。
			 * reserved[3] = GPU 每帧纳秒。 */
			const uint64_t wall = ycn_dp_now() - gpu_start;
			const uint64_t per_frame_ns = ycn_dp_ticks_to_ns(wall) / (uint64_t)iterations;
			gpu_per_frame_ns = per_frame_ns;
			if (out_size)
				out->convert_ticks = per_frame_ns / 1000ull;
			ycn_dp_set_text(gpu_note, sizeof(gpu_note), "gpu-timing-event-ok");
		}
	}

	qsort(samples, iterations, sizeof(uint64_t), ycn_dp_cmp_u64);
	if (out_size)
	{
		uint64_t sum = 0;
		for (i = 0; i < iterations; i++)
			sum += samples[i];
		/* CPU 提交耗时用纳秒：一次 Blt 提交常在 1 µs 以内，用 µs 会全是 0。 */
		out->reserved[0] = (uint32_t)ycn_dp_ticks_to_ns(samples[0]);
		out->reserved[1] = (uint32_t)ycn_dp_ticks_to_ns(sum / iterations);
		out->reserved[2] = (uint32_t)ycn_dp_ticks_to_ns(samples[(iterations * 99u) / 100u]);
		out->frames_presented = iterations;
	}
	status = YCN_DP_STATUS_OK;

done:
	YCN_DP_RELEASE(q_event);
	YCN_DP_RELEASE(out_view);
	YCN_DP_RELEASE(in_view);
	YCN_DP_RELEASE(vp);
	YCN_DP_RELEASE(vp_enum);
	YCN_DP_RELEASE(conv);
	YCN_DP_RELEASE(target);
	YCN_DP_RELEASE(nv12);
	free(samples);
	free(planes);

	if (out_size)
	{
		out->size = out_size;
		out->version = YCN_DP_API_VERSION;
		out->status = (uint32_t)status;
		out->width = width;
		out->height = height;
		out->converter = vp ? 1u : 0u;
		out->dxgi_format = YCN_DP_FORMAT_NV12;
		out->last_hresult = (int32_t)hr;
		out->reserved[3] = (uint32_t)gpu_per_frame_ns;
		ycn_dp_set_text(out->detail, sizeof(out->detail),
		                (gpu_note[0] != '\0') ? gpu_note : "bench-vp-blt");
	}
	return status;
}

/* 一张可渲染的 BGRA 面（bench 专用）。 */
static HRESULT ycn_dp_make_bgra(uint32_t w, uint32_t h, ID3D11Texture2D** out)
{
	D3D11_TEXTURE2D_DESC desc;
	HRESULT hr;

	*out = NULL;
	memset(&desc, 0, sizeof(desc));
	desc.Width = w;
	desc.Height = h;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, out);
	if (FAILED(hr) || !*out)
	{
		*out = NULL;
		return FAILED(hr) ? hr : E_FAIL;
	}
	return S_OK;
}

/* 一张中灰的 NV12 输入面（bench 专用；内容不影响耗时）。 */
static HRESULT ycn_dp_make_nv12(uint32_t w, uint32_t h, ID3D11Texture2D** out)
{
	D3D11_TEXTURE2D_DESC desc;
	uint8_t* planes;
	const size_t y_pitch = (size_t)w;
	const size_t uv_offset = y_pitch * h;
	const size_t total = uv_offset + y_pitch * (h / 2);
	HRESULT hr;

	*out = NULL;
	memset(&desc, 0, sizeof(desc));
	desc.Width = w;
	desc.Height = h;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = (DXGI_FORMAT)YCN_DP_FORMAT_NV12;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_RENDER_TARGET;
	hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, out);
	if (FAILED(hr) || !*out)
	{
		*out = NULL;
		desc.BindFlags = D3D11_BIND_DECODER;
		hr = g_device->lpVtbl->CreateTexture2D(g_device, &desc, NULL, out);
		if (FAILED(hr) || !*out)
		{
			*out = NULL;
			return FAILED(hr) ? hr : E_FAIL;
		}
	}
	planes = (uint8_t*)calloc(1, total);
	if (!planes)
		return E_OUTOFMEMORY;
	memset(planes, 126, uv_offset);
	memset(planes + uv_offset, 128, total - uv_offset);
	g_context->lpVtbl->UpdateSubresource(g_context, (ID3D11Resource*)*out, 0, NULL, planes,
	                                    (UINT)y_pitch, (UINT)total);
	free(planes);
	return S_OK;
}

/* 在线每帧的真实 GPU 工作量，这是「增量到底省不省」唯一问得清的量法：
 *
 *   整幅帧 = Blt 整幅 -> canonical，再 CopyResource(canonical -> 后备缓冲)
 *   增量帧 = Blt 矩形 -> 贴片，CopySubresourceRegion(贴片 -> canonical)，
 *            再 CopyResource(canonical -> 后备缓冲)
 *
 * 只量转换会高估收益（省下的转换只有几十微秒），只量复制会低估收益
 * （复制成本不随面积缩放）。两者相加才是这一帧在 GPU 上真正花的钱。
 *
 * reserved[3] = 每帧纳秒；bench_rect_w/h = 实际矩形；convert_ticks = 微秒。 */
int ycn_dp_bench_frame(uint32_t width, uint32_t height, uint32_t rect_w, uint32_t rect_h,
                       uint32_t iterations, ycn_direct_present_result* out)
{
	ID3D11Texture2D* nv12 = NULL;
	ID3D11Texture2D* canonical = NULL;
	ID3D11Texture2D* target = NULL;
	ID3D11Texture2D* scratch = NULL;
	D3D11_VIDEO_PROCESSOR_CONTENT_DESC content;
	D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in_desc;
	D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out_desc;
	D3D11_VIDEO_PROCESSOR_COLOR_SPACE in_space;
	D3D11_VIDEO_PROCESSOR_COLOR_SPACE out_space;
	D3D11_VIDEO_PROCESSOR_STREAM stream;
	ID3D11VideoProcessorEnumerator* vp_enum = NULL;
	ID3D11VideoProcessor* vp = NULL;
	ID3D11VideoProcessorInputView* in_view = NULL;
	ID3D11VideoProcessorOutputView* out_view = NULL;
	D3D11_QUERY_DESC qdesc;
	ID3D11Query* q_event = NULL;
	uint64_t gpu_start = 0;
	uint64_t gpu_per_frame_ns = 0;
	char gpu_note[48];
	uint64_t* samples = NULL;
	int status = YCN_DP_STATUS_FAILED;
	HRESULT hr = S_OK;
	uint32_t i;

	const uint32_t out_size = (out && (out->size >= sizeof(*out))) ? out->size : 0;
	const int rect_mode = (rect_w > 0) && (rect_h > 0) && (rect_w <= width) && (rect_h <= height) &&
	                      ((rect_w < width) || (rect_h < height));
	const uint32_t conv_w = rect_mode ? rect_w : width;
	const uint32_t conv_h = rect_mode ? rect_h : height;

	memset(gpu_note, 0, sizeof(gpu_note));
	if (out_size)
		memset(out, 0, sizeof(*out));
	if (!g_device || !g_video_device || !g_video_context)
		return YCN_DP_STATUS_FAILED;
	if ((width < 64) || (height < 64) || (iterations == 0) || (iterations > 10000))
		return YCN_DP_STATUS_FAILED;
	if (out_size && rect_mode)
	{
		out->bench_rect_w = rect_w;
		out->bench_rect_h = rect_h;
	}

	samples = (uint64_t*)calloc(iterations, sizeof(uint64_t));
	if (!samples)
		goto done;

	hr = ycn_dp_make_nv12(width, height, &nv12);
	if (FAILED(hr))
		goto done;
	hr = ycn_dp_make_bgra(width, height, &canonical);
	if (FAILED(hr))
		goto done;
	hr = ycn_dp_make_bgra(width, height, &target);
	if (FAILED(hr))
		goto done;
	if (rect_mode)
	{
		hr = ycn_dp_make_bgra(conv_w, conv_h, &scratch);
		if (FAILED(hr))
			goto done;
	}

	memset(&content, 0, sizeof(content));
	content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
	content.InputWidth = width;
	content.InputHeight = height;
	content.OutputWidth = conv_w;
	content.OutputHeight = conv_h;
	content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
	content.InputFrameRate.Numerator = 60;
	content.InputFrameRate.Denominator = 1;
	content.OutputFrameRate.Numerator = 60;
	content.OutputFrameRate.Denominator = 1;
	hr = g_video_device->lpVtbl->CreateVideoProcessorEnumerator(g_video_device, &content,
	                                                           &vp_enum);
	if (FAILED(hr) || !vp_enum)
		goto done;
	hr = g_video_device->lpVtbl->CreateVideoProcessor(g_video_device, vp_enum, 0, &vp);
	if (FAILED(hr) || !vp)
		goto done;

	memset(&in_desc, 0, sizeof(in_desc));
	in_desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
	hr = g_video_device->lpVtbl->CreateVideoProcessorInputView(g_video_device,
	                                                          (ID3D11Resource*)nv12, vp_enum,
	                                                          &in_desc, &in_view);
	if (FAILED(hr) || !in_view)
		goto done;

	memset(&out_desc, 0, sizeof(out_desc));
	out_desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
	hr = g_video_device->lpVtbl->CreateVideoProcessorOutputView(
	    g_video_device, (ID3D11Resource*)(rect_mode ? scratch : canonical), vp_enum, &out_desc,
	    &out_view);
	if (FAILED(hr) || !out_view)
		goto done;

	memset(&in_space, 0, sizeof(in_space));
	in_space.YCbCr_Matrix = 1;  /* BT.709 */
	in_space.Nominal_Range = 1; /* limited */
	g_video_context->lpVtbl->VideoProcessorSetStreamColorSpace(g_video_context, vp, 0, &in_space);
	memset(&out_space, 0, sizeof(out_space));
	g_video_context->lpVtbl->VideoProcessorSetOutputColorSpace(g_video_context, vp, &out_space);
	g_video_context->lpVtbl->VideoProcessorSetStreamFrameFormat(
	    g_video_context, vp, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
	g_video_context->lpVtbl->VideoProcessorSetStreamAutoProcessingMode(g_video_context, vp, 0,
	                                                                  FALSE);

	memset(&stream, 0, sizeof(stream));
	stream.Enable = TRUE;
	stream.pInputSurface = in_view;
	if (rect_mode)
	{
		RECT src_rect = { 0, 0, (LONG)rect_w, (LONG)rect_h };
		RECT dst_rect = { 0, 0, (LONG)conv_w, (LONG)conv_h };
		g_video_context->lpVtbl->VideoProcessorSetStreamSourceRect(g_video_context, vp, 0, TRUE,
		                                                          &src_rect);
		g_video_context->lpVtbl->VideoProcessorSetStreamDestRect(g_video_context, vp, 0, TRUE,
		                                                        &dst_rect);
	}

	/* 一帧的完整 GPU 工作（见函数头注释）。 */
#define YCN_DP_FRAME_WORK()                                                                        \
	do                                                                                             \
	{                                                                                              \
		hr = g_video_context->lpVtbl->VideoProcessorBlt(g_video_context, vp, out_view, 0, 1,        \
		                                               &stream);                                   \
		if (FAILED(hr))                                                                            \
			goto done;                                                                             \
		if (rect_mode)                                                                             \
		{                                                                                          \
			const D3D11_BOX box = { 0, 0, 0, conv_w, conv_h, 1 };                                   \
			g_context->lpVtbl->CopySubresourceRegion(g_context, (ID3D11Resource*)canonical, 0, 0,   \
			                                        0, 0, (ID3D11Resource*)scratch, 0, &box);      \
		}                                                                                          \
		g_context->lpVtbl->CopyResource(g_context, (ID3D11Resource*)target,                        \
		                                (ID3D11Resource*)canonical);                                \
	} while (0)

	for (i = 0; i < 2; i++)
		YCN_DP_FRAME_WORK();

	memset(&qdesc, 0, sizeof(qdesc));
	qdesc.Query = D3D11_QUERY_EVENT;
	if (FAILED(g_device->lpVtbl->CreateQuery(g_device, &qdesc, &q_event)))
		goto measure_cpu;

	gpu_start = ycn_dp_now();
measure_cpu:
	for (i = 0; i < iterations; i++)
	{
		const uint64_t start = ycn_dp_now();
		YCN_DP_FRAME_WORK();
		samples[i] = ycn_dp_now() - start;
	}
#undef YCN_DP_FRAME_WORK

	if (q_event)
	{
		UINT32 spins;
		BOOL done_event = FALSE;
		g_context->lpVtbl->End(g_context, (ID3D11Asynchronous*)q_event);
		g_context->lpVtbl->Flush(g_context);
		for (spins = 0; spins < 5000; spins++)
		{
			if (g_context->lpVtbl->GetData(g_context, (ID3D11Asynchronous*)q_event, NULL, 0,
			                               0) == S_OK)
			{
				done_event = TRUE;
				break;
			}
			if (spins < 4000)
				SwitchToThread();
			else
				Sleep(1);
		}
		if (!done_event)
			ycn_dp_set_text(gpu_note, sizeof(gpu_note), "gpu-timing-timeout");
		else
		{
			const uint64_t wall = ycn_dp_now() - gpu_start;
			gpu_per_frame_ns = ycn_dp_ticks_to_ns(wall) / (uint64_t)iterations;
			if (out_size)
				out->convert_ticks = gpu_per_frame_ns / 1000ull;
			ycn_dp_set_text(gpu_note, sizeof(gpu_note), "gpu-timing-event-ok");
		}
	}

	qsort(samples, iterations, sizeof(uint64_t), ycn_dp_cmp_u64);
	if (out_size)
	{
		uint64_t sum = 0;
		for (i = 0; i < iterations; i++)
			sum += samples[i];
		out->reserved[0] = (uint32_t)ycn_dp_ticks_to_ns(samples[0]);
		out->reserved[1] = (uint32_t)ycn_dp_ticks_to_ns(sum / iterations);
		out->reserved[2] = (uint32_t)ycn_dp_ticks_to_ns(samples[(iterations * 99u) / 100u]);
		out->frames_presented = iterations;
	}
	status = YCN_DP_STATUS_OK;

done:
	YCN_DP_RELEASE(q_event);
	YCN_DP_RELEASE(out_view);
	YCN_DP_RELEASE(in_view);
	YCN_DP_RELEASE(vp);
	YCN_DP_RELEASE(vp_enum);
	YCN_DP_RELEASE(scratch);
	YCN_DP_RELEASE(target);
	YCN_DP_RELEASE(canonical);
	YCN_DP_RELEASE(nv12);
	free(samples);

	if (out_size)
	{
		out->size = out_size;
		out->version = YCN_DP_API_VERSION;
		out->status = (uint32_t)status;
		out->width = width;
		out->height = height;
		out->converter = vp ? 1u : 0u;
		out->dxgi_format = YCN_DP_FORMAT_NV12;
		out->last_hresult = (int32_t)hr;
		out->reserved[3] = (uint32_t)gpu_per_frame_ns;
		ycn_dp_set_text(out->detail, sizeof(out->detail),
		                (gpu_note[0] != '\0') ? gpu_note : "bench-frame");
	}
	return status;
}

int ycn_dp_bench(uint32_t width, uint32_t height, uint32_t iterations,
                 ycn_direct_present_result* out)
{
	return ycn_dp_bench_impl(width, height, 0, 0, iterations, out);
}

/* 只转 rect_w x rect_h 的子矩形：用来量化 dirty 增量的收益。
 * reserved[1..2] 回报实际使用的矩形尺寸。 */
int ycn_dp_bench_rect(uint32_t width, uint32_t height, uint32_t rect_w, uint32_t rect_h,
                      uint32_t iterations, ycn_direct_present_result* out)
{
	return ycn_dp_bench_impl(width, height, rect_w, rect_h, iterations, out);
}

void ycn_dp_shutdown(void)
{
	size_t i;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	for (i = 0; i < YCN_DP_MAX_PRESENTERS; i++)
	{
		YcnPresenter* p = &g_presenters[i];
		if (!p->used)
			continue;
		ycn_dp_drop_slot(&p->pending);
		ycn_dp_drop_slot(&p->ready);
		ycn_dp_reset_processor(p);
		ycn_dp_release_chain_views(p);
		YCN_DP_RELEASE(p->swapchain);
		if (p->panel)
			p->panel->lpVtbl->Release(p->panel);
		memset(p, 0, sizeof(*p));
	}

	YCN_DP_RELEASE(g_video_context);
	YCN_DP_RELEASE(g_video_device);
	YCN_DP_RELEASE(g_context);
	YCN_DP_RELEASE(g_device);
	g_device_status = 0;
}

/* 摘掉本会话的交换链并把面板上的交换链清空，供回退到托管渲染器时使用。
 *
 * 为什么必须显式清一次：托管渲染器随后会用自己的交换链 SetSwapChain，但那发生
 * 在「下一拍」；在此之前面板仍挂着我们的链，而我们已经把链释放掉了。WinUI 对
 * 已被释放的链只会在合成时静默失败，表现为画面停在最后一帧 —— 排查起来极像
 * 「直显卡死」。这里先把面板解绑，语义干净。 */
void ycn_dp_detach(int session)
{
	YcnPresenter* p;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (p)
		p->enabled = 0;
	LeaveCriticalSection(&g_dp_lock);

	if (!p)
		return;

	ycn_dp_drop_slot(&p->pending);
	ycn_dp_drop_slot(&p->ready);
	ycn_dp_reset_processor(p);
	ycn_dp_release_chain_views(p);

	if (p->panel)
	{
		YcnSwapChainPanelNative* panel = NULL;
		if (SUCCEEDED(p->panel->lpVtbl->QueryInterface(p->panel, &YCN_IID_ISwapChainPanelNative,
		                                              (void**)&panel)) &&
		    panel)
		{
			panel->lpVtbl->SetSwapChain(panel, NULL);
			panel->lpVtbl->Release(panel);
		}
		p->panel->lpVtbl->Release(p->panel);
		p->panel = NULL;
	}

	YCN_DP_RELEASE(p->swapchain);
	p->chain_width = 0;
	p->chain_height = 0;

	/* 槽位还回去：session id 单调递增、不复用，留着只会把 8 个槽位耗光。
	 * 下一次 attach/enable 会按需重建。 */
	EnterCriticalSection(&g_dp_lock);
	memset(p, 0, sizeof(*p));
	LeaveCriticalSection(&g_dp_lock);
}

/* 会话结束：释放该 session 的 presenter 槽位，但**不碰面板**。
 *
 * 为什么分开：这里在 FreeRDP 事件线程上跑，SetSwapChain 只能由 UI 线程调。
 * 槽位必须在这里还回去 —— session id 单调递增不复用，不还就等于每次重连
 * 泄漏一个槽（上限 8 个，连 8 次就再也挂不上直显）。面板上的交换链是
 * SetSwapChain 时 AddRef 过的，我们释放自己那份不会让它悬空，最坏只是画面
 * 停在最后一帧，随后由 UI 线程的 detach/Dispose 收拾。 */
void ycn_dp_release_session(int session)
{
	YcnPresenter* p;

	InitOnceExecuteOnce(&g_dp_lock_once, ycn_dp_lock_init, NULL, NULL);
	EnterCriticalSection(&g_dp_lock);
	p = ycn_dp_find(session);
	if (p)
	{
		/* 只关闸 + 丢掉未呈现的帧；**不拆 D3D 资源**。
		 * present 在锁外做 Blt，此刻 UI 线程可能正拿这条链在用；在别的线程上
		 * Release 交换链/视图就是一次必然的 use-after-free。资源留给
		 * ycn_dp_detach（UI 线程）或表满时的孤儿回收处理。 */
		p->enabled = 0;
		p->orphaned = 1;
		ycn_dp_drop_slot(&p->pending);
		ycn_dp_drop_slot(&p->ready);
		if (p->panel)
		{
			p->panel->lpVtbl->Release(p->panel);
			p->panel = NULL;
		}
	}
	LeaveCriticalSection(&g_dp_lock);
}
