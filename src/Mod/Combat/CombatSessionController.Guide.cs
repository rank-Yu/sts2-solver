// Tracks the next action of a retained route while the player plays by hand.
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
    private readonly record struct GuideObservation(
        CombatControllerResult? Source, bool Playable, bool ActionsIdle, int Turn, uint Potions);

    private sealed record GuideSelection(NativeChoiceRequest Request, CardModel[] Cards);

    private static ActionExecutor? _guideExecutor;
    private static GuideObservation _guideObservation;
    private static CombatControllerResult? _guideSource;
    private static CombatControllerResult? _guideRejectedSource;
    private static int _guideIndex = -1;
    private static GuideStepUi? _guideUi;
    private static GameAction? _guideAction;
    private static PlannedChoice[] _guideChoices = [];
    private static int _guideChoiceIndex;
    private static GuideSelection? _guideSelection;

    private static void WatchGuideActions()
    {
        _guideExecutor = RunManager.Instance.ActionExecutor;
        _guideExecutor.BeforeActionExecuted += OnGuideActionStarted;
    }

    private static void ResetGuide()
    {
        if (_guideExecutor != null)
            _guideExecutor.BeforeActionExecuted -= OnGuideActionStarted;
        _guideExecutor = null;
        _guideObservation = default;
        _guideRejectedSource = null;
        ClearGuide();
    }

    private static CombatControllerResult? GuideSource()
        => _combatActive && SolverSettings.Current.Ui.ShowGuide && !_autoExecute
            && !(_runCombat && !_runPaused) && _search == null && !HasBlockingDeployment
            && !PlayerTurnSetupRuntime.HasActivePage && !ReferenceEquals(_latestResult, _guideRejectedSource)
            ? _latestResult
            : null;

    private static GuideStepUi? CurrentGuide()
    {
        if (_guideUi == null || !ReferenceEquals(GuideSource(), _guideSource))
            return null;
        if (_guideAction != null)
            return _guideSelection?.Request is { IsPending: true } ? _guideUi : null;
        return _guideObservation is { Playable: true, ActionsIdle: true } ? _guideUi : null;
    }

    private static bool RefreshGuide(bool playable, bool actionsIdle, int turn)
    {
        if (!ReferenceEquals(_guideRejectedSource, _latestResult))
            _guideRejectedSource = null;
        CombatControllerResult? source = GuideSource();
        // A native action paused for a player choice can temporarily leave the queue idle.
        bool settled = actionsIdle && _guideAction?.CompletionTask.IsCompleted != false
            && _guideSelection?.Request.IsPending != true;
        uint potions = 0;
        if (source != null && playable && settled)
        {
            // Discarding a potion can change the boundary without a combat action.
            for (int slot = 0; slot < _player!.PotionSlots.Count; slot++)
                if (_player.GetPotionAtSlotIndex(slot) != null)
                    potions |= 1u << slot;
        }
        GuideObservation observation = new(source, playable, settled, turn, potions);
        if (observation != _guideObservation)
        {
            _guideObservation = observation;
            if (source == null || !ReferenceEquals(source, _guideSource))
                ClearGuide();
            if (source != null && playable && settled)
                MatchGuideBoundary(source, turn);
        }
        return !ReferenceEquals(CurrentGuide(), CombatUiStore.Snapshot.Guide);
    }

    private static void MatchGuideBoundary(CombatControllerResult source, int turn)
    {
        if (_guideSelection is { } selection && !SelectedAsPlanned(selection))
        {
            RejectGuide(source);
            return;
        }
        int index = FindGuideIndex(source, turn);
        ClearGuideAction();
        if (index < 0)
        {
            ClearGuide();
            return;
        }
        if (ReferenceEquals(_guideSource, source) && _guideIndex == index)
            return;

        ReplayedAction[] actions = source.Plan.Actions;
        int start = CombatRouteDecision.FindTurnIndex(actions, turn);
        int end = index + 1;
        while (end < actions.Length && actions[end].Before.PlayerTurn == turn)
            end++;
        _guideSource = source;
        _guideIndex = index;
        _guideUi = CombatUiProjection.GuideStep(actions[index], index - start + 1, end - start,
            _names!, _rules!);
    }

    private static int FindGuideIndex(CombatControllerResult source, int turn)
    {
        ReplayedAction[] actions = source.Plan.Actions;
        int start = CombatRouteDecision.FindTurnIndex(actions, turn);
        if (start < 0)
            return -1;
        if (ReferenceEquals(_guideSource, source) && _guideIndex >= start)
            start = _guideIndex;
        for (int index = start; index < actions.Length && actions[index].Before.PlayerTurn == turn; index++)
        {
            if (GuideBoundaryMatches(actions[index]))
                return index;
        }
        return -1;
    }

    private static bool GuideBoundaryMatches(ReplayedAction action)
    {
        try
        {
            Battle live = _tracker!.ImportForComparison(_state!, _player!, _rules!, action.Before);
            return CombatRouteDecision.BoundaryMatches(action.Before, live);
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

    private static void OnGuideActionStarted(GameAction action)
    {
        if (GuideSource() is not { } source)
            return;
        _guideObservation = default;
        if (_player is not { } player || action.OwnerId != player.NetId
            || action is not (PlayCardAction or UsePotionAction or EndPlayerTurnAction)
            || ReferenceEquals(action, _guideAction))
        {
            return;
        }
        if (_guideAction?.CompletionTask.IsCompleted == true)
            MatchGuideBoundary(source, CurrentTurn());
        if (_guideUi == null || !ReferenceEquals(source, _guideSource))
            return;

        ReplayedAction planned = source.Plan.Actions[_guideIndex];
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
            RejectGuide(source);
            return;
        }
        _guideAction = action;
        _guideChoices = [.. planned.Choices.Where(static choice => !choice.Implicit
            && choice.Request.Timing is ChoiceTiming.Action or ChoiceTiming.PlayerTurnEnd)];
    }

    internal static bool ObservesManualGuideChoice(Player player)
        => _guideAction != null && ReferenceEquals(player, _player)
            && GuideSource() is { } source && ReferenceEquals(source, _guideSource);

    internal static void ObserveManualGuideChoice(NativeChoiceRequest request)
    {
        if (_guideSource is not { } source || !request.RequiresSurface)
            return;
        if ((_guideSelection is { } previous && !SelectedAsPlanned(previous))
            || _guideChoiceIndex >= _guideChoices.Length)
        {
            RejectGuide(source);
            return;
        }
        PlannedChoice choice = _guideChoices[_guideChoiceIndex];
        if (!PlannedNativeSelector.TryMapOptions(request.Player, choice, request.Options, "guide",
                out int[] map, out _))
        {
            RejectGuide(source);
            return;
        }
        CardModel[] cards = [.. choice.SelectedIndexes().Select(index => request.Options[map[index]])];
        if (cards.Length < request.MinSelect || cards.Length > request.MaxSelect)
        {
            RejectGuide(source);
            return;
        }
        _guideChoiceIndex++;
        _guideSelection = new GuideSelection(request, cards);
    }

    private static bool SelectedAsPlanned(GuideSelection selection)
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
        if (ReferenceEquals(step, _guideUi) && _guideSelection is { Request.IsPending: true } selection)
        {
            surface = selection.Request.Surface;
            sequence = selection.Request.Sequence;
            return selection.Cards;
        }
        surface = default;
        sequence = 0;
        return null;
    }

    private static void RejectGuide(CombatControllerResult source)
    {
        _guideRejectedSource = source;
        ClearGuide();
    }

    private static void ClearGuide()
    {
        _guideSource = null;
        _guideIndex = -1;
        _guideUi = null;
        ClearGuideAction();
    }

    private static void ClearGuideAction()
    {
        _guideAction = null;
        _guideChoices = [];
        _guideChoiceIndex = 0;
        _guideSelection = null;
    }
}
