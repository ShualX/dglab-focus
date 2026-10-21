using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DglabFocus.Windows;
public sealed record BrowserReport(string Browser, string Url, bool Focused, bool Ambiguous, DateTimeOffset ReceivedAt);
public sealed class BrowserBridge(string token) : IDisposable
{
    public const int Port = 17839;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Guid, BrowserReport> reports = new();
    public string Status { get; private set; } = "等待扩展连接";
    public void Start()
    {
        try
        {
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _ = Listen();
        }
        catch (HttpListenerException) { Status = "本地桥接端口不可用，网页检测已暂停"; }
    }
    private async Task Listen()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try { var ctx = await listener.GetContextAsync(); _ = Serve(ctx); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
        }
    }
    private async Task Serve(HttpListenerContext context)
    {
        var id = Guid.NewGuid();
        WebSocket? ws = null;
        try
        {
            var origin = context.Request.Headers["Origin"] ?? "";
            if (context.Request.Url?.AbsolutePath != "/browser" || !context.Request.IsWebSocketRequest
                || !System.Text.RegularExpressions.Regex.IsMatch(origin, "^chrome-extension://[a-p]{32}$"))
            { context.Response.StatusCode = 403; context.Response.Close(); return; }
            ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            authTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var auth = JsonSerializer.Deserialize<AuthMessage>(await Receive(ws, authTimeout.Token), JsonOptions);
            if (auth?.Type != "auth" || auth.Browser is not ("Chrome" or "Edge") || auth.Token == null
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth.Token), Encoding.UTF8.GetBytes(token)))
            { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid pairing", lifetime.Token); return; }
            await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"ready\"}"), WebSocketMessageType.Text, true, lifetime.Token);
            while (ws.State == WebSocketState.Open)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var data = JsonSerializer.Deserialize<ReportMessage>(await Receive(ws, timeout.Token), JsonOptions);
                if (data?.Type != "report" || data.Url == null || data.Url.Length > 6000) continue;
                reports[id] = new(auth.Browser, data.Url, data.Focused, data.Ambiguous, DateTimeOffset.UtcNow);
                Status = "扩展已连接";
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or HttpListenerException or InvalidDataException) { }
        finally { reports.TryRemove(id, out _); ws?.Dispose(); }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record AuthMessage(string Type, string? Token, string? Browser);
    private sealed record ReportMessage(string Type, string? Url, bool Focused, bool Ambiguous);
    private static async Task<string> Receive(WebSocket ws, CancellationToken ct)
    {
        var bytes = new byte[8192];
        var length = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), ct);
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException();
            length += result.Count;
            if (length >= bytes.Length) throw new InvalidDataException();
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
    public BrowserReport? Get(string browser, DateTimeOffset now)
    {
        var fresh = reports.Values.Where(x => x.Browser == browser && x.Focused && now - x.ReceivedAt < TimeSpan.FromSeconds(12)).ToList();
        // Multiple focused browser profiles cannot be mapped reliably to a Win32 HWND. Abstain.
        return fresh.Count == 1 && !fresh[0].Ambiguous ? fresh[0] : null;
    }
    public int ConnectionCount(DateTimeOffset now) => reports.Values.Count(x => now - x.ReceivedAt < TimeSpan.FromSeconds(12));
    public void Dispose() { lifetime.Cancel(); listener.Close(); }
}
