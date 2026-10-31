# Verify the native runtime set after every ycn_rdp build.
#
# Two layers:
#   1. deterministic file inventory (the original eight DLLs + every FFmpeg
#      component by <name>-*.dll, never a hard-coded ABI number);
#   2. a real PE import walk + FFmpeg HWACCEL evidence check via
#      winui3/tools/verify_native_runtime.py.
#
# Inputs:
#   YCN_OUT_DIR           target output directory
#   YCN_REQUIRED_DLLS     semicolon-separated required DLL names (includes ycn_rdp.dll)
#   YCN_FFMPEG_COMPONENTS avcodec;avutil;swscale;swresample
#   YCN_VERIFY_TOOL       path to verify_native_runtime.py
#   YCN_PYTHON            Python interpreter (optional)
#   YCN_EXPECT_HWACCEL    ON/OFF

if(NOT DEFINED YCN_OUT_DIR)
  message(FATAL_ERROR "verify_runtime_dlls: 缺少 YCN_OUT_DIR")
endif()
if(NOT DEFINED YCN_REQUIRED_DLLS)
  message(FATAL_ERROR "verify_runtime_dlls: 缺少 YCN_REQUIRED_DLLS")
endif()

set(missing "")
foreach(name IN LISTS YCN_REQUIRED_DLLS)
  if(NOT EXISTS "${YCN_OUT_DIR}/${name}")
    list(APPEND missing "${name}")
  endif()
endforeach()

if(DEFINED YCN_FFMPEG_COMPONENTS)
  foreach(component IN LISTS YCN_FFMPEG_COMPONENTS)
    file(GLOB component_dlls "${YCN_OUT_DIR}/${component}-*.dll")
    if(NOT component_dlls)
      list(APPEND missing "${component}-*.dll")
    endif()
  endforeach()
endif()

if(missing)
  list(JOIN missing ", " joined)
  message(FATAL_ERROR
    "原生层运行库不完整，缺少：${joined}\n"
    "ycn_rdp.dll 会因缺依赖而加载失败。\n"
    "请用 winui3/tools/build_native.ps1 重新安装固定依赖并编译。")
endif()

# OS/GPU driver DLLs must come from Windows, never from the package.
set(forbidden_system_dlls
  d3d11.dll dxgi.dll d3d12.dll d3dcompiler_47.dll
  nvapi64.dll nvapi.dll nvcuda.dll nvngx.dll
  atiadlxx.dll amdvlk64.dll vulkan-1.dll opengl32.dll
  kernel32.dll user32.dll gdi32.dll advapi32.dll)
set(bad "")
foreach(name IN LISTS forbidden_system_dlls)
  if(EXISTS "${YCN_OUT_DIR}/${name}")
    list(APPEND bad "${name}")
  endif()
endforeach()
if(bad)
  list(JOIN bad ", " joined)
  message(FATAL_ERROR "原生层目录混入了系统/GPU 驱动 DLL，不应打包：${joined}")
endif()

if(YCN_PYTHON AND YCN_VERIFY_TOOL AND EXISTS "${YCN_VERIFY_TOOL}")
  set(_verify_args
    "${YCN_VERIFY_TOOL}"
    --dir "${YCN_OUT_DIR}"
    --require-ffmpeg
    --forbid-system-gpu
    --allow-vc-runtime-external)
  if(YCN_EXPECT_HWACCEL)
    list(APPEND _verify_args --expect-hwaccel)
  endif()

  execute_process(
    COMMAND "${YCN_PYTHON}" ${_verify_args}
    RESULT_VARIABLE _verify_rc
    OUTPUT_VARIABLE _verify_out
    ERROR_VARIABLE _verify_err)
  if(NOT _verify_rc EQUAL 0)
    message(FATAL_ERROR
      "PE 传递依赖 / FFmpeg 能力验证失败（exit ${_verify_rc}）：\n"
      "${_verify_out}\n${_verify_err}")
  endif()
  message(STATUS "${_verify_out}")
else()
  message(WARNING
    "未找到 Python 或 verify_native_runtime.py；只完成了 DLL 清单检查，"
    "没有验证实际 PE 传递依赖。请用 winui3/tools/build_native.ps1 构建。")
endif()

message(STATUS "原生层运行库校验通过（${YCN_OUT_DIR}）")
