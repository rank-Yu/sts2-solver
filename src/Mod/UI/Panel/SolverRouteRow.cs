// Ported from CombatSolver src/UI/SolverRouteRow.cs at 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors).
// The row renders one SolverOverlayTurnSnapshot; actions carry global route indexes so deployment
// progress can be compared without re-slicing the route. Turn-start choices, end-turn, outcome
// metrics and the measured-pace highlight motion are kept.
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
    private readonly List<(CanvasItem Pill, SolverActionRun Run, int Offset, int Index)> _deploymentActions = [];
    private readonly List<Control> _turnStartChoicePills = [];
    private readonly ColorRect _currentMarker;
    private readonly StyleBoxFlat _normalBox;
    private readonly StyleBoxFlat _currentBox;
    private readonly List<(SolverLoopGroup Loop, SolverActionRun Run)> _loopGroups = [];
    private int _deploymentActionCount;
    private CanvasItem? _endTurnAction;
    private int _endTurnIndex = -1;
    private SolverPillState _endTurnState;
    private readonly Dictionary<CanvasItem, SolverPillState> _pillStates = [];
    private SolverOverlayTurnSnapshot? _populatedTurn;
    private string? _populatedLanguage;

    public Label TurnLabel { get; }

    public SolverRouteActionFlow ActionFlow { get; }

    public Label EnemyDamageLabel { get; }

    public Label OutcomeLabel { get; }

    public Label EnergyLabel { get; }

    public int Turn => _populatedTurn?.Turn ?? 0;

    public int StartActionIndex => _populatedTurn?.StartActionIndex ?? 0;

    public int DeploymentActionCount => _deploymentActionCount;

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
            SolverUiTokens.Spacing.Sm);
        _currentBox = SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            new Color(SolverUiTokens.Palette.Accent, 0.35f),
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Sm);
        AddThemeStyleboxOverride("panel", index == 0 ? _currentBox : _normalBox);

        HBoxContainer layout = new()
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _currentMarker = new ColorRect
        {
            Color = index == 0 ? SolverUiTokens.Palette.Accent : Colors.Transparent,
            CustomMinimumSize = new Vector2(3, 24),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        layout.AddChild(_currentMarker);

        TurnLabel = SolverUiTokens.CreateLabel(
            SolverText.Format($"第 {index + 1} 回合"),
            SolverUiTokens.Type.Body,
            index == 0 ? SolverUiTokens.Palette.Accent : SolverUiTokens.Palette.TextPrimary);
        TurnLabel.CustomMinimumSize = new Vector2(SolverUiTokens.Size.TurnColumnWidth,
            SolverUiTokens.Size.ActionPillHeight);
        TurnLabel.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        TurnLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
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

        HBoxContainer outcomeLayout = new()
        {
            Alignment = BoxContainer.AlignmentMode.End,
            CustomMinimumSize = new Vector2(
                SolverUiTokens.Size.MetricsDamageWidth + SolverUiTokens.Size.MetricsHpWidth
                    + SolverUiTokens.Size.MetricsEnergyWidth + SolverUiTokens.Spacing.Sm * 2,
                SolverUiTokens.Size.ActionPillHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        outcomeLayout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        EnemyDamageLabel = CreateMetric(SolverUiTokens.Size.MetricsDamageWidth,
            SolverUiTokens.Palette.Warning);
        OutcomeLabel = CreateMetric(SolverUiTokens.Size.MetricsHpWidth, SolverUiTokens.Palette.TextMuted);
        EnergyLabel = CreateMetric(SolverUiTokens.Size.MetricsEnergyWidth,
            SolverUiTokens.Palette.TextSecondary);
        outcomeLayout.AddChild(EnemyDamageLabel);
        outcomeLayout.AddChild(OutcomeLabel);
        outcomeLayout.AddChild(EnergyLabel);
        layout.AddChild(outcomeLayout);
        AddChild(layout);
    }

    private static Label CreateMetric(float width, Color color)
    {
        Label label = SolverUiTokens.CreateLabel("", SolverUiTokens.Type.Body, color);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.CustomMinimumSize = new Vector2(width, SolverUiTokens.Size.ActionPillHeight);
        label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return label;
    }

    public void Populate(SolverOverlayTurnSnapshot turn)
    {
        if (HasSameActions(turn))
        {
            ResetDeploymentMotion();
            return;
        }
        ClearActions();
        TurnLabel.Text = SolverText.Format($"第 {turn.Turn} 回合");
        foreach (string choice in turn.TurnStartChoices)
        {
            Control pill = SolverActionPill.CreateChoice(choice);
            ActionFlow.AddChild(pill);
            _turnStartChoicePills.Add(pill);
        }
        if (turn.Actions.Count == 0)
        {
            Control endTurn = turn.EndTurnAction == null
                ? SolverActionPill.CreateStatus(SolverText.Get("直接结束"), SolverUiTokens.Palette.TextMuted)
                : SolverActionPill.Create(turn.EndTurnAction);
            ActionFlow.AddChild(endTurn);
            _endTurnAction = endTurn;
            _endTurnIndex = turn.EndTurnAction != null ? turn.Actions.Count : -1;
            RememberPopulated(turn);
            return;
        }

        foreach (SolverActionRun run in SolverActionRuns.Capture(turn.Actions,
                     static (left, right) => left.HasSamePresentation(right)))
        {
            Container destination = ActionFlow;
            if (run.Repetitions > 1)
            {
                SolverLoopGroup loopGroup = SolverActionPill.CreateCycle(run, out HFlowContainer loopActions);
                ActionFlow.AddChild(loopGroup);
                destination = loopActions;
                _loopGroups.Add((loopGroup, run));
            }
            for (int offset = 0; offset < run.Period; offset++)
            {
                SolverOverlayActionSnapshot action = turn.Actions[run.Start + offset];
                Control pill = SolverActionPill.Create(action);
                destination.AddChild(pill);
                _deploymentActions.Add((pill, run, offset, run.Start + offset));
            }
        }
        _deploymentActionCount = turn.Actions.Count;

        if (turn.EndTurnAction is { Kills.Count: > 0 } endTurnAction)
        {
            Control endTurn = SolverActionPill.Create(endTurnAction);
            ActionFlow.AddChild(endTurn);
            _endTurnAction = endTurn;
        }
        _endTurnIndex = turn.EndTurnAction != null ? turn.Actions.Count : -1;
        RememberPopulated(turn);
    }

    public void ShowStatus(string text)
    {
        ClearActions();
        ActionFlow.AddChild(SolverActionPill.CreateStatus(text, SolverUiTokens.Palette.TextMuted));
    }

    public void SetRowState(bool current, bool completed)
    {
        AddThemeStyleboxOverride("panel", current ? _currentBox : _normalBox);
        _currentMarker.Color = current ? SolverUiTokens.Palette.Accent : Colors.Transparent;
        TurnLabel.AddThemeColorOverride("font_color",
            current ? SolverUiTokens.Palette.Accent : SolverUiTokens.Palette.TextPrimary);
        Modulate = completed && !current ? SolverUiTokens.Palette.CompletedRowModulate : Colors.White;
    }

    public void SetOutcome(
        string text,
        Color color,
        string energyText = "",
        string enemyDamageText = "")
    {
        EnemyDamageLabel.Text = enemyDamageText;
        OutcomeLabel.Text = text;
        OutcomeLabel.AddThemeColorOverride("font_color", color);
        EnergyLabel.Text = energyText;
    }

    private void RememberPopulated(SolverOverlayTurnSnapshot turn)
    {
        _populatedLanguage = LocManager.Instance.Language;
        _populatedTurn = turn;
    }

    private bool HasSameActions(SolverOverlayTurnSnapshot turn)
    {
        if (_populatedTurn is not { } previous
            || _populatedLanguage != LocManager.Instance.Language
            || previous.Turn != turn.Turn
            || previous.StartActionIndex != turn.StartActionIndex
            || previous.HpLoss != turn.HpLoss
            || previous.HpRecovered != turn.HpRecovered
            || previous.EnemyHpDamageLost != turn.EnemyHpDamageLost
            || previous.EnergyLeft != turn.EnergyLeft
            || previous.CombatEnded != turn.CombatEnded
            || !previous.TurnStartChoices.SequenceEqual(turn.TurnStartChoices)
            || previous.Actions.Count != turn.Actions.Count)
            return false;
        for (int index = 0; index < turn.Actions.Count; index++)
        {
            if (previous.Actions[index].Index != turn.Actions[index].Index
                || !previous.Actions[index].HasSamePresentation(turn.Actions[index]))
                return false;
        }
        return ReferenceEquals(previous.EndTurnAction, turn.EndTurnAction)
            || previous.EndTurnAction is { } endTurn && turn.EndTurnAction is { } nextEndTurn
                && endTurn.HasSamePresentation(nextEndTurn);
    }

    public void SetDeploymentProgress(int completedActions, int? activeActionIndex)
    {
        if (completedActions < 0 || completedActions > _deploymentActionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedActions),
                completedActions,
                $"路线只有 {_deploymentActionCount} 个可执行动作。");
        }
        if (activeActionIndex is { } active
            && (active < completedActions || active >= _deploymentActionCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeActionIndex),
                active,
                "当前动作必须是尚未完成的路线动作。");
        }
        if (activeActionIndex is { } current)
            NoteDeploymentStep((completedActions, current));
        bool choiceActive = completedActions == 0 && activeActionIndex == null;
        bool choiceCompleted = completedActions > 0 || activeActionIndex != null;
        foreach (Control pill in _turnStartChoicePills)
        {
            SetPillTarget(
                pill,
                choiceCompleted
                    ? SolverUiTokens.Palette.CompletedActionModulate
                    : choiceActive
                        ? SolverUiTokens.Palette.ActiveActionModulate
                        : Colors.White,
                choiceActive);
        }
        foreach ((CanvasItem pill, _, _, int index) in _deploymentActions)
        {
            bool isActive = activeActionIndex == index;
            bool isCompleted = completedActions > index;
            _pillStates[pill] = isActive ? SolverPillState.Active
                : isCompleted ? SolverPillState.Completed
                : SolverPillState.Pending;
            SetPillTarget(
                pill,
                isActive
                    ? SolverUiTokens.Palette.ActiveActionModulate
                    : isCompleted
                        ? SolverUiTokens.Palette.CompletedActionModulate
                        : Colors.White,
                isActive);
        }
        foreach ((SolverLoopGroup loop, SolverActionRun run) in _loopGroups)
        {
            bool isCompleted = completedActions >= run.End;
            bool isActive = !isCompleted && activeActionIndex is { } activeIndex
                && activeIndex >= run.Start
                && activeIndex < run.End;
            SetPillTarget(
                loop.Badge,
                isCompleted
                    ? SolverUiTokens.Palette.CompletedActionModulate
                    : isActive
                        ? SolverUiTokens.Palette.ActiveActionModulate
                        : Colors.White,
                isActive);
            loop.SetCompleted(isCompleted);
        }
        if (_endTurnAction != null)
        {
            bool isActive = activeActionIndex == _endTurnIndex;
            bool isCompleted = completedActions > _endTurnIndex;
            _endTurnState = isActive ? SolverPillState.Active
                : isCompleted ? SolverPillState.Completed
                : SolverPillState.Pending;
            SetPillTarget(
                _endTurnAction,
                isCompleted
                    ? SolverUiTokens.Palette.CompletedActionModulate
                    : isActive
                        ? SolverUiTokens.Palette.ActiveActionModulate
                        : Colors.White,
                isActive);
        }
    }

    public void SetTurnStartChoiceDeploymentState(bool active, bool completed)
    {
        if (_turnStartChoicePills.Count == 0)
            return;
        foreach (Control pill in _turnStartChoicePills)
        {
            SetPillTarget(
                pill,
                completed
                    ? SolverUiTokens.Palette.CompletedActionModulate
                    : active
                        ? SolverUiTokens.Palette.ActiveActionModulate
                        : Colors.White,
                active && !completed);
        }
    }

    public void SetEndTurnDeploymentState(bool active, bool completed)
    {
        if (completed)
        {
            SetTurnStartChoiceDeploymentState(active: false, completed: true);
            foreach ((SolverLoopGroup loop, _) in _loopGroups)
            {
                SetPillTarget(loop.Badge, SolverUiTokens.Palette.CompletedActionModulate, active: false);
                loop.SetCompleted(true);
            }
        }
        if (_endTurnAction == null)
            return;
        _endTurnState = completed ? SolverPillState.Completed
            : active ? SolverPillState.Active
            : SolverPillState.Pending;
        SetPillTarget(
            _endTurnAction,
            completed
                ? SolverUiTokens.Palette.CompletedActionModulate
                : active
                    ? SolverUiTokens.Palette.ActiveActionModulate
                    : Colors.White,
            active && !completed);
    }

    // Deployment highlight motion. Steps can arrive faster than any fixed animation, so every
    // duration is derived from the measured gap between steps: fast deployments collapse to
    // near-instant color changes and never flash or breathe; slow ones get a visible fade, an
    // activation flash and a breathing highlight.
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
        double blend = timeConstant < 0.008d ? 1d : SolverUiMotion.Blend(delta, timeConstant);
        bool moving = false;
        foreach ((CanvasItem pill, PillMotion motion) in _pillMotion)
        {
            if (!GodotObject.IsInstanceValid(pill))
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
            if (GodotObject.IsInstanceValid(pill))
                pill.Modulate = Colors.White;
        }
        _pillMotion.Clear();
        foreach ((SolverLoopGroup loop, _) in _loopGroups)
        {
            if (GodotObject.IsInstanceValid(loop))
                loop.SetCompleted(false);
        }
        _lastDeploymentStep = null;
        _deploymentStepSeconds = null;
    }

#if STS2_SOLVER_TESTING
    internal SolverRowProbe ProbeForTest()
    {
        List<SolverPillProbe> pills = [.. _deploymentActions.Select(entry =>
            new SolverPillProbe("action", entry.Run.Start, entry.Run.Period, entry.Run.Repetitions,
                entry.Offset, (_pillStates.GetValueOrDefault(entry.Pill)).ToString().ToLowerInvariant()))];
        if (_endTurnAction != null)
            pills.Add(new SolverPillProbe("end_turn", -1, 1, 1, 0,
                _endTurnState.ToString().ToLowerInvariant()));
        return new SolverRowProbe(Turn, StartActionIndex, _deploymentActionCount,
            _endTurnIndex >= 0 ? StartActionIndex + _endTurnIndex : -1,
            _endTurnAction != null, [.. pills]);
    }
#endif

    private void ClearActions()
    {
        ResetDeploymentMotion();
        _populatedTurn = null;
        _populatedLanguage = null;
        _turnStartChoicePills.Clear();
        _loopGroups.Clear();
        _deploymentActions.Clear();
        _deploymentActionCount = 0;
        _endTurnAction = null;
        _endTurnIndex = -1;
        _endTurnState = SolverPillState.Pending;
        _pillStates.Clear();
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
