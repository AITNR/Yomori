#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Verify the native RDP runtime by walking real PE imports.

This tool is deliberately dependency-free (no pefile): CI and release builds
must be able to run it with a stock Python.  It checks:

  * the original eight FreeRDP/WinPR/OpenSSL/zlib/cJSON DLLs;
  * FFmpeg components avcodec/avutil/swscale/swresample by ``<name>-*.dll``
    (never by a hard-coded ABI number);
  * the full transitive PE import closure of ``ycn_rdp.dll``;
  * that FreeRDP was compiled with FFmpeg hardware decode support (the
    h264_ffmpeg.c HWACCEL code imports av_hwdevice_ctx_create /
    av_hwframe_transfer_data), while Media Foundation and OpenH264 are absent;
  * that system GPU/driver DLLs were not bundled;
  * optionally, a clean load of ``ycn_rdp.dll`` with a minimal PATH, proving
    every DLL resolves from the package directory (or from Windows itself).

Exit code 0 means every requested guard passed.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import re
import struct
import subprocess
import sys
from pathlib import Path

BASE_REQUIRED = (
    "ycn_rdp.dll",
    "freerdp3.dll",
    "freerdp-client3.dll",
    "winpr3.dll",
    "z.dll",
    "cjson.dll",
    "libssl-3-x64.dll",
    "libcrypto-3-x64.dll",
    "legacy.dll",
)

FFMPEG_COMPONENTS = ("avcodec", "avutil", "swscale", "swresample")
FFMPEG_DLL_RE = re.compile(r"^(avcodec|avutil|swscale|swresample)-\d+\.dll$", re.IGNORECASE)

# Windows OS DLLs.  These are resolved from System32 and must never be bundled.
SYSTEM_DLLS = {
    "advapi32.dll", "bcrypt.dll", "cabinet.dll", "cfgmgr32.dll", "comctl32.dll",
    "comdlg32.dll", "credui.dll", "crypt32.dll", "cryptbase.dll", "dbghelp.dll",
    "dwmapi.dll", "dwrite.dll", "d2d1.dll", "gdi32.dll", "gdiplus.dll",
    "imagehlp.dll", "iphlpapi.dll", "kernel32.dll", "kernelbase.dll",
    "mf.dll", "mfcore.dll", "mfplat.dll", "mfreadwrite.dll", "mfuuid.dll",
    "msimg32.dll", "mpr.dll", "msvcrt.dll", "ncrypt.dll", "netapi32.dll",
    "normaliz.dll", "ntdll.dll", "ole32.dll", "oleaut32.dll", "opengl32.dll",
    "powrprof.dll", "psapi.dll", "rpcrt4.dll", "secur32.dll", "setupapi.dll",
    "shell32.dll", "shlwapi.dll", "sspicli.dll", "ucrtbase.dll", "user32.dll",
    "userenv.dll", "uxtheme.dll", "version.dll", "winhttp.dll", "wininet.dll",
    "winmm.dll", "winspool.drv", "ws2_32.dll", "wldap32.dll", "wtsapi32.dll",
    "d3d11.dll", "dxgi.dll", "d3d12.dll", "d3dcompiler_47.dll",
}
SYSTEM_PREFIXES = ("api-ms-win-", "ext-ms-")

# VC++ redistributable: not part of Windows, but commonly supplied by the
# installer/OS image.  Allowed with --allow-vc-runtime-external and reported.
VC_RUNTIME = {
    "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll",
    "msvcp140_1.dll", "msvcp140_2.dll", "concrt140.dll",
}

# GPU / vendor driver DLLs must never be copied into the package.
GPU_DRIVER = {
    "d3d11.dll", "dxgi.dll", "d3d12.dll", "d3dcompiler_47.dll",
    "nvapi64.dll", "nvapi.dll", "nvcuda.dll", "nvngx.dll", "nvml.dll",
    "atiadlxx.dll", "amdvlk64.dll", "amdxc64.dll", "igd10iumd64.dll",
    "vulkan-1.dll", "opengl32.dll",
}

HWACCEL_IMPORTS = ("av_hwdevice_ctx_create", "av_hwframe_transfer_data")


class PEError(RuntimeError):
    pass


class PEImage:
    """Minimal PE32/PE32+ import parser."""

    def __init__(self, path: Path):
        self.path = path
        self.data = path.read_bytes()
        if len(self.data) < 0x40 or self.data[:2] != b"MZ":
            raise PEError("not an MZ/PE file")
        self.pe_off = self._u32(0x3C)
        if self.data[self.pe_off:self.pe_off + 4] != b"PE\0\0":
            raise PEError("missing PE signature")
        coff = self.pe_off + 4
        self.number_of_sections = self._u16(coff + 2)
        optional_size = self._u16(coff + 16)
        opt = coff + 20
        magic = self._u16(opt)
        if magic == 0x20B:
            self.pe32_plus = True
            self.image_base = self._u64(opt + 24)
            data_dir = opt + 112
        elif magic == 0x10B:
            self.pe32_plus = False
            self.image_base = self._u32(opt + 28)
            data_dir = opt + 96
        else:
            raise PEError("unsupported optional header magic 0x%x" % magic)
        self.number_of_rva_and_sizes = self._u32(data_dir - 4)
        self.data_dir = data_dir
        self.sections = []
        sec = opt + optional_size
        for i in range(self.number_of_sections):
            off = sec + i * 40
            name = self.data[off:off + 8].rstrip(b"\0").decode("ascii", "replace")
            virtual_size = self._u32(off + 8)
            virtual_address = self._u32(off + 12)
            raw_size = self._u32(off + 16)
            raw_ptr = self._u32(off + 20)
            self.sections.append((name, virtual_address, max(virtual_size, raw_size), raw_ptr, raw_size))

    def _u16(self, off: int) -> int:
        return struct.unpack_from("<H", self.data, off)[0]

    def _u32(self, off: int) -> int:
        return struct.unpack_from("<I", self.data, off)[0]

    def _u64(self, off: int) -> int:
        return struct.unpack_from("<Q", self.data, off)[0]

    def _rva(self, rva: int) -> int:
        for _name, va, size, raw, _raw_size in self.sections:
            if va <= rva < va + size:
                return raw + (rva - va)
        if 0 <= rva < len(self.data):
            return rva
        raise PEError("RVA 0x%x is outside the image" % rva)

    def _cstr(self, rva: int) -> str:
        if not rva:
            return ""
        off = self._rva(rva)
        end = self.data.find(b"\0", off)
        if end < 0:
            raise PEError("unterminated string at RVA 0x%x" % rva)
        return self.data[off:end].decode("ascii", "replace")

    def _directory(self, index: int) -> tuple[int, int]:
        if index >= self.number_of_rva_and_sizes:
            return (0, 0)
        off = self.data_dir + index * 8
        return (self._u32(off), self._u32(off + 4))

    def _thunks(self, thunk_rva: int) -> list[str]:
        names: list[str] = []
        size = 8 if self.pe32_plus else 4
        off = self._rva(thunk_rva)
        for _ in range(65536):
            if self.pe32_plus:
                value = self._u64(off)
            else:
                value = self._u32(off)
            off += size
            if value == 0:
                break
            flag = (1 << (size * 8 - 1))
            if value & flag:
                names.append("#ordinal_%d" % (value & (flag - 1)))
                continue
            names.append(self._cstr((value & 0x7FFFFFFF) + 2))
        return names

    def imports(self) -> dict[str, set[str]]:
        result: dict[str, set[str]] = {}

        def add(module: str, names: list[str]) -> None:
            if not module:
                return
            key = module.lower()
            result.setdefault(key, set()).update(names)

        imp_rva, imp_size = self._directory(1)
        if imp_rva and imp_size:
            off = self._rva(imp_rva)
            for _ in range(65536):
                original_first_thunk = self._u32(off)
                name_rva = self._u32(off + 12)
                first_thunk = self._u32(off + 16)
                if not (original_first_thunk or name_rva or first_thunk):
                    break
                module = self._cstr(name_rva)
                thunk = original_first_thunk or first_thunk
                add(module, self._thunks(thunk) if thunk else [])
                off += 20

        delay_rva, delay_size = self._directory(13)
        if delay_rva and delay_size:
            off = self._rva(delay_rva)
            for _ in range(65536):
                attrs = self._u32(off)
                name_rva = self._u32(off + 4)
                int_rva = self._u32(off + 16)
                if not (attrs or name_rva or int_rva):
                    break
                if not (attrs & 1):
                    name_rva -= self.image_base
                    int_rva -= self.image_base
                add(self._cstr(name_rva), self._thunks(int_rva) if int_rva else [])
                off += 32

        return result


def _is_system(name: str) -> bool:
    n = name.lower()
    return n in SYSTEM_DLLS or any(n.startswith(p) for p in SYSTEM_PREFIXES)


def _walk(root: Path, dlls: dict[str, Path], allow_vc_runtime: bool):
    loaded: set[str] = set()
    missing: set[str] = set()
    external: set[str] = set()
    vc_runtime: set[str] = set()
    stack = [root.name.lower()]

    while stack:
        name = stack.pop()
        if name in loaded:
            continue
        loaded.add(name)
        path = dlls.get(name)
        if path is None:
            if _is_system(name):
                external.add(name)
            elif name in VC_RUNTIME and allow_vc_runtime:
                vc_runtime.add(name)
            else:
                missing.add(name)
            continue
        try:
            imports = PEImage(path).imports()
        except (OSError, PEError) as exc:
            missing.add("%s (unreadable: %s)" % (name, exc))
            continue
        for module in imports:
            if _is_system(module):
                external.add(module)
            elif module in dlls:
                stack.append(module)
            elif module in VC_RUNTIME and allow_vc_runtime:
                vc_runtime.add(module)
            else:
                missing.add(module)

    return loaded, missing, external, vc_runtime


def _imported_functions(path: Path) -> set[str]:
    funcs: set[str] = set()
    for names in PEImage(path).imports().values():
        funcs.update(names)
    return funcs


def _find_ffmpeg_dlls(directory: Path) -> dict[str, list[str]]:
    found: dict[str, list[str]] = {c: [] for c in FFMPEG_COMPONENTS}
    for item in directory.iterdir():
        if not item.is_file():
            continue
        match = FFMPEG_DLL_RE.match(item.name)
        if match:
            found[match.group(1).lower()].append(item.name)
    for names in found.values():
        names.sort()
    return found


def _patch_hashes(patch_dir: Path | None) -> list[dict[str, str]]:
    if patch_dir is None or not patch_dir.is_dir():
        return []
    import hashlib
    result = []
    for item in sorted(patch_dir.glob("*.patch"), key=lambda p: p.name.lower()):
        result.append({
            "name": item.name,
            "sha256": hashlib.sha256(item.read_bytes()).hexdigest(),
        })
    return result


def _validate_capabilities(
    directory: Path,
    capabilities_path: Path | None,
    lock_path: Path | None,
    patch_dir: Path | None,
    problems: list[str],
) -> dict | None:
    if capabilities_path is None:
        return None
    if not capabilities_path.is_file():
        problems.append("能力信息文件不存在：%s" % capabilities_path)
        return None
    try:
        cap = json.loads(capabilities_path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        problems.append("能力信息读不出来：%s（%s）" % (capabilities_path, exc))
        return None

    if lock_path is not None:
        if not lock_path.is_file():
            problems.append("native-deps.lock.json 不存在：%s" % lock_path)
        else:
            try:
                lock = json.loads(lock_path.read_text(encoding="utf-8-sig"))
            except (OSError, ValueError) as exc:
                problems.append("native-deps.lock.json 读不出来：%s（%s）" % (lock_path, exc))
                lock = None
            if lock is not None:
                checks = [
                    ("vcpkg_commit", cap.get("vcpkg_commit"), lock.get("vcpkg_commit")),
                    ("triplet", cap.get("triplet"), lock.get("triplet")),
                    ("freerdp_version", cap.get("freerdp_version"), (lock.get("freerdp") or {}).get("version")),
                    ("ffmpeg_version", cap.get("ffmpeg_version"), (lock.get("ffmpeg") or {}).get("version")),
                    ("ycn_h264_decode_api_version", cap.get("ycn_h264_decode_api_version"), lock.get("ycn_h264_decode_api_version")),
                    ("ycn_h264_decode_capabilities_mask", cap.get("ycn_h264_decode_capabilities_mask"), lock.get("ycn_h264_decode_capabilities_mask")),
                ]
                for label, actual, expected in checks:
                    if actual != expected:
                        problems.append("能力信息 %s=%r，与 lock=%r 不一致" % (label, actual, expected))
                for label, actual, expected in (
                    ("freerdp_features", cap.get("freerdp_features"), (lock.get("freerdp") or {}).get("features")),
                    ("ffmpeg_components", cap.get("ffmpeg_components"), (lock.get("ffmpeg") or {}).get("components")),
                ):
                    if sorted(actual or []) != sorted(expected or []):
                        problems.append("能力信息 %s=%r，与 lock=%r 不一致" % (label, actual, expected))
                expected_options = lock.get("freerdp_options") or {}
                actual_options = cap.get("build_options") or {}
                for key, expected in expected_options.items():
                    if actual_options.get(key) is not expected:
                        problems.append("能力信息 build_options.%s=%r，lock 要求 %r" % (
                            key, actual_options.get(key), expected))

    dlls = {p.name.lower(): p for p in directory.glob("*.dll") if p.is_file()}
    for name in cap.get("ffmpeg_dlls") or []:
        if name.lower() not in dlls:
            problems.append("能力信息列出了 FFmpeg DLL %s，但包内不存在" % name)

    expected_patches = cap.get("patches")
    if expected_patches is None:
        problems.append("能力信息缺少 patches 字段（无法证明 DLL 与 native-deps/patches 一致）")
    else:
        actual = _patch_hashes(patch_dir)
        normalized = [
            {"name": str(p.get("name", "")), "sha256": str(p.get("sha256", "")).lower()}
            for p in expected_patches
        ]
        if normalized != actual:
            problems.append(
                "能力信息 patches 与当前 native-deps/patches 不一致：cap=%r actual=%r"
                % (normalized, actual)
            )

    if not cap.get("cache_key"):
        problems.append("能力信息缺少 cache_key")

    return cap


def _clean_load(directory: Path, required_modules: list[str], problems: list[str]) -> dict | None:
    if os.name != "nt":
        problems.append("--clean-load 只支持 Windows")
        return None

    child = r"""
import ctypes, json, os, sys
app = os.path.abspath(sys.argv[1])
required = sys.argv[2:]
os.environ["PATH"] = os.path.join(os.environ["SystemRoot"], "System32")
os.environ["OPENSSL_MODULES"] = app
_keep = os.add_dll_directory(app)
try:
    dll = ctypes.WinDLL(os.path.join(app, "ycn_rdp.dll"))
except OSError as exc:
    print("LOAD_ERROR=" + repr(exc))
    sys.exit(3)
version = dll.ycn_rdp_version
version.restype = ctypes.c_char_p
raw = version()
version_text = raw.decode("utf-8", "replace") if raw else ""
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetModuleHandleW.restype = ctypes.c_void_p
k32.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
k32.GetModuleFileNameW.restype = ctypes.c_uint
k32.GetModuleFileNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint]
bad = []
for name in required:
    handle = k32.GetModuleHandleW(name)
    if not handle:
        bad.append("NOT_LOADED:" + name)
        continue
    buf = ctypes.create_unicode_buffer(32768)
    if not k32.GetModuleFileNameW(handle, buf, len(buf)):
        bad.append("NO_PATH:" + name)
        continue
    actual = os.path.normcase(os.path.abspath(buf.value))
    expected = os.path.normcase(os.path.normpath(os.path.join(app, name)))
    if actual != expected:
        bad.append("WRONG_PATH:%s:%s" % (name, buf.value))
if bad:
    print("CLEAN_LOAD_ERRORS=" + json.dumps(bad))
    sys.exit(2)

# The overlay patch exports a versioned H264 decode API from freerdp3.dll.
# Calling it proves the patch is really compiled into the loaded binary, not
# merely declared in capabilities.json.
exports = [
    "ycn_h264_decode_api_version",
    "ycn_h264_decode_capabilities",
    "ycn_h264_context_new_ex",
    "ycn_h264_context_recover_software",
    "ycn_h264_get_diagnostics",
]
info = {"exports": [], "decode_api_version": None, "decode_capabilities": None}
try:
    fd = ctypes.WinDLL(os.path.join(app, "freerdp3.dll"))
except OSError as exc:
    print("FREERDP_LOAD_ERROR=" + repr(exc))
    sys.exit(4)
info["exports"] = [name for name in exports if hasattr(fd, name)]
if "ycn_h264_decode_api_version" in info["exports"]:
    fd.ycn_h264_decode_api_version.restype = ctypes.c_uint32
    info["decode_api_version"] = int(fd.ycn_h264_decode_api_version())
if "ycn_h264_decode_capabilities" in info["exports"]:
    fd.ycn_h264_decode_capabilities.restype = ctypes.c_uint32
    info["decode_capabilities"] = int(fd.ycn_h264_decode_capabilities())
# OpenSSL providers are loaded dynamically and absent from PE imports.
crypto = ctypes.CDLL(os.path.join(app, "libcrypto-3-x64.dll"))
crypto.OSSL_PROVIDER_load.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
crypto.OSSL_PROVIDER_load.restype = ctypes.c_void_p
crypto.EVP_MD_fetch.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p]
crypto.EVP_MD_fetch.restype = ctypes.c_void_p
crypto.EVP_MD_free.argtypes = [ctypes.c_void_p]
crypto.EVP_CIPHER_fetch.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p]
crypto.EVP_CIPHER_fetch.restype = ctypes.c_void_p
crypto.EVP_CIPHER_free.argtypes = [ctypes.c_void_p]
provider = crypto.OSSL_PROVIDER_load(None, b"legacy")
md4 = crypto.EVP_MD_fetch(None, b"MD4", None)
rc4 = crypto.EVP_CIPHER_fetch(None, b"RC4", None)
if not provider or not md4 or not rc4:
    print("OPENSSL_LEGACY_ERROR: provider/MD4/RC4 unavailable")
    sys.exit(5)
crypto.EVP_MD_free(md4)
crypto.EVP_CIPHER_free(rc4)

print("YCN_RDP_VERSION=" + version_text)
print("YCN_H264_API=" + json.dumps(info, sort_keys=True))
"""

    env = os.environ.copy()
    env["PATH"] = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
    env.pop("PYTHONPATH", None)
    env.pop("PYTHONHOME", None)
    cmd = [sys.executable, "-I", "-S", "-c", child, str(directory)] + required_modules
    proc = subprocess.run(cmd, cwd=str(directory), env=env, capture_output=True, text=True, timeout=30)
    if proc.returncode != 0:
        detail = (proc.stdout or "") + (proc.stderr or "")
        problems.append("干净加载失败（exit %d）：%s" % (proc.returncode, detail.strip()))
        return None
    info = {"version": None, "exports": [], "decode_api_version": None, "decode_capabilities": None}
    for line in proc.stdout.splitlines():
        if line.startswith("YCN_RDP_VERSION="):
            info["version"] = line.split("=", 1)[1]
        elif line.startswith("YCN_H264_API="):
            try:
                info.update(json.loads(line.split("=", 1)[1]))
            except ValueError as exc:
                problems.append("干净加载返回的 H264 API 信息读不出来：%s" % exc)
    if info["version"] is None:
        problems.append("干净加载没有返回 ycn_rdp_version")
        return None
    return info


def verify(
    directory: Path,
    *,
    require_ffmpeg: bool,
    expect_hwaccel: bool,
    forbid_system_gpu: bool,
    allow_vc_runtime: bool,
    clean_load: bool,
    expect_ycn_h264_api: bool,
    capabilities_path: Path | None,
    lock_path: Path | None,
    patch_dir: Path | None,
) -> tuple[list[str], dict]:
    problems: list[str] = []
    report: dict = {}

    directory = directory.resolve()
    if not directory.is_dir():
        return ["目录不存在：%s" % directory], report

    dlls = {p.name.lower(): p for p in directory.glob("*.dll") if p.is_file()}
    for name in BASE_REQUIRED:
        if name.lower() not in dlls:
            problems.append("缺少原有运行库 %s" % name)

    if forbid_system_gpu:
        for path in directory.iterdir():
            if not path.is_file():
                continue
            name = path.name.lower()
            if name in GPU_DRIVER:
                problems.append("发布目录混入了系统 GPU/驱动 DLL：%s" % path.name)
            elif _is_system(name):
                problems.append("发布目录混入了 Windows 系统 DLL：%s" % path.name)

    ffmpeg = _find_ffmpeg_dlls(directory)
    report["ffmpeg_dlls"] = {k: v for k, v in ffmpeg.items()}
    if require_ffmpeg:
        for component in FFMPEG_COMPONENTS:
            if not ffmpeg.get(component):
                problems.append("缺少 FFmpeg 组件 %s-*.dll" % component)

    required_root = directory / "ycn_rdp.dll"
    loaded: set[str] = set()
    missing: set[str] = set()
    external: set[str] = set()
    vc_runtime: set[str] = set()
    if required_root.is_file():
        loaded, missing, external, vc_runtime = _walk(required_root, dlls, allow_vc_runtime)
        for item in sorted(missing):
            problems.append("PE 传递依赖缺失：%s" % item)
    else:
        problems.append("缺少入口 DLL ycn_rdp.dll")

    if require_ffmpeg:
        for component in FFMPEG_COMPONENTS:
            names = ffmpeg.get(component) or []
            if names and not any(n.lower() in loaded for n in names):
                problems.append("FFmpeg 组件 %s 不可从 ycn_rdp.dll 传递到达" % component)

    report["loaded"] = sorted(loaded)
    report["external_system"] = sorted(external)
    report["vc_runtime_external"] = sorted(vc_runtime)
    report["missing"] = sorted(missing)

    freerdp3 = directory / "freerdp3.dll"
    hw_imports: list[str] = []
    if freerdp3.is_file():
        try:
            funcs = _imported_functions(freerdp3)
            hw_imports = [name for name in HWACCEL_IMPORTS if name in funcs]
            report["hwaccel_imports"] = hw_imports
            if expect_hwaccel:
                for name in HWACCEL_IMPORTS:
                    if name not in funcs:
                        problems.append(
                            "freerdp3.dll 未导入 %s —— 不能证明构建启用了 WITH_FFMPEG_HWACCEL" % name
                        )
            modules = set(PEImage(freerdp3).imports())
            mf = sorted(m for m in modules if m.startswith("mf") and m.endswith(".dll"))
            openh264 = sorted(m for m in modules if m.startswith("openh264"))
            report["media_foundation_imports"] = mf
            report["openh264_imports"] = openh264
            if mf:
                problems.append("freerdp3.dll 仍导入 Media Foundation：%s" % ", ".join(mf))
            if openh264:
                problems.append("freerdp3.dll 仍导入 OpenH264：%s" % ", ".join(openh264))
        except (OSError, PEError) as exc:
            problems.append("freerdp3.dll 导入表读不出来：%s" % exc)

    cap = _validate_capabilities(
        directory, capabilities_path, lock_path, patch_dir, problems
    )
    if cap is not None:
        report["capabilities"] = {
            "vcpkg_commit": cap.get("vcpkg_commit"),
            "triplet": cap.get("triplet"),
            "freerdp_version": cap.get("freerdp_version"),
            "ffmpeg_version": cap.get("ffmpeg_version"),
            "cache_key": cap.get("cache_key"),
        }

    if expect_ycn_h264_api and not clean_load:
        problems.append("--expect-ycn-h264-api 需要同时启用 --clean-load（必须实际调用导出函数）")

    if clean_load:
        required_modules = ["ycn_rdp.dll", "freerdp3.dll", "winpr3.dll"]
        for component in FFMPEG_COMPONENTS:
            required_modules.extend(ffmpeg.get(component) or [])
        load_info = _clean_load(directory, required_modules, problems)
        if load_info is not None:
            report["clean_load"] = load_info
            if expect_ycn_h264_api:
                expected_exports = {
                    "ycn_h264_decode_api_version",
                    "ycn_h264_decode_capabilities",
                    "ycn_h264_context_new_ex",
                    "ycn_h264_context_recover_software",
                    "ycn_h264_get_diagnostics",
                }
                actual_exports = set(load_info.get("exports") or [])
                missing_exports = sorted(expected_exports - actual_exports)
                if missing_exports:
                    problems.append("freerdp3.dll 缺少 YCN H264 导出：%s" % ", ".join(missing_exports))
                api_version = load_info.get("decode_api_version")
                caps = load_info.get("decode_capabilities")
                if api_version != 1:
                    problems.append("ycn_h264_decode_api_version=%r，期望 1" % api_version)
                # AVC | diagnostics | hardware compiled
                required_caps = 0x1 | 0x2 | 0x4
                if not isinstance(caps, int) or (caps & required_caps) != required_caps:
                    problems.append("ycn_h264_decode_capabilities=%r，缺少 AVC/诊断/硬件编译标志" % caps)

    return problems, report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify native RDP runtime DLLs and PE closure")
    parser.add_argument("--dir", required=True, help="directory containing ycn_rdp.dll and its runtime DLLs")
    parser.add_argument("--require-ffmpeg", action="store_true", help="require avcodec/avutil/swscale/swresample")
    parser.add_argument("--expect-hwaccel", action="store_true", help="require FFmpeg HWACCEL import evidence")
    parser.add_argument("--forbid-system-gpu", action="store_true", help="reject bundled Windows/GPU driver DLLs")
    parser.add_argument("--allow-vc-runtime-external", action="store_true",
                        help="treat vcruntime140/msvcp140 as an external target prerequisite")
    parser.add_argument("--clean-load", action="store_true", help="load ycn_rdp.dll with a minimal PATH")
    parser.add_argument("--expect-ycn-h264-api", action="store_true",
                        help="require the overlay-patched YCN H264 decode API exports and caps")
    parser.add_argument("--capabilities", default=None, help="ycn_rdp.capabilities.json")
    parser.add_argument("--lock", default=None, help="winui3/native-deps/native-deps.lock.json")
    parser.add_argument("--patches-dir", default=None, help="winui3/native-deps/patches")
    parser.add_argument("--json-out", default=None, help="optional machine-readable report path")
    args = parser.parse_args(argv)

    problems, report = verify(
        Path(args.dir),
        require_ffmpeg=args.require_ffmpeg,
        expect_hwaccel=args.expect_hwaccel,
        forbid_system_gpu=args.forbid_system_gpu,
        allow_vc_runtime=args.allow_vc_runtime_external,
        clean_load=args.clean_load,
        expect_ycn_h264_api=args.expect_ycn_h264_api,
        capabilities_path=Path(args.capabilities) if args.capabilities else None,
        lock_path=Path(args.lock) if args.lock else None,
        patch_dir=Path(args.patches_dir) if args.patches_dir else None,
    )

    if args.json_out:
        Path(args.json_out).write_text(
            json.dumps({"ok": not problems, "problems": problems, "report": report},
                       ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )

    if problems:
        print("原生层验证未通过：", file=sys.stderr)
        for item in problems:
            print("  - " + item, file=sys.stderr)
        return 1

    print("原生层验证通过")
    print("  FFmpeg DLL：%s" % ", ".join(
        n for names in (report.get("ffmpeg_dlls") or {}).values() for n in names))
    print("  外部系统 DLL：%s" % ", ".join(report.get("external_system") or []))
    print("  外部 VC 运行库（目标机前提）：%s" % (", ".join(report.get("vc_runtime_external") or []) or "无"))
    print("  硬件解码导入证据：%s" % ", ".join(report.get("hwaccel_imports") or []))
    if report.get("clean_load"):
        load = report["clean_load"]
        print("  干净加载：OK（ycn_rdp_version=%s）" % load.get("version"))
        if load.get("decode_api_version") is not None:
            print("  YCN H264 decode API：version=%s caps=0x%x exports=%s" % (
                load.get("decode_api_version"), load.get("decode_capabilities") or 0,
                ",".join(load.get("exports") or [])))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
