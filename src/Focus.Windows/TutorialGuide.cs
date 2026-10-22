using System.Text.Json.Serialization;

namespace DglabFocus.Windows;

public sealed class TutorialProgress
{
    public int Step { get; set; }
    public bool Completed { get; set; }
    public bool Dismissed { get; set; }
    [JsonIgnore] public bool ShowAutomatically => !Completed && !Dismissed;
    public void Normalize() => Step = Math.Clamp(Step, 0, TutorialGuide.Steps.Count - 1);
    public void Restart() { Step = 0; Completed = Dismissed = false; }
    public void Finish() { Step = TutorialGuide.Steps.Count - 1; Completed = true; Dismissed = false; }
}

public sealed record TutorialStep(string Title, string Summary, string[] Instructions, string Tip,
    string Action, string Page, string? Tab = null, string? Target = null);

public static class TutorialGuide
{
    public static IReadOnlyList<TutorialStep> Steps { get; } = Array.AsReadOnly<TutorialStep>([
        new("配置方案", "新建或选择一个方案。",
            ["添加学习软件／网站，点「保存整个方案」。",
             "时长和强度可先用默认值。"],
            "要检测网页，先在「设备与浏览器」配置浏览器扩展。",
            "配置方案", "PlansPage", "RulesEditor", "PlanPicker"),
        new("连接设备（可跳过）", "默认只模拟；需要开电时再设置。",
            ["在方案「强度曲线」启用真实反馈；用 DG-LAB 4 App 扫码连接。",
             "选择主机、通道和本次上限，点击「授权本次设备输出」。"],
            "手机和电脑连同一路由器，主机需在 App 中开放控制。",
            "打开设备连接", "DevicePage", Target: "DeviceConnectButton"),
        new("开始专注", "在控制台选好方案，点击「开始专注」。",
            ["当前强度会显示在顶部和悬浮球。",
             "随时点击「立即停电」或按 Ctrl+Alt+F10 急停。"],
            "按钮不能用时，点击可查看原因。教程可从「帮助」重开。",
            "打开专注控制台", "FocusPage", Target: "FocusPlanPicker")
    ]);
}
