// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Yomori.Models;
using Yomori.ViewModels;

namespace Yomori.Services;

/// <summary>
/// 主控进程内的最小 MCP Server。
///
/// <para>服务只绑定本机回环地址，复用当前 MainViewModel 持有的内嵌 RDP 客户端。
/// MCP 层负责 JSON-RPC 和工具适配，通道状态、输入锁和连接生命周期仍由主控端负责。</para>
///
/// <para>读取工具无需令牌。写工具同时要求 YOMORI_MCP_TOKEN 和短期控制租约。
/// 未配置令牌时，服务仍可提供只读状态和画面，所有写操作都会失败关闭。</para>
/// </summary>
public sealed class YomoriMcpServer : IDisposable
{
    private const string ProtocolVersion = "2024-11-05";
    private const string TokenHeader = "X-Yomori-Mcp-Token";
    private const int DefaultPort = 23100;
    private const int MaxRequestBytes = 1_000_000;
    private const int MaxLeaseSeconds = 60;

    private readonly MainViewModel _viewModel;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _log;
    private readonly string _writeToken;
    private readonly bool _enabled;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly object _leaseGate = new();
    private HttpListener? _listener;
    private Task? _listenTask;
    private string? _controlToken;
    private readonly object _frameConsumerGate = new();
    private readonly HashSet<string> _frameConsumerChannels = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _controlExpiresAt;
    private int _port;
    private int _disposed;

    public YomoriMcpServer(MainViewModel viewModel, DispatcherQueue dispatcher, Action<string> log)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _log = log;
        _writeToken = (Environment.GetEnvironmentVariable("YOMORI_MCP_TOKEN") ?? string.Empty).Trim();
        _enabled = !string.Equals(Environment.GetEnvironmentVariable("YOMORI_MCP_DISABLE"), "1", StringComparison.OrdinalIgnoreCase);
        _port = ResolvePort();
    }

    public string Endpoint => $"http://127.0.0.1:{_port}/mcp/";

    public bool IsStarted => _listener?.IsListening == true;

    public void Start()
    {
        if (!_enabled)
        {
            _log("[MCP] 主控服务已通过 YOMORI_MCP_DISABLE=1 禁用。");
            return;
        }

        if (IsStarted || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(Endpoint);
            listener.Start();
            _listener = listener;
            _listenTask = ListenAsync(listener, _stop.Token);
            _log($"[MCP] 主控服务已启动：{Endpoint}，写操作令牌{(_writeToken.Length == 0 ? "未配置（只读）" : "已配置")}。");
        }
        catch (Exception ex)
        {
            _listener = null;
            _log($"[MCP] 主控服务启动失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // 退出路径上的监听器清理不应再次影响主控进程。
        }

        _inputGate.Dispose();
        _stop.Dispose();
        _log("[MCP] 主控服务已停止。");
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"[MCP] 接收请求失败：{ex.Message}");
                return;
            }

            _ = Task.Run(() => HandleRequestAsync(context), token);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                return;
            }

            if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 405;
                return;
            }

            if (context.Request.ContentLength64 > MaxRequestBytes)
            {
                await WriteJsonAsync(context, null, 413, "请求体过大").ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                await WriteJsonAsync(context, null, 400, "仅支持单个 JSON-RPC 对象").ConfigureAwait(false);
                return;
            }

            var request = document.RootElement;
            var id = GetId(request);
            var method = GetString(request, "method");
            var result = await DispatchAsync(request, method, context.Request).ConfigureAwait(false);

            if (!id.HasValue)
            {
                context.Response.StatusCode = 202;
                return;
            }

            await WriteRpcAsync(context, id, result).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(context, null, 400, $"JSON 无效：{ex.Message}").ConfigureAwait(false);
        }
        catch (McpProtocolException ex)
        {
            await WriteJsonAsync(context, null, 400, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"[MCP] 请求处理失败：{ex.Message}");
            await WriteJsonAsync(context, null, 500, "MCP 请求处理失败").ConfigureAwait(false);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // 客户端提前断开时关闭响应可能再次失败。
            }
        }
    }

    private async Task<object?> DispatchAsync(JsonElement request, string method, HttpListenerRequest httpRequest)
    {
        switch (method)
        {
            case "initialize":
                return new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "Yomori", version = "mcp-in-process-dev" },
                };

            case "notifications/initialized":
            case "ping":
                return new { };

            case "tools/list":
                return new { tools = ToolDefinitions() };

            case "tools/call":
                return await CallToolAsync(request, httpRequest).ConfigureAwait(false);

            default:
                throw new McpProtocolException(-32601, $"不支持的方法：{method}");
        }
    }

    private async Task<object> CallToolAsync(JsonElement request, HttpListenerRequest httpRequest)
    {
        var parameters = GetObject(request, "params");
        var name = GetString(parameters, "name");
        var arguments = GetObject(parameters, "arguments");

        return name switch
        {
            "yomori_status" => ToolSuccess(await BuildStatusAsync().ConfigureAwait(false)),
            "yomori_observe" => await ObserveAsync(arguments).ConfigureAwait(false),
            "yomori_connect" => await ConnectAsync(arguments, httpRequest).ConfigureAwait(false),
            "yomori_disconnect" => await DisconnectAsync(arguments, httpRequest).ConfigureAwait(false),
            "yomori_acquire_control" => AcquireControl(httpRequest, arguments),
            "yomori_release_control" => ReleaseControl(httpRequest, arguments),
            "yomori_mouse_click" => await MouseClickAsync(httpRequest, arguments).ConfigureAwait(false),
            "yomori_mouse_drag" => await MouseDragAsync(httpRequest, arguments).ConfigureAwait(false),
            "yomori_key_press" => await KeyPressAsync(httpRequest, arguments).ConfigureAwait(false),
            "yomori_stop" => StopControl(httpRequest, arguments),
            _ => ToolError($"未知工具：{name}"),
        };
    }

    private async Task<object> BuildStatusAsync()
    {
        return await OnUiAsync(() =>
        {
            var channels = new List<object>();
            foreach (var channel in _viewModel.Channels)
            {
                var client = _viewModel.GetSurfaceClient(channel.Id);
                var size = _viewModel.SurfaceLastSize(channel.Id);
                channels.Add(new
                {
                    id = channel.Id,
                    name = channel.DisplayName,
                    host = channel.Host,
                    user = channel.User,
                    enabled = channel.Enabled,
                    credentialSaved = channel.CredentialSaved,
                    embedded = client is not null,
                    state = client?.State.ToString() ?? "Idle",
                    session = client?.Session ?? 0,
                    width = size.Width,
                    height = size.Height,
                    inputLocked = _viewModel.IsSurfaceInputLocked(channel.Id),
                    poppedOut = _viewModel.IsSurfacePoppedOut(channel.Id),
                });
            }

            return new
            {
                endpoint = Endpoint,
                transport = "streamable-http-compatible-json-rpc",
                inputEnabled = _writeToken.Length > 0,
                embeddedMode = _viewModel.IsEmbeddedMode,
                channels,
                controlLease = LeaseStatus(),
            };
        }).ConfigureAwait(false);
    }

    private async Task<object> ObserveAsync(JsonElement arguments)
    {
        var channelId = RequireChannelId(arguments);
        var surface = await ResolveSurfaceAsync(channelId).ConfigureAwait(false);
        if (surface.Error is not null)
        {
            return ToolError(surface.Error);
        }

        if (surface.Client is null || surface.Client.State != RdpClientState.Connected)
        {
            return ToolError("通道当前没有已连接的内嵌画面");
        }

        if (!surface.Client.TryCopyFrame(out var width, out var height, out var stride, out var pixels))
        {
            return ToolError("当前没有可读取的画面帧");
        }

        var png = EncodePng(width, height, stride, pixels);
        var metadata = new
        {
            channelId,
            width,
            height,
            capturedAt = DateTimeOffset.UtcNow,
            state = surface.Client.State.ToString(),
        };

        return new
        {
            content = new object[]
            {
                new { type = "image", data = Convert.ToBase64String(png), mimeType = "image/png" },
                new { type = "text", text = JsonSerializer.Serialize(metadata) },
            },
            structuredContent = metadata,
        };
    }

    private async Task<object> ConnectAsync(JsonElement arguments, HttpListenerRequest request)
    {
        var authError = AuthorizeWrite(request);
        if (authError is not null)
        {
            return ToolError(authError);
        }

        var channelId = RequireChannelId(arguments);
        await OnUiAsync(() =>
        {
            _viewModel.ConnectChannelSurfaceNow(channelId);
            var client = _viewModel.GetSurfaceClient(channelId);
            if (client is not null)
            {
                lock (_frameConsumerGate)
                {
                    if (_frameConsumerChannels.Add(channelId))
                    {
                        client.AddFrameConsumer();
                    }
                }
            }
            return true;
        }).ConfigureAwait(false);

        return ToolSuccess(new { channelId, accepted = true, state = "connecting" });
    }

    private async Task<object> DisconnectAsync(JsonElement arguments, HttpListenerRequest request)
    {
        var authError = AuthorizeWrite(request);
        if (authError is not null)
        {
            return ToolError(authError);
        }

        var channelId = RequireChannelId(arguments);
        await OnUiAsync(() =>
        {
            var client = _viewModel.GetSurfaceClient(channelId);
            lock (_frameConsumerGate)
            {
                if (_frameConsumerChannels.Remove(channelId))
                {
                    client?.RemoveFrameConsumer();
                }
            }
            _viewModel.DisposeSurface(channelId, "MCP 请求断开");
            return true;
        }).ConfigureAwait(false);

        ReleaseLeaseIfChannel(channelId);
        return ToolSuccess(new { channelId, disconnected = true });
    }

    private object AcquireControl(HttpListenerRequest request, JsonElement arguments)
    {
        var authError = AuthorizeWrite(request);
        if (authError is not null)
        {
            return ToolError(authError);
        }

        var seconds = Math.Clamp(GetInt(arguments, "ttl_seconds", 30), 5, MaxLeaseSeconds);
        var channelId = RequireChannelId(arguments);
        lock (_leaseGate)
        {
            if (_controlToken is not null && _controlExpiresAt > DateTimeOffset.UtcNow)
            {
                return ToolError("已有其他 MCP 控制租约");
            }

            _controlToken = Guid.NewGuid().ToString("N");
            _controlExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            _controlChannelId = channelId;
            return ToolSuccess(new
            {
                channelId,
                controlToken = _controlToken,
                expiresAt = _controlExpiresAt,
                ttlSeconds = seconds,
            });
        }
    }

    private object ReleaseControl(HttpListenerRequest request, JsonElement arguments)
    {
        var authError = AuthorizeWrite(request);
        if (authError is not null)
        {
            return ToolError(authError);
        }

        var controlError = ValidateLease(arguments, requireChannel: false);
        if (controlError is not null)
        {
            return ToolError(controlError);
        }

        ClearLease();
        return ToolSuccess(new { released = true });
    }

    private async Task<object> MouseClickAsync(HttpListenerRequest request, JsonElement arguments)
    {
        var authError = AuthorizeWrite(request);
        if (authError is not null)
        {
            return ToolError(authError);
        }

        var channelId = RequireChannelId(arguments);
        var leaseError = ValidateLease(arguments, requireChannel: true, channelId);
        if (leaseError is not null)
        {
            return ToolError(leaseError);
        }

        var x = GetInt(arguments, "x", -1);
        var y = GetInt(arguments, "y", -1);
        if (x < 0 || y < 0 || x > ushort.MaxValue || y > ushort.MaxValue)
        {
            return ToolError("坐标必须是非负的 16 位整数");
        }

        if (!await _inputGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
        {
            return ToolError("输入通道正忙");
        }

        try
        {
            var surface = await ResolveSurfaceAsync(channelId).ConfigureAwait(false);
            if (surface.Error is not null)
            {
                return ToolError(surface.Error);
            }

            if (surface.Client is null || surface.Client.State != RdpClientState.Connected)
            {
                return ToolError("通道当前没有可输入的内嵌连接");
            }

            if (surface.InputLocked)
            {
                return ToolError("通道输入已被锁定");
            }

            if (!surface.Client.TryCopyFrame(out var width, out var height, out _, out _)
                || x >= width || y >= height)
            {
                return ToolError("坐标超出当前画面范围，或当前没有新画面");
            }

            var client = surface.Client;
            await McpMouseClick.ExecuteAsync(client.SendMouseDispatchedAsync, async () =>
            {
                var error = ValidateLease(arguments, requireChannel: true, channelId);
                if (error is not null) throw new InvalidOperationException(error);
                var current = await ResolveSurfaceAsync(channelId).ConfigureAwait(false);
                if (current.Error is not null || !ReferenceEquals(current.Client, client)
                    || client.State != RdpClientState.Connected || current.InputLocked)
                    throw new InvalidOperationException("Mouse input channel changed or locked.");
                if (!client.TryCopyFrame(out var currentWidth, out var currentHeight, out _, out _)
                    || currentWidth != width || currentHeight != height)
                    throw new InvalidOperationException("Remote desktop dimensions changed.");
            }, (ushort)x, (ushort)y).ConfigureAwait(false);
            return ToolSuccess(new { channelId, x, y, width, height,
                move = true, down = true, up = true, transportDispatched = true,
                holdMilliseconds = McpMouseClick.HoldMilliseconds, gameResponseVerified = false });
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or AggregateException)
        {
            return ToolError("Mouse click failed or unconfirmed; observe before another input: " + error.Message);
        }
        finally
        {
            _inputGate.Release();
        }
    }
    private async Task<object> MouseDragAsync(HttpListenerRequest request, JsonElement arguments)
    {
        var authError = AuthorizeWrite(request);
        if (authError is not null) return ToolError(authError);

        var channelId = RequireChannelId(arguments);
        var leaseError = ValidateLease(arguments, requireChannel: true, channelId);
        if (leaseError is not null) return ToolError(leaseError);

        var startX = GetInt(arguments, "start_x", -1);
        var startY = GetInt(arguments, "start_y", -1);
        var endX = GetInt(arguments, "end_x", -1);
        var endY = GetInt(arguments, "end_y", -1);
        if (startX < 0 || startY < 0 || endX < 0 || endY < 0
            || startX > ushort.MaxValue || startY > ushort.MaxValue
            || endX > ushort.MaxValue || endY > ushort.MaxValue)
            return ToolError("拖动坐标必须是非负的 16 位整数");

        if (!await _inputGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            return ToolError("输入通道正忙");

        try
        {
            var surface = await ResolveSurfaceAsync(channelId).ConfigureAwait(false);
            if (surface.Error is not null) return ToolError(surface.Error);
            if (surface.Client is null || surface.Client.State != RdpClientState.Connected)
                return ToolError("通道当前没有可输入的内嵌连接");
            if (surface.InputLocked) return ToolError("通道输入已被锁定");
            if (!surface.Client.TryCopyFrame(out var width, out var height, out _, out _)
                || startX >= width || startY >= height || endX >= width || endY >= height)
                return ToolError("拖动坐标超出当前画面范围，或当前没有新画面");

            var client = surface.Client;
            async Task Validate()
            {
                var error = ValidateLease(arguments, requireChannel: true, channelId);
                if (error is not null) throw new InvalidOperationException(error);
                var current = await ResolveSurfaceAsync(channelId).ConfigureAwait(false);
                if (current.Error is not null || !ReferenceEquals(current.Client, client)
                    || client.State != RdpClientState.Connected || current.InputLocked)
                    throw new InvalidOperationException("Mouse drag channel changed or locked.");
                if (!client.TryCopyFrame(out var currentWidth, out var currentHeight, out _, out _)
                    || currentWidth != width || currentHeight != height)
                    throw new InvalidOperationException("Remote desktop dimensions changed.");
            }

            await McpMouseDrag.ExecuteAsync(client.SendMouseDispatchedAsync, Validate,
                (ushort)startX, (ushort)startY, (ushort)endX, (ushort)endY).ConfigureAwait(false);
            return ToolSuccess(new { channelId, startX, startY, endX, endY, width, height,
                move = true, down = true, dragMove = true, up = true,
                transportDispatched = true, preDragMilliseconds = McpMouseDrag.PreDragMilliseconds,
                holdMilliseconds = McpMouseDrag.HoldMilliseconds, dragSteps = McpMouseDrag.DragSteps,
                dragStepMilliseconds = McpMouseDrag.DragStepMilliseconds, gameResponseVerified = false });
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or AggregateException)
        {
            return ToolError("Mouse drag failed or unconfirmed; observe before another input: " + error.Message);
        }
        finally
        {
            _inputGate.Release();
        }
    }


    private async Task<object> KeyPressAsync(HttpListenerRequest request, JsonElement arguments)
    {
        var authError = AuthorizeWrite(request);
        if (authError is not null)
        {
            return ToolError(authError);
        }

        var channelId = RequireChannelId(arguments);
        var leaseError = ValidateLease(arguments, requireChannel: true, channelId);
        if (leaseError is not null)
        {
            return ToolError(leaseError);
        }

        var scancode = GetInt(arguments, "scancode", -1);
        if (scancode <= 0 || scancode > ushort.MaxValue)
        {
            return ToolError("scancode 必须是正的 16 位整数");
        }

        if (!await _inputGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
        {
            return ToolError("输入通道正忙");
        }

        try
        {
            var surface = await ResolveSurfaceAsync(channelId).ConfigureAwait(false);
            if (surface.Error is not null)
            {
                return ToolError(surface.Error);
            }

            if (surface.Client is null || surface.Client.State != RdpClientState.Connected)
            {
                return ToolError("通道当前没有可输入的内嵌连接");
            }

            if (surface.InputLocked)
            {
                return ToolError("通道输入已被锁定");
            }

            var extended = GetBool(arguments, "extended", false);
            var client = surface.Client;
            var session = client.Session;
            await McpKeyPress.ExecuteAsync(
                down => RdpNativeInterop.ycn_rdp_send_key(session, down ? 1 : 0, extended ? 1 : 0, (ushort)scancode) == 0,
                async () =>
                {
                    var error = ValidateLease(arguments, requireChannel: true, channelId);
                    if (error is not null) throw new InvalidOperationException(error);
                    var current = await ResolveSurfaceAsync(channelId).ConfigureAwait(false);
                    if (session <= 0 || current.Error is not null || !ReferenceEquals(current.Client, client)
                        || client.Session != session || client.State != RdpClientState.Connected || current.InputLocked)
                        throw new InvalidOperationException("Keyboard input channel changed or locked.");
                }).ConfigureAwait(false);
            return ToolSuccess(new { channelId, scancode, extended, down = true, up = true,
                queueHoldMilliseconds = McpKeyPress.QueueHoldMilliseconds,
                receipt = "queue-admission", dispatchVerified = false, gameResponseVerified = false });
        }
        catch (Exception error) when (error is InvalidOperationException or AggregateException)
        {
            return ToolError("Key press failed or unconfirmed; observe before another input: " + error.Message);
        }
        finally
        {
            _inputGate.Release();
        }
    }

    private object StopControl(HttpListenerRequest request, JsonElement arguments)
    {
        var authError = AuthorizeWrite(request);
        if (authError is not null)
        {
            return ToolError(authError);
        }

        var controlError = ValidateLease(arguments, requireChannel: false);
        if (controlError is not null)
        {
            return ToolError(controlError);
        }

        ClearLease();
        return ToolSuccess(new { stopped = true, inputLeaseReleased = true });
    }

    private async Task<SurfaceSnapshot> ResolveSurfaceAsync(string channelId)
    {
        return await OnUiAsync(() =>
        {
            var channel = _viewModel.Channels.FirstOrDefault(item =>
                string.Equals(item.Id, channelId, StringComparison.OrdinalIgnoreCase));
            if (channel is null)
            {
                return new SurfaceSnapshot(null, false, $"找不到通道：{channelId}");
            }

            return new SurfaceSnapshot(
                _viewModel.GetSurfaceClient(channel.Id),
                _viewModel.IsSurfaceInputLocked(channel.Id),
                null);
        }).ConfigureAwait(false);
    }

    private async Task<T> OnUiAsync<T>(Func<T> callback)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return callback();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    completion.SetResult(callback());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            throw new InvalidOperationException("Yomori UI 调度器已经关闭");
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private string? AuthorizeWrite(HttpListenerRequest request)
    {
        if (_writeToken.Length == 0)
        {
            return "未配置 YOMORI_MCP_TOKEN，当前 MCP 仅开放只读工具";
        }

        return string.Equals(request.Headers[TokenHeader], _writeToken, StringComparison.Ordinal)
            ? null
            : "缺少有效的 MCP 写操作令牌";
    }

    private string? ValidateLease(JsonElement arguments, bool requireChannel, string? channelId = null)
    {
        var token = GetString(arguments, "control_token");
        lock (_leaseGate)
        {
            if (_controlToken is null || _controlExpiresAt <= DateTimeOffset.UtcNow)
            {
                ClearLeaseUnsafe();
                return "没有有效的 MCP 控制租约";
            }

            if (!string.Equals(token, _controlToken, StringComparison.Ordinal))
            {
                return "MCP 控制租约无效";
            }

            if (requireChannel && !string.Equals(_controlChannelId, channelId, StringComparison.OrdinalIgnoreCase))
            {
                return "控制租约不属于当前通道";
            }
        }

        return null;
    }

    private object LeaseStatus()
    {
        lock (_leaseGate)
        {
            if (_controlToken is null || _controlExpiresAt <= DateTimeOffset.UtcNow)
            {
                ClearLeaseUnsafe();
                return new { active = false };
            }

            return new { active = true, channelId = _controlChannelId, expiresAt = _controlExpiresAt };
        }
    }

    private string? _controlChannelId;

    private void ReleaseLeaseIfChannel(string channelId)
    {
        lock (_leaseGate)
        {
            if (string.Equals(_controlChannelId, channelId, StringComparison.OrdinalIgnoreCase))
            {
                ClearLeaseUnsafe();
            }
        }
    }

    private void ClearLease()
    {
        lock (_leaseGate)
        {
            ClearLeaseUnsafe();
        }
    }

    private void ClearLeaseUnsafe()
    {
        _controlToken = null;
        _controlChannelId = null;
        _controlExpiresAt = default;
    }

    private static object[] ToolDefinitions()
    {
        var channel = new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = "Yomori 通道 id",
        };
        var writeChannel = new Dictionary<string, object>(channel);

        return new object[]
        {
            new
            {
                name = "yomori_status",
                description = "读取 Yomori 主控、通道、内嵌连接和输入锁状态。",
                inputSchema = new { type = "object", properties = new Dictionary<string, object>() },
            },
            new
            {
                name = "yomori_observe",
                description = "读取当前通道的最新画面。",
                inputSchema = new { type = "object", properties = new Dictionary<string, object> { ["channel_id"] = channel }, required = new[] { "channel_id" } },
            },
            new
            {
                name = "yomori_connect",
                description = "连接已经配置好的内嵌通道，不接收账户凭据。",
                inputSchema = new { type = "object", properties = new Dictionary<string, object> { ["channel_id"] = writeChannel }, required = new[] { "channel_id" } },
            },
            new
            {
                name = "yomori_disconnect",
                description = "断开指定内嵌画面连接并释放 MCP 租约。",
                inputSchema = new { type = "object", properties = new Dictionary<string, object> { ["channel_id"] = writeChannel }, required = new[] { "channel_id" } },
            },
            new
            {
                name = "yomori_acquire_control",
                description = "获取单通道短期输入租约。",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["channel_id"] = channel,
                        ["ttl_seconds"] = new { type = "integer", minimum = 5, maximum = MaxLeaseSeconds },
                    },
                    required = new[] { "channel_id" },
                },
            },
            new
            {
                name = "yomori_release_control",
                description = "释放当前 MCP 输入租约。",
                inputSchema = new { type = "object", properties = new Dictionary<string, object> { ["control_token"] = new { type = "string" } }, required = new[] { "control_token" } },
            },
            new
            {
                name = "yomori_mouse_click",
                description = "在当前画面坐标执行一次左键点击。每次调用都会重新检查画面尺寸和输入锁。",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["channel_id"] = channel,
                        ["control_token"] = new { type = "string" },
                        ["x"] = new { type = "integer", minimum = 0 },
                        ["y"] = new { type = "integer", minimum = 0 },
                    },
                    required = new[] { "channel_id", "control_token", "x", "y" },
                },
            },
            new
            {
                name = "yomori_mouse_drag",
                description = "在当前画面执行一次受控左键拖动。每个事件都会等待 Native 发送回执。",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["channel_id"] = channel,
                        ["control_token"] = new { type = "string" },
                        ["start_x"] = new { type = "integer", minimum = 0 },
                        ["start_y"] = new { type = "integer", minimum = 0 },
                        ["end_x"] = new { type = "integer", minimum = 0 },
                        ["end_y"] = new { type = "integer", minimum = 0 },
                    },
                    required = new[] { "channel_id", "control_token", "start_x", "start_y", "end_x", "end_y" },
                },
            },
            new
            {
                name = "yomori_key_press",
                description = "向当前通道发送一次扫描码短按。",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["channel_id"] = channel,
                        ["control_token"] = new { type = "string" },
                        ["scancode"] = new { type = "integer", minimum = 1, maximum = ushort.MaxValue },
                        ["extended"] = new { type = "boolean" },
                    },
                    required = new[] { "channel_id", "control_token", "scancode" },
                },
            },
            new
            {
                name = "yomori_stop",
                description = "立即释放 MCP 输入租约。",
                inputSchema = new { type = "object", properties = new Dictionary<string, object> { ["control_token"] = new { type = "string" } }, required = new[] { "control_token" } },
            },
        };
    }

    private static object ToolSuccess(object data)
        => new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(data) } },
            structuredContent = data,
        };

    private static object ToolError(string message)
        => new
        {
            isError = true,
            content = new[] { new { type = "text", text = message } },
        };

    private async Task WriteRpcAsync(HttpListenerContext context, JsonElement? id, object? result)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Mcp-Session-Id"] = _sessionId;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, result });
        await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, JsonElement? id, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code = -32600, message },
        });
        await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
    }

    private static int ResolvePort()
    {
        var value = Environment.GetEnvironmentVariable("YOMORI_MCP_PORT");
        return int.TryParse(value, out var port) && port is >= 1024 and <= 65535
            ? port
            : DefaultPort;
    }

    private static JsonElement? GetId(JsonElement request)
        => request.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Undefined
            ? id.Clone()
            : null;

    private static JsonElement GetObject(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string GetString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string RequireChannelId(JsonElement arguments)
    {
        var value = GetString(arguments, "channel_id");
        return value.Length > 0 ? value : throw new McpProtocolException(-32602, "缺少 channel_id");
    }

    private static int GetInt(JsonElement parent, string name, int fallback)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result)
            ? result
            : fallback;

    private static bool GetBool(JsonElement parent, string name, bool fallback)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var value)
           && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;

    private static byte[] EncodePng(uint width, uint height, uint sourceStride, byte[] pixels)
    {
        using var bitmap = new Bitmap((int)width, (int)height, PixelFormat.Format32bppArgb);
        var rectangle = new Rectangle(0, 0, (int)width, (int)height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked((int)(width * 4));
            for (var row = 0; row < (int)height; row++)
            {
                var sourceOffset = checked(row * (int)sourceStride);
                var destination = IntPtr.Add(data.Scan0, row * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(pixels, sourceOffset, destination, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private sealed record SurfaceSnapshot(RdpEmbeddedClient? Client, bool InputLocked, string? Error);

    private sealed class McpProtocolException : Exception
    {
        public McpProtocolException(int code, string message) : base(message) => Code = code;
        public int Code { get; }
    }
}
