namespace DglabFocus.Core;

public enum StudyMode { Computer, Reading, Paper }
public enum SessionState { Idle, Focusing, Resting, ExitPending, Suspended, Completed }
public sealed record SessionOptions
{
    public double FocusMinutes { get; init; } = 50;
    public double RestMinutes { get; init; } = 10;
    public bool Repeat { get; init; }
    public StudyMode Mode { get; init; }
    public RuleMode ApplicationMode { get; init; }
    public RuleMode WebsiteMode { get; init; }
    public StrengthPreviewOptions StrengthPreview { get; init; } = new();
    public double GraceSeconds { get; init; } = 10;
    public double AlertThresholdSeconds { get; init; } = 60;
    public double RecoveryDelaySeconds { get; init; } = 120;
    public double RecoveryRate { get; init; } = 0.5;
    public double IdleSeconds { get; init; } = 180;
    public double CheckInMinutes { get; init; } = 15;
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(ApplicationMode) || !Enum.IsDefined(WebsiteMode))
            throw new ArgumentException("请选择有效的学习方式与黑白名单模式。");
        if (StrengthPreview == null) throw new ArgumentException("模拟强度设置无效。");
        StrengthPreview.Validate();
        if (!double.IsFinite(FocusMinutes) || FocusMinutes < 0 || FocusMinutes > 1440
            || !double.IsFinite(RestMinutes) || RestMinutes < 0 || RestMinutes > 240
            || !double.IsFinite(GraceSeconds) || GraceSeconds < 1 || GraceSeconds > 300
            || !double.IsFinite(AlertThresholdSeconds) || AlertThresholdSeconds < 5 || AlertThresholdSeconds > 3600
            || !double.IsFinite(RecoveryDelaySeconds) || RecoveryDelaySeconds < 0 || RecoveryDelaySeconds > 3600
            || !double.IsFinite(RecoveryRate) || RecoveryRate <= 0 || RecoveryRate > 10
            || !double.IsFinite(IdleSeconds) || IdleSeconds < 30 || IdleSeconds > 3600
            || !double.IsFinite(CheckInMinutes) || CheckInMinutes < 1 || CheckInMinutes > 120)
            throw new ArgumentException("计时参数不在允许范围内。");
    }
}
public sealed record SessionEvent(DateTimeOffset At, string Kind, string Message, int Level = 0);
public sealed record SessionSnapshot
{
    public SessionOptions Options { get; init; } = new();
    public SessionState State { get; init; }
    public SessionState ResumeState { get; init; }
    public List<AllowRule> Rules { get; init; } = [];
    public string? TemporaryHost { get; init; }
    public double FocusSeconds { get; init; }
    public double SegmentSeconds { get; init; }
    public double RestRemaining { get; init; }
    public double ExitRemaining { get; init; }
    public double DebtSeconds { get; init; }
    public double SinceCheckIn { get; init; }
    public bool RestEndsSession { get; init; }
    public List<DateTimeOffset> Incidents { get; init; } = [];
}

public sealed class Session
{
    public const double ExitDelaySeconds = 300;
    public SessionOptions Options { get; private set; } = new();
    public SessionState State { get; private set; }
    public SessionState ResumeState { get; private set; } = SessionState.Focusing;
    public List<AllowRule> Rules { get; private set; } = [];
    public string? TemporaryHost { get; private set; }
    public double FocusSeconds { get; private set; }
    public double SegmentSeconds { get; private set; }
    public double RestRemaining { get; private set; }
    public double ExitRemaining { get; private set; }
    public double DebtSeconds { get; private set; }
    public double SinceCheckIn { get; private set; }
    public bool NeedsCheckIn { get; private set; }
    public string StatusReason { get; private set; } = "配置允许使用的内容，开始这一段专注。";
    public List<DateTimeOffset> Incidents { get; private set; } = [];
    public bool IsActive => State is SessionState.Focusing or SessionState.Resting or SessionState.ExitPending or SessionState.Suspended;
    public event Action<SessionEvent>? Changed;
    private double outsideSeconds, recoverySeconds;
    private bool incidentCounted, alerted, restEndsSession;
    private bool previewEligible;
    public double PreviewStrength => previewEligible && !NeedsCheckIn && State is SessionState.Focusing or SessionState.ExitPending
        ? Options.StrengthPreview.At(DebtSeconds, Options.AlertThresholdSeconds) : 0;
    public int RecentCount(DateTimeOffset now) => Incidents.Count(x => x > now.AddHours(-1) && x <= now);

    public void Start(SessionOptions options, IEnumerable<AllowRule> rules, DateTimeOffset now)
    {
        if (IsActive) throw new InvalidOperationException("请先结束当前会话。");
        options.Validate();
        var copy = rules.ToList();
        if (options.Mode != StudyMode.Paper && options.ApplicationMode == RuleMode.Whitelist && options.WebsiteMode == RuleMode.Whitelist
            && !copy.Any(x => x.Action == RuleAction.Allow)) throw new ArgumentException("白名单模式请先添加至少一条白名单规则。");
        Options = options with { };
        Rules = copy;
        TemporaryHost = null;
        FocusSeconds = SegmentSeconds = DebtSeconds = SinceCheckIn = RestRemaining = ExitRemaining = 0;
        outsideSeconds = recoverySeconds = 0;
        NeedsCheckIn = incidentCounted = alerted = restEndsSession = false;
        previewEligible = false;
        State = SessionState.Focusing;
        Emit(now, "start", "专注已开始。临时域名名额：1 个。");
    }

    public bool AddTemporaryDomain(string value, DateTimeOffset now, out string message)
    {
        if (!IsActive) { message = "请先开始专注。"; return false; }
        if (TemporaryHost != null) { message = "本次会话已使用临时域名名额，无法替换。"; return false; }
        if (!DglabFocus.Core.Rules.TryHost(value, out var host)) { message = "请输入完整的域名，例如 docs.example.com。"; return false; }
        if (Rules.Any(x => x.Action == RuleAction.Block && x.Kind == RuleKind.Domain
            && DglabFocus.Core.Rules.MatchesUrl(x, "https://" + host + "/")))
        { message = "这个域名命中黑名单，临时放行不能覆盖黑名单，未占用名额。"; return false; }
        if (Options.WebsiteMode == RuleMode.Blacklist || Rules.Any(x => x.Action == RuleAction.Allow
            && DglabFocus.Core.Rules.MatchesUrl(x, "https://" + host + "/")))
        { message = "这个域名已经允许使用，未占用名额。"; return false; }
        TemporaryHost = host;
        Rules.Add(new(RuleKind.Domain, host));
        message = "已临时允许 " + host + "，本次会话剩余 0 个名额。";
        Emit(now, "temporary-domain", message);
        return true;
    }

    public void RequestExit(DateTimeOffset now)
    {
        if (State is not (SessionState.Focusing or SessionState.Resting)) return;
        ExitRemaining = ExitDelaySeconds;
        State = SessionState.ExitPending;
        Emit(now, "exit-pending", "退出倒计时 5 分钟。这段时间继续复习，可随时取消退出。");
    }
    public void CancelExit(DateTimeOffset now)
    {
        if (State != SessionState.ExitPending) return;
        State = SessionState.Focusing;
        ExitRemaining = 0;
        Emit(now, "exit-cancelled", "已取消退出，继续本次专注。");
    }
    public void Suspend(DateTimeOffset now, string reason)
    {
        if (!IsActive || State == SessionState.Suspended) return;
        ResumeState = State;
        State = SessionState.Suspended;
        previewEligible = false;
        outsideSeconds = recoverySeconds = 0;
        NeedsCheckIn = false;
        StatusReason = reason;
        Emit(now, "suspended", reason);
    }
    public void Resume(DateTimeOffset now)
    {
        if (State != SessionState.Suspended) return;
        State = ResumeState is SessionState.Focusing or SessionState.Resting or SessionState.ExitPending ? ResumeState : SessionState.Focusing;
        previewEligible = false;
        SinceCheckIn = 0;
        Emit(now, "resumed", "已继续本次会话，临时域名名额保持原样。");
    }
    public void CheckIn(DateTimeOffset now)
    {
        NeedsCheckIn = false;
        SinceCheckIn = 0;
        Emit(now, "check-in", "已确认仍在学习。");
    }
    public void TakeBreak(DateTimeOffset now)
    {
        if (State != SessionState.Focusing || Options.RestMinutes <= 0) return;
        State = SessionState.Resting;
        previewEligible = false;
        RestRemaining = Options.RestMinutes * 60;
        restEndsSession = false;
        outsideSeconds = recoverySeconds = 0;
        Emit(now, "rest", "开始休息，临时域名名额不会重置。");
    }
    public void EndBreak(DateTimeOffset now)
    {
        if (State != SessionState.Resting) return;
        if (restEndsSession) { Complete(now, "本次专注与休息已完成。"); return; }
        State = SessionState.Focusing;
        RestRemaining = SinceCheckIn = 0;
        Emit(now, "focus", "休息结束，继续学习。");
    }

    public void Tick(TimeSpan delta, DateTimeOffset now, Attention attention, double idleSeconds)
    {
        previewEligible = false;
        Incidents.RemoveAll(x => x <= now.AddHours(-1));
        if (!IsActive || State == SessionState.Suspended) return;
        var seconds = delta.TotalSeconds;
        // A stalled dispatcher or sleeping machine must never accumulate a catch-up penalty.
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        if (seconds > 5) { Suspend(now, "检测中断，已暂停计时。确认后继续，不补计中断时间。"); return; }
        if (State == SessionState.Resting)
        {
            RestRemaining = Math.Max(0, RestRemaining - seconds);
            StatusReason = "休息中，分心时间保持不变。";
            if (RestRemaining == 0) EndBreak(now);
            return;
        }
        FocusSeconds += seconds;
        SegmentSeconds += seconds;
        SinceCheckIn += seconds;
        if (State == SessionState.ExitPending)
        {
            ExitRemaining = Math.Max(0, ExitRemaining - seconds);
            if (ExitRemaining == 0) { Complete(now, "等待结束，已退出专注。"); return; }
        }
        else if (Options.FocusMinutes > 0 && SegmentSeconds >= Options.FocusMinutes * 60)
        {
            SegmentSeconds = 0;
            if (Options.RestMinutes > 0)
            {
                TakeBreak(now);
                restEndsSession = !Options.Repeat;
            }
            else if (!Options.Repeat) Complete(now, "本次专注已完成。");
            return;
        }

        NeedsCheckIn = Options.Mode == StudyMode.Computer
            ? idleSeconds >= Options.IdleSeconds && SinceCheckIn >= Options.IdleSeconds
            : SinceCheckIn >= Options.CheckInMinutes * 60;
        if (Options.Mode == StudyMode.Paper || NeedsCheckIn)
        {
            StatusReason = NeedsCheckIn ? "请确认是否仍在学习。未确认只作屏幕提醒。" : "纸笔模式，等待下一次学习确认。";
            ResetContinuity();
            return;
        }
        if (attention is Attention.Unknown or Attention.Neutral)
        {
            StatusReason = attention == Attention.Unknown ? "检测状态未知，暂停累计与恢复。" : "正在操作专注面板，暂停累计与恢复。";
            ResetContinuity();
            return;
        }
        if (attention == Attention.Outside)
        {
            recoverySeconds = 0;
            var before = outsideSeconds;
            outsideSeconds += seconds;
            previewEligible = outsideSeconds > Options.GraceSeconds;
            var increment = Math.Max(0, outsideSeconds - Options.GraceSeconds) - Math.Max(0, before - Options.GraceSeconds);
            DebtSeconds += increment;
            if (!incidentCounted && outsideSeconds > Options.GraceSeconds)
            {
                incidentCounted = true;
                Incidents.Add(now);
                Emit(now, "distraction", "离开允许内容超过宽限期，开始累计分心时间。");
            }
            if (!alerted && DebtSeconds >= Options.AlertThresholdSeconds)
            {
                alerted = true;
                var level = Math.Clamp(RecentCount(now), 1, 3);
                Emit(now, "feedback", "累计分心已达到提醒线，请回到学习内容。", level);
            }
            StatusReason = outsideSeconds <= Options.GraceSeconds ? $"切换宽限期，剩余 {Math.Ceiling(Options.GraceSeconds - outsideSeconds)} 秒。" : "当前内容未放行，分心时间正在累计。";
        }
        else
        {
            outsideSeconds = 0;
            var before = recoverySeconds;
            recoverySeconds += seconds;
            // Brief returns cannot split one distraction into many new incidents.
            if (recoverySeconds >= 5) incidentCounted = false;
            var recover = Math.Max(0, recoverySeconds - Options.RecoveryDelaySeconds) - Math.Max(0, before - Options.RecoveryDelaySeconds);
            DebtSeconds = Math.Max(0, DebtSeconds - recover * Options.RecoveryRate);
            if (DebtSeconds < Options.AlertThresholdSeconds * 0.5) alerted = false;
            StatusReason = DebtSeconds == 0 ? "正在使用允许的学习内容。" : recoverySeconds < Options.RecoveryDelaySeconds
                ? $"持续专注 {Math.Ceiling(Options.RecoveryDelaySeconds - recoverySeconds)} 秒后开始消除累计时间。" : "持续专注中，累计分心时间正在减少。";
        }
    }
    private void ResetContinuity() { outsideSeconds = recoverySeconds = 0; }
    private void Complete(DateTimeOffset now, string message)
    {
        State = SessionState.Completed;
        NeedsCheckIn = false;
        StatusReason = message;
        Emit(now, "completed", message);
    }
    private void Emit(DateTimeOffset now, string kind, string message, int level = 0) => Changed?.Invoke(new(now, kind, message, level));
    public SessionSnapshot Snapshot() => new()
    {
        State = State, ResumeState = ResumeState, Options = Options, Rules = [..Rules], TemporaryHost = TemporaryHost,
        FocusSeconds = FocusSeconds, SegmentSeconds = SegmentSeconds, RestRemaining = RestRemaining,
        ExitRemaining = ExitRemaining, DebtSeconds = DebtSeconds, SinceCheckIn = SinceCheckIn,
        Incidents = [..Incidents], RestEndsSession = restEndsSession
    };
    public static Session Restore(SessionSnapshot saved)
    {
        saved.Options.Validate();
        var s = new Session
        {
            Options = saved.Options, State = saved.State, ResumeState = saved.ResumeState, Rules = [..saved.Rules],
            TemporaryHost = saved.TemporaryHost, FocusSeconds = Math.Max(0, saved.FocusSeconds),
            SegmentSeconds = Math.Max(0, saved.SegmentSeconds), RestRemaining = Math.Max(0, saved.RestRemaining),
            ExitRemaining = Math.Clamp(saved.ExitRemaining, 0, ExitDelaySeconds), DebtSeconds = Math.Max(0, saved.DebtSeconds),
            SinceCheckIn = Math.Max(0, saved.SinceCheckIn), Incidents = [..saved.Incidents], restEndsSession = saved.RestEndsSession
        };
        if (s.IsActive)
        {
            if (s.State != SessionState.Suspended) s.ResumeState = s.State;
            s.State = SessionState.Suspended;
            s.StatusReason = "已恢复上次会话。点击继续，保留临时域名与滚动次数。";
        }
        return s;
    }
}
