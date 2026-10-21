using System.Windows;

namespace DglabFocus.Windows;
public partial class App : Application
{
    private Mutex? singleInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test");
        if (!smoke)
        {
            singleInstance = new Mutex(true, "Local\\DglabFocus.Singleton", out var created);
            if (!created) { MessageBox.Show("郊狼自习室已经运行，请从任务栏或托盘打开。", "郊狼自习室"); Shutdown(); return; }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            (MainWindow as MainWindow)?.SuspendForFault("界面发生异常，已暂停会话。");
            MessageBox.Show("会话已暂停。请重新打开软件。\n" + args.Exception.Message, "郊狼自习室");
            args.Handled = true;
        };
        MainWindow = new MainWindow(smoke, e.Args);
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { singleInstance?.Dispose(); base.OnExit(e); }
}
