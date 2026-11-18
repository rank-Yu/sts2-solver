using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Golden;

internal static class FoolPowerContracts
{
    internal static void Run()
    {
        SearchCase source = PowerEvidenceContracts.ReadRoot("fool-rain-shot");
        Scorer scorer = source.Options.Scorer!;
        string[] powers = ["MYTHIC_FORM", "MOTIF", "HIDDEN_ROOTS", "LULLABY", "SONG_PATH", "VIOLENT_CHORD"];
        foreach (string name in powers)
        {
            if (!Registry.TryCard(name, out ushort id)) throw new InvalidDataException(name);
            Battle single = default;
            Piles.Append(ref single, Pile.Hand, new Card { Id = id });
            var catalog = PowerCatalogBuilder.Build(single, source.Rules, scorer, 1);
            if (catalog is null || !catalog.Rules[id].Admission.RequirePositiveProjection)
                throw new InvalidDataException($"Missing Fool power route: {name}");
        }

        Battle battle = source.Battles[0];
        battle.HandCount = battle.DrawCount = battle.DiscardCount = battle.ExhaustCount = battle.PlayCount = 0;
        battle.Fool.ArrowCount = 0;
        if (!Registry.TryCard("FOOLISH_SHOT", out ushort shotId)
            || !Registry.TryPower("HONING_POWER", out ushort honing)
            || !Registry.TryPower("EXPLOSIVE_ARROWS_POWER", out ushort explosive))
            throw new InvalidDataException("Missing Fool arrow content.");
        scorer.RulesForCodec.Powers[honing] = new() { Kind = StrategicKind.Honing, Type = PowerType.Buff };
        scorer.RulesForCodec.Powers[explosive] = new() { Kind = StrategicKind.ExplosiveArrows, Type = PowerType.Buff };
        Card shot = Kernel.CreateCard(source.Rules, shotId);
        int baseDamage = (int)CardEffects.Var(battle, source.Rules, shot, 0);
        const int blast = 6;
        int blastDamage = 0;
        for (int i = 0; i < battle.RosterCount; i++)
        {
            byte owner = Kernel.EnemyOwner(battle.Roster[i]);
            if (Kernel.IsHittable(battle, owner)) blastDamage += Math.Min(blast, battle.Creature(owner).CurrentHp);
        }
        void Expect(int arrowDamage, int burst)
        {
            int score = scorer.Features(battle, 0).StrategicDamagePotential;
            Check(honing, 100, arrowDamage);
            Check(explosive, blast, burst);
            void Check(ushort id, int amount, int expected)
            {
                Kernel.SetPowerAmount(ref battle, Limits.PlayerOwner, id, amount);
                int actual = scorer.Features(battle, 0).StrategicDamagePotential - score;
                Kernel.SetPowerAmount(ref battle, Limits.PlayerOwner, id, 0);
                if (actual != expected)
                    throw new InvalidDataException($"{Registry.Power(id).NativeId}: expected {expected}, got {actual}.");
            }
        }

        Piles.Append(ref battle, Pile.Draw, shot);
        Expect(0, 0);
        Kernel.GainArrow(ref battle);
        Expect(0, 0);
        Piles.Append(ref battle, Pile.Hand, shot);
        Expect(baseDamage, blastDamage);
        Kernel.GainArrow(ref battle);
        Expect(baseDamage, blastDamage);
        Console.WriteLine("FOOL_POWER six openings; current hand and arrow supply=pass");
    }
}
