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
        using FileStream file = File.OpenRead($"tests/Golden/data/d1/quality-{root}/config.br");
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
            Assert(Project(item, scorer, "AFTERIMAGE", parent, child, pf, cf) == 8,
                "Afterimage one block per played card");
        }

        // Footwork/Abrasive: Defend has 5 block with 10 incoming; one Dexterity turns it into 6,
        // removing one projected HP loss -> 1 * 8. No future draw is present in this state.
        {
            Battle parent = State(item, "DEFEND_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Dexterity, 1);
            ScoreFeatures pf = Features(scorer, parent, 10) with { EnemyHp = 600 };
            ScoreFeatures cf = Features(scorer, child, 10) with { EnemyHp = 600 };
            Assert(Project(item, scorer, "FOOTWORK", parent, child, pf, cf) == 8,
                "Footwork current-hand Dexterity frontier");
            Assert(Project(item, scorer, "ABRASIVE", parent, child, pf, cf) == 8,
                "Abrasive shares the Dexterity frontier");
            ScoreFeatures shortFight = Features(scorer, parent, 10);
            Assert(Project(item, scorer, "FOOTWORK", parent, child, shortFight, shortFight) == 0,
                "Footwork keeps the trigger floor below the long-fight threshold");
        }

        // Accuracy: a 4-damage Shiv with one target gains 4 per play; one play is one extra hit.
        {
            Battle parent = State(item, "SHIV");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Accuracy, 4);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            Assert(Project(item, scorer, "ACCURACY", parent, child, pf, cf) == 4,
                "Accuracy adds the increment once per Shiv target");
        }

        // Fan of Knives: the retained trigger floor only. The upstream AoE frontier would credit
        // the one-target-to-two-target Shiv uplift, but D2 proved it pushes worse Silent routes.
        {
            Battle parent = State(item, "SHIV");
            Battle child = parent;
            AddSecondEnemy(ref child);
            SetPower(ref child, PowerEvidenceIds.FanOfKnives, 1);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            PowerEvidence.View view = new(child, cf, item.Rules, scorer);
            Assert(Project(item, scorer, "FAN_OF_KNIVES", parent, child, pf, cf) == 0
                && PowerEvidence.TriggerProjectionFloor(Card("FAN_OF_KNIVES"), view) == 2,
                "Fan of Knives keeps the trigger floor only");
        }

        // Infinite Blades: the retained trigger floor only (no opening projection).
        {
            Battle parent = State(item);
            Battle child = parent;
            ScoreFeatures features = scorer.Features(child, 0) with
            {
                ProjectedPlayerHp = child.Player.CurrentHp,
                EnemyHp = 600,
                RetainedAttackValue = 100,
                LiveDeckSize = 1,
            };
            Assert(Project(item, scorer, "INFINITE_BLADES", parent, child, features, features) == 0,
                "Infinite Blades keeps the trigger floor only");
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

        // Phantom Blades: one Shiv gains 1 first-Shiv damage (4 -> 5, +1) and becomes retained;
        // with no other retained cards the next-turn transition pays 4, total 5.
        {
            Battle parent = State(item, "SHIV");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.PhantomBlades, 1);
            child.Cards[0].Keywords |= Keywords.Retain;
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            Assert(Project(item, scorer, "PHANTOM_BLADES", parent, child, pf, cf) == 5,
                "Phantom Blades first-Shiv damage plus retained-hand transition");
        }

        // Serpent Form: the retained resource estimate. A 6-value hand over one remaining turn
        // after the current one is 6 * 1 = 6.
        {
            Battle parent = State(item, "STRIKE_SILENT");
            Battle child = parent;
            ScoreFeatures features = scorer.Features(child, 0) with
            {
                ProjectedPlayerHp = child.Player.CurrentHp,
                EnemyHp = 600,
                RetainedAttackValue = 100,
                LiveDeckSize = 1,
            };
            Assert(Project(item, scorer, "SERPENT_FORM", parent, child, features, features) == 6,
                "Serpent Form retained resource estimate");
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
            Assert(Project(item, scorer, "SPEEDSTER", parent, child, pf, cf) == 12,
                "Speedster damage per non-hand draw and target");
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

        // Tools of the Trade recurring floor: with no draw pile the next-turn transition is zero,
        // but a 6-value hand over nine remaining turns still contributes 6 * 9 / 16 = 3.
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
            Assert(Project(item, scorer, "TOOLS_OF_THE_TRADE", parent, child, features, features) == 3,
                "Tools of the Trade recurring resource floor");
        }

        // Tracking: a 6-damage Strike against a Weak enemy gains 50% -> 3.
        {
            Battle parent = State(item, "STRIKE_SILENT");
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Tracking, 50);
            Kernel.SetPowerAmount(
                ref child, Kernel.EnemyOwner(0), PowerEvidenceIds.Weak, 1);
            ScoreFeatures pf = Features(scorer, parent, 10);
            ScoreFeatures cf = Features(scorer, child, 10);
            Assert(Project(item, scorer, "TRACKING", parent, child, pf, cf) == 3,
                "Tracking Weak-target attack bonus");
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
        // the saved 10 HP is worth 8 each.
        {
            Battle parent = State(item);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Intangible, 1);
            ScoreFeatures cf = scorer.Features(child, 0) with { ProjectedPlayerHp = 20 };
            Assert(Project(item, scorer, "WRAITH_FORM", parent, child, cf, cf, 10) == 80,
                "Wraith Form projected HP prevention");
        }

        Console.WriteLine("SILENT_POWER_PROJECTION contracts=14");
    }
}
