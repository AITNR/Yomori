"""阶梯测量：VideoProcessorBlt 的转换成本跟输入尺寸、输出矩形，还是固定开销走。

复用 ycn_rdp.dll 的两个 bench 导出（只量转换，不含「落到目标面」的复制），
单进程内重复多轮取中位数。输出 markdown 表格，直接进报告。

用法：
    python tests/bench_ladder.py <ycn_rdp.dll> [每轮迭代次数]
"""
import ctypes
import statistics
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_decode_checks as N

R = N.DirectPresentResult


def main() -> int:
    dll = Path(sys.argv[1]).resolve()
    iters = int(sys.argv[2]) if len(sys.argv) > 2 else 400
    rounds = 3
    lib = N._load_dll(dll)
    # 全局 D3D11 设备由 color_test 首次使用时创建；不先跑一次，bench 直接
    # 返回 YCN_ERR_INTERNAL(-23)。
    N.check_direct_color(lib)
    full = lib.ycn_rdp_direct_bench
    full.restype = ctypes.c_int
    full.argtypes = [ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32, ctypes.POINTER(R)]
    rect = lib.ycn_rdp_direct_bench_rect
    rect.restype = ctypes.c_int
    rect.argtypes = [ctypes.c_uint32] * 5 + [ctypes.POINTER(R)]

    def one(w, h, rw, rh):
        vals = []
        for _ in range(rounds):
            out = R()
            out.size = ctypes.sizeof(R)
            if rw:
                rc = int(rect(w, h, rw, rh, iters, ctypes.byref(out)))
            else:
                rc = int(full(w, h, iters, ctypes.byref(out)))
            if rc != 0 or out.status != 0:
                raise SystemExit(f"bench failed rc={rc} status={out.status}")
            vals.append(int(out.reserved[3]))
        return statistics.median(vals) / 1000.0

    cases = [(1920, 1080), (1280, 720), (960, 540), (480, 270), (128, 128)]
    rects = [None, (960, 540), (256, 144), (64, 64)]
    print(f"每轮 {iters} 次 Blt，{rounds} 轮取中位数；单位 µs/帧，只含 VideoProcessorBlt。")
    print()
    print("| 输入帧 | " + " | ".join("整幅" if r is None else f"{r[0]}×{r[1]} 矩形" for r in rects) + " |")
    print("|" + "---|" * (len(rects) + 1))
    for w, h in cases:
        cells = []
        for r in rects:
            rw, rh = (0, 0) if r is None else (min(r[0], w), min(r[1], h))
            cells.append(f"{one(w, h, rw, rh):.1f}")
        print(f"| {w}×{h} | " + " | ".join(cells) + " |")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
