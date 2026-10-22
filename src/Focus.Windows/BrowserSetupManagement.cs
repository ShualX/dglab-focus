using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private string? browserSetupTarget;
    private string? browserSetupTestRoot;
    private bool preparingBrowser;

    private async void ConfigureBrowserClick(object sender, RoutedEventArgs e)
    {
        if (preparingBrowser || storage.LoadError != null) { ExplainUnavailableAction(sender, UnavailableReason([ConfigureChromeButton])); return; }
        preparingBrowser = true; RefreshBrowserSetup();
        var browser = (string)((Button)sender).Tag;
        try
        {
            Save();
            var source = ExtensionDirectory();
            var setup = new BrowserExtensionSetup(smoke ? browserSetupTestRoot ?? throw new InvalidOperationException("测试目录尚未设置。") : storage.DirectoryPath);
            var directory = await Task.Run(() => setup.Prepare(source, settings.BridgeToken));
            ShowPreparedBrowserSetup(browser, directory);
            Notice($"已为 {browser} 准备扩展，请在浏览器确认加载；加载后会自动配对。");
            if (!smoke)
            {
                try { Clipboard.SetText(directory); BrowserSetupPreparedLabel.Text = "1 · 扩展已准备，安装目录已复制。"; }
                catch (COMException) { BrowserSetupPreparedLabel.Text = "1 · 扩展已准备。剪贴板被占用，可用下方按钮重试复制。"; }
                try { Process.Start(BrowserExtensionSetup.BrowserStartInfo(browser)); }
                catch (System.ComponentModel.Win32Exception)
                { BrowserSetupLaunchLabel.Text = $"未能自动打开 {browser}。请在浏览器地址栏输入 {(browser == "Chrome" ? "chrome" : "edge")}://extensions，然后继续下列步骤。"; }
            }
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); BrowserSetupPanel.BringIntoView(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { ExplainUnavailableAction(sender, "扩展准备失败：" + ex.Message); }
        finally { preparingBrowser = false; RefreshBrowserSetup(); }
    }
    private void ShowPreparedBrowserSetup(string browser, string directory)
    {
        browserSetupTarget = browser;
        ConfigureChromeButton.SetResourceReference(StyleProperty, browser == "Chrome" ? "Primary" : typeof(Button));
        ConfigureEdgeButton.SetResourceReference(StyleProperty, browser == "Edge" ? "Primary" : typeof(Button));
        PreparedExtensionPathBox.Text = directory;
        BrowserSetupPreparedLabel.Text = "1 · 扩展已准备，加载后会自动配对。";
        BrowserSetupLaunchLabel.Text = $"扩展尚未上架商店，请在 {browser} 中确认加载。";
        BrowserSetupLoadLabel.Text = "2 · 开启「开发者模式」，点击「加载已解压的扩展」，粘贴下方目录并选择文件夹。";
        BrowserSetupPanel.Visibility = Visibility.Visible;
        RefreshBrowserSetup();
    }
    private void RenderBrowserSetupConnection(bool connected)
    {
        BrowserSetupConnectionLabel.Text = connected ? $"3 · {browserSetupTarget} 扩展已连接，安装与配对完成。"
            : $"3 · 等待 {browserSetupTarget} 连接，加载后自动配对。更新旧扩展时请点击浏览器中的重新加载按钮。";
    }
    private void RefreshBrowserSetup()
    {
        if (ConfigureChromeButton == null || ConfigureEdgeButton == null || BrowserSetupPanel == null) return;
        ConfigureChromeButton.IsEnabled = ConfigureEdgeButton.IsEnabled = !preparingBrowser && storage.LoadError == null;
        if (browserSetupTarget != null) RenderBrowserSetupConnection(bridge.ConnectionCount(browserSetupTarget, DateTimeOffset.Now) > 0);
    }
    private void CopyPreparedExtensionPathClick(object sender, RoutedEventArgs e)
    {
        if (PreparedExtensionPathBox.Text.Length == 0) { ExplainUnavailableAction(sender, "尚未准备浏览器扩展。请先点击「一键配置 Chrome」或「一键配置 Edge」。"); return; }
        try { Clipboard.SetText(PreparedExtensionPathBox.Text); Notice("安装目录已复制，在浏览器选择扩展文件夹时粘贴即可。"); }
        catch (COMException) { Notice("剪贴板被占用，请稍后重试或直接复制上方路径。"); }
    }
}
