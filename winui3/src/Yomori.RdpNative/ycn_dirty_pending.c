#include "ycn_dirty_pending.h"

#include <string.h>

static void clear_bits(uint64_t* bits)
{
    memset(bits, 0, sizeof(uint64_t) * YCN_DIRTY_PENDING_WORDS);
}

static uint32_t has_bits(const uint64_t* bits)
{
    uint32_t i;
    for (i = 0; i < YCN_DIRTY_PENDING_WORDS; i++)
        if (bits[i] != 0u) return 1u;
    return 0u;
}

static int set_dimensions(YcnDirtyPending* p, int32_t width, int32_t height)
{
    uint32_t cols;
    uint32_t rows;
    if (width <= 0 || height <= 0) return -1;
    cols = ((uint32_t)width + 63u) / 64u;
    rows = ((uint32_t)height + 63u) / 64u;
    if (cols == 0u || rows == 0u || cols > YCN_DIRTY_PENDING_MAX_COLS ||
        rows > YCN_DIRTY_PENDING_MAX_ROWS) return -1;
    p->width = width;
    p->height = height;
    p->cols = cols;
    p->rows = rows;
    p->dimensions_known = 1u;
    return 0;
}

static void mark_rect(YcnDirtyPending* p, int32_t x, int32_t y,
                      int32_t w, int32_t h)
{
    uint32_t x0 = (uint32_t)x / 64u;
    uint32_t y0 = (uint32_t)y / 64u;
    uint32_t x1 = ((uint32_t)(x + w) + 63u) / 64u;
    uint32_t y1 = ((uint32_t)(y + h) + 63u) / 64u;
    uint32_t row;
    if (x1 > p->cols) x1 = p->cols;
    if (y1 > p->rows) y1 = p->rows;
    for (row = y0; row < y1; row++)
    {
        uint32_t col;
        for (col = x0; col < x1; col++)
        {
            uint32_t index = row * p->cols + col;
            p->pending_tiles[index / 64u] |= 1ull << (index % 64u);
        }
    }
}

void ycn_dirty_pending_init(YcnDirtyPending* pending)
{
    if (pending) memset(pending, 0, sizeof(*pending));
}

void ycn_dirty_pending_attach(YcnDirtyPending* pending, uint64_t token)
{
    if (!pending) return;
    clear_bits(pending->pending_tiles);
    clear_bits(pending->sealed_tiles);
    pending->token = token;
    pending->attached = 1u;
    pending->dimensions_known = 0u;
    pending->pending_full = 1u;
    pending->sealed_full = 0u;
    pending->sealed_active = 0u;
    pending->frame_seq = 0u;
    pending->sealed_seq = 0u;
    pending->box_count = 0u;
}

int ycn_dirty_pending_detach(YcnDirtyPending* pending, uint64_t token)
{
    if (!pending || !pending->attached || pending->token != token) return -1;
    pending->attached = 0u;
    pending->sealed_active = 0u;
    clear_bits(pending->pending_tiles);
    clear_bits(pending->sealed_tiles);
    return 0;
}

void ycn_dirty_pending_reset_stream(YcnDirtyPending* pending)
{
    if (!pending) return;
    pending->pending_full = 1u;
    pending->sealed_active = 0u;
    pending->sealed_full = 0u;
    pending->box_count = 0u;
    clear_bits(pending->pending_tiles);
    clear_bits(pending->sealed_tiles);
}

void ycn_dirty_pending_add_frame(YcnDirtyPending* pending, int32_t width,
                                 int32_t height, uint32_t full,
                                 const YcnDirtySnapshotRect* rects,
                                 uint32_t rect_count)
{
    uint32_t i;
    if (!pending || !pending->attached) return;
    pending->frame_seq++;
    if (!pending->dimensions_known || pending->width != width ||
        pending->height != height)
    {
        if (set_dimensions(pending, width, height) != 0)
        {
            pending->pending_full = 1u;
            clear_bits(pending->pending_tiles);
            return;
        }
        pending->pending_full = 1u;
        clear_bits(pending->pending_tiles);
        if (pending->sealed_active) pending->sealed_full = 1u;
        return;
    }
    if (pending->pending_full || full || !rects || rect_count == 0u)
    {
        pending->pending_full = 1u;
        clear_bits(pending->pending_tiles);
        return;
    }
    for (i = 0; i < rect_count; i++)
    {
        const YcnDirtySnapshotRect* r = &rects[i];
        if (r->x < 0 || r->y < 0 || r->w <= 0 || r->h <= 0 ||
            (int64_t)r->x + r->w > width ||
            (int64_t)r->y + r->h > height)
        {
            pending->pending_full = 1u;
            clear_bits(pending->pending_tiles);
            return;
        }
        mark_rect(pending, r->x, r->y, r->w, r->h);
    }
}

static int build_boxes(YcnDirtyPending* pending)
{
    uint32_t row;
    pending->box_count = 0u;
    for (row = 0; row < pending->rows; row++)
    {
        uint32_t col = 0;
        while (col < pending->cols)
        {
            uint32_t start;
            while (col < pending->cols)
            {
                uint32_t index = row * pending->cols + col;
                if (pending->sealed_tiles[index / 64u] & (1ull << (index % 64u))) break;
                col++;
            }
            if (col >= pending->cols) break;
            start = col++;
            while (col < pending->cols)
            {
                uint32_t index = row * pending->cols + col;
                if (!(pending->sealed_tiles[index / 64u] & (1ull << (index % 64u)))) break;
                col++;
            }
            if (pending->box_count >= YCN_DIRTY_PENDING_MAX_BOXES)
            {
                pending->sealed_full = 1u;
                pending->box_count = 0u;
                return 0;
            }
            {
                YcnDirtyPendingRect* box = &pending->boxes[pending->box_count++];
                box->x = (int32_t)(start * 64u);
                box->y = (int32_t)(row * 64u);
                box->w = (int32_t)((col - start) * 64u);
                box->h = 64;
                if (box->x + box->w > pending->width) box->w = pending->width - box->x;
                if (box->y + box->h > pending->height) box->h = pending->height - box->y;
            }
        }
    }
    return 0;
}

int ycn_dirty_pending_prepare(YcnDirtyPending* pending,
                              YcnDirtyPendingRect* boxes,
                              uint32_t box_capacity, uint32_t* out_flags,
                              uint32_t* out_box_count, uint64_t* out_seq)
{
    if (!pending || !pending->attached || !boxes || !out_flags ||
        !out_box_count || !out_seq) return -1;
    if (pending->sealed_active)
    {
        *out_flags = pending->sealed_full ? YCN_DIRTY_PENDING_FULL : YCN_DIRTY_PENDING_DIRTY;
        *out_box_count = pending->sealed_full ? 0u : pending->box_count;
        *out_seq = pending->sealed_seq;
        if (*out_box_count > box_capacity) return -1;
        if (*out_box_count) memcpy(boxes, pending->boxes, sizeof(*boxes) * *out_box_count);
        return 0;
    }
    if (!pending->pending_full && !has_bits(pending->pending_tiles))
    {
        *out_flags = YCN_DIRTY_PENDING_EMPTY;
        *out_box_count = 0u;
        *out_seq = pending->frame_seq;
        return 0;
    }
    pending->sealed_active = 1u;
    pending->sealed_seq = pending->frame_seq;
    pending->sealed_full = pending->pending_full;
    pending->box_count = 0u;
    if (!pending->sealed_full)
    {
        memcpy(pending->sealed_tiles, pending->pending_tiles, sizeof(pending->pending_tiles));
        clear_bits(pending->pending_tiles);
        build_boxes(pending);
    }
    pending->pending_full = 0u;
    *out_flags = pending->sealed_full ? YCN_DIRTY_PENDING_FULL : YCN_DIRTY_PENDING_DIRTY;
    *out_box_count = pending->sealed_full ? 0u : pending->box_count;
    *out_seq = pending->sealed_seq;
    if (*out_box_count > box_capacity) return -1;
    if (*out_box_count) memcpy(boxes, pending->boxes, sizeof(*boxes) * *out_box_count);
    return 0;
}

int ycn_dirty_pending_ack(YcnDirtyPending* pending, uint64_t token, uint64_t frame_seq)
{
    if (!pending || !pending->attached || pending->token != token ||
        !pending->sealed_active || pending->sealed_seq != frame_seq) return -1;
    pending->sealed_active = 0u;
    pending->sealed_full = 0u;
    pending->box_count = 0u;
    clear_bits(pending->sealed_tiles);
    return 0;
}
