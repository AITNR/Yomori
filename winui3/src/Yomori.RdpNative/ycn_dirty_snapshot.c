#include "ycn_dirty_snapshot.h"

#include <limits.h>
#include <string.h>

static uint32_t ycn_dirty_add_u64(uint64_t* dst, uint64_t value)
{
    if (*dst > UINT64_MAX - value)
    {
        *dst = UINT64_MAX;
        return 1u;
    }
    *dst += value;
    return 0u;
}

void ycn_dirty_snapshot_init(YcnDirtySnapshot* snapshot, int32_t width,
                             int32_t height, uint32_t force_full)
{
    if (!snapshot)
        return;

    /* Do not clear the fixed rectangle array on every EndPaint: only the
     * header and live count are state; the array is overwritten as rects are
     * appended. */
    snapshot->desktop_width = width;
    snapshot->desktop_height = height;
    snapshot->count = 0u;
    snapshot->full_required = force_full ? 1u : 0u;
    snapshot->merged = 0u;
    snapshot->invalid_rects = 0u;
    snapshot->overflowed = 0u;
    snapshot->sum_area = 0u;
    snapshot->copy_area = 0u;
    if (width <= 0 || height <= 0)
        snapshot->full_required = 1u;
}

void ycn_dirty_snapshot_add_rect(YcnDirtySnapshot* snapshot, int32_t x,
                                 int32_t y, int32_t w, int32_t h)
{
    int64_t right;
    int64_t bottom;
    uint64_t area;

    if (!snapshot || snapshot->full_required)
        return;

    if (snapshot->desktop_width <= 0 || snapshot->desktop_height <= 0 ||
        x < 0 || y < 0 || w <= 0 || h <= 0)
    {
        snapshot->invalid_rects++;
        snapshot->full_required = 1u;
        return;
    }

    right = (int64_t)x + (int64_t)w;
    bottom = (int64_t)y + (int64_t)h;
    if (right > snapshot->desktop_width || bottom > snapshot->desktop_height)
    {
        snapshot->invalid_rects++;
        snapshot->full_required = 1u;
        return;
    }

    if (snapshot->count >= YCN_DIRTY_SNAPSHOT_MAX_RECTS)
    {
        snapshot->overflowed = 1u;
        snapshot->full_required = 1u;
        return;
    }

    area = (uint64_t)(uint32_t)w * (uint64_t)(uint32_t)h;
    if (ycn_dirty_add_u64(&snapshot->sum_area, area))
    {
        snapshot->overflowed = 1u;
        snapshot->full_required = 1u;
        return;
    }

    snapshot->rects[snapshot->count].x = x;
    snapshot->rects[snapshot->count].y = y;
    snapshot->rects[snapshot->count].w = w;
    snapshot->rects[snapshot->count].h = h;
    snapshot->count++;
}

void ycn_dirty_snapshot_finish(YcnDirtySnapshot* snapshot)
{
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
    uint64_t desktop_area;
    uint64_t bbox_area;
    uint32_t i;

    if (!snapshot || snapshot->full_required)
        return;

    if (snapshot->desktop_width <= 0 || snapshot->desktop_height <= 0 ||
        snapshot->count == 0u || snapshot->sum_area == 0u)
    {
        snapshot->full_required = 1u;
        return;
    }

    left = snapshot->rects[0].x;
    top = snapshot->rects[0].y;
    right = left + snapshot->rects[0].w;
    bottom = top + snapshot->rects[0].h;

    for (i = 1u; i < snapshot->count; i++)
    {
        const YcnDirtySnapshotRect* rect = &snapshot->rects[i];
        if (rect->x < left)
            left = rect->x;
        if (rect->y < top)
            top = rect->y;
        if (rect->x + rect->w > right)
            right = rect->x + rect->w;
        if (rect->y + rect->h > bottom)
            bottom = rect->y + rect->h;
    }

    desktop_area = (uint64_t)(uint32_t)snapshot->desktop_width *
                   (uint64_t)(uint32_t)snapshot->desktop_height;
    bbox_area = (uint64_t)(uint32_t)(right - left) *
                (uint64_t)(uint32_t)(bottom - top);

    /* Coalescing to the bounding box is safe and only accepted when it cannot
     * increase the number of copied pixels compared with the rectangle sum. */
    if (snapshot->count > 1u && bbox_area <= snapshot->sum_area)
    {
        snapshot->rects[0].x = left;
        snapshot->rects[0].y = top;
        snapshot->rects[0].w = right - left;
        snapshot->rects[0].h = bottom - top;
        snapshot->count = 1u;
        snapshot->merged = 1u;
        snapshot->copy_area = bbox_area;
    }
    else
    {
        snapshot->copy_area = snapshot->sum_area;
    }

    if (snapshot->copy_area >= desktop_area)
        snapshot->full_required = 1u;
}

int ycn_dirty_snapshot_copy_rects(const YcnDirtySnapshot* snapshot,
                                  const uint8_t* src, size_t src_size,
                                  uint32_t src_stride, uint8_t* dst,
                                  size_t dst_size, uint32_t dst_stride)
{
    uint32_t row_bytes;
    uint32_t i;

    if (!snapshot || !src || !dst || snapshot->full_required ||
        snapshot->count == 0u || snapshot->desktop_width <= 0 ||
        snapshot->desktop_height <= 0 ||
        snapshot->desktop_width > (int32_t)(UINT32_MAX / 4u))
        return -1;

    row_bytes = (uint32_t)snapshot->desktop_width * 4u;
    if (src_stride < row_bytes || dst_stride < row_bytes)
        return -1;

    if ((uint64_t)src_stride * (uint64_t)(snapshot->desktop_height - 1) +
            row_bytes > src_size ||
        (uint64_t)dst_stride * (uint64_t)(snapshot->desktop_height - 1) +
            row_bytes > dst_size)
        return -1;

    /* Validate the complete plan before touching dst; a failed copy must never
     * leave a partially updated frame. */
    for (i = 0u; i < snapshot->count; i++)
    {
        const YcnDirtySnapshotRect* rect = &snapshot->rects[i];
        if (rect->x < 0 || rect->y < 0 || rect->w <= 0 || rect->h <= 0 ||
            (int64_t)rect->x + rect->w > snapshot->desktop_width ||
            (int64_t)rect->y + rect->h > snapshot->desktop_height)
            return -1;
    }

    for (i = 0u; i < snapshot->count; i++)
    {
        const YcnDirtySnapshotRect* rect = &snapshot->rects[i];
        const uint32_t bytes = (uint32_t)rect->w * 4u;
        const size_t src_offset = (size_t)rect->y * src_stride + (size_t)rect->x * 4u;
        const size_t dst_offset = (size_t)rect->y * dst_stride + (size_t)rect->x * 4u;
        int32_t row;

        for (row = 0; row < rect->h; row++)
        {
            const size_t src_row = src_offset + (size_t)row * src_stride;
            const size_t dst_row = dst_offset + (size_t)row * dst_stride;
            memcpy(dst + dst_row, src + src_row, bytes);
        }
    }
    return 0;
}
