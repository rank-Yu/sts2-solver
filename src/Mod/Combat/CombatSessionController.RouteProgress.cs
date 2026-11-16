// Shared route progress for manual play, resumed deployment and optional in-game guidance.
// The cursor advances only after the expected native action and its choices settle.
using NativeTargetType = MegaCrit.Sts2.Core.Entities.Cards.TargetType;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    private readonly record struct RouteObservation(
        CombatControllerResult? Source, bool Playable, bool ActionsIdle, int Turn, uint Potions);

    private sealed record RouteSelection(NativeChoiceRequest Request, CardModel[] Cards);

    private static ActionExecutor? _routeExecutor;
    private static RouteObservation _routeObservation;
    private static CombatControllerResult? _routeSource;
    private static CombatControllerResult? _rejectedRoute;
    private static int _routeIndex = -1;
    private static GuideStepUi? _guideUi;
    private static GameAction? _manualRouteAction;
    private static PlannedChoice[] _manualRouteChoices = [];
    private static int _manualRouteChoiceIndex;
    private static RouteSelection? _manualRouteSelection;

#if STS2_SOLVER_TESTING
    internal static CombatRoutePlan? RetainedPlanForTest => _latestResult?.Plan;
    internal static int RouteIndexForTest => _routeIndex;
    internal static NativeChoiceRequest? ManualRouteChoiceForTest => _manualRouteSelection?.Request;
    internal static IReadOnlyList<CardModel>? ManualRouteChoiceCardsForTest => _manualRouteSelection?.Cards;
#endif

    private static void WatchRouteActions()
    {
        _routeExecutor = RunManager.Instance.ActionExecutor;
        _routeExecutor.BeforeActionExecuted += OnManualRouteActionStarted;
    }

    private static void ResetRouteProgress()
    {
        if (_routeExecutor != null)
            _routeExecutor.BeforeActionExecuted -= OnManualRouteActionStarted;
        _routeExecutor = null;
        _routeObservation = default;
        _rejectedRoute = null;
        ClearRouteProgress();
    }

    private static CombatControllerResult? RetainedRoute()
        => _combatActive && _latestResult?.StartTurn == CurrentTurn()
            && _search == null && !HasBlockingDeployment
            && !PlayerTurnSetupRuntime.HasActivePage && !ReferenceEquals(_latestResult, _rejectedRoute)
            ? _latestResult
            : null;

    private static GuideStepUi? CurrentGuide()
    {
        if (!SolverSettings.Current.Ui.ShowGuide || _autoExecute || (_runCombat && !_runPaused)
            || _guideUi == null || !ReferenceEquals(RetainedRoute(), _routeSource))
            return null;
        if (_manualRouteAction != null)
            return _manualRouteSelection?.Request is { IsPending: true } ? _guideUi : null;
        return _routeObservation is { Playable: true, ActionsIdle: true } ? _guideUi : null;
    }

    private static bool RefreshRouteProgress(bool playable, bool actionsIdle, int turn)
    {
        if (!ReferenceEquals(_rejectedRoute, _latestResult))
            _rejectedRoute = null;
        CombatControllerResult? source = RetainedRoute();
        // A native action paused for a player choice can temporarily leave the queue idle.
        bool settled = actionsIdle && _manualRouteAction?.CompletionTask.IsCompleted != false
            && _manualRouteSelection?.Request.IsPending != true;
        uint potions = 0;
        if (source != null && playable && settled)
        {
            // Discarding a potion can change the boundary without a combat action.
            for (int slot = 0; slot < _player!.PotionSlots.Count; slot++)
                if (_player.GetPotionAtSlotIndex(slot) != null)
                    potions |= 1u << slot;
        }
        RouteObservation observation = new(source, playable, settled, turn, potions);
        if (observation != _routeObservation)
        {
            _routeObservation = observation;
            if (source == null || !ReferenceEquals(source, _routeSource))
                ClearRouteProgress();
            if (source != null && playable && settled)
                MatchRouteBoundary(source, turn);
        }
        bool ready = source != null && ReferenceEquals(source, _routeSource)
            && _routeIndex >= 0 && playable && settled;
        return !ReferenceEquals(CurrentGuide(), CombatUiStore.Snapshot.Guide)
            || (!PlayerTurnSetupRuntime.HasActivePage
                && ready != CombatUiStore.Snapshot.CurrentTurnRouteReady)
            || (ready && CombatUiStore.Snapshot.Route?.CompletedActions != _routeIndex);
    }

    private static void MatchRouteBoundary(CombatControllerResult source, int turn)
    {
        if (_manualRouteSelection is { } selection && !SelectedAsPlanned(selection))
        {
            RejectRoute(source);
            return;
        }
        int index = ReferenceEquals(source, _routeSource)
            ? _routeIndex : CombatRouteDecision.FindTurnIndex(source.Plan.Actions, turn);
        bool completedManualAction = _manualRouteAction != null;
        if (completedManualAction)
        {
            if (!_manualRouteAction!.CompletionTask.IsCompletedSuccessfully
                || _manualRouteChoiceIndex != _manualRouteChoices.Length)
            {
                RejectRoute(source);
                return;
            }
            index++;
        }
        if (index < 0 || index >= source.Plan.Actions.Length
            || source.Plan.Actions[index].Before.PlayerTurn != turn
            || !RouteBoundaryMatches(source, index))
        {
            RejectRoute(source);
            return;
        }
        if (completedManualAction)
        {
            ReplayedAction completed = source.Plan.Actions[index - 1];
            _tracker!.Observe(completed.After);
            if (completed.Action.Kind == PlayerActionKind.UsePotion)
                RecordPotionUsed(completed);
        }
        SetRouteProgress(source, index);
    }

    private static void SetRouteProgress(CombatControllerResult source, int index)
    {
        ClearManualRouteAction();
        _routeSource = source;
        _routeIndex = index;
        ReplayedAction[] actions = source.Plan.Actions;
        int turn = actions[index].Before.PlayerTurn;
        int start = CombatRouteDecision.FindTurnIndex(actions, turn);
        int end = index + 1;
        while (end < actions.Length && actions[end].Before.PlayerTurn == turn)
            end++;
        _guideUi = CombatUiProjection.GuideStep(actions[index], index - start + 1, end - start,
            _names!, _rules!);
    }

    private static bool CurrentRouteMatches(CombatControllerResult result)
    {
        if (result.StartTurn != CurrentTurn() || ReferenceEquals(result, _rejectedRoute) || !CanSolve(out _)
            || _manualRouteAction != null || _manualRouteSelection?.Request.IsPending == true)
            return false;
        return ReferenceEquals(result, _routeSource)
            ? _routeIndex >= 0 && result.Plan.Actions[_routeIndex].Before.PlayerTurn == CurrentTurn()
                && RouteBoundaryMatches(result, _routeIndex)
            : SearchStampMatches(result.Stamp);
    }

    private static int RouteStartIndex(CombatControllerResult result)
        => ReferenceEquals(result, _routeSource) ? _routeIndex : 0;

    private static bool RouteBoundaryMatches(CombatControllerResult source, int index)
    {
        ReplayedAction action = source.Plan.Actions[index];
        try
        {
            Battle live = _tracker!.ImportForComparison(_state!, _player!, _rules!, action.Before);
            // A retained end-turn confirmation captures the changed live state in its stamp.
            // At the route's root that stamp is authoritative; later steps use replay boundaries.
            return index == 0
                ? source.Stamp.Matches(live)
                : CombatRouteDecision.BoundaryMatches(action.Before, live);
        }
        catch (UnsupportedImportException)
        {
            return false;
        }
        catch (UnsupportedException)
        {
            return false;
        }
        catch (CapacityException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void OnManualRouteActionStarted(GameAction action)
    {
        if (RetainedRoute() is not { } source)
            return;
        _routeObservation = default;
        if (_player is not { } player || action.OwnerId != player.NetId
            || action is not (PlayCardAction or UsePotionAction or EndPlayerTurnAction)
            || ReferenceEquals(action, _manualRouteAction))
        {
            return;
        }
        if (_manualRouteAction?.CompletionTask.IsCompleted == true
            || !ReferenceEquals(source, _routeSource))
            MatchRouteBoundary(source, CurrentTurn());
        if (_guideUi == null || !ReferenceEquals(source, _routeSource))
            return;
        InvalidateSearchContinuation("manual_action");

        ReplayedAction planned = source.Plan.Actions[_routeIndex];
        PlayerAction expected = planned.Action;
        uint? target = expected.Target switch
        {
            Limits.NoOwner => null,
            Limits.PlayerOwner => player.Creature.CombatId,
            Limits.OstyOwner => player.Osty?.CombatId,
            _ => planned.Before.Enemies[expected.Target - Limits.FirstEnemyOwner].CombatId,
        };
        bool matches = action switch
        {
            PlayCardAction play when expected.Kind == PlayerActionKind.PlayCard =>
                play.NetCombatCard.ToCardModelOrNull() is { } card
                && ReferenceEquals(player.PlayerCombatState!.Hand.Cards.ElementAtOrDefault(expected.HandIndex), card)
                && (play.TargetId == target
                    || (card.TargetType == NativeTargetType.Self
                        && (target ?? player.Creature.CombatId) == player.Creature.CombatId
                        && (play.TargetId ?? player.Creature.CombatId) == player.Creature.CombatId)),
            UsePotionAction use when expected.Kind == PlayerActionKind.UsePotion =>
                use.PotionIndex == (uint)expected.PotionSlot
                && (use.TargetId == target
                    || (target == null && use.TargetId == player.Creature.CombatId
                        && player.GetPotionAtSlotIndex(expected.PotionSlot)?.IsValidTarget(player.Creature) == true)),
            EndPlayerTurnAction when expected.Kind == PlayerActionKind.EndTurn => true,
            _ => false,
        };
        if (!matches)
        {
            RejectRoute(source);
            return;
        }
        _manualRouteAction = action;
        _manualRouteChoices = [.. planned.Choices.Where(static choice => !choice.Implicit
            && choice.Request.Timing is ChoiceTiming.Action or ChoiceTiming.PlayerTurnEnd)];
    }

    internal static bool ObservesManualRouteChoice(Player player)
        => _manualRouteAction != null && ReferenceEquals(player, _player)
            && RetainedRoute() is { } source && ReferenceEquals(source, _routeSource);

    internal static void ObserveManualRouteChoice(NativeChoiceRequest request)
    {
        if (_routeSource is not { } source || !request.RequiresSurface)
            return;
        if ((_manualRouteSelection is { } previous && !SelectedAsPlanned(previous))
            || _manualRouteChoiceIndex >= _manualRouteChoices.Length)
        {
            RejectRoute(source);
            return;
        }
        PlannedChoice choice = _manualRouteChoices[_manualRouteChoiceIndex];
        if (!PlannedNativeSelector.TryMapOptions(request.Player, choice, request.Options, "manual_route",
                out int[] map, out _))
        {
            RejectRoute(source);
            return;
        }
        CardModel[] cards = [.. choice.SelectedIndexes().Select(index => request.Options[map[index]])];
        if (cards.Length < request.MinSelect || cards.Length > request.MaxSelect)
        {
            RejectRoute(source);
            return;
        }
        _manualRouteChoiceIndex++;
        _manualRouteSelection = new RouteSelection(request, cards);
    }

    private static bool SelectedAsPlanned(RouteSelection selection)
        => selection.Request.Completion switch
        {
            Task<CardModel?> { IsCompletedSuccessfully: true } one => one.Result == null
                ? selection.Cards.Length == 0
                : selection.Cards.Length == 1 && ReferenceEquals(one.Result, selection.Cards[0]),
            Task<IEnumerable<CardModel>> { IsCompletedSuccessfully: true } many =>
                new HashSet<CardModel>(selection.Cards, ReferenceEqualityComparer.Instance).SetEquals(many.Result),
            _ => false,
        };

    internal static IReadOnlyList<CardModel>? GuideChoiceCards(GuideStepUi step,
        out NativeChoiceSurfaceKind surface, out long sequence)
    {
        if (ReferenceEquals(step, _guideUi) && _manualRouteSelection is { Request.IsPending: true } selection)
        {
            surface = selection.Request.Surface;
            sequence = selection.Request.Sequence;
            return selection.Cards;
        }
        surface = default;
        sequence = 0;
        return null;
    }

    private static void RejectRoute(CombatControllerResult source)
    {
        _rejectedRoute = source;
        _continuationSource = null;
        ClearRouteProgress();
    }

    private static void ClearRouteProgress()
    {
        _routeSource = null;
        _routeIndex = -1;
        _guideUi = null;
        ClearManualRouteAction();
    }

    private static void ClearManualRouteAction()
    {
        _manualRouteAction = null;
        _manualRouteChoices = [];
        _manualRouteChoiceIndex = 0;
        _manualRouteSelection = null;
    }
}
