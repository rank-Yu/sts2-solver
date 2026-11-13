using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal contract for the shared remaining-turn estimate. The lower-bound additions are checked
/// separately: an all-enemy attack must not double the turn count for twice the enemy HP, existing
/// enemy poison must shorten the estimate, a poison skill must count its poison, accumulating
/// poison and Demon Form grow the per-turn damage, Corrosive Wave lasts one turn, and existing Doom
/// lowers the kill line.
/// </summary>
internal static class RemainingTurnsContracts
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException("remaining-turns: " + message);
    }

    private static SearchCase Read(string root)
    {
        using FileStream file = File.OpenRead(GoldenFixtures.PathOf($"d1/quality-{root}/config.br"));
        using BrotliStream zip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        zip.CopyTo(bytes);
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
                throw new InvalidDataException("remaining-turns: missing " + id);
            Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(item.Rules, cardId));
        }
        return battle;
    }

    private static void AddSecondEnemy(ref Battle battle)
    {
        battle.EnemyCount = 2;
        battle.RosterCount = 2;
        battle.Roster[1] = 1;
        battle.Enemies[1] = battle.Enemies[0];
        battle.Enemies[1].Creature.CurrentHp = 100;
    }

    private static int Turns(SearchCase item, Scorer scorer, in Battle battle)
    {
        ScoreFeatures features = scorer.Features(battle, 0);
        PowerEvidence.View view = new(battle, features, item.Rules, scorer);
        return view.RemainingTurns();
    }

    internal static void Run()
    {
        SearchCase item = Read("SILENT-014");
        Scorer scorer = item.Options.Scorer!;

        Battle empty = State(item);
        Assert(Turns(item, scorer, empty) == 16, "an empty 100-HP fight keeps the 16-turn horizon");

        Battle oneTarget = State(item, "DAGGER_SPRAY");
        Assert(Turns(item, scorer, oneTarget) == 5, "one Dagger Spray target is 5 turns");

        Battle allEnemies = oneTarget;
        AddSecondEnemy(ref allEnemies);
        Assert(Turns(item, scorer, allEnemies) == 5,
            "Dagger Spray hitting both 100-HP enemies stays 5 turns, not 10");

        Battle poisoned = State(item);
        Kernel.SetPowerAmount(ref poisoned, Limits.FirstEnemyOwner, PowerEvidenceIds.Poison, 10);
        Assert(Turns(item, scorer, poisoned) == 10,
            "10 existing poison counts as 10 damage per turn: 100 HP -> 10 turns");

        // The one-card deck draws Deadly Poison five times a turn: 25 poison next turn, and the
        // stacks keep triggering, so turn k deals 25 + 24(k-1) (one stack decays per trigger):
        // 25, 74, 147 -> 3 turns (4 without growth).
        Battle poisonSkill = State(item, "DEADLY_POISON");
        Assert(Turns(item, scorer, poisonSkill) == 3,
            "Deadly Poison's stacks accumulate: 25 + 24(k-1) per turn, 100 HP -> 3 turns");

        // Noxious Fumes 3 with an empty deck: 1 + 3 next turn, growing by 3 - 1 decay per turn:
        // 4, 6, 8, ... reaches 100 after 9 turns (the game deals 3, 5, 7, ... and needs 10).
        Battle fumes = State(item);
        Kernel.SetPowerAmount(ref fumes, Limits.PlayerOwner, PowerEvidenceIds.NoxiousFumes, 3);
        int fumesTurns = Turns(item, scorer, fumes);
        Assert(fumesTurns == 9, "Noxious Fumes poison accumulates: 100 HP -> 9 turns, got " + fumesTurns);

        // Corrosive Wave removes itself at the end of the player's turn; it adds no later damage.
        Battle wave = State(item);
        Kernel.SetPowerAmount(ref wave, Limits.PlayerOwner, PowerEvidenceIds.CorrosiveWave, 3);
        Assert(Turns(item, scorer, wave) == 16, "Corrosive Wave does not repeat on later turns");

        // Demon Form adds Strength to every later attack hit.
        int strikeTurns = Turns(item, scorer, State(item, "STRIKE_SILENT"));
        Battle demon = State(item, "STRIKE_SILENT");
        Kernel.SetPowerAmount(ref demon, Limits.PlayerOwner, PowerEvidenceIds.DemonForm, 2);
        int demonTurns = Turns(item, scorer, demon);
        Assert(demonTurns < strikeTurns, "Demon Form growth shortens the fight");

        // Existing Doom lowers the kill line by its full amount: the enemy dies at its turn end
        // when its HP is at most its Doom. Dagger Spray deals 20 a turn: 60 HP left -> 3 turns.
        Battle doomed = State(item, "DAGGER_SPRAY");
        Kernel.SetPowerAmount(ref doomed, Limits.FirstEnemyOwner, PowerEvidenceIds.Doom, 40);
        int doomTurns = Turns(item, scorer, doomed);
        Assert(doomTurns == 3, "40 Doom on 100 HP leaves 60 to deal: 3 turns, got " + doomTurns);

        Console.WriteLine("REMAINING_TURNS aoe=5 poison=10 poisonSkill=3 fumes=" + fumesTurns
            + " corrosiveWave=16 strike=" + strikeTurns + " demonForm=" + demonTurns
            + " doom=" + doomTurns);
    }
}
