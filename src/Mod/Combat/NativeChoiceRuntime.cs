// Ported from CombatSolver src/Runtime/NativeChoiceRuntime.cs at commit 5c773caa
// (MIT License, Copyright (c) 2026 Torch1230 and contributors). The observation patches, request
// queue, surface visibility/locking and native click sequence keep the original semantics; the
// plan type is Sts2Solver's PlannedChoice and the trace/log sink is this Mod's logger.
// Peek input recovery follows the same upstream file at bb64b33fd1 (same MIT license).
using System.Reflection;
using Environment = System.Environment;
using System.Threading.Channels;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using Sts2Solver.Simulation.Combat;
using STS2RitsuLib.Patching.Models;

namespace Sts2Solver;

internal enum NativeChoiceSurfaceKind
{
    ChooseCard,
    SimpleGrid,
    CombatPile,
    Hand,
    HandUpgrade,
}

internal enum NativeChoiceSurfaceState
{
    ExpectedVisible,
    CoveredByOtherOverlay,
    Missing,
}

internal readonly record struct NativeChoiceObservedOption(string CardId, int UpgradeLevel);

internal sealed record NativeChoiceRequest(
    long Sequence,
    NativeChoiceSurfaceKind Surface,
    Player Player,
    IReadOnlyList<CardModel> Options,
    IReadOnlyList<NativeChoiceObservedOption>? ObservedOptions,
    int MinSelect,
    int MaxSelect,
    bool RequiresSurface,
    bool CanSkip,
    bool RequireManualConfirmation,
    string SourceId)
{
    public Task? Completion { get; set; }

    /// <summary>Live options after the pause; side-start effects can change a hand selection.</summary>
    internal IReadOnlyList<CardModel> LiveOptions { get; set; } = Options;

    internal bool PromotedSurface { get; set; }

    internal Func<CardModel, bool>? Filter { get; init; }

    internal bool RequiresSurfaceNow => RequiresSurface || PromotedSurface;

    public bool IsPending => Completion?.IsCompleted != true;
}

internal sealed record NativeChoiceTrace(
    long Order,
    long Sequence,
    string Owner,
    NativeChoiceSurfaceKind Surface,
    string Stage,
    int Turn,
    long OccurredAtMilliseconds);

internal class NativeChoicePlanMismatchException(string message)
    : InvalidOperationException(message);

internal class NativeChoiceSurfaceMismatchException(string message)
    : InvalidOperationException(message);

internal sealed class NativeChoiceSurfaceTimeoutException(string message)
    : NativeChoiceSurfaceMismatchException(message);

internal sealed class NativeChoicePlanNotRequestedException(string message)
    : NativeChoicePlanMismatchException(message);

internal static class NativeChoiceRuntime
{
    private static readonly MethodInfo CancelHandSelectionMethod = AccessTools.Method(
        typeof(NPlayerHand),
        "CancelHandSelectionIfNecessary")
        ?? throw new MissingMethodException(typeof(NPlayerHand).FullName, "CancelHandSelectionIfNecessary");
    private static readonly List<NativeChoiceTrace> Traces = [];
    private static readonly object TraceSync = new();
    private static NativeChoiceSession? _session;
    private static long _nextSequence;
    private static long _nextTraceOrder;

    internal static NativeChoiceSession Begin(CombatState combat, Player player, string owner)
    {
        if (_session != null)
            throw new InvalidOperationException("上一项可见选牌会话尚未释放。");
        NativeChoiceSession session = new(combat, player, owner);
        _session = session;
        return session;
    }

    internal static NativeChoiceRequest? Observe(
        NativeChoiceSurfaceKind surface,
        Player player,
        IReadOnlyList<CardModel> options,
        int minSelect,
        int maxSelect,
        bool requiresSurface,
        bool canSkip = false,
        bool requireManualConfirmation = false,
        string sourceId = "",
        Func<CardModel, bool>? filter = null)
    {
        try
        {
            return ObserveCore(surface, player, options, minSelect, maxSelect, requiresSurface,
                canSkip, requireManualConfirmation, sourceId, filter);
        }
        catch (Exception error)
        {
            NativeCallbackGuard.Report("NativeChoiceObserve", error);
            return null;
        }
    }

    private static NativeChoiceRequest? ObserveCore(
        NativeChoiceSurfaceKind surface,
        Player player,
        IReadOnlyList<CardModel> options,
        int minSelect,
        int maxSelect,
        bool requiresSurface,
        bool canSkip,
        bool requireManualConfirmation,
        string sourceId,
        Func<CardModel, bool>? filter)
    {
        if (MegaCrit.Sts2.Core.Runs.RunManager.Instance.CardSelectorStack.GetSelector(player) != null || MegaCrit.Sts2.Core.Runs.RunManager.Instance.CardSelectorStack.LocalSelector != null)
            return null;
        NativeChoiceSession? session = _session;
        if (session == null
            || !ReferenceEquals(session.Player, player)
            || !ReferenceEquals(session.Combat, CombatManager.Instance.DebugOnlyGetState()))
        {
            return null;
        }
        CardModel[] observedOptions = options.ToArray();
        NativeChoiceObservedOption[]? observedIdentities = requiresSurface
            ? null
            : observedOptions
                .Select(static option => new NativeChoiceObservedOption(
                    option.Id.Entry, option.CurrentUpgradeLevel))
                .ToArray();
        int optionCount = observedOptions.Length;
        NativeChoiceRequest request = new(
            Interlocked.Increment(ref _nextSequence),
            surface,
            player,
            observedOptions,
            observedIdentities,
            Math.Min(minSelect, optionCount),
            Math.Min(maxSelect, optionCount),
            requiresSurface,
            canSkip,
            requireManualConfirmation,
            sourceId)
        {
            Filter = filter,
        };
        session.Enqueue(request);
        return request;
    }

    internal static void End(NativeChoiceSession session)
    {
        if (ReferenceEquals(_session, session))
            _session = null;
    }

    internal static bool IsActiveSession(NativeChoiceSession session)
        => ReferenceEquals(_session, session);

    internal static bool IsActiveCombat(CombatState combat)
        => _session is { } session && ReferenceEquals(session.Combat, combat);

    internal static void CancelHandSelectionForExit(NPlayerHand hand)
        => CancelHandSelectionMethod.Invoke(hand, null);

    internal static bool CancelActiveHandSelectionForSceneExit()
    {
        if (_session is not { } session
            || NPlayerHand.Instance is not { IsInCardSelection: true } hand
            || !hand.IsInsideTree())
        {
            return false;
        }
        session.ReleaseVisibleSurface();
        CancelHandSelectionForExit(hand);
        ModLog.Info("[Sts2Solver] NATIVE_CHOICE_CANCELED reason=scene_exit surface=Hand");
        return true;
    }

    internal static void RecordTrace(NativeChoiceSession session, NativeChoiceRequest request,
        string stage)
    {
        int turn = session.Player.PlayerCombatState?.TurnNumber ?? -1;
        lock (TraceSync)
        {
            Traces.Add(new NativeChoiceTrace(
                ++_nextTraceOrder,
                request.Sequence,
                session.Owner,
                request.Surface,
                stage,
                turn,
                Environment.TickCount64));
        }
    }

    internal static void ResetTraceForTesting()
    {
        lock (TraceSync)
        {
            Traces.Clear();
            _nextTraceOrder = 0;
        }
    }

    internal static IReadOnlyList<NativeChoiceTrace> TraceSnapshotForTesting
    {
        get
        {
            lock (TraceSync)
                return Traces.ToArray();
        }
    }
}

internal sealed class NativeChoiceSession : IDisposable
{
    private readonly Channel<NativeChoiceRequest> _requests = Channel.CreateUnbounded<NativeChoiceRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<NativeChoiceRequest> _firstVisibleRequest = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allPlansConsumed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<long> _visibleTraceSequences = [];
    private readonly List<NativeChoiceRequest> _implicitRequests = [];
    private NativeChoiceSurfaceLock? _surfaceLock;
    private CancellationTokenSource? _driverCancellation;
    private IReadOnlyList<PlannedChoice>? _plans;
    private Task? _driver;
#if STS2_SOLVER_TESTING
    private TaskCompletionSource? _testDriveGate;
#endif
    private bool _producerCompleted;
    private bool _detached;
    private bool _disposed;

    internal NativeChoiceSession(CombatState combat, Player player, string owner)
    {
        Combat = combat;
        Player = player;
        Owner = owner;
    }

    internal CombatState Combat { get; }
    internal Player Player { get; }
    internal string Owner { get; }
    internal bool HasVisibleRequest => _firstVisibleRequest.Task.IsCompletedSuccessfully;
    internal long FirstVisibleSequence => HasVisibleRequest ? _firstVisibleRequest.Task.Result.Sequence : 0;
    internal long LatestVisibleSequence { get; private set; }

    internal bool IsVisibleSurfaceOpen
        => HasVisibleRequest && NativeChoiceSurface.IsVisible(_firstVisibleRequest.Task.Result.Surface);

    internal bool IsVisibleChoicePending
    {
        get
        {
            if (!HasVisibleRequest || !_firstVisibleRequest.Task.Result.IsPending)
                return false;
            NativeChoiceSurfaceKind surface = _firstVisibleRequest.Task.Result.Surface;
            return surface is NativeChoiceSurfaceKind.Hand or NativeChoiceSurfaceKind.HandUpgrade
                ? NPlayerHand.Instance?.IsInCardSelection == true
                : NativeChoiceSurface.IsVisible(surface);
        }
    }

    internal Task? DriverTask => _driver;

    internal bool DriverFaulted => _driver is { IsFaulted: true };

    internal bool BlockerActive => _surfaceLock != null;

    internal int BlockerAcquisitionCount { get; private set; }

    internal NativeChoiceRequest? FirstVisibleRequest
        => HasVisibleRequest ? _firstVisibleRequest.Task.Result : null;

    internal void Enqueue(NativeChoiceRequest request)
    {
        if (_producerCompleted)
            throw new InvalidOperationException($"原生选牌会话 {Owner} 完成后又收到选择请求。");
        if (!_requests.Writer.TryWrite(request))
            throw new InvalidOperationException($"原生选牌会话 {Owner} 无法记录选择请求。");
        if (request.RequiresSurface)
        {
            LatestVisibleSequence = request.Sequence;
            _firstVisibleRequest.TrySetResult(request);
        }
        else
        {
            _implicitRequests.Add(request);
        }
        NativeChoiceRuntime.RecordTrace(this, request, "Requested");
        ModLog.Info(
            $"[Sts2Solver] NATIVE_CHOICE_REQUEST owner={Owner} sequence={request.Sequence} " +
            $"surface={request.Surface} visible={request.RequiresSurface} source={request.SourceId} " +
            $"options={request.LiveOptions.Count} select={request.MinSelect}..{request.MaxSelect} " +
            $"manual_confirmation={request.RequireManualConfirmation}");
    }

    internal async Task<bool> WaitForFirstVisibleSurfaceAsync(NGame host, Task phaseTask,
        CancellationToken token)
    {
        while (!HasVisibleRequest)
        {
            token.ThrowIfCancellationRequested();
            if (TryPromoteImplicitSurface())
                break;
            if (phaseTask.IsCompleted)
            {
                await phaseTask.WaitAsync(token);
                return false;
            }
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        NativeChoiceRequest request = await _firstVisibleRequest.Task.WaitAsync(token);
        NativeChoiceSurfaceWaitBudget waitBudget = new(Environment.TickCount64);
        NativeChoiceSurfaceState previousState = NativeChoiceSurfaceState.Missing;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (phaseTask.IsCompleted)
                return false;
            NativeChoiceSurfaceState state = NativeChoiceSurface.GetState(request.Surface, out _);
            if (state == NativeChoiceSurfaceState.ExpectedVisible)
                break;
            if (state != previousState)
            {
                ModLog.Info(
                    $"[Sts2Solver] NATIVE_CHOICE_SURFACE owner={Owner} sequence={request.Sequence} " +
                    $"surface={request.Surface} state={state}");
                previousState = state;
            }
            if (waitBudget.IsExpired(state, Environment.TickCount64))
            {
                throw new NativeChoiceSurfaceTimeoutException(
                    $"30 秒内没有出现原生选牌页面 {request.Surface}（来源 {request.SourceId}）。");
            }
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (phaseTask.IsCompleted || !IsVisibleChoicePending)
            return false;
        // CardSelectCmd.FromHand builds its candidate list after SignalPlayerChoiceBegun, so the
        // hand observed by the Harmony prefix can be stale when a side-start effect (Crossbow) adds
        // a card during the pause. Refresh the live candidates from the opened page.
        RefreshHandOptions(request);
        RecordVisibleOnce(request);
        ModLog.Info(
            $"[Sts2Solver] NATIVE_CHOICE_VISIBLE owner={Owner} sequence={request.Sequence} " +
            $"surface={request.Surface}");
        return true;
    }

    private void RefreshHandOptions(NativeChoiceRequest request)
    {
        if (request.Surface is not (NativeChoiceSurfaceKind.Hand or NativeChoiceSurfaceKind.HandUpgrade))
            return;
        IReadOnlyList<CardModel> live = request.Surface == NativeChoiceSurfaceKind.Hand
            ? HandCards(request)
            : HandUpgradeCards(request);
        int observed = request.LiveOptions.Count;
        request.LiveOptions = live;
        if (live.Count == observed)
            return;
        ModLog.Info(
            $"[Sts2Solver] NATIVE_CHOICE_OPTIONS_REFRESHED owner={Owner} " +
            $"sequence={request.Sequence} surface={request.Surface} " +
            $"observed={observed} live={live.Count} " +
            $"cards={string.Join(',', live.Select(CardName))}");
    }

    internal void RecordSearchStarted()
    {
        NativeChoiceRequest request = HasVisibleRequest
            ? _firstVisibleRequest.Task.Result
            : throw new InvalidOperationException($"原生选牌会话 {Owner} 尚未显示页面。");
        NativeChoiceRuntime.RecordTrace(this, request, "SearchStarted");
    }

    internal void RecordPlanReady()
    {
        NativeChoiceRequest request = HasVisibleRequest
            ? _firstVisibleRequest.Task.Result
            : throw new InvalidOperationException($"原生选牌会话 {Owner} 尚未显示页面。");
        NativeChoiceRuntime.RecordTrace(this, request, "PlanReady");
    }

    internal void SetPlanAndStartDriving(NGame host, IReadOnlyList<PlannedChoice> plans,
        CancellationToken token)
    {
        if (_plans != null)
            throw new InvalidOperationException($"原生选牌会话 {Owner} 已经安装计划。");
        _plans = plans;
        if (plans.Count == 0)
            _allPlansConsumed.TrySetResult();
#if STS2_SOLVER_TESTING
        if (CombatControllerTestHooks.HoldTurnSetupDriverForTest)
            _testDriveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
#endif
        _driverCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _driver = DriveAsync(host, _driverCancellation.Token);
    }

    internal void StopDriving()
    {
        _driverCancellation?.Cancel();
        ReleaseVisibleSurface();
    }

    internal async Task CompleteAsync()
    {
        if (_producerCompleted)
            return;
        _producerCompleted = true;
        _requests.Writer.Complete();
        if (_driver == null)
            return;
        try
        {
            await _driver.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void ReleaseVisibleSurface()
    {
        _surfaceLock?.Dispose();
        _surfaceLock = null;
    }

    internal async Task SelectVisibleCardsForTesting(NGame host, IReadOnlyList<CardModel> selected,
        CancellationToken token)
    {
#if STS2_SOLVER_TESTING
        NativeChoiceRequest request = await _firstVisibleRequest.Task.WaitAsync(token);
        NativeChoiceSurfaceLock surfaceLock = await NativeChoiceSurface.WaitAndLockAsync(
            host, request, token);
        using (surfaceLock)
            await NativeChoiceSurface.SelectAsync(host, surfaceLock, request, selected, token);
        NativeChoiceRuntime.RecordTrace(this, request, "ManualSelected");
#else
        await Task.CompletedTask;
        throw new InvalidOperationException("正式版不提供选牌测试入口。");
#endif
    }

    internal async Task SelectPlannedOrDifferentVisibleCardsForTesting(NGame host,
        PlannedChoice plan, bool different, CancellationToken token)
    {
#if STS2_SOLVER_TESTING
        NativeChoiceRequest request = await _firstVisibleRequest.Task.WaitAsync(token);
        using NativeChoiceSurfaceLock surfaceLock = await NativeChoiceSurface.WaitAndLockAsync(
            host, request, token);
        IReadOnlyList<CardModel> planned = NativePlanMatcher.Resolve(plan, request, Player);
        IReadOnlyList<CardModel> selected = planned;
        if (different)
        {
            if (planned.Count == 0)
                throw new InvalidOperationException("选牌失配夹具需要计划删牌。");
            CardModel alternative = request.LiveOptions.FirstOrDefault(option => !planned.Contains(option))
                ?? throw new InvalidOperationException("选牌失配夹具没有可选的另一张牌。");
            selected = [alternative, .. planned.Skip(1)];
        }
        await NativeChoiceSurface.SelectAsync(host, surfaceLock, request, selected, token);
        NativeChoiceRuntime.RecordTrace(this, request, "ManualSelected");
#else
        await Task.CompletedTask;
        throw new InvalidOperationException("正式版不提供选牌测试入口。");
#endif
    }

    private async Task DriveAsync(NGame host, CancellationToken token)
    {
#if STS2_SOLVER_TESTING
        if (_testDriveGate is { } gate)
            await gate.Task.WaitAsync(token);
#endif
        IReadOnlyList<PlannedChoice> plans = _plans
            ?? throw new InvalidOperationException($"原生选牌会话 {Owner} 缺少计划。");
        int planIndex = 0;
        await foreach (NativeChoiceRequest request in _requests.Reader.ReadAllAsync(token))
        {
            if (request.LiveOptions.Count == 0
                && request.MinSelect == 0
                && request.MaxSelect == 0)
            {
                bool consumedEmptyPlan = planIndex < plans.Count
                    && !plans[planIndex].SelectedIndexes().Any();
                if (consumedEmptyPlan)
                    planIndex++;
                ModLog.Info(
                    $"[Sts2Solver] NATIVE_CHOICE_NO_OP owner={Owner} sequence={request.Sequence} " +
                    $"surface={request.Surface} source={request.SourceId} " +
                    $"consumed_empty_plan={consumedEmptyPlan}");
                NativeChoiceRuntime.RecordTrace(this, request, "NoOp");
                if (planIndex == plans.Count)
                    _allPlansConsumed.TrySetResult();
                continue;
            }

            if (planIndex >= plans.Count)
            {
                throw new NativeChoicePlanNotRequestedException(
                    $"原生选牌会话 {Owner} 收到计划外选择：{request.SourceId}/{request.Surface}。");
            }
            PlannedChoice plan = plans[planIndex];
            IReadOnlyList<CardModel> selected;
            if (request.RequiresSurfaceNow)
            {
                using NativeChoiceSurfaceLock surfaceLock =
                    await NativeChoiceSurface.WaitAndLockAsync(host, request, token);
                _surfaceLock = surfaceLock;
                BlockerAcquisitionCount++;
                RecordVisibleOnce(request);
                try
                {
                    selected = NativePlanMatcher.Resolve(plan, request, Player);
                    token.ThrowIfCancellationRequested();
                    await NativeChoiceSurface.SelectAsync(host, surfaceLock, request, selected, token);
                }
                finally
                {
                    if (ReferenceEquals(_surfaceLock, surfaceLock))
                        _surfaceLock = null;
                }
            }
            else
            {
                selected = NativePlanMatcher.ResolveImplicit(plan, request);
                ValidateImplicitSelection(request, selected);
                ModLog.Info(
                    $"[Sts2Solver] NATIVE_CHOICE_IMPLICIT owner={Owner} sequence={request.Sequence} " +
                    $"source={request.SourceId} cards={string.Join(',', selected.Select(CardName))}");
            }
            ModLog.Info(
                $"[Sts2Solver] NATIVE_CHOICE_SELECTED owner={Owner} sequence={request.Sequence} " +
                $"surface={request.Surface} source={request.SourceId} " +
                $"selected={string.Join(',', selected.Select(CardName))}");
            NativeChoiceRuntime.RecordTrace(this, request, "Selected");
            planIndex++;
            if (planIndex == plans.Count)
                _allPlansConsumed.TrySetResult();
        }

        while (planIndex < plans.Count && !plans[planIndex].SelectedIndexes().Any())
            planIndex++;
        if (planIndex != plans.Count)
        {
            throw new NativeChoicePlanNotRequestedException(
                $"原生选牌会话 {Owner} 仍有 {plans.Count - planIndex} 个计划没有被游戏请求。");
        }
    }

    private static void ValidateImplicitSelection(NativeChoiceRequest request,
        IReadOnlyList<CardModel> selected)
    {
        if (request.LiveOptions.Count == 0)
        {
            if (selected.Count != 0)
                throw new InvalidOperationException("无候选的原生选择包含计划卡牌。");
            return;
        }
        if (!selected.SequenceEqual(request.LiveOptions))
        {
            throw new InvalidOperationException(
                $"原版按候选顺序隐式选择全部 {request.LiveOptions.Count} 张牌，计划必须保持同样的实例与顺序。");
        }
    }

    private bool TryPromoteImplicitSurface()
    {
        foreach (NativeChoiceRequest request in _implicitRequests)
        {
            if (request.RequiresSurfaceNow
                || !NativeChoiceSurface.IsVisible(request.Surface))
            {
                continue;
            }
            IReadOnlyList<CardModel> live = request.Surface switch
            {
                NativeChoiceSurfaceKind.Hand => HandCards(request),
                NativeChoiceSurfaceKind.HandUpgrade => HandUpgradeCards(request),
                _ => request.LiveOptions,
            };
            request.LiveOptions = live;
            request.PromotedSurface = true;
            LatestVisibleSequence = request.Sequence;
            _firstVisibleRequest.TrySetResult(request);
            RecordVisibleOnce(request);
            ModLog.Info(
                $"[Sts2Solver] NATIVE_CHOICE_PROMOTED owner={Owner} sequence={request.Sequence} " +
                $"surface={request.Surface} options={live.Count} " +
                $"cards={string.Join(',', live.Select(static card => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}"))}");
            return true;
        }
        return false;
    }

    private static IReadOnlyList<CardModel> HandCards(NativeChoiceRequest request)
    {
        PlayerCombatState state = request.Player.PlayerCombatState
            ?? throw new InvalidOperationException("原生手牌选择缺少战斗牌堆。");
        Func<CardModel, bool> filter = request.Filter ?? (static _ => true);
        return [.. state.Hand.Cards.Where(filter)];
    }

    private static IReadOnlyList<CardModel> HandUpgradeCards(NativeChoiceRequest request)
    {
        PlayerCombatState state = request.Player.PlayerCombatState
            ?? throw new InvalidOperationException("原生手牌升级选择缺少战斗牌堆。");
        return [.. state.Hand.Cards.Where(static card => card.IsUpgradable)];
    }

    private void RecordVisibleOnce(NativeChoiceRequest request)
    {
        if (_visibleTraceSequences.Add(request.Sequence))
            NativeChoiceRuntime.RecordTrace(this, request, "Visible");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _driverCancellation?.Cancel();
        _driverCancellation?.Dispose();
        _driverCancellation = null;
        if (!_producerCompleted)
            _requests.Writer.TryComplete();
        ReleaseVisibleSurface();
        Detach();
    }

    private void Detach()
    {
        if (_detached)
            return;
        NativeChoiceRuntime.End(this);
        _detached = true;
    }

    private static string CardName(CardModel card) => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}";
}

internal static class NativePlanMatcher
{
    private readonly record struct Identity(string Id, int Upgrade);

    internal static IReadOnlyList<CardModel> Resolve(PlannedChoice plan,
        NativeChoiceRequest request, Player player)
    {
        if (request.LiveOptions.Count != plan.OptionCount)
        {
            throw new NativeChoicePlanMismatchException(
                $"{Describe(plan)} 选项数 {request.LiveOptions.Count} 与计划 {plan.OptionCount} 不符。");
        }
        Card[] predicted = plan.OptionsAreGenerated ? plan.GeneratedOptions : plan.PileOptions;
        if (!plan.OptionsAreGenerated && !plan.Request.SourceIsNone)
        {
            PlayerCombatState state = player.PlayerCombatState
                ?? throw new NativeChoicePlanMismatchException("原生选牌玩家没有战斗牌堆。");
            IReadOnlyList<CardModel> source = PileCards(state, plan.Request.Source);
            for (int index = 0; index < request.LiveOptions.Count; index++)
            {
                if (!ContainsReference(source, request.LiveOptions[index]))
                {
                    throw new NativeChoicePlanMismatchException(
                        $"{Describe(plan)} 选项 {request.LiveOptions[index].Id.Entry} 不在计划的牌堆 " +
                        $"{plan.Request.Source} 中。");
                }
            }
        }
        bool[] used = new bool[plan.OptionCount];
        int[] map = new int[plan.OptionCount];
        for (int option = 0; option < plan.OptionCount; option++)
        {
            Identity key = IdentityOf(predicted[option]);
            int match = -1;
            for (int candidate = 0; candidate < request.LiveOptions.Count; candidate++)
            {
                if (!used[candidate] && IdentityOf(request.LiveOptions[candidate]) == key)
                {
                    match = candidate;
                    break;
                }
            }
            if (match < 0)
            {
                throw new NativeChoicePlanMismatchException(
                    $"{Describe(plan)} 找不到选项 {option} 对应的原生卡 {key}。" +
                    $"native=[{string.Join(',', request.LiveOptions.Select(CardName))}]");
            }
            used[match] = true;
            map[option] = match;
        }
        List<int> selectedIndexes = [.. plan.SelectedIndexes()];
        if (selectedIndexes.Count < request.MinSelect || selectedIndexes.Count > request.MaxSelect)
        {
            throw new NativeChoicePlanMismatchException(
                $"{Describe(plan)} 计划选择 {selectedIndexes.Count} 张，原生界面要求 " +
                $"{request.MinSelect}..{request.MaxSelect} 张。");
        }
        List<CardModel> selected = new(selectedIndexes.Count);
        foreach (int option in selectedIndexes)
            selected.Add(request.LiveOptions[map[option]]);
        return selected;
    }

    internal static IReadOnlyList<CardModel> ResolveImplicit(PlannedChoice plan,
        NativeChoiceRequest request)
    {
        if (request.ObservedOptions == null)
            throw new InvalidOperationException("隐式原生选牌请求缺少冻结候选身份。");
        if (request.LiveOptions.Count != plan.OptionCount
            || request.ObservedOptions.Count != plan.OptionCount)
        {
            throw new NativeChoicePlanMismatchException(
                $"{Describe(plan)} 隐式选项数 {request.LiveOptions.Count} 与计划 {plan.OptionCount} 不符。");
        }
        Card[] predicted = plan.OptionsAreGenerated ? plan.GeneratedOptions : plan.PileOptions;
        for (int index = 0; index < plan.OptionCount; index++)
        {
            Identity expected = IdentityOf(predicted[index]);
            NativeChoiceObservedOption observed = request.ObservedOptions[index];
            if (expected.Id != observed.CardId || expected.Upgrade != observed.UpgradeLevel)
            {
                throw new NativeChoicePlanMismatchException(
                    $"{Describe(plan)} 隐式候选 {index} 身份 {expected} 与观测 " +
                    $"{observed.CardId}+{observed.UpgradeLevel} 不符。");
            }
        }
        if (!plan.SelectsAll())
            throw new NativeChoicePlanMismatchException($"{Describe(plan)} 隐式选择没有选中全部候选。");
        return request.LiveOptions;
    }

    private static Identity IdentityOf(Card card)
        => new(Registry.Card(card.Id).NativeId, card.Upgrade);

    private static Identity IdentityOf(CardModel card)
        => new(card.Id.Entry, card.CurrentUpgradeLevel);

    private static IReadOnlyList<CardModel> PileCards(PlayerCombatState state, Pile pile) => pile switch
    {
        Pile.Hand => state.Hand.Cards,
        Pile.Draw => state.DrawPile.Cards,
        Pile.Discard => state.DiscardPile.Cards,
        Pile.Exhaust => state.ExhaustPile.Cards,
        Pile.Play => state.PlayPile.Cards,
        _ => throw new NativeChoicePlanMismatchException($"选择来源牌堆 {pile} 无效。"),
    };

    private static bool ContainsReference(IReadOnlyList<CardModel> cards, CardModel card)
    {
        for (int index = 0; index < cards.Count; index++)
        {
            if (ReferenceEquals(cards[index], card))
                return true;
        }
        return false;
    }

    private static string CardName(CardModel card) => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}";

    private static string Describe(PlannedChoice choice)
        => $"{choice.Request.Kind}/{choice.Request.Effect} source={choice.Request.Source} " +
            $"count={choice.OptionCount} bounds={choice.Request.MinCount}..{choice.Request.MaxCount} " +
            $"selected=0x{choice.Selection:X}";
}

internal sealed class NativeChoiceSurfaceLock(
    NativeChoiceRequest request,
    Node surface,
    Control blocker,
    NPlayerHand? hand,
    bool handUnhandledInput) : IDisposable
{
    private bool _disposed;

    internal NativeChoiceRequest Request { get; } = request;
    internal Node Surface { get; } = surface;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (GodotObject.IsInstanceValid(blocker))
        {
            blocker.GetParent()?.RemoveChild(blocker);
            blocker.QueueFree();
        }
        if (hand != null && GodotObject.IsInstanceValid(hand))
            hand.SetProcessUnhandledInput(handUnhandledInput);
    }
}

internal static class NativeChoiceSurface
{
    private const long SurfaceTimeoutMilliseconds = 30_000;
    private const ulong ChooseCardMinimumOpenMilliseconds = 350;
    private const ulong OtherSurfaceMinimumOpenMilliseconds = 250;
    private const ulong MultiSelectStepMilliseconds = 150;
    private static readonly FieldInfo ChooseCardOptionsField = AccessTools.Field(
        typeof(NChooseACardSelectionScreen), "_cards")
        ?? throw new MissingFieldException(typeof(NChooseACardSelectionScreen).FullName, "_cards");
    private static readonly FieldInfo ChooseCardOpenedTicksField = AccessTools.Field(
        typeof(NChooseACardSelectionScreen), "_openedTicks")
        ?? throw new MissingFieldException(typeof(NChooseACardSelectionScreen).FullName, "_openedTicks");
    private static readonly FieldInfo ChooseCardCompletionField = AccessTools.Field(
        typeof(NChooseACardSelectionScreen), "_completionSource")
        ?? throw new MissingFieldException(typeof(NChooseACardSelectionScreen).FullName, "_completionSource");

    internal static bool IsVisible(NativeChoiceSurfaceKind kind)
        => GetState(kind, out _) == NativeChoiceSurfaceState.ExpectedVisible;

    internal static NativeChoiceSurfaceState GetState(NativeChoiceSurfaceKind kind, out Node? surface)
    {
        Node? overlayTop = NOverlayStack.Instance?.Peek() as Node;
        surface = FindSurface(kind);
        bool isHand = kind is NativeChoiceSurfaceKind.Hand or NativeChoiceSurfaceKind.HandUpgrade;
        bool expectedVisible = surface is CanvasItem canvas
            && canvas.IsInsideTree()
            && canvas.IsVisibleInTree()
            && (!isHand || overlayTop == null);
        bool covered = overlayTop != null
            && (!ReferenceEquals(overlayTop, surface) || !expectedVisible);
        return Classify(expectedVisible, covered);
    }

    private static NativeChoiceSurfaceState Classify(bool expectedVisible, bool covered)
        => expectedVisible
            ? NativeChoiceSurfaceState.ExpectedVisible
            : covered
                ? NativeChoiceSurfaceState.CoveredByOtherOverlay
                : NativeChoiceSurfaceState.Missing;

    internal static async Task<NativeChoiceSurfaceLock> WaitAndLockAsync(NGame host,
        NativeChoiceRequest request, CancellationToken token)
    {
        NativeChoiceSurfaceWaitBudget waitBudget = new(Environment.TickCount64);
        NativeChoiceSurfaceState previousState = NativeChoiceSurfaceState.Missing;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            NativeChoiceSurfaceState state = GetState(request.Surface, out Node? surface);
            if (state == NativeChoiceSurfaceState.ExpectedVisible && surface != null)
            {
                Control blocker = new()
                {
                    Name = $"Sts2SolverChoiceBlocker{request.Sequence}",
                    MouseFilter = Control.MouseFilterEnum.Stop,
                    FocusMode = Control.FocusModeEnum.All,
                };
                surface.AddChild(blocker);
                blocker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                blocker.MoveToFront();
                NPlayerHand? hand = surface as NPlayerHand;
                bool handUnhandledInput = hand?.IsProcessingUnhandledInput() ?? false;
                hand?.SetProcessUnhandledInput(false);
                blocker.CallDeferred(Control.MethodName.GrabFocus);
                return new NativeChoiceSurfaceLock(request, surface, blocker, hand, handUnhandledInput);
            }
            if (state != previousState)
            {
                ModLog.Info(
                    $"[Sts2Solver] NATIVE_CHOICE_SURFACE sequence={request.Sequence} " +
                    $"surface={request.Surface} state={state}");
                previousState = state;
            }
            if (waitBudget.IsExpired(state, Environment.TickCount64))
            {
                throw new NativeChoiceSurfaceTimeoutException(
                    $"30 秒内没有出现原生选牌页面 {request.Surface}（来源 {request.SourceId}）。");
            }
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    internal static async Task SelectAsync(NGame host, NativeChoiceSurfaceLock surfaceLock,
        NativeChoiceRequest request, IReadOnlyList<CardModel> selected, CancellationToken token)
    {
        if (request.Surface == NativeChoiceSurfaceKind.ChooseCard)
        {
            await SelectChooseCardAsync(host, surfaceLock.Surface, request, selected, token);
            return;
        }
        ulong openedAt = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - openedAt < OtherSurfaceMinimumOpenMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        switch (request.Surface)
        {
            case NativeChoiceSurfaceKind.SimpleGrid:
            case NativeChoiceSurfaceKind.CombatPile:
                await SelectGridAsync(host, surfaceLock.Surface, request, selected, token);
                break;
            case NativeChoiceSurfaceKind.Hand:
            case NativeChoiceSurfaceKind.HandUpgrade:
                await SelectHandAsync(host, surfaceLock.Surface, request, selected, token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Surface), request.Surface, null);
        }
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static async Task SelectChooseCardAsync(NGame host, Node surface,
        NativeChoiceRequest request, IReadOnlyList<CardModel> selected, CancellationToken token)
    {
        var screen = (NChooseACardSelectionScreen)surface;
        var options = (IReadOnlyList<CardModel>)ChooseCardOptionsField.GetValue(screen)!;
        if (!options.SequenceEqual(request.LiveOptions, ReferenceEqualityComparer.Instance))
            throw new NativeChoiceSurfaceMismatchException("原生三选一页面的候选实例与当前选择请求不一致。");
        var completion = ((TaskCompletionSource<IEnumerable<CardModel>>)ChooseCardCompletionField
            .GetValue(screen)!).Task;
        long started = Environment.TickCount64;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (GetState(request.Surface, out Node? current) != NativeChoiceSurfaceState.ExpectedVisible
                || !ReferenceEquals(current, screen))
            {
                throw new NativeChoiceSurfaceMismatchException("原生三选一页面在计划提交前发生变化。");
            }
            ulong openedAt = (ulong)ChooseCardOpenedTicksField.GetValue(screen)!;
            if (Time.GetTicksMsec() - openedAt > ChooseCardMinimumOpenMilliseconds)
                break;
            if (Environment.TickCount64 - started >= SurfaceTimeoutMilliseconds)
                throw new NativeChoiceSurfaceTimeoutException("原生三选一页面在 30 秒内仍未完成开启。");
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        token.ThrowIfCancellationRequested();
        if (selected.Count == 0)
        {
            if (!request.CanSkip)
                throw new InvalidOperationException("原生三选一页面不可跳过，但计划没有选择卡牌。");
            NChoiceSelectionSkipButton skip = surface.GetNode<NChoiceSelectionSkipButton>("SkipButton");
            skip.EmitSignal(NClickableControl.SignalName.Released, skip);
        }
        else
        {
            if (selected.Count != 1)
                throw new InvalidOperationException($"原生三选一页面计划选择了 {selected.Count} 张牌。");
            NCardHolder holder = Descendants<NCardHolder>(surface)
                .FirstOrDefault(candidate => ReferenceEquals(candidate.CardModel, selected[0]))
                ?? throw new NativeChoicePlanMismatchException(
                    $"原生三选一页面没有 {selected[0].Id.Entry} 的卡牌节点。");
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        }
        if (!completion.IsCompleted)
            throw new NativeChoiceSurfaceMismatchException("原生三选一页面未接受计划选择。");
        if (!(await completion).SequenceEqual(selected, ReferenceEqualityComparer.Instance))
            throw new NativeChoicePlanMismatchException("原生三选一页面接受的卡牌实例与计划不一致。");
    }

    private static async Task SelectGridAsync(NGame host, Node surface,
        NativeChoiceRequest request, IReadOnlyList<CardModel> selected, CancellationToken token)
    {
        NCardGrid grid = Descendants<NCardGrid>(surface).Single();
        var highlighted = (IEnumerable<CardModel>)AccessTools.Field(surface.GetType(), "_selectedCards")
            .GetValue(surface)!;
        foreach (CardModel card in highlighted.ToArray())
        {
            token.ThrowIfCancellationRequested();
            NGridCardHolder holder = await FindGridCardHolderAsync(host, grid, request, card, token);
            grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
        }
        foreach (CardModel card in selected)
        {
            token.ThrowIfCancellationRequested();
            NGridCardHolder holder = await FindGridCardHolderAsync(host, grid, request, card, token);
            grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
            token.ThrowIfCancellationRequested();
            await WaitMillisecondsAsync(host, MultiSelectStepMilliseconds, token);
        }
        if (request.RequireManualConfirmation)
        {
            NConfirmButton confirm = surface.GetNode<NConfirmButton>("%Confirm");
            await WaitForConfirmationAsync(
                host,
                confirm,
                () => surface.IsInsideTree()
                    && surface is CanvasItem canvas
                    && canvas.IsVisibleInTree(),
                "原生网格页面在选择完成后关闭，确认计划无法提交。",
                "原生网格页面选择完成后确认按钮在 30 秒内仍不可用。",
                token);
            token.ThrowIfCancellationRequested();
            confirm.EmitSignal(NClickableControl.SignalName.Released, confirm);
        }
    }

    private static async Task<NGridCardHolder> FindGridCardHolderAsync(NGame host, NCardGrid grid,
        NativeChoiceRequest request, CardModel card, CancellationToken token)
    {
        NGridCardHolder? holder = grid.CurrentlyDisplayedCardHolders
            .FirstOrDefault(candidate => ReferenceEquals(candidate.CardModel, card));
        if (holder != null)
            return holder;

        float top = grid.Get(NCardGrid.PropertyName.ScrollLimitTop).AsSingle();
        float bottom = grid.Get(NCardGrid.PropertyName.ScrollLimitBottom).AsSingle();
        int sweepSteps = Math.Max(1, request.LiveOptions.Count);
        for (int step = 1; step <= sweepSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            grid.SetScrollPosition(Mathf.Lerp(top, bottom, step / (float)sweepSteps));
            grid.Call(NCardGrid.MethodName.AllocateCardHolders);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            holder = grid.CurrentlyDisplayedCardHolders
                .FirstOrDefault(candidate => ReferenceEquals(candidate.CardModel, card));
            if (holder != null)
                return holder;
        }
        throw new NativeChoicePlanMismatchException(
            $"原生网格页面滚动完整个卡组后仍没有 {card.Id.Entry} 的卡牌节点。");
    }

    private static async Task SelectHandAsync(NGame host, Node surface,
        NativeChoiceRequest request, IReadOnlyList<CardModel> selected, CancellationToken token)
    {
        NPlayerHand hand = (NPlayerHand)surface;
        // Native OnHolderPressed ignores selection input while the battlefield is being peeked at.
        hand.PeekButton.SetPeeking(false);
        var highlighted = (List<CardModel>)AccessTools.Field(typeof(NPlayerHand), "_selectedCards")
            .GetValue(hand)!;
        foreach (CardModel card in highlighted.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (request.Surface == NativeChoiceSurfaceKind.HandUpgrade)
            {
                hand.DeselectCard(NCard.Create(card)
                    ?? throw new InvalidOperationException("原生升级选择无法创建取消选择的卡牌节点。"));
            }
            else
            {
                hand.GetNode<NSelectedHandCardContainer>("%SelectedHandCardContainer")
                    .DeselectCard(card);
            }
        }
        foreach (CardModel card in selected)
        {
            token.ThrowIfCancellationRequested();
            NCardHolder holder = hand.GetCardHolder(card)
                ?? throw new NativeChoicePlanMismatchException(
                    $"原生手牌选择没有 {card.Id.Entry} 的卡牌节点。");
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            token.ThrowIfCancellationRequested();
            await WaitMillisecondsAsync(host, MultiSelectStepMilliseconds, token);
        }
        if (!hand.IsInCardSelection)
        {
            if (request.RequireManualConfirmation)
                throw new InvalidOperationException("原生手牌选择在手动确认前已经结束。");
            return;
        }
        NConfirmButton confirm = hand.GetNode<NConfirmButton>("%SelectModeConfirmButton");
        await WaitForConfirmationAsync(
            host,
            confirm,
            () => hand.IsInsideTree() && hand.IsInCardSelection,
            "原生手牌选择页面在确认前关闭，确认计划无法提交。",
            "原生手牌选择完成后确认按钮在 30 秒内仍不可用。",
            token);
        token.ThrowIfCancellationRequested();
        confirm.EmitSignal(NClickableControl.SignalName.Released, confirm);
    }

    private static async Task WaitForConfirmationAsync(NGame host, NConfirmButton confirm,
        Func<bool> surfaceIsActive, string closedMessage, string timeoutMessage,
        CancellationToken token)
    {
        long deadline = Environment.TickCount64 + SurfaceTimeoutMilliseconds;
        while (!confirm.IsEnabled)
        {
            token.ThrowIfCancellationRequested();
            if (!surfaceIsActive())
                throw new NativeChoiceSurfaceMismatchException(closedMessage);
            if (Environment.TickCount64 >= deadline)
                throw new NativeChoiceSurfaceTimeoutException(timeoutMessage);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static async Task WaitMillisecondsAsync(NGame host, ulong milliseconds,
        CancellationToken token)
    {
        ulong startedAt = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - startedAt < milliseconds)
        {
            token.ThrowIfCancellationRequested();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static Node? FindSurface(NativeChoiceSurfaceKind kind)
        => kind switch
        {
            NativeChoiceSurfaceKind.ChooseCard => NOverlayStack.Instance?.Peek()
                as NChooseACardSelectionScreen,
            NativeChoiceSurfaceKind.SimpleGrid => NOverlayStack.Instance?.Peek()
                as NSimpleCardSelectScreen,
            NativeChoiceSurfaceKind.CombatPile => NOverlayStack.Instance?.Peek()
                as NCombatPileCardSelectScreen,
            NativeChoiceSurfaceKind.Hand or NativeChoiceSurfaceKind.HandUpgrade
                when NPlayerHand.Instance?.IsInCardSelection == true => NPlayerHand.Instance,
            _ => null,
        };

    private static IEnumerable<T> Descendants<T>(Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T match)
                yield return match;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}

internal sealed class NativeChoiceSurfaceWaitBudget(long startedAt)
{
    private long _lastObservedAt = startedAt;
    private long _missingMillisecondsRemaining = 30_000;

    internal bool IsExpired(NativeChoiceSurfaceState state, long observedAt)
    {
        if (observedAt < _lastObservedAt)
            throw new ArgumentOutOfRangeException(nameof(observedAt));
        long elapsed = observedAt - _lastObservedAt;
        _lastObservedAt = observedAt;
        if (state == NativeChoiceSurfaceState.Missing)
            _missingMillisecondsRemaining -= elapsed;
        return _missingMillisecondsRemaining <= 0;
    }
}

internal sealed class ChooseCardObservationPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_native_choice_choose_card";
    public static string Description => "观测原版战斗三选一卡牌页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
                typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(bool)]),
    ];

    public static void Prefix(IReadOnlyList<CardModel> cards, Player player, bool canSkip,
        out NativeChoiceRequest? __state)
    {
        NativeChoiceRequest? observed = null;
        NativeCallbackGuard.Try("ChooseCardObservation", () =>
            observed = NativeChoiceRuntime.Observe(
                NativeChoiceSurfaceKind.ChooseCard,
                player,
                cards,
                0,
                1,
                cards.Count > 0,
                canSkip));
        __state = observed;
    }

    public static void Postfix(Task __result, NativeChoiceRequest? __state)
    {
        if (__state != null)
            __state.Completion = __result;
    }
}

internal sealed class SimpleGridObservationPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_native_choice_simple_grid";
    public static string Description => "观测原版战斗卡牌网格页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
                typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(CardSelectorPrefs)]),
    ];

    public static void Prefix(IReadOnlyList<CardModel> cardsIn, Player player,
        CardSelectorPrefs prefs, out NativeChoiceRequest? __state)
    {
        NativeChoiceRequest? observed = null;
        NativeCallbackGuard.Try("SimpleGridObservation", () =>
            observed = NativeChoiceRuntime.Observe(
                NativeChoiceSurfaceKind.SimpleGrid,
                player,
                cardsIn,
                prefs.MinSelect,
                prefs.MaxSelect,
                cardsIn.Count > 0
                    && (prefs.RequireManualConfirmation || cardsIn.Count > prefs.MinSelect),
                requireManualConfirmation: prefs.RequireManualConfirmation));
        __state = observed;
    }

    public static void Postfix(Task __result, NativeChoiceRequest? __state)
    {
        if (__state != null)
            __state.Completion = __result;
    }
}

internal sealed class RewardGridObservationPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_native_choice_reward_grid";
    public static string Description => "观测原版战斗生成牌网格页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGridForRewards),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
                typeof(List<CardCreationResult>), typeof(Player), typeof(CardSelectorPrefs)]),
    ];

    public static void Prefix(List<CardCreationResult> cards, Player player,
        CardSelectorPrefs prefs, out NativeChoiceRequest? __state)
    {
        NativeChoiceRequest? observed = null;
        NativeCallbackGuard.Try("RewardGridObservation", () =>
        {
            IReadOnlyList<CardModel> options =
                cards.Select(static result => result.Card).ToArray();
            observed = NativeChoiceRuntime.Observe(
                NativeChoiceSurfaceKind.SimpleGrid,
                player,
                options,
                prefs.MinSelect,
                prefs.MaxSelect,
                options.Count > 0
                    && (prefs.RequireManualConfirmation || options.Count > prefs.MinSelect),
                requireManualConfirmation: prefs.RequireManualConfirmation);
        });
        __state = observed;
    }

    public static void Postfix(Task __result, NativeChoiceRequest? __state)
    {
        if (__state != null)
            __state.Completion = __result;
    }
}

internal sealed class CombatPileObservationPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_native_choice_combat_pile";
    public static string Description => "观测原版战斗牌堆选择页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
                typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs),
                typeof(Func<CardModel, bool>)]),
    ];

    public static void Prefix(CardPile pile, Player player, CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter, out NativeChoiceRequest? __state)
    {
        NativeChoiceRequest? observed = null;
        NativeCallbackGuard.Try("CombatPileObservation", () =>
        {
            IReadOnlyList<CardModel> options =
                (filter == null ? pile.Cards : pile.Cards.Where(filter)).ToArray();
            observed = NativeChoiceRuntime.Observe(
                NativeChoiceSurfaceKind.CombatPile,
                player,
                options,
                prefs.MinSelect,
                prefs.MaxSelect,
                options.Count > 0
                    && (prefs.RequireManualConfirmation || options.Count > prefs.MinSelect),
                requireManualConfirmation: prefs.RequireManualConfirmation);
        });
        __state = observed;
    }

    public static void Postfix(Task __result, NativeChoiceRequest? __state)
    {
        if (__state != null)
            __state.Completion = __result;
    }
}

internal sealed class HandObservationPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_native_choice_hand";
    public static string Description => "观测原版战斗手牌选择动画";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
                typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>),
                typeof(AbstractModel)]),
    ];

    public static void Prefix(Player player, CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter, AbstractModel source, out NativeChoiceRequest? __state)
    {
        NativeChoiceRequest? observed = null;
        NativeCallbackGuard.Try("HandObservation", () =>
        {
            IReadOnlyList<CardModel> options = player.PlayerCombatState!.Hand.Cards
                .Where(filter ?? (static _ => true))
                .ToArray();
            observed = NativeChoiceRuntime.Observe(
                NativeChoiceSurfaceKind.Hand,
                player,
                options,
                prefs.MinSelect,
                prefs.MaxSelect,
                options.Count > 0
                    && (prefs.RequireManualConfirmation || options.Count > prefs.MinSelect),
                requireManualConfirmation: prefs.RequireManualConfirmation,
                sourceId: source.Id.Entry,
                filter: filter);
        });
        __state = observed;
    }

    public static void Postfix(Task __result, NativeChoiceRequest? __state)
    {
        if (__state != null)
            __state.Completion = __result;
    }
}

internal sealed class HandUpgradeObservationPatch : IPatchMethod
{
    public static string PatchId => "sts2solver_native_choice_hand_upgrade";
    public static string Description => "观测原版战斗手牌升级页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),
                typeof(Player), typeof(AbstractModel)]),
    ];

    public static void Prefix(Player player, AbstractModel source,
        out NativeChoiceRequest? __state)
    {
        NativeChoiceRequest? observed = null;
        NativeCallbackGuard.Try("HandUpgradeObservation", () =>
        {
            IReadOnlyList<CardModel> options = player.PlayerCombatState!.Hand.Cards
                .Where(static card => card.IsUpgradable)
                .ToArray();
            observed = NativeChoiceRuntime.Observe(
                NativeChoiceSurfaceKind.HandUpgrade,
                player,
                options,
                1,
                1,
                options.Count > 1,
                requireManualConfirmation: false,
                sourceId: source.Id.Entry);
        });
        __state = observed;
    }

    public static void Postfix(Task __result, NativeChoiceRequest? __state)
    {
        if (__state != null)
            __state.Completion = __result;
    }
}
