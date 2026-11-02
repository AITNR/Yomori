/* 直显的「版面」纯计算层：缩放（Viewbox / 拉伸）、DPI、以及 dirty 矩形的合并与钳位。
 *
 * 这一层不碰 D3D、不碰 WinUI，只做整数/浮点几何，因此在没有窗口、没有服务器的
 * 前提下可以被完整单测。presenter 只负责把这里算出来的矩形交给 VideoProcessorBlt。
 *
 * 约定：
 *   · 源坐标系 = 解码纹理像素（左上原点）；
 *   · 目标坐标系 = 交换链后备缓冲像素（左上原点）；
 *   · 所有输出矩形都保证落在目标范围内，宽高 ≥ 0（退化时返回空矩形）。
 */
#ifndef YCN_PRESENT_LAYOUT_H
#define YCN_PRESENT_LAYOUT_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct
{
	int32_t x;
	int32_t y;
	int32_t w;
	int32_t h;
} YcnLayoutRect;

/* 缩放模式 */
typedef enum
{
	/* 拉伸铺满（会变形）：旧行为，仅在源与目标宽高比一致时使用。 */
	YCN_FIT_STRETCH = 0,
	/* 等比缩放并留黑边（Viewbox）：默认。 */
	YCN_FIT_LETTERBOX = 1,
	/* 等比缩放并裁切（UniformToFill）。 */
	YCN_FIT_CROP = 2
} YcnFitMode;

/* dirty 矩形列表容量。超出即退化为整帧，绝不部分更新。 */
#define YCN_LAYOUT_MAX_DIRTY 16

typedef struct
{
	YcnLayoutRect rects[YCN_LAYOUT_MAX_DIRTY];
	uint32_t count;
	/* 1 = 本帧必须整帧重画（没有 dirty 信息 / 尺寸变了 / 列表溢出） */
	uint32_t full;
} YcnDirtySet;

/* 计算目标矩形。`epsilon` 是允许的亚像素余量（单位：目标像素），用于吸收
 * DPI 非整数缩放带来的 0.5px 抖动：只有当偏差超过 epsilon 才重新拟合。
 * `dst_w/dst_h` 为 0 或负数时返回空矩形。 */
YcnLayoutRect ycn_layout_fit(int32_t src_w, int32_t src_h, int32_t dst_w, int32_t dst_h,
                             YcnFitMode mode, double epsilon);

/* DPI：WinUI 的 CompositionScale 与交换链矩阵。返回写进
 * DXGI_MATRIX_3X2_F 的 (m11, m12, m21, m22, dx, dy)：交换链按 1/scale 反缩放，
 * 这样 96 DPI 的像素密度映射到物理像素。scale <= 0 时按 1.0 处理。 */
void ycn_layout_dpi_matrix(double composition_scale, float out_6[6]);

/* 两个矩形求并；任一为空则返回另一个。 */
YcnLayoutRect ycn_layout_union(YcnLayoutRect a, YcnLayoutRect b);

/* 求交；不相交返回空矩形（w=0,h=0）。 */
YcnLayoutRect ycn_layout_intersect(YcnLayoutRect a, YcnLayoutRect b);

int ycn_layout_is_empty(YcnLayoutRect r);

/* 把矩形钳到 [0,0,bound_w,bound_h]；越界部分裁掉。 */
YcnLayoutRect ycn_layout_clip(YcnLayoutRect r, int32_t bound_w, int32_t bound_h);

void ycn_dirty_reset(YcnDirtySet* set);

/* 加入一个矩形：先钳位，再与已有矩形合并（完全包含则忽略，相交则取并）。
 * 返回 1 = 已加入/已合并；0 = 因溢出退化为整帧（set->full = 1）。 */
int ycn_dirty_add(YcnDirtySet* set, YcnLayoutRect r, int32_t bound_w, int32_t bound_h);

/* 面积占比（0..100）。用于判断增量是否真的划算。 */
uint32_t ycn_dirty_area_percent(const YcnDirtySet* set, int32_t bound_w, int32_t bound_h);

/* 增量是否值得做：面积占比超过 `threshold_percent` 或整帧标记为真时返回 0。 */
int ycn_dirty_is_incremental_worthwhile(const YcnDirtySet* set, int32_t bound_w, int32_t bound_h,
                                        uint32_t threshold_percent);

#ifdef __cplusplus
}
#endif

#endif /* YCN_PRESENT_LAYOUT_H */
