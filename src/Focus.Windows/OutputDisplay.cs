using DglabFocus.Core;

namespace DglabFocus.Windows;

public sealed record OutputDisplay(string Status, string Value, string Detail, string BallStatus, string BallValue, double Level, double Maximum)
{
    public static OutputDisplay Create(SessionOptions? options, double preview, DeviceOutputState output)
    {
        if (output.Authorized)
        {
            var channel = output.Channel == 0 ? "A" : "B";
            return new(output.Requested > 0 ? $"正在发送 · {channel} 通道" : $"真实反馈待命 · {channel} 通道",
                $"{output.Requested} / {output.Limit}", "当前请求强度 / 本次上限",
                output.Requested > 0 ? $"输出 {channel}" : "待命", $"{output.Requested}", output.Requested, output.Limit);
        }
        if (options == null) return new("未选择方案", "—", "选择方案后开始专注", "待选择", "—", 0, 1);
        if (options.DeviceFeedback) return new("真实反馈 · 待授权", "0", "连接设备并授权本次上限", "待授权", "0", 0, 1);
        return new("模拟预览 · 无设备输出", $"{preview:0.#}%", $"曲线百分比 / 上限 {options.StrengthPreview.Maximum:0.#}%",
            "模拟", $"{preview:0.#}%", preview, options.StrengthPreview.Maximum);
    }
}

public partial class MainWindow
{
    private bool outputWasActive;
    private void RefreshOutputDisplay()
    {
        if (OutputIntensityLabel == null) return;
        var options = session.IsActive ? session.Options : SelectedPlan?.Options;
        var output = device.OutputState;
        var display = OutputDisplay.Create(options, session.PreviewStrength, output);
        OutputModeLabel.Text = display.Status;
        OutputIntensityLabel.Text = display.Value;
        OutputDetailLabel.Text = display.Detail;
        System.Windows.Automation.AutomationProperties.SetName(OutputStatusPanel, $"{display.Status}，{display.Detail}：{display.Value}");
        floating?.UpdateFeedback(display);
        RefreshTutorialStatus();
        var active = output.Authorized && output.Requested > 0;
        if (active && !outputWasActive) Pulse(OutputStatusPanel);
        outputWasActive = active;
    }
}
