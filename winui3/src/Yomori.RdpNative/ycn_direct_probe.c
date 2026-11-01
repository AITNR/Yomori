/* ============================================================
 * Yomori.RdpNative — 直显 surface 探测实现（S0）
 *
 * 只回答一个问题：解码器交出来的这块 surface，GPU 侧能不能直接消费？
 *   - D3D11（D3D11VA）：在**解码器自己的** device 上尝试建 VideoProcessor
 *     输入视图 / SRV；再试跨设备共享句柄（同适配器）。
 *   - DXVA2（D3D9）：查设备是否 D3D9Ex、纹理是否可共享；不解的就不硬凑。
 *
 * 这里的所有调用都是只读的：不创建 swapchain、不挂 presenter、不改解码器。
 * 设备按适配器 LUID 缓存，避免每次探测重建（会话切换不重复付初始化成本）。
 * ============================================================ */
#include "ycn_direct_probe.h"

#include <string.h>

#include <windows.h>
#include <d3d11_1.h>
#include <d3d9.h>
#include <dxgi1_2.h>

#define YCN_MAX_PROBE_DEVICES 2

typedef struct
{
	int used;
	LUID luid;
	ID3D11Device* device;
} YcnProbeDevice;

static YcnProbeDevice g_probe_devices[YCN_MAX_PROBE_DEVICES];
static CRITICAL_SECTION g_probe_lock;
static INIT_ONCE g_probe_once = INIT_ONCE_STATIC_INIT;

#define YCN_SAFE_RELEASE(p)          \
	do                               \
	{                                \
		if (p)                       \
		{                            \
			(p)->lpVtbl->Release(p); \
			(p) = NULL;              \
		}                            \
	} while (0)

static void ycn_set_text(char* dst, size_t size, const char* src)
{
	size_t len;
	if (!dst || (size == 0))
		return;
	if (!src)
		src = "";
	len = strlen(src);
	if (len >= size)
		len = size - 1;
	memcpy(dst, src, len);
	dst[len] = '\0';
}

static BOOL CALLBACK ycn_probe_init_once(PINIT_ONCE once, PVOID param, PVOID* context)
{
	(void)once;
	(void)param;
	(void)context;
	InitializeCriticalSection(&g_probe_lock);
	return TRUE;
}

static void ycn_probe_ensure_lock(void)
{
	InitOnceExecuteOnce(&g_probe_once, ycn_probe_init_once, NULL, NULL);
}

void ycn_direct_probe_shutdown(void)
{
	int i;
	ycn_probe_ensure_lock();
	EnterCriticalSection(&g_probe_lock);
	for (i = 0; i < YCN_MAX_PROBE_DEVICES; i++)
	{
		if (g_probe_devices[i].device)
		{
			ID3D11Device* device = g_probe_devices[i].device;
			g_probe_devices[i].device = NULL;
			g_probe_devices[i].used = 0;
			device->lpVtbl->Release(device);
		}
	}
	LeaveCriticalSection(&g_probe_lock);
}

/* 同适配器、带 VIDEO_SUPPORT 的探测设备。失败返回 NULL（只记录，不抛）。 */
static ID3D11Device* ycn_probe_device_for_luid(const LUID* luid)
{
	IDXGIFactory1* factory = NULL;
	IDXGIAdapter1* adapter = NULL;
	ID3D11Device* result = NULL;
	int i;
	int freeSlot = -1;
	HRESULT hr;

	if (!luid)
		return NULL;

	ycn_probe_ensure_lock();
	EnterCriticalSection(&g_probe_lock);
	for (i = 0; i < YCN_MAX_PROBE_DEVICES; i++)
	{
		if (g_probe_devices[i].used && (g_probe_devices[i].luid.LowPart == luid->LowPart) &&
		    (g_probe_devices[i].luid.HighPart == luid->HighPart) && g_probe_devices[i].device)
		{
			result = g_probe_devices[i].device;
			LeaveCriticalSection(&g_probe_lock);
			return result;
		}
		if (!g_probe_devices[i].used)
			freeSlot = i;
	}
	LeaveCriticalSection(&g_probe_lock);

	if (freeSlot < 0)
		return NULL;
	if (FAILED(CreateDXGIFactory1(&IID_IDXGIFactory1, (void**)&factory)))
		return NULL;

	for (UINT index = 0; SUCCEEDED(factory->lpVtbl->EnumAdapters1(factory, index, &adapter));
	     index++)
	{
		DXGI_ADAPTER_DESC1 desc;
		if (SUCCEEDED(adapter->lpVtbl->GetDesc1(adapter, &desc)) &&
		    (desc.AdapterLuid.LowPart == luid->LowPart) &&
		    (desc.AdapterLuid.HighPart == luid->HighPart))
		{
			static const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1,
				                                        D3D_FEATURE_LEVEL_11_0,
				                                        D3D_FEATURE_LEVEL_10_1 };
			const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT |
			                   D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
			hr = D3D11CreateDevice((IDXGIAdapter*)adapter, D3D_DRIVER_TYPE_UNKNOWN, NULL, flags,
			                       levels, (UINT)(sizeof(levels) / sizeof(levels[0])),
			                       D3D11_SDK_VERSION, &result, NULL, NULL);
			if (FAILED(hr))
				result = NULL;
			adapter->lpVtbl->Release(adapter);
			break;
		}
		adapter->lpVtbl->Release(adapter);
		adapter = NULL;
	}
	if (adapter)
		adapter->lpVtbl->Release(adapter);
	factory->lpVtbl->Release(factory);

	if (result)
	{
		EnterCriticalSection(&g_probe_lock);
		g_probe_devices[freeSlot].used = 1;
		g_probe_devices[freeSlot].luid = *luid;
		g_probe_devices[freeSlot].device = result;
		LeaveCriticalSection(&g_probe_lock);
	}
	return result;
}

/* 这块纹理能否被 VideoProcessor 直接吃（硬件转色的前提）。 */
static HRESULT ycn_probe_video_processor_input(ID3D11Device* device, ID3D11Texture2D* texture,
                                               const D3D11_TEXTURE2D_DESC* desc)
{
	ID3D11VideoDevice* video = NULL;
	ID3D11VideoProcessorEnumerator* enumerator = NULL;
	ID3D11VideoProcessor* processor = NULL;
	ID3D11VideoProcessorInputView* input = NULL;
	D3D11_VIDEO_PROCESSOR_CONTENT_DESC content;
	D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC view;
	HRESULT hr;

	if (!device || !texture || !desc)
		return E_POINTER;

	hr = device->lpVtbl->QueryInterface(device, &IID_ID3D11VideoDevice, (void**)&video);
	if (FAILED(hr))
		return hr;

	memset(&content, 0, sizeof(content));
	content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
	content.InputWidth = desc->Width;
	content.InputHeight = desc->Height;
	content.OutputWidth = desc->Width;
	content.OutputHeight = desc->Height;
	content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

	hr = video->lpVtbl->CreateVideoProcessorEnumerator(video, &content, &enumerator);
	if (FAILED(hr))
		goto fail;

	hr = video->lpVtbl->CreateVideoProcessor(video, enumerator, 0, &processor);
	if (FAILED(hr))
		goto fail;

	memset(&view, 0, sizeof(view));
	view.FourCC = 0;
	view.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
	view.Texture2D.MipSlice = 0;
	view.Texture2D.ArraySlice = 0;

	hr = video->lpVtbl->CreateVideoProcessorInputView(video, (ID3D11Resource*)texture,
	                                                  enumerator, &view, &input);

fail:
	YCN_SAFE_RELEASE(input);
	YCN_SAFE_RELEASE(processor);
	YCN_SAFE_RELEASE(enumerator);
	YCN_SAFE_RELEASE(video);
	return hr;
}

static void ycn_probe_d3d11(ycn_direct_surface_info* out, ID3D11Texture2D* texture)
{
	ID3D11Device* sourceDevice = NULL;
	IDXGIDevice* dxgiDevice = NULL;
	IDXGIAdapter* adapter = NULL;
	IDXGIResource1* resource1 = NULL;
	IDXGIResource* resource = NULL;
	ID3D11Device* probeDevice = NULL;
	ID3D11Device1* probeDevice1 = NULL;
	ID3D11Texture2D* shared = NULL;
	BOOL sharedIsNtHandle = FALSE;
	D3D11_TEXTURE2D_DESC desc;
	DXGI_ADAPTER_DESC adapterDesc;
	HANDLE sharedHandle = NULL;
	UINT support = 0;
	HRESULT hr;

	memset(&desc, 0, sizeof(desc));
	memset(&adapterDesc, 0, sizeof(adapterDesc));
	texture->lpVtbl->GetDesc(texture, &desc);
	out->dxgi_format = (uint32_t)desc.Format;
	out->width = desc.Width;
	out->height = desc.Height;
	out->texture_array_size = desc.ArraySize;
	out->texture_mip_levels = desc.MipLevels;

	/* ID3D11Texture2D::GetDevice 返回 void：只能查输出指针。 */
	texture->lpVtbl->GetDevice(texture, &sourceDevice);
	if (!sourceDevice)
	{
		ycn_set_text(out->detail, sizeof(out->detail), "d3d11:no-device");
		return;
	}
	if (SUCCEEDED(sourceDevice->lpVtbl->QueryInterface(sourceDevice, &IID_IDXGIDevice,
	                                                   (void**)&dxgiDevice)) &&
	    dxgiDevice)
	{
		if (SUCCEEDED(dxgiDevice->lpVtbl->GetAdapter(dxgiDevice, &adapter)) && adapter)
		{
			if (SUCCEEDED(adapter->lpVtbl->GetDesc(adapter, &adapterDesc)))
			{
				out->adapter_vendor = adapterDesc.VendorId;
				out->adapter_device = adapterDesc.DeviceId;
			}
			adapter->lpVtbl->Release(adapter);
			adapter = NULL;
		}
		dxgiDevice->lpVtbl->Release(dxgiDevice);
		dxgiDevice = NULL;
	}

	/* 1) 解码器自己的 device 能不能直接把这块 surface 交给 VideoProcessor。
	 *    这是硬件转色路径的前提，不需要任何跨设备共享。 */
	out->sample_status = (int32_t)ycn_probe_video_processor_input(sourceDevice, texture, &desc);
	if (SUCCEEDED((HRESULT)out->sample_status))
		out->flags |= YCN_DIRECT_FLAG_SAMPLEABLE;

	if (SUCCEEDED(sourceDevice->lpVtbl->CheckFormatSupport(sourceDevice, desc.Format, &support)))
		out->format_support = support;

	/* 2) 跨设备共享（给“渲染器自建 device”这条备选路径用）。 */
	sharedIsNtHandle = FALSE;
	hr = sourceDevice->lpVtbl->QueryInterface(sourceDevice, &IID_IDXGIResource1,
	                                          (void**)&resource1);
	if (SUCCEEDED(hr) && resource1)
	{
		hr = resource1->lpVtbl->CreateSharedHandle(resource1, NULL,
		                                           DXGI_SHARED_RESOURCE_READ |
		                                               DXGI_SHARED_RESOURCE_WRITE,
		                                           NULL, &sharedHandle);
		if (SUCCEEDED(hr))
			sharedIsNtHandle = TRUE;
		resource1->lpVtbl->Release(resource1);
		resource1 = NULL;
	}
	if (FAILED(hr))
	{
		if (SUCCEEDED(sourceDevice->lpVtbl->QueryInterface(sourceDevice, &IID_IDXGIResource,
		                                                   (void**)&resource)) &&
		    resource)
		{
			hr = resource->lpVtbl->GetSharedHandle(resource, &sharedHandle);
			resource->lpVtbl->Release(resource);
			resource = NULL;
		}
	}
	out->share_status = (int32_t)hr;
	if (SUCCEEDED(hr) && sharedHandle)
		out->flags |= YCN_DIRECT_FLAG_SHAREABLE;

	/* 3) 用同适配器的探测设备打开共享句柄：能开就说明“自建 device”这条路成立。 */
	if (out->flags & YCN_DIRECT_FLAG_SHAREABLE)
	{
		const LUID luid = adapterDesc.AdapterLuid;
		probeDevice = ycn_probe_device_for_luid(&luid);
		if (probeDevice)
		{
			if (sharedIsNtHandle &&
			    SUCCEEDED(probeDevice->lpVtbl->QueryInterface(probeDevice, &IID_ID3D11Device1,
		                                                  (void**)&probeDevice1)) &&
			    probeDevice1)
			{
				hr = probeDevice1->lpVtbl->OpenSharedResource1(probeDevice1, sharedHandle,
				                                              &IID_ID3D11Texture2D, (void**)&shared);
				probeDevice1->lpVtbl->Release(probeDevice1);
				probeDevice1 = NULL;
			}
			else
				hr = probeDevice->lpVtbl->OpenSharedResource(probeDevice, sharedHandle,
				                                            &IID_ID3D11Texture2D, (void**)&shared);
			out->open_status = (int32_t)hr;
			if (SUCCEEDED(hr))
				out->flags |= YCN_DIRECT_FLAG_OPENED;
			YCN_SAFE_RELEASE(shared);
		}
		else
			out->open_status = (int32_t)E_FAIL;
		CloseHandle(sharedHandle);
	}

	out->verdict = (out->flags & YCN_DIRECT_FLAG_SAMPLEABLE) ? YCN_DIRECT_VERDICT_D3D11_DIRECT
	                                                         : YCN_DIRECT_VERDICT_UNSUPPORTED;
	ycn_set_text(out->detail, sizeof(out->detail),
	             (out->flags & YCN_DIRECT_FLAG_SAMPLEABLE) ? "d3d11:vp-input-ok"
	                                                      : "d3d11:vp-input-failed");

	sourceDevice->lpVtbl->Release(sourceDevice);
}

static void ycn_probe_dxva2(ycn_direct_surface_info* out, IDirect3DSurface9* surface)
{
	IDirect3DDevice9* device = NULL;
	IDirect3DDevice9Ex* deviceEx = NULL;
	IDirect3DTexture9* texture = NULL;
	IDXGIResource* resource = NULL;
	D3DSURFACE_DESC surfaceDesc;
	D3DSURFACE_DESC textureDesc;
	HANDLE sharedHandle = NULL;
	HRESULT hr;

	memset(&surfaceDesc, 0, sizeof(surfaceDesc));
	surface->lpVtbl->GetDesc(surface, &surfaceDesc);
	out->width = surfaceDesc.Width;
	out->height = surfaceDesc.Height;
	out->dxgi_format = (uint32_t)surfaceDesc.Format;

	if (FAILED(surface->lpVtbl->GetDevice(surface, &device)) || !device)
	{
		ycn_set_text(out->detail, sizeof(out->detail), "dxva2:no-device");
		out->verdict = YCN_DIRECT_VERDICT_CPU_READBACK;
		return;
	}

	/* open_status 在这里记“设备是否 D3D9Ex”：只有 Ex 设备才谈得上共享。
	 * share_status 只记共享句柄本身的 HRESULT。 */
	hr = device->lpVtbl->QueryInterface(device, &IID_IDirect3DDevice9Ex, (void**)&deviceEx);
	out->open_status = (int32_t)hr;
	out->share_status = (int32_t)E_NOINTERFACE;
	/* D3D9 面没有 VideoProcessor 输入视图可言，别把 0（S_OK）留在这里。 */
	out->sample_status = (int32_t)E_NOTIMPL;

	if (SUCCEEDED(surface->lpVtbl->GetContainer(surface, &IID_IDirect3DTexture9,
	                                            (void**)&texture)) &&
	    texture)
	{
		memset(&textureDesc, 0, sizeof(textureDesc));
		texture->lpVtbl->GetLevelDesc(texture, 0, &textureDesc);
		out->texture_array_size = 1;
		out->texture_mip_levels = 1;
		/* D3D9Ex 纹理才可能拿到 DXGI 共享句柄；DXVA2 解码面通常拿不到。 */
		hr = texture->lpVtbl->QueryInterface(texture, &IID_IDXGIResource, (void**)&resource);
		out->share_status = (int32_t)hr;
		if (SUCCEEDED(hr) && resource)
		{
			hr = resource->lpVtbl->GetSharedHandle(resource, &sharedHandle);
			resource->lpVtbl->Release(resource);
			out->share_status = (int32_t)hr;
			if (SUCCEEDED(hr) && sharedHandle)
			{
				out->flags |= YCN_DIRECT_FLAG_SHAREABLE;
				CloseHandle(sharedHandle);
			}
		}
		texture->lpVtbl->Release(texture);
	}

	out->verdict = (out->flags & YCN_DIRECT_FLAG_SHAREABLE) ? YCN_DIRECT_VERDICT_DXVA2_INTEROP
	                                                        : YCN_DIRECT_VERDICT_CPU_READBACK;
	ycn_set_text(out->detail, sizeof(out->detail),
	             (out->flags & YCN_DIRECT_FLAG_SHAREABLE) ? "dxva2:shared-handle-ok"
	                                                     : "dxva2:no-shared-handle");

	if (deviceEx)
		deviceEx->lpVtbl->Release(deviceEx);
	device->lpVtbl->Release(device);
}

void ycn_direct_probe_surface(ycn_direct_surface_info* out, uint32_t stage,
                              const void* const plane[4])
{
	if (!out)
		return;

	out->stage = stage;
	out->flags |= YCN_DIRECT_FLAG_PROBED;

	switch (stage)
	{
		case YCN_DIRECT_STAGE_D3D11:
			if (plane && plane[0])
				ycn_probe_d3d11(out, (ID3D11Texture2D*)plane[0]);
			else
				out->verdict = YCN_DIRECT_VERDICT_UNSUPPORTED;
			break;
		case YCN_DIRECT_STAGE_DXVA2:
			if (plane && plane[3])
				ycn_probe_dxva2(out, (IDirect3DSurface9*)plane[3]);
			else
				out->verdict = YCN_DIRECT_VERDICT_UNSUPPORTED;
			break;
		case YCN_DIRECT_STAGE_SOFTWARE:
			out->verdict = YCN_DIRECT_VERDICT_CPU_READBACK;
			ycn_set_text(out->detail, sizeof(out->detail), "software:no-gpu-surface");
			break;
		default:
			out->verdict = YCN_DIRECT_VERDICT_UNSUPPORTED;
			ycn_set_text(out->detail, sizeof(out->detail), "unknown-surface");
			break;
	}
}
