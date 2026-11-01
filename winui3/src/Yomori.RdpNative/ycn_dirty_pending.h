#ifndef YCN_DIRTY_PENDING_H
#define YCN_DIRTY_PENDING_H

#include <stdint.h>
#include "ycn_dirty_snapshot.h"

#define YCN_DIRTY_PENDING_TILE_SIZE 64u
#define YCN_DIRTY_PENDING_MAX_COLS 32u
#define YCN_DIRTY_PENDING_MAX_ROWS 32u
#define YCN_DIRTY_PENDING_MAX_TILES (YCN_DIRTY_PENDING_MAX_COLS * YCN_DIRTY_PENDING_MAX_ROWS)
#define YCN_DIRTY_PENDING_WORDS ((YCN_DIRTY_PENDING_MAX_TILES + 63u) / 64u)
#define YCN_DIRTY_PENDING_MAX_BOXES 2048u

#define YCN_DIRTY_PENDING_EMPTY 0u
#define YCN_DIRTY_PENDING_DIRTY 1u
#define YCN_DIRTY_PENDING_FULL 2u

typedef struct ycn_dirty_pending_rect
{
    int32_t x;
    int32_t y;
    int32_t w;
    int32_t h;
} YcnDirtyPendingRect;

typedef struct ycn_dirty_pending
{
    int32_t width;
    int32_t height;
    uint32_t cols;
    uint32_t rows;
    uint64_t token;
    uint64_t frame_seq;
    uint64_t sealed_seq;
    uint32_t attached;
    uint32_t dimensions_known;
    uint32_t pending_full;
    uint32_t sealed_full;
    uint32_t sealed_active;
    uint64_t pending_tiles[YCN_DIRTY_PENDING_WORDS];
    uint64_t sealed_tiles[YCN_DIRTY_PENDING_WORDS];
    uint32_t box_count;
    YcnDirtyPendingRect boxes[YCN_DIRTY_PENDING_MAX_BOXES];
} YcnDirtyPending;

void ycn_dirty_pending_init(YcnDirtyPending* pending);
void ycn_dirty_pending_attach(YcnDirtyPending* pending, uint64_t token);
int ycn_dirty_pending_detach(YcnDirtyPending* pending, uint64_t token);
void ycn_dirty_pending_reset_stream(YcnDirtyPending* pending);
void ycn_dirty_pending_add_frame(YcnDirtyPending* pending, int32_t width,
                                 int32_t height, uint32_t full,
                                 const YcnDirtySnapshotRect* rects,
                                 uint32_t rect_count);
int ycn_dirty_pending_prepare(YcnDirtyPending* pending,
                              YcnDirtyPendingRect* boxes,
                              uint32_t box_capacity, uint32_t* out_flags,
                              uint32_t* out_box_count, uint64_t* out_seq);
int ycn_dirty_pending_ack(YcnDirtyPending* pending, uint64_t token,
                          uint64_t frame_seq);

#endif
