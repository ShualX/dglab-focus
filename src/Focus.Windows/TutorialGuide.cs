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
        new("先建方案，再开始专注", "没有设备也能先体验模拟模式。",
            ["新建方案，设置名单、计时和强度。",
             "点每一步的设置按钮，可以边看边操作。",
             "准备好后，亲自点击「开始专注」。"],
            "可随时跳过；「帮助 → 新手教程」可以重开。",
            "", "FocusPage"),
        new("新建专注方案", "每个方案独立保存设置。",
            ["进入「专注方案」，点击「新建」。",
             "填写名称；已有方案也可直接选择。",
             "设置后点击「保存整个方案」。"],
            "新方案默认只模拟，不会开电。",
            "打开方案设置", "PlansPage", "RulesEditor", "PlanNameBox"),
        new("设置黑白名单", "软件和网站分开设置，黑名单优先。",
            ["白名单只放行列出的内容；黑名单放行未列出的内容。",
             "软件可选运行中窗口、捕获窗口或 .exe。",
             "网页可填域名、页面或路径，选好名单后添加并保存。"],
            "开始后名单锁定；网站白名单每次会话可临时新增 1 个域名。",
            "配置黑白名单", "PlansPage", "RulesEditor", "PolicyEditor"),
        new("安装网页检测", "Chrome 和 Edge 分别配置。",
            ["点击「一键配置 Chrome」或「一键配置 Edge」。",
             "在浏览器开启开发者模式，加载软件准备的扩展目录。",
             "回到软件，等待显示连接完成。"],
            "首次须在浏览器确认；只用普通软件可以跳过。",
            "打开浏览器配置", "DevicePage", Target: "ConfigureChromeButton"),
        new("设置计时", "默认连续分心满 10 秒就启动反馈。",
            ["选学习方式，填写专注和休息时长。",
             "调整「分心启动延迟」；累计分心用于提高强度。",
             "默认 5 秒无操作后开始分心计时，保存方案。"],
            "普通模式 0 分钟为无限；狠心模式限时，锁定普通暂停和退出。",
            "设置专注节奏", "PlansPage", "RhythmEditor", "SessionSettingsEditor"),
        new("设置强度", "曲线百分比决定强度，默认只模拟。",
            ["设置起始值、上限、递增量和间隔。",
             "需要开电时勾选「此方案使用真实设备反馈」。",
             "看下方曲线，确认后保存方案。"],
            "真实输出还需下一步手动授权；百分比不是电流值。",
            "打开强度曲线", "PlansPage", "StrengthEditor", "StrengthSettingsEditor"),
        new("连接并授权设备", "适用于 DG-LAB 4 App 和郊狼 3。",
            ["手机电脑连同一路由器，生成二维码，用 App 的 Socket 入口扫码。",
             "在 App 连接主机并开放控制，再在软件选择主机和通道。",
             "填写本次上限，点击「授权本次设备输出」。"],
            "没有主机可以跳过；急停、重连或重启后需要重新授权。",
            "打开设备连接", "DevicePage", Target: "DeviceConnectButton"),
        new("开始与停止", "选好方案，完成教程后点击「开始专注」。",
            ["顶部和悬浮球会显示当前强度。",
             "真实反馈的 3 / 20 表示请求强度 3、上限 20。",
             "点击「立即停电」或按 Ctrl+Alt+F10，可以急停。"],
            "普通退出等待 5 分钟；狠心模式到时结束。",
            "打开专注控制台", "FocusPage", Target: "FocusPlanPicker")
    ]);
}
