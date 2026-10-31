# overlay triplet：在官方 x64-windows 基础上修正 host 工具的查找方式
#
# 为什么需要：
#
#   vcpkg 会把 CMAKE_FIND_ROOT_PATH 设成 installed/<triplet> 目录（目标库的根），
#   CMake 据此认为在做 cross-compiling。当 CMAKE_FIND_ROOT_PATH_MODE_PROGRAM 不是
#   NEVER 时，find_program 会把 PATH 里的每一项都拼到 root 前缀之后再去查：
#
#     PATH 里:  C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64
#     实际查:   .../installed/x64-windows/C:/Program Files (x86)/Windows Kits/...
#
#   于是 rc.exe / mt.exe 永远找不到（构建日志里 candidate_directories 全是带
#   installed/x64-windows 前缀的怪路径）。表现是连 cl 编译器的最小测试都过不去：
#
#     --rc=rc --mt=CMAKE_MT-NOTFOUND
#     RC Pass 1: command "rc /fo .../manifest.res ..." failed
#
#   这会拦住 vcpkg 里所有走 CMake 的 port。设成 NEVER 后，find_program 只按真实
#   PATH 查找 host 工具，与「找目标库时仍用 root path」并不冲突（那是 MODE_LIBRARY）。
#
# 注意：本文件与官方 triplet 同名，必须用 --overlay-triplets 指过来才会生效；
# 它改变 triplet ABI，首次使用会重建已装依赖（cjson / openssl / zlib 等）。
# 这是有意的：此前那些包是在环境正常时编好的，重建一次不影响产物质量。

set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE dynamic)
set(VCPKG_PROVIDED_FORTRAN ON)

# 关键：不要让 find_program 把 host 工具路径重定位到 installed/<triplet> 下面。
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)

# 第二个坑：vcpkg 重建环境时会丢掉 Windows SDK 的 include / lib 目录（它靠 reg.exe
# 读注册表还原系统环境，reg 被拦就没还原）。表现是链接阶段找不到系统库：
#
#     LINK : fatal error LNK1104: 无法打开文件“kernel32.lib”
#
# 显式钉死 CMAKE_SYSTEM_VERSION 不行 —— CMake 仍要靠注册表找该版本 SDK 的目录。
# 直接把 SDK 的 include / lib 塞进环境变量，等价于 vcvarsall 做的事。
# 版本号取本机实际安装的最高版本（与 tools/build_deps.ps1 的探测保持一致）。
set(_ycn_sdk_root "C:/Program Files (x86)/Windows Kits/10")
set(_ycn_sdk_ver "10.0.26100.0")

if(EXISTS "${_ycn_sdk_root}/Lib/${_ycn_sdk_ver}/um/x64")
    set(CMAKE_SYSTEM_VERSION "${_ycn_sdk_ver}")

    set(ENV{INCLUDE} "$ENV{INCLUDE};${_ycn_sdk_root}/Include/${_ycn_sdk_ver}/ucrt;${_ycn_sdk_root}/Include/${_ycn_sdk_ver}/um;${_ycn_sdk_root}/Include/${_ycn_sdk_ver}/shared;${_ycn_sdk_root}/Include/${_ycn_sdk_ver}/winrt")
    set(ENV{LIB} "$ENV{LIB};${_ycn_sdk_root}/Lib/${_ycn_sdk_ver}/ucrt/x64;${_ycn_sdk_root}/Lib/${_ycn_sdk_ver}/um/x64")
endif()
