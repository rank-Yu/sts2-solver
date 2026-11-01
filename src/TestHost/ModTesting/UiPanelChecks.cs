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
    internal static async Task<Session> BeginAsync(NGame host)
    {
        Session session = new();
        SolverPanelNode? panel = SolverPanelNode.Instance;
        session.Check("panel_exists", panel != null, null);
        if (panel == null)
            return session;
        bool visible = await WaitAsync(host, () => panel.ProbeForTest().Visible, 180);
        session.Check("panel_visible_in_combat", visible, Describe(panel));

        PushCtrlF9(host);
        bool hidden = await WaitAsync(host, () => !panel.ProbeForTest().Visible, 20);
        session.Check("shortcut_hides_panel", hidden && panel.ProbeForTest().HiddenByShortcut, Describe(panel));
        PushCtrlF9(host);
        bool shown = await WaitAsync(host, () => panel.ProbeForTest().Visible, 20);
        session.Check("shortcut_shows_panel", shown && !panel.ProbeForTest().HiddenByShortcut, Describe(panel));

        string language = LocManager.Instance.Language;
        bool english = language != "eng";
        int buildsBefore = panel.ProbeForTest().BuildCount;
        if (english)
        {
            // The game's own English override fires the locale-change callbacks like a settings switch.
            LocManager.Instance.StartOverridingLanguageAsEnglish();
            await Frames(host, 4);
            SolverPanelProbe switched = panel.ProbeForTest();
            session.Check("locale_rebuilds_in_english",
                switched.Language == "eng" && switched.BuildCount == buildsBefore + 1, Describe(panel));
        }
        else
        {
            session.Skip("locale_rebuilds_in_english", "already_english");
        }
        await ExerciseAsync(host, panel, session);
        if (english)
        {
            (int resolved, int missing) = SolverUiTokens.LatinFontsForTesting;
            session.Check("latin_fonts_resolved", resolved > 0 && missing == 0,
                $"resolved={resolved} missing={missing}");
            int buildsEnglish = panel.ProbeForTest().BuildCount;
            LocManager.Instance.StopOverridingLanguageAsEnglish();
            await Frames(host, 4);
            SolverPanelProbe restored = panel.ProbeForTest();
            session.Check("locale_restores", restored.Language == language
                && restored.BuildCount == buildsEnglish + 1, Describe(panel));
        }
        string[] missingKeys = SolverText.MissingKeysForTesting;
        session.Check("english_keys_present", missingKeys.Length == 0, string.Join("|", missingKeys));
        session.Observer = new UiPanelObserver(panel) { Name = "Sts2SolverPanelObserver" };
        host.AddChild(session.Observer);
        return session;
    }

    internal static async Task FinishAsync(NGame host, Session session, string resultDirectory)
    {
        UiPanelObserver? observer = session.Observer;
        session.Stop();
        SolverPanelNode? panel = SolverPanelNode.Instance;
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
                $"checks={observer.HighlightChecks} active={observer.ActiveHighlights} mismatches={observer.HighlightMismatches}");
            session.Check("panel_visible_during_combat", observer.HiddenFrames == 0,
                $"hidden={observer.HiddenFrames}");
        }
        string? afterCombat = null;
        if (panel != null)
        {
            bool ended = await WaitAsync(host, static () => !CombatManager.Instance.IsInProgress, 300);
            if (ended)
            {
                bool hidden = await WaitAsync(host, () => !panel.ProbeForTest().Visible, 30);
                session.Check("panel_hidden_after_combat", hidden, Describe(panel));
                afterCombat = hidden ? "hidden" : "visible";
            }
            else
            {
                // A cancelled run leaves the combat in progress; the hide check then has nothing to observe.
                afterCombat = "combat_in_progress";
                session.Skip("panel_hidden_after_combat", "combat_in_progress");
            }
        }
        UiPanelResult result = new(1, session.Failures.Count == 0 ? "ok" : "failed", afterCombat,
            [.. session.Checks], [.. session.Failures], observer?.Mismatches.ToArray() ?? [],
            observer?.ObservedFrames ?? 0, observer?.RowChecks ?? 0, observer?.HighlightChecks ?? 0,
            observer?.ActiveHighlights ?? 0, observer?.PhaseChecks ?? 0);
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

    private static async Task ExerciseAsync(NGame host, SolverPanelNode panel, Session session)
    {
        await Press(host, panel.DetailsButtonForTest);
        session.Check("details_opens", panel.ProbeForTest().DetailsVisible, Describe(panel));
        await Press(host, panel.DetailsButtonForTest);
        session.Check("details_closes", !panel.ProbeForTest().DetailsVisible, Describe(panel));

        await Press(host, panel.CollapseButtonForTest);
        session.Check("collapse_persists",
            panel.ProbeForTest().Collapsed && SolverSettings.Current.Ui.Collapsed, Describe(panel));
        await Press(host, panel.CollapseButtonForTest);
        session.Check("expand_persists",
            !panel.ProbeForTest().Collapsed && !SolverSettings.Current.Ui.Collapsed, Describe(panel));

        await Press(host, panel.SettingsButtonForTest);
        session.Check("settings_opens", panel.ProbeForTest().SettingsVisible, Describe(panel));
        SolverUiTheme original = SolverSettings.Current.Ui.Theme;
        SolverUiTheme other = original == SolverUiTheme.Dark ? SolverUiTheme.Light : SolverUiTheme.Dark;
        int builds = panel.ProbeForTest().BuildCount;
        await SelectTheme(host, panel, other);
        SolverPanelProbe switched = panel.ProbeForTest();
        session.Check("theme_rebuilds", switched.Theme == other.ToString() && switched.BuildCount == builds + 1
            && switched.SettingsVisible && SolverSettings.Current.Ui.Theme == other, Describe(panel));
        await SelectTheme(host, panel, original);
        SolverPanelProbe restored = panel.ProbeForTest();
        session.Check("theme_restores", restored.Theme == original.ToString() && restored.BuildCount == builds + 2
            && SolverSettings.Current.Ui.Theme == original, Describe(panel));
        await Press(host, panel.SettingsButtonForTest);
        session.Check("settings_closes", !panel.ProbeForTest().SettingsVisible, Describe(panel));
    }

    private static async Task Press(NGame host, Button? button)
    {
        if (button == null)
            throw new InvalidDataException("panel_button_missing");
        button.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(host, 3);
    }

    private static async Task SelectTheme(NGame host, SolverPanelNode panel, SolverUiTheme theme)
    {
        OptionButton option = panel.ThemeOptionForTest ?? throw new InvalidDataException("theme_option_missing");
        int index = option.GetItemIndex((int)theme);
        option.Select(index);
        option.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
        await Frames(host, 4);
    }

    private static void PushCtrlF9(NGame host)
    {
        Viewport viewport = host.GetViewport();
        viewport.PushInput(new InputEventKey
        {
            Keycode = Key.F9,
            PhysicalKeycode = Key.F9,
            CtrlPressed = true,
            Pressed = true,
        });
        viewport.PushInput(new InputEventKey
        {
            Keycode = Key.F9,
            PhysicalKeycode = Key.F9,
            CtrlPressed = true,
            Pressed = false,
        });
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

    private static async Task Frames(NGame host, int count)
    {
        for (int frame = 0; frame < count; frame++)
            await CombatFrameWaiter.NextFrameAsync(host);
    }

    private static string Describe(SolverPanelNode panel)
    {
        SolverPanelProbe probe = panel.ProbeForTest();
        return $"visible={probe.Visible} hidden_by_shortcut={probe.HiddenByShortcut} collapsed={probe.Collapsed} " +
            $"settings={probe.SettingsVisible} details={probe.DetailsVisible} theme={probe.Theme} " +
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
        int HighlightChecks, int ActiveHighlights, int PhaseChecks);
}

/// <summary>
/// Per-frame comparison of the panel with the snapshot it last applied. Frames where the panel has
/// not caught up with the newest snapshot are counted as pending, not compared.
/// </summary>
internal sealed partial class UiPanelObserver : Node
{
    private const int MaximumMismatchRecords = 8;

    private readonly SolverPanelNode? _panel;

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

    internal List<string> Mismatches { get; } = [];

    public UiPanelObserver()
    {
    }

    public UiPanelObserver(SolverPanelNode panel)
    {
        _panel = panel;
    }

    public override void _Process(double delta)
    {
        if (_panel == null || !IsInstanceValid(_panel) || !CombatManager.Instance.IsInProgress)
            return;
        CombatUiSnapshot snapshot = CombatUiStore.Snapshot;
        SolverPanelProbe probe = _panel.ProbeForTest();
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
        CheckRows(snapshot, probe);
    }

    private void CheckRows(CombatUiSnapshot snapshot, SolverPanelProbe probe)
    {
        RouteUiSnapshot? route = snapshot.Route;
        RowChecks++;
        if (route == null || route.Turns.Length == 0)
        {
            if (probe.Rows.Length != 0)
            {
                RowMismatches++;
                Record($"rows seq={snapshot.Sequence} panel={probe.Rows.Length} snapshot=none");
            }
            return;
        }
        // Expected window: at most 16 turns, starting one turn before the current turn when it fits.
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

    private void CheckHighlight(CombatUiSnapshot snapshot, RouteUiSnapshot route, SolverPanelProbe probe,
        int start, int count)
    {
        HighlightChecks++;
        int? active = snapshot.Phase == CombatUiPhase.Deploying
            && route.CurrentActionIndex >= route.CompletedActions
            && route.CurrentActionIndex < route.ActionCount
                ? route.CurrentActionIndex
                : null;
        // Where the active action should be drawn: an action pill, a shown end-turn pill, or nowhere
        // (a plain end turn is implied by the row, and turns outside the window have no row).
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
        // Completed marks must agree with the snapshot's completed count for every drawn action pill.
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
        if (Mismatches.Count < MaximumMismatchRecords)
            Mismatches.Add(mismatch);
    }
}
