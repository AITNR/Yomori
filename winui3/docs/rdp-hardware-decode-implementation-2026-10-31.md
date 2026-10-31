# 内嵌硬解首阶段实施与验证（2026-10-31）

本次完成硬解首阶段代码和本机构建验证。默认 `decode_backend=auto`，优先 FFmpeg/DXVA2 解码 AVC420；`software` 强制禁用视频硬解。`render_backend=software`、`--embed-nod3d` 也强制软件解码。旧 DLL 缺版本化导出时保留原非 AVC 连接入口，原参数和回调结构布局未变。

新增 FreeRDP 补丁提供按上下文的策略、真实硬解成功帧计数、诊断结构大小/版本检查，以及一次软件 decoder 恢复。初始化失败时软件回退；运行错误后丢弃失败包、请求刷新并等待关键帧，5 秒未恢复则明确报错断开。没有加入任务重发、远端注销或无限自动重连。AVC444 首版关闭，保留原 CPU BGRA 合成与显示管线；GPU 表面直显和复制优化未实施。

## 代码审查修正

- 原始 H264 HW_ACCEL 查询只回请求值：新增独立成功帧计数，设备创建成功不计作硬解。
- 原 GFX 回调原函数指针为全局：改为每会话保存。
- 强制软件不读取硬解专用环境覆盖，避免无效覆盖令软件模式也失败。
- 硬件初始化后续失败补软件初始化重试；失败初始化释放部分 FFmpeg 对象。
- 软件恢复不能盲目重放 P 帧：请求刷新，等待后续关键帧且有超时。
- OpenSSL legacy provider 不在 PE 导入表：显式打包 `legacy.dll`，并在干净进程测试 MD4/RC4。
- 统一脚本/CI 的缓存覆盖依赖、补丁、源码、测试帧及验证脚本；缓存命中也执行干净加载和软件解码自检。
- 干净 FreeRDP 源码存在 MSVC 不能接受的末尾空标签，单独补丁修正，避免依赖开发机已有源码修改。

## 已完成验证

| 检查 | 结果 |
| --- | --- |
| 固定 manifest/overlay 从干净源码构建 FreeRDP 3.32.0 + FFmpeg 9.0.2（Debug/Release） | 通过 |
| 统一脚本构建桥接与完整运行依赖守卫 | 通过 |
| WinUI 主程序 Release/x64 | 通过，最终增量构建 0 警告、0 错误；先前完整构建有既有 nullable 警告 |
| DecodeChecks | 6/6 通过 |
| 原生旧入口、自动入口、软件入口的连接失败/取消生命周期 | 各 12 次失败连接 + 1 次立即取消通过；终态恰好一次，无会话表泄漏 |
| 诊断 ABI | 152 字节 v1；零大小/错误版本被拒绝 |
| 软件 128×128/640×360 AVC420 解码 | 有效多色输出；hw_init=0，hw_frames=0，sw_frames 增加 |
| DXVA2 128×128/640×360 | hw_frames 增加且实际输出与软件 BGRA 哈希一致 |
| D3D11VA 故障 | 设备初始化成功但解码失败，hw_frames=0；软件恢复输出通过，未误记硬解成功 |
| 无效硬解后端环境覆盖 | auto 可回退，software 仍可创建软件 decoder |
| 发布目录 | PE 依赖闭包、FFmpeg HWACCEL 导入、真实补丁导出、MD4/RC4、最小 PATH 干净加载均通过 |
| Python/PowerShell/YAML 语法和 Git diff 空白 | 通过 |

既有 `Yomori.LogChecks` 在实施前基线即因未引用 TaskTypeModels/AgentVersionInfo 等源码出现 CS0103；未扩大本次修改去修旧工程。新增 DecodeChecks 独立链接生产代码。

## 结果边界

测试使用自行生成的单帧 SPS/PPS/IDR AVC420 数据，验证了本机真实 GPU 解码与故障软件恢复，未登录真实目标 RDP 会话。服务器最终协商 codec、音频/输入、全屏/切页、多路持续运行和 CPU 降幅仍需真实会话验收。硬解后的 GPU→CPU 回读、YUV 转换、GDI 合成和多次复制仍存在，不能将本次结果解释为整体 CPU 已下降某个百分比。

本地正式验证产物位于 `winui3/src/Yomori.App/build/hardware-decode-review-final/`，原生依赖位于 `winui3/src/Yomori.RdpNative/build/Release/`。均为构建目录，不纳入 Git。未改安装目录、用户配置或服务器策略，未提交、推送或发布。
