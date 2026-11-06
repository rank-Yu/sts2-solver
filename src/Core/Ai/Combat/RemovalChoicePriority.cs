// Removal ordering ported from CombatSolver src/Search/CardChoiceSupport.cs (RemovalPriority,
// BasicCardRemovalValue, CardValue, LeavesOnItsOwn) at 5c773caa (MIT License, Copyright (c) 2026
// Torch1230 and contributors).
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat;

/// <summary>
/// Branch policy for hand removal choices with a free count (Purity, Ashwater, Guards): which cards
/// to exhaust or transform first, and how many selections the search explores. Such a choice has up
/// to hundreds of selections, and every kept one is a distinct state for the rest of the turn, so the
/// search keeps <see cref="Branches"/> of them: the cheapest prefix of every size, then the remaining
/// content classes from the cheapest. Statuses and curses go first, then the basic Strike and Defend,
/// then by a rough card value; a card that leaves on its own (Ethereal) goes last.
/// </summary>
internal static class RemovalChoicePriority
{
    /// <summary>Selections explored for one truncated removal choice.</summary>
    internal const int Branches = 12;

    private const double BasicStrikeWeight = 2d / 3d;
    private const double BasicDefendWeight = 1.2d;
    private const double LeavesOnItsOwnPenalty = 1_000d;

    /// <summary>A hand Exhaust/Transform choice whose count is free (at least two cards).</summary>
    internal static bool Applies(in ChoiceRequest request)
        => !request.UsesIndexSelection && !request.OptionsAreGenerated && !request.SourceIsNone
            && request.Source == Pile.Hand
            && request.Effect is ChoiceEffect.Exhaust or ChoiceEffect.Transform
            && request.MinCount < request.MaxCount && request.MaxCount >= 2;

    /// <summary>Lower removes first; tenths of the CombatSolver value.</summary>
    internal static int Of(in Card card, Rules rules)
    {
        CardStats stats = rules.Card(card.Id, card.Upgrade);
        IReadOnlyList<string> names = Registry.Card(card.Id).VarNames;
        double value;
        if (stats.Type is CardType.Status or CardType.Curse)
            value = -1d;
        else if (stats.Rarity == CardRarity.Basic && (stats.Tags & CardTag.Strike) != 0)
            value = Var(names, stats.Vars, "Damage") * BasicStrikeWeight;
        else if (stats.Rarity == CardRarity.Basic && (stats.Tags & CardTag.Defend) != 0)
            value = Var(names, stats.Vars, "Block") * BasicDefendWeight;
        else
        {
            value = Var(names, stats.Vars, "Damage") + Var(names, stats.Vars, "Block") * 0.8d
                + Var(names, stats.Vars, "Cards") * 3d
                + (stats.Type == CardType.Power ? 8d : 0d);
        }
        if (card.HasKeyword(Keywords.Ethereal))
            value += LeavesOnItsOwnPenalty;
        return (int)Math.Round(value * 10d);
    }

    private static double Var(IReadOnlyList<string> names, decimal[] values, string name)
    {
        for (int index = 0; index < names.Count && index < values.Length; index++)
        {
            if (names[index] == name)
                return (double)values[index];
        }
        return 0d;
    }
}
