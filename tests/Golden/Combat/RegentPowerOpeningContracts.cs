using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal Regent power-opening contracts. Every case builds a synthetic battle over the rule table
/// of one real Regent D1 root, sets exactly the power increment, stars, pile contents and incoming
/// damage that the chosen formula reads, and compares the projection with that formula.
/// </summary>
internal static class RegentPowerOpeningContracts
{
    private const string Root = "REGENT-018";
    private const int Incoming = 10;

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("regent-power-opening: " + message); }

    private static SearchCase Read()
    {
        using FileStream file = File.OpenRead($"tests/Golden/data/d1/quality-{Root}/config.br");
        using BrotliStream zip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new(); zip.CopyTo(bytes);
        return SearchCodec.DecodeRequest(bytes.ToArray());
    }

    private static ushort CardId(string nativeId)
        => Registry.TryCard(nativeId, out ushort id)
            ? id
            : throw new InvalidDataException("regent-power-opening: missing card " + nativeId);

    private static Battle EmptyBattle(int enemyHp = 100)
    {
        Battle battle = default;
        battle.Player.CurrentHp = 100;
        battle.Player.MaxHp = 100;
        battle.EnemyCount = 1;
        battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0].Creature.CurrentHp = enemyHp;
        battle.Enemies[0].Creature.MaxHp = enemyHp;
        battle.Enemies[0].Flags = EnemyFlags.InCombat;
        battle.Enemies[0].DeathPhase = DeathPhase.None;
        battle.Energy = 3;
        return battle;
    }

    private static void AddCard(SearchCase item, ref Battle battle, Pile pile, string nativeId)
        => Piles.Append(ref battle, pile, Kernel.CreateCard(item.Rules, CardId(nativeId)));

    // The remaining-turn estimate reads the fixture's live cards (a few Strikes deal about 6 a
    // turn), so 30 enemy HP gives the multi-turn horizon these formula checks need.
    private static ScoreFeatures Features(SearchCase item, int incoming, int enemyHp = 30)
        => item.Options.Scorer!.Features(item.Battles[0], 0) with
        {
            PlayerHp = 100,
            ProjectedPlayerHp = 100 - incoming,
            EnemyHp = enemyHp,
            AliveEnemyCount = 1,
            RetainedAttackValue = 1,
            LiveDeckSize = 5,
        };

    private static int Projection(SearchCase item, string nativeId, in Battle parent,
        in Battle child, int incoming = Incoming, int enemyHp = 30)
    {
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, incoming, enemyHp);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        return PowerEvidence.ProjectionPotential(CardId(nativeId), view, parent,
            features.ProjectedPlayerHp);
    }

    private static void AssertDamageCap(SearchCase item, string nativeId, Battle parent,
        Battle child, int raw)
    {
        Assert(raw > 3, nativeId + " cap fixture needs a raw value above 3");
        child.Enemies[0].Creature.CurrentHp = 3;
        child.Enemies[0].Creature.MaxHp = 3;
        Assert(Projection(item, nativeId, parent, child) == Math.Min(3, raw),
            nativeId + " caps damage at one living enemy's HP");

        child.EnemyCount = 2;
        child.RosterCount = 2;
        child.Roster[1] = 1;
        child.Enemies[1].Flags = EnemyFlags.InCombat;
        child.Enemies[1].DeathPhase = DeathPhase.None;
        child.Enemies[1].Creature.CurrentHp = 4;
        child.Enemies[1].Creature.MaxHp = 4;
        Assert(Projection(item, nativeId, parent, child) == Math.Min(7, raw),
            nativeId + " sums the remaining HP of primary enemies");

        child.Enemies[0].Creature.Block = 50;
        Assert(Projection(item, nativeId, parent, child) == Math.Min(7, raw),
            nativeId + " ignores enemy block");
        child.Enemies[0].Creature.CurrentHp = 0;
        child.Enemies[0].DeathPhase = DeathPhase.Reviving;
        Assert(Projection(item, nativeId, parent, child) == Math.Min(4, raw),
            nativeId + " ignores a reviving enemy's zero current HP");
    }

    private static int Bounded(int raw, int incoming)
        => Math.Min(raw, Math.Max(incoming, raw / 2));

    internal static void Run()
    {
        SearchCase item = Read();
        CheckArsenal(item);
        CheckBlackHole(item);
        CheckChildOfTheStars(item);
        CheckFurnace(item);
        CheckGenesis(item);
        CheckMonarchsGaze(item);
        CheckNeutronAegis(item);
        CheckOrbit(item);
        CheckPaleBlueDot(item);
        CheckParry(item);
        CheckPillarOfCreation(item);
        CheckSeekingEdge(item);
        CheckSpectrumShift(item);
        CheckSwordSage(item);
        CheckTheSealedThrone(item);
        CheckTyranny(item);
        CheckVoidForm(item);
        CheckRoyalties(item);
        Console.WriteLine("REGENT_POWER_OPENING cards=18 formulas/cap=pass");
    }

    private static void CheckArsenal(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        AddCard(item, ref parent, Pile.Hand, "GLIMMER");
        AddCard(item, ref parent, Pile.Hand, "FINESSE");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.Arsenal, 4);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int generation = view.CountCardGeneration();
        Assert(generation == 2, "Arsenal counts both generation sources");
        int expected = PowerEvidence.GrowthFrontier(view,
            damagePerAttack: 4 * generation,
            remainingTurns: view.RemainingTurns());
        int actual = Projection(item, "ARSENAL", parent, child);
        Assert(actual == expected && actual > 0,
            $"Arsenal growth frontier expected {expected} actual {actual}");
        AssertDamageCap(item, "ARSENAL", parent, child, expected);
        Battle noGain = child;
        Kernel.SetPowerAmount(ref noGain, Limits.PlayerOwner, PowerEvidenceIds.Arsenal, 0);
        Assert(Projection(item, "ARSENAL", parent, noGain) == 0,
            "Arsenal has no projection without an increment");
    }

    private static void CheckBlackHole(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        AddCard(item, ref parent, Pile.Hand, "FALLING_STAR");
        AddCard(item, ref parent, Pile.Hand, "GAMMA_BLAST");
        Battle child = parent;
        child.Stars = 2;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.BlackHole, 3);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int triggers = 2 + 2;
        int expected = 3 * triggers * Math.Max(1, features.AliveEnemyCount);
        int actual = Projection(item, "BLACK_HOLE", parent, child);
        Assert(actual == expected,
            $"Black Hole star/card triggers expected {expected} actual {actual}");
        AssertDamageCap(item, "BLACK_HOLE", parent, child, expected);
    }

    private static void CheckChildOfTheStars(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "FALLING_STAR");
        AddCard(item, ref parent, Pile.Hand, "GAMMA_BLAST");
        Battle child = parent;
        child.Stars = 3;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner,
            PowerEvidenceIds.ChildOfTheStars, 2);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int blocks = view.StarSpendCapacity();
        Assert(blocks == 3, "Child of the Stars spends at most the current stars");
        int expected = Bounded(2 * blocks, Incoming);
        int actual = Projection(item, "CHILD_OF_THE_STARS", parent, child);
        Assert(actual == expected,
            $"Child of the Stars block triggers expected {expected} actual {actual}");
    }

    private static void CheckFurnace(SearchCase item)
    {
        Battle parent = EmptyBattle();
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.Furnace, 5);
        PowerEvidence.View view = new(child, Features(item, Incoming), item.Rules,
            item.Options.Scorer!);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = 5 * turns;
        int actual = Projection(item, "FURNACE", parent, child);
        Assert(actual == expected, $"Furnace resource expected {expected} actual {actual}");
    }

    private static void CheckGenesis(SearchCase item)
    {
        Battle parent = EmptyBattle();
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.Genesis, 2);
        PowerEvidence.View view = new(child, Features(item, Incoming), item.Rules,
            item.Options.Scorer!);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = 2 * turns;
        int actual = Projection(item, "GENESIS", parent, child);
        Assert(actual == expected, $"Genesis resource expected {expected} actual {actual}");
    }

    private static void CheckMonarchsGaze(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        AddCard(item, ref parent, Pile.Hand, "FALLING_STAR");
        AddCard(item, ref parent, Pile.Hand, "GAMMA_BLAST");
        AddCard(item, ref parent, Pile.Hand, "DEFEND_REGENT");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.MonarchsGaze, 2);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int hits = view.IncomingAttackHits(nextTurn: false);
        int expected = Bounded(2 * hits, Incoming) * view.RemainingTurns();
        int actual = Projection(item, "MONARCHS_GAZE", parent, child);
        Assert(actual == expected,
            $"Monarchs Gaze bounded prevention expected {expected} actual {actual}");
        Battle capped = child;
        capped.Enemies[0].Creature.CurrentHp = 3;
        capped.Enemies[0].Creature.MaxHp = 3;
        Assert(Projection(item, "MONARCHS_GAZE", parent, capped) == expected,
            "Monarchs Gaze prevention is not enemy-HP damage capped");
    }

    private static void CheckNeutronAegis(SearchCase item)
    {
        Battle parent = EmptyBattle();
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.Plating, 8);
        PowerEvidence.View view = new(child, Features(item, Incoming), item.Rules,
            item.Options.Scorer!);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = Bounded(8, Incoming) * turns;
        int actual = Projection(item, "NEUTRON_AEGIS", parent, child);
        Assert(actual == expected, $"Neutron Aegis block expected {expected} actual {actual}");
    }

    private static void CheckOrbit(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        AddCard(item, ref parent, Pile.Hand, "DEFEND_REGENT");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.Orbit, 1);
        Scorer scorer = item.Options.Scorer!;
        // The hand Strike deals 6 a turn in the remaining-turn estimate: 25 enemy HP -> R = 5.
        ScoreFeatures features = Features(item, Incoming, enemyHp: 25);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int payouts = view.ModifiedMaxEnergy() * turns / 4;
        int expected = view.EnergyUnit() * payouts;
        int actual = Projection(item, "ORBIT", parent, child, enemyHp: 25);
        Assert(payouts == 3 && actual == expected,
            $"Orbit energy payouts expected {expected} actual {actual}");
    }

    private static void CheckPaleBlueDot(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.PaleBlueDot, 2);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = 2 * view.EnergyUnit() * turns;
        int actual = Projection(item, "PALE_BLUE_DOT", parent, child);
        Assert(actual == expected, $"Pale Blue Dot draw value expected {expected} actual {actual}");
    }

    private static void CheckParry(SearchCase item)
    {
        Battle parent = EmptyBattle();
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.Parry, 5);
        PowerEvidence.View view = new(child, Features(item, Incoming), item.Rules,
            item.Options.Scorer!);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = Bounded(5, Incoming) * turns;
        int actual = Projection(item, "PARRY", parent, child);
        Assert(actual == expected, $"Parry current stack block expected {expected} actual {actual}");
        Assert(Projection(item, "PARRY", parent, child, incoming: 100) == 5 * turns,
            "Parry uses current stacks across the remaining turns");
        Battle noParry = child;
        Kernel.SetPowerAmount(ref noParry, Limits.PlayerOwner, PowerEvidenceIds.Parry, 0);
        Assert(Projection(item, "PARRY", parent, noParry) == 0,
            "Parry has no projection without current stacks");
    }

    private static void CheckPillarOfCreation(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "GLIMMER");
        AddCard(item, ref parent, Pile.Hand, "FINESSE");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner,
            PowerEvidenceIds.PillarOfCreation, 2);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int generation = view.CountCardGeneration();
        Assert(generation == 2, "Pillar of Creation counts both generation sources");
        int expected = Bounded(2 * generation, Incoming);
        int actual = Projection(item, "PILLAR_OF_CREATION", parent, child);
        Assert(actual == expected,
            $"Pillar of Creation block triggers expected {expected} actual {actual}");
    }

    private static void CheckSeekingEdge(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        Battle child = parent;
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = view.EnergyUnit() * turns;
        int actual = Projection(item, "SEEKING_EDGE", parent, child);
        Assert(actual == expected, $"Seeking Edge resource expected {expected} actual {actual}");
    }

    private static void CheckSpectrumShift(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.SpectrumShift, 1);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = view.EnergyUnit() * turns;
        int actual = Projection(item, "SPECTRUM_SHIFT", parent, child);
        Assert(actual == expected, $"Spectrum Shift card access expected {expected} actual {actual}");
    }

    private static void CheckSwordSage(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        Battle child = parent;
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = Math.Max(1, view.MaxDamage()) * turns;
        int actual = Projection(item, "SWORD_SAGE", parent, child);
        Assert(actual == expected, $"Sword Sage replay damage expected {expected} actual {actual}");
        AssertDamageCap(item, "SWORD_SAGE", parent, child, expected);
    }

    private static void CheckTheSealedThrone(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        AddCard(item, ref parent, Pile.Hand, "DEFEND_REGENT");
        AddCard(item, ref parent, Pile.Draw, "STRIKE_REGENT");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner,
            PowerEvidenceIds.TheSealedThrone, 1);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        Assert(view.LiveCount == 3, "Sealed Throne counts hand and draw cards");
        int expected = view.EnergyUnit() * 3;
        int actual = Projection(item, "THE_SEALED_THRONE", parent, child);
        Assert(actual == expected,
            $"Sealed Throne per-card star triggers expected {expected} actual {actual}");
    }

    private static void CheckTyranny(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.Tyranny, 1);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = view.EnergyUnit() * turns;
        int actual = Projection(item, "TYRANNY", parent, child);
        Assert(actual == expected, $"Tyranny extra draw expected {expected} actual {actual}");
    }

    private static void CheckVoidForm(SearchCase item)
    {
        Battle parent = EmptyBattle();
        AddCard(item, ref parent, Pile.Hand, "STRIKE_REGENT");
        Battle child = parent;
        Kernel.SetPowerAmount(ref child, Limits.PlayerOwner, PowerEvidenceIds.VoidForm, 2);
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        int turns = Math.Max(0, view.RemainingTurns() - 1);
        int expected = 2 * view.EnergyUnit() * turns;
        int actual = Projection(item, "VOID_FORM", parent, child);
        Assert(actual == expected, $"Void Form free cards expected {expected} actual {actual}");
    }

    private static void CheckRoyalties(SearchCase item)
    {
        Battle parent = EmptyBattle();
        Battle child = parent;
        Scorer scorer = item.Options.Scorer!;
        ScoreFeatures features = Features(item, Incoming);
        PowerEvidence.View view = new(child, features, item.Rules, scorer);
        ushort cardId = CardId("ROYALTIES");
        Assert(PowerEvidence.ProjectionPotential(cardId, view, parent,
            features.ProjectedPlayerHp) == 0
            && !PowerEvidence.HasTriggerEvidence(cardId, parent, view),
            "Royalties has no in-combat opening projection");
    }
}
