# Yomori.RdpNative — FreeRDP 桥接层（C ABI，计划书 §4）
#
# 构建（同 spike）：
#   cmake -S . -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
# 产物：build/Release/ycn_rdp.dll（+ 运行时 DLL 全量拷贝）
#
# 依赖：vcpkg FreeRDP 3.32.0（本机默认 <仓库>/../_tools/vcpkg，纯 ASCII 路径）
#       可用 -DVCPKG_INST=<路径> 覆盖（CI 就是这么用的）
#
# ⚠️ 覆盖必须先判空再取默认值：`get_filename_component` 会**无条件**覆盖同名变量，
#    早先那种直接赋值写法会把 -DVCPKG_INST 悄悄吃掉，于是 CI 上永远去啃那条本机路径
#    （D:/a/_tools/vcpkg/...）并报「找不到 freerdp3.lib」。

cmake_minimum_required(VERSION 3.20)
project(Yomori.RdpNative C)

if(NOT DEFINED VCPKG_INST OR "${VCPKG_INST}" STREQUAL "")
  get_filename_component(VCPKG_INST
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../_tools/vcpkg/installed/x64-windows"
    ABSOLUTE)
else()
  # 归一化：CI 传进来的可能是 Windows 反斜杠路径
  file(TO_CMAKE_PATH "${VCPKG_INST}" VCPKG_INST)
  get_filename_component(VCPKG_INST "${VCPKG_INST}" ABSOLUTE)
endif()

message(STATUS "VCPKG_INST = ${VCPKG_INST}")

if(NOT EXISTS "${VCPKG_INST}/lib/freerdp3.lib")
  message(FATAL_ERROR "找不到 ${VCPKG_INST}/lib/freerdp3.lib —— 先在 vcpkg 里安装 freerdp[client]:x64-windows，或用 -DVCPKG_INST 指到已装好的 installed/x64-windows")
endif()

add_library(ycn_rdp SHARED ycn_rdp.c)
# 注意：不要定义 UNICODE/_UNICODE —— 会引发 freerdp rail.h 的 NIIF_* 与 shellapi.h 宏冲突
target_compile_definitions(ycn_rdp PRIVATE YCN_RDP_EXPORTS)

# 源码含 UTF-8 中文注释，MSVC 默认 CP936 会啃坏
target_compile_options(ycn_rdp PRIVATE /utf-8)

target_include_directories(ycn_rdp PRIVATE
  "${VCPKG_INST}/include"
  "${VCPKG_INST}/include/freerdp3"
  "${VCPKG_INST}/include/winpr3")

target_link_libraries(ycn_rdp PRIVATE
  "${VCPKG_INST}/lib/freerdp-client3.lib"
  "${VCPKG_INST}/lib/freerdp3.lib"
  "${VCPKG_INST}/lib/winpr3.lib"
  ws2_32.lib)

# 运行时 DLL 拷到输出目录（freerdp3/winpr3/openssl/zlib/cjson…）
# ⚠️ 只拷 freerdp3 / winpr3 / freerdp-client3 是不够的：它们是**动态链接**的，
#    还依赖同目录下的 z.dll、cjson.dll、libssl-3-x64.dll、libcrypto-3-x64.dll。
#    2026-10-09：本机 -DVCPKG_INST 指向的 SDK bin 里只有那三个 DLL，copy_directory
#    照抄了这份不完整集合，于是运行/发布目录缺依赖 → ycn_rdp.dll 加载即
#    DllNotFoundException，点「开始执行」直接卡死。
#    所以拷完必须显式校验依赖齐全，缺一个就让构建失败 —— 绝不静默产出会崩的包。
set(YCN_RUNTIME_DLLS
  ycn_rdp.dll
  freerdp3.dll
  freerdp-client3.dll
  winpr3.dll
  z.dll
  cjson.dll
  libssl-3-x64.dll
  libcrypto-3-x64.dll
  # FFmpeg —— GFX 通道的 H.264 解码后端（overlay port 开 WITH_FFMPEG_HWACCEL）。
  # 缺了它们，WITH_GFX_H264 会是 OFF，客户端不声明 AVC caps，服务端退回 GDI 全帧，
  # 两路 1080p 的 CPU 会重新飙到 350%+ 单核。
  # 名字带 FFmpeg major 版本号（avcodec-63.dll 之类），故用通配，见 verify_runtime_dlls.cmake。
  avcodec-*.dll
  avutil-*.dll
  swscale-*.dll
  swresample-*.dll)

add_custom_command(TARGET ycn_rdp POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E echo "Deploying FreeRDP runtime DLLs <- ${VCPKG_INST}/bin"
  COMMAND ${CMAKE_COMMAND} -E copy_directory "${VCPKG_INST}/bin" "$<TARGET_FILE_DIR:ycn_rdp>"
  COMMAND ${CMAKE_COMMAND}
          "-DYCN_OUT_DIR=$<TARGET_FILE_DIR:ycn_rdp>"
          "-DYCN_RUNTIME_DLLS=${YCN_RUNTIME_DLLS}"
          -P "${CMAKE_CURRENT_SOURCE_DIR}/verify_runtime_dlls.cmake")
