/* ============================================================
 * Yomori.RdpNative — 直显 surface 探测（S0 只读取证）
 *
 * 目的：回答“这条解码 surface 能不能被 GPU 零拷贝访问”，而不是推断。
 * 输入是解码器发布的 surface 平面指针（D3D11 texture / D3D9 surface），
 * 输出写进 ycn_direct_surface_info 的 verdict/status 字段。
 *
 * 纪律：
 *   - 一次会话只探一次（按 generation），稳态零成本；
 *   - 只读探测，不改解码器、不挂载、不影响 GDI 绘制；
 *   - 任何一步失败都只记录 HRESULT/状态，不把异常抛回解码线程。
 * ============================================================ */
#ifndef YCN_DIRECT_PROBE_H
#define YCN_DIRECT_PROBE_H

#include <stdint.h>

#include "ycn_rdp.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 用 plane[] 描述的硬件 surface 填充 out 的 verdict 与 *_status 字段。
 * stage 取 YCN_DIRECT_STAGE_*；plane 语义与解码器一致：
 *   D3D11：plane[0] = ID3D11Texture2D*，plane[1] = array slice
 *   DXVA2：plane[3] = IDirect3DSurface9*
 * out 的其它字段（尺寸、格式名、字节计数）由调用方填好，本函数不覆盖。 */
void ycn_direct_probe_surface(ycn_direct_surface_info* out, uint32_t stage,
                              const void* const plane[4]);

/* 释放探测过程保留的设备（进程退出或不再探测时调用；可重复调用）。 */
void ycn_direct_probe_shutdown(void);

#ifdef __cplusplus
}
#endif

#endif /* YCN_DIRECT_PROBE_H */
