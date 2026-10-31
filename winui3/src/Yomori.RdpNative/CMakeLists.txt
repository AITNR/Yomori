# Yomori.RdpNative — FreeRDP 3.32.0 桥接层（C ABI）
#
# 统一构建入口（推荐）：
#   pwsh winui3/tools/build_native.ps1
# 该脚本固定 vcpkg 提交、安装隔离的 x64-windows 依赖、应用 overlay port /
# native-deps/patches，然后调用本 CMakeLists 并做 PE 传递依赖验证。
#
# 直接 CMake 构建（CI / 调试）：
#   cmake -S winui3/src/Yomori.RdpNative -B <build> -G "Visual Studio 17 2022" -A x64 `
#         -DVCPKG_INST=<...>/installed/x64-windows
#   cmake --build <build> --config Release --parallel
#
# 依赖：vcpkg FreeRDP 3.32.0，features = client,ffmpeg，FFmpeg 必需运行组件为
#       avcodec/avutil/swscale/swresample；WITH_FFMPEG_HWACCEL=ON，
#       WITH_MEDIA_FOUNDATION=OFF，WITH_OPENH264=OFF。

cmake_minimum_required(VERSION 3.20)
project(Yomori.RdpNative C)

if(NOT DEFINED VCPKG_INST OR "${VCPKG_INST}" STREQUAL "")
  get_filename_component(VCPKG_INST
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../_tools/vcpkg/installed/x64-windows"
    ABSOLUTE)
else()
  file(TO_CMAKE_PATH "${VCPKG_INST}" VCPKG_INST)
  get_filename_component(VCPKG_INST "${VCPKG_INST}" ABSOLUTE)
endif()

set(YCN_VCPKG_COMMIT "10541e317a660f4165ba4ac2851ab54a8d4577b1" CACHE STRING "固定 vcpkg 提交")
set(YCN_VCPKG_TRIPLET "x64-windows" CACHE STRING "vcpkg triplet")
set(YCN_FREERDP_FEATURES "client;ffmpeg" CACHE STRING "FreeRDP vcpkg features")
set(YCN_EXPECTED_FREERDP_VERSION "" CACHE STRING "期望的 FreeRDP 版本（空 = 不校验）")
set(YCN_EXPECTED_FFMPEG_VERSION "" CACHE STRING "期望的 FFmpeg 版本（空 = 不校验）")
set(YCN_FFMPEG_COMPONENTS "avcodec;avutil;swscale;swresample" CACHE STRING "最小 FFmpeg 组件")
set(YCN_VERIFY_TOOL "${CMAKE_CURRENT_SOURCE_DIR}/../../tools/verify_native_runtime.py" CACHE FILEPATH "PE 依赖验证脚本")
set(YCN_PYTHON_EXECUTABLE "" CACHE FILEPATH "Python 解释器（用于完整 PE 依赖验证）")

message(STATUS "VCPKG_INST       = ${VCPKG_INST}")
message(STATUS "YCN vcpkg commit = ${YCN_VCPKG_COMMIT}")
message(STATUS "YCN triplet      = ${YCN_VCPKG_TRIPLET}")
message(STATUS "YCN features     = ${YCN_FREERDP_FEATURES}")

if(NOT EXISTS "${VCPKG_INST}/lib/freerdp3.lib")
  message(FATAL_ERROR "找不到 ${VCPKG_INST}/lib/freerdp3.lib —— 先运行 winui3/tools/build_native.ps1，或用 -DVCPKG_INST 指向已装好的 installed/x64-windows")
endif()

function(ycn_read_spdx_version out_var share_name)
  set(_json "${VCPKG_INST}/share/${share_name}/vcpkg.spdx.json")
  set(_version "unknown")
  if(EXISTS "${_json}")
    file(READ "${_json}" _text)
    string(REGEX MATCH "\"versionInfo\"[ \t\r\n]*:[ \t\r\n]*\"([^\"]+)\"" _match "${_text}")
    if(CMAKE_MATCH_1)
      set(_version "${CMAKE_MATCH_1}")
      string(REGEX REPLACE "#[0-9]+$" "" _version "${_version}")
    endif()
  endif()
  set(${out_var} "${_version}" PARENT_SCOPE)
endfunction()

ycn_read_spdx_version(YCN_FREERDP_VERSION freerdp)
ycn_read_spdx_version(YCN_FFMPEG_VERSION ffmpeg)

if(YCN_EXPECTED_FREERDP_VERSION AND NOT YCN_FREERDP_VERSION STREQUAL YCN_EXPECTED_FREERDP_VERSION)
  message(FATAL_ERROR "FreeRDP 版本 ${YCN_FREERDP_VERSION} != 期望 ${YCN_EXPECTED_FREERDP_VERSION}")
endif()
if(YCN_EXPECTED_FFMPEG_VERSION AND NOT YCN_FFMPEG_VERSION STREQUAL YCN_EXPECTED_FFMPEG_VERSION)
  message(FATAL_ERROR "FFmpeg 版本 ${YCN_FFMPEG_VERSION} != 期望 ${YCN_EXPECTED_FFMPEG_VERSION}")
endif()

# 按“组件前缀-任意 ABI 数字.dll”发现实际 DLL 名，避免把 avcodec-63 这类
# FFmpeg ABI 版本写死。守卫与发布包都使用这里发现的真实文件名。
set(YCN_FFMPEG_DLLS "")
foreach(_component IN LISTS YCN_FFMPEG_COMPONENTS)
  file(GLOB _component_dlls "${VCPKG_INST}/bin/${_component}-*.dll")
  if(NOT _component_dlls)
    message(FATAL_ERROR "缺少 FFmpeg 组件 ${_component}-*.dll（${VCPKG_INST}/bin）")
  endif()
  foreach(_dll IN LISTS _component_dlls)
    get_filename_component(_name "${_dll}" NAME)
    list(APPEND YCN_FFMPEG_DLLS "${_name}")
  endforeach()
endforeach()
list(REMOVE_DUPLICATES YCN_FFMPEG_DLLS)
list(SORT YCN_FFMPEG_DLLS)
message(STATUS "FFmpeg DLLs      = ${YCN_FFMPEG_DLLS}")

set(YCN_BASE_RUNTIME_DLLS
  freerdp3.dll
  freerdp-client3.dll
  winpr3.dll
  z.dll
  cjson.dll
  libssl-3-x64.dll
  libcrypto-3-x64.dll
  legacy.dll)

set(YCN_COPY_RUNTIME_DLLS ${YCN_BASE_RUNTIME_DLLS} ${YCN_FFMPEG_DLLS})
set(YCN_REQUIRED_DLLS ycn_rdp.dll ${YCN_COPY_RUNTIME_DLLS})

foreach(_dll IN LISTS YCN_COPY_RUNTIME_DLLS)
  if(NOT EXISTS "${VCPKG_INST}/bin/${_dll}")
    message(FATAL_ERROR "缺少运行库 ${VCPKG_INST}/bin/${_dll}")
  endif()
endforeach()

add_library(ycn_rdp SHARED ycn_rdp.c)
# 注意：不要定义 UNICODE/_UNICODE —— 会引发 freerdp rail.h 的 NIIF_* 与 shellapi.h 宏冲突
target_compile_definitions(ycn_rdp PRIVATE YCN_RDP_EXPORTS)
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

# ---- 能力信息（构建时快照，发布包和 CI 都会校验） -------------------------
set(_ffmpeg_dll_json "")
foreach(_dll IN LISTS YCN_FFMPEG_DLLS)
  if(_ffmpeg_dll_json)
    string(APPEND _ffmpeg_dll_json ",\n    ")
  endif()
  string(APPEND _ffmpeg_dll_json "\"${_dll}\"")
endforeach()

set(_runtime_dll_json "")
foreach(_dll IN LISTS YCN_REQUIRED_DLLS)
  if(_runtime_dll_json)
    string(APPEND _runtime_dll_json ",\n    ")
  endif()
  string(APPEND _runtime_dll_json "\"${_dll}\"")
endforeach()

set(_feature_json "")
foreach(_feature IN LISTS YCN_FREERDP_FEATURES)
  if(_feature_json)
    string(APPEND _feature_json ",\n    ")
  endif()
  string(APPEND _feature_json "\"${_feature}\"")
endforeach()

set(_component_json "")
foreach(_component IN LISTS YCN_FFMPEG_COMPONENTS)
  if(_component_json)
    string(APPEND _component_json ",\n    ")
  endif()
  string(APPEND _component_json "\"${_component}\"")
endforeach()

set(YCN_CAPABILITIES_FILE "${CMAKE_CURRENT_BINARY_DIR}/ycn_rdp.capabilities.json")
file(GENERATE OUTPUT "${YCN_CAPABILITIES_FILE}" CONTENT
"{
  \"schema\": 1,
  \"product\": \"Yomori.RdpNative\",
  \"generated_by\": \"winui3/src/Yomori.RdpNative/CMakeLists.txt\",
  \"vcpkg_commit\": \"${YCN_VCPKG_COMMIT}\",
  \"triplet\": \"${YCN_VCPKG_TRIPLET}\",
  \"freerdp_version\": \"${YCN_FREERDP_VERSION}\",
  \"freerdp_features\": [
    ${_feature_json}
  ],
  \"ffmpeg_version\": \"${YCN_FFMPEG_VERSION}\",
  \"ffmpeg_components\": [
    ${_component_json}
  ],
  \"ffmpeg_dlls\": [
    ${_ffmpeg_dll_json}
  ],
  \"runtime_dlls\": [
    ${_runtime_dll_json}
  ],
  \"ycn_h264_decode_api_version\": 1,
  \"ycn_h264_decode_capabilities_mask\": 7,
  \"build_options\": {
    \"WITH_FFMPEG\": true,
    \"WITH_VIDEO_FFMPEG\": true,
    \"WITH_FFMPEG_HWACCEL\": true,
    \"WITH_DSP_FFMPEG\": true,
    \"WITH_SWSCALE\": true,
    \"WITH_GFX_H264\": true,
    \"WITH_MEDIA_FOUNDATION\": false,
    \"WITH_OPENH264\": false
  },
  \"patches\": [],
  \"cache_key\": \"\"
}
")

# ---- 运行库部署 + 完整依赖守卫 -------------------------------------------
add_custom_command(TARGET ycn_rdp POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E echo "Deploying FreeRDP/FFmpeg runtime DLLs <- ${VCPKG_INST}/bin")

foreach(_dll IN LISTS YCN_COPY_RUNTIME_DLLS)
  add_custom_command(TARGET ycn_rdp POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${VCPKG_INST}/bin/${_dll}"
            "$<TARGET_FILE_DIR:ycn_rdp>/${_dll}")
endforeach()

add_custom_command(TARGET ycn_rdp POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
          "${YCN_CAPABILITIES_FILE}"
          "$<TARGET_FILE_DIR:ycn_rdp>/ycn_rdp.capabilities.json")

find_program(_ycn_python NAMES python3 python)
if(NOT YCN_PYTHON_EXECUTABLE AND _ycn_python)
  set(YCN_PYTHON_EXECUTABLE "${_ycn_python}")
endif()
if(YCN_PYTHON_EXECUTABLE AND NOT EXISTS "${YCN_VERIFY_TOOL}")
  message(WARNING "找不到 PE 验证脚本 ${YCN_VERIFY_TOOL}，POST_BUILD 只做 DLL 清单检查")
endif()

add_custom_command(TARGET ycn_rdp POST_BUILD
  COMMAND ${CMAKE_COMMAND}
          "-DYCN_OUT_DIR=$<TARGET_FILE_DIR:ycn_rdp>"
          "-DYCN_REQUIRED_DLLS=${YCN_REQUIRED_DLLS}"
          "-DYCN_FFMPEG_COMPONENTS=${YCN_FFMPEG_COMPONENTS}"
          "-DYCN_VERIFY_TOOL=${YCN_VERIFY_TOOL}"
          "-DYCN_PYTHON=${YCN_PYTHON_EXECUTABLE}"
          "-DYCN_EXPECT_HWACCEL=ON"
          -P "${CMAKE_CURRENT_SOURCE_DIR}/verify_runtime_dlls.cmake")
