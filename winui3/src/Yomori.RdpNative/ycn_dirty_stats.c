#include "ycn_dirty_stats.h"

#include <string.h>

/* Keep the bucket mapping in one place; values match ycn_rdp.h. */
static YcnDirtyStatsReason classify_rc(int rc)
{
    switch (rc)
    {
        case 0:
            return YCN_DIRTY_REASON_OK;
        case -1: /* YCN_ERR_INVALID_ARG */
            return YCN_DIRTY_REASON_INVALID_ARG;
        case -2: /* YCN_ERR_NO_MEMORY */
            return YCN_DIRTY_REASON_NO_MEMORY;
        case -4: /* YCN_ERR_SESSION_NOT_FOUND */
            return YCN_DIRTY_REASON_SESSION_NOT_FOUND;
        case -8: /* YCN_ERR_NOT_CONNECTED */
            return YCN_DIRTY_REASON_NOT_CONNECTED;
        case -20: /* YCN_ERR_DIRTY_NOT_ATTACHED */
            return YCN_DIRTY_REASON_NOT_ATTACHED;
        case -21: /* YCN_ERR_DIRTY_STALE_TOKEN */
            return YCN_DIRTY_REASON_STALE_TOKEN;
        case -22: /* YCN_ERR_DIRTY_STALE_SEQ */
            return YCN_DIRTY_REASON_STALE_SEQ;
        default:
            return YCN_DIRTY_REASON_OTHER;
    }
}

static void add_u64(uint64_t* target, uint64_t value)
{
    if (UINT64_MAX - *target < value)
        *target = UINT64_MAX;
    else
        *target += value;
}

void ycn_dirty_stats_init(YcnDirtyStats* stats)
{
    if (stats)
        memset(stats, 0, sizeof(*stats));
}

YcnDirtyStatsReason ycn_dirty_stats_classify(int rc)
{
    return classify_rc(rc);
}

void ycn_dirty_stats_record_copy(YcnDirtyStats* stats, int rc, uint32_t kind,
                                 uint32_t box_count, uint64_t bytes)
{
    if (!stats)
        return;

    if (rc == 0)
    {
        stats->copy_ok++;
        switch (kind)
        {
            case YCN_DIRTY_STATS_KIND_EMPTY:
                stats->kind_empty++;
                break;
            case YCN_DIRTY_STATS_KIND_FULL:
                stats->kind_full++;
                add_u64(&stats->bytes_total, bytes);
                break;
            case YCN_DIRTY_STATS_KIND_BOXES:
                stats->kind_boxes++;
                add_u64(&stats->boxes_total, box_count);
                if (box_count > stats->boxes_max)
                    stats->boxes_max = box_count;
                add_u64(&stats->bytes_total, bytes);
                break;
            default:
                break;
        }
        return;
    }

    stats->copy_fail++;
    stats->reason[classify_rc(rc)]++;
}

void ycn_dirty_stats_record_ack(YcnDirtyStats* stats, int rc)
{
    if (!stats)
        return;
    if (rc == 0)
        stats->ack_ok++;
    else
    {
        stats->ack_fail++;
        stats->reason[classify_rc(rc)]++;
    }
}

void ycn_dirty_stats_record_attach(YcnDirtyStats* stats, int rc)
{
    if (!stats)
        return;
    if (rc != 0)
    {
        stats->attach_fail++;
        stats->reason[classify_rc(rc)]++;
    }
}

void ycn_dirty_stats_record_detach(YcnDirtyStats* stats, int rc)
{
    if (!stats)
        return;
    if (rc != 0)
    {
        stats->detach_fail++;
        stats->reason[classify_rc(rc)]++;
    }
}