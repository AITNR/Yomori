#ifndef YCN_DIRTY_SNAPSHOT_H
#define YCN_DIRTY_SNAPSHOT_H

#include <stddef.h>
#include <stdint.h>

/*
 * Bounded, allocation-free description of one GDI EndPaint batch.
 *
 * The caller owns the backing storage. A safe dirty copy is possible only
 * while full_required == 0 and count > 0; every malformed or uncertain input
 * switches the batch to full-frame fallback.
 */

#define YCN_DIRTY_SNAPSHOT_MAX_RECTS 2048u
#define YCN_DIRTY_SNAPSHOT_MAX_GDI_CAPACITY 65536u

typedef struct ycn_dirty_snapshot_rect
{
    int32_t x;
    int32_t y;
    int32_t w;
    int32_t h;
} YcnDirtySnapshotRect;

typedef struct ycn_dirty_snapshot
{
    int32_t desktop_width;
    int32_t desktop_height;
    uint32_t count;
    uint32_t full_required;
    uint32_t merged;
    uint32_t invalid_rects;
    uint32_t overflowed;
    uint64_t sum_area;  /* rectangle area with overlaps counted repeatedly */
    uint64_t copy_area; /* area represented by rects[] after optional coalescing */
    YcnDirtySnapshotRect rects[YCN_DIRTY_SNAPSHOT_MAX_RECTS];
} YcnDirtySnapshot;

void ycn_dirty_snapshot_init(YcnDirtySnapshot* snapshot, int32_t width,
                             int32_t height, uint32_t force_full);
void ycn_dirty_snapshot_add_rect(YcnDirtySnapshot* snapshot, int32_t x,
                                 int32_t y, int32_t w, int32_t h);
void ycn_dirty_snapshot_finish(YcnDirtySnapshot* snapshot);

/* Copies only rects[] into an already complete destination frame. Returns 0 on
 * success and -1 when the snapshot or supplied buffers cannot be proven safe. */
int ycn_dirty_snapshot_copy_rects(const YcnDirtySnapshot* snapshot,
                                  const uint8_t* src, size_t src_size,
                                  uint32_t src_stride, uint8_t* dst,
                                  size_t dst_size, uint32_t dst_stride);

#endif /* YCN_DIRTY_SNAPSHOT_H */
