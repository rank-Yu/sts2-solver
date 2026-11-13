using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal per-card contracts for the Silent opening projections. Each battle is constructed from a
/// frozen D1 rules/scorer table with a default empty combat state, so the expected integer is the
/// value of the selected formula on exactly the state the projection reads.
/// </summary>
internal static class SilentPowerProjectionContracts
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("silent-power-projection: " + message); }

    private static SearchCase Read(string root)
    {
        using FileStream file = File.OpenRead(GoldenFixtures.PathOf($"d1/quality-{root}/config.br"));
        using BrotliStream zip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new(); zip.CopyTo(bytes);
        return SearchCodec.DecodeRequest(bytes.ToArray());
    }

    private static Battle State(SearchCase item, params string[] hand)
    {
        Battle battle = default;
        battle.Player.CurrentHp = 70;
        battle.Player.MaxHp = 70;
        battle.Player.Flags = CreatureFlags.Present;
        battle.Energy = 3;
        battle.MaxHandSize = 10;
        battle.PlayerTurn = 1;
        battle.EnemyCount = 1;
        battle.RosterCount = 1;
        battle.Roster[0] = 0;
        battle.Enemies[0].Creature.CurrentHp = 100;
        battle.Enemies[0].Creature.MaxHp = 100;
        battle.Enemies[0].Creature.Flags = CreatureFlags.Present;
        battle.Enemies[0].Flags = EnemyFlags.InCombat;
        foreach (string id in hand)
        {
            if (!Registry.TryCard(id, out ushort cardId))
                throw new InvalidDataException("silent-power-projection: missing " + id);
            Card card = Kernel.CreateCard(item.Rules, cardId);
            Piles.Append(ref battle, Pile.Hand, card);
        }
        return battle;
    }

    private static void AddDraw(SearchCase item, ref Battle battle, params string[] ids)
    {
        foreach (string id in ids)
        {
            if (!Registry.TryCard(id, out ushort cardId))
                throw new InvalidDataException("silent-power-projection: missing " + id);
            Card card = Kernel.CreateCard(item.Rules, cardId);
            Piles.Append(ref battle, Pile.Draw, card);
        }
    }

    private static ushort Card(string id)
    {
        if (!Registry.TryCard(id, out ushort cardId))
            throw new InvalidDataException("silent-power-projection: missing " + id);
        return cardId;
    }

    private static void SetPower(ref Battle battle, ushort id, int amount)
        => Kernel.SetPowerAmount(ref battle, Limits.PlayerOwner, id, amount);

    private static ScoreFeatures Features(Scorer scorer, in Battle battle, int incomingDamage)
        => scorer.Features(battle, 0) with
        {
            ProjectedPlayerHp = battle.Player.CurrentHp - incomingDamage,
        };

    private static int Project(SearchCase item, Scorer scorer, string card,
        in Battle parent, in Battle child, in ScoreFeatures parentFeatures,
        in ScoreFeatures childFeatures, int parentProjectedPlayerHp = 0)
    {
        PowerEvidence.View view = new(child, childFeatures, item.Rules, scorer);
        return PowerEvidence.ProjectionPotential(
            Card(card), view, parent, parentProjectedPlayerHp == 0
                ? parentFeatures.ProjectedPlayerHp
                : parentProjectedPlayerHp);
    }

    private static void AddSecondEnemy(ref Battle battle)
    {
        battle.EnemyCount = 2;
        battle.RosterCount = 2;
        battle.Roster[1] = 1;
        battle.Enemies[1] = battle.Enemies[0];
        battle.Enemies[1].Creature.CurrentHp = 100;
    }

    /// <summary>
    /// Damage projections use <see cref="PowerEvidence.View.RemainingEnemyHp"/>: the summed current
    /// HP of living primary enemies. Exercise a small single enemy, two enemies, enemy block, and a
    /// reviving enemy whose current HP is zero. All-enemy uplifts that last only while the extra
    /// targets live (<paramref name="killOrderLife"/>) shrink with those enemies' HP, so on the
    /// multi-enemy fixtures they must stay positive and never exceed the cap.
    /// </summary>
    private static void AssertDamageCap(SearchCase item, Scorer scorer, string card,
        in Battle parent, in Battle child, in ScoreFeatures parentFeatures,
        in ScoreFeatures childFeatures, int rawActual, bool killOrderLife = false)
    {
        void AssertCapped(int actual, int cap, string message)
        {
            bool ok = killOrderLife
                ? actual > 0 && actual <= Math.Min(cap, rawActual)
                : actual == Math.Min(cap, rawActual);
            Assert(ok, card + " " + message);
        }

        Battle capped = child;
        capped.EnemyCount = 1;
        capped.RosterCount = 1;
        capped.Roster[0] = 0;
        capped.Enemies[0].Flags = EnemyFlags.InCombat;
        capped.Enemies[0].DeathPhase = DeathPhase.None;
        capped.Enemies[0].SteamPhase = SteamPhase.None;
        capped.Enemies[0].Creature.CurrentHp = 3;
        capped.Enemies[0].Creature.MaxHp = Math.Max(capped.Enemies[0].Creature.MaxHp, 3);
        capped.Enemies[0].Creature.Block = 0;
        PowerEvidence.View cappedView = new(capped, childFeatures, item.Rules, scorer);
        Assert(cappedView.RemainingEnemyHp() == 3, card + " single-enemy cap");
        int cappedActual = Project(item, scorer, card, parent, capped,
            parentFeatures, childFeatures);
        Assert(cappedActual == Math.Min(3, rawActual),
            card + " single-enemy capped projection");
        if (rawActual > 3)
            Assert(cappedActual < rawActual, card + " cap must reduce a positive damage projection");

        capped.EnemyCount = 2;
        capped.RosterCount = 2;
        capped.Roster[1] = 1;
        capped.Enemies[1] = capped.Enemies[0];
        capped.Enemies[1].Creature.CurrentHp = 4;
        capped.Enemies[1].Creature.MaxHp = 4;
        capped.Enemies[1].Creature.Block = 0;
        Assert(cappedView.RemainingEnemyHp() == 7, card + " summed-enemy cap");
        AssertCapped(Project(item, scorer, card, parent, capped, parentFeatures, childFeatures),
            7, "summed living cap");

        capped.Enemies[0].Creature.Block = 50;
        Assert(cappedView.RemainingEnemyHp() == 7, card + " cap ignores enemy block");

        capped.Enemies[0].Creature.CurrentHp = 0;
        capped.Enemies[0].DeathPhase = DeathPhase.Reviving;
        // Tracking's Weak-target multiplier is per enemy power, not an Enemy struct field; keep a
        // Weak source alive on the second enemy so the damage term itself is unchanged.
        Kernel.SetPowerAmount(
            ref capped, Kernel.EnemyOwner(1), PowerEvidenceIds.Weak, 1);
        Assert(cappedView.RemainingEnemyHp() == 4, card + " cap ignores reviving current HP");
        AssertCapped(Project(item, scorer, card, parent, capped, parentFeatures, childFeatures),
            4, "reviving-enemy cap");
    }

    internal static void Run()
    {
        SearchCase item = Read("SILENT-014");
        Scorer scorer = item.Options.Scorer!;

        // Afterimage: Strike deals 6 with 10 incoming; one Afterimage stack turns the same play into
        // 6 damage + 1 block, removing one projected HP loss -> 1 * 8.
        {
            Battle parent = State(item, "STRIKE_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Afterimage, 1);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            PowerEvidence.View view = new(child, cf, item.Rules, scorer);
            Assert(Project(item, scorer, "AFTERIMAGE", parent, child, pf, cf)
                == view.RemainingTurns(),
                "Afterimage repeats its current-hand block frontier");
        }

        // Footwork/Abrasive: Defend has 5 block with 10 incoming; one Dexterity turns it into 6,
        // removing one projected HP loss -> 1 * 8. No future draw is present in this state.
        {
            Battle parent = State(item, "DEFEND_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Dexterity, 1);
            ScoreFeatures pf = Features(scorer, parent, 10) with { EnemyHp = 600 };
            ScoreFeatures cf = Features(scorer, child, 10) with { EnemyHp = 600 };
            PowerEvidence.View view = new(child, cf, item.Rules, scorer);
            // One block a turn against 10 incoming is fully credited every turn (per-turn bound).
            int footworkExpected = view.RemainingTurns();
            Assert(Project(item, scorer, "FOOTWORK", parent, child, pf, cf) == footworkExpected,
                "Footwork repeats the current-hand Dexterity frontier");
            Assert(Project(item, scorer, "ABRASIVE", parent, child, pf, cf) == footworkExpected,
                "Abrasive shares the Dexterity frontier");
            ScoreFeatures shortFight = Features(scorer, parent, 10);
            Assert(Project(item, scorer, "FOOTWORK", parent, child, shortFight, shortFight)
                == footworkExpected, "Footwork uses the same frontier on short fights");
        }

        // Accuracy: a 4-damage Shiv with one target gains 4 per play; one play is one extra hit.
        {
            Battle parent = State(item, "SHIV");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Accuracy, 4);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            int expected = Project(item, scorer, "ACCURACY", parent, child, pf, cf);
            PowerEvidence.View accuracyView = new(child, cf, item.Rules, scorer);
            Assert(expected == 4 * accuracyView.RemainingTurns(),
                "Accuracy repeats the increment once per Shiv target");
            AssertDamageCap(item, scorer, "ACCURACY", parent, child, pf, cf, expected);
        }

        // Fan of Knives: the AoE frontier credits the one-target-to-two-target Shiv uplift, plus
        // any Shivs this copy generates (none here). The uplift lasts while the extra target lives:
        // the enemy-turns beyond the last enemys R, i.e. the first-killed enemys kill-order life,
        // which is shorter than R. The trigger floor remains two enemies.
        {
            Battle parent = State(item, "SHIV");
            Battle child = parent;
            AddSecondEnemy(ref child);
            SetPower(ref child, PowerEvidenceIds.FanOfKnives, 1);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            PowerEvidence.View view = new(child, cf, item.Rules, scorer);
            int expected = Project(item, scorer, "FAN_OF_KNIVES", parent, child, pf, cf);
            int extraTargetTurns = view.EnemyTurns(0) - view.RemainingTurns();
            Assert(extraTargetTurns > 0 && extraTargetTurns < view.RemainingTurns(),
                "the first-killed enemy lives fewer turns than the whole fight");
            Assert(expected == 4 * extraTargetTurns
                && PowerEvidence.TriggerProjectionFloor(Card("FAN_OF_KNIVES"), view) == 2,
                "Fan of Knives repeats the AoE frontier while the extra target lives, plus trigger floor");
            AssertDamageCap(item, scorer, "FAN_OF_KNIVES", parent, child, pf, cf, expected,
                killOrderLife: true);
        }

        // Infinite Blades: one generated Shiv per future turn, base Shiv damage, current target
        // count. With no live cards the remaining-turn estimate deals its minimum 1 damage a turn,
        // so EnemyHp 2 gives RemainingTurns = 2: one future turn of a 4-damage Shiv.
        {
            Battle parent = State(item);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.InfiniteBlades, 1);
            ScoreFeatures features = scorer.Features(child, 0) with
            {
                ProjectedPlayerHp = child.Player.CurrentHp,
                EnemyHp = 2,
                LiveDeckSize = 1,
            };
            int expected = Project(item, scorer, "INFINITE_BLADES",
                parent, child, features, features);
            Assert(expected == 4, "Infinite Blades future-turn Shiv damage");
            AssertDamageCap(item, scorer, "INFINITE_BLADES",
                parent, child, features, features, expected);
        }

        // Master Planner: Survivor (value 6) is the highest-value current-hand skill and also the
        // only discard window, so the one-seed DP returns 6 / payoff turn 1.
        {
            Battle parent = State(item, "SURVIVOR", "DEFEND_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.MasterPlanner, 1);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            Assert(Project(item, scorer, "MASTER_PLANNER", parent, child, pf, cf) == 6,
                "Master Planner one-seed discard-window DP");
        }

        // Phantom Blades: four added first-Shiv damage (4 -> 8) plus the retained-hand transition
        // (4). The damage half is capped by remaining enemy HP; the retention half is resource.
        {
            Battle parent = State(item, "SHIV");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.PhantomBlades, 4);
            child.Cards[0].Keywords |= Keywords.Retain;
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            int expected = Project(item, scorer, "PHANTOM_BLADES", parent, child, pf, cf);
            PowerEvidence.View phantomView = new(child, cf, item.Rules, scorer);
            Assert(expected == 4 * phantomView.RemainingTurns() + 4,
                "Phantom Blades repeats first-Shiv damage plus one retained-hand transition");
            Battle capped = child;
            capped.Enemies[0].Creature.CurrentHp = 3;
            capped.Enemies[0].Creature.MaxHp = Math.Max(capped.Enemies[0].Creature.MaxHp, 3);
            PowerEvidence.View cappedView = new(capped, cf, item.Rules, scorer);
            Assert(cappedView.RemainingEnemyHp() == 3, "Phantom Blades damage cap");
            Assert(Project(item, scorer, "PHANTOM_BLADES", parent, capped, pf, cf) == 7,
                "Phantom Blades caps only the damage half (3 + retained 4)");
        }

        // Serpent Form: each played card gains the increment as damage; one Strike with five
        // stacks gains five damage on the current-hand frontier.
        {
            Battle parent = State(item, "STRIKE_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.SerpentForm, 5);
            ScoreFeatures features = scorer.Features(child, 0) with
            {
                ProjectedPlayerHp = child.Player.CurrentHp,
                EnemyHp = 600,
                RetainedAttackValue = 100,
                LiveDeckSize = 1,
            };
            int expected = Project(item, scorer, "SERPENT_FORM",
                parent, child, features, features);
            PowerEvidence.View serpentView = new(child, features, item.Rules, scorer);
            Assert(expected == 5 * serpentView.RemainingTurns(),
                "Serpent Form repeats per-card damage frontier");
            AssertDamageCap(item, scorer, "SERPENT_FORM",
                parent, child, features, features, expected);
        }

        // Speedster: Acrobatics draws 3 outside the hand draw; 2 damage to each of two enemies is
        // 3 * 2 * 2 = 12.
        {
            Battle parent = State(item, "ACROBATICS");
            Battle child = parent;
            AddSecondEnemy(ref child);
            SetPower(ref child, PowerEvidenceIds.Speedster, 2);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            int expected = Project(item, scorer, "SPEEDSTER", parent, child, pf, cf);
            PowerEvidence.View speedsterView = new(child, cf, item.Rules, scorer);
            int burstDraws = speedsterView.DrawCount(speedsterView.HandCard(0));
            int expectedSpeedster = Math.Min(speedsterView.RemainingEnemyHp(),
                4 * (burstDraws + speedsterView.ModifiedHandDraw()
                    * speedsterView.RemainingTurns()));
            Assert(expected == expectedSpeedster,
                "Speedster counts current-hand draws once and natural draws per turn");
            AssertDamageCap(item, scorer, "SPEEDSTER", parent, child, pf, cf, expected);
        }

        // Tools of the Trade: the power adds one draw, so the baseline draws six and the tool draws
        // seven then discards the lowest. Values [6,4,4,6,9,6,6]: 41 - 4 - 35 = 2.
        {
            Battle parent = State(item);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.ToolsOfTheTrade, 1);
            AddDraw(item, ref child, "STRIKE_SILENT", "DEFEND_SILENT", "SHIV",
                "STRIKE_SILENT", "ACROBATICS", "SURVIVOR", "SURVIVOR");
            ScoreFeatures features = scorer.Features(child, 0) with
            {
                ProjectedPlayerHp = child.Player.CurrentHp,
                EnemyHp = 2000,
                RetainedAttackValue = 100,
                LiveDeckSize = 1,
            };
            Assert(Project(item, scorer, "TOOLS_OF_THE_TRADE", parent, child, features, features) == 2,
                "Tools of the Trade draw/discard transition");
        }

        // Tools of the Trade without a draw pile: the next-turn draw/discard transition is zero,
        // and the old resource floor is intentionally gone.
        {
            Battle parent = State(item, "STRIKE_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.ToolsOfTheTrade, 1);
            ScoreFeatures features = scorer.Features(child, 0) with
            {
                ProjectedPlayerHp = child.Player.CurrentHp,
                EnemyHp = 6000,
                RetainedAttackValue = 100,
                LiveDeckSize = 1,
            };
            Assert(Project(item, scorer, "TOOLS_OF_THE_TRADE", parent, child, features, features) == 0,
                "Tools of the Trade draw/discard transition without a draw pile");
        }

        // Tracking: a 6-damage Strike against a Weak enemy gains 100% -> 6.
        {
            Battle parent = State(item, "STRIKE_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Tracking, 100);
            Kernel.SetPowerAmount(
                ref child, Kernel.EnemyOwner(0), PowerEvidenceIds.Weak, 1);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            int expected = Project(item, scorer, "TRACKING", parent, child, pf, cf);
            PowerEvidence.View trackingView = new(child, cf, item.Rules, scorer);
            Assert(expected == Math.Min(trackingView.RemainingEnemyHp(),
                    6 * trackingView.RemainingTurns()), "Tracking repeats Weak-target attack bonus");
            AssertDamageCap(item, scorer, "TRACKING", parent, child, pf, cf, expected);
        }

        // Well Laid Plans: retaining a 6-value Strike with one energy cost leaves 6 payable value
        // and blocks no draw because the hand is far below the limit.
        {
            Battle parent = State(item, "STRIKE_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.WellLaidPlans, 1);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            Assert(Project(item, scorer, "WELL_LAID_PLANS", parent, child, pf, cf) == 6,
                "Well Laid Plans retained-hand transition");
        }

        // Wraith Form: the constructed child projects 20 HP against a parent projection of 10, so
        // the saved 10 HP is worth 8 each. One Defend in hand loses one block per Wraith Form
        // Dexterity tick, i.e. 8 score units.
        {
            Battle parent = State(item, "DEFEND_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Intangible, 1);
            SetPower(ref child, PowerEvidenceIds.WraithForm, 1);
            ScoreFeatures cf = scorer.Features(child, 0) with { ProjectedPlayerHp = 20 };
            Assert(Project(item, scorer, "WRAITH_FORM", parent, child, cf, cf, 10) == 72,
                "Wraith Form projected HP prevention minus Dexterity cost");
        }

        Console.WriteLine("SILENT_POWER_PROJECTION contracts=14 cap=pass");
    }
}
