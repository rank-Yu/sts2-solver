using System.IO.Compression;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

/// <summary>
/// Minimal contract for the delayed poison value: one trigger when the player is projected to die
/// this turn, otherwise the shared remaining-turn estimate (at least two); every trigger decrements
/// one stack, Accelerant adds hits, and the per-enemy current-HP cap stays in force.
/// </summary>
internal static class PoisonDelayedDamageContracts
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException("poison-delayed-damage: " + message);
    }

    private static SearchCase Read(string root)
    {
        using FileStream file = File.OpenRead($"tests/Golden/data/d1/{root}/config.br");
        using BrotliStream zip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        zip.CopyTo(bytes);
        return SearchCodec.DecodeRequest(bytes.ToArray());
    }

    internal static void Run()
    {
        SearchCase item = Read("quality-SILENT-000");
        Scorer scorer = item.Options.Scorer!;
        Battle root = item.Battles[0];
        Assert(Registry.TryPower("POISON_POWER", out ushort poisonId), "Poison registered");
        Assert(Registry.TryPower("ACCELERANT_POWER", out ushort accelerantId), "Accelerant registered");

        // Isolate the delayed-damage term: no relic hooks, powers or live cards contribute.
        root.PowerCount = 0;
        root.RelicCount = 0;
        root.HandCount = 0;
        root.DrawCount = 0;
        root.DiscardCount = 0;
        root.ExhaustCount = 0;
        root.PlayCount = 0;
        root.Player.Block = 0;
        int target = -1;
        bool hasAttacker = false;
        for (int position = 0; position < root.RosterCount; position++)
        {
            int index = root.Roster[position];
            ref Enemy enemy = ref root.Enemies[index];
            if (!enemy.Creature.IsAlive || (enemy.Flags & EnemyFlags.InCombat) == 0)
                continue;
            if (target < 0)
                target = index;
            MonsterNode[] nodes = item.Rules.MonsterRule(enemy.Rules).Nodes;
            int attack = Array.FindIndex(nodes, static node => node.Attacks.Length > 0);
            if (attack < 0)
                continue;
            enemy.Move = checked((byte)attack);
            enemy.Flags &= ~EnemyFlags.SkipNextMove;
            enemy.SteamPhase = SteamPhase.None;
            hasAttacker = true;
        }
        Assert(target >= 0, "root has a live enemy");
        Assert(hasAttacker, "root has an attacking enemy");
        for (int index = 0; index < root.EnemyCount; index++)
        {
            if (index == target)
                continue;
            root.Enemies[index].Creature.CurrentHp = 0;
            root.Enemies[index].Flags &= ~EnemyFlags.InCombat;
        }

        Battle survivor = root;
        survivor.Player.CurrentHp = survivor.Player.MaxHp = 100_000;
        survivor.Enemies[target].Creature.CurrentHp = 30;
        Assert(scorer.ProjectHpAfterThreat(survivor).Hp > 0, "high-HP fixture survives the turn");

        int targetOwner = Kernel.EnemyOwner((byte)target);
        int Value(in Battle battle) => scorer.Features(battle, 0).DelayedDamageValue;

        Assert(Value(survivor) == 0, "no poison contributes nothing");

        Battle fiveTicks = survivor;
        fiveTicks.Powers[0] = new Power { Id = poisonId, Owner = (byte)targetOwner, Amount = 5 };
        fiveTicks.PowerCount = 1;
        Assert(Value(fiveTicks) == 15, "30 HP / 6 damage per turn gives R=5 and five poison triggers, 5+4+3+2+1");

        Battle capped = fiveTicks;
        capped.Enemies[target].Creature.CurrentHp = 3;
        Assert(Value(capped) == 3, "poison value is capped at the enemy's current HP");

        Battle accelerated = survivor;
        accelerated.Powers[0] = new Power { Id = accelerantId, Owner = Limits.PlayerOwner, Amount = 2 };
        accelerated.Powers[1] = new Power { Id = poisonId, Owner = (byte)targetOwner, Amount = 5 };
        accelerated.PowerCount = 2;
        Assert(Value(accelerated) == 15,
            "R=3 gives three triggers; the 5 poison depletes after 5+4+3+2+1 = 15");

        Battle lethal = fiveTicks;
        lethal.Player.CurrentHp = 1;
        Assert(scorer.ProjectHpAfterThreat(lethal).Hp <= 0, "low-HP fixture is projected to die");
        Assert(Value(lethal) == 5, "a lethal turn counts only the first poison trigger");

        Battle lethalAccelerated = accelerated;
        lethalAccelerated.Player.CurrentHp = 1;
        Assert(scorer.ProjectHpAfterThreat(lethalAccelerated).Hp <= 0,
            "low-HP Accelerant fixture is projected to die");
        Assert(Value(lethalAccelerated) == 12,
            "a lethal Accelerant turn counts only the first trigger's 3 hits (5+4+3)");

        Console.WriteLine("POISON_DELAYED_DAMAGE oneTick=5 fiveTicks=15 capped=3 "
            + "accelerantTwoTicks=15 accelerantOneTick=12");
    }
}
