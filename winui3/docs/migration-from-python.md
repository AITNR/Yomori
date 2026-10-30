# 从 Python / PySide6 版移植过来的那些事

> **归档文档**。仓库已于 2026-09-28 移除 Python + PySide6 版（`main.py` / `core/` / `ui/`），
> 本文只作为移植记录保留，不再随代码更新。需要旧代码可从移除前的 Git 历史取回
> （`git checkout <移除前的提交> -- main.py core ui`）。
>
> 想了解现在的程序，看 [../README.md](../README.md) 与 [../USER_GUIDE.md](../USER_GUIDE.md)。

---

## 一、为什么要重写

原 Python 版约 5600 行，界面层基于 PySide6（Qt），存在几个绕不开的问题：

| 问题 | 说明 |
|---|---|
| 分发体积 | PyInstaller 打包后体积大，冷启动慢 |
| 主题割裂 | Qt 自定义皮肤与 Windows 11 系统主题、Mica、亚克力、系统强调色无法统一 |
| DPI 表现 | 高 DPI / 多显示器缩放下偶发错位 |
| 依赖链 | Python + PySide6 + psutil 运行时依赖，用户环境差异大 |

改成 C# + Windows App SDK（WinUI 3）之后：

- 原生 Fluent Design，直接吃到 **Mica 背景、系统主题、系统强调色、圆角、阴影**
- 自定义标题栏（`ExtendsContentIntoTitleBar`），无 Qt 那种边框割裂感
- `PerMonitorV2` 高 DPI，多显示器拖拽不糊不错位
- 单一 exe 直接运行（unpackaged 模式），无需 Python 运行时
- 进程/窗口监控直接用 Win32 P/Invoke，少一层 Python ↔ 系统调用开销

---

## 二、移植对照表

### 核心逻辑

| Python（原版） | C# / WinUI 3（新版） | 说明 |
|---|---|---|
| `core/core.py` | `Services/ScriptRunner.cs` | 任务执行引擎全量移植 |
| `core/runtime/runtime_watchdog.py` | `Services/ScriptRunner.cs` + `ProcessHelper.cs` | 监控循环内联合并 |
| `ui/main_window.py` | `Views/MainWindow.xaml(.cs)` + `ViewModels/MainViewModel.cs` | 拆分为视图 + 视图模型 |
| `ui/cards.py` | `Views/HomePage.xaml` | 卡片式首页 |
| `ui/card_scene.py` | `Views/MascotPanel.xaml` + `Services/MascotService.cs` | 看板娘面板 |
| `config.json` | `Models/AppConfig.cs` / `TaskConfig.cs` | 键名完全一致，双向兼容 |
| `app_dir()` 路径回溯 | `Services/AppPaths.cs` | 同样向上回溯找 `config.json` / `assets` |
| `shlex.split(posix=False)` | `CommandLine.Split` | 用 `CommandLineToArgvW` 实现等价语义 |
| `subprocess` + `runas` | `Services/MonitoredProcess.cs` | `ShellExecuteExW(runas)` 提权 |

### 界面

| 原 Qt 组件 | WinUI 3 对应 |
|---|---|
| `QMainWindow` | `Window` + `NavigationView` + `Frame` |
| `QListWidget` | `ListView` |
| `QStackedWidget` | `Frame.Navigate` |
| `QGroupBox` | `Expander` |
| `QSpinBox` / `QDoubleSpinBox` | `NumberBox` |
| `QCheckBox` | `ToggleSwitch` / `CheckBox` |
| `QMessageBox` | `ContentDialog` |
| `QSystemTrayIcon` 提示 | `InfoBar` |
| 自定义标题栏 | `ExtendsContentIntoTitleBar` + `SetTitleBar` |
| — | **新增**：`MicaBackdrop` 背景 |

### 主题

原 6 套 Qt 皮肤（`yukino` / `campus` / `fresh` / `fantasy` …）收敛为 WinUI 3 原生三档：

| 值 | 含义 |
|---|---|
| `system` | 跟随系统（默认） |
| `light` | 浅色 |
| `dark` | 深色 |

旧配置里的 `yukino` / `campus` / `fresh` / `fantasy` 仍会被识别为合法值并平滑降级为 **跟随系统**，不会报错、不会被强制改写。

---

## 三、已完整移植的执行语义

以下行为与原 Python 版 **逐条对齐**，不是"看起来差不多"：

**监控关键词优先级**

```text
window_keywords  >  process_keywords  >  legacy wait_mode  >  direct_process
```

**三层清理顺序**（超时 / 停止 / 紧急停止共用）

```text
启动脚本进程  →  目标进程关键词  →  游戏/扩展进程
```

**并发组**

- `wait_all`：等组内全部任务完成
- `wait_first`：组内任一完成即推进

**超时动作**

- `kill_and_continue`：杀进程后继续下一项
- `skip_and_continue`：不杀进程，直接下一项
- `stop_all`：停止整条任务链

**监控循环**

| 参数 | 值 |
|---|---|
| 监控目标连续消失宽限 | 15 秒 |
| 监控目标出现截止 | 180 秒 |
| 启动成功确认窗口 | 2 秒 |

**看板娘状态机**

- 状态：`idle` / `work` / `rest` / `error`
- `error` 锁定：`15 秒 × 错误次数` 叠加后自动释放
- 气泡优先级：`error 100` > `work 70` > `rest 50` > `guide 30` > `idle 20`
- 素材目录：`assets/mascot/{state}.png` 或 `{state}_*.png`

**日志**

- 单文件上限按启动批次自动分配 `2026-05-26.log` / `(2)` / `(3)` …
- 会话标记：`===== SESSION START : 任务名 =====` / `===== SESSION END : 任务名 =====`

**其他**

- 超时现场截图：`logs/screenshots/{session}_{HHmmss}_{task}_timeout_{before,after}_kill.png`
- 异常报告：`logs/last_abnormal_report.json`，下次启动在首页 InfoBar 主动汇报
- 开机自启动：HKCU `Software\Microsoft\Windows\CurrentVersion\Run`，值名「Yomori / 二游脚本助手」
- 自动关机：`shutdown /s /t {delay}`，倒计时对话框可取消（`shutdown /a`）
- 快捷键：`F8` 停止执行，`Ctrl+Alt+F8` 紧急停止

---

## 四、沿用下来的数据目录约定

Python 版与 WinUI 3 版原先共用同一份数据目录，路径规则被 WinUI 3 版完整继承
（`AppPaths.ResolveBaseDir()` 向上回溯到有 `config.json` / `assets/` 的那一层）：

| 用途 | 路径 | 说明 |
|---|---|---|
| 配置文件 | `config.json` | 任务、通道、窗口几何都在这一份里 |
| 日志目录 | `logs/` | 按日期分文件 |
| 统计目录 | `runtime_stats/` | 脚本耗时历史，按账户分家 |
| 看板娘素材 | `assets/mascot/` | 缺失时看板娘区域留空，程序仍能跑 |
