using System.Globalization;
using System.Windows;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private readonly UnavailableActionFeedback unavailableActions;
    private string PlanEditReason => session.IsActive
        ? "本次会话进行中，方案和名单已锁定。请在会话结束后修改。"
        : "尚未选择方案。请到「专注方案」新建或选择一个方案。";

    private string UnavailableReason(IReadOnlyList<DependencyObject> chain)
    {
        bool Has(params DependencyObject[] controls) => controls.Any(chain.Contains);
        if (Has(NavigationPanel, MainContentPanel, SessionMenu) && TutorialOverlay.Visibility == Visibility.Visible)
            return "请先完成或跳过教程；也可点击教程里的设置按钮继续操作。";
        if (Has(PlanActions, FocusPlanPicker, PlanPicker, PresetEditor, PlanNameBox, SavePlanButton,
            PolicyEditor, AppRuleEditor, BrowserRuleEditor, SessionSettingsEditor, StrengthSettingsEditor, AddAppButton)
            && session.IsActive) return PlanEditReason;
        if (Has(StartButton)) return storage.LoadError != null ? "本机配置读取失败，请先恢复配置后再开始。" : SelectedPlan == null && !session.IsActive ? PlanEditReason
            : session.State == SessionState.ExitPending ? "正在等待退出，复习仍在继续。想留下来，请点击「取消退出」。"
            : "专注已经开始，无需重复启动。当前状态和强度可在控制台查看。";
        if (Has(ExitButton, ExitMenuItem, BreakButton)) return session.IsHardModeLocked
            ? "狠心模式会在约定时间结束，不能提前退出或休息。需要停止输出时，请点击「立即停电」。"
            : "当前没有可退出的专注会话。请先开始专注；已暂停时可继续会话。";
        if (Has(TemporaryButton, TemporaryInput)) return !session.IsActive ? "尚未开始专注。请先在方案中配置名单，开始后可临时放行 1 个域名。"
            : session.TemporaryHost != null ? "本次会话已用完 1 个临时域名名额。其他域名需在下次专注前加入方案。"
            : "当前是网站黑名单模式，未列出的域名已允许，无需临时放行。";
        if (SelectedPlan == null && Has(CopyPlanButton, DeletePlanButton, PresetEditor, SavePlanButton, PolicyEditor, AppRuleEditor,
            BrowserRuleEditor, SessionSettingsEditor, StrengthSettingsEditor, AddAppButton)) return PlanEditReason;
        if (Has(UndoDeleteButton)) return "当前没有可撤销的删除操作。";
        if (Has(RestMinutesBox, RepeatBox)) return "狠心模式只执行一次限时专注，不安排休息或循环。关闭狠心模式后可修改。";
        if (Has(ArmDeviceButton)) return DeviceAuthorizationReason();
        if (Has(DevicePicker, DeviceChannelBox, DeviceCapBox)) return armingDevice ? "正在授权设备，请等待完成后再更改主机、通道或上限。"
            : "设备已授权。请先点击「立即清零并撤销授权」，再更改主机、通道或上限。";
        if (Has(ConnectionModeBox, LanAddressBox, RefreshLanButton, DeviceConnectButton, DeviceDisconnectButton))
            return connectingDevice ? "正在建立连接，请等待完成后再操作。" : "连接期间无法修改网络。请先断开连接，再选择网卡或连接方式。";
        if (Has(ConfigureChromeButton, ConfigureEdgeButton)) return storage.LoadError != null
            ? "本机配置读取失败，暂时无法准备浏览器配对信息。请先恢复配置。" : "正在准备浏览器扩展，请等待完成。";
        if (Has(ConfirmAppSelectionButton)) return !CanSelectApplication ? PlanEditReason
            : appSelectionPlan != selectedPlanId ? "当前方案已变更，请重新选择要加入的软件。"
            : loadingRunningApps ? "正在读取运行中的软件，请稍候。" : "还没有选中软件。请点选列表中的一项，或激活要捕获的窗口。";
        if (Has(RefreshRunningAppsButton, RunningAppList)) return "正在读取运行中的软件，请等待列表加载完成。";
        if (Has(AppActionBox)) return "正在选择软件。请先完成或取消选择，再切换黑白名单。";
        if (Has(TutorialBackButton)) return "已经是教程第一步。可以点击「下一步」或「暂时跳过」。";
        return "此操作在当前状态下不可用。请先完成当前操作后再试。";
    }

    private string DeviceAuthorizationReason()
    {
        if (armingDevice) return "正在授权设备，请等待完成。";
        if (device.IsArmed) return "本次设备已经授权，无需重复授权。更改上限前请先清零并撤销授权。";
        var options = session.IsActive ? session.Options : SelectedPlan?.Options;
        if (options == null) return PlanEditReason;
        if (!options.DeviceFeedback) return "当前方案使用模拟模式。请在「强度曲线」勾选真实设备反馈并保存方案。";
        if (!device.IsConnected) return "尚未连接 DG-LAB App。请先生成二维码，在手机 App 的 Socket 入口扫码。";
        if (DevicePicker.SelectedItem is not DglabDevice selected) return "尚未选择主机。请在手机 App 连接设备并开放控制，再从上方选择主机。";
        var current = device.Devices.FirstOrDefault(x => x.Key == selected.Key);
        if (current == null) return "所选主机已离线。请在手机 App 重新连接设备后再选择。";
        if (!current.TryLimit(DeviceChannelBox.SelectedIndex, out _, out var reason)) return reason + " 请在手机 App 检查设备和通道状态。";
        if (!int.TryParse(DeviceCapBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap) || cap is < 1 or > 100)
            return "本次上限必须填写 1～100 的整数，并且不能超过 App 通道上限。";
        return "设备暂时无法授权。请检查 App 中的连接、通道和上限后重试。";
    }

    private void ExplainUnavailableAction(object sender, string reason)
        => unavailableActions.Show(sender is System.Windows.Controls.MenuItem ? SessionMenu : sender as FrameworkElement ?? (page == "DevicePage" ? DeviceConnectButton : StartButton), reason);
}
