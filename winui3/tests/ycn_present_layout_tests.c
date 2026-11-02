/* ycn_present_layout 的纯单元测试：缩放（Viewbox/拉伸/裁切）、DPI 矩阵、
 * dirty 矩形的合并/钳位/溢出退化。不链接 D3D，也不需要窗口。
 *
 * 对应计划书 S3 验收里「缩放、DPI/Viewbox、overlap、dirty 丢失 full」几项。
 */
#include "ycn_present_layout.h"

#include <stdio.h>

static int g_failures = 0;
static int g_checks = 0;

static void expect(int cond, const char* what)
{
	g_checks++;
	if (!cond)
	{
		g_failures++;
		printf("  [FAIL] %s\n", what);
	}
}

static void expect_rect(YcnLayoutRect r, int x, int y, int w, int h, const char* what)
{
	g_checks++;
	if ((r.x != x) || (r.y != y) || (r.w != w) || (r.h != h))
	{
		g_failures++;
		printf("  [FAIL] %s: got (%d,%d,%d,%d) want (%d,%d,%d,%d)\n", what, r.x, r.y, r.w, r.h, x,
		       y, w, h);
	}
}

static void test_letterbox(void)
{
	/* 1920x1080 源放进 1920x1200 面板：上下各留 60 行黑边。 */
	expect_rect(ycn_layout_fit(1920, 1080, 1920, 1200, YCN_FIT_LETTERBOX, 0.0), 0, 60, 1920, 1080,
	            "1080p into 1920x1200 letterboxes top/bottom");

	/* 方形面板：上下留边，横向铺满（1080 * 9/16 = 607.5 -> 608）。 */
	expect_rect(ycn_layout_fit(1920, 1080, 1080, 1080, YCN_FIT_LETTERBOX, 0.0), 0, 236, 1080, 608,
	            "16:9 into square panel is centred vertically");

	/* 完全等比：铺满，不留边。 */
	expect_rect(ycn_layout_fit(1280, 720, 2560, 1440, YCN_FIT_LETTERBOX, 0.0), 0, 0, 2560, 1440,
	            "exact aspect fills the panel");

	/* 拉伸模式仍可用（用于已知等比的快速路径）。 */
	expect_rect(ycn_layout_fit(1920, 1080, 800, 800, YCN_FIT_STRETCH, 0.0), 0, 0, 800, 800,
	            "stretch ignores aspect");

	/* 裁切模式：铺满并裁掉超出部分。 */
	YcnLayoutRect crop = ycn_layout_fit(1920, 1080, 1000, 1000, YCN_FIT_CROP, 0.0);
	expect_rect(crop, 0, 0, 1000, 1000, "crop fills the panel");
	expect(ycn_layout_fit(1920, 1080, 0, 720, YCN_FIT_LETTERBOX, 0.0).w == 0,
	       "zero-size target yields an empty rect");
}

static void test_letterbox_epsilon(void)
{
	/* 1001x563 面板、1920x1080 源：等比算出 1001x563.06 -> 整数化会有 1px 差，
	 * epsilon=1.0 时应当直接铺满，避免每帧黑边闪一下。 */
	const YcnLayoutRect tight = ycn_layout_fit(1920, 1080, 1001, 563, YCN_FIT_LETTERBOX, 0.0);
	const YcnLayoutRect snuggled = ycn_layout_fit(1920, 1080, 1001, 563, YCN_FIT_LETTERBOX, 2.0);
	expect(snuggled.w == 1001 && snuggled.h == 563, "epsilon absorbs sub-pixel mismatch");
	expect(tight.w <= 1001 && tight.h <= 563, "without epsilon the rect never exceeds the target");
}

static void test_dpi_matrix(void)
{
	float m[6];
	ycn_layout_dpi_matrix(1.5, m);
	expect(m[0] > 0.66f && m[0] < 0.67f, "1.5x DPI scales the swap chain by 1/1.5");
	expect(m[1] == 0.0f && m[2] == 0.0f && m[4] == 0.0f && m[5] == 0.0f, "no shear/translation");
	ycn_layout_dpi_matrix(0.0, m);
	expect(m[0] == 1.0f, "invalid scale falls back to 1.0");
	ycn_layout_dpi_matrix(2.0, m);
	expect(m[3] == 0.5f, "2x DPI halves the y scale");
}

static void test_dirty_merge(void)
{
	YcnDirtySet set;
	ycn_dirty_reset(&set);
	expect(ycn_dirty_add(&set, (YcnLayoutRect){ 10, 10, 20, 20 }, 1920, 1080) == 1, "add rect");
	expect(ycn_dirty_add(&set, (YcnLayoutRect){ 25, 25, 20, 20 }, 1920, 1080) == 1,
	       "overlapping rect merges");
	expect(set.count == 1, "overlap collapses to one rect");
	expect_rect(set.rects[0], 10, 10, 35, 35, "union covers both rects");

	/* 不相交：保留两个矩形（不合并成一个大 bbox，否则增量没意义）。 */
	expect(ycn_dirty_add(&set, (YcnLayoutRect){ 500, 500, 10, 10 }, 1920, 1080) == 1,
	       "disjoint rect added");
	expect(set.count == 2, "disjoint rects stay separate");
}

static void test_dirty_clip_and_bounds(void)
{
	YcnDirtySet set;
	ycn_dirty_reset(&set);

	/* 越界矩形被裁到面板内。 */
	ycn_dirty_add(&set, (YcnLayoutRect){ 1900, 1000, 100, 100 }, 1920, 1080);
	expect_rect(set.rects[0], 1900, 1000, 20, 80, "rect clipped to bounds");

	/* 完全在界外：不产生矩形，也不算溢出。 */
	ycn_dirty_reset(&set);
	expect(ycn_dirty_add(&set, (YcnLayoutRect){ 5000, 5000, 10, 10 }, 1920, 1080) == 1,
	       "out-of-bounds rect is accepted but produces nothing");
	expect(set.count == 0, "no rect for out-of-bounds input");
	expect(set.full == 0, "out-of-bounds is not treated as overflow");
}

static void test_dirty_overflow_falls_back_to_full(void)
{
	YcnDirtySet set;
	ycn_dirty_reset(&set);
	for (int i = 0; i < YCN_LAYOUT_MAX_DIRTY; i++)
	{
		/* 互不相交的小矩形，每个间隔 40px。 */
		ycn_dirty_add(&set, (YcnLayoutRect){ i * 40, 0, 10, 10 }, 1920, 1080);
	}
	expect(set.count == YCN_LAYOUT_MAX_DIRTY, "list filled to capacity");
	expect(set.full == 0, "not yet overflowed");

	/* 再来一个：必须退化整帧，而不是把新矩形丢掉。 */
	const int rc = ycn_dirty_add(&set, (YcnLayoutRect){ 1000, 1000, 10, 10 }, 1920, 1080);
	expect(rc == 0, "overflow reports failure");
	expect(set.full == 1, "overflow falls back to a full frame");
	expect(set.count == 0, "rect list is cleared on overflow");
}

static void test_dirty_area_and_threshold(void)
{
	YcnDirtySet set;
	ycn_dirty_reset(&set);
	ycn_dirty_add(&set, (YcnLayoutRect){ 0, 0, 192, 108 }, 1920, 1080);
	expect(ycn_dirty_area_percent(&set, 1920, 1080) == 1, "1% of the frame");
	expect(ycn_dirty_is_incremental_worthwhile(&set, 1920, 1080, 50) == 1,
	       "small dirty area is worth doing incrementally");

	ycn_dirty_reset(&set);
	ycn_dirty_add(&set, (YcnLayoutRect){ 0, 0, 1920, 1080 }, 1920, 1080);
	expect(ycn_dirty_area_percent(&set, 1920, 1080) == 100, "full area");
	expect(ycn_dirty_is_incremental_worthwhile(&set, 1920, 1080, 50) == 0,
	       "a full-area dirty set is not worth the incremental path");

	ycn_dirty_reset(&set);
	expect(ycn_dirty_is_incremental_worthwhile(&set, 1920, 1080, 50) == 0,
	       "empty set never claims to be incremental");

	set.full = 1;
	expect(ycn_dirty_is_incremental_worthwhile(&set, 1920, 1080, 50) == 0,
	       "full flag wins over the area heuristic");
}

static void test_dirty_overlap_chain(void)
{
	/* 三个矩形依次搭桥：A∩B、B∩C，但 A 与 C 不相交。
	 * 合并后必须收敛成一个能覆盖全部三块的矩形，且不会漏掉中间的缝隙。 */
	YcnDirtySet set;
	ycn_dirty_reset(&set);
	ycn_dirty_add(&set, (YcnLayoutRect){ 0, 0, 100, 100 }, 1000, 1000);
	ycn_dirty_add(&set, (YcnLayoutRect){ 80, 80, 100, 100 }, 1000, 1000);
	ycn_dirty_add(&set, (YcnLayoutRect){ 160, 160, 100, 100 }, 1000, 1000);
	expect(set.count == 1, "chained overlaps collapse into one rect");
	expect_rect(set.rects[0], 0, 0, 260, 260, "collapsed rect covers the whole chain");
}

int main(void)
{
	printf("ycn_present_layout tests\n");
	test_letterbox();
	test_letterbox_epsilon();
	test_dpi_matrix();
	test_dirty_merge();
	test_dirty_clip_and_bounds();
	test_dirty_overflow_falls_back_to_full();
	test_dirty_area_and_threshold();
	test_dirty_overlap_chain();

	printf("%d checks, %d failures\n", g_checks, g_failures);
	return (g_failures == 0) ? 0 : 1;
}
