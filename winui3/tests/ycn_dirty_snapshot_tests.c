#include "ycn_dirty_snapshot.h"

#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static int g_failures = 0;

#define CHECK(expr)                                                                  \
    do                                                                               \
    {                                                                                \
        if (!(expr))                                                                 \
        {                                                                            \
            fprintf(stderr, "%s:%d: CHECK failed: %s\n", __FILE__, __LINE__, #expr); \
            g_failures++;                                                            \
        }                                                                            \
    } while (0)

static void fill_pattern(uint8_t* data, size_t size, uint8_t seed)
{
    size_t i;
    for (i = 0; i < size; i++)
        data[i] = (uint8_t)(seed + i * 17u);
}

static void test_boundaries(void)
{
    YcnDirtySnapshot snapshot;
    YcnDirtySnapshot invalid;
    uint8_t src[10u * 8u * 4u];
    uint8_t dst[10u * 8u * 4u];
    uint8_t expected[10u * 8u * 4u];

    ycn_dirty_snapshot_init(&snapshot, 10, 8, 0u);
    ycn_dirty_snapshot_add_rect(&snapshot, 0, 0, 3, 2);
    ycn_dirty_snapshot_add_rect(&snapshot, 7, 6, 3, 2); /* right/bottom edge */
    ycn_dirty_snapshot_finish(&snapshot);

    CHECK(snapshot.full_required == 0u);
    CHECK(snapshot.count == 2u);
    CHECK(snapshot.merged == 0u);
    CHECK(snapshot.copy_area == 12u);

    fill_pattern(src, sizeof(src), 3u);
    memset(dst, 0xA5, sizeof(dst));
    memcpy(expected, dst, sizeof(expected));
    memcpy(expected + 0u * 40u + 0u * 4u, src + 0u * 40u + 0u * 4u, 3u * 4u);
    memcpy(expected + 1u * 40u + 0u * 4u, src + 1u * 40u + 0u * 4u, 3u * 4u);
    memcpy(expected + 6u * 40u + 7u * 4u, src + 6u * 40u + 7u * 4u, 3u * 4u);
    memcpy(expected + 7u * 40u + 7u * 4u, src + 7u * 40u + 7u * 4u, 3u * 4u);

    CHECK(ycn_dirty_snapshot_copy_rects(&snapshot, src, sizeof(src), 40u,
                                        dst, sizeof(dst), 40u) == 0);
    CHECK(memcmp(dst, expected, sizeof(dst)) == 0);

    ycn_dirty_snapshot_init(&invalid, 10, 8, 0u);
    ycn_dirty_snapshot_add_rect(&invalid, 9, 0, 2, 1);
    ycn_dirty_snapshot_finish(&invalid);
    CHECK(invalid.full_required == 1u);
    CHECK(invalid.invalid_rects == 1u);

    ycn_dirty_snapshot_init(&invalid, 10, 8, 0u);
    ycn_dirty_snapshot_add_rect(&invalid, 0, 0, 0, 1);
    ycn_dirty_snapshot_finish(&invalid);
    CHECK(invalid.full_required == 1u);

    ycn_dirty_snapshot_init(&invalid, 10, 8, 0u);
    ycn_dirty_snapshot_add_rect(&invalid, -1, 0, 1, 1);
    ycn_dirty_snapshot_finish(&invalid);
    CHECK(invalid.full_required == 1u);

    ycn_dirty_snapshot_init(&invalid, 10, 8, 0u);
    ycn_dirty_snapshot_add_rect(&invalid, INT32_MAX - 1, 0, 2, 1);
    ycn_dirty_snapshot_finish(&invalid);
    CHECK(invalid.full_required == 1u);
}

static void test_merge_and_coverage(void)
{
    YcnDirtySnapshot snapshot;
    uint8_t src[8u * 8u * 4u];
    uint8_t dst[8u * 8u * 4u];
    uint8_t expected[8u * 8u * 4u];
    uint32_t row;

    ycn_dirty_snapshot_init(&snapshot, 8, 8, 0u);
    ycn_dirty_snapshot_add_rect(&snapshot, 0, 0, 3, 3);
    ycn_dirty_snapshot_add_rect(&snapshot, 1, 1, 3, 3);
    ycn_dirty_snapshot_finish(&snapshot);

    CHECK(snapshot.full_required == 0u);
    CHECK(snapshot.merged == 1u);
    CHECK(snapshot.count == 1u);
    CHECK(snapshot.copy_area == 16u);
    CHECK(snapshot.rects[0].x == 0 && snapshot.rects[0].y == 0);
    CHECK(snapshot.rects[0].w == 4 && snapshot.rects[0].h == 4);

    fill_pattern(src, sizeof(src), 9u);
    memset(dst, 0x5A, sizeof(dst));
    memcpy(expected, dst, sizeof(expected));
    for (row = 0u; row < 4u; row++)
        memcpy(expected + row * 32u, src + row * 32u, 16u);
    CHECK(ycn_dirty_snapshot_copy_rects(&snapshot, src, sizeof(src), 32u,
                                        dst, sizeof(dst), 32u) == 0);
    CHECK(memcmp(dst, expected, sizeof(dst)) == 0);
}

static void test_overlimit(void)
{
    YcnDirtySnapshot snapshot;
    uint32_t i;

    ycn_dirty_snapshot_init(&snapshot, 4096, 1, 0u);
    for (i = 0u; i < YCN_DIRTY_SNAPSHOT_MAX_RECTS; i++)
        ycn_dirty_snapshot_add_rect(&snapshot, (int32_t)i, 0, 1, 1);
    ycn_dirty_snapshot_finish(&snapshot);
    CHECK(snapshot.full_required == 0u);
    CHECK(snapshot.overflowed == 0u);
    CHECK(snapshot.count <= YCN_DIRTY_SNAPSHOT_MAX_RECTS);
    CHECK(snapshot.copy_area == YCN_DIRTY_SNAPSHOT_MAX_RECTS);

    ycn_dirty_snapshot_init(&snapshot, 4097, 1, 0u);
    for (i = 0u; i <= YCN_DIRTY_SNAPSHOT_MAX_RECTS; i++)
        ycn_dirty_snapshot_add_rect(&snapshot, (int32_t)i, 0, 1, 1);
    ycn_dirty_snapshot_finish(&snapshot);
    CHECK(snapshot.full_required == 1u);
    CHECK(snapshot.overflowed == 1u);
}

static void test_full_fallbacks(void)
{
    YcnDirtySnapshot snapshot;

    ycn_dirty_snapshot_init(&snapshot, 10, 8, 1u);
    ycn_dirty_snapshot_finish(&snapshot);
    CHECK(snapshot.full_required == 1u);

    ycn_dirty_snapshot_init(&snapshot, 10, 8, 0u);
    ycn_dirty_snapshot_finish(&snapshot);
    CHECK(snapshot.full_required == 1u);

    ycn_dirty_snapshot_init(&snapshot, 0, 8, 0u);
    ycn_dirty_snapshot_finish(&snapshot);
    CHECK(snapshot.full_required == 1u);

    ycn_dirty_snapshot_init(&snapshot, 10, 8, 0u);
    snapshot.sum_area = UINT64_MAX - 1u;
    ycn_dirty_snapshot_add_rect(&snapshot, 0, 0, 2, 2);
    ycn_dirty_snapshot_finish(&snapshot);
    CHECK(snapshot.full_required == 1u);
    CHECK(snapshot.overflowed == 1u);

    ycn_dirty_snapshot_init(&snapshot, 10, 8, 0u);
    ycn_dirty_snapshot_add_rect(&snapshot, 0, 0, 10, 8);
    ycn_dirty_snapshot_finish(&snapshot);
    CHECK(snapshot.full_required == 1u);

    ycn_dirty_snapshot_init(&snapshot, 10, 8, 0u);
    ycn_dirty_snapshot_add_rect(&snapshot, 0, 0, 2, 2);
    ycn_dirty_snapshot_finish(&snapshot);
    CHECK(ycn_dirty_snapshot_copy_rects(&snapshot, NULL, 0u, 40u,
                                        NULL, 0u, 40u) == -1);
}

int main(void)
{
    test_boundaries();
    test_merge_and_coverage();
    test_overlimit();
    test_full_fallbacks();

    if (g_failures != 0)
    {
        fprintf(stderr, "ycn_dirty_snapshot_tests: %d failure(s)\n", g_failures);
        return 1;
    }

    printf("ycn_dirty_snapshot_tests: ok\n");
    return 0;
}
