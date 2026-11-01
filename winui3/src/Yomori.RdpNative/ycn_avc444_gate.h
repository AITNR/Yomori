/* AVC444 / AVC444v2 整帧提交闸门。
 *
 * AVC444 的一条 RDPGFX 命令可能只携带亮度流（op=1）或只携带色度流（op=2），
 * 也可能两条流一起（op=0）。面板只允许看到「同一帧的亮度与色度都到齐且尺寸
 * 一致」的结果：任何半帧、错序帧、尺寸不匹配帧都必须整帧丢弃并请求整屏刷新，
 * 绝不允许「先显示主流、色度后到」——那样会闪一帧错色。
 *
 * 本模块是纯状态机：不碰 D3D、不碰 FreeRDP、不做像素运算，因此可以在没有
 * 服务器的前提下被单元测试完整覆盖。租约（lease）也在这里记账，丢弃路径必须
 * 把两条流的租约都归还，否则解码器侧会累积未释放的 AVFrame。
 */
#ifndef YCN_AVC444_GATE_H
#define YCN_AVC444_GATE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 与 RDPGFX_AVC444_BITMAP_STREAM::LC 一致。 */
#define YCN_AVC444_OP_LUMA_AND_CHROMA 0u
#define YCN_AVC444_OP_LUMA_ONLY 1u
#define YCN_AVC444_OP_CHROMA_ONLY 2u

/* 丢弃原因：出现在统计里，也用于「为什么这一帧没上屏」的排障。 */
typedef enum
{
	YCN_AVC444_DROP_NONE = 0,
	YCN_AVC444_DROP_LUMA_FAILED = 1,      /* 亮度流解码失败 */
	YCN_AVC444_DROP_CHROMA_FAILED = 2,    /* 色度流解码失败 */
	YCN_AVC444_DROP_SIZE_MISMATCH = 3,    /* 两条流尺寸不一致 */
	YCN_AVC444_DROP_STALE_GENERATION = 4, /* 比已提交的帧旧 */
	YCN_AVC444_DROP_ORPHAN_CHROMA = 5,    /* 没有对应亮度的孤立色度 */
	YCN_AVC444_DROP_ORPHAN_LUMA = 6,      /* 亮度一直等不到色度 */
	YCN_AVC444_DROP_PARTIAL_OP0 = 7,      /* op0 只到了一半 */
	YCN_AVC444_DROP_AFTER_RESET = 8,      /* reset 之后的残帧 */
	YCN_AVC444_DROP_BAD_OP = 9,           /* 未知 op */
	YCN_AVC444_DROP_REASON_COUNT = 10
} ycn_avc444_drop_reason;

typedef struct
{
	/* 累计计数 */
	uint64_t frames_offered;     /* 进入闸门的命令数 */
	uint64_t frames_committed;   /* 真正允许上屏的整帧 */
	uint64_t frames_dropped;     /* 整帧丢弃 */
	uint64_t frames_pending;     /* op1 之后等待色度的次数 */
	uint64_t leases_acquired;
	uint64_t leases_released;
	uint64_t leases_outstanding; /* 必须恒为 0，不为 0 就是泄漏 */
	/* 按原因计数，索引即 ycn_avc444_drop_reason */
	uint64_t drops_by_reason[YCN_AVC444_DROP_REASON_COUNT];
	/* 最近一次结果 */
	uint32_t last_op;
	uint32_t last_drop;  /* ycn_avc444_drop_reason */
	uint32_t last_width; /* 最近一次提交的整帧尺寸 */
	uint32_t last_height;
	uint32_t generation;        /* 最近一次提交的帧代号 */
	uint32_t need_full_refresh; /* 最近一次丢弃后是否要求整屏刷新 */
} ycn_avc444_stats;

typedef struct
{
	/* 状态：一帧正在组装时 in_progress=1 */
	int in_progress;
	uint32_t op;
	uint32_t width; /* 亮度流尺寸 */
	uint32_t height;
	uint32_t chroma_width; /* 色度流尺寸，必须与亮度一致 */
	uint32_t chroma_height;
	int luma_ok;
	int chroma_ok;
	void* luma_lease;
	void* chroma_lease;
	/* 代号：每开始一帧 +1，提交/丢弃后结算 */
	uint32_t next_generation;
	uint32_t committed_generation;
	uint32_t active_generation;
	uint32_t reset_epoch;
	/* 租约归还回调：由调用方提供（桥里是 freerdp 的 Unref） */
	void (*release_lease)(void* lease);
} ycn_avc444_gate;

void ycn_avc444_gate_init(ycn_avc444_gate* gate, void (*release_lease)(void* lease));

/* 一帧开始。op 见 YCN_AVC444_OP_*；generation 传 0 表示自动分配。
 * `lease` 是当前命令持有的流租约（可为 NULL），所有权交给闸门，闸门必然释放。
 * 返回本次组装的帧代号（>0）；返回 0 表示命令被立即拒绝（op 非法 / 代号过期），
 * 此时 stats->last_drop 已写明原因，租约也已释放。 */
uint32_t ycn_avc444_begin(ycn_avc444_gate* gate, ycn_avc444_stats* stats, uint32_t op,
                          uint32_t generation, uint32_t width, uint32_t height, void* luma_lease,
                          void* chroma_lease);

/* 声明某条流的结果。ok=0 时该流失败，整帧必丢。
 * `lease` 可为 NULL：本次命令没有新租约（例如复用上一帧的流）。 */
void ycn_avc444_part(ycn_avc444_gate* gate, ycn_avc444_stats* stats, int is_chroma, int ok,
                     uint32_t width, uint32_t height, void* lease);

/* 结算：只有两条流都到齐且尺寸一致才允许上屏。
 * 返回 1 = 整帧可以上屏；返回 0 = 整帧丢弃，stats->last_drop 写明原因。
 * 无论成败闸门都回到空闲态，在途租约全部释放。 */
int ycn_avc444_commit(ycn_avc444_gate* gate, ycn_avc444_stats* stats);

/* 会话重置 / 设备重建：丢弃在途帧并清空租约。 */
void ycn_avc444_reset(ycn_avc444_gate* gate, ycn_avc444_stats* stats);

/* 在途帧超时（迟迟等不到后续流）：整帧丢弃。返回 1 表示确实丢了一帧。 */
int ycn_avc444_expire(ycn_avc444_gate* gate, ycn_avc444_stats* stats);

const char* ycn_avc444_drop_reason_str(uint32_t reason);

#ifdef __cplusplus
}
#endif

#endif /* YCN_AVC444_GATE_H */
