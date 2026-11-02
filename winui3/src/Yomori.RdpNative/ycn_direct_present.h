/* Yomori direct present: the presentation half of the AVC direct path.
 *
 * Everything here must run on the same ID3D11Device the decoder decodes onto,
 * otherwise the decoded texture cannot be sampled without a round trip through
 * shared handles (which the S0 probe proved unavailable on this driver stack).
 * The device is therefore created here and injected into FreeRDP before the
 * decoder context exists.
 *
 * Threading contract:
 *   - ycn_dp_publish() runs on the FreeRDP event loop thread. It only stores a
 *     pointer; it never touches D3D.
 *   - ycn_dp_present() runs on the UI thread (the SwapChainPanel owner) and does
 *     all GPU work: video processor blit plus Present.
 *   - ycn_dp_enable()/attach_panel() must be called from the UI thread before
 *     the first frame, because the panel hand-off touches XAML.
 */
#ifndef YOMORI_DIRECT_PRESENT_H
#define YOMORI_DIRECT_PRESENT_H

#include <stdint.h>

#define YCN_DP_API_VERSION 1u

/* Colour metadata, mirrored from the FreeRDP bridge. */
#define YCN_DP_MATRIX_UNKNOWN 0u
#define YCN_DP_MATRIX_BT601 1u
#define YCN_DP_MATRIX_BT709 2u
#define YCN_DP_MATRIX_BT2020 3u

#define YCN_DP_RANGE_UNKNOWN 0u
#define YCN_DP_RANGE_LIMITED 1u
#define YCN_DP_RANGE_FULL 2u

/* Presentation status. */
#define YCN_DP_STATUS_OK 0
#define YCN_DP_STATUS_NO_FRAME 1
#define YCN_DP_STATUS_NOT_READY 2
#define YCN_DP_STATUS_NEED_PANEL 3
#define YCN_DP_STATUS_DISABLED 4
#define YCN_DP_STATUS_FAILED 5

/* One decoded GPU frame. Layout mirrors the FreeRDP side exactly; the fields
 * are fixed width and the struct is only passed by pointer. */
typedef struct ycn_dp_frame
{
	uint32_t size;
	uint32_t version;
	uint32_t flags;
	uint32_t width;
	uint32_t height;
	uint32_t hw_type;
	uint32_t pix_format;
	uint32_t sw_format;
	uint32_t subresource;
	uint32_t color_matrix;
	uint32_t color_range;
	uint32_t color_primaries;
	uint32_t color_transfer;
	uint32_t reserved0;
	uint64_t frame_index;
	int64_t pts;
	void* frame;   /* AVFrame lease; released through freerdp */
	void* texture; /* ID3D11Texture2D* valid while the lease is held */
	void* device;  /* borrowed */
	uint32_t reserved[6];
} ycn_dp_frame;

typedef struct ycn_direct_present_result
{
	uint32_t size;
	uint32_t version;
	uint32_t status; /* YCN_DP_STATUS_* */
	uint32_t width;
	uint32_t height;
	uint32_t color_matrix;
	uint32_t color_range;
	uint32_t converter; /* 1 = video processor */
	uint32_t dxgi_format;
	/* 1 = 码流没有 VUI 色彩标注，矩阵/范围是按分辨率推断的
	 * （≥720 行 BT.709，否则 BT.601；范围缺省 limited）。0 = 来自码流。 */
	uint32_t color_inferred;
	uint64_t frame_seq;
	uint64_t frames_presented;
	uint64_t frames_dropped;
	uint64_t leases_outstanding;
	uint64_t lease_idle_ticks;
	uint64_t convert_ticks;
	uint64_t present_ticks;
	int32_t last_hresult;
	uint32_t reserved1;
	char detail[96];
	/* ycn_dp_bench_rect 实际转过的子矩形（0 = 整幅）。
	 * 单独占字段而不是塞 reserved[]：reserved[0..3] 已被 CPU/GPU 计时占满，
	 * 早期版本把矩形写进 reserved[1..2] 后被统计量原地覆盖，测出来的
	 * 「矩形」永远是 1920x1080 —— 判据因此假红。 */
	uint32_t bench_rect_w;
	uint32_t bench_rect_h;
	uint32_t reserved[4];
} ycn_direct_present_result;

typedef struct ycn_direct_color_result
{
	uint32_t size;
	uint32_t version;
	uint32_t status; /* YCN_DP_STATUS_* */
	uint32_t matrix;
	uint32_t range;
	uint32_t width;
	uint32_t height;
	int32_t last_hresult;
	/* Rendered BGRA samples for the four patches, in patch order:
	 * black, white, red, mid grey. */
	uint8_t samples[4][4];
	/* Absolute error of each patch against the expected value, 0..255. */
	uint32_t error[4];
	/* Failing step when status != OK: 1 nv12, 2 update, 3 target, 4 enum,
	 * 5 processor, 6 input-view, 7 output-view, 8 blt, 9 staging, 10 copy,
	 * 11 map. */
	uint32_t stage;
	uint32_t reserved[3];
} ycn_direct_color_result;

/* Sets the freerdp-side release callback for frame leases. */
void ycn_dp_set_release(void (*release)(void* frame));

/* Creates the shared device on first use and returns the ID3D11Device* that the
 * decoder must decode onto, or NULL when unavailable. */
void* ycn_dp_device(void);

/* Enables direct present for a session. Returns 0 on success. */
int ycn_dp_enable(int session);
void ycn_dp_disable(int session);
int ycn_dp_is_enabled(int session);

/* UI thread only: hands the SwapChainPanel (IUnknown*) to the presenter. The
 * swap chain itself is created lazily on the first present. */
int ycn_dp_attach_panel(int session, void* panel);

/* 版面：fit 模式（0=拉伸 1=Viewbox 2=裁切）、epsilon（目标像素）。
 * 参数变化会让下一帧整面重画。 */
int ycn_dp_set_layout(int session, uint32_t fit_mode, double epsilon);

/* UI 线程：CompositionScale -> 交换链矩阵（1/scale）。0 或负数按 1.0 处理。 */
int ycn_dp_set_scale(int session, double composition_scale);

/* 增量直显开关（默认关）。开启后只有 dirty 面积 ≤ 60% 且累积面有效时才逐矩形转换，
 * 其余情况一律整幅 —— 失败模式是「多转」，不是「少画」。 */
int ycn_dp_set_incremental(int session, uint32_t enable);

/* 本帧的 dirty 矩形（目标坐标系，xywh 交替，最多 16 个）。
 * count=0 表示「本帧整面」。present 之后列表自动清空。 */
int ycn_dp_set_frame_rects(int session, const int32_t* xywh, uint32_t count);

/* FreeRDP thread: offers a decoded frame. Takes ownership on success. */
int ycn_dp_publish(int session, void* freerdp_frame);

/* FreeRDP thread: complete-frame boundary (RDPGFX EndFrame). */
void ycn_dp_end_frame(int session);

/* UI thread: convert and present the latched frame. */
int ycn_dp_present(int session, ycn_direct_present_result* out);

/* UI thread: renders a known NV12 pattern through the exact same conversion the
 * live path uses and reads the RGB result back. Used by the acceptance tests. */
int ycn_dp_color_test(uint32_t matrix, uint32_t range, uint32_t width, uint32_t height,
                      ycn_direct_color_result* out);

/* Offline cost measurement: runs `iterations` real VideoProcessorBlt calls at the
 * given size on a synthetic NV12 texture and reports the distribution in
 * `reserved[0..2]` (nanoseconds: min / mean / p99 of the CPU submission) plus
 * `reserved[3]` (nanoseconds of GPU time per frame, from timestamp queries) and
 * `convert_ticks` (same GPU number in microseconds); `frames_presented` carries
 * the iteration count. No panel, no decoder, no readback — this is exactly the
 * GPU conversion segment of a live frame and nothing else. */
/* 只转 rect_w x rect_h 的子矩形：量化 dirty 增量的收益。
 * 实际转过的矩形回填在 bench_rect_w / bench_rect_h。 */
int ycn_dp_bench_rect(uint32_t width, uint32_t height, uint32_t rect_w, uint32_t rect_h,
                      uint32_t iterations, ycn_direct_present_result* out);

/* 在线每帧的**完整** GPU 工作（转换 + 落到累积面 + 落到后备缓冲），
 * rect_w/rect_h 为 0 时按整幅帧量。这是判断「增量到底省不省」的唯一口径：
 * 只量转换会高估收益，只量复制会低估收益。 */
int ycn_dp_bench_frame(uint32_t width, uint32_t height, uint32_t rect_w, uint32_t rect_h,
                       uint32_t iterations, ycn_direct_present_result* out);

int ycn_dp_bench(uint32_t width, uint32_t height, uint32_t iterations,
                 ycn_direct_present_result* out);

/* UI thread: drops the swap chain and unbinds it from the panel, so the managed
 * renderer can take the panel over cleanly. */
void ycn_dp_detach(int session);

/* Any thread, session teardown: releases the presenter slot without touching the
 * panel (SetSwapChain is UI-thread only). Session ids are never reused, so the
 * slot must be given back or every reconnect leaks one. */
void ycn_dp_release_session(int session);

void ycn_dp_shutdown(void);

#endif /* YOMORI_DIRECT_PRESENT_H */
