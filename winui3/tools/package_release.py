# -*- coding: utf-8 -*-
"""把构建产物收拢成一个可以直接解压运行的发布包。

为什么需要它（CI 与本机都用同一份逻辑）：

1. **`dotnet publish` 的输出不完整**。WinUI 3 的 `Yomori.pri` 与各页面的 `*.xbf`
   （`Views/`、`Themes/`、`embed/`）只在 **build** 输出里，publish 目录里没有；
   `Yomori.RdpNative` 那批 FreeRDP 运行库（freerdp3 / winpr3 / openssl / zlib…）
   也只在 build 输出里。缺了它们，程序要么起不来、要么页面打不开 ——
   所以打包 = publish 输出（自包含时，带 .NET 运行时）+ build 输出（补齐运行期文件）。
2. **绝不能把仓库根的 `config.json` 打进包**：那是使用者本机的真实配置
   （目标账户名、桥目录等）。程序首次运行会自己生成默认配置。
3. 应用图标等资源在**仓库根** `assets/`，必须与 exe 同级落地 ——
   `AppPaths.ResolveBaseDir()` 就是靠"同级有 assets/ 或 config.json"回溯定位程序根目录的。
4. **第三方组件的许可证副本必须随包发**。包里实际分发着 FreeRDP / WinPR / OpenSSL 的
   DLL，这三者都是 Apache-2.0，该许可证 §4 要求分发时附上许可证副本 —— 所以打包末尾会
   调 `gen_third_party_notices.py` 生成 `THIRD-PARTY-NOTICES.txt`，并把它列入自检。
   许可证原文优先取构建现场的 vcpkg（版本最准）；CI 原生层命中缓存时机器上根本没有
   `.vcpkg/`，退回仓库里冻结的副本（`third_party/licenses/`）。

用法：

    # 框架依赖（体积小，需要目标机器装 .NET 8 运行时）
    python winui3/tools/package_release.py --build-dir <build 输出> \
        --assets assets --out dist --name Yomori-winui3-2.1.0-x64

    # 自包含（解压即用）
    python winui3/tools/package_release.py --build-dir <build 输出> --publish-dir <publish 输出> ...

打包完成后会做**自检**：关键文件缺任何一个就非零退出（宁可 CI 红，也别发出一个跑不起来的包）。
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import sys
import zipfile
from pathlib import Path


def _force_utf8_stdio() -> None:
    """把标准输出/错误强制成 UTF-8。

    Windows 上 Python 的 stdout 跟着控制台代码页走：本机是 cp936 尚可，
    而 GitHub Actions 的 Windows runner 是 **cp1252** —— 一打印中文就
    UnicodeEncodeError 直接把打包打断（CI 真实踩过一轮）。
    这里统一重设，并允许个别字符降级成 ?，保证日志不因编码问题中断。
    """
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):  # 老版本 / 被重定向的流
            pass

# ---------------------------------------------------------------- 文件筛选

# build 输出里的"构建过程垃圾"：不进发布包
_JUNK_SUFFIXES = (
    ".cache",
    ".g.cs",
    ".g.i.cs",
    ".resfiles",
    ".editorconfig",
    ".sourcelink.json",
    ".filelistabsolute.txt",
    ".up2date",
)

# 这些文件由 publish（自包含）说了算，build 那份**一律不复制**过去。
# 见 main() 里的注释：build 产出的是框架依赖版，混进去会让装好的程序弹
# "You must install or update .NET to run this application"。
_PUBLISH_AUTHORITATIVE = {
    "yomori.exe",
    "yomori.runtimeconfig.json",
    "yomori.deps.json",
}

_JUNK_NAMES = {
    "input.json",
    "output.json",
    "xamlsavestatefile.xml",
    "multiplequalifiersperdimensionfound.txt",
    "yomori.app.assemblyinfo.cs",
    "yomori.app.assemblyinfoinputs.cache",
}

# build 输出里的中间目录：不进发布包
_JUNK_DIRS = {"ref", "refint", "obj", ".vs", "win-x64", "bin"}

# WinUI 3 自带 83 种语言的 MUI 目录：<语言>\Microsoft.ui.xaml.dll.mui
#     （+ Microsoft.UI.Xaml.Phone.dll.mui），每个目录固定 2 个文件，
#     452 个条目里 166 个是它们，共 3.7MB。
# 这些是 **WinUI 内置控件**的本地化字符串（日期选择器的月份名、内置对话框按钮、
# 自动化名称），不是本程序的界面文字（那走 XAML / .pri，与这里无关）。
# 删掉不报错：Windows 的 MUI 查找失败会回退到 DLL 内的中性资源（英文），
# 只有上面那几句会变英文。所以只留简体中文 + 英文兜底。
#
# ⚠️ 目录名就是语言标识（OS 的 MUI 查找规则），**不能改造成 lang\zh-CN 之类**，
#    只能整个目录丢掉。这条规则也解释了为什么安装目录看着散 —— 摊平不了。
# ⚠️ `<SatelliteResourceLanguages>` 对这批文件无效：它们是通过
#    RuntimeTargetsCopyLocalItems（runtime 资产）进输出的，不是卫星程序集。
_MUI_KEEP_CULTURES = {"zh-cn", "zh-tw", "en-us"}

# 语言目录名形状：af-ZA / sr-Cyrl-RS / ca-Es-VALENCIA / quz-PE
_CULTURE_DIR_RE = re.compile(r"^[a-z]{2,3}(?:-[a-z0-9]{2,8})+$", re.IGNORECASE)

# WinAppSDK 框架 MSIX **整包**带进来的 AI / ML / Widgets 载荷：本程序一次都没用
#     （grep 全源码：Windows.AI / OnnxRuntime / DirectML / Widgets 零引用），
#     却占 25 个文件、约 46MB —— onnxruntime.dll 21MB、DirectML.dll 18MB 是最大两块。
# ⚠️ 在 csproj 里换子包引用**挡不住**（载荷来自 MSIX 解压，与引用哪几个子包无关），
#    只有在"发布包"这个出口裁才行 —— 所以放在这里，不放 csproj。
_UNUSED_PAYLOAD_PREFIXES = (
    "microsoft.windows.ai.",   # 含 .Projection.dll / .winmd
    "microsoft.windows.widgets.",
    "microsoft.ml.onnxruntime",
    "onnxruntime",             # onnxruntime.dll / onnxruntime_providers_shared.dll
    "directml",
    "workloads",               # workloads*.json：AI 工作负载清单
)


def _is_junk(rel: Path) -> bool:
    """rel 是相对 build 输出根的路径。"""
    if any(part.lower() in _JUNK_DIRS for part in rel.parts[:-1]):
        return True

    name = rel.name.lower()
    if name in _JUNK_NAMES:
        return True
    if name.endswith(_JUNK_SUFFIXES):
        return True
    # XAML 源码副本（真正生效的是 *.xbf，源码拷进去只是噪音）
    if name.endswith(".xaml"):
        return True
    if name.endswith(".pdb"):
        return True  # 需要符号时用 --keep-pdb

    return False


def _copy_tree(
    src: Path,
    dst: Path,
    *,
    skip_junk: bool,
    keep_pdb: bool = False,
    skip_names: set[str] | None = None,
) -> int:
    """把 src 目录内容拷进 dst（合并覆盖），返回拷贝的文件数。

    skip_names 是小写文件名集合，命中的直接跳过（用于"publish 权威"的那几个文件）。
    """
    if not src.is_dir():
        raise SystemExit(f"目录不存在：{src}")

    count = 0
    for item in src.rglob("*"):
        rel = item.relative_to(src)
        if item.is_dir():
            continue
        if skip_names and item.name.lower() in skip_names:
            continue
        if skip_junk and _is_junk(rel) and not (keep_pdb and rel.name.lower().endswith(".pdb")):
            continue

        target = dst / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(item, target)
        count += 1

    return count


# ---------------------------------------------------------------- 收尾裁剪

def _deps_listed_names(app_dir: Path) -> set[str]:
    """`Yomori.deps.json` 里登记过的文件名（全小写）。

    ⚠️ 这是裁剪的**护栏**，别去掉：宿主启动时按 deps.json 里的相对路径去找程序集，
       登记了却不在磁盘上会直接抛
         "An assembly specified in the application dependencies manifest was not found"
       → 双击没反应、也没有界面可以报错。
       实测：`Microsoft.Windows.AI.*.Projection.dll` / `Microsoft.Windows.Widgets.Projection.dll`
       这 7 个**托管**投影**确实登记在 deps.json 里**（虽然本程序一次不加载），
       而 onnxruntime / DirectML / *.winmd / workloads*.json 都没登记。
       所以按名单删之前先查这里 —— 登记过的留着（合计不到 0.3MB，不差这点）。
    """
    deps = app_dir / "Yomori.deps.json"
    if not deps.exists():
        return set()

    data = json.loads(deps.read_text(encoding="utf-8"))
    names: set[str] = set()
    for library in (data.get("targets") or {}).values():
        for entry in (library or {}).values():
            for section in ("runtime", "native", "resources", "runtimeTargets"):
                for rel in (entry.get(section) or {}):
                    names.add(Path(rel).name.lower())
    return names


def _prune_unused(app_dir: Path) -> tuple[int, int]:
    """把"发了但程序用不到"的东西从发布包里删掉，返回 (删除文件数, 删除目录数)。

    为什么放在最后统一做，而不是在 _copy_tree 里筛：
    publish 那份是 `skip_junk=False` 原样铺的（它自带完整自包含运行时，逐项筛风险更大），
    而 AI 载荷在 build 和 publish 两边都有 —— 只有在"两边都铺完"这一个点上裁才不会漏。

    两类东西：
    1. WinAppSDK 框架 MSIX 整包带进来的 AI / ML / Widgets 载荷（见
       _UNUSED_PAYLOAD_PREFIXES 的说明），约 25 个文件 / 46MB。
    2. WinUI 自带的 80 余种语言 MUI 目录（见 _MUI_KEEP_CULTURES 的说明），
       每个固定 2 个 .mui，共约 3.7MB。

    被 `deps.json` 登记过的文件名一律不删（见 _deps_listed_names）。
    """
    files = 0
    dirs = 0
    listed = _deps_listed_names(app_dir)

    for item in list(app_dir.iterdir()):
        if item.is_file():
            name = item.name.lower()
            if name.startswith(_UNUSED_PAYLOAD_PREFIXES) and name not in listed:
                item.unlink()
                files += 1
            continue

        # 语言目录：名字像语言代码，且内容**全部**是 .mui 才敢整目录删
        if not _CULTURE_DIR_RE.match(item.name) or item.name.lower() in _MUI_KEEP_CULTURES:
            continue
        children = list(item.iterdir())
        if not children:
            continue
        if not all(c.is_file() and c.suffix.lower() == ".mui" for c in children):
            continue
        files += len(children)
        shutil.rmtree(item)
        dirs += 1

    return files, dirs



# ---------------------------------------------------------------- 自检

def _verify(app_dir: Path, *, require_dotnet_runtime: bool, agent_included: bool = False) -> None:
    """关键文件自检：缺任何一个都算打包失败。"""
    problems: list[str] = []

    def need(rel: str, note: str) -> None:
        if not (app_dir / rel).exists():
            problems.append(f"{rel}（{note}）")

    need("Yomori.exe", "主程序")
    need("Yomori.dll", "主程序集")
    need("Yomori.pri", "XAML 资源索引 —— 缺了界面打不开")
    need("Yomori.runtimeconfig.json", ".NET 运行时配置")
    need("ycn_rdp.dll", "内嵌 RDP 原生层")
    need("freerdp3.dll", "FreeRDP 运行库")
    need("freerdp-client3.dll", "FreeRDP 客户端运行库")
    need("winpr3.dll", "WinPR 运行库")
    # FreeRDP 是动态链接的：少了下面这几个，ycn_rdp.dll 会在「开始执行」时
    # 抛 DllNotFoundException 直接卡死（2026-10-09 复现过）。打包自检挡在发布前。
    need("z.dll", "zlib（FreeRDP 依赖）")
    need("cjson.dll", "cJSON（FreeRDP 依赖）")
    need("libssl-3-x64.dll", "OpenSSL libssl（FreeRDP 依赖）")
    need("libcrypto-3-x64.dll", "OpenSSL libcrypto（FreeRDP 依赖）")
    need("assets/icon.ico", "应用图标（也是程序根目录的定位标记）")

    # 会话代理载荷（agent\ 子目录）：目标账户登录后跑的就是它。
    # 缺了不影响主控端自身启动，但「部署代理」一定失败 —— 那是远程通道的全部前提，
    # 所以按硬性缺失处理。给了 --agent-dir 就必须真的铺进来。
    if agent_included:
        need("agent/Yomori.Agent.exe", "会话代理主程序")
        need("agent/Yomori.Agent.dll", "会话代理程序集")

        # ⚠️ 这条断言才是 agent\ 存在的意义：代理是纯后台进程，不碰 XAML / WinRT。
        #    一旦哪天有人把它改回「复用主控端整套依赖」，这里会先红 ——
        #    而不是等目标机上白占 150MB 才发现。
        jank = [
            name
            for name in (
                "Microsoft.WinUI.dll",
                "Microsoft.UI.Xaml.dll",
                "Microsoft.Windows.SDK.NET.dll",
                "onnxruntime.dll",
                "DirectML.dll",
                "unins000.exe",
            )
            if (app_dir / "agent" / name).exists()
        ]
        if jank:
            problems.append(
                "agent/ 里混入了主控端才有的组件：" + "、".join(jank)
                + "（代理是纯后台进程，这些它一次都用不到，只会让每台目标机多占上百 MB）"
            )

    # 第三方组件与许可声明。FreeRDP / WinPR / OpenSSL 都是 Apache-2.0，§4 要求分发时
    # 随附许可证副本 —— 这个文件缺了就是不合规的包，按硬性缺失处理（与"跑不起来"同级）。
    need("THIRD-PARTY-NOTICES.txt", "第三方组件与许可声明（Apache-2.0 要求随包分发）")

    # 本程序自己的许可证：GPL-3.0 §1 要求「随目标代码分发本许可证副本」。
    # 与上面那份同为硬性缺失 —— 少了它，这个包在合规上与"跑不起来"同级。
    need("LICENSE.txt", "本程序许可证全文（GPL-3.0 要求随包分发）")

    # 外置 UI 看门狗（可选但强烈建议带上）：独立进程，主控端整窗假死时它不受影响，
    # 负责记录现场 + 抓 dump。进程内看门狗会被同一个冻结拖住，这就是要它的理由。
    # 不设为硬性缺失：CI 未构建它时不该让整个打包失败。
    wd = app_dir / "Yomori.Watchdog.exe"
    if not wd.exists():
        print("提示：未包含 Yomori.Watchdog.exe（外置 UI 看门狗）。", file=sys.stderr)
        print("      主控端假死时将没有外部证据可查。", file=sys.stderr)

    xbf = list(app_dir.glob("Views/*.xbf"))
    if not xbf:
        problems.append("Views/*.xbf（页面 XAML 编译产物）")

    if require_dotnet_runtime:
        if not (app_dir / "System.Private.CoreLib.dll").exists():
            problems.append("System.Private.CoreLib.dll（自包含模式应带 .NET 运行时）")
        problems.extend(_verify_self_contained(app_dir))

        # 代理同样要能"解压即用"：目标账户那台机器（其实是同一台）不该被要求另装运行时。
        if agent_included and not (app_dir / "agent" / "System.Private.CoreLib.dll").exists():
            problems.append(
                "agent/System.Private.CoreLib.dll（自包含包的代理也必须是自包含的，"
                "否则目标账户登录后代理起不来，远程通道全断）"
            )

    if (app_dir / "config.json").exists():
        problems.append("config.json（不该出现在发布包里 —— 会泄露使用者本机配置）")

    if problems:
        print("打包自检未通过，缺少/多出：", file=sys.stderr)
        for p in problems:
            print("  - " + p, file=sys.stderr)
        raise SystemExit(1)


def _verify_self_contained(app_dir: Path) -> list[str]:
    """自包含模式必须**真的**是自包含：光看有没有 System.Private.CoreLib.dll 是不够的。

    踩过的坑：build 目录（框架依赖）的 runtimeconfig 覆盖了 publish 的，
    CoreLib 还在（是 publish 铺的），但 runtimeconfig 写的是
    `"framework": Microsoft.NETCore.App 8.0.0` → 装完双击就弹
    "You must install or update .NET to run this application"。
    所以这里直接读 runtimeconfig：自包含必须是 `includedFrameworks`，出现 `framework` 就是被覆盖了。
    """
    import json

    problems: list[str] = []
    cfg_path = app_dir / "Yomori.runtimeconfig.json"
    if not cfg_path.is_file():
        return problems  # 缺文件已由 need() 报过

    try:
        cfg = json.loads(cfg_path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        return [f"Yomori.runtimeconfig.json 读不出来（{exc}）"]

    opts = cfg.get("runtimeOptions") or {}
    included = opts.get("includedFrameworks")
    framework = opts.get("framework")
    if framework and not included:
        problems.append(
            "Yomori.runtimeconfig.json 是**框架依赖**版"
            f"（framework={framework.get('name')} {framework.get('version')}）—— "
            "自包含包必须是 includedFrameworks；多半是 build 输出覆盖了 publish 输出"
        )
    elif not included:
        problems.append("Yomori.runtimeconfig.json 里既没有 includedFrameworks 也没有 framework（自包含应写 includedFrameworks）")

    return problems


# ---------------------------------------------------------------- 说明文件

_HELP_TEXT = """\
Yomori · WinUI 3 版
版本：{version}
打包时间：{built_at}

────────────────────────────────────────
怎么用
────────────────────────────────────────
1. 解压到一个**固定目录**（不要放在临时目录里，日志与统计会写在它旁边）；
2. 双击 Yomori.exe —— 首次运行会自动生成 config.json（默认配置）；
3. 要接远程会话，先在左侧菜单「会话通道 → 通道管理…」里配置通道：
   目标主机 / 目标账户 / 保存凭据 / 部署会话代理 / 重新预检。
   详细步骤见仓库里的 winui3/USER_GUIDE.md。

────────────────────────────────────────
这个包是什么
────────────────────────────────────────
{package_kind}

目录约定：
  Yomori.exe       主控端（界面）
  agent/           会话代理载荷（目标账户登录后跑的就是它；纯后台，无界面）
  assets/          应用图标等资源（删掉会导致程序找不到自己的根目录）
  logs/            运行日志（首次运行后生成）
  runtime_stats/   耗时统计（首次运行后生成）
  config.json      你的配置，**不随包发布**（首次运行自动生成）

────────────────────────────────────────
注意
────────────────────────────────────────
· WinUI 3 内嵌画面依赖 D3D11，远程桌面/虚拟机里可能需要图形加速；
· 程序本体只支持 Windows 10 1809 及以上、x64；
· 许可证：GNU General Public License v3.0，全文见同目录 LICENSE.txt，
  源码见 https://github.com/AITNR/Yomori 。
"""


def _write_help(app_dir: Path, version: str, package_kind: str, built_at: str) -> None:
    (app_dir / "使用说明.txt").write_text(
        _HELP_TEXT.format(version=version, package_kind=package_kind, built_at=built_at),
        encoding="utf-8-sig",  # 记事本打开不乱码
    )


def _write_third_party_notices(app_dir: Path, version: str, vcpkg_inst: str | None) -> None:
    """生成 THIRD-PARTY-NOTICES.txt 落到包根目录。

    为什么不提交一份"现成的"声明文件进仓库：里面的版本号必须与**这次实际分发的 DLL**
    对应，而手抄的那份在升级 vcpkg 之后就悄悄过期了。所以每次打包都重新生成 ——
    优先现场读 vcpkg，读不到就退回仓库里冻结的许可证副本（正文是固定文本，版本另记）。
    两种来源都会在文件里如实标注，不假装是现场读的。

    直接 import 同目录的生成器（而不是 subprocess）：错误信息与退出码都能就地传播，
    失败就是打包失败，不存在"生成器挂了但包照样发出去"。
    """
    import sys as _sys
    _here = str(Path(__file__).resolve().parent)
    if _here not in _sys.path:
        # 以 `python winui3/tools/package_release.py` 方式运行时 sys.path[0] 已经是它，
        # 但被别的脚本 import 进来时不是 —— 显式补一次，别让生成器找不到。
        _sys.path.insert(0, _here)

    import gen_third_party_notices

    argv = ["--out", str(app_dir / "THIRD-PARTY-NOTICES.txt"), "--version", version]
    if vcpkg_inst:
        argv += ["--vcpkg-inst", vcpkg_inst]

    code = gen_third_party_notices.main(argv)
    if code != 0:
        raise SystemExit(
            "第三方声明生成失败 —— 拒绝产出一个缺少许可证副本的发布包。\n"
            "  本机：确认 vcpkg 在 <仓库>/../_tools/vcpkg，或用 --vcpkg-inst 指定。\n"
            "  CI：传 --vcpkg-inst .vcpkg/installed/x64-windows。"
        )


def _write_license(app_dir: Path, license_src: Path) -> None:
    """把本程序的许可证全文落到包根目录。

    GPL-3.0 §1 要求「分发目标代码时随附本许可证副本」—— 只在 README 里写一句
    "以 GPL-3.0 发布"不算履行义务：拿到 zip 的人必须能在包里读到条款正文。

    与 THIRD-PARTY-NOTICES.txt 的分工：那一份是**别人**的许可（FreeRDP 等要求随附），
    这一份是本程序自己的。

    落成 `LICENSE.txt` 而不是无扩展名的 `LICENSE`：最终用户双击就能用记事本打开。
    仓库根那份仍叫 `LICENSE` —— GitHub 靠这个名字识别许可证类型。
    """
    if not license_src.is_file():
        raise SystemExit(
            f"找不到许可证文件：{license_src}\n"
            "  仓库根的 LICENSE 缺失或被改名了 —— 缺了它就不该产出发布包"
            "（GPL-3.0 §1 要求随二进制分发许可证副本）。"
        )

    shutil.copyfile(license_src, app_dir / "LICENSE.txt")


# ---------------------------------------------------------------- 主流程

def main(argv: list[str] | None = None) -> int:
    _force_utf8_stdio()

    parser = argparse.ArgumentParser(description="收拢 WinUI 3 发布包")
    parser.add_argument("--build-dir", required=True, help="dotnet build 的输出目录")
    parser.add_argument("--publish-dir", help="dotnet publish 的输出目录（自包含时给）")
    parser.add_argument("--assets", required=True, help="仓库根的 assets 目录")
    parser.add_argument(
        "--agent-dir",
        default=None,
        help="会话代理的 dotnet publish 输出（src/Yomori.Agent），会铺到 <app>/agent；"
        "不给则包里没有代理载荷，安装版部署远程通道会失败",
    )
    parser.add_argument("--out", required=True, help="输出目录（会生成 <out>/app 与 <out>/<name>.zip）")
    parser.add_argument("--name", required=True, help="ZIP 文件名（不含扩展名）")
    parser.add_argument("--version", default="0.0.0", help="写进说明文件的版本号")
    parser.add_argument("--keep-pdb", action="store_true", help="保留 pdb 符号（排查崩溃用）")
    parser.add_argument("--no-zip", action="store_true", help="只铺目录，不压缩（本地调试用）")
    parser.add_argument(
        "--license",
        default=None,
        help="本程序的许可证文件（默认取仓库根的 LICENSE）",
    )
    parser.add_argument(
        "--vcpkg-inst",
        default=None,
        help="vcpkg 的 installed/x64-windows 目录（读第三方组件许可证原文用；"
        "不给则按 gen_third_party_notices.py 的默认规则找 <仓库>/../_tools/vcpkg）",
    )
    args = parser.parse_args(argv)

    build_dir = Path(args.build_dir).resolve()
    publish_dir = Path(args.publish_dir).resolve() if args.publish_dir else None
    agent_dir = Path(args.agent_dir).resolve() if args.agent_dir else None
    assets_dir = Path(args.assets).resolve()
    out_dir = Path(args.out).resolve()
    app_dir = out_dir / "app"

    # 默认按本脚本位置回推仓库根：<repo>/winui3/tools/package_release.py
    license_src = (
        Path(args.license).resolve()
        if args.license
        else Path(__file__).resolve().parents[2] / "LICENSE"
    )

    for path, label in ((build_dir, "build 输出"), (assets_dir, "assets")):
        if not path.is_dir():
            raise SystemExit(f"{label}目录不存在：{path}")
    if publish_dir is not None and not publish_dir.is_dir():
        raise SystemExit(f"publish 输出目录不存在：{publish_dir}")
    if agent_dir is not None and not agent_dir.is_dir():
        raise SystemExit(f"代理发布目录不存在：{agent_dir}")

    if app_dir.exists():
        shutil.rmtree(app_dir)
    app_dir.mkdir(parents=True)

    copied = 0
    # ⚠️ 顺序 + 黑名单要紧：build 先铺、publish 后铺（覆盖），且 build 里的
    #    exe / runtimeconfig / deps.json 一律不参与覆盖。
    #
    #    build 是 `dotnet build`（build.yml 里没带 -r win-x64）→ **框架依赖**产物：
    #      · Yomori.exe               = 框架依赖 apphost
    #      · Yomori.runtimeconfig.json 里写 "framework": Microsoft.NETCore.App 8.0.0
    #    publish 是 `--self-contained true -r win-x64` → 写的是 "includedFrameworks"。
    #
    #    早先的顺序是反的（publish 先、build 后），自包含的那份运行时配置被 build 版盖掉：
    #    System.Private.CoreLib.dll 还在（publish 铺的），旧自检只看它有没有 → 查不出来，
    #    CI 全绿，装完双击就弹 "You must install or update .NET to run this application"。
    #    （同一份代码本机 bin\x64\Release\...\ 与 ...\win-x64\ 两处的 runtimeconfig 就是
    #      framework / includedFrameworks 的区别，可以直接对比确认。）
    #
    #    现在：build 先铺（补 .pri / *.xbf / FreeRDP 运行库），publish 后铺（覆盖同名）。
    #    同名一律以 publish 为准 —— 那才是"自包含"的那一份。
    #    框架依赖模式（没给 --publish-dir）时不存在"publish 说了算"，那三个文件照常从 build 取。
    authoritative = _PUBLISH_AUTHORITATIVE if publish_dir is not None else None
    copied += _copy_tree(
        build_dir, app_dir, skip_junk=True, keep_pdb=args.keep_pdb, skip_names=authoritative
    )
    if publish_dir is not None:
        copied += _copy_tree(publish_dir, app_dir, skip_junk=False)

    # 资源目录：必须在 exe 同级（AppPaths 靠它认定程序根目录）
    copied += _copy_tree(assets_dir, app_dir / "assets", skip_junk=False)

    # 会话代理载荷：独立工程 src/Yomori.Agent 的发布产物，铺到 agent\。
    # 部署逻辑（RdpSessionService.DeployAgent）按 <BaseDir>\agent\Yomori.Agent.exe 找它，
    # 找到了就只搬这约 73MB 的纯后台程序，而不是整套 228MB 的主控端。
    if agent_dir is not None:
        copied += _copy_tree(agent_dir, app_dir / "agent", skip_junk=False)

    # 收尾裁剪：AI/ML/Widgets 载荷 + 用不到的语言目录（必须在 build / publish 都铺完之后）
    pruned_files, pruned_dirs = _prune_unused(app_dir)

    import datetime

    built_at = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
    package_kind = (
        "自包含包（.NET 运行时与 Windows App SDK 都已内置）—— 解压即可运行，不需要额外安装任何东西。"
        if publish_dir is not None
        else "框架依赖包 —— 需要目标机器已安装 .NET 8 运行时（Desktop Runtime 或 SDK 均可）。"
    )
    _write_help(app_dir, args.version, package_kind, built_at)
    _write_license(app_dir, license_src)
    _write_third_party_notices(app_dir, args.version, args.vcpkg_inst)

    _verify(
        app_dir,
        require_dotnet_runtime=publish_dir is not None,
        agent_included=agent_dir is not None,
    )

    total_mb = sum(f.stat().st_size for f in app_dir.rglob("*") if f.is_file()) / 1024 / 1024
    print(f"已铺好 {app_dir}")
    print(f"  文件 {sum(1 for f in app_dir.rglob('*') if f.is_file())} 个 / {total_mb:.1f} MB（拷贝 {copied} 次）")
    print(f"  裁掉用不到的载荷：{pruned_files} 个文件 / {pruned_dirs} 个语言目录")

    if args.no_zip:
        return 0

    zip_path = out_dir / f"{args.name}.zip"
    if zip_path.exists():
        zip_path.unlink()

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for item in sorted(app_dir.rglob("*")):
            if item.is_file():
                zf.write(item, Path(args.name) / item.relative_to(app_dir))

    zip_mb = zip_path.stat().st_size / 1024 / 1024
    print(f"已打包 {zip_path}（{zip_mb:.1f} MB）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
