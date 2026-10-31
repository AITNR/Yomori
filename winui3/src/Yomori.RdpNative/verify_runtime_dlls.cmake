# 校验 ycn_rdp.dll 的运行时依赖是否齐全。
#
# 由 CMakeLists.txt 的 POST_BUILD 调用。存在的意义见 CMakeLists.txt 里的长注释：
# FreeRDP 是动态链接的，只拷 freerdp3/winpr3/freerdp-client3 三个 DLL 不足以运行；
# 还必须有 z.dll / cjson.dll / libssl-3-x64.dll / libcrypto-3-x64.dll。
# 缺任何一个，应用会在「开始执行」时 DllNotFoundException 卡死。
#
# 入参：
#   YCN_OUT_DIR      —— 原生产物输出目录（$<TARGET_FILE_DIR:ycn_rdp>）
#   YCN_RUNTIME_DLLS —— 分号分隔的必需 DLL 名列表

if(NOT DEFINED YCN_OUT_DIR)
  message(FATAL_ERROR "verify_runtime_dlls: 缺少 YCN_OUT_DIR")
endif()

if(NOT DEFINED YCN_RUNTIME_DLLS)
  message(FATAL_ERROR "verify_runtime_dlls: 缺少 YCN_RUNTIME_DLLS")
endif()

set(missing "")
foreach(name IN LISTS YCN_RUNTIME_DLLS)
  # 名字里带 * 的按通配处理：FFmpeg 的 DLL 带 major 版本号后缀
  # （avcodec-63.dll 之类），写死会在升级 FFmpeg 时误报。
  if(name MATCHES "\\*")
    file(GLOB _hits "${YCN_OUT_DIR}/${name}")
    if(NOT _hits)
      list(APPEND missing "${name}")
    endif()
  elseif(NOT EXISTS "${YCN_OUT_DIR}/${name}")
    list(APPEND missing "${name}")
  endif()
endforeach()

if(missing)
  list(JOIN missing ", " joined)
  message(FATAL_ERROR
    "原生层运行库不完整，缺少：${joined}\n"
    "ycn_rdp.dll 会因缺依赖而加载失败，应用点「开始执行」会直接卡死。\n"
    "请把 -DVCPKG_INST 指向**完整**的 freerdp[client,ffmpeg]:x64-windows 安装目录\n"
    "（其 bin 下应同时含 freerdp3 / winpr3 / freerdp-client3 / z / cjson / openssl\n"
    " 以及 avcodec-* / avutil-* / swscale-* / swresample-*），\n"
    "重新 cmake 配置并编译。")
endif()

message(STATUS "原生层运行库校验通过（${YCN_OUT_DIR}）")
