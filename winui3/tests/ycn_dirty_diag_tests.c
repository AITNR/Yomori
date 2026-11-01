#include "ycn_dirty_diag.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>

static int g_failures;

#define CHECK(cond)                                                        \
    do                                                                     \
    {                                                                      \
        if (!(cond))                                                       \
        {                                                                  \
            fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
            g_failures++;                                                  \
        }                                                                  \
    } while (0)

static void test_clipping(void)
{
    YcnDirtyDiagBatch batch;
    YcnDirtyDiagAggregate aggregate;
    const YcnDirtyRect rect = { -10, -20, 30, 40 };

    memset(&aggregate, 0, sizeof(aggregate));
    ycn_dirty_diag_batch_init(&batch, 100, 100, 0u);
    ycn_dirty_diag_batch_add_rect(&batch, &rect);
    ycn_dirty_diag_commit(&aggregate, &batch);

    CHECK(batch.rect_count == 1u);
    CHECK(batch.valid_rects == 1u);
    CHECK(batch.clipped_rects == 1u);
    CHECK(batch.sum_area == 400u);
    CHECK(aggregate.paints == 1u);
    CHECK(aggregate.rect_count == 1u);
    CHECK(aggregate.sum_area == 400u);
    CHECK(aggregate.bbox_area_sum == 400u);
    CHECK(aggregate.clipped_rects == 1u);
    CHECK(aggregate.full_fallback_batches == 0u);
}

static void test_bbox_and_sum(void)
{
    YcnDirtyDiagBatch batch;
    YcnDirtyDiagAggregate aggregate;
    const YcnDirtyRect a = { 10, 10, 20, 20 };
    const YcnDirtyRect b = { 70, 50, 20, 20 };

    memset(&aggregate, 0, sizeof(aggregate));
    ycn_dirty_diag_batch_init(&batch, 100, 100, 0u);
    ycn_dirty_diag_batch_add_rect(&batch, &a);
    ycn_dirty_diag_batch_add_rect(&batch, &b);
    ycn_dirty_diag_commit(&aggregate, &batch);

    CHECK(batch.sum_area == 800u);
    CHECK(batch.bbox_left == 10);
    CHECK(batch.bbox_top == 10);
    CHECK(batch.bbox_right == 90);
    CHECK(batch.bbox_bottom == 70);
    CHECK(aggregate.sum_area == 800u);
    CHECK(aggregate.bbox_area_sum == 4800u);
    CHECK(aggregate.desktop_area_sum == 10000u);
    CHECK(ycn_dirty_diag_percent(aggregate.sum_area, aggregate.desktop_area_sum) == 8.0);
    CHECK(ycn_dirty_diag_percent(aggregate.bbox_area_sum, aggregate.desktop_area_sum) == 48.0);
    CHECK(aggregate.full_fallback_batches == 0u);
}

static void test_empty_and_full(void)
{
    YcnDirtyDiagBatch batch;
    YcnDirtyDiagAggregate empty_aggregate;
    YcnDirtyDiagAggregate full_aggregate;
    const YcnDirtyRect full = { 0, 0, 100, 100 };

    memset(&empty_aggregate, 0, sizeof(empty_aggregate));
    ycn_dirty_diag_batch_init(&batch, 100, 100, 1u);
    ycn_dirty_diag_commit(&empty_aggregate, &batch);
    CHECK(empty_aggregate.paints == 1u);
    CHECK(empty_aggregate.empty_batches == 1u);
    CHECK(empty_aggregate.full_fallback_batches == 1u);
    CHECK(empty_aggregate.sum_area == 0u);

    memset(&full_aggregate, 0, sizeof(full_aggregate));
    ycn_dirty_diag_batch_init(&batch, 100, 100, 0u);
    ycn_dirty_diag_batch_add_rect(&batch, &full);
    ycn_dirty_diag_commit(&full_aggregate, &batch);
    CHECK(full_aggregate.empty_batches == 0u);
    CHECK(full_aggregate.full_fallback_batches == 0u);
    CHECK(full_aggregate.sum_area == 10000u);
    CHECK(full_aggregate.bbox_area_sum == 10000u);
    CHECK(ycn_dirty_diag_percent(full_aggregate.sum_area,
                                 full_aggregate.desktop_area_sum) == 100.0);
}

static void test_out_of_bounds_and_zero_size(void)
{
    YcnDirtyDiagBatch batch;
    YcnDirtyDiagAggregate aggregate;
    const YcnDirtyRect outside = { -20, -20, 10, 10 };
    const YcnDirtyRect zero = { 1, 1, 0, 9 };

    memset(&aggregate, 0, sizeof(aggregate));
    ycn_dirty_diag_batch_init(&batch, 100, 100, 0u);
    ycn_dirty_diag_batch_add_rect(&batch, &outside);
    ycn_dirty_diag_batch_add_rect(&batch, &zero);
    ycn_dirty_diag_commit(&aggregate, &batch);

    CHECK(batch.rect_count == 2u);
    CHECK(batch.valid_rects == 0u);
    CHECK(batch.clipped_rects == 2u);
    CHECK(aggregate.sum_area == 0u);
    CHECK(aggregate.bbox_area_sum == 0u);
    CHECK(aggregate.full_fallback_batches == 1u);
}

static void test_rect_area_overflow(void)
{
    YcnDirtyDiagBatch batch;
    YcnDirtyDiagAggregate aggregate;
    const YcnDirtyRect a = { 0, 0, 1, 1 };
    const YcnDirtyRect b = { 1, 0, 1, 1 };

    memset(&aggregate, 0, sizeof(aggregate));
    ycn_dirty_diag_batch_init(&batch, 100, 100, 0u);
    batch.sum_area = UINT64_MAX - 1u;
    ycn_dirty_diag_batch_add_rect(&batch, &a);
    ycn_dirty_diag_batch_add_rect(&batch, &b);
    ycn_dirty_diag_commit(&aggregate, &batch);

    CHECK(batch.sum_area == UINT64_MAX);
    CHECK(batch.overflow_events == 1u);
    CHECK(aggregate.overflow_events == 1u);
    CHECK(aggregate.full_fallback_batches == 1u);
}

static void test_aggregate_overflow(void)
{
    YcnDirtyDiagBatch batch;
    YcnDirtyDiagAggregate aggregate;
    const YcnDirtyRect full = { 0, 0, 100, 100 };

    memset(&aggregate, 0, sizeof(aggregate));
    aggregate.desktop_area_sum = UINT64_MAX - 1u;
    ycn_dirty_diag_batch_init(&batch, 100, 100, 0u);
    ycn_dirty_diag_batch_add_rect(&batch, &full);
    ycn_dirty_diag_commit(&aggregate, &batch);

    CHECK(aggregate.overflow_events == 1u);
    CHECK(aggregate.full_fallback_batches == 1u);
    CHECK(aggregate.desktop_area_sum == UINT64_MAX);
}

static void test_bad_capacity_and_missing(void)
{
    YcnDirtyDiagBatch batch;
    YcnDirtyDiagAggregate aggregate;

    memset(&aggregate, 0, sizeof(aggregate));
    ycn_dirty_diag_batch_init(&batch, 100, 100, 0u);
    batch.bad_capacity = 1u;
    ycn_dirty_diag_commit(&aggregate, &batch);

    ycn_dirty_diag_batch_init(&batch, 100, 100, 0u);
    batch.list_missing = 1u;
    ycn_dirty_diag_commit(&aggregate, &batch);

    CHECK(aggregate.paints == 2u);
    CHECK(aggregate.bad_capacity_batches == 1u);
    CHECK(aggregate.missing_batches == 1u);
    CHECK(aggregate.full_fallback_batches == 2u);
    CHECK(aggregate.desktop_area_sum == 0u);
}

static void test_invalid_dimensions_and_percent(void)
{
    YcnDirtyDiagBatch batch;
    YcnDirtyDiagAggregate aggregate;

    memset(&aggregate, 0, sizeof(aggregate));
    ycn_dirty_diag_batch_init(&batch, 0, 100, 0u);
    ycn_dirty_diag_commit(&aggregate, &batch);

    CHECK(batch.desktop_area == 0u);
    CHECK(batch.overflow_events == 1u);
    CHECK(aggregate.full_fallback_batches == 1u);
    CHECK(ycn_dirty_diag_percent(25u, 200u) == 12.5);
    CHECK(ycn_dirty_diag_percent(1u, 0u) == 0.0);
}

int main(void)
{
    test_clipping();
    test_bbox_and_sum();
    test_empty_and_full();
    test_out_of_bounds_and_zero_size();
    test_rect_area_overflow();
    test_aggregate_overflow();
    test_bad_capacity_and_missing();
    test_invalid_dimensions_and_percent();

    if (g_failures != 0)
    {
        fprintf(stderr, "ycn_dirty_diag_tests: %d failure(s)\n", g_failures);
        return 1;
    }

    printf("ycn_dirty_diag_tests: ok\n");
    return 0;
}