using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal opening-projection contracts for every Necrobinder power card. The battle is built from
/// the frozen Necrobinder rules/scorer table with a controlled hand, enemy HP and parent/child power
/// increment. Damage projections are checked twice: the uncapped formula and the remaining-enemy-HP
/// cap used by the production projection.
/// </summary>
internal static class NecrobinderPowerOpeningContracts
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidDataException("necrobinder-power-opening: " + message); }

    private static Battle State(SearchCase item, string[] hand, int enemyHp = 10_000)
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
        battle.Enemies[0].Creature.CurrentHp = enemyHp;
        battle.Enemies[0].Creature.MaxHp = enemyHp;
        battle.Enemies[0].Creature.Flags = CreatureFlags.Present;
        battle.Enemies[0].Flags = EnemyFlags.InCombat;
        battle.Enemies[0].DeathPhase = DeathPhase.None;
        foreach (string id in hand)
        {
            if (!Registry.TryCard(id, out ushort cardId))
                throw new InvalidDataException("necrobinder-power-opening: missing " + id);
            Card card = Kernel.CreateCard(item.Rules, cardId);
            Piles.Append(ref battle, Pile.Hand, card);
        }
        return battle;
    }

    private static ushort Card(string id)
    {
        if (!Registry.TryCard(id, out ushort cardId))
            throw new InvalidDataException("necrobinder-power-opening: missing " + id);
        return cardId;
    }

    private static void SetVar(ref Battle battle, int index, short value)
        => battle.Cards[index].Var0 = value;

    private static void SetCost(ref Battle battle, int index, sbyte cost)
        => battle.Cards[index].BaseCost = cost;

    private static void SetEthereal(ref Battle battle, int index)
        => battle.Cards[index].Keywords |= Keywords.Ethereal;

    private static void SetPower(ref Battle battle, ushort id, int amount)
        => Kernel.SetPowerAmount(ref battle, Limits.PlayerOwner, id, amount);

    private static ScoreFeatures Features(in Battle battle, int incomingDamage)
        => new()
        {
            PlayerHp = 70,
            ProjectedPlayerHp = 70 - incomingDamage,
            EnemyHp = 1_000_000,
            RetainedAttackValue = 1,
            LiveDeckSize = 1,
            AliveEnemyCount = 1,
            OstyHp = (battle.Flags & BattleFlags.OstyPresent) != 0
                ? battle.Osty.CurrentHp : 0,
            OstyMaxHp = (battle.Flags & BattleFlags.OstyPresent) != 0
                ? battle.Osty.MaxHp : 0,
        };

    private static int Project(SearchCase item, Scorer scorer, string card,
        in Battle parent, in Battle child, int incomingDamage = 8)
    {
        ScoreFeatures parentFeatures = Features(parent, incomingDamage);
        ScoreFeatures childFeatures = Features(child, incomingDamage);
        PowerEvidence.View view = new(child, childFeatures, item.Rules, scorer);
        return PowerEvidence.ProjectionPotential(
            Card(card), view, parent, parentFeatures.ProjectedPlayerHp);
    }

    private static int ProjectCapped(SearchCase item, Scorer scorer, string card,
        in Battle parent, in Battle child, int enemyHp, int incomingDamage = 8)
    {
        Battle capped = child;
        capped.Enemies[0].Creature.CurrentHp = enemyHp;
        capped.Enemies[0].Creature.MaxHp = enemyHp;
        return Project(item, scorer, card, parent, capped, incomingDamage);
    }

    /// <summary>
    /// Exercise the full remaining-enemy-HP cap: one small enemy, two enemies, enemy block, and a
    /// reviving enemy whose current HP is zero. Controlled features keep the formula inputs fixed.
    /// </summary>
    private static void AssertDamageCap(SearchCase item, Scorer scorer, string card,
        in Battle parent, in Battle child, int raw)
    {
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
        PowerEvidence.View view = new(capped, Features(capped, 8), item.Rules, scorer);
        Assert(view.RemainingEnemyHp() == 3, card + " single-enemy cap");
        Assert(Project(item, scorer, card, parent, capped) == Math.Min(3, raw),
            card + " single-enemy capped projection");

        capped.EnemyCount = 2;
        capped.RosterCount = 2;
        capped.Roster[1] = 1;
        capped.Enemies[1] = capped.Enemies[0];
        capped.Enemies[1].Creature.CurrentHp = 4;
        capped.Enemies[1].Creature.Block = 0;
        Assert(view.RemainingEnemyHp() == 7, card + " summed-enemy cap");
        Assert(Project(item, scorer, card, parent, capped) == Math.Min(7, raw),
            card + " summed living cap");

        capped.Enemies[0].Creature.Block = 50;
        Assert(view.RemainingEnemyHp() == 7, card + " cap ignores enemy block");

        capped.Enemies[0].Creature.CurrentHp = 0;
        capped.Enemies[0].DeathPhase = DeathPhase.Reviving;
        Assert(view.RemainingEnemyHp() == 4, card + " cap ignores reviving current HP");
        Assert(Project(item, scorer, card, parent, capped) == Math.Min(4, raw),
            card + " reviving-enemy cap");
    }

    internal static void Run()
    {
        SearchCase item = PowerEvidenceContracts.ReadRoot("quality-NECROBINDER-013");
        Scorer scorer = item.Options.Scorer
            ?? throw new InvalidDataException("necrobinder-power-opening: missing scorer");

        // The constant 15 is RemainingTurns - 1 for the controlled features below (EnemyHp
        // 1,000,000, RetainedAttackValue 1, LiveDeckSize 1).
        {
            Battle state = State(item, ["STRIKE_NECROBINDER"]);
            SetVar(ref state, 0, 4);
            ScoreFeatures features = Features(state, 8);
            PowerEvidence.View view = new(state, features, item.Rules, scorer);
            Assert(Math.Max(0, view.RemainingTurns() - 1) == 15, "controlled remaining turns");
            Assert(view.EnergyUnit() == 10 && view.MaxDamage() == 10
                && view.TotalAttackDamage() == 10,
                $"controlled attack facts energy={view.EnergyUnit()} max={view.MaxDamage()} total={view.TotalAttackDamage()}");
        }

        // CALCIFY: CalcifyPower adds to Osty attacks; the upstream per-turn damage estimate is
        // gain * turns on one target, now capped by the remaining enemy HP.
        {
            Battle parent = State(item, []);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Calcify, 4);
            Assert(Project(item, scorer, "CALCIFY", parent, child) == 60,
                "Calcify damage per turn");
            Assert(ProjectCapped(item, scorer, "CALCIFY", parent, child, 7) == 7,
                "Calcify remaining-HP cap");
            AssertDamageCap(item, scorer, "CALCIFY", parent, child, 60);
        }

        // CALL_OF_THE_VOID: generated cards per turn; each stack generates one card, valued as one
        // energy unit, for the remaining turns.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER"]);
            SetVar(ref parent, 0, 4);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.CallOfTheVoid, 2);
            Assert(Project(item, scorer, "CALL_OF_THE_VOID", parent, child) == 300,
                "Call of the Void generated cards");
        }

        // COUNTDOWN: one random target takes Doom per turn, so the upstream per-turn formula uses one
        // target rather than every living enemy.
        {
            Battle parent = State(item, []);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Countdown, 6);
            Assert(Project(item, scorer, "COUNTDOWN", parent, child) == 90,
                "Countdown single-target Doom");
            Assert(ProjectCapped(item, scorer, "COUNTDOWN", parent, child, 7) == 7,
                "Countdown remaining-HP cap");
            AssertDamageCap(item, scorer, "COUNTDOWN", parent, child, 90);
        }

        // DANSE_MACABRE: block whenever a card costing at least two is played; two live two-cost
        // cards, blocked by 20 incoming.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER", "STRIKE_NECROBINDER"]);
            SetCost(ref parent, 0, 2);
            SetCost(ref parent, 1, 2);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.DanseMacabre, 5);
            Assert(Project(item, scorer, "DANSE_MACABRE", parent, child, incomingDamage: 20) == 10,
                "Danse Macabre block per two-cost card");
        }

        // DEMESNE: each stack adds one energy and one card draw per turn; the upstream resource
        // projection prices the energy in energy units.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER"]);
            SetVar(ref parent, 0, 4);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Demesne, 1);
            Assert(Project(item, scorer, "DEMESNE", parent, child) == 150,
                "Demesne per-turn energy resource");
        }

        // DEVOUR_LIFE: each Soul play summons the power amount as Osty HP; two live Souls resource.
        {
            Battle parent = State(item, ["SOUL", "SOUL"]);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.DevourLife, 2);
            Assert(Project(item, scorer, "DEVOUR_LIFE", parent, child) == 4,
                "Devour Life resource per Soul");
        }

        // FRIENDSHIP: each stack adds one maximum energy per turn, but the card removes two
        // Strength. With a one-card deck that means one attack hit per turn.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER"]);
            SetVar(ref parent, 0, 4);
            SetPower(ref parent, PowerEvidenceIds.Strength, 2);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Friendship, 1);
            SetPower(ref child, PowerEvidenceIds.Strength, 0);
            PowerEvidence.View view = new(child, Features(child, 8), item.Rules, scorer);
            int expected = Math.Max(0, 1 * 10 * view.RemainingTurns()
                - 2 * 1 * view.RemainingTurns());
            Assert(Project(item, scorer, "FRIENDSHIP", parent, child) == expected,
                "Friendship energy subtracts lost Strength damage");
        }

        // HAUNT: each Soul play deals the power amount to one random enemy; capped by enemy HP.
        {
            Battle parent = State(item, ["SOUL", "SOUL"]);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Haunt, 7);
            Assert(Project(item, scorer, "HAUNT", parent, child) == 14,
                "Haunt damage per Soul");
            Assert(ProjectCapped(item, scorer, "HAUNT", parent, child, 10) == 10,
                "Haunt remaining-HP cap");
            AssertDamageCap(item, scorer, "HAUNT", parent, child, 14);
        }

        // LETHALITY: first attack each remaining turn gains gain percent of its damage.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER"]);
            SetVar(ref parent, 0, 4);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Lethality, 50);
            Assert(Project(item, scorer, "LETHALITY", parent, child) == 75,
                "Lethality per-turn percentage damage");
            Assert(ProjectCapped(item, scorer, "LETHALITY", parent, child, 20) == 20,
                "Lethality remaining-HP cap");
            AssertDamageCap(item, scorer, "LETHALITY", parent, child, 75);
        }

        // NECRO_MASTERY: Osty redirects unblocked powered attacks (DieForYouPower), so its lost HP
        // is the incoming unblocked damage, capped by its current HP. Each lost HP damages all
        // enemies for the power amount.
        {
            Battle parent = State(item, []);
            parent.Flags |= BattleFlags.OstyPresent;
            parent.Osty.CurrentHp = 20;
            parent.Osty.MaxHp = 20;
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.NecroMastery, 1);
            PowerEvidence.View view = new(child, Features(child, 8), item.Rules, scorer);
            int expected = Math.Min(20, 8 * view.RemainingTurns());
            Assert(Project(item, scorer, "NECRO_MASTERY", parent, child) == expected,
                "Necro Mastery Osty HP-loss damage");
        }

        // NEUROSURGE: the immediate energy/draw value stays, but each turn adds its amount of
        // Doom to the player; subtract the accumulated Doom threat, capped by player HP.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER"]);
            SetVar(ref parent, 0, 4);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Neurosurge, 2);
            PowerEvidence.View view = new(child, Features(child, 8), item.Rules, scorer);
            int benefit = 2 * 10;
            long doom = 2L * view.RemainingTurns();
            int cost = (int)Math.Min(70L, doom * doom / 70L);
            Assert(Project(item, scorer, "NEUROSURGE", parent, child)
                == Math.Max(0, benefit - cost),
                "Neurosurge resource subtracts self-Doom threat");
        }

        // PAGESTORM: each Ethereal card drawn draws the power amount more; two Ethereal cards at
        // 10 energy units each and two stacks is 40.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER", "STRIKE_NECROBINDER"]);
            SetVar(ref parent, 0, 4);
            SetVar(ref parent, 1, 4);
            SetEthereal(ref parent, 0);
            SetEthereal(ref parent, 1);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Pagestorm, 2);
            Assert(Project(item, scorer, "PAGESTORM", parent, child) == 40,
                "Pagestorm resource per Ethereal card and stack");
        }

        // REAPER_FORM: the upstream trigger is the total attack damage in the live piles; capped by
        // enemy HP.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER"]);
            SetVar(ref parent, 0, 4);
            Battle child = parent;
            Assert(Project(item, scorer, "REAPER_FORM", parent, child) == 10,
                "Reaper Form total attack damage");
            Assert(ProjectCapped(item, scorer, "REAPER_FORM", parent, child, 6) == 6,
                "Reaper Form remaining-HP cap");
            AssertDamageCap(item, scorer, "REAPER_FORM", parent, child, 10);
        }

        // SENTRY_MODE: Sweeping Gaze cards per turn, priced as one energy unit per stack and turn.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER"]);
            SetVar(ref parent, 0, 4);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.SentryMode, 2);
            Assert(Project(item, scorer, "SENTRY_MODE", parent, child) == 300,
                "Sentry Mode generated-card resource");
        }

        // SHROUD: block per Doom application; two live Doom sources and three block each.
        {
            Battle parent = State(item, ["SCOURGE", "SCOURGE"]);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.Shroud, 3);
            Assert(Project(item, scorer, "SHROUD", parent, child, incomingDamage: 20) == 6,
                "Shroud block per Doom source");
        }

        // SLEIGHT_OF_FLESH: damage per debuff source; two live Vulnerable attacks and nine damage
        // each, capped by enemy HP.
        {
            Battle parent = State(item, ["FEAR", "FEAR"]);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.SleightOfFlesh, 9);
            Assert(Project(item, scorer, "SLEIGHT_OF_FLESH", parent, child) == 18,
                "Sleight of Flesh damage per debuff source");
            Assert(ProjectCapped(item, scorer, "SLEIGHT_OF_FLESH", parent, child, 11) == 11,
                "Sleight of Flesh remaining-HP cap");
            AssertDamageCap(item, scorer, "SLEIGHT_OF_FLESH", parent, child, 18);
        }

        // SPIRIT_OF_ASH: block per Ethereal card played; two Ethereal cards and four block each.
        {
            Battle parent = State(item, ["STRIKE_NECROBINDER", "STRIKE_NECROBINDER"]);
            SetEthereal(ref parent, 0);
            SetEthereal(ref parent, 1);
            Battle child = parent;
            SetPower(ref child, PowerEvidenceIds.SpiritOfAsh, 4);
            Assert(Project(item, scorer, "SPIRIT_OF_ASH", parent, child, incomingDamage: 20) == 8,
                "Spirit of Ash block per Ethereal card");
        }

        // FORBIDDEN_GRIMOIRE only pays out across combats; it has no in-combat opening projection.
        {
            Battle parent = State(item, []);
            Battle child = parent;
            Assert(Project(item, scorer, "FORBIDDEN_GRIMOIRE", parent, child) == 0,
                "Forbidden Grimoire has no in-combat projection");
        }

        Console.WriteLine("NECROBINDER_POWER_OPENING cards=18 cap=pass");
    }
}
