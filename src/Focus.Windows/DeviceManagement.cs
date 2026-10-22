using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private LocalDglabRelay? localRelay;
    private bool connectingDevice;
    private void ClearPairing()
    {
        pairingUrl = null; PairingQr.Source = null; PairingPanel.Visibility = Visibility.Collapsed;
        PairingAddressBox.Clear();
    }
    private async Task StopLocalRelay()
    { if (localRelay is { } relay) { localRelay = null; await relay.DisposeAsync(); } }
    private void RefreshLanAddresses()
    {
        var old = (LanAddressBox.SelectedItem as LanAddress)?.Address;
        try
        {
            var addresses = LanAddress.Find(); LanAddressBox.ItemsSource = addresses;
            LanAddressBox.SelectedItem = addresses.FirstOrDefault(x => x.Address.Equals(old)) ?? addresses.FirstOrDefault();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { LanAddressBox.ItemsSource = Array.Empty<LanAddress>(); }
    }
    private void RefreshConnectionControls()
    {
        if (ConnectionModeBox == null || LanAddressBox == null || DeviceDisconnectButton == null) return;
        var editable = !connectingDevice && !device.IsConnected;
        ConnectionModeBox.IsEnabled = LanAddressBox.IsEnabled = RefreshLanButton.IsEnabled = editable;
        DeviceConnectButton.IsEnabled = DeviceDisconnectButton.IsEnabled = !connectingDevice;
    }
    private void ConnectionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || LanAddressPanel == null || ConnectionHelpLabel == null) return;
        var local = ConnectionModeBox.SelectedIndex == 0;
        LanAddressPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        ConnectionHelpLabel.Text = local
            ? "手机与电脑需在同一路由器网络。选择实际联网的网卡；若 Windows 询问防火墙权限，请允许专用网络。VPN 需允许局域网访问。"
            : "手机与电脑均需能连接 trex.dungeon-lab.cn。官方演示中继不可用时，请断开并切换到局域网直连。";
    }
    private void RefreshLanClick(object sender, RoutedEventArgs e) => RefreshLanAddresses();
    private void CopyPairingAddressClick(object sender, RoutedEventArgs e)
    {
        if (device.PairingSocketUrl is not { } address || !device.IsConnected) { ClearPairing(); ExplainUnavailableAction(sender, "配对地址已失效，请重新生成二维码后再复制。"); return; }
        try { Clipboard.SetText(address); Notice("服务器地址已复制。在 DG-LAB App 的服务器地址输入框中粘贴即可。"); }
        catch (System.Runtime.InteropServices.COMException) { Notice("剪贴板正被占用，请稍后重试。"); }
    }
    private bool refreshingDevices, armingDevice;
    private void RefreshDeviceList()
    {
        if (DevicePicker == null) return;
        var key = (DevicePicker.SelectedItem as DglabDevice)?.Key;
        var list = device.Devices;
        var old = DevicePicker.Items.Cast<DglabDevice>().ToList();
        if (!old.Select(x => (x.Key, x.Display)).SequenceEqual(list.Select(x => (x.Key, x.Display))))
        {
            refreshingDevices = true;
            DevicePicker.ItemsSource = list;
            DevicePicker.SelectedItem = list.FirstOrDefault(x => x.Key == key);
            refreshingDevices = false;
        }
        RefreshDeviceControls();
    }
    private void RefreshDeviceControls()
    {
        RefreshConnectionControls();
        if (DevicePicker == null || ArmDeviceButton == null || DeviceCapBox == null || DeviceInfoLabel == null) return;
        var selected = DevicePicker.SelectedItem as DglabDevice;
        var current = device.Devices.FirstOrDefault(x => x.Key == selected?.Key);
        var ready = current?.TryLimit(DeviceChannelBox.SelectedIndex, out _, out _) == true;
        var options = session.IsActive ? session.Options : SelectedPlan?.Options;
        var validCap = int.TryParse(DeviceCapBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap) && cap is >= 1 and <= 100;
        ArmDeviceButton.IsEnabled = !armingDevice && !device.IsArmed && ready && validCap && options?.DeviceFeedback == true;
        DevicePicker.IsEnabled = DeviceChannelBox.IsEnabled = DeviceCapBox.IsEnabled = !device.IsArmed && !armingDevice;
        if (current != null)
        {
            current.TryLimit(DeviceChannelBox.SelectedIndex, out var limit, out var reason);
            DeviceInfoLabel.Text = ready ? $"{current.Name} · App 通道上限 {limit:0.#}。软件硬上限还受本次手动数值约束。" : reason;
        }
        else DeviceInfoLabel.Text = "选择一台主机。配对成功后，App 中已开放的设备会出现在这里。";
        RefreshOutputDisplay();
        if (device.IsArmed) DeviceStatusLabel.Text = session.IsActive ? "设备已授权，当前会话正在运行。" : "设备已授权，等待开始所选方案。";
        else if (DeviceStatusLabel.Text.StartsWith("设备已授权", StringComparison.Ordinal))
            DeviceStatusLabel.Text = "真实输出未授权，当前请求为零。";
        DeviceOutputLabel.Text = device.IsArmed ? $"本次已授权 · 当前请求强度 {device.RequestedIntensity}。立即停止可随时清零。"
            : options?.DeviceFeedback == true ? "此方案要求真实输出：连接设备、填写本次上限并授权后才能开始。"
            : "此方案使用模拟模式。如需真实输出，请先在方案的强度曲线中启用。";
    }
    private async void ArmDeviceClick(object sender, RoutedEventArgs e)
    {
        if (armingDevice || DevicePicker.SelectedItem is not DglabDevice) { ExplainUnavailableAction(sender, DeviceAuthorizationReason()); return; }
        var selected = (DglabDevice)DevicePicker.SelectedItem;
        var options = session.IsActive ? session.Options : SelectedPlan?.Options;
        if (options?.DeviceFeedback != true) { ExplainUnavailableAction(sender, "请先保存一个启用真实设备反馈的方案。"); return; }
        if (!int.TryParse(DeviceCapBox.Text, out var cap)) { ExplainUnavailableAction(sender, "请填写有效的设备上限整数。"); return; }
        armingDevice = true; RefreshDeviceControls();
        try
        {
            await device.ArmAsync(selected.Key, DeviceChannelBox.SelectedIndex, cap);
            Notice("本次设备已授权。开始或继续专注后，仅在满足分心条件时按方案输出。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException or TimeoutException or WebSocketException or IOException)
        { ExplainUnavailableAction(sender, "未授权：" + ex.Message); }
        finally { armingDevice = false; RefreshDeviceControls(); }
    }
    private async void StopDeviceClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive && session.Options.DeviceFeedback) session.Suspend(DateTimeOffset.Now, "已清零设备并撤销授权。");
        await device.StopAsync(); Refresh(); Notice("已停止续发并请求清零，设备授权已撤销。");
    }
    private void DeviceChoiceChanged(object sender, SelectionChangedEventArgs e)
    { if (!initializing && !refreshingDevices) RefreshDeviceControls(); }
    private void DeviceCapChanged(object sender, TextChangedEventArgs e)
    { if (!initializing) RefreshDeviceControls(); }
    private async void RefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        if (!device.IsConnected) { ExplainUnavailableAction(sender, "尚未连接 DG-LAB App，请先生成配对二维码并扫码连接。"); return; }
        await device.RefreshDevicesAsync();
    }

    private static string ExtensionDirectory()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 9 && folder != null; i++, folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "extension", "manifest.json"))) return Path.Combine(folder.FullName, "extension");
        throw new IOException("未找到扩展文件夹，请完整解压下载包。");
    }
    private void OpenExtensionFolderClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ExtensionDirectory()) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Notice(ex.Message); }
    }
    private void CopyExtensionPathClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ExtensionDirectory()); Notice("扩展路径已复制。在浏览器中点击「加载已解压的扩展」后粘贴该路径。"); }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException) { Notice(ex.Message); }
    }
    private void OpenBrowserSetupClick(object sender, RoutedEventArgs e)
    {
        var browser = (string)((Button)sender).Tag;
        try { Process.Start(new ProcessStartInfo(browser + ".exe", browser == "chrome" ? "chrome://extensions/" : "edge://extensions/") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Notice("未找到浏览器。请手动打开 chrome://extensions 或 edge://extensions。"); }
    }
}
