using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal opening-projection contracts for the 19 Ironclad power cards. Each case builds a
/// controlled hand, enemy HP and parent/child power increment, then asserts that
/// <see cref="PowerEvidence.ProjectionPotential"/> equals the Ironclad formula including the
/// remaining-enemy-HP damage cap. This is the template later character batches can extend.
/// </summary>
internal static class IroncladPowerOpeningContracts
{
    private sealed record CardCase(
        string Id,
        string RulesRoot,
        string[] Hand,
        ushort GainPower,
        int Gain,
        bool DamageCapped,
        bool ExhaustDefend = false,
        bool BoostAttackDamage = false);

    private sealed class Setup
    {
        internal Battle Parent;
        internal Battle Child;
        internal ScoreFeatures Features;
        internal Rules Rules = null!;
        internal Scorer Scorer = null!;
        internal ushort CardId;

        internal void Build(SearchCase battleSource, Scorer scorer, CardCase card, int enemyHp)
        {
            if (!Registry.TryCard(card.Id, out CardId))
                throw new InvalidDataException("ironclad-power-opening: unregistered " + card.Id);
            Rules = PowerEvidenceContracts.ReadRoot(card.RulesRoot).Rules;
            Scorer = scorer;
            Parent = battleSource.Battles[0];
            Parent.HandCount = checked((ushort)card.Hand.Length);
            Parent.DrawCount = 0;
            Parent.DiscardCount = 0;
            Parent.ExhaustCount = 0;
            Parent.PlayCount = 0;
            Parent.Energy = 10;
            for (int index = 0; index < card.Hand.Length; index++)
            {
                if (!Registry.TryCard(card.Hand[index], out ushort id))
                    throw new InvalidDataException(
                        "ironclad-power-opening: unregistered " + card.Hand[index]);
                Card value = default;
                value.Id = id;
                if (card.BoostAttackDamage && index == 0
                    && Registry.Card(id).VarNames.Contains("Damage", StringComparer.Ordinal))
                {
                    value.Var0 = 1000;
                }
                if (card.ExhaustDefend && card.Hand[index] == "DEFEND_IRONCLAD")
                    value.Keywords |= Keywords.Exhaust;
                Parent.Cards[index] = value;
            }
            Parent.EnemyCount = 1;
            Parent.RosterCount = 1;
            Parent.Roster[0] = 0;
            Parent.Enemies[0].Flags = EnemyFlags.InCombat;
            Parent.Enemies[0].DeathPhase = DeathPhase.None;
            Parent.Enemies[0].Creature.CurrentHp = enemyHp;
            Parent.Enemies[0].Creature.MaxHp =
                Math.Max(Parent.Enemies[0].Creature.MaxHp, enemyHp);
            if (card.GainPower != 0)
                Kernel.SetPowerAmount(ref Parent, Limits.PlayerOwner, card.GainPower, 0);
            Child = Parent;
            if (card.GainPower != 0)
                Kernel.SetPowerAmount(ref Child, Limits.PlayerOwner, card.GainPower, card.Gain);
            Features = default(ScoreFeatures) with
            {
                PlayerHp = 60,
                ProjectedPlayerHp = 52,
                EnemyHp = 1_000_000,
                RetainedAttackValue = 1,
                LiveDeckSize = 1,
                AliveEnemyCount = 1,
            };
        }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("ironclad-power-opening: " + message); }

    private static readonly CardCase[] Cases =
    [
        new("AGGRESSION", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"], 0, 0, false),
        new("BARRICADE", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"], 0, 0, false),
        new("CORRUPTION", "kg2-a-corruption-unrelenting",
            ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"], 0, 0, false),
        new("CRIMSON_MANTLE", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"],
            PowerEvidenceIds.CrimsonMantle, 4, false),
        new("CRUELTY", "ec-c-ancient", ["STRIKE_IRONCLAD", "BASH"],
            PowerEvidenceIds.Cruelty, 5, true, BoostAttackDamage: true),
        new("DARK_EMBRACE", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"],
            PowerEvidenceIds.DarkEmbrace, 2, false, ExhaustDefend: true),
        new("DEMON_FORM", "ec-c-ancient", ["STRIKE_IRONCLAD"],
            PowerEvidenceIds.DemonForm, 5, true),
        new("FEEL_NO_PAIN", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"],
            PowerEvidenceIds.FeelNoPain, 3, false, ExhaustDefend: true),
        new("HELLRAISER", "ec-c-ancient", ["STRIKE_IRONCLAD", "BASH"], 0, 0, true),
        new("INFERNO", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"],
            PowerEvidenceIds.Inferno, 2, true),
        new("INFLAME", "ec-c-ancient", ["STRIKE_IRONCLAD"], PowerEvidenceIds.Strength, 5, true),
        new("JUGGERNAUT", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"],
            PowerEvidenceIds.Juggernaut, 4, true),
        new("JUGGLING", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"], 0, 0, false),
        new("PYRE", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"],
            PowerEvidenceIds.Pyre, 2, false),
        new("RUPTURE", "ec-c-ancient", ["STRIKE_IRONCLAD"], PowerEvidenceIds.Rupture, 5, true),
        new("STAMPEDE", "ec-c-ancient", ["STRIKE_IRONCLAD", "BASH"], 0, 0, true),
        new("STONE_ARMOR", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"],
            PowerEvidenceIds.Plating, 3, false),
        new("UNMOVABLE", "ec-c-ancient", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"], 0, 0, false),
        new("VICIOUS", "ec-c-ancient", ["STRIKE_IRONCLAD", "BASH"],
            PowerEvidenceIds.Vicious, 2, false),
    ];

    internal static void Run()
    {
        SearchCase battleSource = PowerEvidenceContracts.ReadRoot("quality-IRONCLAD-002");
        Scorer scorer = battleSource.Options.Scorer
            ?? throw new InvalidDataException("ironclad-power-opening: missing scorer");
        foreach (CardCase card in Cases)
            Check(battleSource, scorer, card);
        Console.WriteLine($"IRONCLAD_POWER_OPENING cards={Cases.Length} cap=pass");
    }

    private static void Check(SearchCase battleSource, Scorer scorer, CardCase card)
    {
        Setup uncapped = Create(battleSource, scorer, card, enemyHp: 10_000);
        PowerEvidence.View uncappedView = new(
            uncapped.Child, uncapped.Features, uncapped.Rules, uncapped.Scorer);
        int turns = Math.Max(0, uncappedView.RemainingTurns() - 1);
        int incoming = uncappedView.IncomingDamage();
        Assert(turns == 15 && incoming == 8,
            $"{card.Id} controlled turns/incoming expected 15/8, got {turns}/{incoming}");
        int raw = Expected(card, uncapped, uncappedView, uncappedView.RemainingEnemyHp());
        int actual = PowerEvidence.ProjectionPotential(uncapped.CardId, uncappedView, uncapped.Parent,
            uncapped.Features.ProjectedPlayerHp);
        Assert(actual == raw, $"{card.Id} projection expected {raw}, got {actual}");

        Setup capped = Create(battleSource, scorer, card, enemyHp: 3);
        PowerEvidence.View cappedView = new(
            capped.Child, capped.Features, capped.Rules, capped.Scorer);
        int cappedActual = PowerEvidence.ProjectionPotential(capped.CardId, cappedView, capped.Parent,
                capped.Features.ProjectedPlayerHp);
        int cappedExpected = Expected(card, capped, cappedView, 3);
        Assert(cappedView.RemainingEnemyHp() == 3, $"{card.Id} remaining enemy cap");
        Assert(cappedExpected == (card.DamageCapped ? Math.Min(3, raw) : raw),
            $"{card.Id} capped formula expected {cappedExpected}");
        Assert(cappedActual == cappedExpected,
            $"{card.Id} capped projection expected {cappedExpected}, got {cappedActual}");
        if (card.DamageCapped)
        {
            Assert(raw > 3 && cappedActual < raw,
                $"{card.Id} cap must reduce a positive damage projection");
            // The cap sums the HP still needed: current HP over living primary enemies.
            // Enemy block counts nothing, and a dead enemy with no revive contributes nothing.
            capped.Child.EnemyCount = 2;
            capped.Child.RosterCount = 2;
            capped.Child.Roster[1] = 1;
            capped.Child.Enemies[1].Flags = EnemyFlags.InCombat;
            capped.Child.Enemies[1].DeathPhase = DeathPhase.None;
            capped.Child.Enemies[1].Creature.CurrentHp = 4;
            capped.Child.Enemies[1].Creature.MaxHp = 4;
            Assert(cappedView.RemainingEnemyHp() == 7, $"{card.Id} cap sums remaining enemies");
            Assert(PowerEvidence.ProjectionPotential(capped.CardId, cappedView, capped.Parent,
                capped.Features.ProjectedPlayerHp)
                == Math.Min(7, raw), $"{card.Id} projection uses the summed living cap");
            capped.Child.Enemies[0].Creature.Block = 50;
            Assert(cappedView.RemainingEnemyHp() == 7, $"{card.Id} cap ignores enemy block");
            capped.Child.Enemies[0].Creature.CurrentHp = 0;
            capped.Child.Enemies[0].DeathPhase = DeathPhase.Reviving;
            Assert(cappedView.RemainingEnemyHp() == 4, $"{card.Id} cap ignores a dead enemy with no revive");
            Assert(PowerEvidence.ProjectionPotential(capped.CardId, cappedView, capped.Parent,
                capped.Features.ProjectedPlayerHp)
                == Math.Min(4, raw), $"{card.Id} projection ignores reviving current HP");
        }
    }

    private static Setup Create(SearchCase battleSource, Scorer scorer, CardCase card, int enemyHp)
    {
        Setup setup = new();
        setup.Build(battleSource, scorer, card, enemyHp);
        return setup;
    }

    private static int Expected(CardCase card, Setup setup,
        scoped in PowerEvidence.View view, int enemyHpCap)
    {
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int incoming = view.IncomingDamage();
        int enemies = Math.Max(1, setup.Features.AliveEnemyCount);
        int gain = card.GainPower == 0
            ? 0
            : view.PowerAmountGain(setup.Parent, card.GainPower);
        Assert(gain == card.Gain, $"{card.Id} power increment expected {card.Gain}, got {gain}");
        return card.Id switch
        {
            "AGGRESSION" => PerTurnResource(Math.Max(1, view.MaxDamage()), turns),
            "BARRICADE" => Mul(BoundedPrevention(Math.Max(1, view.MaxBlock()), incoming), turns),
            "CORRUPTION" => Mul(Math.Max(0, view.CountType(CardType.Skill)),
                Math.Max(1, view.EnergyUnit())),
            "CRIMSON_MANTLE" => Mul(BoundedPrevention(gain, incoming), turns),
            "CRUELTY" => CappedDamage(enemyHpCap, gain, view.TotalAttackDamage() / 100),
            "DARK_EMBRACE" => Mul(Mul(gain, Math.Max(1, view.CountType(CardType.Attack))),
                view.CountExhaustSources()),
            "DEMON_FORM" => CappedGrowthTurns(
                enemyHpCap, gain, TriangularTurns(view.RemainingTurns())),
            "FEEL_NO_PAIN" => BoundedPrevention(Mul(gain, view.CountExhaustSources()), incoming),
            "HELLRAISER" => CappedPerTurn(enemyHpCap, Math.Max(1, view.MaxDamage()), turns, 1),
            "INFERNO" => CappedPerTurn(enemyHpCap, gain, turns, enemies),
            "INFLAME" => CappedGrowthTurns(
                enemyHpCap, gain, view.RemainingTurns()),
            "JUGGERNAUT" => CappedDamage(enemyHpCap, gain, Math.Max(1, view.CountVar("Block"))),
            "JUGGLING" => PerTurnResource(Math.Max(1, view.MaxDamage()), turns),
            "PYRE" => PerTurnResource(gain, turns),
            "RUPTURE" => CappedGrowthTurns(enemyHpCap, Mul(gain, Math.Max(1,
                view.CountVar("SelfDamage") + view.CountVar("HpLoss") + view.CountVar("LoseHp"))),
                view.RemainingTurns()),
            "STAMPEDE" => CappedPerTurn(enemyHpCap, Math.Max(1, view.MaxDamage()), turns, 1),
            "STONE_ARMOR" => Mul(BoundedPrevention(gain, incoming), turns),
            "UNMOVABLE" => BoundedPrevention(Math.Max(1, view.MaxBlock()), incoming),
            "VICIOUS" => Mul(Mul(gain, Math.Max(1, view.EnergyUnit())), view.CountVar("Vulnerable")),
            _ => throw new InvalidDataException("ironclad-power-opening: no formula for " + card.Id),
        };
    }

    // The growth frontier with a single attack in hand and no draw/block bonuses is exactly the
    // attack's incremental damage per turn, repeated for the turn multiplier and bounded by HP.
    private static int CappedGrowthTurns(int enemyHpCap, int gainPerTurn, int turns)
        => Math.Min(enemyHpCap, Mul(gainPerTurn, Math.Max(0, turns)));

    private static int TriangularTurns(int turns)
        => turns <= 0 ? 0 : (int)Math.Min(int.MaxValue, (long)turns * (turns + 1) / 2);

    private static int CappedPerTurn(int enemyHpCap, int perTurn, int turns, int targets)
        => Math.Min(enemyHpCap, Mul(Mul(perTurn, Math.Max(0, turns)), Math.Max(1, targets)));

    private static int CappedDamage(int enemyHpCap, int perTrigger, int triggers)
        => Math.Min(enemyHpCap, Mul(perTrigger, Math.Max(0, triggers)));

    private static int PerTurnResource(int perTurn, int turns)
        => Mul(perTurn, Math.Max(0, turns));

    private static int BoundedPrevention(int rawBlock, int incomingDamage)
    {
        int ceiling = Math.Max(Math.Max(0, incomingDamage), rawBlock / 2);
        return Math.Min(rawBlock, ceiling);
    }

    private static int Mul(int left, int right)
        => (int)Math.Min(int.MaxValue, (long)left * right);
}
