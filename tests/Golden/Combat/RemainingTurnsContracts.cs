using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal contract for the shared remaining-turn estimate. The three lower-bound additions are
/// checked separately: an all-enemy attack must not double the turn count for twice the enemy HP,
/// existing enemy poison must shorten the estimate, and a poison skill must count its poison.
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
        using FileStream file = File.OpenRead($"tests/Golden/data/d1/quality-{root}/config.br");
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

        Battle poisonSkill = State(item, "DEADLY_POISON");
        Assert(Turns(item, scorer, poisonSkill) == 4,
            "5 poison from Deadly Poison is counted in the per-cycle damage: 100 HP -> 4 turns");

        Console.WriteLine("REMAINING_TURNS aoe=5 poison=10 poisonSkill=4");
    }
}
