using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Relics;

/// <summary>
/// <c>BigHat</c>: on <c>AfterSideTurnStart</c> while the player's turn number is at most 1, generate
/// its <c>Cards</c> distinct Ethereal character cards into the hand.
/// </summary>
[ContentId("BIG_HAT")]
public sealed class BigHat : RelicDef
{
    // The old play-phase closure froze the whole character pool; keep it for rule parity.
    public override IReadOnlyList<string> GeneratedCardPools => [CardPools.CharacterAll];

    // Only the Ethereal subset participates while the opening battle is built.
    public override IReadOnlyList<string> SetupOnlyCardPools => [CardPools.CharacterEthereal];

}
