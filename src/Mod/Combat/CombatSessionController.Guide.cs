// In-game guide tracking. While the player plays by hand, the retained route stays useful as long as
// the live state equals one of its predicted boundaries; the action that starts at that boundary is
// the guide's next step. Matching uses the deployer's comparison import and BattleReconciler, the
// same check that verifies an executed action, so a step is shown only when the solver would accept
// the live state as that route position. Nothing here changes the route, the tracker's ledgers or
// the execute gate.
using Sts2Solver.Autoplay;
using Sts2Solver.Import;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

internal static partial class CombatSessionController
{
    private readonly record struct GuideObservation(
        CombatControllerResult? Source,
        bool Enabled,
        bool Playable,
        bool ActionsIdle,
        int Turn,
        int Hand,
        int Draw,
        int Discard,
        int Exhaust,
        int Energy,
        int Stars,
        int Potions,
        int PlayerHp,
        int PlayerBlock);

    private static GuideObservation _guideObservation;
    private static bool _guideObserved;
    private static CombatControllerResult? _guideSource;
    private static int _guideIndex = -1;
    private static GuideStepUi? _guideUi;

    private static void ResetGuide()
    {
        _guideObserved = false;
        _guideObservation = default;
        _guideSource = null;
        _guideIndex = -1;
        _guideUi = null;
    }

    /// <summary>The guide step for the published snapshot; null while a worker or deployment owns the turn.</summary>
    private static GuideStepUi? CurrentGuide()
        => _search == null && !HasBlockingDeployment && _guideUi != null
            && ReferenceEquals(_guideSource, _latestResult)
            ? _guideUi
            : null;

    /// <summary>
    /// Re-evaluates the guide step after a settled live change. The live import runs only when the
    /// cheap observation changed and the native action queue is idle in the player's play phase;
    /// while a card is resolving (a hand or grid choice is open) the current step stays shown.
    /// Returns true when the published step changed.
    /// </summary>
    private static bool RefreshGuide(bool playable, bool actionsIdle, int turn)
    {
        GuideObservation observation = ObserveGuide(playable, actionsIdle, turn);
        if (_guideObserved && observation == _guideObservation)
            return false;
        _guideObservation = observation;
        _guideObserved = true;
        GuideStepUi? before = _guideUi;
        if (!observation.Enabled || observation.Source is not { } source)
        {
            ClearGuide();
            return before != null;
        }
        if (!playable || !actionsIdle)
        {
            // A resolving action keeps its step; a different route source has nothing to keep.
            if (!ReferenceEquals(_guideSource, source))
                ClearGuide();
            return !ReferenceEquals(before, _guideUi);
        }
        int index = FindGuideIndex(source, turn);
        if (index < 0)
        {
            ClearGuide();
        }
        else if (!ReferenceEquals(_guideSource, source) || _guideIndex != index || _guideUi == null)
        {
            ReplayedAction[] actions = source.Plan.Actions;
            int start = CombatRouteDecision.FindTurnIndex(actions, turn);
            ReplayedAction[] turnActions = CombatRouteDecision.CurrentTurnActions(actions, turn);
            _guideSource = source;
            _guideIndex = index;
            _guideUi = _names == null || _rules == null
                ? null
                : CombatUiProjection.GuideStep(turnActions, index - start, _names, _rules,
                    advanced: index > 0);
            ModLog.Info(
                $"[Sts2Solver] GUIDE_STEP turn={turn} index={index} step={index - start + 1}/" +
                $"{turnActions.Length} action={actions[index].Action}");
        }
        return !ReferenceEquals(before, _guideUi);
    }

    private static void ClearGuide()
    {
        if (_guideIndex >= 0)
            ModLog.Info($"[Sts2Solver] GUIDE_CLEARED turn={CurrentTurn()} index={_guideIndex}");
        _guideSource = null;
        _guideIndex = -1;
        _guideUi = null;
    }

    private static GuideObservation ObserveGuide(bool playable, bool actionsIdle, int turn)
    {
        CombatControllerResult? source = _search == null && !HasBlockingDeployment
            && !PlayerTurnSetupRuntime.HasActivePage
            ? _latestResult
            : null;
        bool enabled = SolverSettings.Current.Ui.ShowGuide;
        if (_player?.PlayerCombatState is not { } live)
            return new GuideObservation(source, enabled, playable, actionsIdle, turn, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        int potions = 0;
        for (int slot = 0; slot < _player.PotionSlots.Count; slot++)
        {
            if (_player.GetPotionAtSlotIndex(slot) != null)
                potions |= 1 << Math.Min(slot, 30);
        }
        return new GuideObservation(source, enabled, playable, actionsIdle, turn,
            live.Hand.Cards.Count, live.DrawPile.Cards.Count, live.DiscardPile.Cards.Count,
            live.ExhaustPile.Cards.Count, live.Energy, live.Stars, potions,
            _player.Creature.CurrentHp, _player.Creature.Block);
    }

    /// <summary>
    /// The route action of <paramref name="turn"/> whose predicted boundary equals the live state,
    /// searched forward from the last matched step first; -1 when none does.
    /// </summary>
    private static int FindGuideIndex(CombatControllerResult source, int turn)
    {
        if (_state == null || _player == null || _rules == null || _tracker == null)
            return -1;
        ReplayedAction[] actions = source.Plan.Actions;
        int start = CombatRouteDecision.FindTurnIndex(actions, turn);
        if (start < 0)
            return -1;
        int end = start;
        while (end < actions.Length && actions[end].Before.PlayerTurn == turn)
            end++;
        int from = ReferenceEquals(_guideSource, source) && _guideIndex >= start && _guideIndex < end
            ? _guideIndex
            : start;
        for (int index = from; index < end; index++)
        {
            if (GuideBoundaryMatches(actions[index]))
                return index;
        }
        for (int index = start; index < from; index++)
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
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
