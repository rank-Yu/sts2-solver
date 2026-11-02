using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
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
        session.Check("collapsed_solver_toggle_clickable",
            await VisibleAndClickable(host, panel.SolverEnabledButtonForTest), Describe(panel));
        session.Check("collapsed_mode_button_clickable",
            await VisibleAndClickable(host, panel.ModeButtonForTest), Describe(panel));
        await Press(host, panel.CollapseButtonForTest);
        session.Check("expand_persists",
            !panel.ProbeForTest().Collapsed && !SolverSettings.Current.Ui.Collapsed, Describe(panel));

        SolverMode originalMode = SolverSettings.Current.Automation.Mode;
        await Press(host, panel.ModeButtonForTest);
        await Frames(host, 2);
        session.Check("mode_button_cycles",
            SolverSettings.Current.Automation.Mode != originalMode, Describe(panel));
        for (int attempt = 0; attempt < 3 && SolverSettings.Current.Automation.Mode != originalMode;
             attempt++)
        {
            await Press(host, panel.ModeButtonForTest);
        }
        await WaitModeAsync(host, originalMode, 30);

        await Press(host, panel.SolverEnabledButtonForTest);
        await WaitAsync(host, static s => s.SolverDisabled, 60, "solver disabled by panel");
        session.Check("solver_toggle_off", CombatUiStore.Snapshot.SolverDisabled, Describe(panel));
        await Press(host, panel.SolverEnabledButtonForTest);
        await WaitAsync(host, static s => !s.SolverDisabled, 60, "solver enabled by panel");
        session.Check("solver_toggle_on", !CombatUiStore.Snapshot.SolverDisabled, Describe(panel));
        if (SolverSettings.Current.Automation.Mode != originalMode)
            CombatUiCommands.Instance.SetMode(originalMode);
        await WaitModeAsync(host, originalMode, 60);

        bool detailsOpened = false;
        for (int attempt = 0; attempt < 3 && !detailsOpened; attempt++)
        {
            if (!panel.ProbeForTest().DetailsVisible)
                await Press(host, panel.DetailsButtonForTest);
            await Frames(host, 2);
            detailsOpened = panel.ProbeForTest().DetailsVisible;
        }
        session.Check("details_opens", detailsOpened, Describe(panel));
        bool detailsClosed = false;
        for (int attempt = 0; attempt < 3 && !detailsClosed; attempt++)
        {
            if (panel.ProbeForTest().DetailsVisible)
                await Press(host, panel.DetailsButtonForTest);
            await Frames(host, 2);
            detailsClosed = !panel.ProbeForTest().DetailsVisible;
        }
        session.Check("details_closes", detailsClosed, Describe(panel));

        await Press(host, panel.SettingsButtonForTest);
        session.Check("settings_opens", panel.ProbeForTest().SettingsVisible, Describe(panel));
        session.Check("settings_no_light_theme",
            panel.FindChild("ThemeOption", recursive: true, owned: false) == null,
            "theme selector present");
        await Press(host, panel.SettingsButtonForTest);
        session.Check("settings_closes", !panel.ProbeForTest().SettingsVisible, Describe(panel));

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
        }
        else
        {
            session.Skip("locale_rebuilds_in_english", "already_english");
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
            session.Check("panel_visible_during_combat", observer.HiddenFrames == 0,
                $"hidden={observer.HiddenFrames}");
            session.Check("button_rules_frames",
                observer.SearchFrames > 0 && observer.DeployFrames > 0 && observer.IdleFrames > 0,
                $"search={observer.SearchFrames} deploy={observer.DeployFrames} idle={observer.IdleFrames}");
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

    private static async Task WaitModeAsync(NGame host, SolverMode mode, int frames)
    {
        await WaitAsync(host, _ => SolverSettings.Current.Automation.Mode == mode, frames,
            "mode restored");
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
            $"collapsed={probe.Collapsed} settings={probe.SettingsVisible} " +
            $"details={probe.DetailsVisible} theme={probe.Theme} " +
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

    internal int SearchFrames { get; private set; }

    internal int DeployFrames { get; private set; }

    internal int IdleFrames { get; private set; }

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
            IdleFrames++;
        CheckButtons(snapshot);
        CheckRows(snapshot, probe);
    }

    private void CheckButtons(CombatUiSnapshot snapshot)
    {
        SolverOverlay panel = _panel!;
        bool searching = snapshot.IsSearching;
        bool adopting = snapshot.IsAdoptingRoute;
        bool ok = true;
        string? detail = null;
        void Expect(string name, bool condition)
        {
            if (condition)
                return;
            ok = false;
            detail ??= name;
        }
        Expect("stop_visible", panel.StopSearchButtonForTest?.Visible == searching);
        Expect("recalculate_visible", panel.RecalculateButtonForTest?.Visible == !searching);
        Expect("execute_visible", panel.ExecuteButtonForTest?.Visible == !searching);
        Expect("adopt_visible", panel.AdoptRouteButtonForTest?.Visible
            == (snapshot.CanAdoptCurrentRoute || adopting));
        Expect("memory_collapsed", panel.MemoryRowVisibleForTest != panel.ProbeForTest().Collapsed);
        Expect("stop_disabled", panel.StopSearchButtonForTest?.Disabled
            == (snapshot.SolverDisabled || !searching || snapshot.Search?.Phase == SearchUiPhase.Stopping));
        Expect("recalculate_disabled", panel.RecalculateButtonForTest?.Disabled
            == (snapshot.SolverDisabled || searching || snapshot.IsDeploying || adopting));
        Expect("adopt_disabled", panel.AdoptRouteButtonForTest?.Disabled
            == (snapshot.SolverDisabled || !snapshot.CanAdoptCurrentRoute || adopting));
        bool executeDisabled = snapshot.SolverDisabled || snapshot.IsDeploying
            || snapshot.IsApplyingCurrentTurn || adopting
            || searching && !snapshot.CanApplyCurrentTurn
            || !searching && !snapshot.CanExecuteCurrentTurn;
        Expect("execute_disabled", panel.ExecuteButtonForTest?.Disabled == executeDisabled);
        string expectedText = snapshot.IsDeploying ? SolverText.Get("执行中…")
            : snapshot.IsApplyingCurrentTurn ? SolverText.Get("正在应用…")
            : searching ? SolverText.Get("应用当前回合")
            : snapshot.DeployWhenReady ? SolverText.Get("已排队执行")
            : panel.PresentationForTest == SolverOverlayPresentation.ExecutedHistory
                && !snapshot.CanExecuteCurrentTurn ? SolverText.Get("等待下一回合")
            : SolverText.Get("执行本回合");
        Expect("execute_text", panel.ExecuteButtonForTest?.Text == expectedText);
        if (!ok)
        {
            ButtonMismatches++;
            Record($"buttons seq={snapshot.Sequence} phase={snapshot.Phase} detail={detail} " +
                $"searching={searching} canAdopt={snapshot.CanAdoptCurrentRoute} " +
                $"canApply={snapshot.CanApplyCurrentTurn} canExecute={snapshot.CanExecuteCurrentTurn}");
        }
    }

    private void CheckRows(CombatUiSnapshot snapshot, SolverOverlayProbe probe)
    {
        if (!CombatManager.Instance.IsInProgress)
            return;
        RouteUiSnapshot? route = snapshot.Route;
        RowChecks++;
        if (route == null && snapshot.Search?.RoutePreview is { Turns.Length: > 0 } preview)
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
                $"snapshot={count}/{length}");
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
        int? active = snapshot.Phase == CombatUiPhase.Deploying
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
                $"completed={route.CompletedActions} expected={expectedKind ?? "none"}@{expectedRow} lit={lit.Count}");
        }
    }

    private void Record(string mismatch)
    {
        if (Mismatches.Count < MaxMismatchRecords)
            Mismatches.Add(mismatch);
    }

    private const int MaxMismatchRecords = 8;
}
