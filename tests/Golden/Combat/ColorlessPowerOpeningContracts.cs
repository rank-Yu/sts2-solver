using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal per-card contracts for the 12 colorless power opening projections. Each case builds a
/// controlled hand, enemy HP and parent/child power increment, then asserts that
/// <see cref="PowerEvidence.ProjectionPotential"/> equals the selected formula. Damage-valued
/// projections also run with three enemy HP left to verify the remaining-HP cap.
/// </summary>
internal static class ColorlessPowerOpeningContracts
{
    private sealed record CardCase(
        string Id,
        string[] Hand,
        ushort GainPower,
        int Gain,
        ushort SecondPower = 0,
        int SecondGain = 0,
        int Enemies = 1,
        bool DamageCapped = false,
        bool MixedDamageBlock = false);

    private sealed class Setup
    {
        internal Battle Parent;
        internal Battle Child;
        internal ScoreFeatures ParentFeatures;
        internal ScoreFeatures ChildFeatures;
        internal Rules Rules = null!;
        internal Scorer Scorer = null!;
        internal ushort CardId;

        internal void Build(SearchCase source, Scorer scorer, CardCase card, int totalEnemyHp)
        {
            if (!Registry.TryCard(card.Id, out CardId))
                throw new InvalidDataException("colorless-power-opening: unregistered " + card.Id);
            Rules = source.Rules;
            Scorer = scorer;
            Parent = State(source, card.Hand, card.Enemies, totalEnemyHp);
            Child = Parent;
            if (card.GainPower != 0)
                Kernel.SetPowerAmount(ref Child, Limits.PlayerOwner, card.GainPower, card.Gain);
            if (card.SecondPower != 0)
                Kernel.SetPowerAmount(ref Child, Limits.PlayerOwner, card.SecondPower, card.SecondGain);
            ParentFeatures = Features(scorer, Parent, incoming: 8, card.Enemies);
            ChildFeatures = Features(scorer, Child, incoming: 8, card.Enemies);
        }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("colorless-power-opening: " + message); }

    private static Battle State(SearchCase item, string[] hand, int enemies, int totalEnemyHp)
    {
        Battle battle = default;
        battle.Player.CurrentHp = 70;
        battle.Player.MaxHp = 70;
        battle.Player.Flags = CreatureFlags.Present;
        battle.Energy = 10;
        battle.MaxHandSize = 10;
        battle.PlayerTurn = 1;
        battle.EnemyCount = (byte)enemies;
        battle.RosterCount = (byte)enemies;
        int baseHp = Math.Max(1, totalEnemyHp / enemies);
        int remainder = Math.Max(0, totalEnemyHp - baseHp * enemies);
        for (int index = 0; index < enemies; index++)
        {
            int hp = baseHp + (index == 0 ? remainder : 0);
            battle.Roster[index] = (byte)index;
            battle.Enemies[index].Flags = EnemyFlags.InCombat;
            battle.Enemies[index].DeathPhase = DeathPhase.None;
            battle.Enemies[index].Creature.Flags = CreatureFlags.Present;
            battle.Enemies[index].Creature.CurrentHp = hp;
            battle.Enemies[index].Creature.MaxHp = hp;
        }
        foreach (string id in hand)
        {
            if (!Registry.TryCard(id, out ushort cardId))
                throw new InvalidDataException("colorless-power-opening: missing " + id);
            Card card = Kernel.CreateCard(item.Rules, cardId);
            Piles.Append(ref battle, Pile.Hand, card);
        }
        return battle;
    }

    private static ScoreFeatures Features(Scorer scorer, in Battle battle, int incoming,
        int enemies)
    {
        ScoreFeatures features = scorer.Features(battle, 0);
        return features with
        {
            PlayerHp = battle.Player.CurrentHp,
            ProjectedPlayerHp = battle.Player.CurrentHp - incoming,
            EnemyHp = 1_000_000,
            RetainedAttackValue = 1,
            LiveDeckSize = 1,
            AliveEnemyCount = enemies,
        };
    }

    private static readonly CardCase[] Cases =
    [
        new("AUTOMATION", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"],
            PowerEvidenceIds.Automation, 2),
        new("CALAMITY", ["STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"],
            PowerEvidenceIds.Calamity, 1, DamageCapped: true),
        new("ENTROPY", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"], 0, 0),
        new("ETERNAL_ARMOR", [], PowerEvidenceIds.Plating, 2),
        new("FASTEN", ["DEFEND_IRONCLAD"], PowerEvidenceIds.Fasten, 2),
        new("MAYHEM", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"], 0, 0),
        new("NOSTALGIA", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"], 0, 0),
        new("PANACHE", ["STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD",
            "STRIKE_IRONCLAD", "STRIKE_IRONCLAD"], PowerEvidenceIds.Panache, 5,
            SecondPower: PowerEvidenceIds.Pyre, SecondGain: 2,
            Enemies: 2, DamageCapped: true),
        new("PREP_TIME", ["STRIKE_IRONCLAD"], PowerEvidenceIds.Vigor, 5,
            DamageCapped: true),
        new("PROWESS", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"], PowerEvidenceIds.Strength, 2,
            PowerEvidenceIds.Dexterity, 1, MixedDamageBlock: true),
        new("ROLLING_BOULDER", [], PowerEvidenceIds.RollingBoulder, 2,
            Enemies: 2, DamageCapped: true),
        new("STRATAGEM", ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD"], 0, 0),
    ];

    internal static void Run()
    {
        SearchCase source = PowerEvidenceContracts.ReadRoot("quality-IRONCLAD-002");
        Scorer scorer = source.Options.Scorer
            ?? throw new InvalidDataException("colorless-power-opening: missing scorer");
        foreach (CardCase card in Cases)
            Check(source, scorer, card);
        Console.WriteLine($"COLORLESS_POWER_OPENING cards={Cases.Length} cap=pass");
    }

    private static void Check(SearchCase source, Scorer scorer, CardCase card)
    {
        Setup uncapped = new();
        uncapped.Build(source, scorer, card, totalEnemyHp: 10_000);
        PowerEvidence.View view = new(
            uncapped.Child, uncapped.ChildFeatures, uncapped.Rules, uncapped.Scorer);
        Assert(view.RemainingTurns() == 16 && view.IncomingDamage() == 8,
            $"{card.Id} controlled turns/incoming expected 16/8, got "
            + $"{view.RemainingTurns()}/{view.IncomingDamage()}");
        if (card.GainPower != 0)
            Assert(view.PowerAmountGain(uncapped.Parent, card.GainPower) == card.Gain,
                $"{card.Id} primary power increment");
        if (card.SecondPower != 0)
            Assert(view.PowerAmountGain(uncapped.Parent, card.SecondPower) == card.SecondGain,
                $"{card.Id} secondary power increment");
        int raw = Expected(card, view);
        int actual = PowerEvidence.ProjectionPotential(uncapped.CardId, view, uncapped.Parent,
            uncapped.ParentFeatures.ProjectedPlayerHp);
        Assert(actual == raw, $"{card.Id} projection expected {raw}, got {actual}");

        Setup capped = new();
        capped.Build(source, scorer, card, totalEnemyHp: 3);
        PowerEvidence.View cappedView = new(
            capped.Child, capped.ChildFeatures, capped.Rules, capped.Scorer);
        Assert(cappedView.RemainingEnemyHp() == 3,
            $"{card.Id} remaining enemy HP expected 3, got {cappedView.RemainingEnemyHp()}");
        int cappedExpected = Expected(card, cappedView);
        int cappedActual = PowerEvidence.ProjectionPotential(capped.CardId, cappedView,
            capped.Parent, capped.ParentFeatures.ProjectedPlayerHp);
        Assert(cappedActual == cappedExpected,
            $"{card.Id} capped projection expected {cappedExpected}, got {cappedActual}");
        if (card.DamageCapped)
        {
            Assert(raw > 3 && cappedActual < raw,
                $"{card.Id} cap must reduce a damage-valued projection");
            AssertDamageCap(card, capped, raw);
        }
        else if (card.MixedDamageBlock)
        {
            Assert(cappedActual < raw,
                $"{card.Id} enemy-HP cap must reduce the damage half");
            AssertMixedCap(card, capped, raw);
        }
        else
        {
            Assert(cappedActual == raw,
                $"{card.Id} non-damage projection must not be changed by the enemy-HP cap");
        }
    }

    /// <summary>
    /// Damage projections use <see cref="PowerEvidence.View.RemainingEnemyHp"/>: the summed current
    /// HP of living primary enemies. Exercise a small single enemy, two enemies, enemy block, and a
    /// reviving enemy whose current HP is zero. The controlled features keep AliveEnemyCount fixed.
    /// </summary>
    private static void AssertDamageCap(CardCase card, Setup setup, int raw)
    {
        Battle child = setup.Child;
        child.EnemyCount = 1;
        child.RosterCount = 1;
        child.Roster[0] = 0;
        child.Enemies[0].Flags = EnemyFlags.InCombat;
        child.Enemies[0].DeathPhase = DeathPhase.None;
        child.Enemies[0].SteamPhase = SteamPhase.None;
        child.Enemies[0].Creature.CurrentHp = 3;
        child.Enemies[0].Creature.MaxHp = Math.Max(child.Enemies[0].Creature.MaxHp, 3);
        child.Enemies[0].Creature.Block = 0;
        PowerEvidence.View view = new(
            child, setup.ChildFeatures, setup.Rules, setup.Scorer);
        Assert(view.RemainingEnemyHp() == 3, card.Id + " single-enemy cap");
        Assert(PowerEvidence.ProjectionPotential(setup.CardId, view, setup.Parent,
                setup.ParentFeatures.ProjectedPlayerHp) == Math.Min(3, raw),
            card.Id + " single-enemy capped projection");

        child.EnemyCount = 2;
        child.RosterCount = 2;
        child.Roster[1] = 1;
        child.Enemies[1] = child.Enemies[0];
        child.Enemies[1].Creature.CurrentHp = 4;
        child.Enemies[1].Creature.Block = 0;
        Assert(view.RemainingEnemyHp() == 7, card.Id + " summed-enemy cap");
        Assert(PowerEvidence.ProjectionPotential(setup.CardId, view, setup.Parent,
                setup.ParentFeatures.ProjectedPlayerHp) == Math.Min(7, raw),
            card.Id + " summed living cap");

        child.Enemies[0].Creature.Block = 50;
        Assert(view.RemainingEnemyHp() == 7, card.Id + " cap ignores enemy block");

        child.Enemies[0].Creature.CurrentHp = 0;
        child.Enemies[0].DeathPhase = DeathPhase.Reviving;
        Assert(view.RemainingEnemyHp() == 4, card.Id + " cap ignores reviving current HP");
        Assert(PowerEvidence.ProjectionPotential(setup.CardId, view, setup.Parent,
                setup.ParentFeatures.ProjectedPlayerHp) == Math.Min(4, raw),
            card.Id + " reviving-enemy cap");
    }

    private static void AssertMixedCap(CardCase card, Setup setup, int raw)
    {
        Battle child = setup.Child;
        child.EnemyCount = 1;
        child.RosterCount = 1;
        child.Roster[0] = 0;
        child.Enemies[0].Flags = EnemyFlags.InCombat;
        child.Enemies[0].DeathPhase = DeathPhase.None;
        child.Enemies[0].SteamPhase = SteamPhase.None;
        child.Enemies[0].Creature.CurrentHp = 3;
        child.Enemies[0].Creature.MaxHp = Math.Max(child.Enemies[0].Creature.MaxHp, 3);
        child.Enemies[0].Creature.Block = 0;
        PowerEvidence.View view = new(
            child, setup.ChildFeatures, setup.Rules, setup.Scorer);
        Assert(PowerEvidence.ProjectionPotential(setup.CardId, view, setup.Parent,
                setup.ParentFeatures.ProjectedPlayerHp) == Expected(card, view)
            && PowerEvidence.ProjectionPotential(setup.CardId, view, setup.Parent,
                setup.ParentFeatures.ProjectedPlayerHp) < raw,
            card.Id + " mixed single-enemy cap");

        child.EnemyCount = 2;
        child.RosterCount = 2;
        child.Roster[1] = 1;
        child.Enemies[1] = child.Enemies[0];
        child.Enemies[1].Creature.CurrentHp = 4;
        child.Enemies[1].Creature.Block = 0;
        Assert(PowerEvidence.ProjectionPotential(setup.CardId, view, setup.Parent,
                setup.ParentFeatures.ProjectedPlayerHp) == Expected(card, view),
            card.Id + " mixed summed living cap");
        child.Enemies[0].Creature.Block = 50;
        Assert(PowerEvidence.ProjectionPotential(setup.CardId, view, setup.Parent,
                setup.ParentFeatures.ProjectedPlayerHp) == Expected(card, view),
            card.Id + " mixed cap ignores enemy block");
        child.Enemies[0].Creature.CurrentHp = 0;
        child.Enemies[0].DeathPhase = DeathPhase.Reviving;
        Assert(PowerEvidence.ProjectionPotential(setup.CardId, view, setup.Parent,
                setup.ParentFeatures.ProjectedPlayerHp) == Expected(card, view),
            card.Id + " mixed reviving-enemy cap");
    }

    private static int Expected(CardCase card, scoped in PowerEvidence.View view)
    {
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int incoming = view.IncomingDamage();
        int enemies = Math.Max(1, view.Features.AliveEnemyCount);
        int cap = view.RemainingEnemyHp();
        return card.Id switch
        {
            // One payout per ten future draws; each payout returns the gained energy once.
            "AUTOMATION" => (int)Math.Min(int.MaxValue,
                (long)card.Gain * view.EnergyUnit() * (view.ModifiedHandDraw() * turns / 10)),
            // Average live attack value, expected attack plays per turn, over R turns.
            "CALAMITY" => CalamityExpected(view, card.Gain),
            "ENTROPY" => Mul(view.EnergyUnit(), turns),
            // Plating stacks each turn; prevention is bounded by the current incoming damage.
            "ETERNAL_ARMOR" => BoundedPrevention(Mul(card.Gain, turns), incoming),
            // Permanent block bonus repeats every remaining turn.
            "FASTEN" => PowerEvidence.GrowthFrontier(view,
                blockPerSkillBonus: card.Gain,
                remainingTurns: view.RemainingTurns()),
            "MAYHEM" => Mul(view.EnergyUnit(), turns),
            "NOSTALGIA" => Mul(view.EnergyUnit(), turns),
            // Every five playable cards per turn triggers on all enemies.
            "PANACHE" => PanacheExpected(view, card.Gain, enemies),
            // Prep Time adds Vigor each turn; repeat the current-hand damage uplift.
            "PREP_TIME" => PowerEvidence.GrowthFrontier(view,
                damagePerAttack: card.Gain,
                remainingTurns: view.RemainingTurns()),
            // Strength and Dexterity both repeat every remaining turn; only damage is capped.
            "PROWESS" => PowerEvidence.GrowthFrontier(view,
                blockPerSkillBonus: card.SecondGain,
                damagePerAttack: card.Gain,
                remainingTurns: view.RemainingTurns()),
            "ROLLING_BOULDER" => RollingExpected(view, card.Gain, enemies),
            "STRATAGEM" => Mul(view.EnergyUnit(), turns),
            _ => throw new InvalidDataException(
                "colorless-power-opening: no expected formula for " + card.Id),
        };
    }

    private static int CalamityExpected(
        scoped in PowerEvidence.View view, int gain)
    {
        int attacks = view.CountType(CardType.Attack);
        if (attacks == 0 || gain == 0)
            return 0;
        long totalValue = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.Type(card) == CardType.Attack)
                totalValue += Math.Max(0, view.CardValue(card));
        }
        int average = (int)Math.Max(1, totalValue / attacks);
        return Math.Min(view.RemainingEnemyHp(),
            Mul(gain, average));
    }

    private static int PanacheExpected(
        scoped in PowerEvidence.View view, int gain, int enemies)
    {
        int draws = view.RawModifiedHandDraw();
        if (draws == 0)
            return 0;
        long totalCost = 0;
        int playable = 0;
        for (int index = 0; index < view.LiveCount; index++)
        {
            Card card = view.LiveCard(index);
            if (view.HasKeyword(card, Keywords.Unplayable))
                continue;
            Pile pile = Piles.PileOf(view.Battle, index, out _);
            totalCost += Math.Max(0, Kernel.EnergyCostWithModifiers(
                view.Battle, view.Rules, card, pile));
            playable++;
        }
        if (playable == 0)
            return 0;
        double averageCost = (double)totalCost / playable;
        int byEnergy = averageCost <= 0d
            ? draws
            : (int)Math.Min(int.MaxValue, Math.Floor(
                view.ModifiedMaxEnergy() / averageCost));
        int plays = Math.Min(draws, byEnergy);
        return plays < 5
            ? 0
            : Math.Min(view.RemainingEnemyHp(),
                Mul(Mul(gain, enemies), Mul(plays / 5, view.RemainingTurns())));
    }

    private static int RollingExpected(
        scoped in PowerEvidence.View view, int gain, int enemies)
    {
        int turns = view.RemainingTurns();
        long growth = 5L * turns * (turns - 1) / 2;
        long raw = ((long)gain * turns + growth) * Math.Max(1, enemies);
        return Math.Min(view.RemainingEnemyHp(), (int)Math.Min(int.MaxValue, raw));
    }

    private static int BoundedPrevention(int rawBlock, int incomingDamage)
    {
        int ceiling = Math.Max(Math.Max(0, incomingDamage), rawBlock / 2);
        return Math.Min(rawBlock, ceiling);
    }

    private static int Mul(int left, int right)
        => (int)Math.Min(int.MaxValue, (long)left * right);

    private static int Mul(int first, int second, int third)
        => (int)Math.Min(int.MaxValue, (long)first * second * third);
}
