/* 阶段 2B：dirty pending 纯 C 集成 / ABI 状态测试。
 *
 * 不链接 FreeRDP：只编 ycn_dirty_pending.c，并用与 ycn_rdp.c
 * ycn_rdp_dirty_copy_pending() 相同的稳定帧复制循环验证状态机语义：
 *   attach 强制 FULL / BOXES 只写 sealed 矩形 / FULL 写整帧 /
 *   sealed 冻结 / ack token+seq / resize/reset/非法矩形强制 FULL / detach。
 */
#include "ycn_rdp.h"
#include "ycn_dirty_pending.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static int g_failures = 0;

#define CHECK(expr)                                                              \
    do                                                                           \
    {                                                                            \
        if (!(expr))                                                             \
        {                                                                        \
            fprintf(stderr, "%s:%d: CHECK failed: %s\n", __FILE__, __LINE__,     \
                    #expr);                                                      \
            g_failures++;                                                        \
        }                                                                        \
    } while (0)

#define STATIC_ASSERT(name, expr) typedef char static_assert_##name[(expr) ? 1 : -1]

/* ABI 版本 / 公共结构布局必须与 ycn_rdp.h 一致。 */
STATIC_ASSERT(dirty_api_version, YCN_DIRTY_API_VERSION == 1u);
STATIC_ASSERT(dirty_rect_size, sizeof(ycn_rdp_dirty_rect) == 16u);
STATIC_ASSERT(dirty_info_seq_offset, offsetof(ycn_rdp_dirty_info, seq) == 24u);
STATIC_ASSERT(dirty_info_token_offset, offsetof(ycn_rdp_dirty_info, token) == 32u);
STATIC_ASSERT(dirty_info_size, sizeof(ycn_rdp_dirty_info) == 40u);
STATIC_ASSERT(dirty_kinds_distinct,
              YCN_DIRTY_KIND_EMPTY != YCN_DIRTY_KIND_BOXES &&
              YCN_DIRTY_KIND_BOXES != YCN_DIRTY_KIND_FULL &&
              YCN_DIRTY_KIND_EMPTY != YCN_DIRTY_KIND_FULL);
STATIC_ASSERT(dirty_errors_are_negative,
              YCN_ERR_DIRTY_NOT_ATTACHED < 0 && YCN_ERR_DIRTY_STALE_TOKEN < 0 &&
              YCN_ERR_DIRTY_STALE_SEQ < 0);

#define W 200u
#define H 150u
#define STRIDE (W * 4u)
#define FRAME_BYTES (STRIDE * H)
#define TOKEN_A 0x1001ull
#define TOKEN_B 0x2002ull

static uint8_t g_src[FRAME_BYTES];
static uint8_t g_dst[FRAME_BYTES];
static uint8_t g_expected[FRAME_BYTES];
static YcnDirtyPending g_pending;
static YcnDirtyPendingRect g_boxes[64];

static void fill_pattern(uint8_t* data, size_t size, uint8_t seed)
{
    size_t i;
    for (i = 0; i < size; i++)
        data[i] = (uint8_t)(seed + i * 17u);
}

/* 与 ycn_rdp_dirty_copy_pending() 的 FULL 路径一致。 */
static void copy_full(const uint8_t* src, uint8_t* dst)
{
    memcpy(dst, src, FRAME_BYTES);
}

/* 与 ycn_rdp_dirty_copy_pending() 的 BOXES 路径一致（只写 sealed 矩形）。 */
static void copy_boxes(const uint8_t* src, uint8_t* dst,
                       const YcnDirtyPendingRect* boxes, uint32_t count)
{
    uint32_t i;
    for (i = 0; i < count; i++)
    {
        const YcnDirtyPendingRect* b = &boxes[i];
        uint32_t row;
        if (b->x < 0 || b->y < 0 || b->w <= 0 || b->h <= 0)
            continue;
        for (row = 0; row < (uint32_t)b->h; row++)
        {
            memcpy(dst + (size_t)(b->y + (int32_t)row) * STRIDE +
                       (size_t)b->x * 4u,
                   src + (size_t)(b->y + (int32_t)row) * STRIDE +
                       (size_t)b->x * 4u,
                   (size_t)b->w * 4u);
        }
    }
}

static int prepare_expect(uint32_t* flags, uint32_t* count, uint64_t* seq)
{
    return ycn_dirty_pending_prepare(&g_pending, g_boxes, 64u, flags, count, seq);
}

static void test_attach_forces_full(void)
{
    uint32_t flags = 0u;
    uint32_t count = 0u;
    uint64_t seq = 0u;

    ycn_dirty_pending_init(&g_pending);
    fill_pattern(g_src, sizeof(g_src), 3u);

    ycn_dirty_pending_attach(&g_pending, TOKEN_A);

    /* attach 后立刻 prepare：没有 add_frame 也必须 FULL。 */
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_FULL);
    CHECK(count == 0u);

    memset(g_dst, 0xA5, sizeof(g_dst));
    copy_full(g_src, g_dst);
    CHECK(memcmp(g_dst, g_src, FRAME_BYTES) == 0);

    /* ack 只接受当前 token + 当前 sealed seq。 */
    CHECK(ycn_dirty_pending_ack(&g_pending, 99u, seq) != 0);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) == 0);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) != 0);

    /* ack 之后没有新帧：EMPTY。 */
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_EMPTY);
    CHECK(count == 0u);

    /* attach 后第一次 add_frame 要建立尺寸，按设计也是 FULL；
     * ack 之后 dimension 已知，后续才是增量 BOXES。 */
    ycn_dirty_pending_add_frame(&g_pending, (int32_t)W, (int32_t)H, 1u, NULL, 0u);
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_FULL);
    CHECK(count == 0u);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) == 0);

    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_EMPTY);
}

static void test_boxes_only_then_sealed_freeze(void)
{
    YcnDirtySnapshotRect r1 = {0, 0, 64, 64};
    YcnDirtySnapshotRect r2 = {128, 64, 64, 64};
    YcnDirtyPendingRect first_box;
    uint32_t flags = 0u;
    uint32_t count = 0u;
    uint64_t seq = 0u;
    uint32_t flags2 = 0u;
    uint32_t count2 = 0u;
    uint64_t seq2 = 0u;

    /* 接上上一个测试的 CLEAN 状态；先制造一个 sealed DIRTY 批次。 */
    ycn_dirty_pending_add_frame(&g_pending, (int32_t)W, (int32_t)H, 0u, &r1, 1u);
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_DIRTY);
    CHECK(count == 1u);
    CHECK(g_boxes[0].x == 0 && g_boxes[0].y == 0);
    CHECK(g_boxes[0].w == 64 && g_boxes[0].h == 64);
    first_box = g_boxes[0];

    /* BOXES 只写 sealed 矩形，其它字节保持哨兵。 */
    memset(g_dst, 0xA5, sizeof(g_dst));
    copy_boxes(g_src, g_dst, g_boxes, count);
    memset(g_expected, 0xA5, sizeof(g_expected));
    copy_boxes(g_src, g_expected, g_boxes, count);
    CHECK(memcmp(g_dst, g_expected, FRAME_BYTES) == 0);
    CHECK(g_dst[(size_t)(H - 1u) * STRIDE + (W - 1u) * 4u] == 0xA5);
    CHECK(g_dst[(size_t)(H - 1u) * STRIDE + (W - 1u) * 4u] !=
          g_src[(size_t)(H - 1u) * STRIDE + (W - 1u) * 4u]);

    /* sealed 期间的新 pending 不能并入已封批次；再次 prepare 返回同一批。 */
    ycn_dirty_pending_add_frame(&g_pending, (int32_t)W, (int32_t)H, 0u, &r2, 1u);
    CHECK(prepare_expect(&flags2, &count2, &seq2) == 0);
    CHECK(flags2 == YCN_DIRTY_PENDING_DIRTY);
    CHECK(count2 == 1u);
    CHECK(seq2 == seq);
    CHECK(memcmp(&g_boxes[0], &first_box, sizeof(first_box)) == 0);

    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) == 0);

    /* ack 后下一批才是 r2。 */
    CHECK(prepare_expect(&flags2, &count2, &seq2) == 0);
    CHECK(flags2 == YCN_DIRTY_PENDING_DIRTY);
    CHECK(count2 == 1u);
    CHECK(seq2 != seq);
    CHECK(g_boxes[0].x == 128 && g_boxes[0].y == 64);
    CHECK(g_boxes[0].w == 64 && g_boxes[0].h == 64);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq2) == 0);
}

static void test_merge_and_tile_alignment(void)
{
    YcnDirtySnapshotRect pair[2] = {{0, 0, 64, 64}, {64, 0, 64, 64}};
    YcnDirtySnapshotRect small = {70, 70, 10, 10};
    YcnDirtySnapshotRect clipped = {190, 0, 64, 64}; /* x+w > W -> full */
    uint32_t flags = 0u;
    uint32_t count = 0u;
    uint64_t seq = 0u;

    /* 同一 tile 行内相邻两 tile 合并成一个横向 run。 */
    ycn_dirty_pending_add_frame(&g_pending, (int32_t)W, (int32_t)H, 0u, pair, 2u);
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_DIRTY);
    CHECK(count == 1u);
    CHECK(g_boxes[0].x == 0 && g_boxes[0].y == 0);
    CHECK(g_boxes[0].w == 128 && g_boxes[0].h == 64);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) == 0);

    /* 非 tile 对齐的小矩形按 tile 对齐返回。 */
    ycn_dirty_pending_add_frame(&g_pending, (int32_t)W, (int32_t)H, 0u, &small, 1u);
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_DIRTY);
    CHECK(count == 1u);
    CHECK(g_boxes[0].x == 64 && g_boxes[0].y == 64);
    CHECK(g_boxes[0].w == 64 && g_boxes[0].h == 64);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) == 0);

    /* 越界矩形：保守降级为 FULL。 */
    ycn_dirty_pending_add_frame(&g_pending, (int32_t)W, (int32_t)H, 0u, &clipped, 1u);
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_FULL);
    CHECK(count == 0u);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) == 0);
}

static void test_resize_and_reset_force_full(void)
{
    uint32_t flags = 0u;
    uint32_t count = 0u;
    uint64_t seq = 0u;

    /* 尺寸变化：即使只给 partial 也强制 FULL。 */
    ycn_dirty_pending_add_frame(&g_pending, 256, 192, 0u, NULL, 0u);
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_FULL);
    CHECK(count == 0u);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) == 0);

    /* reset_stream：下一批强制 FULL。 */
    ycn_dirty_pending_reset_stream(&g_pending);
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_FULL);
    CHECK(count == 0u);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) == 0);

    /* 再空一次确认 reset 不会留下永久的 pending。 */
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_EMPTY);
}

static void test_detach_and_reattach(void)
{
    uint32_t flags = 0u;
    uint32_t count = 0u;
    uint64_t seq = 0u;

    CHECK(ycn_dirty_pending_detach(&g_pending, 77u) != 0);
    CHECK(ycn_dirty_pending_detach(&g_pending, TOKEN_A) == 0);
    CHECK(prepare_expect(&flags, &count, &seq) != 0);

    /* 重新 attach（新 token）仍然强制 FULL。 */
    ycn_dirty_pending_attach(&g_pending, TOKEN_B);
    CHECK(prepare_expect(&flags, &count, &seq) == 0);
    CHECK(flags == YCN_DIRTY_PENDING_FULL);
    CHECK(count == 0u);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_A, seq) != 0);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_B, seq) == 0);

    /* detach 后旧 token 的 ack 必须失败。 */
    CHECK(ycn_dirty_pending_detach(&g_pending, TOKEN_B) == 0);
    CHECK(ycn_dirty_pending_ack(&g_pending, TOKEN_B, seq) != 0);
}

int main(void)
{
    fill_pattern(g_src, sizeof(g_src), 9u);

    test_attach_forces_full();
    test_boxes_only_then_sealed_freeze();
    test_merge_and_tile_alignment();
    test_resize_and_reset_force_full();
    test_detach_and_reattach();

    if (g_failures != 0)
    {
        fprintf(stderr, "dirty pending session tests: %d failure(s)\n", g_failures);
        return 1;
    }
    puts("dirty pending session/ABI ok");
    return 0;
}
