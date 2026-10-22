using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DglabFocus.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DglabFocus.Windows;

public sealed record LanAddress(string Name, IPAddress Address, bool Preferred)
{
    public string Display => $"{Name} · {Address}";
    public static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork &&
            (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168);
    }
    public static IReadOnlyList<LanAddress> Find() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n =>
        {
            var properties = n.GetIPProperties();
            var virtualAdapter = new[] { "vmware", "virtual", "vpn", "tun", "hyper-v", "tap" }
                .Any(word => (n.Name + " " + n.Description).Contains(word, StringComparison.OrdinalIgnoreCase));
            var preferred = !virtualAdapter && properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any));
            return properties.UnicastAddresses.Where(a => IsPrivate(a.Address)).Select(a => new LanAddress(n.Name, a.Address, preferred));
        }).OrderByDescending(x => x.Preferred).ThenBy(x => x.Name).ToList();
}

public static class DglabPairing
{
    public static Uri SocketUri(Uri relay, string controllerId) => new UriBuilder(relay)
    { Query = "tid=" + Uri.EscapeDataString(controllerId) }.Uri;
    public static string QrUrl(Uri socket) => "https://dungeon-lab.cn/s/?v=1&action=socket&url=" + Uri.EscapeDataString(socket.AbsoluteUri);
}

// Independent implementation of the public V4 relay wire protocol; no device commands are interpreted here.
public sealed class LocalDglabRelay : IAsyncDisposable
{
    public const int Port = 17841;
    private readonly object sync = new();
    private readonly ConcurrentDictionary<string, Peer> phones = new();
    private readonly CancellationTokenSource lifetime = new();
    private WebApplication? server;
    private Peer? controller;
    private bool phoneAttached;
    public Uri Address { get; private set; } = null!;

    private sealed class Peer(WebSocket socket)
    {
        public string Id { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim Gate { get; } = new(1);
        public async Task Send(JsonObject frame, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await Gate.WaitAsync(timeout.Token);
            try { await Socket.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, timeout.Token); }
            finally { Gate.Release(); }
        }
    }

    public async Task StartAsync(IPAddress address, int port = Port)
    {
        if (server != null) throw new InvalidOperationException("局域网服务已启动。");
        if (!IPAddress.IsLoopback(address) && !LanAddress.IsPrivate(address)) throw new ArgumentException("只允许绑定私有 IPv4 局域网地址。");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port));
        var app = builder.Build(); server = app;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Run(context => Handle(context, address));
        try
        {
            await app.StartAsync(lifetime.Token);
            var endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Address = new UriBuilder(endpoint) { Scheme = "ws", Path = "/v4" }.Uri;
        }
        catch { await DisposeAsync(); throw; }
    }

    private async Task Handle(HttpContext context, IPAddress local)
    {
        if (context.Request.Path != "/v4" || !context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 404; return; }
        var tid = context.Request.Query["tid"].ToString();
        if (string.IsNullOrEmpty(tid)) tid = context.Request.Query["targetId"].ToString();
        var isController = string.IsNullOrEmpty(tid);
        // Browsers on the LAN cannot create a controller. Phone access requires the random pairing secret.
        if (isController && (!Equals(context.Connection.RemoteIpAddress, local) || context.Request.Headers.ContainsKey("Origin")))
        { context.Response.StatusCode = 403; return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, context.RequestAborted);
        var ct = linked.Token;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var peer = new Peer(socket);
        Peer? target;
        lock (sync)
        {
            target = controller;
            if (isController && controller == null) { controller = peer; phoneAttached = false; }
            else if (!isController && target?.Id == tid && phones.Count < 4) { phones[peer.Id] = peer; phoneAttached = true; }
        }
        try
        {
            if (isController && !ReferenceEquals(controller, peer) || !isController && !phones.ContainsKey(peer.Id))
            {
                await peer.Send(new() { ["type"] = "error", ["code"] = isController ? "controller_busy" : "controller_not_found" }, ct);
                await socket.CloseOutputAsync((WebSocketCloseStatus)4001, "pairing unavailable", ct); return;
            }
            await peer.Send(new() { ["type"] = "hello", ["clientId"] = peer.Id }, ct);
            if (isController) _ = ExpireUnusedPairing(peer, ct);
            if (!isController)
            {
                await peer.Send(new() { ["type"] = "controller_attached", ["clientId"] = target!.Id }, ct);
                await target.Send(new() { ["type"] = "client_attached", ["clientId"] = peer.Id }, ct);
            }
            var bytes = new byte[262144];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var length = 0; WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), ct);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    length += part.Count;
                    if (part.MessageType != WebSocketMessageType.Text || length >= bytes.Length) throw new InvalidDataException();
                } while (!part.EndOfMessage);
                if (JsonNode.Parse(bytes.AsSpan(0, length)) is not JsonObject frame) throw new InvalidDataException();
                var type = DglabDevice.Text(frame, "type");
                if (type == "ping") await peer.Send(new() { ["type"] = "pong", ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);
                else if (type == "message" && frame["data"] is JsonNode data)
                {
                    if (isController)
                    {
                        if (DglabDevice.Text(frame, "clientId") is { } id && phones.TryGetValue(id, out var phone))
                            await phone.Send(new() { ["type"] = "message", ["data"] = data.DeepClone() }, ct);
                    }
                    else await target!.Send(new() { ["type"] = "message", ["clientId"] = peer.Id, ["data"] = data.DeepClone() }, ct);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or JsonException or InvalidOperationException or ArgumentException) { }
        finally
        {
            if (isController)
            {
                Peer[] detached = [];
                lock (sync)
                {
                    if (ReferenceEquals(controller, peer)) { controller = null; detached = phones.Values.ToArray(); phones.Clear(); }
                }
                foreach (var phone in detached)
                {
                    try
                    {
                        await phone.Send(new() { ["type"] = "controller_disconnected", ["clientId"] = peer.Id }, lifetime.Token);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await phone.Socket.CloseOutputAsync((WebSocketCloseStatus)4000, "controller disconnected", timeout.Token);
                        _ = AbortAfterGrace(phone.Socket);
                    }
                    catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException) { phone.Socket.Abort(); }
                }
            }
            else if (phones.TryRemove(peer.Id, out _) && target != null)
            {
                try { await target.Send(new() { ["type"] = "client_disconnected", ["clientId"] = peer.Id }, lifetime.Token); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidOperationException) { }
            }
            linked.Cancel();
        }
    }

    private static async Task AbortAfterGrace(WebSocket socket)
    {
        // Allow the close frame and final notification to reach the App before releasing a silent peer.
        await Task.Delay(TimeSpan.FromSeconds(2));
        try { socket.Abort(); } catch (ObjectDisposedException) { }
    }

    private async Task ExpireUnusedPairing(Peer peer, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            lock (sync) { if (ReferenceEquals(controller, peer) && !phoneAttached) peer.Socket.Abort(); }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        controller?.Socket.Abort(); foreach (var peer in phones.Values) peer.Socket.Abort();
        if (server is { } app)
        {
            server = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
            await app.DisposeAsync();
        }
    }
}
