#!/usr/bin/env python3
"""Offline probe for the production FreeRDP AVC444/AVC444v2 decoder.

The required check decodes a real Annex-B H.264 IDR through
``avc444_decompress(..., op=1, ...)`` for both codec ids 14/15, once with
software policy 2 and once with DXVA2 policy 1.  A pass requires valid BGRA
output and a matching increase in the production diagnostics counters.

``--full`` additionally builds a real dual-stream op=0 fixture in-process:
FreeRDP's ``yuv444_context_encode`` splits a deterministic BGRA image into the
luma and chroma YUV420 streams, and a local FFmpeg with libopenh264 encodes
each stream independently.  The decoded image is then compared with the source
so that merely decoding two unrelated H.264 packets cannot pass the check.

Only generated artifacts under ``winui3/tests/build/avc444`` are written.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import h264_decode_probe as base  # noqa: E402  (reuse fixture/load/get_diag)

ROOT = HERE / "build" / "avc444"
BGRA32 = base.BGRA32
POLICY_HW = base.POLICY_HW
POLICY_SW = base.POLICY_SW
CODECS = {14: "avc444-v1", 15: "avc444-v2"}
BEGIN, END = "AVC444_PROBE_JSON_BEGIN", "AVC444_PROBE_JSON_END"
THREADING_FLAGS_DISABLE_THREADS = 0x00000001
FFMPEG_CANDIDATES = (
    Path(r"C:\Program Files\Krita (x64)\bin\ffmpeg.exe"),
    Path(r"C:\Program Files\AI-Girlfriend-Desktop-Pet2.1\sensvoice\ffmpeg\bin\ffmpeg.exe"),
)


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def command(args: list[str], *, data: bytes | None = None, env: dict[str, str] | None = None,
            timeout: float = 60.0) -> tuple[int, bytes, bytes]:
    proc = subprocess.run(args, input=data, capture_output=True, timeout=timeout, env=env,
                          check=False)
    return proc.returncode, proc.stdout, proc.stderr


def image_stats(raw: bytes, width: int, height: int) -> dict[str, Any]:
    if len(raw) != width * height * 4:
        raise RuntimeError(f"BGRA size mismatch: got {len(raw)}, expected {width * height * 4}")
    colors: set[tuple[int, int, int]] = set()
    nonzero = 0
    lo = [255, 255, 255]
    hi = [0, 0, 0]
    sums = [0, 0, 0]
    for offset in range(0, len(raw), 4):
        b, g, r = raw[offset], raw[offset + 1], raw[offset + 2]
        colors.add((r, g, b))
        nonzero += bool(r or g or b)
        for index, value in enumerate((r, g, b)):
            lo[index] = min(lo[index], value)
            hi[index] = max(hi[index], value)
            sums[index] += value
    count = width * height
    ranges = [hi[index] - lo[index] for index in range(3)]
    return {
        "sha256": hashlib.sha256(raw).hexdigest(),
        "colors": len(colors),
        "nonzero": round(nonzero / count, 6),
        "ranges": ranges,
        "means": [round(value / count, 3) for value in sums],
        "valid": nonzero / count >= 0.5 and len(colors) >= 32 and
                 sum(value >= 32 for value in ranges) >= 2,
    }


def compare_bgra(actual: bytes, expected: bytes) -> dict[str, Any]:
    if len(actual) != len(expected):
        raise RuntimeError("decoded/source BGRA size mismatch")
    absolute = 0
    squared = 0
    samples = 0
    max_error = 0
    for offset in range(0, len(actual), 4):
        for channel in range(3):
            delta = abs(actual[offset + channel] - expected[offset + channel])
            absolute += delta
            squared += delta * delta
            max_error = max(max_error, delta)
            samples += 1
    mae = absolute / samples
    mse = squared / samples
    psnr = 99.0 if mse == 0 else 10.0 * math.log10((255.0 * 255.0) / mse)
    return {
        "psnr": round(psnr, 3),
        "mae": round(mae, 3),
        "maxChannelError": max_error,
        "match": psnr >= 20.0 and mae <= 12.0,
    }


def bind_avc444(api: dict[str, Any]) -> None:
    rect = api["Rect"]
    fn = api["lib"].avc444_decompress
    fn.argtypes = [
        ctypes.c_void_p, ctypes.c_ubyte, ctypes.POINTER(rect), ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_ubyte), ctypes.c_uint32,
        ctypes.POINTER(rect), ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_ubyte), ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_ubyte), ctypes.c_uint32,
        ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32,
    ]
    fn.restype = ctypes.c_int32
    api["avc444"] = fn


def bind_yuv_encoder(api: dict[str, Any]) -> None:
    rect = api["Rect"]
    byte_p = ctypes.POINTER(ctypes.c_ubyte)
    byte_p3 = byte_p * 3
    api["yuv_new"] = api["lib"].yuv_context_new
    api["yuv_new"].argtypes = [ctypes.c_int32, ctypes.c_uint32]
    api["yuv_new"].restype = ctypes.c_void_p
    api["yuv_free"] = api["lib"].yuv_context_free
    api["yuv_free"].argtypes = [ctypes.c_void_p]
    api["yuv_free"].restype = None
    api["yuv_reset"] = api["lib"].yuv_context_reset
    api["yuv_reset"].argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32]
    api["yuv_reset"].restype = ctypes.c_int32
    api["yuv_encode"] = api["lib"].yuv444_context_encode
    api["yuv_encode"].argtypes = [
        ctypes.c_void_p, ctypes.c_ubyte, byte_p, ctypes.c_uint32, ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_uint32), byte_p3, byte_p3, ctypes.POINTER(rect),
        ctypes.c_uint32,
    ]
    api["yuv_encode"].restype = ctypes.c_int32


def decode_packet(api: dict[str, Any], ctx: int, op: int, main: bytes, aux: bytes | None,
                  width: int, height: int, codec_id: int) -> tuple[int, bytes, dict[str, Any]]:
    main_buf = (ctypes.c_ubyte * len(main)).from_buffer_copy(main)
    aux_buf = (ctypes.c_ubyte * len(aux)).from_buffer_copy(aux) if aux is not None else None
    dst = (ctypes.c_ubyte * (width * height * 4))()
    rect = api["Rect"](0, 0, width, height)
    rc = api["avc444"](
        ctx, op, ctypes.byref(rect), 1, main_buf, len(main),
        ctypes.byref(rect) if aux is not None else None, 1 if aux is not None else 0,
        aux_buf, len(aux) if aux is not None else 0,
        dst, BGRA32, width * 4, width, height, codec_id,
    )
    raw = bytes(dst)
    return int(rc), raw, image_stats(raw, width, height)


def worker_main(args: argparse.Namespace) -> int:
    dll = args.dll.expanduser().resolve()
    main = args.main.read_bytes()
    aux = args.aux.read_bytes() if args.aux else None
    if args.op == 0 and aux is None:
        raise RuntimeError("op=0 requires --aux")
    api: dict[str, Any] | None = None
    ctx: int | None = None
    started = time.perf_counter()
    output: dict[str, Any] = {
        "pid": os.getpid(), "dll": str(dll), "policy": args.policy, "codecId": args.codec_id,
        "op": args.op, "width": args.width, "height": args.height,
        "mainBytes": len(main), "auxBytes": len(aux) if aux else 0,
        "backendEnv": os.environ.get("FREERDP_HWCTX_TYPE"),
    }
    try:
        api = base.load(dll)
        bind_avc444(api)
        ctx = api["new_ex"](0, args.policy) if api["newApi"] else api["new"](0)
        if not ctx:
            raise RuntimeError("context creation returned NULL")
        output.update(newApi=bool(api["newApi"]), apiVersion=api["apiVersion"],
                      capabilities=api["capabilities"])
        output["reset"] = int(api["reset"](ctx, args.width, args.height))
        if not output["reset"]:
            raise RuntimeError("h264_context_reset failed")
        before = output["diagBefore"] = base.get_diag(api, ctx)
        decoded = output["decoded"] = []
        last_raw = b""
        for _ in range(args.frames):
            rc, last_raw, stats = decode_packet(api, ctx, args.op, main, aux, args.width,
                                                args.height, args.codec_id)
            decoded.append({"rc": rc, **stats})
        output["diagAfter"] = base.get_diag(api, ctx)
        if args.output_bgra:
            if not decoded:
                raise RuntimeError("no decoded frame to export")
            args.output_bgra.write_bytes(last_raw)
            output["outputBgra"] = str(args.output_bgra)
    except Exception as exc:
        output["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        if ctx and api:
            try:
                api["free"](ctx)
            except Exception as exc:
                output["freeError"] = f"{type(exc).__name__}: {exc}"
        if api:
            api["dir"].close()
        output["elapsedMs"] = round((time.perf_counter() - started) * 1000, 3)
        print(BEGIN, flush=True)
        print(json.dumps(output, sort_keys=True), flush=True)
        print(END, flush=True)
    return 0


def extract(text: str) -> dict[str, Any] | None:
    match = re.search(rf"{BEGIN}\s*(\{{.*?\}})\s*{END}", text, re.S)
    return json.loads(match.group(1)) if match else None


def launch(dll: Path, main: Path, aux: Path | None, width: int, height: int, policy: int,
           codec_id: int, op: int, label: str, output_bgra: Path | None = None) -> dict[str, Any]:
    argv = [sys.executable, str(Path(__file__).resolve()), "--_worker", "--dll", str(dll),
            "--main", str(main), "--width", str(width), "--height", str(height),
            "--policy", str(policy), "--codec-id", str(codec_id), "--op", str(op),
            "--frames", "2"]
    if aux is not None:
        argv.extend(["--aux", str(aux)])
    if output_bgra is not None:
        argv.extend(["--output-bgra", str(output_bgra)])
    env = os.environ.copy()
    env.update(WLOG_LEVEL="DEBUG", WLOG_APPENDER="CONSOLE", PYTHONIOENCODING="utf-8")
    if policy == POLICY_HW:
        env["FREERDP_HWCTX_TYPE"] = "dxva2"
    else:
        env.pop("FREERDP_HWCTX_TYPE", None)
    rc, stdout, stderr = command(argv, env=env, timeout=90.0)
    text = f"--- STDOUT ---\n{stdout.decode('utf-8', 'replace')}\n" \
           f"--- STDERR ---\n{stderr.decode('utf-8', 'replace')}"
    log = ROOT / f"{label}-{width}x{height}.log"
    write(log, text)
    return {"processRc": rc, "worker": extract(text), "log": str(log)}


def delta(after: dict[str, Any] | None, before: dict[str, Any] | None, key: str) -> int:
    if not after or not before:
        return 0
    return int(after.get(key, 0)) - int(before.get(key, 0))


def classify(record: dict[str, Any], policy: int, frames: int = 2) -> dict[str, Any]:
    worker = record.get("worker")
    if not worker:
        return {"ok": False, "reason": "worker emitted no JSON result", "checks": {}}
    if worker.get("error"):
        return {"ok": False, "reason": worker["error"], "checks": {}}
    decoded = worker.get("decoded") or []
    before = worker.get("diagBefore") or {}
    after = worker.get("diagAfter") or {}
    frame_delta = delta(after, before, "frames")
    hw_delta = delta(after, before, "hwFrames")
    sw_delta = delta(after, before, "swFrames")
    failure_delta = delta(after, before, "failures")
    output_ok = len(decoded) == frames and all(item.get("rc") == 0 and item.get("valid")
                                               for item in decoded)
    common = bool(worker.get("reset") == 1 and output_ok and frame_delta == frames and
                  failure_delta == 0 and int(after.get("lastError", -1)) == 0)
    if policy == POLICY_SW:
        policy_ok = bool(int(before.get("policy", -1)) == POLICY_SW and
                         int(before.get("hardwareRequested", -1)) == 0 and
                         after.get("backendName") == "software" and
                         hw_delta == 0 and sw_delta == frames)
    else:
        policy_ok = bool(int(before.get("policy", -1)) == POLICY_HW and
                         int(after.get("hardwareInitialized", 0)) == 1 and
                         after.get("activeBackend") == 2 and
                         after.get("backendName") == "dxva2" and
                         hw_delta == frames and sw_delta == 0 and
                         int(after.get("recoveryAttempts", -1)) == 0)
    checks = {
        "output": output_ok,
        "policyBackend": policy_ok,
        "successfulFrames": frame_delta == frames,
        "successfulFramesDelta": frame_delta,
        "hardwareFrames": hw_delta,
        "softwareFrames": sw_delta,
        "failureDelta": failure_delta,
        "lastError": after.get("lastError"),
        "errorPhase": after.get("errorPhase"),
    }
    return {"ok": common and policy_ok, "reason": "valid decode and expected diagnostics"
            if common and policy_ok else "decode or diagnostic invariant failed",
            "checks": checks, "diagAfter": after}


def annex_b_nal_types(data: bytes) -> list[int]:
    starts = [match.start() for match in re.finditer(b"\x00\x00(?:\x00)?\x01", data)]
    types: list[int] = []
    for index, start in enumerate(starts):
        prefix = 4 if data[start:start + 4] == b"\x00\x00\x00\x01" else 3
        end = starts[index + 1] if index + 1 < len(starts) else len(data)
        if start + prefix < end:
            types.append(data[start + prefix] & 0x1F)
    return types


def source_image(width: int, height: int) -> bytes:
    result = bytearray(width * height * 4)
    for y in range(height):
        for x in range(width):
            offset = (y * width + x) * 4
            quadrant = (1 if x >= width // 2 else 0) + (2 if y >= height // 2 else 0)
            base_rgb = ((230, 30, 30), (30, 220, 40), (30, 50, 230), (230, 210, 30))[quadrant]
            r = max(0, min(255, base_rgb[0] - (x * 70) // width + (y * 30) // height))
            g = max(0, min(255, base_rgb[1] - (y * 60) // height + (x * 20) // width))
            b = max(0, min(255, base_rgb[2] + (x * 50) // width - (y * 20) // height))
            result[offset:offset + 4] = bytes((b, g, r, 255))
    return bytes(result)


def split_bgra(api: dict[str, Any], source: bytes, width: int, height: int,
               version: int) -> tuple[bytes, bytes, dict[str, Any]]:
    bind_yuv_encoder(api)
    context = api["yuv_new"](1, THREADING_FLAGS_DISABLE_THREADS)
    if not context:
        raise RuntimeError("yuv_context_new(encoder=TRUE) returned NULL")
    main_buffers: list[Any] = []
    aux_buffers: list[Any] = []
    try:
        if not api["yuv_reset"](context, width, height):
            raise RuntimeError("yuv_context_reset failed")
        stride0 = width
        strides = (ctypes.c_uint32 * 3)(stride0, (stride0 + 1) // 2, (stride0 + 1) // 2)
        plane_size = stride0 * height
        for _ in range(3):
            main_buffers.append((ctypes.c_ubyte * plane_size)())
            aux_buffers.append((ctypes.c_ubyte * plane_size)())
        main_ptrs = (ctypes.POINTER(ctypes.c_ubyte) * 3)(
            *[ctypes.cast(item, ctypes.POINTER(ctypes.c_ubyte)) for item in main_buffers])
        aux_ptrs = (ctypes.POINTER(ctypes.c_ubyte) * 3)(
            *[ctypes.cast(item, ctypes.POINTER(ctypes.c_ubyte)) for item in aux_buffers])
        src = (ctypes.c_ubyte * len(source)).from_buffer_copy(source)
        rect = api["Rect"](0, 0, width, height)
        if not api["yuv_encode"](context, version, src, width * 4, BGRA32, strides,
                                 main_ptrs, aux_ptrs, ctypes.byref(rect), 1):
            raise RuntimeError(f"yuv444_context_encode(version={version}) failed")

        def pack_planes(buffers: list[Any]) -> bytes:
            y_size = width * height
            c_width = (width + 1) // 2
            c_size = c_width * ((height + 1) // 2)
            return (ctypes.string_at(buffers[0], y_size) +
                    ctypes.string_at(buffers[1], c_size) +
                    ctypes.string_at(buffers[2], c_size))
        main, aux = pack_planes(main_buffers), pack_planes(aux_buffers)
        details = {"version": version, "mainSha256": hashlib.sha256(main).hexdigest(),
                   "auxSha256": hashlib.sha256(aux).hexdigest(),
                   "mainBytes": len(main), "auxBytes": len(aux)}
        return main, aux, details
    finally:
        api["yuv_free"](context)


def find_ffmpeg(explicit: Path | None) -> Path | None:
    candidates = ([explicit] if explicit else []) + list(FFMPEG_CANDIDATES)
    for candidate in candidates:
        if not candidate or not candidate.is_file():
            continue
        rc, stdout, stderr = command([str(candidate), "-hide_banner", "-encoders"], timeout=20.0)
        if rc == 0 and b"libopenh264" in stdout:
            return candidate
    return None


def encode_h264(ffmpeg: Path, yuv: bytes, width: int, height: int, path: Path) -> dict[str, Any]:
    if len(yuv) != width * height * 3 // 2:
        raise RuntimeError(f"YUV420 size mismatch: got {len(yuv)}")
    args = [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-f", "rawvideo",
            "-pix_fmt", "yuv420p", "-s", f"{width}x{height}", "-framerate", "30",
            "-i", "pipe:0", "-frames:v", "1", "-an", "-c:v", "libopenh264",
            "-b:v", "6M", "-g", "1", "-bf", "0", "-f", "h264", "-y", str(path)]
    rc, stdout, stderr = command(args, data=yuv, timeout=60.0)
    if rc != 0:
        raise RuntimeError(f"ffmpeg/libopenh264 failed ({rc}): "
                           f"{stderr.decode('utf-8', 'replace').strip()}")
    data = path.read_bytes()
    types = annex_b_nal_types(data)
    if not {7, 8, 5}.issubset(types):
        raise RuntimeError(f"encoded stream lacks SPS/PPS/IDR: {types}")
    return {"path": str(path), "bytes": len(data), "nals": types,
            "sha256": hashlib.sha256(data).hexdigest()}


def run_case(dll: Path, main: Path, aux: Path | None, width: int, height: int, policy: int,
             codec_id: int, op: int, label: str, expected: bytes | None = None) -> dict[str, Any]:
    output_path = ROOT / f"{label}-decoded.bgra" if expected is not None else None
    record = launch(dll, main, aux, width, height, policy, codec_id, op, label, output_path)
    result = classify(record, policy)
    if expected is not None:
        compare = {"match": False, "error": "decoded BGRA was not exported"}
        if output_path and output_path.is_file():
            try:
                compare = compare_bgra(output_path.read_bytes(), expected)
            except Exception as exc:
                compare = {"match": False, "error": f"{type(exc).__name__}: {exc}"}
        record["compare"] = compare
        result["ok"] = bool(result["ok"] and compare.get("match"))
        if not compare.get("match"):
            result["reason"] += "; source-image comparison failed"
    record["cls"] = result
    return record


def full_fixture(dll: Path, ffmpeg: Path, width: int, height: int) -> dict[str, Any]:
    ROOT.mkdir(parents=True, exist_ok=True)
    source = source_image(width, height)
    source_path = ROOT / f"source-{width}x{height}.bgra"
    source_path.write_bytes(source)
    api: dict[str, Any] | None = None
    try:
        api = base.load(dll)
        bind_avc444(api)
        fixtures = []
        for version, codec_id in ((1, 14), (2, 15)):
            main_yuv, aux_yuv, split = split_bgra(api, source, width, height, version)
            main_path = ROOT / f"main-v{version}-{width}x{height}.yuv420"
            aux_path = ROOT / f"aux-v{version}-{width}x{height}.yuv420"
            main_path.write_bytes(main_yuv)
            aux_path.write_bytes(aux_yuv)
            main_h264 = ROOT / f"main-v{version}-{width}x{height}.h264"
            aux_h264 = ROOT / f"aux-v{version}-{width}x{height}.h264"
            fixtures.append({
                "version": version, "codecId": codec_id, "split": split,
                "mainYuv": str(main_path), "auxYuv": str(aux_path),
                "main": encode_h264(ffmpeg, main_yuv, width, height, main_h264),
                "aux": encode_h264(ffmpeg, aux_yuv, width, height, aux_h264),
            })
        return {"source": str(source_path), "fixtures": fixtures}
    finally:
        if api:
            api["dir"].close()


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="FreeRDP AVC444/AVC444v2 offline decoder probe")
    p.add_argument("--dll", type=Path, default=base.DEFAULT_DLL)
    p.add_argument("--width", type=int, default=128)
    p.add_argument("--height", type=int, default=128)
    p.add_argument("--main", type=Path, help="existing Annex-B SPS/PPS/IDR fixture")
    p.add_argument("--aux", type=Path, help="optional dual-stream auxiliary H.264 fixture")
    p.add_argument("--output-bgra", type=Path, help=argparse.SUPPRESS)
    p.add_argument("--full", action="store_true", help="generate and test op=0 dual streams")
    p.add_argument("--ffmpeg", type=Path, help="FFmpeg executable providing libopenh264")
    p.add_argument("--_worker", action="store_true", help=argparse.SUPPRESS)
    p.add_argument("--policy", type=int, choices=(POLICY_HW, POLICY_SW), default=POLICY_SW,
                   help=argparse.SUPPRESS)
    p.add_argument("--codec-id", type=int, choices=tuple(CODECS), default=14,
                   help=argparse.SUPPRESS)
    p.add_argument("--op", type=int, choices=(0, 1, 2), default=1, help=argparse.SUPPRESS)
    p.add_argument("--frames", type=int, default=2, help=argparse.SUPPRESS)
    return p


def main() -> int:
    args = parser().parse_args()
    if args._worker:
        return worker_main(args)
    try:
        if args.width < 16 or args.height < 16 or args.width % 2 or args.height % 2:
            raise RuntimeError("width/height must be even and >= 16")
        dll = args.dll.expanduser().resolve()
        if not dll.is_file():
            raise RuntimeError(f"DLL not found: {dll}")
        ROOT.mkdir(parents=True, exist_ok=True)
        fixture_args = argparse.Namespace(h264=args.main)
        main_path, main_data, nals = base.fixture(fixture_args)
        if args.width != 128 or args.height != 128:
            raise RuntimeError("the bundled H.264 IDR fixture is 128x128")
        print(f"OP1 fixture: {main_path} bytes={len(main_data)} NAL={nals}")
        print(f"DLL: {dll}")

        op1_runs = []
        for codec_id in (14, 15):
            for policy in (POLICY_SW, POLICY_HW):
                policy_name = "software-p2" if policy == POLICY_SW else "dxva2-p1"
                label = f"op1-{CODECS[codec_id]}-{policy_name}"
                record = launch(dll, main_path, None, args.width, args.height, policy,
                                codec_id, 1, label)
                record["label"] = label
                record["cls"] = classify(record, policy)
                op1_runs.append(record)

        for item in op1_runs:
            worker, cls = item.get("worker") or {}, item["cls"]
            decoded = worker.get("decoded") or []
            first = decoded[0] if decoded else {}
            print(f"[{item['label']}] {'PASS' if cls['ok'] else 'FAIL'}: {cls['reason']}")
            print(f"  output: rc={first.get('rc')} valid={first.get('valid')} "
                  f"colors={first.get('colors')} sha={str(first.get('sha256'))[:16]}")
            print(f"  diag: {json.dumps(cls.get('checks', {}), sort_keys=True)}")
            print(f"  log: {item['log']}")

        full: dict[str, Any] = {"attempted": False, "pass": False}
        if args.full:
            full["attempted"] = True
            ffmpeg = find_ffmpeg(args.ffmpeg)
            if not ffmpeg:
                full["error"] = "no local FFmpeg with libopenh264 was found"
            else:
                full["ffmpeg"] = str(ffmpeg)
                try:
                    generated = full_fixture(dll, ffmpeg, args.width, args.height)
                    full["generated"] = generated
                    source = Path(generated["source"]).read_bytes()
                    full_runs = []
                    for fixture in generated["fixtures"]:
                        for policy in (POLICY_SW, POLICY_HW):
                            policy_name = "software-p2" if policy == POLICY_SW else "dxva2-p1"
                            label = f"op0-{CODECS[fixture['codecId']]}-{policy_name}"
                            record = run_case(dll, Path(fixture["main"]["path"]),
                                              Path(fixture["aux"]["path"]), args.width,
                                              args.height, policy, fixture["codecId"], 0,
                                              label, source)
                            record["label"] = label
                            record["fixture"] = fixture
                            full_runs.append(record)

                    full["runs"] = full_runs
                    full["pass"] = bool(full_runs) and all(item["cls"]["ok"] for item in full_runs)
                except Exception as exc:
                    full["error"] = f"{type(exc).__name__}: {exc}"

            for item in full.get("runs", []):
                worker, cls = item.get("worker") or {}, item["cls"]
                decoded = worker.get("decoded") or []
                first = decoded[0] if decoded else {}
                print(f"[{item['label']}] {'PASS' if cls['ok'] else 'FAIL'}: {cls['reason']}")
                print(f"  output: rc={first.get('rc')} valid={first.get('valid')} "
                      f"colors={first.get('colors')} sha={str(first.get('sha256'))[:16]}")
                print(f"  compare: {json.dumps(item.get('compare', {}), sort_keys=True)}")
                print(f"  diag: {json.dumps(cls.get('checks', {}), sort_keys=True)}")
                print(f"  log: {item['log']}")
            if full.get("error"):
                print(f"[full op0] SKIP/FAIL: {full['error']}")

        op1_pass = all(item["cls"]["ok"] for item in op1_runs)
        summary = {"dll": str(dll), "op1": op1_runs, "op1Pass": op1_pass, "full": full}
        summary_path = ROOT / "last-run.json"
        write(summary_path, json.dumps(summary, indent=2, sort_keys=True) + "\n")
        print(f"FINAL: op1({'PASS' if op1_pass else 'FAIL'}) "
              f"full({'PASS' if full.get('pass') else 'NOT-PASS'})")
        print(f"SUMMARY: {summary_path}")
        if not op1_pass:
            return 1
        if args.full and not full.get("pass"):
            return 2
        return 0
    except Exception as exc:
        print(f"ERROR: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())


