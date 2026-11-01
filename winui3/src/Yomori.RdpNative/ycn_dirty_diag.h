#ifndef YCN_DIRTY_DIAG_H
#define YCN_DIRTY_DIAG_H

#include <stdint.h>

/*
 * Read-only aggregation for FreeRDP GDI invalid rectangles.
 *
 * This module is deliberately independent of FreeRDP and performs no
 * allocation, logging, or rendering work. The bridge snapshots the public
 * GDI fields in ycn_end_paint() and feeds each rectangle here.
 */

#define YCN_DIRTY_DIAG_MAX_RECTS 65536u

typedef struct ycn_dirty_rect
{
    int32_t x;
    int32_t y;
    int32_t w;
    int32_t h;
} YcnDirtyRect;

typedef struct ycn_dirty_diag_batch
{
    int32_t desktop_width;
    int32_t desktop_height;
    uint64_t desktop_area;
    uint64_t rect_count;
    uint64_t sum_area;       /* sum of clipped rectangle areas; overlaps count twice */
    uint64_t clipped_rects;
    uint64_t overflow_events;
    int64_t bbox_left;
    int64_t bbox_top;
    int64_t bbox_right;      /* exclusive */
    int64_t bbox_bottom;     /* exclusive */
    uint32_t valid_rects;
    uint32_t invalid_null;
    uint32_t list_missing;
    uint32_t bad_capacity;
} YcnDirtyDiagBatch;

typedef struct ycn_dirty_diag_aggregate
{
    uint64_t paints;
    uint64_t rect_count;
    uint64_t max_rects_in_paint;
    uint64_t empty_batches;
    uint64_t full_fallback_batches; /* conservative estimate; no render fallback is performed */
    uint64_t sum_area;
    uint64_t bbox_area_sum;
    uint64_t desktop_area_sum;       /* denominator: readable-list batches with valid bounds */
    uint64_t clipped_rects;
    uint64_t overflow_events;
    uint64_t bad_capacity_batches;
    uint64_t missing_batches;
} YcnDirtyDiagAggregate;

void ycn_dirty_diag_batch_init(YcnDirtyDiagBatch* batch, int32_t width,
                               int32_t height, uint32_t invalid_null);
void ycn_dirty_diag_batch_add_rect(YcnDirtyDiagBatch* batch,
                                   const YcnDirtyRect* rect);
void ycn_dirty_diag_commit(YcnDirtyDiagAggregate* aggregate,
                           const YcnDirtyDiagBatch* batch);
double ycn_dirty_diag_percent(uint64_t part, uint64_t total);

#endif /* YCN_DIRTY_DIAG_H */