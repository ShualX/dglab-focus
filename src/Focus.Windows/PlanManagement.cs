using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DglabFocus.Core;

namespace DglabFocus.Windows;

public partial class MainWindow
{
    private readonly ObservableCollection<FocusPlan> plans;
    private Guid? selectedPlanId;
    private FocusPlan? deletedPlan;
    private FocusPlan? SelectedPlan => plans.FirstOrDefault(x => x.Id == selectedPlanId);

    private void SyncPlanPickers()
    {
        var before = initializing; initializing = true;
        FocusPlanPicker.SelectedValue = PlanPicker.SelectedValue = selectedPlanId;
        initializing = before;
    }

    private void ReplacePlan(FocusPlan plan)
    {
        var index = plans.ToList().FindIndex(x => x.Id == plan.Id);
        if (index < 0) throw new ArgumentException("方案已不存在，请重新选择。");
        var before = initializing; initializing = true;
        plans[index] = plan;
        SyncPlanPickers(); initializing = before;
    }

    private void PersistPlanRules()
    {
        if (!session.IsActive && SelectedPlan is { } plan && !plan.Rules.SequenceEqual(rules))
            ReplacePlan(plan with { Rules = [..rules] });
    }

    private void CommitSelectedPlan()
    {
        var plan = SelectedPlan ?? throw new ArgumentException("请先选择一个专注方案。");
        var updated = plan with { Name = PlanNameBox.Text.Trim(), Options = ReadOptions(), Rules = [..rules] };
        updated.Validate();
        if (plans.Any(x => x.Id != plan.Id && string.Equals(x.Name, updated.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("这个方案名称已存在，请换一个名称。");
        ReplacePlan(updated);
        settings.Options = updated.Options;
    }

    private void SelectPlan(Guid? id)
    {
        CancelAppSelection();
        if (device.IsArmed) _ = device.StopAsync();
        selectedPlanId = id;
        var plan = SelectedPlan;
        settings.Options = plan?.Options ?? new();
        var before = initializing; initializing = true;
        rules.Clear(); foreach (var rule in plan?.Rules ?? []) rules.Add(rule);
        PlanNameBox.Text = plan?.Name ?? "";
        SyncPlanPickers(); LoadOptions(); initializing = before;
        UpdateStrengthPreview(); Refresh(); Save();
    }

    private void PlanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        if (session.IsActive) { SyncPlanPickers(); return; }
        var next = ((ComboBox)sender).SelectedItem as FocusPlan;
        if (next?.Id == selectedPlanId) return;
        try
        {
            if (SelectedPlan != null) CommitSelectedPlan();
            SelectPlan(next?.Id);
            Notice(next == null ? "请选择一个方案后开始专注。" : "已选择方案：" + next.Name);
        }
        catch (ArgumentException ex) { SyncPlanPickers(); ExplainUnavailableAction(sender, "当前方案尚未切换：" + ex.Message); }
    }

    private string NewPlanName(string prefix)
    {
        var basis = prefix.Length > 32 ? prefix[..32] : prefix;
        var name = basis; var number = 2;
        while (plans.Any(x => x.Name == name)) name = basis + " " + number++;
        return name;
    }

    private void NewPlanClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive) { ExplainUnavailableAction(sender, PlanEditReason); return; }
        try
        {
            if (SelectedPlan != null) CommitSelectedPlan();
            var plan = new FocusPlan { Name = NewPlanName("新方案") };
            initializing = true; plans.Add(plan); initializing = false;
            SelectPlan(plan.Id); ShowPlanTab("RulesEditor");
            PlanNameBox.Focus(); PlanNameBox.SelectAll();
            Notice("已创建方案。配置黑白名单后即可开始专注，强度曲线预览默认开启。");
        }
        catch (ArgumentException ex) { ExplainUnavailableAction(sender, ex.Message); }
    }

    private void CopyPlanClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive || SelectedPlan == null) { ExplainUnavailableAction(sender, PlanEditReason); return; }
        try
        {
            CommitSelectedPlan();
            var copy = SelectedPlan!.Duplicate(NewPlanName(SelectedPlan.Name + " 副本"));
            initializing = true; plans.Add(copy); initializing = false;
            SelectPlan(copy.Id); Notice("已复制方案。修改副本不会影响原方案。");
        }
        catch (ArgumentException ex) { ExplainUnavailableAction(sender, ex.Message); }
    }

    private void DeletePlanClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive || SelectedPlan == null) { ExplainUnavailableAction(sender, PlanEditReason); return; }
        deletedPlan = SelectedPlan;
        initializing = true; plans.Remove(deletedPlan); initializing = false;
        SelectPlan(null); Notice("已删除方案，可点击「撤销删除」恢复。请重新选择方案后开始专注。");
    }

    private void UndoDeletePlanClick(object sender, RoutedEventArgs e)
    {
        if (session.IsActive || deletedPlan == null) { ExplainUnavailableAction(sender, session.IsActive ? PlanEditReason : "当前没有可撤销的删除操作。"); return; }
        try
        {
            if (SelectedPlan != null) CommitSelectedPlan();
            var restored = deletedPlan with { Name = NewPlanName(deletedPlan.Name) }; deletedPlan = null;
            initializing = true; plans.Add(restored); initializing = false;
            SelectPlan(restored.Id); Notice("已恢复删除的方案。");
        }
        catch (ArgumentException ex) { ExplainUnavailableAction(sender, ex.Message); }
    }

    private void RefreshPlans()
    {
        var selected = SelectedPlan;
        var name = session.IsActive ? session.PlanName : selected?.Name;
        var options = session.IsActive ? session.Options : selected?.Options;
        var count = session.IsActive ? session.Rules.Count : selected?.Rules.Count ?? 0;
        SelectedPlanSummary.Text = options == null ? "尚未选择方案，无法开始专注。"
            : $"{name} · {(options.FocusMinutes == 0 ? "无限专注" : $"专注 {options.FocusMinutes:0.#} 分钟")} · {count} 条规则";
        PlanStatusLabel.Text = session.IsActive ? $"本次使用「{name}」，会话结束后可切换或编辑方案。"
            : plans.Count == 0 ? "还没有方案。点击「新建」，配置后再开始专注。"
            : selected == null ? $"共 {plans.Count} 个方案。请选择一个方案后开始专注。"
            : $"共 {plans.Count} 个方案。切换方案或开始专注时会保存当前有效修改。";
        FocusPlanPicker.IsEnabled = PlanPicker.IsEnabled = PlanActions.IsEnabled = !session.IsActive;
        PresetEditor.IsEnabled = !session.IsActive && selected != null;
        CopyPlanButton.IsEnabled = DeletePlanButton.IsEnabled = selected != null;
        UndoDeleteButton.Visibility = deletedPlan == null ? Visibility.Collapsed : Visibility.Visible;
        PlanEditor.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
        PlanNameBox.IsEnabled = SavePlanButton.IsEnabled = !session.IsActive;
    }

    private void ManagePlansClick(object sender, RoutedEventArgs e) => ShowPage("PlansPage");
    private void PlanTabClick(object sender, RoutedEventArgs e) => ShowPlanTab((string)((Button)sender).Tag);
    private void ShowPlanTab(string name)
    {
        if (name != "RulesEditor") CancelAppSelection();
        foreach (var panel in new[] { RulesEditor, RhythmEditor, StrengthEditor })
            panel.Visibility = panel.Name == name ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { PlanTabRules, PlanTabRhythm, PlanTabStrength })
        {
            var selected = (string)button.Tag == name;
            button.Background = (Brush)FindResource(selected ? "Accent" : "Raised");
            button.Foreground = (Brush)FindResource(selected ? "AccentText" : "Text");
        }
        PageScroll.ScrollToTop();
        Pulse((UIElement)FindName(name));
    }
}
