namespace Sts2Solver.Ai.Combat.Scoring;

internal static class FoolPowerRoutePolicy
{
    internal static (PowerCommitmentFamily Family, string? Power) Lookup(string cardId) => cardId switch
    {
        "MYTHIC_FORM" => (PowerCommitmentFamily.CardGenerationEngine, "MYTHIC_FORM_POWER"),
        "MOTIF" => (PowerCommitmentFamily.HandEngine, "MOTIF_POWER"),
        "HIDDEN_ROOTS" => (PowerCommitmentFamily.DefenseEfficiency, "HIDDEN_ROOTS_POWER"),
        "LULLABY" => (PowerCommitmentFamily.DefenseEfficiency, "LULLABY_POWER"),
        "SONG_PATH" => (PowerCommitmentFamily.DefenseEfficiency, "SONG_PATH_POWER"),
        "VIOLENT_CHORD" => (PowerCommitmentFamily.DamageEngine, "VIOLENT_CHORD_POWER"),
        _ => (PowerCommitmentFamily.None, null),
    };
}
