using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DglabFocus.Windows;

internal static class LocalRelayIntegration
{
    private static async Task<ClientWebSocket> Connect(Uri address, string? origin = null)
    {
        var socket = new ClientWebSocket(); socket.Options.Proxy = null;
        if (origin != null) socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await socket.ConnectAsync(address, timeout.Token); return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    private static async Task<JsonObject> Read(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var buffer = new byte[262144]; var length = 0; WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
            if (part.MessageType == WebSocketMessageType.Close) throw new IOException("unexpected socket close");
            length += part.Count;
        } while (!part.EndOfMessage);
        return JsonNode.Parse(buffer.AsSpan(0, length))!.AsObject();
    }
    private static async Task Send(ClientWebSocket socket, JsonObject frame)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame.ToJsonString()), WebSocketMessageType.Text, true, timeout.Token);
    }
    private static async Task Until(Func<bool> condition)
    {
        var end = Environment.TickCount64 + 4500;
        while (!condition()) { if (Environment.TickCount64 >= end) throw new TimeoutException(); await Task.Delay(20); }
    }
    public static async Task Run(Action<bool, string> assert)
    {
        assert(LanAddress.IsPrivate(IPAddress.Parse("192.168.3.54")) && LanAddress.IsPrivate(IPAddress.Parse("172.16.0.2")) &&
            !LanAddress.IsPrivate(IPAddress.Parse("198.18.0.1")) && !LanAddress.IsPrivate(IPAddress.Parse("8.8.8.8")), "LAN discovery excludes public and proxy tunnel IPs");
        var escaped = DglabPairing.SocketUri(new("wss://relay.example/v4"), "a+b/c?d");
        assert(escaped.AbsolutePath == "/v4" && Uri.UnescapeDataString(escaped.Query[5..]) == "a+b/c?d", "pairing preserves relay path and escapes target ID exactly once");
        await using var relay = new LocalDglabRelay(); await relay.StartAsync(IPAddress.Loopback, 0);
        var denied = false;
        try { using var unexpected = await Connect(relay.Address, "https://untrusted.example"); }
        catch (WebSocketException) { denied = true; }
        assert(denied, "a browser page cannot establish a LAN controller");
        using var controller = await Connect(relay.Address);
        var hello = await Read(controller); var id = hello["clientId"]!.GetValue<string>();
        assert(hello["type"]!.GetValue<string>() == "hello" && id.Length == 48, "LAN controller receives a random 192-bit pairing credential");
        using (var invalid = await Connect(DglabPairing.SocketUri(relay.Address, "invalid")))
            assert((await Read(invalid))["code"]!.GetValue<string>() == "controller_not_found", "unknown pairing credential is rejected");
        using (var duplicate = await Connect(relay.Address))
            assert((await Read(duplicate))["code"]!.GetValue<string>() == "controller_busy", "a second controller cannot replace the active one");
        using var phone = await Connect(DglabPairing.SocketUri(relay.Address, id));
        var phoneId = (await Read(phone))["clientId"]!.GetValue<string>();
        var attached = await Read(phone); var controllerAttached = await Read(controller);
        assert(attached["type"]!.GetValue<string>() == "controller_attached" && attached["clientId"]!.GetValue<string>() == id &&
            controllerAttached["clientId"]!.GetValue<string>() == phoneId, "phone and controller receive the official V4 pairing frames");
        using var secondPhone = await Connect(DglabPairing.SocketUri(relay.Address, id));
        var secondId = (await Read(secondPhone))["clientId"]!.GetValue<string>();
        await Read(secondPhone); await Read(controller);
        await Send(controller, new() { ["type"] = "message", ["clientId"] = phoneId, ["data"] = new JsonObject { ["probe"] = 7 } });
        var forwarded = await Read(phone);
        await Send(secondPhone, new() { ["type"] = "ping" });
        var secondReply = await Read(secondPhone);
        assert(forwarded["data"]!["probe"]!.GetValue<int>() == 7 && forwarded["clientId"] == null && secondReply["type"]!.GetValue<string>() == "pong",
            "controller messages reach only the selected phone with the documented envelope");
        await Send(phone, new() { ["type"] = "message", ["clientId"] = secondId, ["data"] = new JsonObject { ["answer"] = true } });
        var report = await Read(controller);
        assert(report["clientId"]!.GetValue<string>() == phoneId && report["data"]!["answer"]!.GetValue<bool>(), "phone cannot spoof another phone's message identity");
        await Send(controller, new() { ["type"] = "message", ["clientId"] = "unknown", ["data"] = new JsonObject() });
        await Send(controller, new() { ["type"] = "ping" });
        assert((await Read(controller))["type"]!.GetValue<string>() == "pong", "unknown message destinations do not disturb the session");
        secondPhone.Abort();
        assert((await Read(controller))["clientId"]!.GetValue<string>() == secondId, "phone disconnect is propagated to the controller");
        await phone.SendAsync(new byte[262144], WebSocketMessageType.Text, true, CancellationToken.None);
        assert((await Read(controller))["clientId"]!.GetValue<string>() == phoneId, "oversized phone messages disconnect and remove the peer");
        controller.Abort();
        // Wait until the server has retired the old controller; no second server or real network interface is opened.
        using var desktop = new DglabController();
        string? qr = null;
        desktop.Updated += (_, url) => { if (!string.IsNullOrEmpty(url)) qr = url; };
        for (int attempt = 0; attempt < 30 && desktop.PairingSocketUrl == null; attempt++)
        { await Task.Delay(30); await desktop.Connect(relay.Address); await Task.Delay(30); }
        await Until(() => desktop.PairingSocketUrl != null);
        var newSocket = new Uri(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(qr!).Query)["url"].ToString());
        assert(newSocket.AbsoluteUri == desktop.PairingSocketUrl, "the URL encoded in the actual QR matches the copyable socket address");
        assert(newSocket.Query != "?tid=" + id && !desktop.IsArmed, "new desktop connection generates a fresh credential without output authorization");
        using (var oldPhone = await Connect(DglabPairing.SocketUri(relay.Address, id)))
            assert((await Read(oldPhone))["code"]!.GetValue<string>() == "controller_not_found", "old QR is invalid after reconnecting");
        using var app = await Connect(newSocket); await Read(app); await Read(app);
        var discovery = await Read(app);
        assert(discovery["data"]!["m"]!.GetValue<string>() == "devices.get", "actual Windows controller discovers the phone through the built-in relay");
        await Send(app, new() { ["type"] = "message", ["data"] = new JsonObject { ["t"] = "resp", ["reqId"] = discovery["data"]!["reqId"]!.GetValue<string>(),
            ["result"] = new JsonObject { ["devices"] = new JsonArray() } } });
        await Until(() => desktop.Status.StartsWith("已发现"));
        assert(!desktop.IsArmed && desktop.PairingSocketUrl == null, "an App with no hardware still pairs and clears the QR without granting output");
        await desktop.DisconnectAsync();
        assert(desktop.PairingSocketUrl == null && !desktop.IsConnected, "disconnect clears manual pairing address as well as device authorization");
        assert((await Read(app))["type"]!.GetValue<string>() == "controller_disconnected", "desktop disconnect notifies the paired phone");
        await desktop.Connect(new UriBuilder(relay.Address) { Path = "/missing" }.Uri);
        assert(desktop.Status.Contains("HTTP 404") && desktop.PairingSocketUrl == null, "failed HTTP handshake keeps its status code and clears the QR");
    }
}
