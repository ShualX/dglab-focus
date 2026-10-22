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
    private readonly DglabController device;
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
        var relayIndex = Array.IndexOf(args, "--device-test-relay");
        Uri? testRelay = null;
        if (smoke && relayIndex >= 0 && relayIndex + 1 < args.Length)
        {
            testRelay = new Uri(args[relayIndex + 1]);
            if (testRelay.Scheme != "ws" || testRelay.Host != "127.0.0.1") throw new ArgumentException("设备界面测试只允许本机模拟中继。");
        }
        device = new DglabController(testRelay);
        storage = new Storage(smoke);
        settings = storage.Load();
        focusMottoIndex = FocusMottos.Pick(settings.LastMottoIndex);
        settings.LastMottoIndex = focusMottoIndex;
        session = settings.Session != null ? Session.Restore(settings.Session) : new Session();
        plans = new(settings.Plans ?? []);
        selectedPlanId = session.IsActive ? session.PlanId : settings.SelectedPlanId;
        var selected = SelectedPlan;
        settings.Options = selected?.Options ?? new();
        rules = new(selected?.Rules ?? []);
        history = new(settings.History.Take(200));
        bridge = new(settings.BridgeToken);
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/focus.ico"));
        RuleList.ItemsSource = rules;
        FocusPlanPicker.ItemsSource = PlanPicker.ItemsSource = plans;
        SyncPlanPickers();
        PlanNameBox.Text = selected?.Name ?? "";
        HistoryList.ItemsSource = history;
        LoadOptions();
        RefreshLanAddresses();
        SoundBox.IsChecked = settings.Sound;
        ReducedMotionBox.IsChecked = settings.ReducedMotion;
        initializing = false;
        UpdateStrengthPreview();
        ShowPage("FocusPage");
        ShowPlanTab("RulesEditor");
        session.Changed += SessionChanged;
        device.Updated += (message, url) => Dispatcher.InvokeAsync(() =>
        {
            DeviceStatusLabel.Text = message;
            if (url == "") ClearPairing();
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
                PairingAddressBox.Text = device.PairingSocketUrl ?? "";
                PairingPanel.Visibility = Visibility.Visible;
            }
            RefreshDeviceControls();
        });
        device.OutputChanged += () => Dispatcher.InvokeAsync(RefreshOutputDisplay);
        device.DevicesChanged += () => Dispatcher.InvokeAsync(RefreshDeviceList);
        device.Faulted += reason => Dispatcher.InvokeAsync(() =>
        {
            if (session.IsActive && session.Options.DeviceFeedback) SuspendForFault(reason);
            else Notice(reason);
            Refresh();
        });
        timer.Tick += (_, _) => Tick();
        PreviewKeyDown += KeyDownHandler;
        PreviewMouseLeftButtonDown += MottoPointerClick;
        SizeChanged += (_, _) => { Dial.Height = ActualHeight < 760 ? 232 : 294; RunningAppList.MaxHeight = ActualHeight < 760 ? 160 : 228; };
        ApplyHighContrast();
        SystemParameters.StaticPropertyChanged += SystemPropertyChanged;
        if (smoke)
            Loaded += async (_, _) => await RunSmoke(args);
        else
        {
            Loaded += async (_, _) => { TryShowFirstRunTutorial(); Save(); await bridge.StartAsync(); };
            SystemEvents.SessionSwitch += SessionSwitch;
            SystemEvents.PowerModeChanged += PowerChanged;
            CreateTray();
            timer.Start();
            if (storage.LoadError != null) Notice(storage.LoadError);
            else if (storage.RecoveryWarning != null) Notice(storage.RecoveryWarning);
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
        ModeBox.SelectedIndex = (int)o.Mode; RepeatBox.IsChecked = o.Repeat; HardModeBox.IsChecked = o.HardMode;
        RefreshHardModeEditor();
        GraceBox.Text = N(o.GraceSeconds);
        IdleBox.Text = N(o.IdleSeconds); CheckInMinutesBox.Text = N(o.CheckInMinutes);
        RecoveryDelayBox.Text = N(o.RecoveryDelaySeconds); RecoveryRateBox.Text = N(o.RecoveryRate);
        AppPolicyBox.SelectedIndex = AppActionBox.SelectedIndex = (int)o.ApplicationMode;
        WebPolicyBox.SelectedIndex = WebActionBox.SelectedIndex = (int)o.WebsiteMode;
        StrengthEnabledBox.IsChecked = o.StrengthPreview.Enabled;
        StrengthInitialBox.Text = N(o.StrengthPreview.Initial); StrengthMaximumBox.Text = N(o.StrengthPreview.Maximum);
        StrengthIncrementBox.Text = N(o.StrengthPreview.Increment); StrengthIntervalBox.Text = N(o.StrengthPreview.IntervalSeconds);
        IncidentBoostBox.Text = N(o.StrengthPreview.IncidentIncrement);
        DeviceFeedbackBox.IsChecked = o.DeviceFeedback;
        IdlePolicyBox.SelectedIndex = (int)o.IdlePolicy;
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
        Increment = Number(StrengthIncrementBox), IntervalSeconds = Number(StrengthIntervalBox), IncidentIncrement = Number(IncidentBoostBox)
    };
    private SessionOptions ReadOptions()
    {
        var o = new SessionOptions
        {
            FocusMinutes = Number(FocusMinutesBox), RestMinutes = Number(RestMinutesBox), Mode = (StudyMode)ModeBox.SelectedIndex,
            Repeat = RepeatBox.IsChecked == true, HardMode = HardModeBox.IsChecked == true, GraceSeconds = Number(GraceBox),
            IdleSeconds = Number(IdleBox), CheckInMinutes = Number(CheckInMinutesBox), RecoveryDelaySeconds = Number(RecoveryDelayBox), RecoveryRate = Number(RecoveryRateBox),
            ApplicationMode = (RuleMode)AppPolicyBox.SelectedIndex, WebsiteMode = (RuleMode)WebPolicyBox.SelectedIndex,
            StrengthPreview = ReadStrength(), DeviceFeedback = DeviceFeedbackBox.IsChecked == true, IdlePolicy = (IdlePolicy)IdlePolicyBox.SelectedIndex
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
            device.SetDesired(session.OutputPercent);
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
            FeedbackLabel.Text = $"分心提醒 · 提醒等级 {e.Level}";
            if (settings.Sound && !smoke) System.Media.SystemSounds.Exclamation.Play();
            Pulse(NoticePanel);
        }
        Notice(e.Message);
        if (e.Kind is "completed" or "suspended") _ = device.StopAsync();
        else if (e.Kind is "rest" or "check-in") device.SetDesired(0);
        Save();
    }
    private void Save()
    {
        PersistPlanRules();
        settings.Plans = [..plans]; settings.SelectedPlanId = selectedPlanId;
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
        ModeLabel.Text = (o.Mode switch { StudyMode.Reading => "阅读 / 网课", StudyMode.Paper => "纸笔复习", _ => "电脑复习" }) + (o.HardMode ? " · 狠心" : "");
        DebtLabel.Text = $"{Math.Floor(session.DebtSeconds)} 秒";
        var curve = o.StrengthPreview;
        var atCeiling = curve.Enabled && curve.At(session.DebtSeconds, session.RecentCount(DateTimeOffset.Now)) >= curve.Maximum;
        DebtBar.Maximum = curve.IntervalSeconds;
        DebtBar.Value = !curve.Enabled ? 0 : atCeiling ? curve.IntervalSeconds : session.DebtSeconds % curve.IntervalSeconds;
        ThresholdLabel.Text = $"连续分心满 {o.GraceSeconds:0.##} 秒启动 · " + (!curve.Enabled ? "曲线已关闭"
            : atCeiling ? "已达曲线上限" : $"再累计 {Math.Ceiling(curve.IntervalSeconds - DebtBar.Value)} 秒递增");
        IncidentLabel.Text = $"{session.RecentCount(DateTimeOffset.Now)} 次分心";
        ReasonLabel.Text = session.StatusReason;
        StrengthValueLabel.Text = $"{session.PreviewStrength:0.#}% / {o.StrengthPreview.Maximum:0.#}%";
        StrengthBar.Maximum = Math.Max(1, o.StrengthPreview.Maximum); StrengthBar.Value = session.PreviewStrength;
        StartButton.Content = session.State switch { SessionState.Suspended => "继续本次会话", SessionState.Resting => "结束休息", SessionState.Focusing => "专注进行中", SessionState.ExitPending => "继续复习，等待退出", _ => "开始专注" };
        StartButton.IsEnabled = storage.LoadError == null && (session.IsActive || SelectedPlan != null) && session.State is not (SessionState.Focusing or SessionState.ExitPending);
        ExitButton.IsEnabled = !session.IsHardModeLocked && session.State is SessionState.Focusing or SessionState.Resting;
        ExitButton.Content = session.IsHardModeLocked ? "到时自动结束" : "申请退出";
        ExitMenuItem.IsEnabled = !session.IsHardModeLocked;
        BreakButton.IsEnabled = !session.IsHardModeLocked;
        ExitButton.Visibility = session.State == SessionState.ExitPending ? Visibility.Collapsed : Visibility.Visible;
        CancelExitButton.Visibility = session.State == SessionState.ExitPending ? Visibility.Visible : Visibility.Collapsed;
        CheckInPanel.Visibility = session.NeedsCheckIn ? Visibility.Visible : Visibility.Collapsed;
        TemporaryButton.IsEnabled = session.IsActive && session.TemporaryHost == null && o.WebsiteMode == RuleMode.Whitelist;
        TemporaryInput.IsEnabled = TemporaryButton.IsEnabled;
        TemporaryLabel.Text = session.TemporaryHost != null ? $"本次临时允许：{session.TemporaryHost} · 剩余 0 个名额"
            : o.WebsiteMode == RuleMode.Blacklist ? "网站黑名单模式：未命中的域名已允许，无需临时放行。" : "每次会话可临时新增 1 个域名，仅本次有效，黑名单优先。";
        var editable = !session.IsActive && SelectedPlan != null;
        if (appSelectionPlan != null && (!editable || appSelectionPlan != selectedPlanId)) CancelAppSelection();
        RefreshApplicationSelectionControls();
        AddAppButton.IsEnabled = BrowserRuleEditor.IsEnabled = SubdomainsBox.IsEnabled = SessionSettingsEditor.IsEnabled = editable;
        PolicyEditor.IsEnabled = AppRuleEditor.IsEnabled = StrengthSettingsEditor.IsEnabled = editable;
        RefreshPlans();
        RefreshDeviceControls();
        RefreshBrowserSetup();
        var summary = $"白名单 {rules.Count(x => x.Action == RuleAction.Allow)} 条 · 黑名单 {rules.Count(x => x.Action == RuleAction.Block)} 条。";
        RuleLockLabel.Text = summary + (session.IsActive ? "本次会话规则与模式已锁定。" : "黑名单始终优先，白名单模式下未列出内容算分心。");
        floating?.Update(stateText, Clock(seconds), session.State, session.NeedsCheckIn, session.IsHardModeLocked);
        RefreshOutputDisplay();
        RefreshTutorialStatus();
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
        if (name != "PlansPage") CancelAppSelection();
        page = name;
        PageScroll.ScrollToTop();
        foreach (FrameworkElement child in PageHost.Children) child.Visibility = child.Name == name ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = name switch
        {
            "PlansPage" => ("专注方案", "把黑白名单、专注节奏与强度曲线保存在同一个方案中。"),
            "SettingsPage" => ("偏好设置", "调整提示音与界面动态效果。"),
            "DevicePage" => ("设备与浏览器", "所有专注判断在本机完成。"),
            "HistoryPage" => ("会话记录", "查看规则变化与提醒发生的时间。"),
            _ => ("专注控制台", FocusMottos.Lines[focusMottoIndex])
        };
        PageSubtitle.Focusable = name == "FocusPage";
        PageSubtitle.Cursor = name == "FocusPage" ? Cursors.Hand : Cursors.Arrow;
        PageSubtitle.ToolTip = name == "FocusPage" ? "点击软件内任意位置换一句；此处也可按回车或空格切换" : null;
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
        if (storage.LoadError != null) { Notice(storage.LoadError); return; }
        try
        {
            if (session.State == SessionState.Suspended)
            {
                if (session.Options.DeviceFeedback && !device.IsArmed) { Notice("本次会话使用真实输出，请先在设备页重新授权。"); ShowPage("DevicePage"); return; }
                session.Resume(DateTimeOffset.Now);
            }
            else if (session.State == SessionState.Resting) session.EndBreak(DateTimeOffset.Now);
            else if (!session.IsActive)
            {
                if (SelectedPlan == null) { Notice("请先创建并选择一个专注方案。"); ShowPage("PlansPage"); PlanPicker.Focus(); return; }
                CommitSelectedPlan();
                if (SelectedPlan!.Options.DeviceFeedback && !device.IsArmed) { Notice("此方案启用了真实输出，请先连接设备并授权本次上限。"); Save(); ShowPage("DevicePage"); return; }
                session.Start(SelectedPlan, DateTimeOffset.Now);
                FeedbackLabel.Text = session.Options.DeviceFeedback ? "设备反馈待命 · 本次已授权" : "模拟反馈待命 · 无设备输出";
                FloatClick(sender, e);
            }
            lastTick = elapsed.Elapsed;
            ShowPage("FocusPage"); Save(); Refresh();
        }
        catch (ArgumentException ex) { Notice(ex.Message); ShowPage("PlansPage"); }
    }
    private void PresetClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive || SelectedPlan == null) { Notice("请先选择方案，会话进行中不能修改时长。"); return; }
        FocusMinutesBox.Text = (string)((Button)sender).Tag;
        SaveSettingsClick(sender, e);
    }
    private void SaveSettingsClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive) { Notice("请在会话结束后修改设置。"); return; }
        try { CommitSelectedPlan(); Save(); Refresh(); Notice("方案已保存：" + SelectedPlan!.Name); }
        catch (ArgumentException ex) { Notice(ex.Message); }
    }
    private void PreferencesClick(object sender, RoutedEventArgs e)
    {
        settings.Sound = SoundBox.IsChecked == true; settings.ReducedMotion = ReducedMotionBox.IsChecked == true;
        Save(); Refresh();
    }
    private void PolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || session.IsActive || SelectedPlan == null || AppPolicyBox.SelectedIndex < 0 || WebPolicyBox.SelectedIndex < 0) return;
        if (sender == AppPolicyBox) AppActionBox.SelectedIndex = AppPolicyBox.SelectedIndex;
        if (sender == WebPolicyBox) WebActionBox.SelectedIndex = WebPolicyBox.SelectedIndex;
        Notice("已修改当前方案的黑白名单模式。保存方案或开始专注时生效。");
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
            StrengthValidationLabel.Text = preview.Enabled ? "横轴为累计分心秒数，从 0 秒起按起始值计算；图示首次分心的前五次递增。启动延迟在「专注节奏」设置。" : "曲线已关闭，不产生曲线反馈。启用后保存方案，在下一次会话生效。";
            RampChart.Update(preview);
        }
        catch (ArgumentException ex)
        {
            StrengthValidationLabel.Foreground = (Brush)FindResource("Danger");
            StrengthValidationLabel.Text = ex.Message; RampChart.Update(new() { Enabled = false });
        }
    }
    private void ExitClick(object sender, RoutedEventArgs e)
    {
        if (session.IsHardModeLocked) { Notice("狠心模式将在约定时长结束后退出。立即停电始终可用。"); return; }
        session.RequestExit(DateTimeOffset.Now); Save(); ShowPage("FocusPage"); Refresh();
    }
    private void CancelExitClick(object sender, RoutedEventArgs e) { session.CancelExit(DateTimeOffset.Now); Save(); Refresh(); }
    private void CheckInClick(object sender, RoutedEventArgs e) { session.CheckIn(DateTimeOffset.Now); Save(); Refresh(); }
    private void BreakClick(object sender, RoutedEventArgs e)
    {
        if (session.IsHardModeLocked) { Notice("狠心模式不允许提前休息。立即停电始终可用。"); return; }
        session.TakeBreak(DateTimeOffset.Now); Save(); ShowPage("FocusPage"); Refresh();
    }
    private void EmergencyClick(object sender, RoutedEventArgs e) => SuspendForFault("会话已暂停，已停止续发并撤销设备授权。继续时保留本次规则。");
    public void SuspendForFault(string reason)
    {
        if (!session.IsActive || session.State == SessionState.Suspended) _ = device.StopAsync();
        session.Suspend(DateTimeOffset.Now, reason);
        Save(); Refresh(); Notice(reason);
    }
    private void TemporaryClick(object sender, RoutedEventArgs e)
    {
        if (session.AddTemporaryDomain(TemporaryInput.Text, DateTimeOffset.Now, out var message)) TemporaryInput.Clear();
        Notice(message); Save(); Refresh();
    }
    private void AddAppClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive || SelectedPlan == null) return;
        CancelAppSelection();
        var picker = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", Title = AppActionBox.SelectedIndex == 1 ? "选择加入黑名单的软件" : "选择加入白名单的软件" };
        if (picker.ShowDialog(this) == true && Rules.TryCreate(RuleKind.Application, picker.FileName, false, out var rule))
            AddRule(rule! with { Action = AppActionBox.SelectedIndex == 1 ? RuleAction.Block : RuleAction.Allow });
    }
    private void AddWebClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive || SelectedPlan == null) return;
        var kind = RuleKindBox.SelectedIndex switch { 1 => RuleKind.Page, 2 => RuleKind.PathPrefix, _ => RuleKind.Domain };
        if (Rules.TryCreate(kind, RuleInput.Text, SubdomainsBox.IsChecked == true, out var rule)) { AddRule(rule! with { Action = WebActionBox.SelectedIndex == 1 ? RuleAction.Block : RuleAction.Allow }); RuleInput.Clear(); }
        else Notice("域名请输入 example.com，页面或路径规则请输入以 https:// 开头的完整地址。");
    }
    private void AddRule(AllowRule rule)
    {
        if (session.IsActive || SelectedPlan == null) return;
        if (rules.Contains(rule) || rule.Kind == RuleKind.Application && rules.Any(x => x.Kind == RuleKind.Application && x.Action == rule.Action &&
            string.Equals(x.Value, rule.Value, StringComparison.OrdinalIgnoreCase))) { Notice("这条规则已经存在。"); return; }
        rules.Add(rule); Save(); Refresh(); Notice((rule.Action == RuleAction.Block ? "黑名单" : "白名单") + "规则已添加，黑名单始终优先。");
    }
    private void RemoveRuleClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive || SelectedPlan == null) { Notice("请在会话结束后编辑方案规则。"); return; }
        rules.Remove((AllowRule)((Button)sender).Tag); Save(); Refresh();
    }
    private void FloatClick(object sender, RoutedEventArgs e)
    {
        if (floating == null)
        {
            floating = new FloatingWindow();
            floating.PreviewMouseLeftButtonDown += MottoPointerClick;
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
        tray = new System.Windows.Forms.NotifyIcon { Text = "郊狼自习室 · Windows", Icon = System.Drawing.SystemIcons.Application, Visible = true };
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/focus.ico")).Stream)
            tray.Icon = new System.Drawing.Icon(stream);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开控制台", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add("显示悬浮球", null, (_, _) => Dispatcher.Invoke(() => FloatClick(this, new RoutedEventArgs())));
        var exitItem = menu.Items.Add("申请退出专注", null, (_, _) => Dispatcher.Invoke(() => ExitClick(this, new RoutedEventArgs())));
        menu.Items.Add("立即停电 / 紧急暂停", null, (_, _) => Dispatcher.Invoke(() => EmergencyClick(this, new RoutedEventArgs())));
        var closeItem = menu.Items.Add("停止输出并关闭软件", null, (_, _) => Dispatcher.Invoke(CloseSoftware));
        menu.Opening += (_, _) => { exitItem.Enabled = closeItem.Enabled = !session.IsHardModeLocked; };
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
        if (e.Key == Key.F1) { TutorialMenuClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Escape && TutorialOverlay.Visibility == Visibility.Visible)
        { TutorialSkipClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Escape && appSelectionPlan != null)
        { CancelAppSelectionClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key is Key.OemPlus or Key.Add) { ZoomInClick(sender, e); e.Handled = true; }
        if (e.Key is Key.OemMinus or Key.Subtract) { ZoomOutClick(sender, e); e.Handled = true; }
    }
    private void ZoomInClick(object sender, RoutedEventArgs e) => SetZoom(Math.Min(1.4, zoom + .1));
    private void ZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(Math.Max(1, zoom - .1));
    private void SetZoom(double value)
    {
        zoom = value;
        if (zoom == 1) RootGrid.ClearValue(LayoutTransformProperty);
        else RootGrid.LayoutTransform = new ScaleTransform(zoom, zoom);
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
        if (connectingDevice) return;
        if (session.IsActive && session.Options.DeviceFeedback) SuspendForFault("重新配对设备，专注会话已暂停。");
        var local = ConnectionModeBox.SelectedIndex == 0;
        var network = LanAddressBox.SelectedItem as LanAddress;
        if (local && network == null) { Notice("未找到局域网 IPv4 地址，请联网后刷新网卡，或选择官方公网中继。"); return; }
        connectingDevice = true; ClearPairing(); RefreshConnectionControls();
        try
        {
            await device.DisconnectAsync(); await StopLocalRelay();
            DeviceStatusLabel.Text = local ? "正在启动局域网服务…" : "正在连接官方中继…";
            if (local)
            {
                localRelay = new LocalDglabRelay();
                await localRelay.StartAsync(network!.Address, smoke ? 0 : LocalDglabRelay.Port);
                await device.Connect(localRelay.Address);
                if (!device.IsConnected) await StopLocalRelay();
            }
            else await device.Connect();
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
        {
            await StopLocalRelay();
            DeviceStatusLabel.Text = "局域网服务启动失败：地址已变化或端口 17841 被占用。请刷新网卡后重试。";
        }
        finally { connectingDevice = false; RefreshConnectionControls(); }
    }
    private async void DeviceDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive && session.Options.DeviceFeedback) SuspendForFault("设备已断开，专注会话暂停。");
        if (connectingDevice) return;
        connectingDevice = true; RefreshConnectionControls();
        try { await device.DisconnectAsync(); await StopLocalRelay(); ClearPairing(); }
        finally { connectingDevice = false; RefreshConnectionControls(); }
        DeviceStatusLabel.Text = "已断开连接"; Refresh();
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (session.IsHardModeLocked)
        { e.Cancel = true; Notice("狠心模式尚未完成，不能提前关闭。立即停电始终可用。"); return; }
        if (session.IsActive && session.State != SessionState.Suspended)
        {
            e.Cancel = true;
            if (session.State != SessionState.ExitPending) ExitClick(this, new RoutedEventArgs());
            Notice("专注会话将在倒计时结束后退出。悬浮球可取消退出，立即暂停始终可用。");
            return;
        }
        e.Cancel = true; Quit();
    }
    private void CloseSoftware()
    {
        if (session.IsHardModeLocked) { ShowMain(); Notice("狠心模式将在约定时长结束后退出。立即停电始终可用。"); return; }
        SuspendForFault("软件已关闭，继续时需确认上次会话。"); Quit();
    }
    private void HardModeChanged(object sender, RoutedEventArgs e) => RefreshHardModeEditor();
    private void RefreshHardModeEditor()
    {
        RestMinutesBox.IsEnabled = RepeatBox.IsEnabled = HardModeBox.IsChecked != true;
    }
    private async void Quit()
    {
        if (closing) return;
        closing = true; timer.Stop(); Save();
        CancelAppSelection();
        await device.DisconnectAsync();
        await StopLocalRelay();
        bridge.Dispose(); floating?.Close(); tray?.Dispose();
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
            await RunTutorialSmoke(folder);
            if (StartButton.IsEnabled) throw new InvalidOperationException("未选方案时启动按钮应禁用。");
            StartClick(this, new RoutedEventArgs());
            if (session.IsActive || page != "PlansPage") throw new InvalidOperationException("菜单绕过了必选方案限制。");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "PlansEmpty"); }, DispatcherPriority.ApplicationIdle);
            ShowPage("FocusPage");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "FocusNoPlan"); }, DispatcherPriority.ApplicationIdle);
            NewPlanClick(this, new RoutedEventArgs());
            if (StrengthEnabledBox.IsChecked != true) throw new InvalidOperationException("新方案没有默认启用曲线预览。");
            await RunApplicationSelectionSmoke(folder, args);
            await RunBrowserSetupSmoke(folder);
            PlanNameBox.Text = "网课复习";
            rules.Add(new(RuleKind.Domain, "learn.example.com"));
            rules.Add(new(RuleKind.Page, "https://docs.example.com/course/lesson-1"));
            rules.Add(new(RuleKind.Application, @"C:\Program Files\StudyReader\StudyReader.exe"));
            rules.Add(new(RuleKind.Domain, "games.example.com", false, RuleAction.Block));
            rules.Add(new(RuleKind.Application, @"C:\Program Files\ExampleGame\Game.exe", false, RuleAction.Block));
            ModeBox.SelectedIndex = (int)StudyMode.Reading;
            SaveSettingsClick(this, new RoutedEventArgs());
            var firstId = selectedPlanId;
            ShowPage("PlansPage"); ShowPlanTab("StrengthEditor");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "StrengthSetup"); }, DispatcherPriority.ApplicationIdle);
            PageScroll.ScrollToBottom();
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "StrengthCurve"); }, DispatcherPriority.ApplicationIdle);
            PageScroll.ScrollToTop();
            CopyPlanClick(this, new RoutedEventArgs());
            var secondId = selectedPlanId;
            PlanNameBox.Text = "纸笔复习"; ModeBox.SelectedIndex = (int)StudyMode.Paper;
            FocusMinutesBox.Text = "25"; rules.Clear();
            SaveSettingsClick(this, new RoutedEventArgs());
            StrengthMaximumBox.Text = "5";
            SaveSettingsClick(this, new RoutedEventArgs());
            if (settings.Options.StrengthPreview.Maximum != 40 || !StrengthValidationLabel.Text.Contains("上限"))
                throw new InvalidOperationException("无效的模拟强度设置被保存。");
            FocusPlanPicker.SelectedValue = firstId;
            if (selectedPlanId != secondId) throw new InvalidOperationException("无效参数被切换方案时静默丢弃。");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "InvalidStrength"); }, DispatcherPriority.ApplicationIdle);
            StrengthMaximumBox.Text = "40";
            DeletePlanClick(this, new RoutedEventArgs());
            if (SelectedPlan != null || StartButton.IsEnabled || plans.Count != 1) throw new InvalidOperationException("删除方案后被自动选择了其他方案。");
            StartClick(this, new RoutedEventArgs());
            if (session.IsActive) throw new InvalidOperationException("删除后未选方案仍可启动。");
            UndoDeletePlanClick(this, new RoutedEventArgs());
            if (plans.Count != 2 || selectedPlanId != secondId) throw new InvalidOperationException("撤销删除没有恢复方案。");
            FocusPlanPicker.SelectedValue = firstId;
            if (rules.Count != 5 || settings.Options.FocusMinutes != 50 || settings.Options.Mode != StudyMode.Reading)
                throw new InvalidOperationException("方案之间的参数或黑白名单相互污染。");
            ShowPage("PlansPage"); ShowPlanTab("RulesEditor");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "PlansRules"); }, DispatcherPriority.ApplicationIdle);
            ShowPlanTab("RhythmEditor");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "PlansRhythm"); }, DispatcherPriority.ApplicationIdle);
            PageScroll.ScrollToBottom();
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "PlansRhythmTiming"); }, DispatcherPriority.ApplicationIdle);
            Width = 920; Height = 680;
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); PageScroll.ScrollToBottom(); UpdateLayout(); Capture(folder, "PlansRhythmTimingCompact"); }, DispatcherPriority.ApplicationIdle);
            Width = 1180; Height = 830; PageScroll.ScrollToTop();
            DeviceFeedbackBox.IsChecked = true; SaveSettingsClick(this, new RoutedEventArgs());
            StartClick(this, new RoutedEventArgs());
            if (session.IsActive || page != "DevicePage") throw new InvalidOperationException("未授权设备的真实方案仍可启动。");
            PageScroll.ScrollToTop();
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "DeviceSetup"); }, DispatcherPriority.ApplicationIdle);
            DeviceFeedbackBox.IsChecked = false; SaveSettingsClick(this, new RoutedEventArgs());
            StartClick(this, new RoutedEventArgs());
            if (session.PlanId != firstId || session.PlanName != "网课复习") throw new InvalidOperationException("启动没有绑定选择的方案。");
            CaptureNextWindowClick(this, new RoutedEventArgs()); ChooseRunningAppClick(this, new RoutedEventArgs());
            if (appSelectionPlan != null || AppRuleEditor.IsEnabled) throw new InvalidOperationException("专注会话中仍能选择软件或修改名单。");
            PlanPicker.SelectedValue = secondId;
            if (selectedPlanId != firstId || session.PlanId != firstId || PlanPicker.SelectedValue as Guid? != firstId)
                throw new InvalidOperationException("会话中可以切换方案。");
            for (int i = 0; i < 1120; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, 0);
            session.CheckIn(DateTimeOffset.Now);
            session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, 0);
            session.AddTemporaryDomain("reference.example.com", DateTimeOffset.Now, out _);
            CurrentAppLabel.Text = "Microsoft Edge"; CurrentUrlLabel.Text = "learn.example.com";
            DetectionLabel.Text = "允许的学习内容";
            Refresh();
            ShowPlanTab("RulesEditor");
            foreach (var name in new[] { "FocusPage", "PlansPage", "SettingsPage", "DevicePage", "HistoryPage" })
            {
                ShowPage(name); await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, name); }, DispatcherPriority.ApplicationIdle);
                if (name == "PlansPage")
                {
                    PageScroll.ScrollToBottom();
                    await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "RuleEntries"); }, DispatcherPriority.ApplicationIdle);
                }
            }
            ShowPage("FocusPage");
            CurrentAppLabel.Text = "Example Game"; CurrentUrlLabel.Text = "示例软件黑名单"; DetectionLabel.Text = "当前内容不符合黑白名单";
            for (int i = 0; i < 9; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Outside, 0);
            if (session.PreviewStrength != 0) throw new InvalidOperationException("启动延迟结束前产生了强度。");
            Refresh();
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "DistractionCountdown"); }, DispatcherPriority.ApplicationIdle);
            session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Outside, 0);
            if (session.PreviewStrength != 10 || session.DebtSeconds != 0) throw new InvalidOperationException("分心满 10 秒时没有以零累计启动。");
            Refresh();
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "DistractionStarted"); }, DispatcherPriority.ApplicationIdle);
            for (int i = 0; i < 100; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Outside, 0);
            Refresh();
            if (session.PreviewStrength != 25) throw new InvalidOperationException("模拟强度验证失败。");
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
            ShowPage("PlansPage"); ShowPlanTab("RulesEditor");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "PlansCompact"); }, DispatcherPriority.ApplicationIdle);
            Width = 1180; Height = 830; ShowPage("DevicePage");
            ConnectionModeBox.SelectedIndex = 0;
            var loopback = new LanAddress("本机协议测试", System.Net.IPAddress.Loopback, true);
            LanAddressBox.ItemsSource = new[] { loopback }; LanAddressBox.SelectedIndex = 0;
            DeviceConnectClick(this, new RoutedEventArgs());
            var pairingUntil = Environment.TickCount64 + 4500;
            while ((connectingDevice || pairingUrl == null) && Environment.TickCount64 < pairingUntil) await Task.Delay(20);
            if (pairingUrl == null || PairingPanel.Visibility != Visibility.Visible || !PairingAddressBox.Text.StartsWith("ws://127.0.0.1:") ||
                ConnectionModeBox.IsEnabled || LanAddressBox.IsEnabled || device.IsArmed)
                throw new InvalidOperationException("局域网配对界面未显示可用地址或未锁定连接设置。");
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "DevicePairingLocalTest"); }, DispatcherPriority.ApplicationIdle);
            Width = 920; Height = 680;
            PageScroll.ScrollToVerticalOffset(180);
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "DevicePairingCompact"); }, DispatcherPriority.ApplicationIdle);
            DeviceDisconnectClick(this, new RoutedEventArgs());
            while (connectingDevice) await Task.Delay(20);
            if (PairingPanel.Visibility != Visibility.Collapsed || PairingAddressBox.Text.Length != 0 || !ConnectionModeBox.IsEnabled)
                throw new InvalidOperationException("局域网断开后没有清理地址并解锁设置。");
            RefreshLanAddresses();
            await RunHardModeSmoke(folder, firstId);
            if (Array.IndexOf(args, "--device-test-relay") >= 0)
            {
                SelectPlan(firstId); DeviceFeedbackBox.IsChecked = true; IncidentBoostBox.Text = "0"; SaveSettingsClick(this, new RoutedEventArgs());
                await device.Connect();
                var until = Environment.TickCount64 + 4000;
                while (device.Devices.Count == 0 && Environment.TickCount64 < until) await Task.Delay(20);
                RefreshDeviceList(); DevicePicker.SelectedIndex = 0; DeviceCapBox.Text = "20";
                ArmDeviceClick(this, new RoutedEventArgs());
                until = Environment.TickCount64 + 4000;
                while (!device.IsArmed && Environment.TickCount64 < until) await Task.Delay(20);
                if (!device.IsArmed) throw new InvalidOperationException("本机模拟 App 的设备未能通过界面授权。");
                StartClick(this, new RoutedEventArgs());
                if (!session.IsActive || !session.Options.DeviceFeedback) throw new InvalidOperationException("真实设备方案没有启动。");
                for (int i = 1; i <= 14; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, i);
                device.SetDesired(session.OutputPercent); await Task.Delay(250);
                if (session.OutputPercent != 0 || device.RequestedIntensity != 0) throw new InvalidOperationException("允许内容的无操作计时提前产生了输出。");
                session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, 15);
                device.SetDesired(session.OutputPercent);
                until = Environment.TickCount64 + 2500;
                while (device.RequestedIntensity != 2 && Environment.TickCount64 < until) await Task.Delay(20);
                if (device.RequestedIntensity != 2 || session.DebtSeconds != 0) throw new InvalidOperationException("允许内容未在无操作 5 秒加启动延迟 10 秒后发出指令。");
                ShowPage("FocusPage"); CurrentAppLabel.Text = "Study Reader"; CurrentUrlLabel.Text = "示例允许的软件"; DetectionLabel.Text = "允许的学习内容";
                Refresh();
                await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "AllowedIdleOutput"); }, DispatcherPriority.ApplicationIdle);
                session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Allowed, 0);
                device.SetDesired(session.OutputPercent);
                until = Environment.TickCount64 + 2500;
                while (device.RequestedIntensity != 0 && Environment.TickCount64 < until) await Task.Delay(20);
                if (device.RequestedIntensity != 0) throw new InvalidOperationException("允许内容恢复键鼠操作后没有清零请求。");
                for (int i = 0; i < 9; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Outside, 0);
                device.SetDesired(session.OutputPercent);
                await Task.Delay(250);
                if (device.RequestedIntensity != 0) throw new InvalidOperationException("分心未满 10 秒就发送了非零指令。");
                session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Outside, 0);
                if (session.DebtSeconds != 0 || session.OutputPercent != 10) throw new InvalidOperationException("零累计未按启动延迟开启反馈。");
                device.SetDesired(session.OutputPercent);
                until = Environment.TickCount64 + 2500;
                while (device.RequestedIntensity != 2 && Environment.TickCount64 < until) await Task.Delay(20);
                if (device.RequestedIntensity != 2) throw new InvalidOperationException("分心满 10 秒没有发出起始强度的真实协议指令。");
                await Dispatcher.InvokeAsync(() =>
                {
                    if (OutputIntensityLabel.Text != "2 / 20" || !OutputModeLabel.Text.Contains("A 通道"))
                        throw new InvalidOperationException("第 10 秒没有在主界面显示当前请求强度与上限。");
                }, DispatcherPriority.ApplicationIdle);
                for (int i = 0; i < 30; i++) session.Tick(TimeSpan.FromSeconds(1), DateTimeOffset.Now, Attention.Outside, 0);
                device.SetDesired(session.OutputPercent);
                until = Environment.TickCount64 + 2500;
                while (device.RequestedIntensity != 3 && Environment.TickCount64 < until) await Task.Delay(20);
                if (device.RequestedIntensity != 3) throw new InvalidOperationException("累计 30 秒没有递增真实协议指令的强度。");
                await Dispatcher.InvokeAsync(() =>
                {
                    if (OutputIntensityLabel.Text != "3 / 20") throw new InvalidOperationException("递增后的请求强度未同步到主界面。");
                }, DispatcherPriority.ApplicationIdle);
                ShowPage("FocusPage"); Width = 1180; Height = 830; Refresh();
                CurrentAppLabel.Text = "Example Game"; CurrentUrlLabel.Text = "示例软件黑名单"; DetectionLabel.Text = "当前内容不符合黑白名单";
                await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "OutputActive"); }, DispatcherPriority.ApplicationIdle);
                Width = 920; Height = 680;
                await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "OutputActiveCompact"); }, DispatcherPriority.ApplicationIdle);
                floating?.Close(); FloatClick(this, new RoutedEventArgs());
                if (floating!.FindName("BallStrengthLabel") is not TextBlock { Text: "3" })
                    throw new InvalidOperationException("悬浮球未显示真实请求强度。");
                floating.Capture(Path.Combine(folder, "OutputBall.png")); floating.Expand();
                if (floating.FindName("FeedbackValueLabel") is not TextBlock { Text: "3 / 20" })
                    throw new InvalidOperationException("展开的悬浮球未显示当前强度与上限。");
                floating.Capture(Path.Combine(folder, "OutputFloating.png"));
                Width = 1180; Height = 830; ShowPage("DevicePage"); Refresh();
                await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "DeviceArmedLocalTest"); }, DispatcherPriority.ApplicationIdle);
                EmergencyClick(this, new RoutedEventArgs());
                if (device.IsArmed || session.State != SessionState.Suspended) throw new InvalidOperationException("急停未撤销授权或暂停会话。");
                await device.StopAsync();
                if (device.RequestedIntensity != 0) throw new InvalidOperationException("急停未清零设备指令。");
                RefreshOutputDisplay();
                if (OutputIntensityLabel.Text != "0" || !OutputModeLabel.Text.Contains("待授权") || floating.FindName("BallStrengthLabel") is not TextBlock { Text: "0" }
                    || DeviceStatusLabel.Text.StartsWith("设备已授权", StringComparison.Ordinal))
                    throw new InvalidOperationException("急停后强度显示没有清零或未标明需要重新授权。");
                await Dispatcher.InvokeAsync(() => { UpdateLayout(); Capture(folder, "DeviceStoppedLocalTest"); }, DispatcherPriority.ApplicationIdle);
            }
            File.WriteAllText(Path.Combine(folder, "smoke-result.txt"), $"PASS: empty plan library, required selection, create/copy/rename/delete/undo, isolated plan rules and timing, invalid edits block switching, active plan locked.\n5 pages, plan tabs, default strength curve, validation, active simulation, exit, floating and compact layouts rendered.\nUnknown state cleared preview. No physical devices or user settings used.\nRuntime: {RuntimeInformation.FrameworkDescription}\n");
            File.AppendAllText(Path.Combine(folder, "smoke-result.txt"), "PASS: built-in relay on loopback only: UI generates a QR and manual server address, locks network choices, disconnects and clears credentials. Wide and compact layouts rendered.\n");
            File.AppendAllText(Path.Combine(folder, "smoke-result.txt"), File.ReadAllText(Path.Combine(folder, "app-selection-result.txt")));
            File.AppendAllText(Path.Combine(folder, "smoke-result.txt"), File.ReadAllText(Path.Combine(folder, "hard-mode-result.txt")));
            File.AppendAllText(Path.Combine(folder, "smoke-result.txt"), File.ReadAllText(Path.Combine(folder, "tutorial-result.txt")));
            if (Array.IndexOf(args, "--device-test-relay") >= 0)
                File.AppendAllText(Path.Combine(folder, "smoke-result.txt"), "PASS: local fake App only: UI authorization → selected plan → 10-second activation with zero debt → accumulated-time ramp → bounded V4 requests → emergency stop. Live requested intensity in main header, collapsed ball and expanded panel follows controller changes and clears on stop. Allowed-content idle 5+10 seconds emits bounded output; new input clears it. No physical device connected.\n");
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(folder, "smoke-result.txt"), "FAIL: " + e); Environment.ExitCode = 1; }
        Quit();
    }
    private void Capture(string folder, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(RootGrid.ActualWidth), (int)Math.Ceiling(RootGrid.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        var bounds = new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
        var offset = VisualTreeHelper.GetOffset(RootGrid);
        using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(RootGrid)
        { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, bounds.Width, bounds.Height), Stretch = Stretch.Fill }, null, bounds);
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
