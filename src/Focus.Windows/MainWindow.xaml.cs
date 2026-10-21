using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DglabFocus.Core;
using Microsoft.Win32;
using QRCoder;

namespace DglabFocus.Windows;
public partial class MainWindow : Window
{
    private readonly Storage storage;
    private readonly AppSettings settings;
    private readonly Session session;
    private readonly BrowserBridge bridge;
    private readonly DglabPreview device = new();
    private readonly ObservableCollection<AllowRule> rules;
    private readonly ObservableCollection<SessionEvent> history;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly bool smoke;
    private System.Windows.Forms.NotifyIcon? tray;
    private FloatingWindow? floating;
    private HwndSource? source;
    private TimeSpan lastTick;
    private int saveTick;
    private bool closing;
    private bool initializing = true;
    private string page = "FocusPage";
    private string? pairingUrl;
    private double zoom = 1;
    private bool Motion => !settings.ReducedMotion && SystemParameters.ClientAreaAnimation && !smoke;
    internal bool CanAnimate => Motion;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow(bool smokeTest, string[] args)
    {
        smoke = smokeTest;
        storage = new Storage(smoke);
        settings = storage.Load();
        session = settings.Session != null ? Session.Restore(settings.Session) : new Session();
        rules = new(settings.Rules);
        history = new(settings.History.Take(200));
        bridge = new(settings.BridgeToken);
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/focus.ico"));
        RuleList.ItemsSource = rules;
        HistoryList.ItemsSource = history;
        LoadOptions();
        SoundBox.IsChecked = settings.Sound;
        ReducedMotionBox.IsChecked = settings.ReducedMotion;
        initializing = false;
        UpdateStrengthPreview();
        ShowPage("FocusPage");
        session.Changed += SessionChanged;
        device.Updated += (message, url) => Dispatcher.InvokeAsync(() =>
        {
            DeviceStatusLabel.Text = message;
            if (url == "") { pairingUrl = null; PairingQr.Source = null; PairingQr.Visibility = Visibility.Collapsed; }
            else if (url != null)
            {
                pairingUrl = url;
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                using var png = new PngByteQRCode(data);
                using var stream = new MemoryStream(png.GetGraphic(6));
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                PairingQr.Source = image;
                PairingQr.Visibility = Visibility.Visible;
            }
        });
        timer.Tick += (_, _) => Tick();
        PreviewKeyDown += KeyDownHandler;
        SizeChanged += (_, _) => { Dial.Height = ActualHeight < 760 ? 232 : 294; };
        ApplyHighContrast();
        SystemParameters.StaticPropertyChanged += SystemPropertyChanged;
        if (smoke)
            Loaded += async (_, _) => await RunSmoke(args);
        else
        {
            Loaded += async (_, _) => await bridge.StartAsync();
            SystemEvents.SessionSwitch += SessionSwitch;
            SystemEvents.PowerModeChanged += PowerChanged;
            CreateTray();
            timer.Start();
            if (storage.LoadError != null) Notice(storage.LoadError);
        }
        Refresh();
    }
    private void LoadOptions()
    {
        var wasInitializing = initializing;
        initializing = true;
        var o = settings.Options;
        string N(double v) => v.ToString(CultureInfo.InvariantCulture);
        FocusMinutesBox.Text = N(o.FocusMinutes); RestMinutesBox.Text = N(o.RestMinutes);
        ModeBox.SelectedIndex = (int)o.Mode; RepeatBox.IsChecked = o.Repeat;
        GraceBox.Text = N(o.GraceSeconds); ThresholdBox.Text = N(o.AlertThresholdSeconds);
        IdleBox.Text = N(o.IdleSeconds); CheckInMinutesBox.Text = N(o.CheckInMinutes);
        RecoveryDelayBox.Text = N(o.RecoveryDelaySeconds); RecoveryRateBox.Text = N(o.RecoveryRate);
        AppPolicyBox.SelectedIndex = AppActionBox.SelectedIndex = (int)o.ApplicationMode;
        WebPolicyBox.SelectedIndex = WebActionBox.SelectedIndex = (int)o.WebsiteMode;
        StrengthEnabledBox.IsChecked = o.StrengthPreview.Enabled;
        StrengthInitialBox.Text = N(o.StrengthPreview.Initial); StrengthMaximumBox.Text = N(o.StrengthPreview.Maximum);
        StrengthIncrementBox.Text = N(o.StrengthPreview.Increment); StrengthIntervalBox.Text = N(o.StrengthPreview.IntervalSeconds);
        initializing = wasInitializing;
        UpdateStrengthPreview();
    }
    private static double Number(TextBox box)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new ArgumentException("请填写有效数字，小数点使用 .。");
        return value;
    }
    private StrengthPreviewOptions ReadStrength() => new()
    {
        Enabled = StrengthEnabledBox.IsChecked == true, Initial = Number(StrengthInitialBox), Maximum = Number(StrengthMaximumBox),
        Increment = Number(StrengthIncrementBox), IntervalSeconds = Number(StrengthIntervalBox)
    };
    private SessionOptions ReadOptions()
    {
        var o = new SessionOptions
        {
            FocusMinutes = Number(FocusMinutesBox), RestMinutes = Number(RestMinutesBox), Mode = (StudyMode)ModeBox.SelectedIndex,
            Repeat = RepeatBox.IsChecked == true, GraceSeconds = Number(GraceBox), AlertThresholdSeconds = Number(ThresholdBox),
            IdleSeconds = Number(IdleBox), CheckInMinutes = Number(CheckInMinutesBox), RecoveryDelaySeconds = Number(RecoveryDelayBox), RecoveryRate = Number(RecoveryRateBox),
            ApplicationMode = (RuleMode)AppPolicyBox.SelectedIndex, WebsiteMode = (RuleMode)WebPolicyBox.SelectedIndex,
            StrengthPreview = ReadStrength()
        };
        o.Validate();
        return o;
    }
    private void Tick()
    {
        try
        {
            var current = elapsed.Elapsed;
            var delta = current - lastTick;
            lastTick = current;
            var foreground = NativeMonitor.Read();
            var now = DateTimeOffset.Now;
            var report = foreground.Browser != null ? bridge.Get(foreground.Browser, now) : null;
            var policy = session.IsActive ? session.Options : settings.Options;
            var attention = foreground.IsOwn ? Attention.Neutral : Rules.Evaluate(session.IsActive ? session.Rules : rules.ToList(), foreground.Path, report?.Url, foreground.Browser != null, report != null, policy.ApplicationMode, policy.WebsiteMode);
            CurrentAppLabel.Text = foreground.Name;
            // Show host only. Full URLs, tokens and query strings never enter the history or display.
            CurrentUrlLabel.Text = report != null && Rules.TryHost(report.Url, out var host) ? host : foreground.Browser != null ? "等待已配对扩展报告当前页面" : $"键鼠空闲 {Math.Floor(foreground.IdleSeconds)} 秒";
            DetectionLabel.Text = attention switch { Attention.Allowed => "允许的学习内容", Attention.Outside => "当前内容未放行", Attention.Neutral => "正在操作专注面板", _ => "检测状态未知" };
            session.Tick(delta, now, attention, foreground.IdleSeconds);
            BridgeLabel.Text = $"{bridge.Status} · {bridge.ConnectionCount(now)} 个浏览器连接";
            Refresh();
            if (++saveTick >= 5) { saveTick = 0; Save(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        { session.Suspend(DateTimeOffset.Now, "检测或配置保存失败，已暂停。"); Notice(e.Message); Refresh(); }
    }
    private void SessionChanged(SessionEvent e)
    {
        history.Insert(0, e);
        while (history.Count > 200) history.RemoveAt(history.Count - 1);
        if (e.Kind == "feedback")
        {
            FeedbackLabel.Text = $"模拟反馈 · 提醒等级 {e.Level}";
            if (settings.Sound && !smoke) System.Media.SystemSounds.Exclamation.Play();
            Pulse(NoticePanel);
        }
        Notice(e.Message);
        Save();
    }
    private void Save()
    {
        settings.Rules = [..rules]; settings.History = [..history]; settings.Session = session.Snapshot();
        try { storage.Save(settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice("配置保存失败：" + e.Message); session.Suspend(DateTimeOffset.Now, "配置保存失败，已暂停。"); }
    }
    internal void Refresh()
    {
        var o = session.IsActive ? session.Options : settings.Options;
        var stateText = session.State switch
        {
            SessionState.Focusing => "专注进行中", SessionState.Resting => "休息中", SessionState.ExitPending => "退出等待中",
            SessionState.Suspended => "会话已暂停", SessionState.Completed => "本次已完成", _ => "准备好再开始"
        };
        var seconds = session.State == SessionState.Completed ? 0 : session.State == SessionState.ExitPending ? session.ExitRemaining : session.State == SessionState.Resting ? session.RestRemaining
            : o.FocusMinutes == 0 ? session.FocusSeconds : Math.Max(0, o.FocusMinutes * 60 - session.SegmentSeconds);
        if (!session.IsActive && session.State != SessionState.Completed) seconds = o.FocusMinutes * 60;
        var progress = session.State == SessionState.ExitPending ? 1 - seconds / Session.ExitDelaySeconds
            : session.State == SessionState.Resting ? 1 - seconds / Math.Max(1, o.RestMinutes * 60)
            : o.FocusMinutes == 0 ? (session.FocusSeconds % 3600) / 3600 : session.SegmentSeconds / (o.FocusMinutes * 60);
        Dial.Motion = Motion;
        Dial.Update(progress, Clock(seconds), stateText);
        System.Windows.Automation.AutomationProperties.SetName(Dial, stateText + " " + Clock(seconds));
        ModeLabel.Text = o.Mode switch { StudyMode.Reading => "阅读 / 网课", StudyMode.Paper => "纸笔复习", _ => "电脑复习" };
        DebtLabel.Text = $"{Math.Floor(session.DebtSeconds)} 秒";
        DebtBar.Maximum = o.AlertThresholdSeconds; DebtBar.Value = Math.Min(o.AlertThresholdSeconds, session.DebtSeconds);
        ThresholdLabel.Text = $"提醒线 {o.AlertThresholdSeconds:0} 秒 / 持续专注后逐渐消除";
        IncidentLabel.Text = $"{session.RecentCount(DateTimeOffset.Now)} 次分心";
        ReasonLabel.Text = session.StatusReason;
        StrengthValueLabel.Text = $"{session.PreviewStrength:0.#} / {o.StrengthPreview.Maximum:0.#}";
        StrengthBar.Maximum = Math.Max(1, o.StrengthPreview.Maximum); StrengthBar.Value = session.PreviewStrength;
        StartButton.Content = session.State switch { SessionState.Suspended => "继续本次会话", SessionState.Resting => "结束休息", SessionState.Focusing => "专注进行中", SessionState.ExitPending => "继续复习，等待退出", _ => "开始专注" };
        StartButton.IsEnabled = storage.LoadError == null && session.State is not (SessionState.Focusing or SessionState.ExitPending);
        ExitButton.IsEnabled = session.State is SessionState.Focusing or SessionState.Resting;
        ExitButton.Visibility = session.State == SessionState.ExitPending ? Visibility.Collapsed : Visibility.Visible;
        CancelExitButton.Visibility = session.State == SessionState.ExitPending ? Visibility.Visible : Visibility.Collapsed;
        CheckInPanel.Visibility = session.NeedsCheckIn ? Visibility.Visible : Visibility.Collapsed;
        TemporaryButton.IsEnabled = session.IsActive && session.TemporaryHost == null && o.WebsiteMode == RuleMode.Whitelist;
        TemporaryInput.IsEnabled = TemporaryButton.IsEnabled;
        TemporaryLabel.Text = session.TemporaryHost != null ? $"本次临时允许：{session.TemporaryHost} · 剩余 0 个名额"
            : o.WebsiteMode == RuleMode.Blacklist ? "网站黑名单模式：未命中的域名已允许，无需临时放行。" : "每次会话可临时新增 1 个域名，仅本次有效，黑名单优先。";
        AddAppButton.IsEnabled = BrowserRuleEditor.IsEnabled = SubdomainsBox.IsEnabled = SessionSettingsEditor.IsEnabled = !session.IsActive;
        PolicyEditor.IsEnabled = AppRuleEditor.IsEnabled = StrengthSettingsEditor.IsEnabled = !session.IsActive;
        var summary = $"白名单 {rules.Count(x => x.Action == RuleAction.Allow)} 条 · 黑名单 {rules.Count(x => x.Action == RuleAction.Block)} 条。";
        RuleLockLabel.Text = summary + (session.IsActive ? "本次会话规则与模式已锁定。" : "黑名单始终优先，白名单模式下未列出内容算分心。");
        floating?.Update(stateText, Clock(seconds), session.State, session.NeedsCheckIn, session.DebtSeconds, o.AlertThresholdSeconds);
    }
    internal static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling(seconds)));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";
    }
    private void Notice(string message) { NoticeLabel.Text = message; floating?.SetMessage(message); }
    private void Pulse(UIElement element)
    {
        if (!Motion) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 1, TimeSpan.FromMilliseconds(240)));
    }
    private void ShowPage(string name)
    {
        page = name;
        PageScroll.ScrollToTop();
        foreach (FrameworkElement child in PageHost.Children) child.Visibility = child.Name == name ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = name switch
        {
            "RulesPage" => ("黑白名单", "软件与网站分别配置，冲突时黑名单优先。"),
            "SettingsPage" => ("会话设置", "选择适合这一段学习的节奏。"),
            "DevicePage" => ("设备与浏览器", "所有专注判断在本机完成。"),
            "HistoryPage" => ("会话记录", "查看规则变化与提醒发生的时间。"),
            _ => ("专注控制台", "把注意力留在当前这一段。")
        };
        foreach (var button in LogicalTreeHelpers.Children<Button>(RootGrid).Where(x => x.Style == FindResource("Nav")))
        {
            var selected = (string?)button.Tag == name;
            button.Background = selected ? (Brush)FindResource("Raised") : Brushes.Transparent;
            button.Foreground = (Brush)FindResource(selected ? "Accent" : "Text");
        }
        if (Motion)
        {
            var selected = (FrameworkElement)FindName(name);
            selected.RenderTransform = new TranslateTransform();
            selected.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            ((TranslateTransform)selected.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }
    private void NavigateClick(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    private void StartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (session.State == SessionState.Suspended) session.Resume(DateTimeOffset.Now);
            else if (session.State == SessionState.Resting) session.EndBreak(DateTimeOffset.Now);
            else if (!session.IsActive)
            {
                settings.Options = ReadOptions();
                session.Start(settings.Options, rules, DateTimeOffset.Now);
                FeedbackLabel.Text = "模拟反馈待命 · 无设备输出";
                FloatClick(sender, e);
            }
            lastTick = elapsed.Elapsed;
            ShowPage("FocusPage"); Save(); Refresh();
        }
        catch (ArgumentException ex) { Notice(ex.Message); ShowPage(!rules.Any(x => x.Action == RuleAction.Allow) ? "RulesPage" : "SettingsPage"); }
    }
    private void PresetClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive) { Notice("会话进行中，专注时长保持不变。"); return; }
        FocusMinutesBox.Text = (string)((Button)sender).Tag;
        SaveSettingsClick(sender, e);
    }
    private void SaveSettingsClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive) { Notice("请在会话结束后修改设置。"); return; }
        try { settings.Options = ReadOptions(); Save(); Refresh(); Notice("会话设置已保存。"); }
        catch (ArgumentException ex) { Notice(ex.Message); }
    }
    private void PreferencesClick(object sender, RoutedEventArgs e)
    {
        settings.Sound = SoundBox.IsChecked == true; settings.ReducedMotion = ReducedMotionBox.IsChecked == true;
        Save(); Refresh();
    }
    private void PolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || session.IsActive || AppPolicyBox.SelectedIndex < 0 || WebPolicyBox.SelectedIndex < 0) return;
        settings.Options = settings.Options with { ApplicationMode = (RuleMode)AppPolicyBox.SelectedIndex, WebsiteMode = (RuleMode)WebPolicyBox.SelectedIndex };
        if (sender == AppPolicyBox) AppActionBox.SelectedIndex = AppPolicyBox.SelectedIndex;
        if (sender == WebPolicyBox) WebActionBox.SelectedIndex = WebPolicyBox.SelectedIndex;
        Save(); Refresh(); Notice("黑白名单模式已保存，下一次会话生效。");
    }
    private void StrengthTextChanged(object sender, TextChangedEventArgs e) => UpdateStrengthPreview();
    private void StrengthInputChanged(object sender, RoutedEventArgs e) => UpdateStrengthPreview();
    private void UpdateStrengthPreview()
    {
        if (initializing || RampChart == null) return;
        try
        {
            var preview = ReadStrength(); preview.Validate();
            StrengthValidationLabel.Foreground = (Brush)FindResource("Muted");
            StrengthValidationLabel.Text = preview.Enabled ? "达到提醒线后的前五次递增预览；只在确定分心且超过宽限期时显示模拟强度。" : "曲线预览已关闭。启用后保存设置，在下一次会话生效。";
            RampChart.Update(preview);
        }
        catch (ArgumentException ex)
        {
            StrengthValidationLabel.Foreground = (Brush)FindResource("Danger");
            StrengthValidationLabel.Text = ex.Message; RampChart.Update(new());
        }
    }
    private void ExitClick(object sender, RoutedEventArgs e) { session.RequestExit(DateTimeOffset.Now); Save(); ShowPage("FocusPage"); Refresh(); }
    private void CancelExitClick(object sender, RoutedEventArgs e) { session.CancelExit(DateTimeOffset.Now); Save(); Refresh(); }
    private void CheckInClick(object sender, RoutedEventArgs e) { session.CheckIn(DateTimeOffset.Now); Save(); Refresh(); }
    private void BreakClick(object sender, RoutedEventArgs e) { session.TakeBreak(DateTimeOffset.Now); Save(); ShowPage("FocusPage"); Refresh(); }
    private void EmergencyClick(object sender, RoutedEventArgs e) => SuspendForFault("已立即暂停会话与模拟反馈。继续时保留本次会话规则。");
    public void SuspendForFault(string reason)
    {
        session.Suspend(DateTimeOffset.Now, reason); device.Dispose(); PairingQr.Visibility = Visibility.Collapsed;
        DeviceStatusLabel.Text = "连接已关闭"; Save(); Refresh(); Notice(reason);
    }
    private void TemporaryClick(object sender, RoutedEventArgs e)
    {
        if (session.AddTemporaryDomain(TemporaryInput.Text, DateTimeOffset.Now, out var message)) TemporaryInput.Clear();
        Notice(message); Save(); Refresh();
    }
    private void AddAppClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive) return;
        var picker = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", Title = AppActionBox.SelectedIndex == 1 ? "选择加入黑名单的软件" : "选择加入白名单的软件" };
        if (picker.ShowDialog(this) == true && Rules.TryCreate(RuleKind.Application, picker.FileName, false, out var rule))
            AddRule(rule! with { Action = AppActionBox.SelectedIndex == 1 ? RuleAction.Block : RuleAction.Allow });
    }
    private void AddWebClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive) return;
        var kind = RuleKindBox.SelectedIndex switch { 1 => RuleKind.Page, 2 => RuleKind.PathPrefix, _ => RuleKind.Domain };
        if (Rules.TryCreate(kind, RuleInput.Text, SubdomainsBox.IsChecked == true, out var rule)) { AddRule(rule! with { Action = WebActionBox.SelectedIndex == 1 ? RuleAction.Block : RuleAction.Allow }); RuleInput.Clear(); }
        else Notice("域名请输入 example.com，页面或路径规则请输入以 https:// 开头的完整地址。");
    }
    private void AddRule(AllowRule rule)
    {
        if (rules.Contains(rule)) { Notice("这条规则已经存在。"); return; }
        rules.Add(rule); Save(); Refresh(); Notice((rule.Action == RuleAction.Block ? "黑名单" : "白名单") + "规则已添加，黑名单始终优先。");
    }
    private void RemoveRuleClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive) { Notice("本次会话的允许规则已锁定。"); return; }
        rules.Remove((AllowRule)((Button)sender).Tag); Save(); Refresh();
    }
    private void FloatClick(object sender, RoutedEventArgs e)
    {
        if (floating == null)
        {
            floating = new FloatingWindow();
            floating.OpenRequested += ShowMain;
            floating.ExitRequested += () => ExitClick(this, new RoutedEventArgs());
            floating.CancelRequested += () => CancelExitClick(this, new RoutedEventArgs());
            floating.StopRequested += () => EmergencyClick(this, new RoutedEventArgs());
            floating.CheckInRequested += () => CheckInClick(this, new RoutedEventArgs());
            floating.Closed += (_, _) => floating = null;
        }
        floating.Show(); Refresh();
    }
    private void ShowMain() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void CreateTray()
    {
        tray = new System.Windows.Forms.NotifyIcon { Text = "郊狼自习室 · 模拟预览版", Icon = System.Drawing.SystemIcons.Application, Visible = true };
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/focus.ico")).Stream)
            tray.Icon = new System.Drawing.Icon(stream);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开控制台", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add("显示悬浮球", null, (_, _) => Dispatcher.Invoke(() => FloatClick(this, new RoutedEventArgs())));
        menu.Items.Add("申请退出专注", null, (_, _) => Dispatcher.Invoke(() => ExitClick(this, new RoutedEventArgs())));
        menu.Items.Add("立即暂停", null, (_, _) => Dispatcher.Invoke(() => EmergencyClick(this, new RoutedEventArgs())));
        menu.Items.Add("暂停并关闭软件", null, (_, _) => Dispatcher.Invoke(() => { SuspendForFault("软件已关闭，继续时需确认上次会话。"); Quit(); }));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
    }
    private void WindowInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        source = HwndSource.FromHwnd(hwnd); source?.AddHook(WindowMessage);
        if (!smoke && !NativeMonitor.RegisterHotKey(hwnd, 1, 0x4003, 0x79)) HotkeyLabel.Text = "热键被占用，请使用按钮";
        else HotkeyLabel.Text = "Ctrl + Alt + F10";
    }
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == 1) { EmergencyClick(this, new RoutedEventArgs()); handled = true; }
        return IntPtr.Zero;
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
            Dispatcher.InvokeAsync(() => SuspendForFault("系统锁定或会话断开，已暂停。解锁后确认继续。"));
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.InvokeAsync(() => SuspendForFault("电脑进入睡眠，已暂停。唤醒后确认继续。"));
    }
    private void SystemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.InvokeAsync(ApplyHighContrast);
    private void ApplyHighContrast()
    {
        var resources = System.Windows.Application.Current.Resources;
        if (!SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "Bg", "Panel", "Raised", "Text", "Muted", "Line", "Accent", "AccentText", "Danger", "Good" }) resources.Remove(key);
            return;
        }
        resources["Bg"] = resources["Panel"] = SystemColors.WindowBrush;
        resources["Raised"] = SystemColors.ControlBrush;
        resources["Text"] = resources["Muted"] = resources["Line"] = SystemColors.WindowTextBrush;
        resources["Accent"] = SystemColors.HighlightBrush; resources["AccentText"] = SystemColors.HighlightTextBrush;
        resources["Danger"] = resources["Good"] = SystemColors.WindowTextBrush;
    }
    private void KeyDownHandler(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key is Key.OemPlus or Key.Add) { ZoomInClick(sender, e); e.Handled = true; }
        if (e.Key is Key.OemMinus or Key.Subtract) { ZoomOutClick(sender, e); e.Handled = true; }
    }
    private void ZoomInClick(object sender, RoutedEventArgs e) => SetZoom(Math.Min(1.4, zoom + .1));
    private void ZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(Math.Max(1, zoom - .1));
    private void SetZoom(double value)
    {
        zoom = value; RootGrid.LayoutTransform = new ScaleTransform(zoom, zoom);
        MinWidth = 920 * zoom; MinHeight = 680;
        Width = Math.Max(Width, MinWidth);
    }
    private static void OpenLink(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void GithubClick(object sender, RoutedEventArgs e) => OpenLink("https://github.com/ShualX/dglab-focus");
    private void HelpClick(object sender, RoutedEventArgs e) => OpenLink("https://github.com/ShualX/dglab-focus#使用");
    private void CopyTokenClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(settings.BridgeToken); Notice("已复制本机配对码。请只粘贴到随附浏览器扩展。"); }
        catch (COMException) { Notice("剪贴板正被占用，请稍后重试。"); }
    }
    private async void DeviceConnectClick(object sender, RoutedEventArgs e)
    {
        PairingQr.Source = null; PairingQr.Visibility = Visibility.Collapsed;
        DeviceConnectButton.IsEnabled = false; DeviceStatusLabel.Text = "正在连接官方中继…";
        try { await device.Connect(); } finally { DeviceConnectButton.IsEnabled = true; }
    }
    private void DeviceDisconnectClick(object sender, RoutedEventArgs e)
    { device.Dispose(); pairingUrl = null; PairingQr.Source = null; PairingQr.Visibility = Visibility.Collapsed; DeviceStatusLabel.Text = "已断开连接"; }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (session.IsActive && session.State != SessionState.Suspended)
        {
            e.Cancel = true;
            if (session.State != SessionState.ExitPending) ExitClick(this, new RoutedEventArgs());
            Notice("专注会话将在倒计时结束后退出。悬浮球可取消退出，立即暂停始终可用。");
            return;
        }
        e.Cancel = true; Quit();
    }
    private void Quit()
    {
        closing = true; timer.Stop(); Save();
        bridge.Dispose(); device.Dispose(); floating?.Close(); tray?.Dispose();
        NativeMonitor.UnregisterHotKey(new WindowInteropHelper(this).Handle, 1);
        SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged;
        SystemParameters.StaticPropertyChanged -= SystemPropertyChanged;
        source?.RemoveHook(WindowMessage);
        System.Windows.Application.Current.Shutdown(Environment.ExitCode);
    }
    private async Task RunSmoke(string[] args)
    {
        var index = Array.IndexOf(args, "--capture-dir");
        var folder = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(Environment.CurrentDirectory, "artifacts", "ui");
        Directory.CreateDirectory(folder);
        try
        {
            rules.Add(new(RuleKind.Domain, "learn.example.com"));
            rules.Add(new(RuleKind.Page, "https://docs.example.com/course/lesson-1"));
            rules.Add(new(RuleKind.Application, @"C:\Program Files\StudyReader\StudyReader.exe"));
            rules.Add(new(RuleKind.Domain, "games.example.com", false, RuleAction.Block));
            rules.Add(new(RuleKind.Application, @"C:\Program Files\ExampleGame\Game.exe", false, RuleAction.Block));
            settings.Options = settings.Options with { Mode = StudyMode.Reading,
                StrengthPreview = new() { Enabled = true, Initial = 10, Maximum = 40, Increment = 5, IntervalSeconds = 30 } };
            LoadOptions();
            ShowPage("SettingsPage");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "StrengthSetup"); }, DispatcherPriority.ApplicationIdle);
            StrengthMaximumBox.Text = "5";
            SaveSettingsClick(this, new RoutedEventArgs());
            if (settings.Options.StrengthPreview.Maximum != 40 || !StrengthValidationLabel.Text.Contains("上限"))
                throw new InvalidOperationException("无效的模拟强度设置被保存。");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "InvalidStrength"); }, DispatcherPriority.ApplicationIdle);
            StrengthMaximumBox.Text = "40";
            StartClick(this, new RoutedEventArgs());
            for (int i = 0; i < 1120; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, 0);
            session.CheckIn(DateTimeOffset.Now);
            session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, 0);
            session.AddTemporaryDomain("reference.example.com", DateTimeOffset.Now, out _);
            CurrentAppLabel.Text = "Microsoft Edge"; CurrentUrlLabel.Text = "learn.example.com";
            DetectionLabel.Text = "允许的学习内容";
            Refresh();
            foreach (var name in new[] { "FocusPage", "RulesPage", "SettingsPage", "DevicePage", "HistoryPage" })
            {
                ShowPage(name); await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, name); }, DispatcherPriority.ApplicationIdle);
                if (name == "RulesPage")
                {
                    PageScroll.ScrollToBottom();
                    await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "RuleEntries"); }, DispatcherPriority.ApplicationIdle);
                }
            }
            ShowPage("FocusPage");
            for (int i = 0; i < 110; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Outside, 0);
            CurrentAppLabel.Text = "Example Game"; CurrentUrlLabel.Text = "示例软件黑名单"; DetectionLabel.Text = "当前内容不符合黑白名单";
            Refresh();
            if (session.PreviewStrength != 15) throw new InvalidOperationException("模拟强度验证失败。");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "StrengthActive"); }, DispatcherPriority.ApplicationIdle);
            session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Unknown, 0);
            if (session.PreviewStrength != 0) throw new InvalidOperationException("未知状态未清除模拟强度。");
            CurrentAppLabel.Text = "检测状态未知（示例）"; CurrentUrlLabel.Text = ""; DetectionLabel.Text = "检测状态未知";
            ExitClick(this, new RoutedEventArgs());
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "ExitPending"); }, DispatcherPriority.ApplicationIdle);
            floating?.Capture(Path.Combine(folder, "Floating.png"));
            floating?.Expand();
            floating?.Capture(Path.Combine(folder, "FloatingExpanded.png"));
            Width = 920; Height = 680;
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "Compact"); }, DispatcherPriority.ApplicationIdle);
            File.WriteAllText(Path.Combine(folder, "smoke-result.txt"), $"PASS: 5 pages, blacklist entries, strength setup, validation, active simulation, exit, floating and compact layout rendered.\nInvalid strength settings rejected; unknown state cleared preview.\nNo device output or user settings used.\nRuntime: {RuntimeInformation.FrameworkDescription}\n");
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(folder, "smoke-result.txt"), "FAIL: " + e); Environment.ExitCode = 1; }
        Quit();
    }
    private void Capture(string folder, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(RootGrid.ActualWidth), (int)Math.Ceiling(RootGrid.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(RootGrid), null, new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight));
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
}
internal static class LogicalTreeHelpers
{
    public static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { if (child is T t) yield return t; foreach (var nested in Children<T>(child)) yield return nested; }
    }
}
