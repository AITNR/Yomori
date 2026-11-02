#!/usr/bin/env python3
"""Native decode ABI/diagnostic checks for Yomori's embedded FreeRDP bridge.

This test intentionally does not generate H.264 fixtures. Pass an existing
Annex-B access unit with --h264 for an offline decoder probe. An offline GPU
decode result is evidence for the local decoder path only; it is not an RDP
end-to-end acceptance result.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
from pathlib import Path
import sys


YCN_DECODE_CAP_AVC = 0x1
YCN_DECODE_CAP_DIAGNOSTICS = 0x2
YCN_DECODE_CAP_HW_COMPILED = 0x4
YCN_KNOWN_CAPS = YCN_DECODE_CAP_AVC | YCN_DECODE_CAP_DIAGNOSTICS | YCN_DECODE_CAP_HW_COMPILED

YCN_H264_DIAGNOSTICS_VERSION = 1
YCN_GRAPHICS_SETTINGS_PROBE_VERSION = 1


class GraphicsSettingsProbe(ctypes.Structure):
    _fields_ = [
        ("size", ctypes.c_uint32),
        ("version", ctypes.c_uint32),
        ("use_gfx", ctypes.c_uint32),
        ("decode_policy", ctypes.c_uint32),
        ("legacy_entry", ctypes.c_uint32),
        ("support_graphics_pipeline", ctypes.c_uint32),
        ("support_dynamic_channels", ctypes.c_uint32),
        ("gfx_thin_client", ctypes.c_uint32),
        ("gfx_small_cache", ctypes.c_uint32),
        ("gfx_h264", ctypes.c_uint32),
        ("gfx_avc444", ctypes.c_uint32),
        ("gfx_avc444v2", ctypes.c_uint32),
        ("gfx_progressive", ctypes.c_uint32),
        ("gfx_progressivev2", ctypes.c_uint32),
        ("gfx_planar", ctypes.c_uint32),
        ("audio_unchanged", ctypes.c_uint32),
        ("reserved", ctypes.c_uint32 * 4),
    ]


class H264Diagnostics(ctypes.Structure):
    _fields_ = [
        ("size", ctypes.c_uint32),
        ("version", ctypes.c_uint32),
        ("policy", ctypes.c_uint32),
        ("capabilities", ctypes.c_uint32),
        ("hardware_requested", ctypes.c_uint32),
        ("hardware_initialized", ctypes.c_uint32),
        ("active_backend", ctypes.c_uint32),
        ("fallback_reason", ctypes.c_uint32),
        ("successful_frames", ctypes.c_uint64),
        ("successful_hardware_frames", ctypes.c_uint64),
        ("successful_software_frames", ctypes.c_uint64),
        ("decode_failures", ctypes.c_uint64),
        ("last_error", ctypes.c_int32),
        ("recovery_attempts", ctypes.c_uint32),
        ("recovery_pending", ctypes.c_uint32),
        ("reserved", ctypes.c_uint32),
        ("backend_name", ctypes.c_char * 32),
        ("last_error_phase", ctypes.c_char * 32),
        ("reserved2", ctypes.c_uint32 * 2),
    ]


class Rect16(ctypes.Structure):
    _fields_ = [
        ("left", ctypes.c_uint16),
        ("top", ctypes.c_uint16),
        ("right", ctypes.c_uint16),
        ("bottom", ctypes.c_uint16),
    ]


def _default_dll() -> Path | None:
    base = Path(__file__).resolve().parents[1]
    candidates = [
        base / "src" / "Yomori.RdpNative" / "build" / "Release" / "ycn_rdp.dll",
        base / "src" / "Yomori.RdpNative" / "build" / "Debug" / "ycn_rdp.dll",
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    matches = sorted((base / "src" / "Yomori.RdpNative" / "build").glob("**/ycn_rdp.dll"))
    return matches[0] if matches else None


_dll_directories: list[object] = []

def _load_dll(path: Path) -> ctypes.CDLL:
    if hasattr(os, "add_dll_directory"):
        _dll_directories.append(os.add_dll_directory(str(path.parent)))
    return ctypes.CDLL(str(path))


def _configure(signature, restype, *argtypes):
    signature.restype = restype
    signature.argtypes = list(argtypes)


def _diag_text(value: bytes) -> str:
    return value.split(b"\0", 1)[0].decode("utf-8", errors="replace")


def _diag_dict(diag: H264Diagnostics) -> dict[str, object]:
    return {
        "size": diag.size,
        "version": diag.version,
        "policy": diag.policy,
        "capabilities": diag.capabilities,
        "hardware_requested": bool(diag.hardware_requested),
        "hardware_initialized": bool(diag.hardware_initialized),
        "active_backend": diag.active_backend,
        "fallback_reason": diag.fallback_reason,
        "successful_frames": diag.successful_frames,
        "successful_hardware_frames": diag.successful_hardware_frames,
        "successful_software_frames": diag.successful_software_frames,
        "decode_failures": diag.decode_failures,
        "last_error": diag.last_error,
        "recovery_attempts": diag.recovery_attempts,
        "recovery_pending": bool(diag.recovery_pending),
        "backend_name": _diag_text(diag.backend_name),
        "last_error_phase": _diag_text(diag.last_error_phase),
    }


def _probe_graphics_settings(
    lib: ctypes.CDLL, use_gfx: int, policy: int, legacy_entry: int
) -> dict[str, object]:
    probe = GraphicsSettingsProbe()
    probe.size = ctypes.sizeof(GraphicsSettingsProbe)
    probe.version = YCN_GRAPHICS_SETTINGS_PROBE_VERSION
    rc = int(lib.ycn_rdp_graphics_settings_probe(use_gfx, policy, legacy_entry, ctypes.byref(probe)))
    if rc != 0:
        raise AssertionError(
            f"graphics settings probe(use_gfx={use_gfx}, policy={policy}, "
            f"legacy={legacy_entry}) returned {rc}"
        )
    if probe.size != ctypes.sizeof(GraphicsSettingsProbe):
        raise AssertionError(f"graphics probe changed size to {probe.size}")
    if probe.version != YCN_GRAPHICS_SETTINGS_PROBE_VERSION:
        raise AssertionError(f"graphics probe version={probe.version}")
    if probe.use_gfx != (1 if use_gfx else 0):
        raise AssertionError("graphics probe did not echo use_gfx")
    if probe.decode_policy != policy or probe.legacy_entry != (1 if legacy_entry else 0):
        raise AssertionError("graphics probe did not echo policy/legacy_entry")
    return {
        "use_gfx": probe.use_gfx,
        "decode_policy": probe.decode_policy,
        "legacy_entry": probe.legacy_entry,
        "support_graphics_pipeline": probe.support_graphics_pipeline,
        "support_dynamic_channels": probe.support_dynamic_channels,
        "gfx_thin_client": probe.gfx_thin_client,
        "gfx_small_cache": probe.gfx_small_cache,
        "gfx_h264": probe.gfx_h264,
        "gfx_avc444": probe.gfx_avc444,
        "gfx_avc444v2": probe.gfx_avc444v2,
        "gfx_progressive": probe.gfx_progressive,
        "gfx_progressivev2": probe.gfx_progressivev2,
        "gfx_planar": probe.gfx_planar,
        "audio_unchanged": probe.audio_unchanged,
    }


def check_graphics_settings_probe(lib: ctypes.CDLL) -> dict[str, object]:
    """Verify generated rdpSettings directly; no diag-string matching.

    Real CAPSADVERTISE/CAPSCONFIRM flags are intentionally not predicted here.
    A separate offline GFX caps PDU probe owns wire-level validation.
    """
    _configure(
        lib.ycn_rdp_graphics_settings_probe,
        ctypes.c_int,
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.POINTER(GraphicsSettingsProbe),
    )
    if ctypes.sizeof(GraphicsSettingsProbe) != 80:
        raise AssertionError(f"unexpected graphics probe size {ctypes.sizeof(GraphicsSettingsProbe)}")

    unsupported = GraphicsSettingsProbe()
    unsupported.size = ctypes.sizeof(GraphicsSettingsProbe)
    unsupported.version = YCN_GRAPHICS_SETTINGS_PROBE_VERSION + 1
    rc = int(lib.ycn_rdp_graphics_settings_probe(1, 1, 0, ctypes.byref(unsupported)))
    if rc >= 0:
        raise AssertionError(f"graphics probe accepted unsupported version: rc={rc}")

    cases: dict[str, tuple[int, int, int]] = {
        "gfx_disabled_auto": (0, 1, 0),
        "auto": (1, 1, 0),
        "software": (1, 2, 0),
        "legacy_entry": (1, 0, 1),
        "gfx_disabled_software": (0, 2, 0),
    }
    results: dict[str, object] = {}
    for name, (use_gfx, policy, legacy_entry) in cases.items():
        results[name] = _probe_graphics_settings(lib, use_gfx, policy, legacy_entry)

    for name in ("gfx_disabled_auto", "gfx_disabled_software"):
        item = results[name]
        assert item["support_graphics_pipeline"] == 0, f"{name}: GFX must be disabled"
        assert item["support_dynamic_channels"] == 1, f"{name}: dynamic channels must stay enabled"
        assert item["audio_unchanged"] == 1, f"{name}: audio setting must be preserved"
        assert item["gfx_thin_client"] == 0, f"{name}: thin client must be off without GFX"
        assert item["gfx_h264"] == 0, f"{name}: AVC must be off without GFX"
        assert item["gfx_avc444"] == 0, f"{name}: AVC444 must stay off"
        assert item["gfx_avc444v2"] == 0, f"{name}: AVC444v2 must stay off"

    for name in ("auto", "software"):
        item = results[name]
        assert item["support_graphics_pipeline"] == 1, f"{name}: GFX must be enabled"
        assert item["support_dynamic_channels"] == 1, f"{name}: dynamic channels must stay enabled"
        assert item["gfx_thin_client"] == 1, f"{name}: ThinClient must suppress progressive"
        assert item["gfx_small_cache"] == 1, f"{name}: ThinClient implies small cache"
        assert item["gfx_h264"] == 1, f"{name}: AVC420 must be advertised"
        assert item["gfx_avc444"] == 1, f"{name}: modern path must advertise AVC444"
        assert item["gfx_avc444v2"] == 1, f"{name}: modern path must advertise AVC444v2"
        assert item["gfx_progressive"] == 0, f"{name}: progressive must remain disabled"
        assert item["gfx_planar"] == 1, f"{name}: planar must remain available"

    legacy = results["legacy_entry"]
    assert legacy["support_graphics_pipeline"] == 1, "legacy entry must keep its GFX behavior"
    assert legacy["gfx_thin_client"] == 0, "legacy entry must not gain ThinClient"
    assert legacy["gfx_h264"] == 0, "legacy entry must stay non-AVC"
    assert legacy["gfx_avc444"] == 0, "legacy entry must not enable AVC444"
    assert legacy["gfx_avc444v2"] == 0, "legacy entry must not enable AVC444v2"
    return results


def check_bridge_abi(lib: ctypes.CDLL) -> dict[str, object]:
    _configure(lib.ycn_rdp_decode_api_version, ctypes.c_uint32)
    _configure(lib.ycn_rdp_decode_capabilities, ctypes.c_uint32)
    api_version = int(lib.ycn_rdp_decode_api_version())
    if api_version != 1:
        raise AssertionError(f"ycn_rdp_decode_api_version()={api_version}, expected 1")
    capabilities = int(lib.ycn_rdp_decode_capabilities())
    if capabilities & ~YCN_KNOWN_CAPS:
        raise AssertionError(f"unknown capability bits: 0x{capabilities:x}")
    if (capabilities & YCN_DECODE_CAP_DIAGNOSTICS) and not (
        capabilities & YCN_DECODE_CAP_AVC
    ):
        raise AssertionError("diagnostics reported without AVC support")
    return {"api_version": api_version, "capabilities": capabilities}


def check_h264_diagnostics(lib: ctypes.CDLL, freerdp: ctypes.CDLL, policy: int) -> dict[str, object]:
    new_ex = getattr(freerdp, "ycn_h264_context_new_ex", None)
    reset = getattr(freerdp, "h264_context_reset", None)
    free_context = getattr(freerdp, "h264_context_free", None)
    get_diag = getattr(freerdp, "ycn_h264_get_diagnostics", None)
    if not all((new_ex, reset, free_context, get_diag)):
        raise RuntimeError("patched FreeRDP diagnostics symbols missing")

    _configure(new_ex, ctypes.c_void_p, ctypes.c_int, ctypes.c_uint32)
    _configure(reset, ctypes.c_bool, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32)
    _configure(free_context, None, ctypes.c_void_p)
    _configure(get_diag, ctypes.c_bool, ctypes.c_void_p, ctypes.POINTER(H264Diagnostics))

    context = new_ex(0, policy)
    if not context:
        raise AssertionError("ycn_h264_context_new_ex returned NULL")
    try:
        if not reset(context, 128, 128):
            raise AssertionError("h264_context_reset(128,128) failed")

        invalid = H264Diagnostics()
        if get_diag(context, ctypes.byref(invalid)):
            raise AssertionError("diagnostics accepted zero size/version")

        wrong_version = H264Diagnostics()
        wrong_version.size = ctypes.sizeof(H264Diagnostics)
        wrong_version.version = 2
        if get_diag(context, ctypes.byref(wrong_version)):
            raise AssertionError("diagnostics accepted unsupported version")

        diag = H264Diagnostics()
        diag.size = ctypes.sizeof(H264Diagnostics)
        diag.version = YCN_H264_DIAGNOSTICS_VERSION
        if not get_diag(context, ctypes.byref(diag)):
            raise AssertionError("ycn_h264_get_diagnostics rejected v1 struct")
        if diag.policy != policy:
            raise AssertionError(f"policy={diag.policy}, expected {policy}")
        if diag.version != YCN_H264_DIAGNOSTICS_VERSION:
            raise AssertionError(f"diagnostics version={diag.version}")
        if ctypes.sizeof(H264Diagnostics) != 152:
            raise AssertionError(f"unexpected diagnostics size {ctypes.sizeof(H264Diagnostics)}")
        return _diag_dict(diag)
    finally:
        free_context(context)


def decode_offline(lib: ctypes.CDLL, freerdp: ctypes.CDLL, h264_path: Path,
                   width: int, height: int, policy: int) -> dict[str, object]:
    if width <= 0 or height <= 0 or width > 65535 or height > 65535:
        raise ValueError("width/height must fit uint16")

    new_ex = freerdp.ycn_h264_context_new_ex
    reset = freerdp.h264_context_reset
    free_context = freerdp.h264_context_free
    get_diag = freerdp.ycn_h264_get_diagnostics
    decompress = freerdp.avc420_decompress
    _configure(new_ex, ctypes.c_void_p, ctypes.c_int, ctypes.c_uint32)
    _configure(reset, ctypes.c_bool, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32)
    _configure(free_context, None, ctypes.c_void_p)
    _configure(get_diag, ctypes.c_bool, ctypes.c_void_p, ctypes.POINTER(H264Diagnostics))
    _configure(
        decompress,
        ctypes.c_int32,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_uint8),
        ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_uint8),
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.POINTER(Rect16),
        ctypes.c_uint32,
    )

    data = h264_path.read_bytes()
    if not data:
        raise ValueError(f"{h264_path} is empty")
    source = (ctypes.c_uint8 * len(data)).from_buffer_copy(data)
    destination = (ctypes.c_uint8 * (width * height * 4))()
    region = Rect16(0, 0, width, height)

    context = new_ex(0, policy)
    if not context:
        raise AssertionError("ycn_h264_context_new_ex returned NULL")
    try:
        if not reset(context, width, height):
            raise AssertionError("h264_context_reset failed")
        rc = int(decompress(context, source, len(data), destination, 0x20048888,
                            width * 4, width, height, ctypes.byref(region), 1))
        diag = H264Diagnostics()
        diag.size = ctypes.sizeof(H264Diagnostics)
        diag.version = YCN_H264_DIAGNOSTICS_VERSION
        if not get_diag(context, ctypes.byref(diag)):
            raise AssertionError("diagnostics query failed after offline decode")
        result = _diag_dict(diag)
        result["avc420_rc"] = rc
        if rc < 0:
            raise AssertionError(f"avc420_decompress failed: {rc}")
        if not any(destination):
            raise AssertionError("decoder returned an empty BGRA image")
        if diag.successful_frames < 1:
            raise AssertionError("successful_frames did not advance")
        if policy == 2 and (diag.successful_hardware_frames != 0 or diag.hardware_initialized):
            raise AssertionError("software policy initialized hardware or counted HW frames")
        return result
    finally:
        free_context(context)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dll", type=Path, help="path to ycn_rdp.dll")
    parser.add_argument("--freerdp-dll", type=Path, help="path to freerdp3.dll")
    parser.add_argument("--h264", type=Path, help="existing Annex-B H.264 access unit")
    parser.add_argument("--width", type=int, default=128)
    parser.add_argument("--height", type=int, default=128)
    parser.add_argument("--policy", type=int, choices=(1, 2), default=1)
    parser.add_argument(
        "--selftest",
        choices=("graphics",),
        help="run a connection-free native self-test and exit",
    )
    args = parser.parse_args()

    dll_path = args.dll or _default_dll()
    if dll_path is None or not dll_path.is_file():
        print("native_decode_checks: ycn_rdp.dll not found; pass --dll", file=sys.stderr)
        return 2
    dll_path = dll_path.resolve()
    lib = _load_dll(dll_path)
    result: dict[str, object] = {"dll": str(dll_path), "bridge": check_bridge_abi(lib)}
    result["graphics_settings"] = check_graphics_settings_probe(lib)
    if args.selftest == "graphics":
        print(json.dumps(result, indent=2, ensure_ascii=False))
        return 0

    caps = int(result["bridge"]["capabilities"])
    if not (caps & YCN_DECODE_CAP_DIAGNOSTICS):
        result["patched_freerdp"] = "skipped: bridge reports no diagnostics capability"
        print(json.dumps(result, indent=2, ensure_ascii=False))
        return 0

    freerdp_path = args.freerdp_dll or (dll_path.parent / "freerdp3.dll")
    if not freerdp_path.is_file():
        print(f"native_decode_checks: {freerdp_path} not found", file=sys.stderr)
        return 2
    freerdp = _load_dll(freerdp_path.resolve())
    result["diagnostics"] = check_h264_diagnostics(lib, freerdp, args.policy)
    if args.h264:
        result["offline_decode"] = decode_offline(
            lib, freerdp, args.h264.resolve(), args.width, args.height, args.policy
        )
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
