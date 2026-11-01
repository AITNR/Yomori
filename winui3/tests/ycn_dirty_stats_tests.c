#include "ycn_dirty_stats.h"

#include <stdio.h>

#define C(x)                                                                  \
    do                                                                        \
    {                                                                         \
        if (!(x))                                                             \
        {                                                                     \
            fprintf(stderr, "FAIL %d: %s\n", __LINE__, #x);                   \
            return 1;                                                         \
        }                                                                     \
    } while (0)

int main(void)
{
    YcnDirtyStats stats;

    ycn_dirty_stats_init(&stats);
    C(stats.copy_ok == 0 && stats.copy_fail == 0);
    C(stats.reason[YCN_DIRTY_REASON_OK] == 0);

    /* Error-code classification matches ycn_rdp.h. */
    C(ycn_dirty_stats_classify(0) == YCN_DIRTY_REASON_OK);
    C(ycn_dirty_stats_classify(-1) == YCN_DIRTY_REASON_INVALID_ARG);
    C(ycn_dirty_stats_classify(-2) == YCN_DIRTY_REASON_NO_MEMORY);
    C(ycn_dirty_stats_classify(-4) == YCN_DIRTY_REASON_SESSION_NOT_FOUND);
    C(ycn_dirty_stats_classify(-8) == YCN_DIRTY_REASON_NOT_CONNECTED);
    C(ycn_dirty_stats_classify(-20) == YCN_DIRTY_REASON_NOT_ATTACHED);
    C(ycn_dirty_stats_classify(-21) == YCN_DIRTY_REASON_STALE_TOKEN);
    C(ycn_dirty_stats_classify(-22) == YCN_DIRTY_REASON_STALE_SEQ);
    C(ycn_dirty_stats_classify(1234) == YCN_DIRTY_REASON_OTHER);

    /* Successful copies split by result kind and accumulate real work. */
    ycn_dirty_stats_record_copy(&stats, 0, YCN_DIRTY_STATS_KIND_EMPTY, 0, 0);
    ycn_dirty_stats_record_copy(&stats, 0, YCN_DIRTY_STATS_KIND_BOXES, 3,
                                3u * 64u * 64u * 4u);
    ycn_dirty_stats_record_copy(&stats, 0, YCN_DIRTY_STATS_KIND_BOXES, 7,
                                7u * 10u * 4u * 8u);
    ycn_dirty_stats_record_copy(&stats, 0, YCN_DIRTY_STATS_KIND_FULL, 0,
                                1920u * 1080u * 4u);
    C(stats.copy_ok == 4 && stats.copy_fail == 0);
    C(stats.kind_empty == 1);
    C(stats.kind_boxes == 2);
    C(stats.kind_full == 1);
    C(stats.boxes_total == 10);
    C(stats.boxes_max == 7);
    C(stats.bytes_total == 3u * 64u * 64u * 4u + 7u * 10u * 4u * 8u +
                               1920u * 1080u * 4u);

    /* Failed copies are bucketed by reason and never touch kind counters. */
    ycn_dirty_stats_record_copy(&stats, -20, YCN_DIRTY_STATS_KIND_EMPTY, 0, 0);
    ycn_dirty_stats_record_copy(&stats, -21, YCN_DIRTY_STATS_KIND_EMPTY, 0, 0);
    ycn_dirty_stats_record_copy(&stats, -2, YCN_DIRTY_STATS_KIND_EMPTY, 0, 0);
    ycn_dirty_stats_record_copy(&stats, -77, YCN_DIRTY_STATS_KIND_EMPTY, 0, 0);
    C(stats.copy_fail == 4);
    C(stats.reason[YCN_DIRTY_REASON_NOT_ATTACHED] == 1);
    C(stats.reason[YCN_DIRTY_REASON_STALE_TOKEN] == 1);
    C(stats.reason[YCN_DIRTY_REASON_NO_MEMORY] == 1);
    C(stats.reason[YCN_DIRTY_REASON_OTHER] == 1);
    C(stats.kind_empty == 1);

    /* ACK / attach / detach failures share the same reason buckets. */
    ycn_dirty_stats_record_ack(&stats, 0);
    ycn_dirty_stats_record_ack(&stats, -22);
    ycn_dirty_stats_record_ack(&stats, -20);
    C(stats.ack_ok == 1 && stats.ack_fail == 2);
    C(stats.reason[YCN_DIRTY_REASON_STALE_SEQ] == 1);
    C(stats.reason[YCN_DIRTY_REASON_NOT_ATTACHED] == 2);

    ycn_dirty_stats_record_attach(&stats, 0);
    ycn_dirty_stats_record_attach(&stats, -8);
    ycn_dirty_stats_record_detach(&stats, 0);
    ycn_dirty_stats_record_detach(&stats, -21);
    C(stats.attach_fail == 1 && stats.detach_fail == 1);
    C(stats.reason[YCN_DIRTY_REASON_NOT_CONNECTED] == 1);
    C(stats.reason[YCN_DIRTY_REASON_STALE_TOKEN] == 2);

    /* Saturating add keeps monotonic counters from wrapping. */
    ycn_dirty_stats_init(&stats);
    stats.bytes_total = UINT64_MAX - 4u;
    ycn_dirty_stats_record_copy(&stats, 0, YCN_DIRTY_STATS_KIND_FULL, 0, 8u);
    C(stats.bytes_total == UINT64_MAX);
    stats.boxes_total = UINT64_MAX - 1u;
    ycn_dirty_stats_record_copy(&stats, 0, YCN_DIRTY_STATS_KIND_BOXES, 5, 1u);
    C(stats.boxes_total == UINT64_MAX);

    puts("dirty stats ok");
    return 0;
}