/* ycn_avc444_gate 的纯单元测试：不链接 FreeRDP、不需要服务器，也不碰 D3D。
 *
 * 覆盖计划书 S2 的验收面：
 *   · GPU/CPU 两条路径共用的 op0 / op1 / op2 语义；
 *   · 丢流（只有亮度 / 只有色度）；
 *   · 错序（旧代号晚到、重复结算）；
 *   · 尺寸不匹配；
 *   · 租约（双流各一份，成败路径都必须归零）；
 *   · reset 之后的残帧；
 *   · 多帧连续序列。
 */
#include "ycn_avc444_gate.h"

#include <stdio.h>
#include <string.h>

static int g_failures = 0;
static int g_checks = 0;

/* 用一个整数当租约句柄，回调里记录释放次数，从而证明「丢弃路径也归还租约」。 */
static int g_released[8];
static int g_release_calls = 0;

static void release_probe(void* lease)
{
	const intptr_t id = (intptr_t)lease;
	if ((id >= 1) && (id < 8))
		g_released[id]++;
	g_release_calls++;
}

static void expect(int cond, const char* what)
{
	g_checks++;
	if (!cond)
	{
		g_failures++;
		printf("  [FAIL] %s\n", what);
	}
}

static void expect_u64(uint64_t actual, uint64_t wanted, const char* what)
{
	g_checks++;
	if (actual != wanted)
	{
		g_failures++;
		printf("  [FAIL] %s: got %llu want %llu\n", what, (unsigned long long)actual,
		       (unsigned long long)wanted);
	}
}

static void reset_probe(void)
{
	g_release_calls = 0;
	memset(g_released, 0, sizeof(g_released));
}

/* op0：一条命令同时带亮度与色度 → 立即整帧提交。 */
static void test_op0_commit(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	expect(ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_AND_CHROMA, 0, 1920, 1080, (void*)1,
	                        (void*)2) > 0,
	       "op0 begin returns a generation");
	ycn_avc444_part(&gate, &st, 0, 1, 1920, 1080, NULL);
	ycn_avc444_part(&gate, &st, 1, 1, 1920, 1080, NULL);
	expect(ycn_avc444_commit(&gate, &st) == 1, "op0 with both streams commits");
	expect_u64(st.frames_committed, 1, "one committed frame");
	expect_u64(st.frames_dropped, 0, "no dropped frames");
	expect_u64(st.leases_outstanding, 0, "no outstanding lease after commit");
	expect_u64((uint64_t)g_released[1], 1, "luma lease released");
	expect_u64((uint64_t)g_released[2], 1, "chroma lease released");
	expect_u64(st.last_width, 1920, "committed width recorded");
	expect_u64(st.last_height, 1080, "committed height recorded");
}

/* op1 单独到达：只算半帧，绝不能上屏。 */
static void test_op1_alone_never_presents(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_ONLY, 0, 1920, 1080, (void*)1, NULL);
	ycn_avc444_part(&gate, &st, 0, 1, 1920, 1080, NULL);
	expect(ycn_avc444_commit(&gate, &st) == 0, "luma-only frame is not presented");
	expect_u64(st.frames_committed, 0, "nothing committed");
	expect_u64(st.frames_dropped, 1, "half frame dropped");
	expect_u64(st.last_drop, YCN_AVC444_DROP_ORPHAN_LUMA, "reason is orphan-luma");
	expect_u64(st.need_full_refresh, 1, "drop requests a full refresh");
	expect_u64(st.leases_outstanding, 0, "lease released on drop");
	expect_u64((uint64_t)g_released[1], 1, "luma lease released on drop");
}

/* op1 → op2 两段式：第二段接续第一段，合成整帧后提交。 */
static void test_op1_then_op2_commits(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	const uint32_t gen =
	    ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_ONLY, 0, 1280, 720, (void*)1, NULL);
	expect(gen > 0, "op1 opens a generation");
	ycn_avc444_part(&gate, &st, 0, 1, 1280, 720, NULL);
	const uint32_t gen2 =
	    ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_CHROMA_ONLY, 0, 1280, 720, NULL, (void*)2);
	expect_u64(gen2, gen, "op2 continues the same generation");
	ycn_avc444_part(&gate, &st, 1, 1, 1280, 720, NULL);
	expect(ycn_avc444_commit(&gate, &st) == 1, "luma+chroma pair commits");
	expect_u64(st.frames_committed, 1, "one committed frame");
	expect_u64(st.frames_pending, 1, "one pending phase recorded");
	expect_u64(st.leases_outstanding, 0, "both leases released");
	expect_u64((uint64_t)g_released[1], 1, "luma lease released");
	expect_u64((uint64_t)g_released[2], 1, "chroma lease released");
}

/* op2 孤立到达（没有等待中的亮度）：不能提交，也不能把色度贴到上一帧上。 */
static void test_orphan_chroma(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_CHROMA_ONLY, 0, 1920, 1080, NULL, (void*)2);
	ycn_avc444_part(&gate, &st, 1, 1, 1920, 1080, NULL);
	expect(ycn_avc444_commit(&gate, &st) == 0, "orphan chroma is not presented");
	expect_u64(st.last_drop, YCN_AVC444_DROP_ORPHAN_CHROMA, "reason is orphan-chroma");
	expect_u64(st.leases_outstanding, 0, "lease released");
}

/* 色度流解码失败：整帧丢弃，不能「只显示主流」。 */
static void test_chroma_failure(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_AND_CHROMA, 0, 1920, 1080, (void*)1,
	                 (void*)2);
	ycn_avc444_part(&gate, &st, 0, 1, 1920, 1080, NULL);
	ycn_avc444_part(&gate, &st, 1, 0, 1920, 1080, NULL);
	expect(ycn_avc444_commit(&gate, &st) == 0, "chroma failure drops the whole frame");
	expect_u64(st.last_drop, YCN_AVC444_DROP_CHROMA_FAILED, "reason is chroma-failed");
	expect_u64(st.frames_committed, 0, "nothing presented");
	expect_u64((uint64_t)g_released[1] + (uint64_t)g_released[2], 2, "both leases released");
}

/* 尺寸不匹配：色度流尺寸与亮度流不一致 → 整帧丢弃。 */
static void test_size_mismatch(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_AND_CHROMA, 0, 1920, 1080, (void*)1,
	                 (void*)2);
	ycn_avc444_part(&gate, &st, 0, 1, 1920, 1080, NULL);
	ycn_avc444_part(&gate, &st, 1, 1, 960, 540, NULL);
	expect(ycn_avc444_commit(&gate, &st) == 0, "mismatched chroma size is dropped");
	expect_u64(st.last_drop, YCN_AVC444_DROP_SIZE_MISMATCH, "reason is size-mismatch");
	expect_u64(st.leases_outstanding, 0, "leases released");
}

/* 错序：旧代号晚到必须被拒；重复结算也必须被拒。 */
static void test_out_of_order(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_AND_CHROMA, 7, 640, 480, (void*)1, (void*)2);
	ycn_avc444_part(&gate, &st, 0, 1, 640, 480, NULL);
	ycn_avc444_part(&gate, &st, 1, 1, 640, 480, NULL);
	expect(ycn_avc444_commit(&gate, &st) == 1, "generation 7 commits");

	/* 代号 6 的命令晚到 */
	expect(ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_AND_CHROMA, 6, 640, 480, (void*)3,
	                        (void*)4) == 0,
	       "older generation is rejected");
	expect_u64(st.last_drop, YCN_AVC444_DROP_STALE_GENERATION, "reason is stale-generation");
	expect_u64((uint64_t)g_released[3] + (uint64_t)g_released[4], 2, "rejected leases released");

	/* 重复结算 */
	expect(ycn_avc444_commit(&gate, &st) == 0, "double commit is rejected");
	expect_u64(st.leases_outstanding, 0, "no lease leak");
}

/* 前一帧还没结算就来了新帧：旧的半帧丢弃，新的正常开始。 */
static void test_partial_then_new_frame(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_ONLY, 0, 800, 600, (void*)1, NULL);
	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_AND_CHROMA, 0, 800, 600, (void*)2, (void*)3);
	expect_u64(st.frames_dropped, 1, "abandoned half frame is dropped");
	expect_u64(st.drops_by_reason[YCN_AVC444_DROP_ORPHAN_LUMA], 1, "dropped as orphan-luma");
	ycn_avc444_part(&gate, &st, 0, 1, 800, 600, NULL);
	ycn_avc444_part(&gate, &st, 1, 1, 800, 600, NULL);
	expect(ycn_avc444_commit(&gate, &st) == 1, "new frame still commits");
	expect_u64(st.leases_outstanding, 0, "no lease leak across the drop");
}

/* reset：在途帧丢弃，之后的残帧不能冒充新帧。 */
static void test_reset(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_ONLY, 0, 1024, 768, (void*)1, NULL);
	ycn_avc444_reset(&gate, &st);
	expect_u64(st.frames_dropped, 1, "reset drops the in-flight frame");
	expect_u64(st.last_drop, YCN_AVC444_DROP_AFTER_RESET, "reason is after-reset");
	expect_u64(st.leases_outstanding, 0, "lease released by reset");
	expect(ycn_avc444_commit(&gate, &st) == 0, "commit after reset is a no-op");
	expect(gate.in_progress == 0, "gate is idle after reset");
}

/* 超时：亮度一直等不到色度，必须能被显式结算掉，而不是永远挂着。 */
static void test_expire(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_ONLY, 0, 640, 480, (void*)1, NULL);
	expect(ycn_avc444_expire(&gate, &st) == 1, "expire drops the pending frame");
	expect_u64(st.last_drop, YCN_AVC444_DROP_ORPHAN_LUMA, "reason is orphan-luma");
	expect(ycn_avc444_expire(&gate, &st) == 0, "expire is idempotent when idle");
	expect_u64(st.leases_outstanding, 0, "lease released");
}

/* 多帧序列：op0 / op1+op2 / op0 连续 30 帧，统计必须对得上。 */
static void test_multi_frame_sequence(void)
{
	ycn_avc444_gate gate;
	ycn_avc444_stats st;
	ycn_avc444_gate_init(&gate, release_probe);
	memset(&st, 0, sizeof(st));
	reset_probe();

	for (int i = 0; i < 10; i++)
	{
		ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_AND_CHROMA, 0, 1920, 1080, (void*)1,
		                 (void*)2);
		ycn_avc444_part(&gate, &st, 0, 1, 1920, 1080, NULL);
		ycn_avc444_part(&gate, &st, 1, 1, 1920, 1080, NULL);
		expect(ycn_avc444_commit(&gate, &st) == 1, "op0 frame commits");

		ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_LUMA_ONLY, 0, 1920, 1080, (void*)1, NULL);
		ycn_avc444_part(&gate, &st, 0, 1, 1920, 1080, NULL);
		ycn_avc444_begin(&gate, &st, YCN_AVC444_OP_CHROMA_ONLY, 0, 1920, 1080, NULL, (void*)2);
		ycn_avc444_part(&gate, &st, 1, 1, 1920, 1080, NULL);
		expect(ycn_avc444_commit(&gate, &st) == 1, "op1+op2 frame commits");
	}

	expect_u64(st.frames_committed, 20, "twenty committed frames");
	expect_u64(st.frames_dropped, 0, "no drops");
	expect_u64(st.leases_outstanding, 0, "no lease leak");
	expect_u64((uint64_t)g_release_calls, st.leases_acquired, "every lease was released");
}

int main(void)
{
	printf("ycn_avc444_gate tests\n");
	test_op0_commit();
	test_op1_alone_never_presents();
	test_op1_then_op2_commits();
	test_orphan_chroma();
	test_chroma_failure();
	test_size_mismatch();
	test_out_of_order();
	test_partial_then_new_frame();
	test_reset();
	test_expire();
	test_multi_frame_sequence();

	printf("%d checks, %d failures\n", g_checks, g_failures);
	return (g_failures == 0) ? 0 : 1;
}
