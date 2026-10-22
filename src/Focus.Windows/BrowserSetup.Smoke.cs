using System.Windows;
using System.Windows.Threading;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private async Task RunBrowserSetupSmoke(string folder)
    {
        browserSetupTestRoot = Path.Combine(folder, "browser-setup-test");
        ShowPage("DevicePage");
        ConfigureBrowserClick(ConfigureChromeButton, new RoutedEventArgs());
        var until = Environment.TickCount64 + 5000;
        while (preparingBrowser && Environment.TickCount64 < until) await Task.Delay(20);
        var actual = Path.Combine(Path.GetFullPath(browserSetupTestRoot), "BrowserExtension");
        if (preparingBrowser || PreparedExtensionPathBox.Text != actual || browserSetupTarget != "Chrome" || BrowserSetupPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("软件中的扩展配置入口未能准备安装目录。");
        if (!File.ReadAllText(Path.Combine(actual, "install-config.js")).Contains(settings.BridgeToken) || !BrowserSetupConnectionLabel.Text.Contains("等待"))
            throw new InvalidOperationException("配置没有写入自动配对信息，或未连接时误报成功。");
        // Public screenshots use a sample path. The real preparation above stays inside this test's artifacts.
        ShowPreparedBrowserSetup("Chrome", @"C:\Users\Study\AppData\Local\DglabFocus\BrowserExtension");
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); BrowserSetupPanel.BringIntoView(); }, DispatcherPriority.ApplicationIdle);
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "BrowserSetupChrome"); }, DispatcherPriority.ApplicationIdle);
        RenderBrowserSetupConnection(true);
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "BrowserSetupConnected"); }, DispatcherPriority.ApplicationIdle);
        if (!BrowserSetupConnectionLabel.Text.Contains("安装与配对完成")) throw new InvalidOperationException("扩展连接后没有完成提示。");
        ConfigureBrowserClick(ConfigureEdgeButton, new RoutedEventArgs());
        until = Environment.TickCount64 + 5000;
        while (preparingBrowser && Environment.TickCount64 < until) await Task.Delay(20);
        if (preparingBrowser || PreparedExtensionPathBox.Text != actual || browserSetupTarget != "Edge")
            throw new InvalidOperationException("Edge 配置没有复用固定目录。");
        ShowPreparedBrowserSetup("Edge", @"C:\Users\Study\AppData\Local\DglabFocus\BrowserExtension");
        Width = 920; Height = 680;
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); BrowserSetupPanel.BringIntoView(); }, DispatcherPriority.ApplicationIdle);
        await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "BrowserSetupCompact"); }, DispatcherPriority.ApplicationIdle);
        if (!BrowserSetupConnectionLabel.Text.Contains("等待 Edge")) throw new InvalidOperationException("Chrome 的安装状态被误用为 Edge 的状态。");
        Width = 1180; Height = 830;
        browserSetupTarget = null; BrowserSetupPanel.Visibility = Visibility.Collapsed;
        File.AppendAllText(Path.Combine(folder, "app-selection-result.txt"), "PASS: desktop extension setup prepares a private stable directory, embeds pairing only in the installation copy, waits for the chosen browser and renders compact instructions. No browser launched or user profile changed.\n");
    }
}
