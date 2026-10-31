# CPU 15% 反馈后的协商修正与采样（2026-10-31）

最新可用运行日志为 19:37 双路 1080p：实际 codec 为 8（ClearCodec）/9（Progressive），hw_init=0、hw_frames=0。旧版独立 H.264 测试成功，没有证明这次真实 RDP 会话采用了 H.264。两路每秒约 30–39 次更新、12–18 次呈现；15% 为用户观察，未获得同一时刻 CPU 栈采样。

## 本次改动

- 保留默认 GFX。对经能力探测的 AVC420 路径设置 GfxThinClient=true；仅写废弃的 GfxProgressive=false 不会表达同样能力。未强制过滤所有非 AVC codec，未开启未经验证的 AVC444，未修改服务器组策略。
- 修复 gfx_enabled 被 PreConnect/LoadChannels 的 TRUE 覆盖。关闭后实际取消 GFX 与 AVC，动态通道和音频意图保持可用，供相同画面的 A/B 对照。
- 诊断记录实际 CAPS_CONFIRM 版本/标志、按 codec 收到的命令数、硬解成功帧；服务器选非 AVC 后明确写 server-selected-non-avc，避免永久 pending。硬件曾经成功不等于当前仍硬解，hwactive 同时检查当前后端和恢复状态。
- 原生按 codec 累计 SurfaceCommand 耗时和编码负载字节，同时记录 EndPaint、copy_frame 的复制次数/字节/耗时。SurfaceCommand 含解码、合成及内嵌复制等嵌套操作，不能把它们相加当独立 CPU 时间；QPC 是墙钟时间，包含等待。
- 托管侧每 5 秒汇总抓帧、D3D 上传/Present、软位图同步提交、异步等待和 tick 的 avg/max 及字节数。逐帧不写日志、不创建计时对象；软等待时间也不是 CPU 消耗。
- SoftwareBitmap 路线复用同一拍已经取得的快照，取消第二次 TryCopyFrame。快照同步复制完成后才 await，避免复用数组跨 await 被覆盖；退休/世代不符分支仍严格清理渲染计数。
- 新增 profile_rdp_cpu.ps1，有界采集归一化进程 CPU；可在已有管理员 PowerShell 中加 -RecordStacks 抓取独立 WPR CPU 会话。脚本不提权、不启动程序、不重连、不修改画质。

## 验证结果

- 实际调用 FreeRDP 内建 rdpgfx DVC 的序列化/解析代码，用假传输捕获能力 PDU：8 个场景通过。包括生产设置 H264+ThinClient+SmallCache（8.1 flags=0x13），以及不开 ThinClient 的对照、能力过滤和 CAPS_CONFIRM 回调。
- 图形设置自检读回真实 rdpSettings：GFX false 时 pipeline=0、H264=0；自动路径 pipeline=1、ThinClient=1、H264=1；音频设置保持不变。错误版本被拒绝。
- DecodeChecks 7/7 通过，含计时窗口聚合、重置、D3D 失败计时和逐帧零分配检查。
- 原生桥接与 WinUI Release/x64 构建通过，保留既有 nullable 警告。
- 独立 DXVA2 解码仍通过；它只验证 decoder，不代替目标会话协商验收。

## 真实会话下一步

当前 Yomori 已退出，日志未再更新，无法重新采样 15% 或证明修正后服务器采用了 AVC。因此没有给出 CPU 降幅，也没有宣称真实会话硬解已经成功。

运行新构建，保持原来的两路动态画面至少 30 秒。保持 decode_backend=auto、render_backend=auto、gfx_enabled=true。检查新增日志：

1. gfx_cfg 的 pipeline/thin/h264 应均为 1。
2. caps 中 confirm/version/flags 是实际服务器确认值。
3. codec 11（AVC420）的计数与 hw_frames 持续增长，才说明真实硬解生效。codec 8/9 持续增长且 hw_frames=0，则仍为服务器选择的软件路径。
4. 根据每 5 秒阶段日志与下面 CPU 采样定位主要时间，不通过降低分辨率或帧率来声称硬解收益。

```powershell
./winui3/tools/profile_rdp_cpu.ps1 -DurationSeconds 30
# 同级管理员 PowerShell 可额外抓栈：
./winui3/tools/profile_rdp_cpu.ps1 -DurationSeconds 30 -RecordStacks
```

如需 GFX A/B，先记录默认开启结果，再在设置中关闭 GFX 并重连，以同分辨率、同画面与相同时长比较 CPU 和 FPS。开关关闭不保证更低 CPU；还要检查画质、输入与条带/撕裂。

## 本地试运行产物

可执行文件：`winui3/src/Yomori.App/build/cpu-review-20261031/Yomori.exe`。主程序和 native 输出中的 ycn_rdp.dll SHA-256 已核对一致。该程序为非自包含的本地构建，当前开发机可运行；未替换安装目录。原生 GFX 新入口 12 次连接失败与 1 次立即取消回归通过，发布目录运行依赖守卫通过。

## 20:17 后真实会话：AVC420 能力仍未被接受

新日志位于 `logs/2026-10-31(2).log`。pipeline/thin/h264 均为 1，但服务器确认 8.1 flags=0x03（清除了 AVC420 0x10），实际 codec=3、hw_frames=0。服务端 RdpCoreTS Event162 同样记录 Version0x80105、AvcEnabled0。不能把客户端设置为 h2641 当作已经进入硬解。

同机今天16:39等历史 Event162 为 Version0xA0701、AvcEnabled1；16:39与19:35的 ClientMode/Profile 相同，但 capability版本不同。这支持本机 8.1/10.x 兼容性假设，不证明所有 Windows 都拒绝8.1。未修改 AVC444ModePreferred 或显示驱动策略。

FreeRDP3.32只在 GfxAVC444=true 时为 H264 客户端广告10.x。此前首阶段固定 false 使客户端退回8.1，是本次需要纠正的取舍。现已通过 codec14/15 的 op1、op0真实双流软件/DXVA2解码与源图比较（约40dB PSNR，同版本软硬输出哈希一致），现代 auto/software+GFX路径启用 AVC444/v2；legacy/GFX-off继续nonAVC。仍未验证真实持续P帧和端到端会话。

实际生产能力包测试新增 modern 场景，合计9/9通过；最高广告版本10.7，flags0x42（没有AVC_DISABLED）。新增按会话 CapsAdvertise hook记录发送时设置、真实版本/flags及send返回码，与CapsConfirm、实际codec和hw_frames分开显示。--embed-avc420-only 可作进程级8.1回滚诊断，不改配置。

20:20的一次10秒进程CPU采样（实际6个计数点）平均4.561%、最高6.434%，按32逻辑CPU归一化；当时约一条动态一路空闲，不能与用户之前15%的不同负载直接比较，也不是CPU改善验收。当前动态画面呈现约28–30FPS，旧日志约15FPS，比较时必须同时记录输出FPS。复制/上传约各1ms一帧，不能据此断言15%都来自复制。

新试运行路径：`winui3/src/Yomori.App/build/modern-avc-review-20261031/Yomori.exe`。未覆盖正在运行的cpu-review目录，未关闭或重连用户的会话。需退出旧主控后用新构建重连同样两路，再看adv最高10.7、server confirm版本、AvcEnabled及hw_frames，之后比较同内容CPU/FPS。
