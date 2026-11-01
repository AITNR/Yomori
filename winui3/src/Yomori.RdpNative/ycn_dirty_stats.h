#ifndef YCN_DIRTY_STATS_H
#define YCN_DIRTY_STATS_H

#include <stdint.h>

/*
 * Pure, allocation-free aggregation for the dirty-frame C ABI.
 *
 * The bridge keeps one YcnDirtyStats per session and updates it only while
 * holding the bridge lock, so plain increments are already serialized.  This
 * module stays independent of FreeRDP so the classification and saturation
 * rules can be unit tested without a live RDP session.
 */

#define YCN_DIRTY_STATS_KIND_EMPTY 0u
#define YCN_DIRTY_STATS_KIND_BOXES 1u
#define YCN_DIRTY_STATS_KIND_FULL  2u

/* Reason buckets deliberately mirror the public error codes in ycn_rdp.h. */
typedef enum ycn_dirty_stats_reason
{
    YCN_DIRTY_REASON_OK = 0,
    YCN_DIRTY_REASON_NOT_ATTACHED = 1,
    YCN_DIRTY_REASON_STALE_TOKEN = 2,
    YCN_DIRTY_REASON_STALE_SEQ = 3,
    YCN_DIRTY_REASON_NOT_CONNECTED = 4,
    YCN_DIRTY_REASON_INVALID_ARG = 5,
    YCN_DIRTY_REASON_NO_MEMORY = 6,
    YCN_DIRTY_REASON_SESSION_NOT_FOUND = 7,
    YCN_DIRTY_REASON_OTHER = 8,
    YCN_DIRTY_REASON_COUNT = 9
} YcnDirtyStatsReason;

typedef struct ycn_dirty_stats
{
    uint64_t copy_ok;
    uint64_t copy_fail;
    uint64_t kind_empty;
    uint64_t kind_boxes;
    uint64_t kind_full;
    uint64_t boxes_total;
    uint64_t boxes_max;
    uint64_t bytes_total;
    uint64_t ack_ok;
    uint64_t ack_fail;
    uint64_t attach_fail;
    uint64_t detach_fail;
    uint64_t reason[YCN_DIRTY_REASON_COUNT];
} YcnDirtyStats;

void ycn_dirty_stats_init(YcnDirtyStats* stats);
YcnDirtyStatsReason ycn_dirty_stats_classify(int rc);
void ycn_dirty_stats_record_copy(YcnDirtyStats* stats, int rc, uint32_t kind,
                                 uint32_t box_count, uint64_t bytes);
void ycn_dirty_stats_record_ack(YcnDirtyStats* stats, int rc);
void ycn_dirty_stats_record_attach(YcnDirtyStats* stats, int rc);
void ycn_dirty_stats_record_detach(YcnDirtyStats* stats, int rc);

#endif /* YCN_DIRTY_STATS_H */