# H.264 解码测试帧

`h264-idr-128x128.h264` 为本任务自行生成的 128×128 彩条与渐变测试图，经本机 FFmpeg 4.4.4 的 libopenh264 编码得到的单帧 Annex B 数据（SPS/PPS/IDR，constrained baseline、yuv420p、无 B 帧）。不包含第三方图片、音视频或用户画面。

SHA-256：`ef384c2d3167bd3a040d5e71a67e384b2a20c184dce5fa33d589b191cb567130`。

用于验证软件/硬件解码均能产生有效多色输出及真实成功帧计数。它不代表完整 RDP 会话、AVC444 或多路性能验收。
