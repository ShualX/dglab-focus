using System.Text.Json.Nodes;

namespace DglabFocus.Core;

public sealed record DglabDevice(string ClientId, string SlotId, string Name, string Type, JsonObject Props, JsonObject SlotState)
{
    public string Key => ClientId + "/" + SlotId;
    public string Display => Name + " · " + Type + " · " + ClientId[..Math.Min(6, ClientId.Length)];
    public override string ToString() => Display;

    public bool TryLimit(int channel, out double maximum, out string reason)
    {
        maximum = 0;
        reason = "设备或通道状态尚未就绪。请在 App 中连接主机并开放设备控制。";
        if (channel is not (0 or 1) || Type != "COYOTE_030") { reason = "当前设备输出支持郊狼 3 主机（COYOTE_030）与 DG-LAB 4 App。"; return false; }
        if (Bool(SlotState, "hasDevice") != true || Text(Props, "connectState") != "connected") return false;
        var key = channel == 0 ? "channelA" : "channelB";
        if (SlotState[key] is not JsonObject state || Bool(state, "isMuted") != false) { reason = "通道静音或通道状态未知，请先在 App 中确认。"; return false; }
        if (state["comfortLimit"] is JsonObject limit && Bool(limit, "overheat") == true) { reason = "App 报告通道过热，输出已停止。"; return false; }
        var status = Number(Props, channel == 0 ? "channelAStatus" : "channelBStatus");
        if (status is 1 or 3 or 4) { reason = "App 报告通道未形成回路、故障或屏蔽，输出已停止。"; return false; }
        var value = Number(state, "intensityMax");
        if (value == null || !double.IsFinite(value.Value) || value <= 0) { reason = "App 尚未报告有效的通道强度上限。"; return false; }
        maximum = Math.Min(100, value.Value);
        reason = "通道已就绪";
        return true;
    }

    public static double? Number(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<double>(out var n) ? n : null;
    public static bool? Bool(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public static string? Text(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

// Independent implementation of the documented V4 wire protocol; no SDK code or waveform assets are bundled.
public static class DglabProtocol
{
    public const int LeaseMilliseconds = 1000;
    public static JsonObject Operation(string slot, int channel, int type, JsonNode value, int duration = 0) => new()
    {
        ["s"] = slot, ["c"] = channel, ["t"] = type, ["p"] = 1, ["d"] = duration, ["im"] = true, ["v"] = value
    };
    public static JsonObject TemporaryIntensity(string slot, int channel, int intensity)
    {
        if (channel is not (0 or 1) || intensity is < 0 or > 100 || string.IsNullOrWhiteSpace(slot)) throw new ArgumentException("设备输出参数无效。");
        return Operation(slot, channel, 4, JsonValue.Create(intensity)!, LeaseMilliseconds);
    }
    public static JsonObject Pulse(string slot, int channel)
    {
        // Uniform V3 frame: four frequency bytes and four normalized amplitude bytes.
        // Finite frames and a finite duration prevent a stopped desktop process from queuing endless output.
        var frames = new JsonArray();
        for (int i = 0; i < 10; i++) frames.Add("0A0A0A0A14141414");
        return Operation(slot, channel, 0, frames, LeaseMilliseconds);
    }
    public static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var item in patch)
        {
            if (target[item.Key] is JsonObject existing && item.Value is JsonObject nested) Merge(existing, nested);
            else target[item.Key] = item.Value?.DeepClone();
        }
    }
}
