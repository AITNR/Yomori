/*
 * gfx_caps_probe.c
 *
 * Offline probe for the real FreeRDP RDPGFX dynamic virtual channel.
 *
 * The probe loads the installed freerdp-client3.dll, resolves the static
 * "rdpgfx" DVC addin entry through freerdp_channels_load_static_addin_entry,
 * and calls the real rdpgfx_DVCPluginEntry.  It supplies fake
 * IDRDYNVC_ENTRY_POINTS / IWTSVirtualChannelManager / IWTSListener /
 * IWTSVirtualChannel objects, so the production RDPGFX plugin owns the
 * serialization and the probe only captures the bytes written by its
 * real IWTSVirtualChannel::Write implementation.
 *
 * No RDP connection, network access, or server change is performed.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <inttypes.h>

#include <freerdp/freerdp.h>
#include <freerdp/dvc.h>
#include <freerdp/addin.h>
#include <freerdp/settings.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/rdpgfx.h>
#include <freerdp/codec/zgfx.h>

#include <winpr/cast.h>
#include <winpr/stream.h>

#define MAX_WRITES 8
#define MAX_CAPS 32

typedef struct
{
	IWTSVirtualChannel iface;
	int writeCount;
	BYTE* writes[MAX_WRITES];
	ULONG sizes[MAX_WRITES];
} FakeChannel;

typedef struct
{
	IWTSVirtualChannelManager iface;
	FakeChannel channel;
	IWTSListener listener;
	IWTSListenerCallback* listenerCallback;
	char channelName[64];
	int createListenerCalls;
	int destroyListenerCalls;
	BOOL listenerActive;
} FakeChannelManager;

typedef struct
{
	IDRDYNVC_ENTRY_POINTS iface;
	rdpContext* context;
	IWTSPlugin* plugin;
	char pluginName[64];
	int registerCalls;
	int getCalls;
} FakeEntryPoints;

typedef struct
{
	UINT16 cmdId;
	UINT16 headerFlags;
	UINT32 pduLength;
	UINT16 capsCount;
	RDPGFX_CAPSET caps[MAX_CAPS];
	UINT32 nonzeroPaddingBytes;
} ParsedCapsAdvertise;

typedef struct
{
	UINT32 version;
	UINT32 length;
	UINT32 flags;
} ExpectedCap;

typedef struct
{
	const char* id;
	const char* title;
	BOOL h264;
	BOOL avc444;
	BOOL thinClient;
	UINT32 capsFilter;
	size_t expectedCount;
	ExpectedCap expected[16];
	BOOL runCapsConfirmHook;
	BOOL smallCache;
} Scenario;

typedef struct
{
	int hits;
	UINT32 version;
	UINT32 length;
	UINT32 flags;
} ConfirmHookState;

static ConfirmHookState g_confirmHook;

static const Scenario g_scenarios[] = {
	{ "modern", "modern AVC420/444 capability set", TRUE, TRUE, TRUE, 0, 11,
	  { { RDPGFX_CAPVERSION_8, 4, RDPGFX_CAPS_FLAG_THINCLIENT },
	    { RDPGFX_CAPVERSION_81, 4, 0x13 },
	    { RDPGFX_CAPVERSION_10, 4, 0x02 },
	    { RDPGFX_CAPVERSION_101, 0x10, 0 },
	    { RDPGFX_CAPVERSION_102, 4, 0x02 },
	    { RDPGFX_CAPVERSION_103, 4, 0x40 },
	    { RDPGFX_CAPVERSION_104, 4, 0x42 },
	    { RDPGFX_CAPVERSION_105, 4, 0x42 },
	    { RDPGFX_CAPVERSION_106, 4, 0x42 },
	    { RDPGFX_CAPVERSION_106_ERR, 4, 0x42 },
	    { RDPGFX_CAPVERSION_107, 4, 0x42 } }, FALSE, TRUE },
	{ "production", "production AVC420 preference: H264+ThinClient+SmallCache", TRUE, FALSE, TRUE, 0, 2,
	  { { RDPGFX_CAPVERSION_8, 4, RDPGFX_CAPS_FLAG_THINCLIENT },
	    { RDPGFX_CAPVERSION_81, 4, RDPGFX_CAPS_FLAG_THINCLIENT | RDPGFX_CAPS_FLAG_SMALL_CACHE | RDPGFX_CAPS_FLAG_AVC420_ENABLED } },
	  FALSE, TRUE },
	{ "s1", "GfxH264=TRUE GfxAVC444=FALSE GfxThinClient=TRUE filter=0x00000000", TRUE, FALSE,
	  TRUE, 0x00000000u, 2,
	  { { RDPGFX_CAPVERSION_8, 4, RDPGFX_CAPS_FLAG_THINCLIENT },
	    { RDPGFX_CAPVERSION_81, 4,
	      RDPGFX_CAPS_FLAG_THINCLIENT | RDPGFX_CAPS_FLAG_AVC420_ENABLED } },
	  TRUE },
	{ "s2", "GfxH264=TRUE GfxAVC444=FALSE GfxThinClient=FALSE filter=0x00000000", TRUE, FALSE,
	  FALSE, 0x00000000u, 2,
	  { { RDPGFX_CAPVERSION_8, 4, 0 },
	    { RDPGFX_CAPVERSION_81, 4, RDPGFX_CAPS_FLAG_AVC420_ENABLED } },
	  FALSE },
	{ "s3", "GfxH264=FALSE GfxAVC444=FALSE GfxThinClient=FALSE filter=0x00000000", FALSE, FALSE,
	  FALSE, 0x00000000u, 11,
	  { { RDPGFX_CAPVERSION_8, 4, 0 },
	    { RDPGFX_CAPVERSION_81, 4, 0 },
	    { RDPGFX_CAPVERSION_10, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_101, 0x10, 0 },
	    { RDPGFX_CAPVERSION_102, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_103, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_104, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_105, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_106, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_106_ERR, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_107, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED } },
	  FALSE },
	{ "s4", "GfxH264=FALSE GfxAVC444=TRUE GfxThinClient=FALSE filter=0x00000000", FALSE, TRUE,
	  FALSE, 0x00000000u, 11,
	  { { RDPGFX_CAPVERSION_8, 4, 0 },
	    { RDPGFX_CAPVERSION_81, 4, 0 },
	    { RDPGFX_CAPVERSION_10, 4, 0 },
	    { RDPGFX_CAPVERSION_101, 0x10, 0 },
	    { RDPGFX_CAPVERSION_102, 4, 0 },
	    { RDPGFX_CAPVERSION_103, 4, 0 },
	    { RDPGFX_CAPVERSION_104, 4, 0 },
	    { RDPGFX_CAPVERSION_105, 4, 0 },
	    { RDPGFX_CAPVERSION_106, 4, 0 },
	    { RDPGFX_CAPVERSION_106_ERR, 4, 0 },
	    { RDPGFX_CAPVERSION_107, 4, 0 } },
	  FALSE },
	{ "s6", "filter=0x00000003 (suppress capList bits 0 and 1)", FALSE, FALSE, FALSE, 0x00000003u,
	  9,
	  { { RDPGFX_CAPVERSION_10, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_101, 0x10, 0 },
	    { RDPGFX_CAPVERSION_102, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_103, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_104, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_105, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_106, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_106_ERR, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_107, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED } },
	  FALSE },
	{ "s7", "filter=0x00000400 (suppress capList bit 10 / 10.7 only)", FALSE, FALSE, FALSE,
	  0x00000400u, 10,
	  { { RDPGFX_CAPVERSION_8, 4, 0 },
	    { RDPGFX_CAPVERSION_81, 4, 0 },
	    { RDPGFX_CAPVERSION_10, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_101, 0x10, 0 },
	    { RDPGFX_CAPVERSION_102, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_103, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_104, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_105, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_106, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED },
	    { RDPGFX_CAPVERSION_106_ERR, 4, RDPGFX_CAPS_FLAG_AVC_DISABLED } },
	  FALSE },
	{ "s8", "filter=0x000007FF (suppress every advertised capset)", FALSE, FALSE, FALSE,
	  0x000007FFu, 0, { 0 }, FALSE },
};

static UINT16 read_u16le(const BYTE* p)
{
	return (UINT16)((UINT16)p[0] | ((UINT16)p[1] << 8));
}

static UINT32 read_u32le(const BYTE* p)
{
	return (UINT32)p[0] | ((UINT32)p[1] << 8) | ((UINT32)p[2] << 16) | ((UINT32)p[3] << 24);
}

static const char* cap_version_name(UINT32 version)
{
	switch (version)
	{
		case RDPGFX_CAPVERSION_8:
			return "8.0";
		case RDPGFX_CAPVERSION_81:
			return "8.1";
		case RDPGFX_CAPVERSION_10:
			return "10.0";
		case RDPGFX_CAPVERSION_101:
			return "10.1";
		case RDPGFX_CAPVERSION_102:
			return "10.2";
		case RDPGFX_CAPVERSION_103:
			return "10.3";
		case RDPGFX_CAPVERSION_104:
			return "10.4";
		case RDPGFX_CAPVERSION_105:
			return "10.5";
		case RDPGFX_CAPVERSION_106:
			return "10.6";
		case RDPGFX_CAPVERSION_106_ERR:
			return "10.6-errata";
		case RDPGFX_CAPVERSION_107:
			return "10.7";
		default:
			return "unknown";
	}
}

static void format_flags(UINT32 flags, char* buffer, size_t size)
{
	size_t used = 0;
	buffer[0] = '\0';

#define APPEND_FLAG(mask, text)                                                              \
	do                                                                                       \
	{                                                                                        \
		if ((flags & (mask)) != 0)                                                            \
		{                                                                                    \
			const int n = snprintf(buffer + used, size - used, "%s%s", used ? "|" : "", text); \
			if (n > 0)                                                                       \
				used += (size_t)n;                                                            \
			if (used >= size)                                                                 \
				used = size - 1;                                                              \
		}                                                                                    \
	} while (0)

	APPEND_FLAG(RDPGFX_CAPS_FLAG_THINCLIENT, "THINCLIENT");
	APPEND_FLAG(RDPGFX_CAPS_FLAG_SMALL_CACHE, "SMALL_CACHE");
	APPEND_FLAG(RDPGFX_CAPS_FLAG_AVC420_ENABLED, "AVC420_ENABLED");
	APPEND_FLAG(RDPGFX_CAPS_FLAG_AVC_DISABLED, "AVC_DISABLED");
	APPEND_FLAG(RDPGFX_CAPS_FLAG_AVC_THINCLIENT, "AVC_THINCLIENT");
	APPEND_FLAG(RDPGFX_CAPS_FLAG_SCALEDMAP_DISABLE, "SCALEDMAP_DISABLE");

#undef APPEND_FLAG

	if (flags == 0)
		snprintf(buffer, size, "NONE");
	else if (buffer[0] == '\0')
		snprintf(buffer, size, "0x%08" PRIX32, flags);
}

static void print_hex_dump(const BYTE* data, size_t length)
{
	for (size_t off = 0; off < length; off += 16)
	{
		const size_t lineLength = (length - off > 16) ? 16 : (length - off);
		printf("    %04" PRIuz ": ", off);
		for (size_t i = 0; i < 16; i++)
		{
			if (i < lineLength)
				printf("%02" PRIX8 " ", data[off + i]);
			else
				printf("   ");
		}
		printf(" |");
		for (size_t i = 0; i < lineLength; i++)
		{
			const BYTE c = data[off + i];
			printf("%c", (c >= 0x20 && c <= 0x7e) ? (char)c : '.');
		}
		printf("|\n");
	}
}

static FILE* open_artifact(const char* name)
{
	FILE* f = NULL;
	if (fopen_s(&f, name, "wb") != 0)
		return NULL;
	return f;
}

static void save_hex_artifact(const char* id, const BYTE* data, size_t length)
{
	char name[128];
	snprintf(name, sizeof(name), "caps-%s.hex.txt", id);
	FILE* f = open_artifact(name);
	if (!f)
	{
		printf("  WARNING: could not write %s\n", name);
		return;
	}
	fprintf(f, "RDPGFX CAPS_ADVERTISE captured from real rdpgfx_DVCPluginEntry\n");
	fprintf(f, "scenario=%s length=%" PRIuz " bytes\n", id, length);
	for (size_t off = 0; off < length; off += 16)
	{
		fprintf(f, "%04" PRIuz ": ", off);
		for (size_t i = 0; i < 16 && off + i < length; i++)
			fprintf(f, "%02" PRIX8 " ", data[off + i]);
		fprintf(f, "\n");
	}
	fclose(f);
}

static void save_bin_artifact(const char* id, const BYTE* data, size_t length)
{
	char name[128];
	snprintf(name, sizeof(name), "caps-%s.bin", id);
	FILE* f = open_artifact(name);
	if (!f)
	{
		printf("  WARNING: could not write %s\n", name);
		return;
	}
	fwrite(data, 1, length, f);
	fclose(f);
}

static BOOL parse_caps_advertise(const BYTE* pdu, size_t length, ParsedCapsAdvertise* parsed)
{
	if (!pdu || !parsed || length < 10)
		return FALSE;

	memset(parsed, 0, sizeof(*parsed));
	parsed->cmdId = read_u16le(pdu + 0);
	parsed->headerFlags = read_u16le(pdu + 2);
	parsed->pduLength = read_u32le(pdu + 4);
	parsed->capsCount = read_u16le(pdu + 8);

	if (parsed->pduLength != length)
		return FALSE;
	if (parsed->capsCount > MAX_CAPS)
		return FALSE;

	size_t off = 10;
	for (UINT16 i = 0; i < parsed->capsCount; i++)
	{
		if (off + 12 > length)
			return FALSE;

		RDPGFX_CAPSET* cap = &parsed->caps[i];
		cap->version = read_u32le(pdu + off + 0);
		cap->length = read_u32le(pdu + off + 4);
		cap->flags = read_u32le(pdu + off + 8);
		off += 12;

		if (cap->length < 4)
			return FALSE;
		const size_t extra = (size_t)cap->length - 4;
		if (off + extra > length)
			return FALSE;
		for (size_t j = 0; j < extra; j++)
		{
			if (pdu[off + j] != 0)
				parsed->nonzeroPaddingBytes++;
		}
		off += extra;
	}

	return off == length;
}

static UINT fake_listener_get_configuration(IWTSListener* listener, void** propertyBag)
{
	(void)listener;
	if (propertyBag)
		*propertyBag = NULL;
	return CHANNEL_RC_OK;
}

static UINT fake_channel_write(IWTSVirtualChannel* channel, ULONG cbSize, const BYTE* buffer,
                               void* reserved)
{
	(void)reserved;
	FakeChannel* fake = (FakeChannel*)channel;
	if (!fake || (!buffer && cbSize != 0))
		return ERROR_INVALID_PARAMETER;
	if (fake->writeCount >= MAX_WRITES)
		return CHANNEL_RC_NO_MEMORY;

	BYTE* copy = NULL;
	if (cbSize != 0)
	{
		copy = (BYTE*)malloc(cbSize);
		if (!copy)
			return CHANNEL_RC_NO_MEMORY;
		memcpy(copy, buffer, cbSize);
	}
	fake->writes[fake->writeCount] = copy;
	fake->sizes[fake->writeCount] = cbSize;
	fake->writeCount++;
	printf("  fake IWTSVirtualChannel::Write(cbSize=%lu)\n", (unsigned long)cbSize);
	return CHANNEL_RC_OK;
}

static UINT fake_channel_close(IWTSVirtualChannel* channel)
{
	(void)channel;
	return CHANNEL_RC_OK;
}

static UINT fake_create_listener(IWTSVirtualChannelManager* manager, const char* channelName,
                                 ULONG flags, IWTSListenerCallback* listenerCallback,
                                 IWTSListener** listener)
{
	(void)flags;
	FakeChannelManager* mgr = (FakeChannelManager*)manager;
	if (!mgr || !channelName || !listenerCallback || !listener)
		return ERROR_INVALID_PARAMETER;
	if (strcmp(channelName, RDPGFX_DVC_CHANNEL_NAME) != 0)
		return ERROR_NOT_FOUND;

	mgr->createListenerCalls++;
	snprintf(mgr->channelName, sizeof(mgr->channelName), "%s", channelName);
	mgr->listenerCallback = listenerCallback;
	mgr->listener.GetConfiguration = fake_listener_get_configuration;
	mgr->listener.pInterface = NULL;
	mgr->listenerActive = TRUE;
	*listener = &mgr->listener;
	printf("  fake IWTSVirtualChannelManager::CreateListener(\"%s\")\n", channelName);
	return CHANNEL_RC_OK;
}

static UINT32 fake_get_channel_id(IWTSVirtualChannel* channel)
{
	(void)channel;
	return 7;
}

static IWTSVirtualChannel* fake_find_channel_by_id(IWTSVirtualChannelManager* manager, UINT32 id)
{
	FakeChannelManager* mgr = (FakeChannelManager*)manager;
	if (!mgr || id != 7)
		return NULL;
	return &mgr->channel.iface;
}

static const char* fake_get_channel_name(IWTSVirtualChannel* channel)
{
	(void)channel;
	return "rdpgfx";
}

static UINT fake_destroy_listener(IWTSVirtualChannelManager* manager, IWTSListener* listener)
{
	FakeChannelManager* mgr = (FakeChannelManager*)manager;
	if (!mgr)
		return ERROR_INVALID_PARAMETER;
	if (listener != &mgr->listener)
		return ERROR_INVALID_PARAMETER;
	mgr->destroyListenerCalls++;
	mgr->listenerActive = FALSE;
	return CHANNEL_RC_OK;
}

static void init_fake_manager(FakeChannelManager* mgr)
{
	memset(mgr, 0, sizeof(*mgr));
	mgr->iface.CreateListener = fake_create_listener;
	mgr->iface.GetChannelId = fake_get_channel_id;
	mgr->iface.FindChannelById = fake_find_channel_by_id;
	mgr->iface.GetChannelName = fake_get_channel_name;
	mgr->iface.DestroyListener = fake_destroy_listener;

	mgr->channel.iface.Write = fake_channel_write;
	mgr->channel.iface.Close = fake_channel_close;
}

static UINT fake_register_plugin(IDRDYNVC_ENTRY_POINTS* entryPoints, const char* name,
                                 IWTSPlugin* plugin)
{
	FakeEntryPoints* ep = (FakeEntryPoints*)entryPoints;
	if (!ep || !name || !plugin)
		return ERROR_INVALID_PARAMETER;
	if (ep->plugin)
		return CHANNEL_RC_ALREADY_INITIALIZED;

	ep->registerCalls++;
	ep->plugin = plugin;
	snprintf(ep->pluginName, sizeof(ep->pluginName), "%s", name);
	printf("  fake IDRDYNVC_ENTRY_POINTS::RegisterPlugin(\"%s\", %p)\n", name, (void*)plugin);
	return CHANNEL_RC_OK;
}

static IWTSPlugin* fake_get_plugin(IDRDYNVC_ENTRY_POINTS* entryPoints, const char* name)
{
	FakeEntryPoints* ep = (FakeEntryPoints*)entryPoints;
	if (!ep || !name)
		return NULL;
	ep->getCalls++;
	if (ep->plugin && strcmp(ep->pluginName, name) == 0)
		return ep->plugin;
	return NULL;
}

static const ADDIN_ARGV* fake_get_plugin_data(IDRDYNVC_ENTRY_POINTS* entryPoints)
{
	(void)entryPoints;
	return NULL;
}

static rdpSettings* fake_get_settings(IDRDYNVC_ENTRY_POINTS* entryPoints)
{
	FakeEntryPoints* ep = (FakeEntryPoints*)entryPoints;
	return ep && ep->context ? ep->context->settings : NULL;
}

static rdpContext* fake_get_context(IDRDYNVC_ENTRY_POINTS* entryPoints)
{
	FakeEntryPoints* ep = (FakeEntryPoints*)entryPoints;
	return ep ? ep->context : NULL;
}

static void init_fake_entry_points(FakeEntryPoints* ep, rdpContext* context)
{
	memset(ep, 0, sizeof(*ep));
	ep->context = context;
	ep->iface.RegisterPlugin = fake_register_plugin;
	ep->iface.GetPlugin = fake_get_plugin;
	ep->iface.GetPluginData = fake_get_plugin_data;
	ep->iface.GetRdpSettings = fake_get_settings;
	ep->iface.GetRdpContext = fake_get_context;
}

static UINT caps_confirm_hook(RdpgfxClientContext* context, const RDPGFX_CAPS_CONFIRM_PDU* pdu)
{
	(void)context;
	if (!pdu || !pdu->capsSet)
		return ERROR_INVALID_PARAMETER;

	g_confirmHook.hits++;
	g_confirmHook.version = pdu->capsSet->version;
	g_confirmHook.length = pdu->capsSet->length;
	g_confirmHook.flags = pdu->capsSet->flags;
	printf("  CapsConfirm hook fired: version=0x%08" PRIX32 " length=%" PRIu32
	       " flags=0x%08" PRIX32 "\n",
	       pdu->capsSet->version, pdu->capsSet->length, pdu->capsSet->flags);
	return CHANNEL_RC_OK;
}

static void store_u16le(BYTE* p, UINT16 v)
{
	p[0] = (BYTE)(v & 0xff);
	p[1] = (BYTE)((v >> 8) & 0xff);
}

static void store_u32le(BYTE* p, UINT32 v)
{
	p[0] = (BYTE)(v & 0xff);
	p[1] = (BYTE)((v >> 8) & 0xff);
	p[2] = (BYTE)((v >> 16) & 0xff);
	p[3] = (BYTE)((v >> 24) & 0xff);
}

static BOOL run_caps_confirm_roundtrip(IWTSVirtualChannelCallback* channelCallback,
                                       RdpgfxClientContext* gfxContext)
{
	BYTE raw[20];
	memset(raw, 0, sizeof(raw));
	store_u16le(raw + 0, RDPGFX_CMDID_CAPSCONFIRM);
	store_u16le(raw + 2, 0);
	store_u32le(raw + 4, (UINT32)sizeof(raw));
	store_u32le(raw + 8, RDPGFX_CAPVERSION_102);
	store_u32le(raw + 12, 4);
	store_u32le(raw + 16, RDPGFX_CAPS_FLAG_AVC_DISABLED);

	printf("  Simulated CAPS_CONFIRM raw PDU (%" PRIuz " bytes):\n", sizeof(raw));
	print_hex_dump(raw, sizeof(raw));

	ZGFX_CONTEXT* compressor = zgfx_context_new(TRUE);
	if (!compressor)
	{
		printf("  FAIL: zgfx_context_new(TRUE) failed\n");
		return FALSE;
	}

	BYTE* compressed = NULL;
	UINT32 compressedSize = 0;
	UINT32 zflags = 0;
	const int zrc = zgfx_compress(compressor, raw, (UINT32)sizeof(raw), &compressed, &compressedSize,
	                              &zflags);
	if (zrc < 0 || !compressed || compressedSize < 2)
	{
		printf("  FAIL: zgfx_compress failed rc=%d\n", zrc);
		free(compressed);
		zgfx_context_free(compressor);
		return FALSE;
	}
	printf("  ZGFX compressed by production zgfx_compress: %" PRIuz " -> %" PRIu32
	       " bytes, flags=0x%08" PRIX32 "\n",
	       sizeof(raw), compressedSize, zflags);

	g_confirmHook.hits = 0;
	g_confirmHook.version = 0;
	g_confirmHook.length = 0;
	g_confirmHook.flags = 0;
	gfxContext->CapsConfirm = caps_confirm_hook;

	wStream* stream = Stream_New(NULL, compressedSize);
	if (!stream)
	{
		printf("  FAIL: Stream_New failed\n");
		free(compressed);
		zgfx_context_free(compressor);
		return FALSE;
	}
	Stream_Write(stream, compressed, compressedSize);
	Stream_SealLength(stream);
	Stream_SetPosition(stream, 0);

	const UINT rc = channelCallback->OnDataReceived(channelCallback, stream);
	Stream_Free(stream, TRUE);
	free(compressed);
	zgfx_context_free(compressor);

	const BOOL ok = (rc == CHANNEL_RC_OK) && (g_confirmHook.hits == 1) &&
	                (g_confirmHook.version == RDPGFX_CAPVERSION_102) &&
	                (g_confirmHook.length == 4) &&
	                (g_confirmHook.flags == RDPGFX_CAPS_FLAG_AVC_DISABLED);
	printf("  OnDataReceived returned %" PRIu32 "; hook hits=%d; result=%s\n", rc,
	       g_confirmHook.hits, ok ? "PASS" : "FAIL");
	return ok;
}

static BOOL validate_caps(const Scenario* scenario, const ParsedCapsAdvertise* parsed)
{
	BOOL ok = TRUE;

	if (parsed->cmdId != RDPGFX_CMDID_CAPSADVERTISE)
	{
		printf("  MISMATCH: cmdId=0x%04" PRIX16 ", expected CAPSADVERTISE (0x%04" PRIX16 ")\n",
		       parsed->cmdId, (UINT16)RDPGFX_CMDID_CAPSADVERTISE);
		ok = FALSE;
	}
	if (parsed->headerFlags != 0)
	{
		printf("  MISMATCH: header flags=0x%04" PRIX16 ", expected 0\n", parsed->headerFlags);
		ok = FALSE;
	}
	if (parsed->capsCount != scenario->expectedCount)
	{
		printf("  MISMATCH: capsSetCount=%" PRIu16 ", expected %" PRIuz "\n", parsed->capsCount,
		       scenario->expectedCount);
		ok = FALSE;
	}
	if (parsed->nonzeroPaddingBytes != 0)
	{
		printf("  MISMATCH: %" PRIu32 " non-zero caps padding byte(s)\n",
		       parsed->nonzeroPaddingBytes);
		ok = FALSE;
	}

	const UINT16 common = parsed->capsCount < scenario->expectedCount ? parsed->capsCount
	                                                                  : (UINT16)scenario->expectedCount;
	for (UINT16 i = 0; i < common; i++)
	{
		const RDPGFX_CAPSET* actual = &parsed->caps[i];
		const ExpectedCap* expected = &scenario->expected[i];
		if (actual->version != expected->version || actual->length != expected->length ||
		    actual->flags != expected->flags)
		{
			printf("  MISMATCH caps[%" PRIu16 "]: actual version=0x%08" PRIX32
			       " length=0x%" PRIX32 " flags=0x%08" PRIX32
			       ", expected version=0x%08" PRIX32 " length=0x%" PRIX32
			       " flags=0x%08" PRIX32 "\n",
			       i, actual->version, actual->length, actual->flags, expected->version,
			       expected->length, expected->flags);
			ok = FALSE;
		}
	}
	return ok;
}

static void print_caps(const ParsedCapsAdvertise* parsed)
{
	printf("  Parsed: cmdId=0x%04" PRIX16 " headerFlags=0x%04" PRIX16
	       " pduLength=%" PRIu32 " capsSetCount=%" PRIu16 "\n",
	       parsed->cmdId, parsed->headerFlags, parsed->pduLength, parsed->capsCount);
	for (UINT16 i = 0; i < parsed->capsCount; i++)
	{
		const RDPGFX_CAPSET* cap = &parsed->caps[i];
		char flagText[160];
		format_flags(cap->flags, flagText, sizeof(flagText));
		printf("    caps[%" PRIu16 "] version=0x%08" PRIX32 " (%s) length=0x%" PRIX32
		       " flags=0x%08" PRIX32 " [%s]\n",
		       i, cap->version, cap_version_name(cap->version), cap->length, cap->flags, flagText);
	}
}

static void free_fake_manager_writes(FakeChannelManager* mgr)
{
	for (int i = 0; i < mgr->channel.writeCount; i++)
	{
		free(mgr->channel.writes[i]);
		mgr->channel.writes[i] = NULL;
	}
	mgr->channel.writeCount = 0;
}

static int run_scenario(PDVC_PLUGIN_ENTRY dvcEntry, const Scenario* scenario)
{
	int result = 1;
	freerdp* instance = NULL;
	FakeChannelManager manager;
	FakeEntryPoints entryPoints;
	IWTSVirtualChannelCallback* channelCallback = NULL;
	int capsWriteIndex = -1;
	ParsedCapsAdvertise parsed;

	printf("\n=== %s: %s ===\n", scenario->id, scenario->title);

	memset(&entryPoints, 0, sizeof(entryPoints));
	init_fake_manager(&manager);

	instance = freerdp_new();
	if (!instance)
	{
		printf("  FAIL: freerdp_new failed\n");
		goto cleanup;
	}
	if (!freerdp_context_new(instance))
	{
		printf("  FAIL: freerdp_context_new failed\n");
		goto cleanup;
	}

	rdpContext* context = instance->context;
	if (!context || !context->settings)
	{
		printf("  FAIL: context/settings missing\n");
		goto cleanup;
	}

	SetLastError(0);
	if (!freerdp_settings_set_bool(context->settings, FreeRDP_GfxH264, scenario->h264) ||
	    !freerdp_settings_set_bool(context->settings, FreeRDP_GfxAVC444, scenario->avc444) ||
	    !freerdp_settings_set_bool(context->settings, FreeRDP_GfxThinClient, scenario->thinClient) ||
	    !freerdp_settings_set_bool(context->settings, FreeRDP_GfxSmallCache, scenario->smallCache) ||
	    !freerdp_settings_set_uint32(context->settings, FreeRDP_GfxCapsFilter,
	                                 scenario->capsFilter))
	{
		printf("  FAIL: freerdp_settings_set_* failed (GetLastError=%lu)\n",
		       (unsigned long)GetLastError());
		goto cleanup;
	}

	printf("  settings: GfxH264=%d GfxAVC444=%d GfxThinClient=%d GfxSmallCache=%d "
	       "GfxCapsFilter=0x%08" PRIX32 "\n",
	       freerdp_settings_get_bool(context->settings, FreeRDP_GfxH264),
	       freerdp_settings_get_bool(context->settings, FreeRDP_GfxAVC444),
	       freerdp_settings_get_bool(context->settings, FreeRDP_GfxThinClient),
	       freerdp_settings_get_bool(context->settings, FreeRDP_GfxSmallCache),
	       freerdp_settings_get_uint32(context->settings, FreeRDP_GfxCapsFilter));

	init_fake_entry_points(&entryPoints, context);

	const UINT entryRc = dvcEntry(&entryPoints.iface);
	printf("  rdpgfx_DVCPluginEntry rc=%" PRIu32 " plugin=%p name=\"%s\"\n", entryRc,
	       (void*)entryPoints.plugin, entryPoints.pluginName);
	if (entryRc != CHANNEL_RC_OK || !entryPoints.plugin)
	{
		printf("  FAIL: static DVC entry did not register a plugin\n");
		goto cleanup;
	}

	const UINT initRc = entryPoints.plugin->Initialize(entryPoints.plugin, &manager.iface);
	printf("  IWTSPlugin::Initialize rc=%" PRIu32 " createListenerCalls=%d\n", initRc,
	       manager.createListenerCalls);
	if (initRc != CHANNEL_RC_OK || !manager.listenerCallback || !manager.listenerActive)
	{
		printf("  FAIL: plugin initialize/CreateListener failed\n");
		goto cleanup;
	}

	RdpgfxClientContext* gfxContext = (RdpgfxClientContext*)entryPoints.plugin->pInterface;
	if (!gfxContext)
	{
		printf("  FAIL: plugin pInterface is NULL\n");
		goto cleanup;
	}

	BOOL accepted = FALSE; /* generic_dynvc leaves this untouched */
	const UINT connectRc = manager.listenerCallback->OnNewChannelConnection(
	    manager.listenerCallback, &manager.channel.iface, NULL, &accepted, &channelCallback);
	printf("  IWTSListenerCallback::OnNewChannelConnection rc=%" PRIu32
	       " channelCallback=%p (generic_dynvc does not touch pbAccept)\n",
	       connectRc, (void*)channelCallback);
	if (connectRc != CHANNEL_RC_OK || !channelCallback)
	{
		printf("  FAIL: fake channel connection was not accepted\n");
		goto cleanup;
	}

	const UINT openRc = channelCallback->OnOpen(channelCallback);
	printf("  IWTSVirtualChannelCallback::OnOpen rc=%" PRIu32 "\n", openRc);
	if (openRc != CHANNEL_RC_OK)
	{
		printf("  FAIL: production OnOpen failed\n");
		goto cleanup;
	}

	for (int i = 0; i < manager.channel.writeCount; i++)
	{
		const BYTE* pdu = manager.channel.writes[i];
		const ULONG size = manager.channel.sizes[i];
		if (size >= 2 && read_u16le(pdu) == RDPGFX_CMDID_CAPSADVERTISE)
		{
			capsWriteIndex = i;
			break;
		}
	}
	if (capsWriteIndex < 0)
	{
		printf("  FAIL: no CAPS_ADVERTISE write captured\n");
		goto cleanup;
	}

	const BYTE* capsPdu = manager.channel.writes[capsWriteIndex];
	const ULONG capsSize = manager.channel.sizes[capsWriteIndex];
	printf("  Captured CAPS_ADVERTISE PDU: %lu bytes\n", (unsigned long)capsSize);
	print_hex_dump(capsPdu, capsSize);
	save_hex_artifact(scenario->id, capsPdu, capsSize);
	save_bin_artifact(scenario->id, capsPdu, capsSize);

	if (!parse_caps_advertise(capsPdu, capsSize, &parsed))
	{
		printf("  FAIL: captured PDU does not parse as CAPS_ADVERTISE\n");
		goto cleanup;
	}
	print_caps(&parsed);

	BOOL valid = validate_caps(scenario, &parsed);
	printf("  CAPS flags/filter check: %s\n", valid ? "PASS" : "FAIL");

	if (scenario->runCapsConfirmHook)
	{
		if (!run_caps_confirm_roundtrip(channelCallback, gfxContext))
			valid = FALSE;
	}

	if (manager.channel.writeCount != 1)
	{
		printf("  NOTE: channel wrote %d PDU(s); CAPS_CONFIRM may have produced extra output\n",
		       manager.channel.writeCount);
	}

	result = valid ? 0 : 1;

cleanup:
	if (channelCallback)
		channelCallback->OnClose(channelCallback);
	channelCallback = NULL;

	if (entryPoints.plugin)
		entryPoints.plugin->Terminated(entryPoints.plugin);

	if (instance)
	{
		freerdp_context_free(instance);
		freerdp_free(instance);
	}

	free_fake_manager_writes(&manager);
	return result;
}

static void print_filter_map(void)
{
	printf("GfxCapsFilter bit map (this build: WITH_GFX_AV1=OFF, WITH_GFX_AZURE=OFF):\n");
	printf("  bit0  -> 8.0        (0x00080004)\n");
	printf("  bit1  -> 8.1        (0x00080105)\n");
	printf("  bit2  -> 10.0       (0x000A0002)\n");
	printf("  bit3  -> 10.1       (0x000A0100)\n");
	printf("  bit4  -> 10.2       (0x000A0200)\n");
	printf("  bit5  -> 10.3       (0x000A0301)\n");
	printf("  bit6  -> 10.4       (0x000A0400)\n");
	printf("  bit7  -> 10.5       (0x000A0502)\n");
	printf("  bit8  -> 10.6       (0x000A0600)\n");
	printf("  bit9  -> 10.6-errata(0x000A0601)\n");
	printf("  bit10 -> 10.7       (0x000A0701)\n");
	printf("  A set bit suppresses that capset. Versions outside this list are treated as filtered.\n");
}

int wmain(int argc, wchar_t** argv)
{
	const wchar_t* dllPath = NULL;
	HMODULE module = NULL;
	int failures = 0;
	int executed = 0;

	if (argc >= 2)
		dllPath = argv[1];

	setvbuf(stdout, NULL, _IONBF, 0);
	printf("FreeRDP RDPGFX offline CAPS probe\n");
	printf("=================================\n");

	if (!dllPath)
	{
		printf("usage: gfx_caps_probe.exe <path-to-freerdp-client3.dll>\n");
		return 2;
	}

	module = LoadLibraryW(dllPath);
	if (!module)
	{
		printf("FAIL: LoadLibraryW(\"%ls\") error=%lu\n", dllPath, (unsigned long)GetLastError());
		return 3;
	}

	wchar_t modulePath[32768];
	DWORD modulePathLength = GetModuleFileNameW(module, modulePath, (DWORD)(sizeof(modulePath) / sizeof(modulePath[0])));
	if (modulePathLength == 0)
		modulePath[0] = L'\0';
	printf("Loaded module: %ls\n", modulePath);

	FARPROC direct = GetProcAddress(module, "rdpgfx_DVCPluginEntry");
	printf("GetProcAddress(\"rdpgfx_DVCPluginEntry\"): %s\n", direct ? "present" : "absent");

	FARPROC loadStaticProc = GetProcAddress(module, "freerdp_channels_load_static_addin_entry");
	printf("GetProcAddress(\"freerdp_channels_load_static_addin_entry\"): %s\n",
	       loadStaticProc ? "present" : "absent");
	if (!loadStaticProc)
	{
		printf("FAIL: static addin resolver is not exported\n");
		FreeLibrary(module);
		return 4;
	}

	typedef PVIRTUALCHANNELENTRY (*PFN_LOAD_STATIC_ADDIN)(LPCSTR, LPCSTR, LPCSTR, DWORD);
	PFN_LOAD_STATIC_ADDIN loadStatic =
	    WINPR_FUNC_PTR_CAST(loadStaticProc, PFN_LOAD_STATIC_ADDIN);

	PVIRTUALCHANNELENTRY resolved =
	    loadStatic("rdpgfx", NULL, NULL, FREERDP_ADDIN_CHANNEL_DYNAMIC);
	printf("freerdp_channels_load_static_addin_entry(\"rdpgfx\", NULL, NULL, "
	       "FREERDP_ADDIN_CHANNEL_DYNAMIC): %p\n",
	       (void*)resolved);
	if (!resolved)
	{
		printf("FAIL: rdpgfx is not present in the static addin table\n");
		FreeLibrary(module);
		return 5;
	}

	PDVC_PLUGIN_ENTRY dvcEntry = WINPR_FUNC_PTR_CAST(resolved, PDVC_PLUGIN_ENTRY);
	printf("Resolved real rdpgfx entry: %p\n", (void*)dvcEntry);

	print_filter_map();

	for (size_t i = 0; i < sizeof(g_scenarios) / sizeof(g_scenarios[0]); i++)
	{
		executed++;
		if (run_scenario(dvcEntry, &g_scenarios[i]) != 0)
			failures++;
	}

	printf("\n=================================\n");
	printf("Scenarios executed: %d, failures: %d, result: %s\n", executed, failures,
	       failures == 0 ? "PASS" : "FAIL");

	FreeLibrary(module);
	return failures == 0 ? 0 : 1;
}










