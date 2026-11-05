// Ported from CombatSolver src/Search/PowerCardValuation/Cards/Silent/SilentDiscardWindowFacts.cs
// at a62f1850 (MIT License, Copyright (c) 2026 Torch1230 and contributors); only the members
// Sts2Solver reads.
namespace Sts2Solver.Ai.Combat.Scoring;

internal static class SilentDiscardWindowFacts
{
    internal static int Capacity(
        string cardId,
        int selectedCards,
        int handCount)
        => cardId switch
        {
            "SURVIVOR" or "ACROBATICS" or "DAGGER_THROW" => 1,
            "PREPARED" or "HIDDEN_DAGGERS" => Math.Max(0, selectedCards),
            "STORM_OF_STEEL" or "SHADOW_STEP" or "CALCULATED_GAMBLE"
                => Math.Max(0, handCount - 1),
            _ => 0,
        };
}
