#!/usr/bin/env python3
"""Lean FreeRDP AVC420 hardware-decode probe.

The software case uses new_ex(policy=2); hardware uses new_ex(policy=1) and
requires ycn_h264_get_diagnostics().successfulHardwareFrames to increase.
On a hardware runtime error, recover_software() is called and the same IDR is
retried once. That result is reported as software recovery, never as HW.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent / "build" / "h264-probe"
DEFAULT_DLL = Path(__file__).resolve().parents[1] / "src/Yomori.RdpNative/build/Release/freerdp3.dll"
BGRA32 = 0x20048888
HW_OPTION = 5
POLICY_HW = 1
POLICY_SW = 2
BACKENDS = {0: "unknown", 1: "software", 2: "dxva2", 3: "d3d11va", 4: "other-hw"}
FALLBACKS = {0: "none", 1: "policy-software", 2: "hw-unavailable", 3: "hw-init-failed", 4: "runtime-failed"}
BEGIN, END = "H264_PROBE_JSON_BEGIN", "H264_PROBE_JSON_END"


class Diagnostics(ctypes.Structure):
    _fields_ = [
        ("size", ctypes.c_uint32), ("version", ctypes.c_uint32),
        ("policy", ctypes.c_uint32), ("capabilities", ctypes.c_uint32),
        ("hardwareRequested", ctypes.c_uint32), ("hardwareInitialized", ctypes.c_uint32),
        ("activeBackend", ctypes.c_uint32), ("fallbackReason", ctypes.c_uint32),
        ("successfulFrames", ctypes.c_uint64), ("successfulHardwareFrames", ctypes.c_uint64),
        ("successfulSoftwareFrames", ctypes.c_uint64), ("decodeFailures", ctypes.c_uint64),
        ("lastError", ctypes.c_int32), ("recoveryAttempts", ctypes.c_uint32),
        ("recoveryPending", ctypes.c_uint32), ("reserved", ctypes.c_uint32),
        ("backendName", ctypes.c_char * 32), ("lastErrorPhase", ctypes.c_char * 32),
        ("reserved2", ctypes.c_uint32 * 2),
    ]


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def command(args: list[str], env: dict[str, str] | None = None, timeout: float = 45.0):
    p = subprocess.run(args, capture_output=True, timeout=timeout, env=env, check=False)
    return p.returncode, p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace")


def fixture(args: argparse.Namespace) -> tuple[Path, bytes, list[int]]:
    path = args.h264.expanduser().resolve() if args.h264 else Path(__file__).resolve().parent / "fixtures/h264-idr-128x128.h264"
    if not path.is_file():
        raise RuntimeError(f"fixture missing: {path}; pass --h264 (no ffmpeg generation required)")
    data = path.read_bytes()
    starts = [m.start() for m in re.finditer(b"\x00\x00(?:\x00)?\x01", data)]
    types = []
    for i, start in enumerate(starts):
        prefix = 4 if data[start : start + 4] == b"\x00\x00\x00\x01" else 3
        end = starts[i + 1] if i + 1 < len(starts) else len(data)
        if start + prefix < end:
            types.append(data[start + prefix] & 0x1F)
    if not {7, 8, 5} <= set(types):
        raise RuntimeError(f"fixture must contain SPS/PPS/IDR; NAL types={types}")
    return path, data, types


def pixels(raw: bytes, width: int, height: int) -> dict[str, Any]:
    if len(raw) != width * height * 4:
        raise RuntimeError("BGRA output size mismatch")
    colors: set[tuple[int, int, int]] = set()
    nonzero, lo, hi = 0, [255, 255, 255], [0, 0, 0]
    for i in range(0, len(raw), 4):
        b, g, r = raw[i], raw[i + 1], raw[i + 2]
        colors.add((r, g, b))
        nonzero += bool(r or g or b)
        for n, v in enumerate((r, g, b)):
            lo[n], hi[n] = min(lo[n], v), max(hi[n], v)
    ranges = [hi[i] - lo[i] for i in range(3)]
    ratio = nonzero / (width * height)
    return {
        "valid": ratio >= 0.5 and len(colors) >= 32 and sum(v >= 32 for v in ranges) >= 2,
        "sha256": hashlib.sha256(raw).hexdigest(), "colors": len(colors),
        "nonzero": round(ratio, 6), "ranges": ranges,
    }


def load(path: Path) -> dict[str, Any]:
    if os.name != "nt" or ctypes.sizeof(ctypes.c_void_p) != 8:
        raise RuntimeError("requires 64-bit Windows")
    directory = os.add_dll_directory(str(path.parent))
    lib = ctypes.CDLL(str(path))

    class Rect(ctypes.Structure):
        _fields_ = [("left", ctypes.c_uint16), ("top", ctypes.c_uint16),
                    ("right", ctypes.c_uint16), ("bottom", ctypes.c_uint16)]

    api: dict[str, Any] = {"lib": lib, "dir": directory, "Rect": Rect}

    def bind(name: str, args: list[Any] | None = None, ret: Any = ctypes.c_int32, optional: bool = False):
        try:
            fn = getattr(lib, name)
        except AttributeError:
            if optional:
                return None
            raise
        if args is not None:
            fn.argtypes, fn.restype = args, ret
        return fn

    api["new"] = bind("h264_context_new", [ctypes.c_int32], ctypes.c_void_p)
    api["free"] = bind("h264_context_free", [ctypes.c_void_p], None)
    api["set"] = bind("h264_context_set_option", [ctypes.c_void_p, ctypes.c_int, ctypes.c_uint32])
    api["get"] = bind("h264_context_get_option", [ctypes.c_void_p, ctypes.c_int], ctypes.c_uint32)
    api["reset"] = bind("h264_context_reset", [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32])
    api["decode"] = bind("avc420_decompress", [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ubyte),
        ctypes.c_uint32, ctypes.POINTER(ctypes.c_ubyte), ctypes.c_uint32, ctypes.c_uint32,
        ctypes.c_uint32, ctypes.c_uint32, ctypes.POINTER(Rect), ctypes.c_uint32])
    api["version"] = bind("ycn_h264_decode_api_version", [], ctypes.c_uint32, True)
    api["caps"] = bind("ycn_h264_decode_capabilities", [], ctypes.c_uint32, True)
    api["new_ex"] = bind("ycn_h264_context_new_ex", [ctypes.c_int32, ctypes.c_uint32], ctypes.c_void_p, True)
    api["recover"] = bind("ycn_h264_context_recover_software", [ctypes.c_void_p], ctypes.c_int32, True)
    api["diag"] = bind("ycn_h264_get_diagnostics", [ctypes.c_void_p, ctypes.POINTER(Diagnostics)],
                       ctypes.c_int32, True)
    api["newApi"] = all(api[k] for k in ("version", "caps", "new_ex", "recover", "diag"))
    api["apiVersion"] = api["version"]() if api["newApi"] else None
    api["capabilities"] = api["caps"]() if api["newApi"] else None
    return api


def get_diag(api: dict[str, Any], ctx: int) -> dict[str, Any] | None:
    if not api["newApi"]:
        return None
    d = Diagnostics()
    d.size, d.version = ctypes.sizeof(Diagnostics), 1
    if not api["diag"](ctx, ctypes.byref(d)):
        return {"ok": False}
    if int(d.size) < ctypes.sizeof(Diagnostics) or int(d.version) != 1:
        return {"ok": False, "size": int(d.size), "version": int(d.version)}
    text = lambda value: bytes(value).split(b"\0", 1)[0].decode("utf-8", "replace")
    backend, fallback = int(d.activeBackend), int(d.fallbackReason)
    return {
        "ok": True, "size": int(d.size), "version": int(d.version), "policy": int(d.policy),
        "hardwareRequested": int(d.hardwareRequested), "hardwareInitialized": int(d.hardwareInitialized),
        "activeBackend": backend, "backendName": BACKENDS.get(backend, str(backend)),
        "fallbackReason": fallback, "fallbackName": FALLBACKS.get(fallback, str(fallback)),
        "frames": int(d.successfulFrames), "hwFrames": int(d.successfulHardwareFrames),
        "swFrames": int(d.successfulSoftwareFrames), "failures": int(d.decodeFailures),
        "lastError": int(d.lastError), "errorPhase": text(d.lastErrorPhase),
        "recoveryAttempts": int(d.recoveryAttempts), "recoveryPending": int(d.recoveryPending),
    }


def decode(api: dict[str, Any], ctx: int, data: bytes, width: int, height: int):
    src = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    dst = (ctypes.c_ubyte * (width * height * 4))()
    rect = api["Rect"](0, 0, width, height)
    rc = api["decode"](ctx, src, len(data), dst, BGRA32, width * 4, width, height,
                       ctypes.byref(rect), 1)
    return int(rc), pixels(bytes(dst), width, height)


def worker_main(args: argparse.Namespace) -> int:
    dll, data = args.dll.expanduser().resolve(), args.h264.read_bytes()
    api: dict[str, Any] | None = None
    ctx: int | None = None
    started, output = time.perf_counter(), {"pid": os.getpid(), "width": args.width,
        "height": args.height, "hardware": bool(args.hw_accel),
        "backendEnv": os.environ.get("FREERDP_HWCTX_TYPE"), "dll": str(dll)}
    try:
        api = load(dll)
        policy = POLICY_HW if args.hw_accel else POLICY_SW
        if api["newApi"]:
            ctx = api["new_ex"](0, policy)
            output["setup"] = f"new_ex({policy})"
        else:
            ctx = api["new"](0)
            if ctx:
                output["legacySet"] = int(api["set"](ctx, HW_OPTION, 1 if args.hw_accel else 0))
            output["setup"] = "legacy"
        if not ctx:
            raise RuntimeError("context creation returned NULL")
        output.update(newApi=bool(api["newApi"]), apiVersion=api["apiVersion"],
                      capabilities=api["capabilities"])
        output["reset"] = int(api["reset"](ctx, args.width, args.height))
        if not output["reset"]:
            raise RuntimeError("h264_context_reset failed")
        output["hwOption"] = int(api["get"](ctx, HW_OPTION))
        before = output["diagBefore"] = get_diag(api, ctx)
        rc, first = decode(api, ctx, data, args.width, args.height)
        output["first"] = {"rc": rc, **first}
        after = output["diagAfter"] = get_diag(api, ctx)
        if args.hw_accel and api["newApi"] and not (rc == 1 and first["valid"]):
            recover_rc = int(api["recover"](ctx))
            recovered = output["diagRecovered"] = get_diag(api, ctx)
            retry_rc, retry = decode(api, ctx, data, args.width, args.height)
            retried = output["diagRetried"] = get_diag(api, ctx)
            output["recovery"] = {"rc": recover_rc, "retryRc": retry_rc, **retry}
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
    m = re.search(rf"{BEGIN}\s*(\{{.*?\}})\s*{END}", text, re.S)
    return json.loads(m.group(1)) if m else None


def log_facts(text: str) -> dict[str, Any]:
    accel = re.findall(r"Using\s+([A-Za-z0-9_]+)\s+\[[^\]]*\]\s+for accelerated H264 decoding", text, re.I)
    formats = re.findall(r"src:(AV_PIX_FMT_[A-Za-z0-9_]+)", text)
    fmt = formats[-1] if formats else None
    return {"backend": accel[-1].lower() if accel else None, "hwFormat": fmt,
            "hwFrame": bool(fmt and any(x in fmt.upper() for x in ("DXVA2", "D3D11", "VAAPI", "VDPAU"))),
            "fallback": "falling back to software" in text.lower(),
            "initFailed": "hwaccel initialisation returned error" in text.lower() or "invalid pixfmt for hwaccel" in text.lower()}


def launch(dll: Path, h264: Path, width: int, height: int, hw: bool, backend: str, label: str):
    argv = [sys.executable, str(Path(__file__).resolve()), "--_worker", "--dll", str(dll),
            "--width", str(width), "--height", str(height), "--h264", str(h264),
            "--_hw-accel", "1" if hw else "0"]
    env = os.environ.copy()
    env.update(WLOG_LEVEL="DEBUG", WLOG_APPENDER="CONSOLE", PYTHONIOENCODING="utf-8")
    if hw and backend != "default":
        env["FREERDP_HWCTX_TYPE"] = backend
    else:
        env.pop("FREERDP_HWCTX_TYPE", None)
    _, stdout, stderr = command(argv, env=env)
    text = f"--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}"
    log = ROOT / f"{label}-{width}x{height}.log"
    write(log, text)
    return extract(text), log_facts(text), log


def delta(after: dict[str, Any] | None, before: dict[str, Any] | None, key: str) -> int:
    return int(after.get(key, 0)) - int(before.get(key, 0)) if after and before else 0


def classify(worker: dict[str, Any] | None, facts: dict[str, Any], hw: bool, backend: str):
    first = (worker or {}).get("first") or {}
    output_ok = first.get("rc") == 1 and bool(first.get("valid"))
    result = {"ok": False, "hardware": False, "recovery": False, "reason": "worker failed"}
    if not worker or worker.get("error"):
        result["reason"] = (worker or {}).get("error", "missing worker result")
        return result
    if not hw:
        if worker.get("newApi"):
            d = worker.get("diagBefore") or {}
            result["ok"] = bool(output_ok and d.get("policy") == POLICY_SW and d.get("hardwareRequested") == 0)
            result["reason"] = "software new_ex policy + valid output" if result["ok"] else "software diagnostics mismatch"
        else:
            result["ok"] = bool(output_ok and not facts["hwFrame"])
            result["reason"] = "legacy software output" if result["ok"] else "legacy software evidence failed"
        return result

    if worker.get("newApi"):
        before, after = worker.get("diagBefore") or {}, worker.get("diagAfter") or {}
        hw_frames = delta(after, before, "hwFrames")
        expected = 2 if backend in ("default", "dxva2") else 3
        result["hardware"] = bool(output_ok and after.get("hardwareInitialized") == 1
                                  and hw_frames > 0 and after.get("activeBackend") in (expected, 4))
        result["reason"] = (f"successfulHardwareFrames +{hw_frames}, backend={after.get('backendName')}"
                            if result["hardware"] else
                            f"HW failed: init={after.get('hardwareInitialized')}, +frames={hw_frames}, "
                            f"backend={after.get('backendName')}, error={after.get('lastError')}/{after.get('errorPhase')}")
    else:
        result["hardware"] = bool(output_ok and facts["hwFrame"] and not facts["fallback"] and not facts["initFailed"])
        result["reason"] = "legacy WLog hardware frame" if result["hardware"] else "legacy hardware evidence failed"

    rec = worker.get("recovery")
    if rec:
        sw_frames = delta(worker.get("diagRetried"), worker.get("diagRecovered"), "swFrames")
        result["recovery"] = bool(rec.get("rc") == 1 and rec.get("retryRc") == 1 and rec.get("valid")
                                  and (worker.get("diagRetried") or {}).get("policy") == POLICY_SW and sw_frames > 0)
        detail = "PASS" if result["recovery"] else "FAIL"
        if rec.get("retryRc") == 1 and rec.get("valid") and sw_frames == 0:
            detail = "output valid but softwareFrames counter absent"
        result["reason"] += f"; recovery={detail} (softwareFrames +{sw_frames})"
    result["ok"] = bool(result["hardware"] or result["recovery"])
    return result


def main() -> int:
    a = parser().parse_args()
    if a._worker:
        return worker_main(a)
    if a.backend != "default":
        a.hardware = True
    try:
        if a.width < 16 or a.height < 16 or a.width % 2 or a.height % 2:
            raise RuntimeError("width/height must be even and >= 16")
        dll = a.dll.expanduser().resolve()
        if not dll.is_file():
            raise RuntimeError(f"DLL not found: {dll}")
        ROOT.mkdir(parents=True, exist_ok=True)
        h264, data, nals = fixture(a)
        a.h264 = h264
        print(f"H.264: {h264} bytes={len(data)} NAL={nals}\nDLL: {dll}")
        worker, facts, log = launch(dll, h264, a.width, a.height, False, "software", "software")
        software = {"worker": worker, "facts": facts, "log": str(log)}
        software["cls"] = classify(worker, facts, False, "software")
        runs = []
        if a.hardware:
            for backend in (["dxva2", "d3d11va"] if a.backend == "both" else [a.backend]):
                label = f"hw-{backend}"
                worker, facts, log = launch(dll, h264, a.width, a.height, True, backend, label)
                runs.append({"label": label, "worker": worker, "facts": facts, "log": str(log),
                             "cls": classify(worker, facts, True, backend)})

        api_version = (software["worker"] or {}).get("apiVersion")
        caps = (software["worker"] or {}).get("capabilities")
        print(f"API: version={api_version or 'legacy'} caps={caps}")
        for item in [software] + runs:
            w, c = item["worker"] or {}, item["cls"]
            first = w.get("first") or {}
            title = "software" if item is software else item["label"]
            tag = "SOFTWARE RECOVERY" if c["recovery"] else ("PASS" if c["ok"] else "FAIL")
            print(f"[{title}] {tag}: {c['reason']}")
            print(f"  first: rc={first.get('rc')} valid={first.get('valid')} colors={first.get('colors')} sha={str(first.get('sha256'))[:16]}")
            d = w.get("diagAfter")
            if d:
                print(f"  diag: size={d.get('size')} v={d.get('version')} policy={d.get('policy')} "
                      f"init={d.get('hardwareInitialized')} backend={d.get('backendName')} "
                      f"hwFrames={d.get('hwFrames')} swFrames={d.get('swFrames')} fallback={d.get('fallbackName')} "
                      f"last={d.get('lastError')}/{d.get('errorPhase')}")
            retried = w.get("diagRetried")
            if retried:
                print(f"  recoveryDiag: policy={retried.get('policy')} backend={retried.get('backendName')} "
                      f"hwFrames={retried.get('hwFrames')} swFrames={retried.get('swFrames')} "
                      f"pending={retried.get('recoveryPending')}")
            print(f"  log: {item['log']}")

        failed = [r for r in runs if not r["cls"]["hardware"]]
        recovery_pass = bool(failed) and all(r["cls"]["recovery"] for r in failed)
        summary = {"dll": str(dll), "h264": str(h264), "nals": nals, "software": software,
                   "hardware": runs, "softwarePass": software["cls"]["ok"],
                   "hardwarePass": bool(runs) and not failed, "recoveryPass": recovery_pass}
        path = ROOT / "last-run.json"
        write(path, json.dumps(summary, indent=2, sort_keys=True) + "\n")
        print(f"FINAL: software={'PASS' if summary['softwarePass'] else 'FAIL'} "
              f"hardware={'PASS' if summary['hardwarePass'] else 'FAIL'} "
              f"recovery={'PASS' if recovery_pass else 'N/A/FAIL'}\nSUMMARY: {path}")
        if not summary["softwarePass"]:
            return 1
        if runs and not summary["hardwarePass"]:
            return 2 if recovery_pass else 3
        return 0
    except Exception as exc:
        print(f"ERROR: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="Lean FreeRDP AVC420 software/hardware probe")
    p.add_argument("--dll", type=Path, default=DEFAULT_DLL)
    p.add_argument("--width", type=int, default=128)
    p.add_argument("--height", type=int, default=128)
    p.add_argument("--h264", type=Path, help="existing Annex B SPS/PPS/IDR fixture")
    p.add_argument("--hardware", action="store_true")
    p.add_argument("--backend", choices=("default", "dxva2", "d3d11va", "both"), default="default")
    p.add_argument("--_worker", action="store_true", help=argparse.SUPPRESS)
    p.add_argument("--_hw-accel", dest="hw_accel", type=int, choices=(0, 1), default=0, help=argparse.SUPPRESS)
    return p


if __name__ == "__main__":
    raise SystemExit(main())
