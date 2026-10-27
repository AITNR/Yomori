# Yomori.UuFix.Native — UU 远程会话修复的注入载荷
#
# 构建（本机实测可用的方式）：
#   设好 MSVC 的 INCLUDE / LIB / PATH 后直接调 cl：
#     cl /nologo /LD /O2 /utf-8 /W3 uu_session_fix.c \
#        /link /INCREMENTAL:NO /OUT:build/Release/uu_session_fix.dll
#   注意 Git Bash 下需 export MSYS_NO_PATHCONV=1，否则 /nologo 会被当成路径改写。
#
# 产物：build/Release/uu_session_fix.dll
#
# ⚠️ 本机的 CMake 探测不到 VS 实例（BuildTools 注册不全），
#    `cmake -G "Visual Studio 17 2022"` 会报 No CMAKE_C_COMPILER，
#    但 cl.exe 实际存在。下面的 CMake 定义保留给环境正常的机器 / CI 使用。
#
# 该 DLL 被注入到 UU 的 GameViewerService 进程，因此必须保持为零外部依赖：
# 不链 FreeRDP、不链 Qt、不链任何 vcpkg 组件 —— 只依赖 kernel32 的导出。

cmake_minimum_required(VERSION 3.20)
project(Yomori.UuFix.Native C)

add_library(uu_session_fix SHARED uu_session_fix.c)

# 源码含 UTF-8 中文注释，MSVC 默认按 CP936 解析会啃坏注释
target_compile_options(uu_session_fix PRIVATE /utf-8)

# 必须用静态 CRT（/MT）：该 DLL 会被注入到别人的进程里，依赖 vcruntime140.dll
# 会和宿主的运行库版本打架，表现是加载阶段就崩、且拿不到任何日志。
target_compile_options(uu_session_fix PRIVATE /MT)
set_target_properties(uu_session_fix PROPERTIES
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# 注入载荷要尽量小：关掉增量链接的中间产物即可，优化级别保持默认
set_target_properties(uu_session_fix PROPERTIES
  PREFIX ""
  LINK_FLAGS "/INCREMENTAL:NO")

