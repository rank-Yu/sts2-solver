using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal opening-projection contracts for the 20 Defect power cards. Each case builds a
/// controlled hand, orb queue, enemy list and parent/child power increment, then asserts that
/// <see cref="PowerEvidence.ProjectionPotential"/> equals the selected Defect formula. Damage
/// projections are also checked against <see cref="PowerEvidence.View.RemainingEnemyHp"/>: the
/// summed HP still needed over living primary enemies, ignoring enemy block and dead enemies
/// without a revive.
/// </summary>
internal static class DefectPowerOpeningContracts
{
    private sealed record CardCase(
        string Id,
        string[] Hand,
        ushort GainPower,
        int Gain,
        ushort GainPower2 = 0,
        int Gain2 = 0,
        string[]? Orbs = null,
        int OrbCapacityBase = 0,
        int OrbCapacityGain = 0,
        int EnemyCount = 1,
        bool DamageCapped = false,
        bool MixedDamageBlock = false);

    private sealed class Setup
    {
        internal Battle Parent;
        internal Battle Child;
        internal ScoreFeatures Features;
        internal Rules Rules = null!;
        internal Scorer Scorer = null!;
        internal ushort CardId;

        internal void Build(SearchCase source, Scorer scorer, CardCase card, int enemyHp)
        {
            if (!Registry.TryCard(card.Id, out CardId))
                throw new InvalidDataException("defect-power-opening: unregistered " + card.Id);
            Rules = source.Rules;
            Scorer = scorer;
            Parent = source.Battles[0];
            Parent.Outcome = Outcome.None;
            Parent.HandCount = checked((ushort)card.Hand.Length);
            Parent.DrawCount = 0;
            Parent.DiscardCount = 0;
            Parent.ExhaustCount = 0;
            Parent.PlayCount = 0;
            Parent.Energy = 10;
            Parent.PowerCount = 0;
            Parent.OrbCount = 0;
            Parent.OrbCapacity = 0;
            for (int index = 0; index < card.Hand.Length; index++)
            {
                if (!Registry.TryCard(card.Hand[index], out ushort id))
                    throw new InvalidDataException(
                        "defect-power-opening: unregistered " + card.Hand[index]);
                Card value = default;
                value.Id = id;
                CardStats stats = Rules.Card(id, 0);
                value.BaseCost = checked((sbyte)stats.EnergyCost);
                value.BaseStarCost = checked((sbyte)stats.StarCost);
                value.Keywords = stats.Keywords;
                if (stats.CostsX)
                    value.Flags |= CardFlags.CostsX;
                Parent.Cards[index] = value;
            }
            Parent.EnemyCount = checked((byte)card.EnemyCount);
            Parent.RosterCount = checked((byte)card.EnemyCount);
            for (int index = 0; index < card.EnemyCount; index++)
            {
                Parent.Roster[index] = checked((byte)index);
                Parent.Enemies[index].Flags = EnemyFlags.InCombat | EnemyFlags.SkipNextMove;
                Parent.Enemies[index].DeathPhase = DeathPhase.None;
                Parent.Enemies[index].SteamPhase = SteamPhase.None;
                Parent.Enemies[index].Creature.CurrentHp = enemyHp;
                Parent.Enemies[index].Creature.MaxHp = enemyHp;
                Parent.Enemies[index].Creature.Block = 0;
            }
            string[] orbs = card.Orbs ?? [];
            for (int index = 0; index < orbs.Length; index++)
            {
                if (!Registry.TryOrb(orbs[index], out ushort id))
                    throw new InvalidDataException(
                        "defect-power-opening: unregistered orb " + orbs[index]);
                Parent.Orbs[index] = new Orb
                {
                    Id = checked((byte)id),
                    Aux = Registry.Orb(id).InitialAux,
                };
            }
            Parent.OrbCount = checked((byte)orbs.Length);
            Parent.OrbCapacity = checked((byte)Math.Max(card.OrbCapacityBase, orbs.Length));
            Child = Parent;
            if (card.GainPower != 0)
                Kernel.SetPowerAmount(ref Child, Limits.PlayerOwner, card.GainPower, card.Gain);
            if (card.GainPower2 != 0)
                Kernel.SetPowerAmount(ref Child, Limits.PlayerOwner, card.GainPower2, card.Gain2);
            if (card.OrbCapacityGain != 0)
                Child.OrbCapacity = checked((byte)(Parent.OrbCapacity + card.OrbCapacityGain));
            Features = default(ScoreFeatures) with
            {
                PlayerHp = 60,
                PlayerMaxHp = 60,
                ProjectedPlayerHp = 52,
                EnemyHp = 1_000_000,
                RetainedAttackValue = 1,
                LiveDeckSize = 1,
                AliveEnemyCount = card.EnemyCount,
            };
        }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("defect-power-opening: " + message); }

    private static readonly CardCase[] Cases =
    [
        new("BIASED_COGNITION", ["STRIKE_DEFECT", "DEFEND_DEFECT"],
            PowerEvidenceIds.Focus, 3,
            GainPower2: BiasedCognitionPower(), Gain2: 1,
            Orbs: ["LIGHTNING_ORB", "FROST_ORB"], DamageCapped: true),
        new("BUFFER", ["DEFEND_DEFECT"], PowerEvidenceIds.Buffer, 2),
        new("BULK_UP", ["STRIKE_DEFECT", "DEFEND_DEFECT"],
            PowerEvidenceIds.Strength, 2, PowerEvidenceIds.Dexterity, 1,
            MixedDamageBlock: true),
        new("CAPACITOR", ["STRIKE_DEFECT"], 0, 0,
            Orbs: ["LIGHTNING_ORB", "FROST_ORB"], OrbCapacityBase: 3, OrbCapacityGain: 2),
        new("CONSUMING_SHADOW", ["STRIKE_DEFECT"], ConsumingShadowPower(), 1,
            Orbs: ["DARK_ORB", "DARK_ORB"], DamageCapped: true),
        new("COOLANT", ["STRIKE_DEFECT"], CoolantPower(), 2,
            Orbs: ["LIGHTNING_ORB", "FROST_ORB"]),
        new("CREATIVE_AI", ["STRIKE_DEFECT", "DEFEND_DEFECT"], CreativeAiPower(), 1),
        new("DEFRAGMENT", ["STRIKE_DEFECT"], PowerEvidenceIds.Focus, 2,
            Orbs: ["LIGHTNING_ORB", "FROST_ORB", "DARK_ORB"], DamageCapped: true),
        new("ECHO_FORM", ["STRIKE_DEFECT"], EchoFormPower(), 1, EnemyCount: 3,
            DamageCapped: true),
        new("FERAL", ["GO_FOR_THE_EYES"], FeralPower(), 1),
        new("HAILSTORM", ["STRIKE_DEFECT"], HailstormPower(), 2,
            Orbs: ["FROST_ORB"], EnemyCount: 3, DamageCapped: true),
        new("ITERATION", ["BOOST_AWAY", "STRIKE_DEFECT"], IterationPower(), 1),
        new("LOOP", ["STRIKE_DEFECT"], LoopPower(), 1,
            Orbs: ["LIGHTNING_ORB", "FROST_ORB"], DamageCapped: true),
        new("MACHINE_LEARNING", ["STRIKE_DEFECT", "DEFEND_DEFECT"],
            MachineLearningPower(), 1),
        new("SMOKESTACK", ["BOOST_AWAY", "STRIKE_DEFECT"], SmokestackPower(), 4,
            EnemyCount: 3, DamageCapped: true),
        new("SPINNER", ["STRIKE_DEFECT"], SpinnerPower(), 1),
        new("STORM", ["DEFRAGMENT", "STRIKE_DEFECT"], StormPower(), 1,
            DamageCapped: true),
        new("SUBROUTINE", ["DEFRAGMENT", "STRIKE_DEFECT"], PowerEvidenceIds.Subroutine, 1),
        new("THUNDER", ["STRIKE_DEFECT"], ThunderPower(), 4,
            Orbs: ["LIGHTNING_ORB", "FROST_ORB"], DamageCapped: true),
        new("TRASH_TO_TREASURE", ["BOOST_AWAY", "STRIKE_DEFECT"],
            TrashToTreasurePower(), 1),
    ];

    internal static void Run()
    {
        SearchCase source = PowerEvidenceContracts.ReadRoot("quality-DEFECT-002");
        Scorer scorer = source.Options.Scorer
            ?? throw new InvalidDataException("defect-power-opening: missing scorer");
        foreach (CardCase card in Cases)
            Check(source, scorer, card);
        Console.WriteLine($"DEFECT_POWER_OPENING cards={Cases.Length} formula/trigger/cap=pass");
    }

    private static void Check(SearchCase source, Scorer scorer, CardCase card)
    {
        Setup uncapped = Create(source, scorer, card, enemyHp: 10_000);
        PowerEvidence.View view = new(
            uncapped.Child, uncapped.Features, uncapped.Rules, uncapped.Scorer);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int incoming = view.IncomingDamage();
        Assert(turns == 15, $"{card.Id} controlled turns expected 15, got {turns}");
        Assert(incoming == 8, $"{card.Id} controlled incoming expected 8, got {incoming}");
        int raw = Expected(card, uncapped, view, turns, incoming, view.RemainingEnemyHp());
        int actual = PowerEvidence.ProjectionPotential(
            uncapped.CardId, view, uncapped.Parent, uncapped.Features.ProjectedPlayerHp);
        Assert(actual == raw, $"{card.Id} projection expected {raw}, got {actual}");
        Assert(actual > 0, $"{card.Id} projection must be positive");
        Assert(PowerEvidence.HasTriggerEvidence(uncapped.CardId, uncapped.Parent, view),
            $"{card.Id} constructed trigger evidence");
        int noGain = PowerEvidence.ProjectionPotential(
            uncapped.CardId, view, uncapped.Child, uncapped.Features.ProjectedPlayerHp);
        Assert(noGain == 0, $"{card.Id} projection without an increment expected 0, got {noGain}");

        Setup capped = Create(source, scorer, card, enemyHp: 3);
        PowerEvidence.View cappedView = new(
            capped.Child, capped.Features, capped.Rules, capped.Scorer);
        int cappedActual = PowerEvidence.ProjectionPotential(
            capped.CardId, cappedView, capped.Parent, capped.Features.ProjectedPlayerHp);
        int cappedExpected = Expected(
            card, capped, cappedView, turns, incoming, cappedView.RemainingEnemyHp());
        int singleEnemyCap = 3 * card.EnemyCount;
        Assert(cappedView.RemainingEnemyHp() == singleEnemyCap,
            $"{card.Id} remaining enemy cap");
        if (card.DamageCapped)
        {
            Assert(cappedExpected == Math.Min(singleEnemyCap, raw),
                $"{card.Id} capped formula expected {cappedExpected}");
        }
        else if (!card.MixedDamageBlock)
        {
            Assert(cappedExpected == raw,
                $"{card.Id} capped formula expected {cappedExpected}");
        }
        Assert(cappedActual == cappedExpected,
            $"{card.Id} capped projection expected {cappedExpected}, got {cappedActual}");
        if (card.DamageCapped)
        {
            Assert(raw > singleEnemyCap && cappedActual < raw,
                $"{card.Id} cap must reduce a positive damage projection");
            AssertDamageCapStates(card, capped, raw);
        }
        else if (card.MixedDamageBlock)
        {
            Assert(cappedActual < raw,
                $"{card.Id} enemy-HP cap must reduce the damage half");
            AssertMixedDamageBlockStates(card, capped, raw);
        }
    }

    private static void AssertDamageCapStates(CardCase card, Setup capped, int raw)
    {
        PowerEvidence.View cappedView = new(
            capped.Child, capped.Features, capped.Rules, capped.Scorer);
        capped.Child.EnemyCount = 2;
        capped.Child.RosterCount = 2;
        capped.Child.Roster[1] = 1;
        capped.Child.Enemies[1].Flags = EnemyFlags.InCombat;
        capped.Child.Enemies[1].DeathPhase = DeathPhase.None;
        capped.Child.Enemies[1].SteamPhase = SteamPhase.None;
        capped.Child.Enemies[1].Creature.CurrentHp = 4;
        capped.Child.Enemies[1].Creature.MaxHp = 4;
        Assert(cappedView.RemainingEnemyHp() == 7, $"{card.Id} cap sums remaining enemies");
        Assert(PowerEvidence.ProjectionPotential(
            capped.CardId, cappedView, capped.Parent, capped.Features.ProjectedPlayerHp)
            == Math.Min(7, raw), $"{card.Id} projection uses the summed living cap");
        capped.Child.Enemies[0].Creature.Block = 50;
        Assert(cappedView.RemainingEnemyHp() == 7, $"{card.Id} cap ignores enemy block");
        capped.Child.Enemies[0].Creature.CurrentHp = 0;
        capped.Child.Enemies[0].DeathPhase = DeathPhase.Reviving;
        Assert(cappedView.RemainingEnemyHp() == 4,
            $"{card.Id} cap ignores a dead enemy with no revive");
        Assert(PowerEvidence.ProjectionPotential(
            capped.CardId, cappedView, capped.Parent, capped.Features.ProjectedPlayerHp)
            == Math.Min(4, raw), $"{card.Id} projection ignores reviving current HP");
    }

    private static void AssertMixedDamageBlockStates(CardCase card, Setup capped, int raw)
    {
        PowerEvidence.View view = new(
            capped.Child, capped.Features, capped.Rules, capped.Scorer);
        int actual = PowerEvidence.ProjectionPotential(
            capped.CardId, view, capped.Parent, capped.Features.ProjectedPlayerHp);
        Assert(actual == Expected(card, capped, view,
                Math.Max(0, view.RemainingTurns() - 1), view.IncomingDamage(),
                view.RemainingEnemyHp()) && actual < raw,
            $"{card.Id} mixed growth keeps block while capping damage");

        capped.Child.EnemyCount = 2;
        capped.Child.RosterCount = 2;
        capped.Child.Roster[1] = 1;
        capped.Child.Enemies[1] = capped.Child.Enemies[0];
        capped.Child.Enemies[1].Creature.CurrentHp = 4;
        capped.Child.Enemies[1].Creature.MaxHp = 4;
        capped.Child.Enemies[1].Creature.Block = 0;
        Assert(view.RemainingEnemyHp() == 7, $"{card.Id} mixed cap sums remaining enemies");
        Assert(PowerEvidence.ProjectionPotential(
            capped.CardId, view, capped.Parent, capped.Features.ProjectedPlayerHp)
            == Expected(card, capped, view,
                Math.Max(0, view.RemainingTurns() - 1), view.IncomingDamage(),
                view.RemainingEnemyHp()), $"{card.Id} mixed summed living cap");

        capped.Child.Enemies[0].Creature.Block = 50;
        Assert(PowerEvidence.ProjectionPotential(
            capped.CardId, view, capped.Parent, capped.Features.ProjectedPlayerHp)
            == Expected(card, capped, view,
                Math.Max(0, view.RemainingTurns() - 1), view.IncomingDamage(),
                view.RemainingEnemyHp()), $"{card.Id} mixed cap ignores enemy block");
        capped.Child.Enemies[0].Creature.CurrentHp = 0;
        capped.Child.Enemies[0].DeathPhase = DeathPhase.Reviving;
        Assert(view.RemainingEnemyHp() == 4, $"{card.Id} mixed cap ignores reviving HP");
        Assert(PowerEvidence.ProjectionPotential(
            capped.CardId, view, capped.Parent, capped.Features.ProjectedPlayerHp)
            == Expected(card, capped, view,
                Math.Max(0, view.RemainingTurns() - 1), view.IncomingDamage(),
                view.RemainingEnemyHp()), $"{card.Id} mixed reviving cap");
    }

    private static Setup Create(SearchCase source, Scorer scorer, CardCase card, int enemyHp)
    {
        Setup setup = new();
        setup.Build(source, scorer, card, enemyHp);
        return setup;
    }

    private static int Expected(CardCase card, Setup setup,
        scoped in PowerEvidence.View view, int turns, int incoming, int enemyHpCap)
    {
        int gain = card.GainPower == 0 ? 0 : view.PowerAmountGain(setup.Parent, card.GainPower);
        Assert(gain == card.Gain, $"{card.Id} power increment expected {card.Gain}, got {gain}");
        int gain2 = card.GainPower2 == 0 ? 0 : view.PowerAmountGain(setup.Parent, card.GainPower2);
        Assert(gain2 == card.Gain2, $"{card.Id} second power increment expected {card.Gain2}, got {gain2}");
        int orbs = view.Battle.OrbCount;
        int enemies = Math.Max(1, view.Features.AliveEnemyCount);
        int statusSources = card.Id is "ITERATION" or "SMOKESTACK" or "TRASH_TO_TREASURE"
            ? view.CountNativeIdContaining("BOOST_AWAY")
            : 0;
        if (statusSources > 0)
            Assert(statusSources == 1, $"{card.Id} one constructed Status source");
        int powerSources = card.Id is "STORM" or "SUBROUTINE"
            ? view.CountType(CardType.Power)
            : 0;
        if (powerSources > 0)
            Assert(view.PlayablePowerSources() == powerSources,
                $"{card.Id} constructed Power source stays playable");
        return card.Id switch
        {
            "BIASED_COGNITION" => BiasedCognitionExpected(
                enemyHpCap, gain, gain2, orbs, view.RemainingTurns()),
            "DEFRAGMENT" => CappedDamage(
                enemyHpCap, Mul(gain, orbs), view.RemainingTurns()),
            "BUFFER" => PowerEvidence.BufferPotential(
                gain, incoming, view.IncomingAttackHits(nextTurn: true)),
            "BULK_UP" => PowerEvidence.GrowthFrontier(view,
                blockPerSkillBonus: gain2, damagePerAttack: gain,
                remainingTurns: view.RemainingTurns()),
            "CAPACITOR" => Mul(
                Math.Max(0, setup.Child.OrbCapacity - setup.Parent.OrbCapacity), turns),
            "CONSUMING_SHADOW" => DarkEvokeExpected(
                view, gain, orbs, view.RemainingTurns(), enemies),
            "COOLANT" => Mul(BoundedPrevention(
                Mul(gain, view.ReachableOrbTypes()), incoming), turns),
            "CREATIVE_AI" or "MACHINE_LEARNING" =>
                Mul(Mul(gain, view.EnergyUnit()), turns),
            "FERAL" => FeralExpected(view, setup.Parent, gain),
            "ECHO_FORM" => CappedPerTurn(
                enemyHpCap, Mul(gain, Math.Max(1, view.MaxDamage())), turns, 1),
            "HAILSTORM" => CappedPerTurn(enemyHpCap, gain, turns, enemies),
            "ITERATION" => Mul(Mul(gain, view.EnergyUnit()), statusSources),
            "LOOP" => LoopExpected(view, gain, view.RemainingTurns(), enemies),
            "SMOKESTACK" => CappedDamage(
                enemyHpCap, gain, Mul(statusSources, enemies)),
            "SPINNER" => Mul(gain, turns),
            "STORM" => CappedDamage(
                enemyHpCap, Mul(gain, view.EnergyUnit()), powerSources),
            "SUBROUTINE" => Mul(Mul(gain, view.EnergyUnit()), powerSources),
            "THUNDER" => CappedDamage(
                enemyHpCap, gain, view.CountOrb("LIGHTNING")),
            "TRASH_TO_TREASURE" => Mul(Mul(gain, view.EnergyUnit()), statusSources),
            _ => throw new InvalidDataException("defect-power-opening: no formula for " + card.Id),
        };
    }

    private static ushort ConsumingShadowPower() => Power("CONSUMING_SHADOW_POWER");
    private static ushort CoolantPower() => Power("COOLANT_POWER");
    private static ushort CreativeAiPower() => Power("CREATIVE_AI_POWER");
    private static ushort EchoFormPower() => Power("ECHO_FORM_POWER");
    private static ushort FeralPower() => Power("FERAL_POWER");
    private static ushort HailstormPower() => Power("HAILSTORM_POWER");
    private static ushort IterationPower() => Power("ITERATION_POWER");
    private static ushort LoopPower() => Power("LOOP_POWER");
    private static ushort MachineLearningPower() => Power("MACHINE_LEARNING_POWER");
    private static ushort SmokestackPower() => Power("SMOKESTACK_POWER");
    private static ushort SpinnerPower() => Power("SPINNER_POWER");
    private static ushort StormPower() => Power("STORM_POWER");
    private static ushort ThunderPower() => Power("THUNDER_POWER");
    private static ushort TrashToTreasurePower() => Power("TRASH_TO_TREASURE_POWER");

    private static int BiasedCognitionExpected(
        int enemyHpCap, int gain, int decay, int orbs, int turns)
    {
        long focusTurns = 0;
        for (int turn = 0; turn < turns; turn++)
        {
            int focus = Math.Max(0, gain - turn * Math.Max(1, decay));
            focusTurns += focus;
            if (focus == 0)
                break;
        }
        return Math.Min(enemyHpCap, (int)Math.Min(int.MaxValue, focusTurns * orbs));
    }

    private static int FeralExpected(
        scoped in PowerEvidence.View view, in Battle parent, int gain)
    {
        int zero = view.CountZeroCostAttack();
        if (zero == 0)
            return 0;
        int before = Math.Min(Kernel.GetAmount(parent, Limits.PlayerOwner, FeralPower()),
            zero);
        int after = Math.Min(view.PlayerPowerAmount(FeralPower()), zero);
        return Mul(Mul(Math.Max(0, after - before), view.EnergyUnit()),
            view.RemainingTurns());
    }

    private static int LoopExpected(
        scoped in PowerEvidence.View view, int gain, int turns, int enemies)
    {
        if (view.Battle.OrbCount == 0)
            return 0;
        string orbId = Registry.Orb(view.Battle.Orbs[0].Id).NativeId;
        int passive = (int)Math.Ceiling(Math.Max(0m,
            Kernel.OrbPassiveValue(view.Battle, view.Rules, 0)));
        return orbId switch
        {
            "FROST_ORB" => Mul(BoundedPrevention(Mul(gain, passive),
                view.IncomingDamage()), turns),
            "PLASMA_ORB" => Mul(gain, Mul(passive, turns)),
            "GLASS_ORB" => Math.Min(view.RemainingEnemyHp(),
                Mul(Mul(gain, passive), Mul(turns, enemies))),
            "LIGHTNING_ORB" or "DARK_ORB" => Math.Min(view.RemainingEnemyHp(),
                Mul(gain, Mul(passive, turns))),
            _ => throw new InvalidDataException("defect-power-opening: unknown Loop orb " + orbId),
        };
    }

    private static int DarkEvokeExpected(
        scoped in PowerEvidence.View view, int gain, int orbCount, int turns, int enemies)
    {
        int evokes = Math.Min(orbCount, Mul(gain, turns));
        long damage = 0;
        for (int evoke = 0; evoke < evokes; evoke++)
        {
            int index = orbCount - 1 - evoke;
            string orbId = Registry.Orb(view.Battle.Orbs[index].Id).NativeId;
            decimal value = Kernel.OrbEvokeValue(view.Battle, view.Rules, index);
            if (orbId == "DARK_ORB")
            {
                int ticks = evoke / gain + 1;
                value += Math.Max(0m,
                    Kernel.OrbPassiveValue(view.Battle, view.Rules, index)) * ticks;
                damage += (long)Math.Ceiling(Math.Max(0m, value));
            }
            else if (orbId == "LIGHTNING_ORB")
            {
                damage += (long)Math.Ceiling(Math.Max(0m, value));
            }
            else if (orbId == "GLASS_ORB")
            {
                damage += (long)Math.Ceiling(Math.Max(0m, value * 2m)) * enemies;
            }
        }
        return Math.Min(view.RemainingEnemyHp(), (int)Math.Min(int.MaxValue, damage));
    }

    private static ushort BiasedCognitionPower() => Power("BIASED_COGNITION_POWER");
    private static ushort Power(string id)
        => Registry.TryPower(id, out ushort power) ? power
            : throw new InvalidDataException("defect-power-opening: unregistered power " + id);

    private static int CappedPerTurn(int enemyHpCap, int perTurn, int turns, int targets)
        => Math.Min(enemyHpCap, Mul(Mul(perTurn, Math.Max(0, turns)), Math.Max(1, targets)));

    private static int CappedDamage(int enemyHpCap, int perTrigger, int triggers)
        => Math.Min(enemyHpCap, Mul(perTrigger, Math.Max(0, triggers)));

    private static int BoundedPrevention(int rawBlock, int incomingDamage)
    {
        int ceiling = Math.Max(Math.Max(0, incomingDamage), rawBlock / 2);
        return Math.Min(rawBlock, ceiling);
    }

    private static int Mul(int left, int right)
        => (int)Math.Min(int.MaxValue, (long)left * right);
}
