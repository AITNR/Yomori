#include "ycn_avc444_gate.h"

#include <string.h>

static void ycn_avc444_release(ycn_avc444_gate* gate, ycn_avc444_stats* stats, void** lease)
{
	if (!*lease)
		return;
	if (gate->release_lease)
		gate->release_lease(*lease);
	*lease = NULL;
	if (stats)
	{
		stats->leases_released++;
		if (stats->leases_outstanding > 0)
			stats->leases_outstanding--;
	}
}

static void ycn_avc444_drop(ycn_avc444_gate* gate, ycn_avc444_stats* stats, uint32_t reason)
{
	if (!gate->in_progress)
		return;
	ycn_avc444_release(gate, stats, &gate->luma_lease);
	ycn_avc444_release(gate, stats, &gate->chroma_lease);
	gate->in_progress = 0;
	gate->luma_ok = 0;
	gate->chroma_ok = 0;
	gate->chroma_width = 0;
	gate->chroma_height = 0;
	gate->active_generation = 0;
	if (stats)
	{
		stats->frames_dropped++;
		if (reason < YCN_AVC444_DROP_REASON_COUNT)
			stats->drops_by_reason[reason]++;
		stats->last_drop = reason;
		/* 半帧已经进了管线（或者尺寸变了），必须整屏重画，不能指望
		 * 增量区域能自愈。 */
		stats->need_full_refresh = 1;
	}
}

void ycn_avc444_gate_init(ycn_avc444_gate* gate, void (*release_lease)(void* lease))
{
	if (!gate)
		return;
	memset(gate, 0, sizeof(*gate));
	gate->release_lease = release_lease;
}

uint32_t ycn_avc444_begin(ycn_avc444_gate* gate, ycn_avc444_stats* stats, uint32_t op,
                          uint32_t generation, uint32_t width, uint32_t height, void* luma_lease,
                          void* chroma_lease)
{
	if (!gate)
		return 0;
	if (stats)
	{
		stats->frames_offered++;
		stats->last_op = op;
		stats->need_full_refresh = 0;
	}

	if (op > YCN_AVC444_OP_CHROMA_ONLY)
	{
		if (gate->release_lease)
		{
			if (luma_lease)
				gate->release_lease(luma_lease);
			if (chroma_lease)
				gate->release_lease(chroma_lease);
		}
		if (stats)
		{
			stats->drops_by_reason[YCN_AVC444_DROP_BAD_OP]++;
			stats->last_drop = YCN_AVC444_DROP_BAD_OP;
		}
		return 0;
	}

	/* op=2 是上一帧亮度的续传：只有确实存在等待中的亮度帧时才接续。
	 * 其余情况（op0/op1，或 op2 但没有等待帧）都开启新的一帧。 */
	if (op == YCN_AVC444_OP_CHROMA_ONLY && gate->in_progress &&
	    (gate->op == YCN_AVC444_OP_LUMA_ONLY))
	{
		if (chroma_lease)
		{
			ycn_avc444_release(gate, stats, &gate->chroma_lease);
			gate->chroma_lease = chroma_lease;
			if (stats)
			{
				stats->leases_acquired++;
				stats->leases_outstanding++;
			}
		}
		return gate->active_generation;
	}

	/* 上一帧还没结算就来了新帧：整帧丢弃，绝不让旧的半帧跟着新的走。 */
	if (gate->in_progress)
	{
		const uint32_t reason = (gate->op == YCN_AVC444_OP_LUMA_AND_CHROMA)
		                            ? YCN_AVC444_DROP_PARTIAL_OP0
		                            : YCN_AVC444_DROP_ORPHAN_LUMA;
		ycn_avc444_drop(gate, stats, reason);
	}

	/* 代号过期：乱序到达的旧帧。 */
	if ((generation != 0) && (generation <= gate->committed_generation))
	{
		if (gate->release_lease)
		{
			if (luma_lease)
				gate->release_lease(luma_lease);
			if (chroma_lease)
				gate->release_lease(chroma_lease);
		}
		if (stats)
		{
			stats->drops_by_reason[YCN_AVC444_DROP_STALE_GENERATION]++;
			stats->last_drop = YCN_AVC444_DROP_STALE_GENERATION;
			stats->need_full_refresh = 1;
		}
		return 0;
	}

	gate->in_progress = 1;
	gate->op = op;
	gate->width = width;
	gate->height = height;
	gate->chroma_width = 0;
	gate->chroma_height = 0;
	gate->active_generation = (generation != 0) ? generation : (++gate->next_generation);
	if (gate->active_generation > gate->next_generation)
		gate->next_generation = gate->active_generation;

	switch (op)
	{
		case YCN_AVC444_OP_LUMA_AND_CHROMA:
			gate->luma_ok = 1;
			gate->chroma_ok = 1;
			break;
		case YCN_AVC444_OP_LUMA_ONLY:
			gate->luma_ok = 1;
			gate->chroma_ok = 0;
			if (stats)
				stats->frames_pending++;
			break;
		default: /* op=2 没有等待中的亮度 */
			gate->luma_ok = 0;
			gate->chroma_ok = 1;
			break;
	}

	if (luma_lease)
	{
		gate->luma_lease = luma_lease;
		if (stats)
		{
			stats->leases_acquired++;
			stats->leases_outstanding++;
		}
	}
	if (chroma_lease)
	{
		gate->chroma_lease = chroma_lease;
		if (stats)
		{
			stats->leases_acquired++;
			stats->leases_outstanding++;
		}
	}
	return gate->active_generation;
}

void ycn_avc444_part(ycn_avc444_gate* gate, ycn_avc444_stats* stats, int is_chroma, int ok,
                     uint32_t width, uint32_t height, void* lease)
{
	if (!gate || !gate->in_progress)
	{
		if (lease && gate && gate->release_lease)
			gate->release_lease(lease);
		return;
	}

	if (lease)
	{
		if (is_chroma)
		{
			ycn_avc444_release(gate, stats, &gate->chroma_lease);
			gate->chroma_lease = lease;
		}
		else
		{
			ycn_avc444_release(gate, stats, &gate->luma_lease);
			gate->luma_lease = lease;
		}
		if (stats)
		{
			stats->leases_acquired++;
			stats->leases_outstanding++;
		}
	}

	if (is_chroma)
	{
		gate->chroma_ok = ok ? 1 : 0;
		gate->chroma_width = width;
		gate->chroma_height = height;
	}
	else
	{
		gate->luma_ok = ok ? 1 : 0;
		gate->width = width;
		gate->height = height;
	}
}

int ycn_avc444_commit(ycn_avc444_gate* gate, ycn_avc444_stats* stats)
{
	if (!gate)
		return 0;

	if (!gate->in_progress)
	{
		/* 重复结算：没有在途帧。这在协议上不会发生，但一旦发生就该
		 * 明确记成「过期帧」而不是静默返回成功。 */
		if (stats)
		{
			stats->drops_by_reason[YCN_AVC444_DROP_STALE_GENERATION]++;
			stats->last_drop = YCN_AVC444_DROP_STALE_GENERATION;
			stats->need_full_refresh = 1;
		}
		return 0;
	}

	uint32_t reason = YCN_AVC444_DROP_NONE;
	if (!gate->luma_ok && !gate->chroma_ok)
		reason = (gate->op == YCN_AVC444_OP_CHROMA_ONLY) ? YCN_AVC444_DROP_ORPHAN_CHROMA
		                                                 : YCN_AVC444_DROP_AFTER_RESET;
	else if (!gate->luma_ok)
		reason = (gate->op == YCN_AVC444_OP_CHROMA_ONLY) ? YCN_AVC444_DROP_ORPHAN_CHROMA
		                                                 : YCN_AVC444_DROP_LUMA_FAILED;
	else if (!gate->chroma_ok)
		reason = (gate->op == YCN_AVC444_OP_LUMA_AND_CHROMA) ? YCN_AVC444_DROP_CHROMA_FAILED
		                                                     : YCN_AVC444_DROP_ORPHAN_LUMA;
	else if ((gate->chroma_width != 0) &&
	         ((gate->chroma_width != gate->width) || (gate->chroma_height != gate->height)))
		reason = YCN_AVC444_DROP_SIZE_MISMATCH;
	else if ((gate->op == YCN_AVC444_OP_LUMA_AND_CHROMA) && (gate->chroma_width == 0))
		reason = YCN_AVC444_DROP_PARTIAL_OP0;

	if (reason != YCN_AVC444_DROP_NONE)
	{
		ycn_avc444_drop(gate, stats, reason);
		return 0;
	}

	/* 整帧成立：结算并把租约交还给调用方（调用方自己在取用时另持引用）。 */
	ycn_avc444_release(gate, stats, &gate->luma_lease);
	ycn_avc444_release(gate, stats, &gate->chroma_lease);
	gate->committed_generation = gate->active_generation;
	gate->in_progress = 0;
	gate->luma_ok = 0;
	gate->chroma_ok = 0;
	gate->chroma_width = 0;
	gate->chroma_height = 0;
	if (stats)
	{
		stats->frames_committed++;
		stats->last_drop = YCN_AVC444_DROP_NONE;
		stats->last_width = gate->width;
		stats->last_height = gate->height;
		stats->generation = gate->committed_generation;
	}
	gate->active_generation = 0;
	return 1;
}

void ycn_avc444_reset(ycn_avc444_gate* gate, ycn_avc444_stats* stats)
{
	if (!gate)
		return;
	if (gate->in_progress)
		ycn_avc444_drop(gate, stats, YCN_AVC444_DROP_AFTER_RESET);
	/* 代号随 epoch 前移，保证 reset 之前的旧命令不会再被当成新帧。 */
	gate->reset_epoch++;
	gate->next_generation = gate->reset_epoch * 1000000u;
	gate->committed_generation = gate->next_generation;
	gate->active_generation = 0;
}

int ycn_avc444_expire(ycn_avc444_gate* gate, ycn_avc444_stats* stats)
{
	if (!gate || !gate->in_progress)
		return 0;
	const uint32_t reason = (gate->op == YCN_AVC444_OP_LUMA_AND_CHROMA)
	                            ? YCN_AVC444_DROP_PARTIAL_OP0
	                            : YCN_AVC444_DROP_ORPHAN_LUMA;
	ycn_avc444_drop(gate, stats, reason);
	return 1;
}

const char* ycn_avc444_drop_reason_str(uint32_t reason)
{
	switch (reason)
	{
		case YCN_AVC444_DROP_NONE:
			return "none";
		case YCN_AVC444_DROP_LUMA_FAILED:
			return "luma-failed";
		case YCN_AVC444_DROP_CHROMA_FAILED:
			return "chroma-failed";
		case YCN_AVC444_DROP_SIZE_MISMATCH:
			return "size-mismatch";
		case YCN_AVC444_DROP_STALE_GENERATION:
			return "stale-generation";
		case YCN_AVC444_DROP_ORPHAN_CHROMA:
			return "orphan-chroma";
		case YCN_AVC444_DROP_ORPHAN_LUMA:
			return "orphan-luma";
		case YCN_AVC444_DROP_PARTIAL_OP0:
			return "partial-op0";
		case YCN_AVC444_DROP_AFTER_RESET:
			return "after-reset";
		case YCN_AVC444_DROP_BAD_OP:
			return "bad-op";
		default:
			return "unknown";
	}
}
