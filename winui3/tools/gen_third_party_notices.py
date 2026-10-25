# -*- coding: utf-8 -*-
"""生成 THIRD-PARTY-NOTICES.txt —— 随包分发的第三方组件与许可证声明。

为什么必须有这个文件（不是"写着好看"）：

1. **法律义务**。发布包里实际分发着 FreeRDP / WinPR / OpenSSL 的 DLL，三者都是
   **Apache-2.0**。该许可证 §4 明确规定：分发衍生作品时须 (a) 给接收者一份本许可证
   副本；(b) 保留原作品的版权与归属声明。只在自己的 LICENSE（GPL-3.0）里写一句
   "用了 FreeRDP" 是不够的 —— 必须把许可证原文一起发出去。
   zlib / MIT 类只要求「保留版权与许可声明」，同样得落在文字里。

2. **别手抄**。手写一份清单，下次 vcpkg 升级 freerdp 就过期了，而且抄错一个版权行
   就等于没声明。这里的做法是：
     · **组件清单**读 `winui3/third_party/components.json`（与 C# 界面共用的唯一数据源）；
     · 原生组件的**许可证原文**取 vcpkg 安装目录里的 `share/<pkg>/copyright`；
     · 原生组件的**版本**取 `share/<pkg>/vcpkg.spdx.json` 的 versionInfo；
     · 托管组件的**版本**读 `Yomori.App.csproj` 里的 PackageReference。
   改依赖只需要改那个 JSON，本脚本的正文不用动。

3. **拿不到 vcpkg 时要能退回**。CI 里原生层命中 `native-out` 缓存时，「取 vcpkg」与
   「安装 freerdp」两步整段跳过 —— 机器上根本没有 `.vcpkg/`，而这个包与之前发过的
   那些在许可证层面毫无区别（DLL 是从缓存复用的同一份）。所以准备第二档：
   仓库里冻结的 `winui3/third_party/licenses/`（由 `--export-licenses` 从真 vcpkg 导出，
   不是手抄）。许可证正文是固定文本，不随版本变化；版本号与"本次取自哪里"都会写进
   生成的文件里，不会假装是现场读的。

4. **抓空要炸，不能静默**。vcpkg 的目录布局一变，`copyright` 就可能读不到。
   那种情况下如果只是少贴一段，文件看起来"生成成功"、CI 全绿，发出去的包却是
   不合规的。所以每个组件都校验：原文存在、非空、且真的含该许可证的特征串。

用法：

    # 本机（vcpkg 默认在 <仓库>/../_tools/vcpkg）
    python winui3/tools/gen_third_party_notices.py --out dist/app/THIRD-PARTY-NOTICES.txt

    # CI（vcpkg 装在 workspace 的 .vcpkg/installed/x64-windows）
    python winui3/tools/gen_third_party_notices.py --out ... \\
        --vcpkg-inst .vcpkg/installed/x64-windows

    # 换 vcpkg / 换 FreeRDP 版本之后：刷新仓库里的冻结副本
    python winui3/tools/gen_third_party_notices.py --export-licenses
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime
from pathlib import Path

# ---------------------------------------------------------------- 组件元数据

# 组件清单在 winui3/third_party/components.json —— **单一数据源**：
# C# 侧（「关于 → 开源许可」对话框）把同一个文件作为嵌入资源读，两边不可能漂移。
# 改依赖 / 换许可证只改那个 JSON，别在代码里另抄一份清单。
_COMPONENTS_JSON = Path(__file__).resolve().parent.parent / "third_party" / "components.json"

# JSON 用 camelCase，脚本内部统一成 snake_case
_RENAME = {
    "mustContain": "must_contain",
    "copyrightFrom": "copyright_from",
    "versionFrom": "version_from",
    "buildOnly": "build_only",
}

_cached: tuple[list[dict], list[dict]] | None = None


def _components() -> tuple[list[dict], list[dict]]:
    """返回 (原生组件, 托管组件)。首次调用时读 JSON，之后走缓存。"""
    global _cached
    if _cached is None:
        if not _COMPONENTS_JSON.is_file():
            raise SystemExit(f"找不到组件清单：{_COMPONENTS_JSON}")
        try:
            data = json.loads(_read_text_any(_COMPONENTS_JSON))
        except ValueError as exc:
            raise SystemExit(f"组件清单不是合法 JSON：{_COMPONENTS_JSON}（{exc}）")

        def normalize(items: list[dict]) -> list[dict]:
            return [
                {_RENAME.get(k, k): v for k, v in item.items()}
                for item in items
            ]

        _cached = (normalize(data.get("native") or []), normalize(data.get("managed") or []))
    return _cached


_SEP = "=" * 78
_SUB = "-" * 78


# ---------------------------------------------------------------- 路径解析

def _default_vcpkg_inst() -> Path:
    """与 CMakeLists.txt 完全一致的默认值：<仓库>/../_tools/vcpkg/installed/x64-windows。

    两边必须同步 —— 这里只是读许可证文本，但读的要是**同一个** vcpkg，
    否则会出现「编译用的是 3.32.0、声明里写的是别的版本」。
    """
    #   脚本在 <仓库>/winui3/tools/ 下，所以 parents[1] = 仓库根、parents[3] = 仓库的上级。
    #   CMakeLists.txt 里的 ../../../../../ 数出来是同一层（从 Yomori.RdpNative 起往上 5 级），
    #   两边任何一个改了另一个都得跟着改。
    here = Path(__file__).resolve().parent      # winui3/tools
    return (here.parents[3] / "_tools" / "vcpkg" / "installed" / "x64-windows").resolve()


def _read_text_any(path: Path) -> str:
    """按 utf-8 → utf-8-sig → gbk 依次尝试。vcpkg 的 copyright 有的带 BOM 有的不带。"""
    for enc in ("utf-8-sig", "utf-8", "gbk"):
        try:
            return path.read_text(encoding=enc)
        except UnicodeDecodeError:
            continue
    return path.read_text(encoding="utf-8", errors="replace")


def _spdx_version(share_dir: Path, pkg: str) -> str:
    """从 vcpkg.spdx.json 里取包版本。取不到返回空串（由调用方决定怎么显示）。

    ⚠️ vcpkg 的 versionInfo 会带 port 修订号（`3.6.4#1`、`1.3.2#2`）—— `#` 后面是
    vcpkg port 的打包修订，不是上游库的版本，写进声明里会让用户以为 OpenSSL 出了个
    "3.6.4#1" 的版本。这里剥掉。
    """
    spdx = share_dir / pkg / "vcpkg.spdx.json"
    if not spdx.is_file():
        return ""
    try:
        data = json.loads(_read_text_any(spdx))
    except (OSError, ValueError):
        return ""
    for entry in data.get("packages") or []:
        # 只要主包本身（带 :x64-windows 后缀的那条是构建产物，不是版本来源）
        if entry.get("name") == pkg and entry.get("versionInfo"):
            return re.sub(r"#\d+$", "", str(entry["versionInfo"]))
    return ""


def _csproj_versions(csproj: Path) -> dict[str, str]:
    """读 csproj 里的 PackageReference 版本。手写解析而不是用 XML 库：
    这个文件里有大量注释、条件属性，ElementTree 处理 MSBuild 语义反而更绕。"""
    if not csproj.is_file():
        return {}
    text = _read_text_any(csproj)
    return dict(
        re.findall(
            r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"',
            text,
            flags=re.IGNORECASE,
        )
    )


# ---------------------------------------------------------------- 生成

def _collect_native(vcpkg_inst: Path, freeze_dir: Path) -> tuple[list[dict], list[str], str]:
    """按「vcpkg 现场 → 仓库冻结副本」两档取许可证原文。

    返回 (组件列表, 问题列表, 来源描述)。问题非空 → 调用方让构建失败。

    **为什么需要第二档（冻结副本）**：CI 里原生层产物命中 `native-out` 缓存时，
    「取 vcpkg」与「安装 freerdp」两步整段跳过 —— 整台机器上根本没有 `.vcpkg/`。
    这时候如果只认 vcpkg，打包会失败；可是这个包和之前发过的那些在许可证层面
    没有任何区别（DLL 是从缓存里复用的同一份）。

    Apache-2.0 / zlib / MIT 的**许可证正文是固定文本**，不随版本变化，所以把正文
    冻结进仓库（`winui3/third_party/licenses/`）不违反"别手抄"——
    那份正文同样是由本脚本从 vcpkg 抓出来导出的（`--export-licenses`），
    而且版本号与"本次取自哪里"会明明白白写进生成的声明里，不会假装是现场读的。
    """
    share = vcpkg_inst / "share"
    problems: list[str] = []
    native, _ = _components()

    # ⚠️ 这里必须复制一份再改：_components() 的结果是缓存的，直接往里面的 dict
    #    塞 version / copyright_text，第二次调用就会带着上一次的结果走。
    native = [dict(c) for c in native]

    if share.is_dir():
        source = "vcpkg"
        versions = {c["key"]: _spdx_version(share, c.get("version_from", c["key"])) for c in native}
    elif (freeze_dir / "sources.json").is_file():
        source = "freeze"
        try:
            meta = json.loads(_read_text_any(freeze_dir / "sources.json"))
        except (OSError, ValueError) as exc:
            return [], [f"{freeze_dir / 'sources.json'} 读不出来（{exc}）"], ""
        versions = {k: str(v.get("version", "")) for k, v in meta.items()}
    else:
        return [], [
            f"既找不到 vcpkg 的 share 目录（{share}），也找不到仓库冻结副本（{freeze_dir}）",
            "→ 用 --vcpkg-inst 指到 vcpkg 的 installed/x64-windows（CI 是 .vcpkg/installed/x64-windows），",
            "   或先跑一次 --export-licenses 把正文冻结进仓库",
        ], ""

    for comp in native:
        src_key = comp.get("copyright_from", comp["key"])
        if source == "vcpkg":
            path = share / src_key / "copyright"
        else:
            path = freeze_dir / f"{src_key}.txt"

        if not path.is_file():
            problems.append(f"{comp['name']}：许可证原文缺失 {path}（来源：{source}）")
            continue

        text = _read_text_any(path).strip()
        if not text:
            problems.append(f"{comp['name']}：{path} 是空的")
            continue

        missing = [s for s in comp["must_contain"] if s not in text]
        if missing:
            problems.append(
                f"{comp['name']}：{path} 里找不到 {missing}"
                " —— 抓到的可能不是许可证正文（vcpkg 布局变了 / 冻结文件被改坏？）"
            )
            continue

        comp["version"] = versions.get(comp.get("version_from", comp["key"])) or "（未知）"
        comp["copyright_text"] = text

    return native, problems, source


def _render(
    argv_version: str,
    vcpkg_inst: Path,
    native: list[dict],
    managed_versions: dict[str, str],
    source: str,
) -> str:
    now = datetime.now().strftime("%Y-%m-%d %H:%M")
    out: list[str] = []
    _, managed = _components()

    out.append("Yomori —— 第三方组件与许可声明")
    out.append(f"程序版本：{argv_version}")
    out.append(f"生成时间：{now}")
    out.append("")
    out.append("本程序在分发时包含下列第三方组件。各组件的版权归属与许可条款如下，")
    out.append("按许可证要求原文附录于每个组件之后。本程序自身以 GNU General Public")
    out.append("License v3.0 发布，许可证全文见程序目录下的 LICENSE.txt。")
    out.append("")
    out.append(_SEP)
    out.append("一、原生层（C/C++ 运行库，随安装包分发的本机 DLL）")
    out.append(_SEP)
    if source == "freeze":
        out.append("（许可证原文取自随本程序一起冻结的副本；组件版本见下。）")
    out.append("")

    for idx, comp in enumerate(native, 1):
        out.append(f"{idx}. {comp['name']} {comp.get('version', '')}".rstrip())
        out.append(f"   许可证：{comp['license']}")
        out.append(f"   分发文件：{', '.join(comp['files'])}")
        out.append(f"   用途：{comp['purpose']}")
        out.append(f"   项目主页：{comp['homepage']}")
        out.append("")
        out.append("   " + _SUB[3:])
        origin = "随程序冻结的副本" if source == "freeze" else "vcpkg 安装目录"
        out.append(f"   以下为 {comp['name']} 的许可与版权声明原文（取自{origin}）：")
        out.append("   " + _SUB[3:])
        out.append("")
        for line in comp.get("copyright_text", "").splitlines():
            out.append(("   " + line).rstrip())
        out.append("")
        out.append("")

    out.append(_SEP)
    out.append("二、托管层（.NET / NuGet 包）")
    out.append(_SEP)
    out.append("")
    for idx, comp in enumerate(managed, 1):
        version = comp.get("version") or managed_versions.get(comp["package"], "")
        note = "（构建期工具，不随发布包分发）" if comp.get("build_only") else ""
        out.append(f"{idx}. {comp['name']}{(' ' + version) if version else ''}{note}")
        out.append(f"   许可证：{comp['license']}")
        out.append(f"   {comp['copyright']}")
        out.append(f"   用途：{comp['purpose']}")
        out.append(f"   项目主页：{comp['homepage']}")
        out.append("")

    out.append(_SUB)
    out.append("MIT 许可证全文（适用于上列全部托管层组件）")
    out.append(_SUB)
    out.append("")
    out.append(_MIT_TEXT.strip())
    out.append("")
    out.append(_SEP)
    out.append("补充说明")
    out.append(_SEP)
    out.append("")
    out.append("· 本程序不包含任何游戏客户端代码或其资源文件。")
    out.append("· 程序不提供游戏内自动化逻辑，只负责按预设顺序调度用户已有的脚本。")
    out.append("· 上列组件的许可证原文以本文件为准；如与组件官方仓库发布的最新版本有出入，")
    out.append("  以官方仓库为准（本文件内容取自构建时所用的 vcpkg 与 NuGet 版本）。")
    if source == "freeze":
        out.append("· 原生组件的许可证原文取自随程序冻结的副本（winui3/third_party/licenses/），")
        out.append("  与本次分发所用 DLL 的版本一致；原文来自构建该版本 DLL 时的 vcpkg。")
    else:
        out.append("· 原生组件的完整许可证原文来自 vcpkg 安装目录：")
        out.append(f"    {vcpkg_inst}")
    out.append("")

    return "\n".join(out)


_MIT_TEXT = """\
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE."""


def _force_utf8_stdio() -> None:
    """CI 的 Windows runner 默认 cp1252，打印中文会 UnicodeEncodeError 打断构建。
    与 package_release.py 同一处理。"""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass


def main(argv: list[str] | None = None) -> int:
    _force_utf8_stdio()

    here = Path(__file__).resolve().parent
    default_csproj = here.parent / "src" / "Yomori.App" / "Yomori.App.csproj"
    freeze_dir = here.parent / "third_party" / "licenses"   # <仓库>/winui3/third_party/licenses

    parser = argparse.ArgumentParser(description="生成第三方组件与许可声明（THIRD-PARTY-NOTICES.txt）")
    parser.add_argument("--out", default=None, help="输出文件路径（--export-licenses 模式下不需要）")
    parser.add_argument("--vcpkg-inst", default=None, help="vcpkg 的 installed/x64-windows 目录")
    parser.add_argument("--csproj", default=str(default_csproj), help="用来读 NuGet 包版本的项目文件")
    parser.add_argument("--version", default="0.0.0", help="写进文件头的程序版本")
    parser.add_argument(
        "--export-licenses",
        action="store_true",
        help="把从 vcpkg 抓到的许可证原文冻结进仓库（third_party/licenses/），"
        "供 CI 在拿不到 vcpkg 时回退。此模式下 --out 可不给。",
    )
    args = parser.parse_args(argv)

    vcpkg_inst = Path(args.vcpkg_inst).resolve() if args.vcpkg_inst else _default_vcpkg_inst()

    if args.export_licenses:
        return _export_licenses(vcpkg_inst, freeze_dir)

    if not args.out:
        parser.error("--out 是必需的（除非用 --export-licenses）")

    native, problems, source = _collect_native(vcpkg_inst, freeze_dir)
    if problems:
        print("生成第三方声明失败：", file=sys.stderr)
        for p in problems:
            print("  - " + p, file=sys.stderr)
        return 1

    managed_versions = _csproj_versions(Path(args.csproj))
    text = _render(args.version, vcpkg_inst, native, managed_versions, source)

    out_path = Path(args.out).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    # utf-8-sig：这个文件是给用户用记事本直接打开的，没有 BOM 在中文系统上会乱码
    out_path.write_text(text, encoding="utf-8-sig")

    origin = "vcpkg 现场" if source == "vcpkg" else "仓库冻结副本"
    print(f"已生成 {out_path}")
    print(f"  来源：{origin} · 原生 {len(native)} 个 · 托管 {len(_components()[1])} 个 · {len(text)} 字符")
    return 0


def _export_licenses(vcpkg_inst: Path, freeze_dir: Path) -> int:
    """从 vcpkg 抓原文冻结进仓库。只在**本地有 vcpkg 的机器**上跑一次即可，
    升级 vcpkg / 换 FreeRDP 版本后再跑一次（否则冻结副本会滞后）。"""
    native, problems, source = _collect_native(vcpkg_inst, freeze_dir)
    if source != "vcpkg":
        print("冻结失败：没有可用的 vcpkg，无法抓到许可证原文。", file=sys.stderr)
        print(f"  期望的 share 目录：{vcpkg_inst / 'share'}", file=sys.stderr)
        for p in problems:
            print("  - " + p, file=sys.stderr)
        return 1

    freeze_dir.mkdir(parents=True, exist_ok=True)
    meta: dict[str, dict] = {}

    # 按 vcpkg 的 key 落地（winpr3 复用 freerdp 的正文，不重复写一份）
    for comp in native:
        src_key = comp.get("copyright_from", comp["key"])
        text = comp["copyright_text"] + "\n"
        # 只在文件内容真的变了时才重写，避免制造无意义的 diff
        target = freeze_dir / f"{src_key}.txt"
        if not target.is_file() or _read_text_any(target) != text:
            target.write_text(text, encoding="utf-8")
            print(f"  写入 {target.name}")
        meta[src_key] = {
            "name": comp["name"] if src_key == comp["key"] else "FreeRDP（含 WinPR）",
            "version": comp.get("version", ""),
            "license": comp["license"],
        }

    meta_path = freeze_dir / "sources.json"
    payload = json.dumps(meta, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    if not meta_path.is_file() or _read_text_any(meta_path) != payload:
        meta_path.write_text(payload, encoding="utf-8")
        print(f"  写入 {meta_path.name}")

    print(f"已把 {len(native)} 个原生组件的许可证原文冻结到 {freeze_dir}")
    print("  ⚠️ 这些文件的版本必须与分发的 DLL 对应 —— 换 vcpkg / 换 FreeRDP 版本后要重跑。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
