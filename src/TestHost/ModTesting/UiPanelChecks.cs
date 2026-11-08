using System.Globalization;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using Sts2Solver.Ai.Combat.Policy;
using Sts2Solver.Import;
using Sts2Solver.TestHost;

namespace Sts2Solver;

/// <summary>
/// Structural checks of the in-combat panel during one full-combat request. Every check compares
/// stable keys and numbers (phase enum names, turn numbers, global action indexes) with the
/// snapshot the panel last applied; localized text is never asserted. Results go to
/// <c>results/&lt;root&gt;/ui-panel.json</c>.
/// </summary>
internal static class UiPanelChecks
{
    private static bool _combatEndedSinceLastBegin;

    internal static async Task<Session> BeginAsync(NGame host)
    {
        Session session = new();
        SolverOverlay? panel = SolverOverlay.Instance;
        session.Check("panel_exists", panel != null, null);
        if (panel == null)
            return session;
        bool visible = await WaitAsync(host, () => panel.ProbeForTest().Visible, 180);
        session.Check("panel_visible_in_combat", visible, Describe(panel));
        session.Observer = new UiPanelObserver(panel) { Name = "Sts2SolverPanelObserver" };
        host.AddChild(session.Observer);
        bool previousCombatEnded = _combatEndedSinceLastBegin;
        _combatEndedSinceLastBegin = false;
        if (previousCombatEnded)
        {
            session.Check("panel_reappears_after_combat", visible, Describe(panel));
        }
        else
        {
            session.Skip("panel_reappears_after_combat", "first_combat");
        }
        if (previousCombatEnded)
        {
            session.Check("sidebar_closed_at_combat_start",
                SidebarClosed(panel) && !panel.SettingsOpenForTest, Describe(panel));
        }
        else
        {
            session.Skip("sidebar_closed_at_combat_start", "first_combat");
        }

        PushKey(host, Key.F8);
        await Frames(host, 3);
        session.Check("f8_ignored_stays_visible", panel.ProbeForTest().Visible, Describe(panel));
        PushCtrlF9(host);
        await Frames(host, 3);
        session.Check("ctrl_f9_ignored_stays_visible", panel.ProbeForTest().Visible, Describe(panel));
        PushCtrlF9(host);
        await Frames(host, 3);
        session.Check("ctrl_f9_ignored_repeat", panel.ProbeForTest().Visible, Describe(panel));

        await Press(host, panel.CollapseButtonForTest);
        SolverOverlayProbe collapsed = panel.ProbeForTest();
        session.Check("collapse_persists",
            collapsed.Collapsed && SolverSettings.Current.Ui.Collapsed, Describe(panel));
        session.Check("collapsed_header_visible", collapsed.Visible, Describe(panel));
        bool switchClickable = await VisibleAndClickable(host, panel.AutoSearchSwitchForTest);
        session.Check("collapsed_auto_search_switch_clickable", switchClickable, Describe(panel));
        await Press(host, panel.CollapseButtonForTest);
        session.Check("expand_persists",
            !panel.ProbeForTest().Collapsed && !SolverSettings.Current.Ui.Collapsed, Describe(panel));

        bool originalAutoSearch = SolverSettings.Current.Automation.AutoSearch;
        await Toggle(host, panel.AutoSearchSwitchForTest, false);
        await WaitAutoSearchAsync(host, false, 30);
        session.Check("auto_search_off_saved",
            !SolverSettings.Current.Automation.AutoSearch && !CombatUiStore.Snapshot.AutoSearchEnabled,
            Describe(panel));
        await Toggle(host, panel.AutoSearchSwitchForTest, true);
        await WaitAutoSearchAsync(host, true, 30);
        session.Check("auto_search_on_saved",
            SolverSettings.Current.Automation.AutoSearch && CombatUiStore.Snapshot.AutoSearchEnabled,
            Describe(panel));
        if (SolverSettings.Current.Automation.AutoSearch != originalAutoSearch)
            CombatUiCommands.Instance.SetAutoSearch(originalAutoSearch);
        await WaitAutoSearchAsync(host, originalAutoSearch, 60);

        session.Check("header_buttons", HeaderButtons(panel, out string header), header);

        // 药水 / 成长 open the strategy sidebar on their page, switch it, and close it again.
        await Press(host, panel.PotionButtonForTest);
        SolverSidebar? sidebar = panel.SidebarForTest;
        session.Check("potion_opens_sidebar",
            SidebarOn(panel, SolverStrategyPageKind.Potions, SolverText.Get("药水策略")), Describe(panel));
        session.Check("sidebar_docked_beside_panel", SidebarDocked(panel, sidebar, out string geometry),
            geometry);
        session.Check("sidebar_dark_surface", DarkSurface(sidebar), "sidebar surface");
        await WaitAppliedAsync(host, panel);
        session.Check("potion_rows_match_snapshot",
            PotionRowsMatch(panel, CombatUiStore.Snapshot.Strategy, out string potionRows), potionRows);
        await Press(host, panel.GrowthButtonForTest);
        session.Check("growth_switches_page",
            SidebarOn(panel, SolverStrategyPageKind.Growth, SolverText.Get("成长策略")), Describe(panel));
        await WaitAppliedAsync(host, panel);
        session.Check("growth_rows_match_snapshot",
            GrowthRowsMatch(panel, CombatUiStore.Snapshot.Strategy, out string growthRows), growthRows);
        await Press(host, panel.GrowthButtonForTest);
        session.Check("pressed_page_closes_sidebar", SidebarClosed(panel), Describe(panel));
        await Press(host, panel.PotionButtonForTest);
        await Press(host, sidebar?.CloseButtonForTest);
        session.Check("close_button_closes_sidebar", SidebarClosed(panel), Describe(panel));
        await Press(host, panel.GrowthButtonForTest);
        await Press(host, panel.CollapseButtonForTest);
        session.Check("collapse_closes_sidebar",
            panel.ProbeForTest().Collapsed && SidebarClosed(panel) && HeaderButtonsHidden(panel),
            Describe(panel));
        await Press(host, panel.CollapseButtonForTest);
        session.Check("sidebar_stays_closed_after_expand",
            !panel.ProbeForTest().Collapsed && SidebarClosed(panel), Describe(panel));

        // 设置 replaces the panel body with the settings page and becomes 返回 without changing width.
        float settingsWidth = panel.SettingsButtonForTest?.Size.X ?? 0f;
        await Press(host, panel.PotionButtonForTest);
        await Press(host, panel.SettingsButtonForTest);
        SolverSettingsPage? page = panel.SettingsPageForTest;
        session.Check("settings_replaces_body", SettingsShown(panel) && SidebarClosed(panel),
            Describe(panel));
        float backWidth = panel.SettingsButtonForTest?.Size.X ?? -1f;
        session.Check("settings_button_width_fixed", Math.Abs(backWidth - settingsWidth) < 0.5f,
            $"settings={settingsWidth:F1} back={backWidth:F1}");
        session.Check("settings_no_light_theme", DarkSurface(page)
            && page?.FindChild("ThemeOption", recursive: true, owned: false) == null,
            "light surface or theme selector");
        session.Check("settings_controls_present", ControlsPresent(page, out string missing), missing);
        await WaitAppliedAsync(host, panel);
        session.Check("boss_options_match_snapshot",
            BossOptionsMatch(page, CombatUiStore.Snapshot.Strategy, out string boss), boss);
        (bool resetOk, string resetDetail) = await ResetKeepsModeAsync(host, page);
        session.Check("settings_reset_keeps_mode", resetOk, resetDetail);
        session.Check("settings_height", SettingsHeight(panel, page, out string height), height);
        await Press(host, panel.CollapseButtonForTest);
        session.Check("collapse_keeps_settings",
            panel.ProbeForTest().Collapsed && panel.SettingsOpenForTest
            && page is { Visible: false } && HeaderButtonsHidden(panel), Describe(panel));
        await Press(host, panel.CollapseButtonForTest);
        session.Check("expand_returns_to_settings",
            !panel.ProbeForTest().Collapsed && SettingsShown(panel), Describe(panel));
        await Press(host, panel.GrowthButtonForTest);
        session.Check("growth_from_settings", !panel.SettingsOpenForTest
            && panel.MainStackForTest is { Visible: true }
            && SidebarOn(panel, SolverStrategyPageKind.Growth, SolverText.Get("成长策略")), Describe(panel));
        await Press(host, panel.SettingsButtonForTest);
        await Press(host, panel.SettingsButtonForTest);
        session.Check("back_restores_body", !panel.SettingsOpenForTest
            && panel.MainStackForTest is { Visible: true } && page is { Visible: false }
            && SidebarClosed(panel) && panel.SettingsButtonForTest?.Text == SolverText.Get("设置"),
            Describe(panel));
        await WaitAppliedAsync(host, panel);
        StrategyUiSnapshot theft = CombatUiStore.Snapshot.Strategy;
        session.Check("theft_notice_matches_snapshot",
            panel.TheftNoticeForTest?.Visible == theft.TheftApplicable,
            $"applicable={theft.TheftApplicable} visible={panel.TheftNoticeForTest?.Visible}");
        // Left open through the locale rebuild and the combat; the next combat must start closed.
        await Press(host, panel.PotionButtonForTest);

        int chineseFontOverrides = panel.FontOverrideCountForTest;
        session.Check("chinese_font_substitution", chineseFontOverrides > 0,
            $"overrides={chineseFontOverrides}");

        string language = LocManager.Instance.Language;
        bool english = language != "eng";
        int buildsBefore = panel.ProbeForTest().BuildCount;
        if (english)
        {
            LocManager.Instance.StartOverridingLanguageAsEnglish();
            await Frames(host, 4);
            SolverOverlayProbe switched = panel.ProbeForTest();
            session.Check("locale_rebuilds_in_english",
                switched.Language == "eng" && switched.BuildCount == buildsBefore + 1, Describe(panel));
            session.Check("locale_rebuild_keeps_sidebar",
                SidebarOn(panel, SolverStrategyPageKind.Potions, SolverText.Get("药水策略")),
                Describe(panel));
            float englishSettings = panel.SettingsButtonForTest?.Size.X ?? 0f;
            await Press(host, panel.SettingsButtonForTest);
            float englishBack = panel.SettingsButtonForTest?.Size.X ?? -1f;
            session.Check("settings_button_width_fixed_english",
                SettingsShown(panel) && Math.Abs(englishBack - englishSettings) < 0.5f,
                $"settings={englishSettings:F1} back={englishBack:F1}");
            await Press(host, panel.SettingsButtonForTest);
            await Press(host, panel.PotionButtonForTest);
        }
        else
        {
            session.Skip("locale_rebuilds_in_english", "already_english");
            session.Skip("locale_rebuild_keeps_sidebar", "already_english");
            session.Skip("settings_button_width_fixed_english", "already_english");
        }
        if (english)
        {
            int englishFontOverrides = panel.FontOverrideCountForTest;
            session.Check("latin_fonts_resolved", englishFontOverrides == 0,
                $"overrides={englishFontOverrides}");
            int buildsEnglish = panel.ProbeForTest().BuildCount;
            LocManager.Instance.StopOverridingLanguageAsEnglish();
            await Frames(host, 4);
            SolverOverlayProbe restored = panel.ProbeForTest();
            session.Check("locale_restores", restored.Language == language
                && restored.BuildCount == buildsEnglish + 1, Describe(panel));
        }
        string[] missingKeys = SolverText.MissingKeysForTesting;
        session.Check("english_keys_present", missingKeys.Length == 0, string.Join("|", missingKeys));
        return session;
    }

    /// <summary>
    /// Drives the strategy controls through their buttons, switches and boxes (the panel_strategy
    /// controller scenario): every press must issue its command, the snapshot must follow, the page
    /// must show it, and nothing may start a search. The caller holds the combat in manual mode; the
    /// settings are restored afterwards.
    /// </summary>
    internal static async Task StrategyAsync(NGame host, CombatControllerChecks.Check check,
        CombatControllerChecks.Action action)
    {
        SolverOverlay panel = SolverOverlay.Instance ?? throw new InvalidDataException("panel missing");
        check("panel_visible", await WaitAsync(host, () => panel.ProbeForTest().Visible, 180),
            Describe(panel));
        SolverSettingsData before = SolverSettings.Current;
        int searches = CombatUiStore.Snapshot.Searches;
        try
        {
            if (CombatUiStore.Snapshot.Strategy.TheftApplicable)
                await TheftNoticeAsync(host, panel, check, action);
            else
                check("theft_notice_hidden", panel.TheftNoticeForTest is { Visible: false }, "visible");
            await PotionPageAsync(host, panel, check, action);
            await GrowthPageAsync(host, panel, check, action);
            await BossOptionsAsync(host, panel, check, action);
            CombatUiSnapshot after = CombatUiStore.Snapshot;
            check("strategy_ui_no_search", after.Searches == searches && !after.IsSearching,
                $"{searches}->{after.Searches}");
        }
        finally
        {
            SolverSettings.Update(_ => before);
        }
    }

    private static async Task TheftNoticeAsync(NGame host, SolverOverlay panel,
        CombatControllerChecks.Check check, CombatControllerChecks.Action action)
    {
        string keep = SolverText.Get("本场有盗贼：保住被偷的牌和金币");
        string escape = SolverText.Get("本场有盗贼：放走，不追回被偷的牌和金币");
        bool Shows(string text, string button)
            => panel.TheftNoticeForTest is { Visible: true }
                && panel.TheftNoticeLabelForTest?.Text == text
                && panel.TheftNoticeButtonForTest?.Text == button;
        check("theft_notice_default", CombatUiStore.Snapshot.Strategy.TheftPolicy == "preserve_resources"
            && Shows(keep, SolverText.Get("改为放走")), panel.TheftNoticeLabelForTest?.Text);
        await Press(host, panel.TheftNoticeButtonForTest);
        action("press:theft_notice");
        check("theft_notice_let_escape", CombatUiStore.Snapshot.Strategy.TheftPolicy == "let_escape"
            && Shows(escape, SolverText.Get("改为保住")), panel.TheftNoticeLabelForTest?.Text);
        await Press(host, panel.SettingsButtonForTest);
        check("theft_notice_hidden_in_settings", panel.TheftNoticeForTest is { Visible: false },
            Describe(panel));
        await Press(host, panel.SettingsButtonForTest);
        await Press(host, panel.CollapseButtonForTest);
        check("theft_notice_collapsed", panel.ProbeForTest().Collapsed
            && Shows(escape, SolverText.Get("改为保住")), Describe(panel));
        await Press(host, panel.CollapseButtonForTest);
        await Press(host, panel.TheftNoticeButtonForTest);
        action("press:theft_notice");
        check("theft_notice_preserve", CombatUiStore.Snapshot.Strategy.TheftPolicy == "preserve_resources"
            && Shows(keep, SolverText.Get("改为放走")), panel.TheftNoticeLabelForTest?.Text);
    }

    private static async Task PotionPageAsync(NGame host, SolverOverlay panel,
        CombatControllerChecks.Check check, CombatControllerChecks.Action action)
    {
        await Press(host, panel.PotionButtonForTest);
        SolverPotionStrategyPage page = panel.PotionPageForTest
            ?? throw new InvalidDataException("potion page missing");
        string saved = SolverText.Get("已保存，下次搜索生效");
        StrategyUiSnapshot strategy = CombatUiStore.Snapshot.Strategy;
        check("potion_rows", PotionRowsMatch(panel, strategy, out string rows), rows);
        int index = Array.FindIndex(strategy.Potions, static potion => potion.CanAssignPolicy);
        if (index < 0)
        {
            check("potion_presets_disabled", page.AllSmartButtonForTest.Disabled
                && page.AllProtectButtonForTest.Disabled && page.AllForceButtonForTest.Disabled, rows);
            await Press(host, panel.PotionButtonForTest);
            return;
        }
        PotionSlotUi target = strategy.Potions[index];
        Button button = page.RowsForTest[index].Button;
        string Mode() => CombatUiStore.Snapshot.Strategy.Potions
            .First(potion => potion.Slot == target.Slot).Mode;
        IEnumerable<PotionSlotUi> Assignable() => CombatUiStore.Snapshot.Strategy.Potions
            .Where(static potion => potion.CanAssignPolicy);
        async Task PressPreset(Button preset, string name, string expected)
        {
            await Press(host, preset);
            action("press:" + name);
            bool rowsMatch = PotionRowsMatch(panel, CombatUiStore.Snapshot.Strategy, out string detail);
            check("potion_" + name, rowsMatch
                && Assignable().All(potion => potion.Mode == expected)
                && page.StatusTextForTest == saved, detail);
        }
        await PressPreset(page.AllSmartButtonForTest, "all_smart", "smart");
        foreach (string expected in new[] { "disabled", "force", "smart" })
        {
            await Press(host, button);
            action($"press:potion_mode:{target.Slot}");
            bool rowsMatch = PotionRowsMatch(panel, CombatUiStore.Snapshot.Strategy, out string detail);
            check("potion_cycle_" + expected, rowsMatch && Mode() == expected
                && page.StatusTextForTest == saved, detail);
        }
        await PressPreset(page.AllForceButtonForTest, "all_force", "force");
        await PressPreset(page.AllProtectButtonForTest, "all_protect", "disabled");
        await PressPreset(page.AllSmartButtonForTest, "all_smart_again", "smart");
        await Press(host, panel.PotionButtonForTest);
    }

    private static async Task GrowthPageAsync(NGame host, SolverOverlay panel,
        CombatControllerChecks.Check check, CombatControllerChecks.Action action)
    {
        await Press(host, panel.GrowthButtonForTest);
        SolverGrowthStrategyPage page = panel.GrowthPageForTest
            ?? throw new InvalidDataException("growth page missing");
        string saved = SolverText.Get("已保存，下次搜索生效");
        StrategyUiSnapshot Strategy() => CombatUiStore.Snapshot.Strategy;
        void CheckRows(string name, bool condition)
        {
            bool rowsMatch = GrowthRowsMatch(panel, Strategy(), out string detail);
            check(name, rowsMatch && condition, detail + " status=" + page.StatusTextForTest);
        }
        CheckRows("growth_rows", true);
        async Task Toggle(CheckButton toggle, bool on, string name)
        {
            toggle.ButtonPressed = on;
            action($"toggle:{name}:{(on ? "on" : "off")}");
            await Frames(host, 3);
        }

        await Toggle(page.IgnoreToggleForTest, true, "ignore_long_term_rewards");
        CheckRows("growth_ignore_on", Strategy().IgnoreLongTermRewards
            && !page.BudgetInputForTest(GrowthSource.Feed).Editable && page.StatusTextForTest == saved);
        await Toggle(page.IgnoreToggleForTest, false, "ignore_long_term_rewards");
        CheckRows("growth_ignore_off", !Strategy().IgnoreLongTermRewards
            && page.BudgetInputForTest(GrowthSource.Feed).Editable);

        await Toggle(page.FlameLimitToggleForTest, true, "brightest_flame_limit");
        CheckRows("growth_flame_on", Strategy().BrightestFlameMaxHpLossLimit == 2
            && page.FlameLimitInputForTest.Editable);
        await Submit(host, page.FlameLimitInputForTest, "5");
        action("submit:brightest_flame_limit:5");
        CheckRows("growth_flame_value", Strategy().BrightestFlameMaxHpLossLimit == 5
            && page.StatusTextForTest == saved);
        page.FlameLimitInputForTest.ReleaseFocus();
        await Toggle(page.FlameLimitToggleForTest, false, "brightest_flame_limit");
        CheckRows("growth_flame_off", Strategy().BrightestFlameMaxHpLossLimit is null
            && !page.FlameLimitInputForTest.Editable);

        LineEdit feed = page.BudgetInputForTest(GrowthSource.Feed);
        await Submit(host, feed, "7");
        action("submit:growth:feed:7");
        feed.ReleaseFocus();
        CheckRows("growth_budget_value", Strategy().GrowthBudgets.Feed == 7
            && page.StatusTextForTest == saved);
        await Submit(host, feed, "abc");
        action("submit:growth:feed:abc");
        check("growth_budget_invalid", Strategy().GrowthBudgets.Feed == 7
            && page.StatusTextForTest == SolverText.Format($"请输入 {0}–{1000} 的整数"),
            page.StatusTextForTest);
        await Press(host, panel.SidebarForTest?.CloseButtonForTest);
        check("growth_invalid_keeps_sidebar",
            SidebarOn(panel, SolverStrategyPageKind.Growth, SolverText.Get("成长策略")), Describe(panel));
        await Submit(host, feed, "0");
        action("submit:growth:feed:0");
        feed.ReleaseFocus();
        await Press(host, panel.SidebarForTest?.CloseButtonForTest);
        check("growth_budget_reset", Strategy().GrowthBudgets.Feed == 0 && SidebarClosed(panel),
            Describe(panel));
    }

    private static async Task BossOptionsAsync(NGame host, SolverOverlay panel,
        CombatControllerChecks.Check check, CombatControllerChecks.Action action)
    {
        await Press(host, panel.SettingsButtonForTest);
        SolverSettingsPage page = panel.SettingsPageForTest
            ?? throw new InvalidDataException("settings page missing");
        StrategyUiSnapshot Strategy() => CombatUiStore.Snapshot.Strategy;
        await Select(host, page.ActBossOptionForTest, SearchBossHpStrategy.MinimizeHpLoss);
        action("select:act_boss_hp:minimize");
        await Select(host, page.FinalBossOptionForTest, SearchBossHpStrategy.MinimizeHpLoss);
        action("select:final_boss_hp:minimize");
        bool minimizeShown = BossOptionsMatch(page, Strategy(), out string minimize);
        check("boss_minimize", minimizeShown
            && Strategy().ActTransitionBossHpStrategy == SearchBossHpStrategy.MinimizeHpLoss
            && Strategy().FinalBossHpStrategy == SearchBossHpStrategy.MinimizeHpLoss
            && page.StatusTextForTest == SolverText.Get("已保存，下次搜索生效"),
            minimize + " status=" + page.StatusTextForTest);
        await Press(host, page.ResetButtonForTest);
        action("press:reset_defaults");
        bool resetShown = BossOptionsMatch(page, Strategy(), out string reset);
        check("boss_reset", resetShown
            && Strategy().ActTransitionBossHpStrategy == SearchBossHpStrategy.ProgressionFirst
            && Strategy().FinalBossHpStrategy == SearchBossHpStrategy.ProgressionFirst, reset);
        await Press(host, panel.SettingsButtonForTest);
    }

    internal static async Task FinishAsync(NGame host, Session session, string resultDirectory)
    {
        UiPanelObserver? observer = session.Observer;
        session.Stop();
        SolverOverlay? panel = SolverOverlay.Instance;
        if (observer != null)
        {
            session.Check("observed_frames", observer.ObservedFrames > 0,
                $"observed={observer.ObservedFrames} pending={observer.PendingFrames} hidden={observer.HiddenFrames}");
            session.Check("phase_matches_snapshot", observer.PhaseChecks > 0 && observer.PhaseMismatches == 0,
                $"checks={observer.PhaseChecks} mismatches={observer.PhaseMismatches}");
            session.Check("rows_match_snapshot_turns", observer.RowChecks > 0 && observer.RowMismatches == 0,
                $"checks={observer.RowChecks} mismatches={observer.RowMismatches}");
            session.Check("highlight_matches_current_action",
                observer.HighlightChecks > 0 && observer.HighlightMismatches == 0,
                $"checks={observer.HighlightChecks} active={observer.ActiveHighlights} " +
                $"mismatches={observer.HighlightMismatches}");
            session.Check("route_motion_stable_during_deploy", observer.MotionMismatches == 0,
                $"checks={observer.MotionChecks} mismatches={observer.MotionMismatches}");
            session.Check("panel_visible_during_combat", observer.HiddenFrames == 0,
                $"hidden={observer.HiddenFrames}");
            // A combat that pauses before its first deployment (e.g. the opening search predicts
            // death this turn) never shows the deploy state, so it has no deploy frames to check.
            session.Check("button_rules_frames",
                observer.SearchFrames > 0 && observer.IdleFrames > 0
                    && (observer.DeployFrames > 0 || observer.PausedBeforeDeploy),
                $"search={observer.SearchFrames} deploy={observer.DeployFrames} idle={observer.IdleFrames} " +
                $"paused_before_deploy={observer.PausedBeforeDeploy}");
            session.Check("button_rules_match", observer.ButtonMismatches == 0,
                $"mismatches={observer.ButtonMismatches}");
        }
        string? afterCombat = null;
        if (panel != null)
        {
            bool ended = await WaitAsync(host, static () => !CombatManager.Instance.IsInProgress, 300);
            if (ended)
            {
                bool hidden = await WaitAsync(host, () => !panel.ProbeForTest().Visible, 30);
                session.Check("panel_hidden_after_combat", hidden, Describe(panel));
                _combatEndedSinceLastBegin = hidden;
                afterCombat = hidden ? "hidden" : "visible";
            }
            else
            {
                afterCombat = "combat_in_progress";
                session.Skip("panel_hidden_after_combat", "combat_in_progress");
            }
        }
        UiPanelResult result = new(1, session.Failures.Count == 0 ? "ok" : "failed", afterCombat,
            [.. session.Checks], [.. session.Failures], observer?.Mismatches.ToArray() ?? [],
            observer?.ObservedFrames ?? 0, observer?.RowChecks ?? 0, observer?.HighlightChecks ?? 0,
            observer?.ActiveHighlights ?? 0, observer?.PhaseChecks ?? 0,
            observer?.SearchFrames ?? 0, observer?.DeployFrames ?? 0, observer?.IdleFrames ?? 0,
            observer?.ButtonMismatches ?? 0);
        string path = Path.Combine(resultDirectory, "ui-panel.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, OfflineJson.Options));
        File.Move(temporary, path, overwrite: true);
        ModLog.Info(
            $"[Sts2Solver] UI_PANEL_CHECKS status={result.Status} checks={session.Checks.Count} " +
            $"failed={session.Failures.Count} frames={result.ObservedFrames} rows={result.RowChecks} " +
            $"highlights={result.HighlightChecks}/{result.ActiveHighlights}");
        if (session.Failures.Count > 0)
            throw new InvalidDataException("ui_panel:" + string.Join(",", session.Failures));
    }

    private static bool SidebarDocked(SolverOverlay panel, SolverSidebar? sidebar, out string detail)
    {
        PanelContainer? box = panel.PanelForTest;
        if (sidebar == null || box == null)
        {
            detail = "sidebar or panel missing";
            return false;
        }
        Rect2 main = box.GetGlobalRect();
        Rect2 side = sidebar.GetGlobalRect();
        Vector2 viewport = sidebar.GetViewportRect().Size;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        float width = Math.Min(SolverSidebar.PreferredWidth, viewport.X - edge * 2f);
        float x = SolverOverlay.SidebarX(main.Position.X, main.Size.X, width, viewport.X, edge,
            SolverUiTokens.Spacing.Md);
        detail = $"panel={main} sidebar={side} viewport={viewport} expected_x={x:F1} expected_width={width:F1}";
        return Math.Abs(side.Position.X - x) < 0.5f
            && Math.Abs(side.Size.X - width) < 0.5f
            && Math.Abs(side.Position.Y - main.Position.Y) < 0.5f
            && Math.Abs(side.Size.Y - main.Size.Y) < 0.5f
            && (side.Position.X >= main.End.X || side.End.X <= main.Position.X);
    }

    private static bool HeaderButtons(SolverOverlay panel, out string detail)
    {
        Button?[] buttons =
        [
            panel.PotionButtonForTest, panel.GrowthButtonForTest, panel.SettingsButtonForTest,
            panel.CollapseButtonForTest,
        ];
        if (buttons.Any(static button => button == null))
        {
            detail = "header button missing";
            return false;
        }
        Node header = buttons[0]!.GetParent();
        int[] order = [.. buttons.Select(button => button!.GetParent() == header ? button.GetIndex() : -1)];
        detail = $"order={string.Join(",", order)} pressed=" +
            string.Join(",", buttons.Select(static button => button!.ButtonPressed)) +
            $" settings={panel.SettingsButtonForTest!.Text}";
        return order.Zip(order.Skip(1)).All(static pair => pair.First >= 0 && pair.Second == pair.First + 1)
            && order[3] == header.GetChildCount() - 1
            && buttons.All(static button => button!.Visible && !button.ButtonPressed)
            && panel.PotionButtonForTest!.Text == SolverText.Get("药水")
            && panel.GrowthButtonForTest!.Text == SolverText.Get("成长")
            && panel.SettingsButtonForTest!.Text == SolverText.Get("设置");
    }

    private static bool HeaderButtonsHidden(SolverOverlay panel)
        => panel.PotionButtonForTest is { Visible: false }
            && panel.GrowthButtonForTest is { Visible: false }
            && panel.SettingsButtonForTest is { Visible: false };

    /// <summary>The sidebar shows one page with its title, and only that page's header button is pressed.</summary>
    private static bool SidebarOn(SolverOverlay panel, SolverStrategyPageKind kind, string title)
    {
        SolverSidebar? sidebar = panel.SidebarForTest;
        SolverStrategyPageKind other = kind == SolverStrategyPageKind.Potions
            ? SolverStrategyPageKind.Growth
            : SolverStrategyPageKind.Potions;
        return panel.ProbeForTest().SidebarPage == kind.ToString()
            && !panel.SettingsOpenForTest
            && sidebar is { Visible: true }
            && sidebar.PageVisibleForTest(kind) && !sidebar.PageVisibleForTest(other)
            && sidebar.TitleForTest == title
            && panel.PotionButtonForTest?.ButtonPressed == (kind == SolverStrategyPageKind.Potions)
            && panel.GrowthButtonForTest?.ButtonPressed == (kind == SolverStrategyPageKind.Growth);
    }

    private static bool SidebarClosed(SolverOverlay panel)
        => panel.ProbeForTest().SidebarPage.Length == 0
            && panel.SidebarForTest is { Visible: false }
            && panel.PotionButtonForTest?.ButtonPressed == false
            && panel.GrowthButtonForTest?.ButtonPressed == false;

    /// <summary>The settings page replaces the summary, route and footer; the button reads 返回.</summary>
    private static bool SettingsShown(SolverOverlay panel)
        => panel.ProbeForTest().SettingsVisible
            && panel.SettingsPageForTest is { } page && page.IsVisibleInTree()
            && panel.MainStackForTest is { Visible: false }
            && panel.SettingsButtonForTest?.Text == SolverText.Get("返回");

    private static bool DarkSurface(Control? box)
        => box != null
            && box.GetThemeStylebox("panel") is StyleBoxFlat style
            && style.BgColor == SolverUiTokens.Palette.Surface;

    private static bool ControlsPresent(Control? page, out string missing)
    {
        string[] names =
        [
            "DeploymentSpeedOption", "InterActionDelayInput", "TierOption", "ThreadsInput",
            "MemoryInput", "ActBossHpOption", "FinalBossHpOption", "OpacitySlider",
        ];
        string[] absent = names
            .Where(name => page?.FindChild(name, recursive: true, owned: false) is not Control control
                || !control.IsVisibleInTree())
            .ToArray();
        missing = absent.Length == 0 ? "all present" : string.Join("|", absent);
        return absent.Length == 0;
    }

    private static bool BossOptionsMatch(SolverSettingsPage? page, StrategyUiSnapshot strategy,
        out string detail)
    {
        if (page == null)
        {
            detail = "page missing";
            return false;
        }
        OptionButton act = page.ActBossOptionForTest;
        OptionButton final = page.FinalBossOptionForTest;
        bool Items(OptionButton option)
            => option.ItemCount == 2
                && option.GetItemId(0) == (int)SearchBossHpStrategy.ProgressionFirst
                && option.GetItemText(0) == SolverText.Get("通关优先（默认）")
                && option.GetItemId(1) == (int)SearchBossHpStrategy.MinimizeHpLoss
                && option.GetItemText(1) == SolverText.Get("最低战损");
        detail = $"act={act.GetSelectedId()}/{(int)strategy.ActTransitionBossHpStrategy} " +
            $"final={final.GetSelectedId()}/{(int)strategy.FinalBossHpStrategy}";
        return Items(act) && Items(final)
            && act.GetSelectedId() == (int)strategy.ActTransitionBossHpStrategy
            && final.GetSelectedId() == (int)strategy.FinalBossHpStrategy;
    }

    /// <summary>The panel requests the page's whole content, at most 540 high.</summary>
    private static bool SettingsHeight(SolverOverlay panel, SolverSettingsPage? page, out string detail)
    {
        PanelContainer? box = panel.PanelForTest;
        if (box == null || page == null)
        {
            detail = "panel or page missing";
            return false;
        }
        float available = box.GetViewportRect().Size.Y - SolverUiTokens.Size.ResizeEdgeThickness * 2f;
        float requested = box.GetCombinedMinimumSize().Y + page.HiddenContentHeight;
        float expected = Math.Min(requested, Math.Min(SolverUiTokens.Size.SettingsMaxHeight, available));
        detail = $"height={box.Size.Y:F1} requested={requested:F1} expected={expected:F1}";
        return Math.Abs(box.Size.Y - expected) < 1f
            && box.Size.Y <= SolverUiTokens.Size.SettingsMaxHeight + 0.5f;
    }

    /// <summary>One row per potion in snapshot order, with the mode label and lock of the snapshot.</summary>
    private static bool PotionRowsMatch(SolverOverlay panel, StrategyUiSnapshot strategy, out string detail)
    {
        SolverPotionStrategyPage? page = panel.PotionPageForTest;
        if (page == null)
        {
            detail = "page missing";
            return false;
        }
        IReadOnlyList<(int Slot, string PotionId, bool CanAssign, Button Button)> rows = page.RowsForTest;
        PotionSlotUi[] potions = strategy.Potions;
        bool anyAssignable = potions.Any(static potion => potion.CanAssignPolicy);
        detail = $"rows={rows.Count} potions={potions.Length} " + string.Join(",", potions.Select(
            static potion => $"{potion.Slot}:{potion.PotionId}:{potion.Mode}:{potion.CanAssignPolicy}"));
        bool presets = page.AllSmartButtonForTest.Disabled == !anyAssignable
            && page.AllProtectButtonForTest.Disabled == !anyAssignable
            && page.AllForceButtonForTest.Disabled == !anyAssignable;
        if (potions.Length == 0)
        {
            return presets && rows.Count == 0
                && page.FindChild("NoPotions", recursive: true, owned: false) is Label { Visible: true } empty
                && empty.Text == SolverText.Get("当前没有药水");
        }
        if (!presets || rows.Count != potions.Length)
            return false;
        for (int index = 0; index < potions.Length; index++)
        {
            PotionSlotUi potion = potions[index];
            (int slot, string potionId, bool canAssign, Button button) = rows[index];
            string tooltip = !potion.CanAssignPolicy
                ? SolverText.Get("该药水不可指定使用策略")
                : potion.Mode == "smart" ? SolverText.Get("只在能省下足够的血、或不用就赢不了时使用。") : "";
            if (slot != potion.Slot || potionId != potion.PotionId || canAssign != potion.CanAssignPolicy
                || button.Disabled != !potion.CanAssignPolicy || button.Text != ModeText(potion.Mode)
                || button.TooltipText != tooltip)
            {
                detail += $" mismatch@{index} text={button.Text} disabled={button.Disabled}";
                return false;
            }
        }
        return true;
    }

    private static bool GrowthRowsMatch(SolverOverlay panel, StrategyUiSnapshot strategy, out string detail)
    {
        SolverGrowthStrategyPage? page = panel.GrowthPageForTest;
        if (page == null)
        {
            detail = "page missing";
            return false;
        }
        int? limit = strategy.BrightestFlameMaxHpLossLimit;
        LineEdit flame = page.FlameLimitInputForTest;
        detail = $"ignore={page.IgnoreToggleForTest.ButtonPressed}/{strategy.IgnoreLongTermRewards} " +
            $"flame={page.FlameLimitToggleForTest.ButtonPressed}:{flame.Text}:{flame.Editable}/" +
            $"{limit?.ToString(CultureInfo.InvariantCulture) ?? "null"}";
        bool ok = page.IgnoreToggleForTest.ButtonPressed == strategy.IgnoreLongTermRewards
            && page.FlameLimitToggleForTest.ButtonPressed == limit.HasValue
            && flame.Text == (limit ?? 2).ToString(CultureInfo.InvariantCulture)
            && flame.Editable == limit.HasValue;
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
        {
            LineEdit input = page.BudgetInputForTest(source);
            int value = Budget(strategy.GrowthBudgets, source);
            if (input.Text != value.ToString(CultureInfo.InvariantCulture)
                || input.Editable == strategy.IgnoreLongTermRewards)
            {
                detail += $" {source}={input.Text}:{input.Editable}/{value}";
                ok = false;
            }
        }
        return ok;
    }

    private static int Budget(SearchGrowthBudget budgets, GrowthSource source) => source switch
    {
        GrowthSource.HandOfGreed => budgets.HandOfGreed,
        GrowthSource.TheHunt => budgets.TheHunt,
        GrowthSource.Feed => budgets.Feed,
        GrowthSource.Royalties => budgets.Royalties,
        GrowthSource.Alchemize => budgets.Alchemize,
        GrowthSource.GeneticAlgorithm => budgets.GeneticAlgorithm,
        GrowthSource.TheScythe => budgets.TheScythe,
        GrowthSource.Goopy => budgets.Goopy,
        GrowthSource.ForbiddenGrimoire => budgets.ForbiddenGrimoire,
        GrowthSource.MadScience => budgets.MadScience,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static string ModeText(string mode) => mode switch
    {
        "smart" => "—  " + SolverText.Get("智能使用"),
        "disabled" => "✕  " + SolverText.Get("禁用 / 保护"),
        "force" => "✓  " + SolverText.Get("强制使用"),
        _ => throw new InvalidDataException("unknown potion mode " + mode),
    };

    /// <summary>Waits until the panel has applied the newest snapshot, so a comparison reads one frame.</summary>
    private static async Task WaitAppliedAsync(NGame host, SolverOverlay panel)
    {
        if (!await WaitAsync(host, () => panel.ProbeForTest().AppliedSequence == CombatUiStore.Snapshot.Sequence, 60))
            throw new TimeoutException("panel did not apply the newest snapshot");
    }

    /// <summary>
    /// Changes the non-search fields on this page, presses 恢复默认 and expects the page defaults
    /// with the mode untouched. Search fields are left at their run values so a search started in
    /// these frames is unchanged; the previous settings are restored afterwards.
    /// </summary>
    private static async Task<(bool Ok, string Detail)> ResetKeepsModeAsync(NGame host,
        SolverSettingsPage? page)
    {
        if (page == null)
            return (false, "page missing");
        SolverSettingsData before = SolverSettings.Current;
        bool autoSearch = before.Automation.AutoSearch;
        SolverSettings.Update(current => current with
        {
            Automation = current.Automation with
            {
                DeploymentSpeed = SolverDeploymentSpeed.Fast,
                InterActionDelaySeconds = 0.5d,
            },
            Ui = current.Ui with { Opacity = 0.5f, ShowBattleDamagePerformanceHint = false },
        });
        page.Opened();
        await Press(host, page.ResetButtonForTest);
        SolverSettingsData after = SolverSettings.Current;
        SolverSettingsData defaults = new();
        bool ok = after.Automation.AutoSearch == autoSearch
            && after.Search.ActTransitionBossHpStrategy == SearchBossHpStrategy.ProgressionFirst
            && after.Search.FinalBossHpStrategy == SearchBossHpStrategy.ProgressionFirst
            && after.Automation.DeploymentSpeed == defaults.Automation.DeploymentSpeed
            && after.Automation.InterActionDelaySeconds.Equals(defaults.Automation.InterActionDelaySeconds)
            && after.Search.Tier == defaults.Search.Tier
            && after.Search.Threads == defaults.Search.Threads
            && after.Search.MemoryBudgetBytes == defaults.Search.MemoryBudgetBytes
            && Math.Abs(after.Ui.Opacity - defaults.Ui.Opacity) < 0.0001f
            && after.Ui.ShowBattleDamagePerformanceHint
            && after.Ui.PanelX is null && after.Ui.PanelWidth is null;
        string detail = $"auto_search={after.Automation.AutoSearch}/{autoSearch} speed={after.Automation.DeploymentSpeed} " +
            $"boss={after.Search.ActTransitionBossHpStrategy}/{after.Search.FinalBossHpStrategy} " +
            $"delay={after.Automation.InterActionDelaySeconds} tier={after.Search.Tier} " +
            $"threads={after.Search.Threads?.ToString(CultureInfo.InvariantCulture) ?? "null"} " +
            $"memory={after.Search.MemoryBudgetBytes?.ToString(CultureInfo.InvariantCulture) ?? "null"} " +
            $"opacity={after.Ui.Opacity} hint={after.Ui.ShowBattleDamagePerformanceHint} " +
            $"status={page.StatusTextForTest}";
        SolverSettings.Update(_ => before);
        page.Opened();
        return (ok, detail);
    }

    private static async Task<bool> VisibleAndClickable(NGame host, Button? button)
    {
        if (button == null)
            return false;
        await Frames(host, 2);
        return button.IsVisibleInTree() && !button.Disabled;
    }

    private static async Task Press(NGame host, Button? button)
    {
        if (button == null)
            throw new InvalidDataException("panel_button_missing");
        button.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(host, 3);
    }

    private static async Task Submit(NGame host, LineEdit input, string text)
    {
        input.GrabFocus();
        input.Text = text;
        input.EmitSignal(LineEdit.SignalName.TextSubmitted, text);
        await Frames(host, 3);
    }

    private static async Task Select(NGame host, OptionButton option, SearchBossHpStrategy strategy)
    {
        int index = option.GetItemIndex((int)strategy);
        option.Select(index);
        option.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
        await Frames(host, 3);
    }

    private static void PushCtrlF9(NGame host)
    {
        PushKey(host, Key.F9, ctrl: true);
    }

    private static void PushKey(NGame host, Key key, bool ctrl = false)
    {
        Viewport viewport = host.GetViewport();
        viewport.PushInput(new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key,
            CtrlPressed = ctrl,
            Pressed = true,
        });
        viewport.PushInput(new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key,
            CtrlPressed = ctrl,
            Pressed = false,
        });
    }

    private static async Task WaitAutoSearchAsync(NGame host, bool enabled, int frames)
    {
        await WaitAsync(host, _ => SolverSettings.Current.Automation.AutoSearch == enabled, frames,
            "auto search switch");
    }

    private static async Task Toggle(NGame host, CheckButton? toggle, bool pressed)
    {
        if (toggle == null)
            throw new InvalidDataException("panel_switch_missing");
        toggle.ButtonPressed = pressed;
        await Frames(host, 3);
    }

    private static async Task<bool> WaitAsync(NGame host, Func<bool> condition, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            if (condition())
                return true;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        return condition();
    }

    private static async Task<CombatUiSnapshot> WaitAsync(NGame host,
        Func<CombatUiSnapshot, bool> predicate, int frames, string description)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            CombatUiSnapshot snapshot = CombatUiStore.Snapshot;
            if (predicate(snapshot))
                return snapshot;
            await CombatFrameWaiter.NextFrameAsync(host);
        }
        throw new TimeoutException(description);
    }

    private static async Task Frames(NGame host, int count)
    {
        for (int frame = 0; frame < count; frame++)
            await CombatFrameWaiter.NextFrameAsync(host);
    }

    private static string Describe(SolverOverlay panel)
    {
        SolverOverlayProbe probe = panel.ProbeForTest();
        return $"visible={probe.Visible} hidden_by_shortcut={probe.HiddenByShortcut} " +
            $"collapsed={probe.Collapsed} settings={probe.SettingsVisible} sidebar={probe.SidebarPage} " +
            $"language={probe.Language} builds={probe.BuildCount}";
    }

    internal sealed class Session
    {
        internal List<UiPanelCheck> Checks { get; } = [];

        internal List<string> Failures { get; } = [];

        internal UiPanelObserver? Observer { get; set; }

        internal void Check(string name, bool ok, string? detail)
        {
            Checks.Add(new UiPanelCheck(name, ok ? "ok" : "failed", detail));
            if (!ok)
                Failures.Add(detail == null ? name : $"{name}[{detail}]");
        }

        internal void Skip(string name, string reason)
            => Checks.Add(new UiPanelCheck(name, "skipped", reason));

        internal void Stop()
        {
            if (Observer != null && GodotObject.IsInstanceValid(Observer))
            {
                Observer.GetParent()?.RemoveChild(Observer);
                Observer.QueueFree();
            }
            Observer = null;
        }
    }

    internal sealed record UiPanelCheck(string Name, string Status, string? Detail);

    internal sealed record UiPanelResult(int SchemaVersion, string Status, string? AfterCombat,
        UiPanelCheck[] Checks, string[] Failures, string[] Mismatches, int ObservedFrames, int RowChecks,
        int HighlightChecks, int ActiveHighlights, int PhaseChecks, int SearchFrames, int DeployFrames,
        int IdleFrames, int ButtonMismatches);
}

/// <summary>
/// Per-frame comparison of the panel with the snapshot it last applied. Frames where the panel has
/// not caught up with the newest snapshot are counted as pending, not compared.
/// </summary>
internal sealed partial class UiPanelObserver : Node
{
    private const int MaximumMismatchRecords = 8;

    private readonly SolverOverlay? _panel;

    internal int ObservedFrames { get; private set; }

    internal int PendingFrames { get; private set; }

    internal int HiddenFrames { get; private set; }

    internal int PhaseChecks { get; private set; }

    internal int PhaseMismatches { get; private set; }

    internal int RowChecks { get; private set; }

    internal int RowMismatches { get; private set; }

    internal int HighlightChecks { get; private set; }

    internal int ActiveHighlights { get; private set; }

    internal int HighlightMismatches { get; private set; }

    internal int MotionChecks { get; private set; }

    internal int MotionMismatches { get; private set; }

    private int _motionTurn = -1;
    private (int Restarts, int Rebuilds)[] _motionBaseline = [];

    internal int SearchFrames { get; private set; }

    internal int DeployFrames { get; private set; }

    internal int IdleFrames { get; private set; }

    /// <summary>The controller paused before any deploy frame was observed.</summary>
    internal bool PausedBeforeDeploy { get; private set; }

    internal int ButtonMismatches { get; private set; }

    internal List<string> Mismatches { get; } = [];

    public UiPanelObserver()
    {
    }

    public UiPanelObserver(SolverOverlay panel)
    {
        _panel = panel;
    }

    public override void _Process(double delta)
    {
        if (_panel == null || !IsInstanceValid(_panel) || !CombatManager.Instance.IsInProgress)
            return;
        CombatUiSnapshot snapshot = CombatUiStore.Snapshot;
        SolverOverlayProbe probe = _panel.ProbeForTest();
        if (!probe.Visible)
        {
            HiddenFrames++;
            return;
        }
        if (probe.AppliedSequence != snapshot.Sequence)
        {
            PendingFrames++;
            return;
        }
        ObservedFrames++;
        PhaseChecks++;
        if (probe.PhaseKey != snapshot.Phase.ToString())
        {
            PhaseMismatches++;
            Record($"phase seq={snapshot.Sequence} panel={probe.PhaseKey} snapshot={snapshot.Phase}");
        }
        if (snapshot.IsSearching)
            SearchFrames++;
        else if (snapshot.IsDeploying)
            DeployFrames++;
        else
        {
            IdleFrames++;
            if (DeployFrames == 0 && snapshot.Phase == CombatUiPhase.Paused)
                PausedBeforeDeploy = true;
        }
        CheckButtons(snapshot);
        CheckRows(snapshot, probe);
        CheckDeploymentMotion(snapshot, probe);
    }

    /// <summary>
    /// Within one deployment turn the panel refreshes on every snapshot. A refresh must neither snap
    /// surviving pills back to white (the completed pills flash and the active pill replays its
    /// activation flash) nor recreate them; both counters may only move when the turn changes.
    /// </summary>
    private void CheckDeploymentMotion(CombatUiSnapshot snapshot, SolverOverlayProbe probe)
    {
        if (snapshot.Phase != CombatUiPhase.Deploying
            || probe.Presentation != nameof(SolverOverlayPresentation.Deploying))
        {
            _motionTurn = -1;
            return;
        }
        (int Restarts, int Rebuilds)[] counts =
            [.. probe.Rows.Select(static row => (row.MotionRestarts, row.Rebuilds))];
        if (_motionTurn != snapshot.Turn || counts.Length != _motionBaseline.Length)
        {
            _motionTurn = snapshot.Turn;
            _motionBaseline = counts;
            return;
        }
        MotionChecks++;
        if (counts.SequenceEqual(_motionBaseline))
            return;
        MotionMismatches++;
        Record($"motion seq={snapshot.Sequence} turn={snapshot.Turn} before=" +
            string.Join(",", _motionBaseline.Select(static c => $"{c.Restarts}/{c.Rebuilds}")) +
            " after=" + string.Join(",", counts.Select(static c => $"{c.Restarts}/{c.Rebuilds}")));
        _motionBaseline = counts;
    }

    /// <summary>
    /// The footer follows the turn's search stage and the execution, never the search result:
    /// executing shows only 停止执行 and greys the search keys; a plain search shows 停止搜索 and no
    /// execute key; otherwise 开始搜索 before the turn's first search, 继续搜索 + 重新搜索 after it, and
    /// both execute keys in place (greyed without a current-turn route).
    /// </summary>
    private void CheckButtons(CombatUiSnapshot snapshot)
    {
        SolverOverlay panel = _panel!;
        bool executing = snapshot.IsDeploying || snapshot.TurnSetupDriving || snapshot.CombatRunActive
            || snapshot.IsSearching && snapshot.DeployWhenReady;
        bool plainSearch = snapshot.IsSearching && !executing;
        bool notStarted = snapshot.SearchStage == TurnSearchStage.NotStarted;
        bool stoppingSearch = snapshot.Search?.Phase == SearchUiPhase.Stopping;
        string leftText = plainSearch ? SolverText.Get("停止搜索")
            : notStarted ? SolverText.Get("开始搜索")
            : SolverText.Get("继续搜索");
        bool leftDisabled = executing
            || (plainSearch ? stoppingSearch : notStarted ? !snapshot.CanStartSearch : !snapshot.CanContinueSearch);
        bool restartVisible = !plainSearch && !notStarted;
        bool executeVisible = !executing && !snapshot.IsSearching;
        bool ok = true;
        string? detail = null;
        void Expect(string name, bool condition)
        {
            if (condition)
                return;
            ok = false;
            detail ??= name;
        }
        Expect("search_visible", panel.SearchButtonForTest?.Visible == true);
        Expect("search_text", panel.SearchButtonForTest?.Text == leftText);
        Expect("search_disabled", panel.SearchButtonForTest?.Disabled == leftDisabled);
        if (!executing && !plainSearch && leftDisabled && !snapshot.CanStartSearch)
        {
            Expect("search_tooltip", panel.SearchButtonForTest?.TooltipText
                == BlockText(snapshot.SearchBlockReason));
        }
        Expect("restart_visible", panel.RestartSearchButtonForTest?.Visible == restartVisible);
        if (restartVisible)
        {
            Expect("restart_disabled",
                panel.RestartSearchButtonForTest?.Disabled == (executing || !snapshot.CanStartSearch));
        }
        Expect("execute_turn_visible", panel.ExecuteTurnButtonForTest?.Visible == executeVisible);
        Expect("execute_combat_visible", panel.ExecuteCombatButtonForTest?.Visible == executeVisible);
        if (executeVisible)
        {
            Expect("execute_turn_disabled",
                panel.ExecuteTurnButtonForTest?.Disabled == !snapshot.CurrentTurnRouteReady);
            Expect("execute_combat_disabled",
                panel.ExecuteCombatButtonForTest?.Disabled == !snapshot.CurrentTurnRouteReady);
            Expect("execute_turn_text", panel.ExecuteTurnButtonForTest?.Text == SolverText.Get("执行本回合"));
            Expect("execute_combat_text",
                panel.ExecuteCombatButtonForTest?.Text == SolverText.Get("执行本场战斗"));
        }
        Expect("stop_visible", panel.StopExecutionButtonForTest?.Visible == executing);
        if (executing)
        {
            Expect("stop_text", panel.StopExecutionButtonForTest?.Text == SolverText.Get("停止执行"));
            Expect("stop_disabled",
                panel.StopExecutionButtonForTest?.Disabled == snapshot.IsStoppingDeployment);
        }
        Expect("auto_search_switch",
            panel.AutoSearchSwitchForTest?.ButtonPressed == snapshot.AutoSearchEnabled
            && panel.AutoSearchSwitchForTest.Visible);
        Expect("memory_collapsed",
            panel.MemoryRowVisibleForTest != panel.ProbeForTest().Collapsed);
        if (!ok)
        {
            ButtonMismatches++;
            Record($"buttons seq={snapshot.Sequence} phase={snapshot.Phase} detail={detail} " +
                $"stage={snapshot.SearchStage} searching={snapshot.IsSearching} executing={executing} " +
                $"run={snapshot.CombatRunActive} canStart={snapshot.CanStartSearch} " +
                $"canContinue={snapshot.CanContinueSearch} routeReady={snapshot.CurrentTurnRouteReady} " +
                $"block={snapshot.SearchBlockReason ?? "-"}");
        }
    }

    private static string BlockText(string? code) => code switch
    {
        "enemy_turn" => SolverText.Get("敌方回合，轮到你时再搜索。"),
        "actions_running" => SolverText.Get("动作结算中（含选牌界面），结算完再搜索。"),
        "turn_start" => SolverText.Get("回合开始结算中，完成开局选择后再搜索。"),
        "executing" => SolverText.Get("正在执行本回合；要重新搜索请先停止执行。"),
        "searching" => SolverText.Get("搜索仍在收尾，请稍候。"),
        "multiplayer" => SolverText.Get("多人游戏中不能搜索。"),
        "not_playable" => SolverText.Get("当前战斗状态不能搜索。"),
        "not_in_combat" => SolverText.Get("当前不在战斗中。"),
        _ => SolverText.Get("当前状态不能搜索。"),
    };

    private void CheckRows(CombatUiSnapshot snapshot, SolverOverlayProbe probe)
    {
        if (!CombatManager.Instance.IsInProgress)
            return;
        RouteUiSnapshot? route = snapshot.Route;
        RowChecks++;
        // A finished search keeps its last preview in the snapshot; the panel shows that preview only
        // while searching (after an execution it keeps the executed route instead).
        if (route == null && snapshot.Phase == CombatUiPhase.Searching
            && snapshot.Search?.RoutePreview is { Turns.Length: > 0 } preview)
        {
            CheckPreviewRows(snapshot, preview, probe);
            return;
        }
        if (route == null || route.Turns.Length == 0)
        {
            // The terminal snapshot is published before the panel's next frame hides the layer;
            // that single cleanup frame has no route to compare.
            if (snapshot.Phase is CombatUiPhase.Idle or CombatUiPhase.Completed
                && probe.Rows.Length == 0)
            {
                return;
            }
            if (snapshot.Phase == CombatUiPhase.Searching && probe.Rows.Length <= 1)
                return;
            // After an execution the panel keeps the executed route until the next turn's result
            // arrives (a forced end turn such as Void Form leaves a visible gap before the reuse).
            if (snapshot.Phase == CombatUiPhase.WaitingTurn
                && probe.Presentation == nameof(SolverOverlayPresentation.ExecutedHistory))
            {
                return;
            }
            if (probe.Rows.Length != 0
                && snapshot.Phase is not (CombatUiPhase.Idle or CombatUiPhase.Completed))
            {
                RowMismatches++;
                Record($"rows seq={snapshot.Sequence} phase={snapshot.Phase} panel={probe.Rows.Length} snapshot=none");
            }
            return;
        }
        int length = route.Turns.Length;
        int count = Math.Min(length, 16);
        int current = Array.FindIndex(route.Turns, turn => turn.Turn == route.CurrentTurn);
        int start = current < 0 ? 0 : Math.Clamp(current - 1, 0, length - count);
        if (probe.Rows.Length != count)
        {
            RowMismatches++;
            Record($"rows seq={snapshot.Sequence} panel={probe.Rows.Length} snapshot={count}/{length}");
            return;
        }
        for (int index = 0; index < count; index++)
        {
            RouteTurnUi turn = route.Turns[start + index];
            SolverRowProbe row = probe.Rows[index];
            bool hasEnd = turn.Actions.Length > 0 && turn.Actions[^1].EndsTurn;
            int displayed = hasEnd ? turn.Actions.Length - 1 : turn.Actions.Length;
            int covered = row.Pills.Where(static pill => pill.Kind == "action" && pill.Offset == 0)
                .Sum(static pill => pill.Period * pill.Repetitions);
            if (row.Turn != turn.Turn || row.StartActionIndex != turn.StartActionIndex
                || row.DisplayedCount != displayed || covered != displayed
                || row.EndTurnIndex != (hasEnd ? turn.Actions[^1].Index : -1))
            {
                RowMismatches++;
                Record($"row seq={snapshot.Sequence} slot={index} panel=({row.Turn},{row.StartActionIndex}," +
                    $"{row.DisplayedCount},{covered},{row.EndTurnIndex}) snapshot=({turn.Turn}," +
                    $"{turn.StartActionIndex},{displayed},{(hasEnd ? turn.Actions[^1].Index : -1)})");
                return;
            }
        }
        CheckHighlight(snapshot, route, probe, start, count);
    }

    private void CheckPreviewRows(CombatUiSnapshot snapshot, SearchRoutePreviewUi preview,
        SolverOverlayProbe probe)
    {
        int length = preview.Turns.Length;
        int count = Math.Min(length, 16);
        int current = Array.FindIndex(preview.Turns, turn => turn.Turn == snapshot.Turn);
        int start = current < 0 ? 0 : Math.Clamp(current - 1, 0, length - count);
        if (probe.Rows.Length != count)
        {
            RowMismatches++;
            Record($"preview rows seq={snapshot.Sequence} panel={probe.Rows.Length} " +
                $"snapshot={count}/{length} presentation={probe.Presentation} " +
                $"overlay={_panel?.LastOverlayTurnCountForTest} phase={snapshot.Phase} " +
                $"route={snapshot.Route?.Turns.Length.ToString() ?? "-"} applied={probe.AppliedSequence}");
            return;
        }
        for (int index = 0; index < count; index++)
        {
            SearchTurnPreviewUi turn = preview.Turns[start + index];
            SolverRowProbe row = probe.Rows[index];
            int displayed = turn.Actions.Count(static action => action.Kind != "end_turn");
            if (row.Turn != turn.Turn || row.DisplayedCount != displayed)
            {
                RowMismatches++;
                Record($"preview row seq={snapshot.Sequence} slot={index} " +
                    $"panel=({row.Turn},{row.DisplayedCount}) snapshot=({turn.Turn},{displayed})");
                return;
            }
        }
    }

    private void CheckHighlight(CombatUiSnapshot snapshot, RouteUiSnapshot route, SolverOverlayProbe probe,
        int start, int count)
    {
        HighlightChecks++;
        // CombatSolver calls ShowDeploymentStep with the next action title only when the action
        // starts; after it completes, the call passes a null title and SolverRouteRow clears the
        // active pill even though CurrentActionIndex and CompletedActions are both the next
        // index. ActionRunning is the explicit running/not-running discriminator, so a boundary
        // frame must have no lit pill while a running frame must have exactly one.
        int? active = snapshot.Phase == CombatUiPhase.Deploying && route.ActionRunning
            && route.CurrentActionIndex >= route.CompletedActions
            && route.CurrentActionIndex < route.ActionCount
                ? route.CurrentActionIndex
                : null;
        string? expectedKind = null;
        int expectedRow = -1;
        if (active is { } index)
        {
            for (int slot = 0; slot < count; slot++)
            {
                RouteTurnUi turn = route.Turns[start + slot];
                if (index < turn.StartActionIndex || index >= turn.StartActionIndex + turn.Actions.Length)
                    continue;
                SolverRowProbe row = probe.Rows[slot];
                if (index < turn.StartActionIndex + row.DisplayedCount)
                    expectedKind = "action";
                else if (row.EndTurnShown && index == row.EndTurnIndex)
                    expectedKind = "end_turn";
                expectedRow = slot;
            }
        }
        List<(int Row, SolverPillProbe Pill)> lit = [];
        for (int slot = 0; slot < probe.Rows.Length; slot++)
        {
            foreach (SolverPillProbe pill in probe.Rows[slot].Pills)
            {
                if (pill.State == "active")
                    lit.Add((slot, pill));
            }
        }
        bool ok;
        if (expectedKind == null)
        {
            ok = lit.Count == 0;
        }
        else
        {
            ActiveHighlights++;
            ok = lit.Count == 1 && lit[0].Row == expectedRow && lit[0].Pill.Kind == expectedKind;
            if (ok && expectedKind == "action")
            {
                SolverPillProbe pill = lit[0].Pill;
                int local = active!.Value - probe.Rows[expectedRow].StartActionIndex;
                ok = local >= pill.RunStart && local < pill.RunStart + pill.Period * pill.Repetitions
                    && (local - pill.RunStart) % pill.Period == pill.Offset;
            }
        }
        for (int slot = 0; slot < probe.Rows.Length && ok; slot++)
        {
            SolverRowProbe row = probe.Rows[slot];
            int completedLocal = Math.Clamp(route.CompletedActions - row.StartActionIndex, 0, row.DisplayedCount);
            foreach (SolverPillProbe pill in row.Pills)
            {
                if (pill.Kind != "action" || pill.State == "active")
                    continue;
                bool completed = pill.RunStart + (pill.Repetitions - 1) * pill.Period + pill.Offset < completedLocal;
                if (completed != (pill.State == "completed"))
                {
                    ok = false;
                    break;
                }
            }
        }
        if (!ok)
        {
            HighlightMismatches++;
            Record($"highlight seq={snapshot.Sequence} phase={snapshot.Phase} current={route.CurrentActionIndex} " +
                $"completed={route.CompletedActions} expected={expectedKind ?? "none"}@{expectedRow} lit={lit.Count} " +
                "rows=" + string.Join(";", probe.Rows.Select(row =>
                    $"{row.Turn}[s={row.StartActionIndex},n={row.DisplayedCount},e={row.EndTurnIndex}]:" +
                    string.Join(",", row.Pills.Select(pill =>
                        $"{pill.Kind}/{pill.RunStart}+{pill.Offset}x{pill.Repetitions}={pill.State}")))) +
                " turns=" + string.Join(";", route.Turns.Select(turn =>
                    $"{turn.Turn}[s={turn.StartActionIndex},n={turn.Actions.Length}]")));
        }
    }

    private void Record(string mismatch)
    {
        if (Mismatches.Count < MaxMismatchRecords)
            Mismatches.Add(mismatch);
    }

    private const int MaxMismatchRecords = 8;
}
