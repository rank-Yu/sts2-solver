// Adapted from CombatSolver src/UI/SolverRouteRow.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The row now renders one RouteTurnUi whose action indexes are global over the route; the
// end-turn action is a real route action in Sts2Solver and is only drawn when it kills or is the
// whole turn. Control reuse, loop folding and the measured-pace highlight motion are kept.
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2Solver;

internal enum SolverPillState
{
    Pending,
    Active,
    Completed,
}

internal sealed partial class SolverRouteRow : PanelContainer
{
    private readonly List<PillEntry> _pills = [];
    private readonly List<(SolverLoopGroup Loop, SolverActionRun Run)> _loops = [];
    private readonly ColorRect _currentMarker;
    private readonly StyleBoxFlat _normalBox;
    private readonly StyleBoxFlat _currentBox;
    private RouteTurnUi? _populated;
    private bool _populatedVictory;
    private string? _populatedLanguage;
    private int _displayedCount;
    private Control? _endTurnPill;
    private SolverPillState _endTurnState;
    private int _endTurnIndex = -1;

    public Label TurnLabel { get; }

    public SolverRouteActionFlow ActionFlow { get; }

    public Label EnemyDamageLabel { get; }

    public Label HpLabel { get; }

    public Label EnergyLabel { get; }

    public int Turn => _populated?.Turn ?? 0;

    public int StartActionIndex => _populated?.StartActionIndex ?? 0;

    public SolverRouteRow(int index)
    {
        Name = $"Route{index + 1}";
        CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.RouteRowHeight);
        MouseFilter = MouseFilterEnum.Ignore;
        _normalBox = SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs + 2);
        _currentBox = SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            new Color(SolverUiTokens.Palette.Accent, 0.35f),
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs + 2);
        AddThemeStyleboxOverride("panel", _normalBox);

        HBoxContainer layout = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _currentMarker = new ColorRect
        {
            Color = Colors.Transparent,
            CustomMinimumSize = new Vector2(3, 22),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        layout.AddChild(_currentMarker);

        TurnLabel = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        TurnLabel.CustomMinimumSize = new Vector2(SolverUiTokens.Size.TurnColumnWidth, SolverUiTokens.Size.ActionPillHeight);
        TurnLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        layout.AddChild(TurnLabel);

        ActionFlow = new SolverRouteActionFlow
        {
            Name = "ActionFlow",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ActionFlow.AddThemeConstantOverride("h_separation", 6);
        ActionFlow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        layout.AddChild(ActionFlow);

        HBoxContainer metrics = new()
        {
            Alignment = BoxContainer.AlignmentMode.End,
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        metrics.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        EnemyDamageLabel = CreateMetric(SolverUiTokens.Size.MetricsDamageWidth, SolverUiTokens.Palette.Warning);
        HpLabel = CreateMetric(SolverUiTokens.Size.MetricsHpWidth, SolverUiTokens.Palette.TextMuted);
        HpLabel.MouseFilter = MouseFilterEnum.Pass;
        EnergyLabel = CreateMetric(SolverUiTokens.Size.MetricsEnergyWidth, SolverUiTokens.Palette.TextSecondary);
        metrics.AddChild(EnemyDamageLabel);
        metrics.AddChild(HpLabel);
        metrics.AddChild(EnergyLabel);
        layout.AddChild(metrics);
        AddChild(layout);
        SetProcess(false);
    }

    /// <summary>Builds the row for one turn; identical content keeps the existing controls and motion.</summary>
    public void Populate(RouteTurnUi turn, bool routeVictory)
    {
        if (HasSameContent(turn, routeVictory))
            return;
        ClearActions();
        TurnLabel.Text = SolverText.Format($"第 {turn.Turn} 回合");
        RouteActionUi[] actions = turn.Actions;
        RouteActionUi? endTurn = actions.Length > 0 && actions[^1].EndsTurn ? actions[^1] : null;
        int displayed = endTurn == null ? actions.Length : actions.Length - 1;
        if (endTurn != null)
            _endTurnIndex = endTurn.Index;
        if (displayed == 0 && endTurn != null)
        {
            Control pill = endTurn.Kills.Length > 0
                ? SolverActionPill.Create(endTurn)
                : SolverActionPill.CreateStatus(SolverText.Get("直接结束"), SolverUiTokens.Palette.TextSecondary);
            ActionFlow.AddChild(pill);
            _endTurnPill = pill;
        }
        else
        {
            RouteActionUi[] shown = actions[..displayed];
            foreach (SolverActionRun run in SolverActionRuns.Capture(shown, SolverActionPill.HasSamePresentation))
            {
                Container destination = ActionFlow;
                if (run.Repetitions > 1)
                {
                    SolverLoopGroup loop = new(run);
                    ActionFlow.AddChild(loop);
                    destination = loop.Actions;
                    _loops.Add((loop, run));
                }
                for (int offset = 0; offset < run.Period; offset++)
                {
                    Control pill = SolverActionPill.Create(shown[run.Start + offset]);
                    destination.AddChild(pill);
                    _pills.Add(new PillEntry(pill, run, offset));
                }
            }
            if (endTurn is { Kills.Length: > 0 })
            {
                Control pill = SolverActionPill.Create(endTurn);
                ActionFlow.AddChild(pill);
                _endTurnPill = pill;
            }
        }
        _displayedCount = displayed;

        EnemyDamageLabel.Text = turn.EnemyHpLost > 0 ? SolverText.Format($"敌方 -{turn.EnemyHpLost}") : "";
        int net = turn.HpAfter - turn.HpBefore;
        (string hpText, Color hpColor) = turn.CombatEnded
            ? (routeVictory ? SolverText.Get("战斗胜利") : SolverText.Get("战斗结束"),
                routeVictory ? SolverUiTokens.Palette.Success : SolverUiTokens.Palette.Danger)
            : net < 0
                ? ($"{net} HP", SolverUiTokens.Palette.Danger)
                : net > 0
                    ? ($"+{net} HP", SolverUiTokens.Palette.Success)
                    : ("0 HP", SolverUiTokens.Palette.TextMuted);
        HpLabel.Text = hpText;
        HpLabel.AddThemeColorOverride("font_color", hpColor);
        HpLabel.TooltipText = SolverText.Format($"生命 {turn.HpBefore} → {turn.HpAfter}");
        EnergyLabel.Text = turn.EnergyLeft is { } energy ? SolverText.Format($"余 {energy} 费") : "";
        _populated = turn;
        _populatedVictory = routeVictory;
        _populatedLanguage = LocManager.Instance?.Language;
    }

    /// <summary>Marks the row as the current turn, a finished turn or a future one.</summary>
    public void SetRowState(bool current, bool completed)
    {
        AddThemeStyleboxOverride("panel", current ? _currentBox : _normalBox);
        _currentMarker.Color = current ? SolverUiTokens.Palette.Accent : Colors.Transparent;
        TurnLabel.AddThemeColorOverride("font_color",
            current ? SolverUiTokens.Palette.Accent : SolverUiTokens.Palette.TextPrimary);
        Modulate = completed && !current ? SolverUiTokens.Palette.CompletedRowModulate : Colors.White;
    }

    /// <summary>Applies global route progress; indexes outside this turn only mark it done or pending.</summary>
    public void SetDeploymentProgress(int completedActions, int? activeActionIndex)
    {
        if (_populated is not { } turn)
            return;
        int start = turn.StartActionIndex;
        int completedLocal = Math.Clamp(completedActions - start, 0, _displayedCount);
        int? activeLocal = activeActionIndex is { } active && active >= start && active < start + _displayedCount
            ? active - start
            : null;
        if (activeLocal is { } current)
            NoteDeploymentStep((completedLocal, current));
        else if (activeActionIndex == _endTurnIndex && _endTurnIndex >= 0)
            NoteDeploymentStep((-1, -1));
        for (int index = 0; index < _pills.Count; index++)
        {
            PillEntry entry = _pills[index];
            bool isActive = entry.Run.IsActive(entry.Offset, activeLocal);
            SolverPillState state = isActive ? SolverPillState.Active
                : entry.Run.IsCompleted(entry.Offset, completedLocal) ? SolverPillState.Completed
                : SolverPillState.Pending;
            _pills[index] = entry with { State = state };
            SetPillTarget(entry.Pill, StateColor(state), isActive);
        }
        foreach ((SolverLoopGroup loop, SolverActionRun run) in _loops)
        {
            bool done = completedLocal >= run.End;
            bool isActive = !done && activeLocal is { } local && local >= run.Start && local < run.End;
            SetPillTarget(loop.Badge, StateColor(done ? SolverPillState.Completed
                : isActive ? SolverPillState.Active : SolverPillState.Pending), isActive);
            loop.SetCompleted(done);
        }
        if (_endTurnPill != null)
        {
            bool isActive = activeActionIndex == _endTurnIndex;
            _endTurnState = isActive ? SolverPillState.Active
                : completedActions > _endTurnIndex ? SolverPillState.Completed
                : SolverPillState.Pending;
            SetPillTarget(_endTurnPill, StateColor(_endTurnState), isActive);
        }
    }

    private static Color StateColor(SolverPillState state) => state switch
    {
        SolverPillState.Active => SolverUiTokens.Palette.ActiveActionModulate,
        SolverPillState.Completed => SolverUiTokens.Palette.CompletedActionModulate,
        _ => Colors.White,
    };

#if STS2_SOLVER_TESTING
    internal SolverRowProbe ProbeForTest()
    {
        List<SolverPillProbe> pills = [.. _pills.Select(static entry => new SolverPillProbe(
            "action", entry.Run.Start, entry.Run.Period, entry.Run.Repetitions, entry.Offset,
            entry.State.ToString().ToLowerInvariant()))];
        if (_endTurnPill != null)
            pills.Add(new SolverPillProbe("end_turn", -1, 1, 1, 0, _endTurnState.ToString().ToLowerInvariant()));
        return new SolverRowProbe(Turn, StartActionIndex, _displayedCount, _endTurnIndex,
            _endTurnPill != null, [.. pills]);
    }
#endif

    private bool HasSameContent(RouteTurnUi turn, bool routeVictory)
    {
        if (_populated is not { } previous
            || _populatedVictory != routeVictory
            || _populatedLanguage != LocManager.Instance?.Language
            || previous.Turn != turn.Turn
            || previous.StartActionIndex != turn.StartActionIndex
            || previous.HpBefore != turn.HpBefore
            || previous.HpAfter != turn.HpAfter
            || previous.EnemyHpLost != turn.EnemyHpLost
            || previous.CombatEnded != turn.CombatEnded
            || previous.EnergyLeft != turn.EnergyLeft
            || previous.Actions.Length != turn.Actions.Length)
        {
            return false;
        }
        for (int index = 0; index < turn.Actions.Length; index++)
        {
            if (!SolverActionPill.HasSameContent(previous.Actions[index], turn.Actions[index]))
                return false;
        }
        return true;
    }

    private static Label CreateMetric(float width, Color color)
    {
        Label label = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Body, color);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.CustomMinimumSize = new Vector2(width, SolverUiTokens.Size.ActionPillHeight);
        return label;
    }

    // Deployment highlight motion. Steps can arrive faster than any fixed animation (Instant speed
    // plays several cards per second), so every duration is derived from the measured gap between
    // steps: fast deployments collapse to near-instant color changes and never flash or breathe,
    // slow ones get a visible fade, an activation flash and a breathing highlight. Motion only
    // touches Modulate after the target is decided; the target itself stays synchronous.
    private const double MaxHighlightTimeConstantSeconds = 0.08d;
    private const double HighlightTimeConstantShareOfStep = 0.2d;
    private const double MinStepForFlashSeconds = 0.3d;
    private const float ActivationFlashGain = 1.45f;
    private const double BreathDelaySeconds = 0.45d;
    private const double BreathRampSeconds = 0.35d;
    private const double BreathPeriodSeconds = 1.2d;
    private const float BreathGain = 0.22f;

    private readonly Dictionary<CanvasItem, PillMotion> _pillMotion = [];
    private (int, int)? _lastDeploymentStep;
    private ulong _lastDeploymentStepUsec;
    private double? _deploymentStepSeconds;

    private sealed class PillMotion
    {
        public Color Current;
        public Color Target;
        public bool Active;
        public double ActiveSeconds;
    }

    private readonly record struct PillEntry(Control Pill, SolverActionRun Run, int Offset)
    {
        public SolverPillState State { get; init; }
    }

    private void NoteDeploymentStep((int, int) step)
    {
        if (_lastDeploymentStep == step)
            return;
        ulong now = Time.GetTicksUsec();
        if (_lastDeploymentStep != null)
            _deploymentStepSeconds = (now - _lastDeploymentStepUsec) / 1_000_000d;
        _lastDeploymentStep = step;
        _lastDeploymentStepUsec = now;
    }

    // Unknown pace (first step of a deployment) counts as slow so the route start is marked.
    private double HighlightTimeConstant()
        => Math.Min(
            MaxHighlightTimeConstantSeconds,
            (_deploymentStepSeconds ?? double.PositiveInfinity) * HighlightTimeConstantShareOfStep);

    private void SetPillTarget(CanvasItem pill, Color target, bool active)
    {
        if (!_pillMotion.TryGetValue(pill, out PillMotion? motion))
        {
            motion = new PillMotion { Current = pill.Modulate };
            _pillMotion.Add(pill, motion);
        }
        if (motion.Target == target && motion.Active == active && motion.Current == target && !active)
            return;
        if (active && !motion.Active)
        {
            motion.ActiveSeconds = 0d;
            if ((_deploymentStepSeconds ?? double.PositiveInfinity) >= MinStepForFlashSeconds)
                motion.Current = Brighten(target, ActivationFlashGain);
        }
        motion.Target = target;
        motion.Active = active;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (_pillMotion.Count == 0)
        {
            SetProcess(false);
            return;
        }
        double timeConstant = HighlightTimeConstant();
        // Below about half a frame an eased step is indistinguishable from a snap.
        double blend = timeConstant < 0.008d ? 1d : SolverUiMotion.Blend(delta, timeConstant);
        bool moving = false;
        foreach ((CanvasItem pill, PillMotion motion) in _pillMotion)
        {
            if (!IsInstanceValid(pill))
                continue;
            motion.Current = SolverUiMotion.Approach(motion.Current, motion.Target, blend);
            Color shown = motion.Current;
            if (motion.Active)
            {
                motion.ActiveSeconds += delta;
                double breath = BreathLevel(motion.ActiveSeconds);
                shown = Brighten(shown, 1f + BreathGain * (float)breath);
                moving = true;
            }
            moving |= motion.Current != motion.Target;
            if (pill.Modulate != shown)
                pill.Modulate = shown;
        }
        if (!moving)
            SetProcess(false);
    }

    // A pill that stays current for a while (slow speed, a choice screen, a long card animation)
    // starts breathing; at fast paces the next step arrives before the delay and nothing pulses.
    private static double BreathLevel(double activeSeconds)
    {
        if (activeSeconds <= BreathDelaySeconds)
            return 0d;
        double sinceDelay = activeSeconds - BreathDelaySeconds;
        double ramp = Math.Min(1d, sinceDelay / BreathRampSeconds);
        return ramp * (0.5d - 0.5d * Math.Cos(sinceDelay / BreathPeriodSeconds * Math.Tau));
    }

    private static Color Brighten(Color color, float gain)
        => new(color.R * gain, color.G * gain, color.B * gain, color.A);

    private void ResetDeploymentMotion()
    {
        foreach ((CanvasItem pill, _) in _pillMotion)
        {
            if (IsInstanceValid(pill))
                pill.Modulate = Colors.White;
        }
        _pillMotion.Clear();
        foreach ((SolverLoopGroup loop, _) in _loops)
        {
            if (IsInstanceValid(loop))
                loop.SetCompleted(false);
        }
        _lastDeploymentStep = null;
        _deploymentStepSeconds = null;
    }

    private void ClearActions()
    {
        ResetDeploymentMotion();
        _populated = null;
        _populatedLanguage = null;
        _loops.Clear();
        _pills.Clear();
        _displayedCount = 0;
        _endTurnPill = null;
        _endTurnState = SolverPillState.Pending;
        _endTurnIndex = -1;
        foreach (Node child in ActionFlow.GetChildren())
        {
            ActionFlow.RemoveChild(child);
            child.QueueFree();
        }
    }
}

#if STS2_SOLVER_TESTING
internal sealed record SolverPillProbe(string Kind, int RunStart, int Period, int Repetitions, int Offset, string State);

internal sealed record SolverRowProbe(int Turn, int StartActionIndex, int DisplayedCount, int EndTurnIndex,
    bool EndTurnShown, SolverPillProbe[] Pills);
#endif
