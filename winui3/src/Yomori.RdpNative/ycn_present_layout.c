#include "ycn_present_layout.h"

#include <string.h>

static YcnLayoutRect ycn_layout_empty(void)
{
	YcnLayoutRect r = { 0, 0, 0, 0 };
	return r;
}

int ycn_layout_is_empty(YcnLayoutRect r)
{
	return (r.w <= 0) || (r.h <= 0);
}

YcnLayoutRect ycn_layout_clip(YcnLayoutRect r, int32_t bound_w, int32_t bound_h)
{
	YcnLayoutRect out = r;
	if ((bound_w <= 0) || (bound_h <= 0))
		return ycn_layout_empty();
	if (ycn_layout_is_empty(out))
		return ycn_layout_empty();
	int64_t x0 = out.x;
	int64_t y0 = out.y;
	int64_t x1 = (int64_t)out.x + out.w;
	int64_t y1 = (int64_t)out.y + out.h;
	if (x0 < 0)
		x0 = 0;
	if (y0 < 0)
		y0 = 0;
	if (x1 > bound_w)
		x1 = bound_w;
	if (y1 > bound_h)
		y1 = bound_h;
	if ((x1 <= x0) || (y1 <= y0))
		return ycn_layout_empty();
	out.x = (int32_t)x0;
	out.y = (int32_t)y0;
	out.w = (int32_t)(x1 - x0);
	out.h = (int32_t)(y1 - y0);
	return out;
}

YcnLayoutRect ycn_layout_union(YcnLayoutRect a, YcnLayoutRect b)
{
	if (ycn_layout_is_empty(a))
		return b;
	if (ycn_layout_is_empty(b))
		return a;
	int64_t x0 = (a.x < b.x) ? a.x : b.x;
	int64_t y0 = (a.y < b.y) ? a.y : b.y;
	int64_t ax1 = (int64_t)a.x + a.w;
	int64_t ay1 = (int64_t)a.y + a.h;
	int64_t bx1 = (int64_t)b.x + b.w;
	int64_t by1 = (int64_t)b.y + b.h;
	int64_t x1 = (ax1 > bx1) ? ax1 : bx1;
	int64_t y1 = (ay1 > by1) ? ay1 : by1;
	YcnLayoutRect out;
	out.x = (int32_t)x0;
	out.y = (int32_t)y0;
	out.w = (int32_t)(x1 - x0);
	out.h = (int32_t)(y1 - y0);
	return out;
}

YcnLayoutRect ycn_layout_intersect(YcnLayoutRect a, YcnLayoutRect b)
{
	if (ycn_layout_is_empty(a) || ycn_layout_is_empty(b))
		return ycn_layout_empty();
	int64_t x0 = (a.x > b.x) ? a.x : b.x;
	int64_t y0 = (a.y > b.y) ? a.y : b.y;
	int64_t ax1 = (int64_t)a.x + a.w;
	int64_t ay1 = (int64_t)a.y + a.h;
	int64_t bx1 = (int64_t)b.x + b.w;
	int64_t by1 = (int64_t)b.y + b.h;
	int64_t x1 = (ax1 < bx1) ? ax1 : bx1;
	int64_t y1 = (ay1 < by1) ? ay1 : by1;
	YcnLayoutRect out;
	if ((x1 <= x0) || (y1 <= y0))
		return ycn_layout_empty();
	out.x = (int32_t)x0;
	out.y = (int32_t)y0;
	out.w = (int32_t)(x1 - x0);
	out.h = (int32_t)(y1 - y0);
	return out;
}

YcnLayoutRect ycn_layout_fit(int32_t src_w, int32_t src_h, int32_t dst_w, int32_t dst_h,
                             YcnFitMode mode, double epsilon)
{
	YcnLayoutRect r = { 0, 0, 0, 0 };

	if ((src_w <= 0) || (src_h <= 0) || (dst_w <= 0) || (dst_h <= 0))
		return ycn_layout_empty();

	if (mode == YCN_FIT_STRETCH)
	{
		r.x = 0;
		r.y = 0;
		r.w = dst_w;
		r.h = dst_h;
		return r;
	}

	const double sx = (double)dst_w / (double)src_w;
	const double sy = (double)dst_h / (double)src_h;
	double scale = (mode == YCN_FIT_LETTERBOX) ? ((sx < sy) ? sx : sy) : ((sx > sy) ? sx : sy);
	if (scale <= 0.0)
		return ycn_layout_empty();

	int32_t w = (int32_t)((double)src_w * scale + 0.5);
	int32_t h = (int32_t)((double)src_h * scale + 0.5);

	/* epsilon：吸收非整数 DPI 缩放下的 ±1px 抖动。偏差在 epsilon 以内时
	 * 直接铺满，避免每帧在 1px 之间来回跳（画面会闪一条黑边）。 */
	if ((w <= dst_w) && (h <= dst_h) && (dst_w - w) <= epsilon && (dst_h - h) <= epsilon)
	{
		w = dst_w;
		h = dst_h;
	}

	if (mode == YCN_FIT_LETTERBOX)
	{
		r.w = w;
		r.h = h;
		r.x = (dst_w - w) / 2;
		r.y = (dst_h - h) / 2;
	}
	else /* CROP：取覆盖目标的那个缩放，居中裁切 */
	{
		r.w = w;
		r.h = h;
		r.x = (dst_w - w) / 2;
		r.y = (dst_h - h) / 2;
	}
	return ycn_layout_clip(r, dst_w, dst_h);
}

void ycn_layout_dpi_matrix(double composition_scale, float out_6[6])
{
	if (!out_6)
		return;
	if (!(composition_scale > 0.0))
		composition_scale = 1.0;
	const float inv = (float)(1.0 / composition_scale);
	/* m11, m12, m21, m22, dx, dy */
	out_6[0] = inv;
	out_6[1] = 0.0f;
	out_6[2] = 0.0f;
	out_6[3] = inv;
	out_6[4] = 0.0f;
	out_6[5] = 0.0f;
}

void ycn_dirty_reset(YcnDirtySet* set)
{
	if (!set)
		return;
	memset(set, 0, sizeof(*set));
}

/* 反复合并相交的矩形，直到没有可合并的为止。矩形数量上限很小（16），
 * O(n^3) 的最坏情况在这里没有实际代价，换来的是「不漏块」的简单证明。 */
static void ycn_dirty_coalesce(YcnDirtySet* set)
{
	int merged = 1;
	while (merged)
	{
		merged = 0;
		for (uint32_t i = 0; i < set->count; i++)
		{
			for (uint32_t j = i + 1; j < set->count; j++)
			{
				if (ycn_layout_is_empty(ycn_layout_intersect(set->rects[i], set->rects[j])))
					continue;
				set->rects[i] = ycn_layout_union(set->rects[i], set->rects[j]);
				set->rects[j] = set->rects[set->count - 1];
				set->count--;
				merged = 1;
				break;
			}
			if (merged)
				break;
		}
	}
}

int ycn_dirty_add(YcnDirtySet* set, YcnLayoutRect r, int32_t bound_w, int32_t bound_h)
{
	if (!set)
		return 0;
	if (set->full)
		return 0;

	const YcnLayoutRect clipped = ycn_layout_clip(r, bound_w, bound_h);
	if (ycn_layout_is_empty(clipped))
		return 1; /* 完全在界外：不需要重画，也不算溢出 */

	for (uint32_t i = 0; i < set->count; i++)
	{
		if (!ycn_layout_is_empty(ycn_layout_intersect(set->rects[i], clipped)))
		{
			set->rects[i] = ycn_layout_union(set->rects[i], clipped);
			ycn_dirty_coalesce(set);
			return 1;
		}
	}

	if (set->count >= YCN_LAYOUT_MAX_DIRTY)
	{
		/* 列表溢出：宁可整帧重画，也不做「漏一块」的部分更新。 */
		set->full = 1;
		set->count = 0;
		return 0;
	}
	set->rects[set->count++] = clipped;
	return 1;
}

uint32_t ycn_dirty_area_percent(const YcnDirtySet* set, int32_t bound_w, int32_t bound_h)
{
	if (!set || (bound_w <= 0) || (bound_h <= 0))
		return 0;
	if (set->full)
		return 100;
	uint64_t area = 0;
	for (uint32_t i = 0; i < set->count; i++)
	{
		uint64_t w = (uint64_t)((set->rects[i].w > 0) ? set->rects[i].w : 0);
		uint64_t h = (uint64_t)((set->rects[i].h > 0) ? set->rects[i].h : 0);
		area += w * h;
	}
	const uint64_t total = (uint64_t)bound_w * (uint64_t)bound_h;
	if (area == 0)
		return 0;
	if (area >= total)
		return 100;
	uint64_t pct = (area * 100u) / total;
	return (uint32_t)pct;
}

int ycn_dirty_is_incremental_worthwhile(const YcnDirtySet* set, int32_t bound_w, int32_t bound_h,
                                        uint32_t threshold_percent)
{
	if (!set || set->full || (set->count == 0))
		return 0;
	const uint32_t pct = ycn_dirty_area_percent(set, bound_w, bound_h);
	return (pct > 0) && (pct <= threshold_percent);
}
