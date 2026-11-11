// The ten relics follow CombatSolver docs/relic-counters.md and src/Prediction/RelicCounterCatalog.cs at
// 5c773caa (MIT License, Copyright (c) 2026 Torch1230 and contributors): the vanilla relics whose
// counter survives the end of combat and moves only with card play or turns. Counter properties and
// periods are read from the game's relic models.
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace Sts2Solver.Import;

/// <summary>
/// The relics whose counter carries into the next combat and that the route can steer. The other
/// vanilla relics with a counter reset every turn or combat, or move only with the number of fights.
/// </summary>
public static class RelicCounterCatalog
{
    /// <summary>Native ids of the ten relics, in a fixed order.</summary>
    public static IReadOnlyList<string> RelicIds { get; } =
    [
        "HAPPY_FLOWER", "FAKE_HAPPY_FLOWER", "PENDULUM", "POLLINOUS_CORE", "PEN_NIB",
        "NUNCHAKU", "TUNING_FORK", "JOSS_PAPER", "IRON_CLUB", "GALACTIC_DUST",
    ];

    /// <summary>The counter a route aims to end the fight on: one short of triggering.</summary>
    public static int TargetCount(int period) => period - 1;

    /// <summary>Pen Nib's attack threshold is a code constant (<c>PenNib.AttacksPlayed % 10</c>).</summary>
    private const int PenNibPeriod = 10;

    /// <summary>
    /// The relic's current counter, reduced to its cycle, and the cycle length; false for any other relic.
    /// The counter triggers the relic when it completes the cycle, so <c>period - 1</c> is one short of it.
    /// </summary>
    /// <remarks>
    /// Joss Paper's Ethereal exhausts wait in a separate count until the turn ends and are dropped when the
    /// combat ends, so only <c>CardsExhausted</c> carries over.
    /// </remarks>
    public static bool TryRead(RelicModel relic, out int value, out int period)
    {
        ArgumentNullException.ThrowIfNull(relic);
        (int raw, int cycle) = relic switch
        {
            HappyFlower flower => (flower.TurnsSeen, flower.DynamicVars["Turns"].IntValue),
            FakeHappyFlower flower => (flower.TurnsSeen, flower.DynamicVars["Turns"].IntValue),
            Pendulum pendulum => (pendulum.TurnsSeen, pendulum.DynamicVars["Turns"].IntValue),
            PollinousCore core => (core.TurnsSeen, core.DynamicVars["Turns"].IntValue),
            PenNib nib => (nib.AttacksPlayed, PenNibPeriod),
            Nunchaku nunchaku => (nunchaku.AttacksPlayed, nunchaku.DynamicVars.Cards.IntValue),
            TuningFork fork => (fork.SkillsPlayed, fork.DynamicVars.Cards.IntValue),
            JossPaper paper => (paper.CardsExhausted, paper.DynamicVars["ExhaustAmount"].IntValue),
            IronClub club => (club.CardsPlayed, club.DynamicVars.Cards.IntValue),
            GalacticDust dust => (dust.StarsSpent, dust.DynamicVars.Stars.IntValue),
            _ => (-1, 0),
        };
        if (cycle == 0)
        {
            value = 0;
            period = 0;
            return false;
        }
        if (cycle is < 2 or > byte.MaxValue)
        {
            throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                $"遗物 {relic.Id.Entry} 的计数周期 {cycle} 不受支持");
        }
        period = cycle;
        value = ((raw % cycle) + cycle) % cycle;
        return true;
    }
}
