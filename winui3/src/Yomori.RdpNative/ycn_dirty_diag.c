#include "ycn_dirty_diag.h"

#include <string.h>

static uint32_t ycn_add_u64(uint64_t* dst, uint64_t value)
{
    if (*dst > UINT64_MAX - value)
    {
        *dst = UINT64_MAX;
        return 1u;
    }
    *dst += value;
    return 0u;
}

void ycn_dirty_diag_batch_init(YcnDirtyDiagBatch* batch, int32_t width,
                               int32_t height, uint32_t invalid_null)
{
    if (!batch)
        return;

    memset(batch, 0, sizeof(*batch));
    batch->desktop_width = width;
    batch->desktop_height = height;
    batch->invalid_null = invalid_null ? 1u : 0u;

    if (width <= 0 || height <= 0)
    {
        batch->overflow_events = 1u;
        return;
    }

    /* INT32_MAX * INT32_MAX remains well below UINT64_MAX. */
    batch->desktop_area = (uint64_t)(uint32_t)width * (uint64_t)(uint32_t)height;
}

void ycn_dirty_diag_batch_add_rect(YcnDirtyDiagBatch* batch,
                                   const YcnDirtyRect* rect)
{
    int64_t x0;
    int64_t y0;
    int64_t x1;
    int64_t y1;
    uint64_t area;

    if (!batch || !rect)
        return;

    if (ycn_add_u64(&batch->rect_count, 1u))
        batch->overflow_events++;

    if (rect->w <= 0 || rect->h <= 0)
    {
        batch->clipped_rects++;
        return;
    }

    if (batch->desktop_width <= 0 || batch->desktop_height <= 0)
    {
        batch->overflow_events++;
        return;
    }

    x0 = rect->x;
    y0 = rect->y;
    x1 = x0 + (int64_t)rect->w;
    y1 = y0 + (int64_t)rect->h;

    if (x0 < 0 || y0 < 0 || x1 > batch->desktop_width ||
        y1 > batch->desktop_height)
        batch->clipped_rects++;

    if (x0 < 0)
        x0 = 0;
    if (y0 < 0)
        y0 = 0;
    if (x1 > batch->desktop_width)
        x1 = batch->desktop_width;
    if (y1 > batch->desktop_height)
        y1 = batch->desktop_height;

    if (x1 <= x0 || y1 <= y0)
        return;

    area = (uint64_t)(x1 - x0) * (uint64_t)(y1 - y0);
    if (ycn_add_u64(&batch->sum_area, area))
        batch->overflow_events++;

    if (batch->valid_rects == 0u)
    {
        batch->bbox_left = x0;
        batch->bbox_top = y0;
        batch->bbox_right = x1;
        batch->bbox_bottom = y1;
    }
    else
    {
        if (x0 < batch->bbox_left)
            batch->bbox_left = x0;
        if (y0 < batch->bbox_top)
            batch->bbox_top = y0;
        if (x1 > batch->bbox_right)
            batch->bbox_right = x1;
        if (y1 > batch->bbox_bottom)
            batch->bbox_bottom = y1;
    }

    batch->valid_rects++;
}

void ycn_dirty_diag_commit(YcnDirtyDiagAggregate* aggregate,
                           const YcnDirtyDiagBatch* batch)
{
    uint32_t overflowed = 0u;
    uint32_t fallback = 0u;

    if (!aggregate || !batch)
        return;

    if (ycn_add_u64(&aggregate->paints, 1u))
        overflowed = 1u;

    if (ycn_add_u64(&aggregate->rect_count, batch->rect_count))
        overflowed = 1u;
    if (batch->rect_count > aggregate->max_rects_in_paint)
        aggregate->max_rects_in_paint = batch->rect_count;

    if (!batch->list_missing && !batch->bad_capacity &&
        (batch->invalid_null || batch->rect_count == 0u))
    {
        if (ycn_add_u64(&aggregate->empty_batches, 1u))
            overflowed = 1u;
    }

    if (batch->list_missing)
    {
        if (ycn_add_u64(&aggregate->missing_batches, 1u))
            overflowed = 1u;
    }

    if (batch->bad_capacity)
    {
        if (ycn_add_u64(&aggregate->bad_capacity_batches, 1u))
            overflowed = 1u;
    }

    if (ycn_add_u64(&aggregate->clipped_rects, batch->clipped_rects))
        overflowed = 1u;
    if (ycn_add_u64(&aggregate->overflow_events, batch->overflow_events))
        overflowed = 1u;

    if (batch->desktop_area > 0u && !batch->list_missing && !batch->bad_capacity)
    {
        if (ycn_add_u64(&aggregate->desktop_area_sum, batch->desktop_area))
            overflowed = 1u;
        if (ycn_add_u64(&aggregate->sum_area, batch->sum_area))
            overflowed = 1u;
        if (batch->valid_rects > 0u)
        {
            const uint64_t bbox_area =
                (uint64_t)(batch->bbox_right - batch->bbox_left) *
                (uint64_t)(batch->bbox_bottom - batch->bbox_top);
            if (ycn_add_u64(&aggregate->bbox_area_sum, bbox_area))
                overflowed = 1u;
        }
    }

    if (overflowed)
    {
        if (aggregate->overflow_events != UINT64_MAX)
            aggregate->overflow_events++;
    }

    fallback = batch->invalid_null || batch->rect_count == 0u ||
               batch->list_missing || batch->bad_capacity ||
               batch->valid_rects == 0u || batch->overflow_events > 0u ||
               overflowed;
    if (fallback)
    {
        if (ycn_add_u64(&aggregate->full_fallback_batches, 1u))
            aggregate->overflow_events = UINT64_MAX;
    }
}

double ycn_dirty_diag_percent(uint64_t part, uint64_t total)
{
    if (total == 0u)
        return 0.0;
    return (double)part * 100.0 / (double)total;
}