using System.Numerics;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Ai.Combat.Scoring;

internal sealed partial class Scorer
{
    private readonly record struct FoolPowerWindow(int ShotDamage, bool CanImpale,
        int NewMelodies, int MelodyPlays, bool CanComplete, bool CanFinishWithKi)
    {
        internal int Draws(in Battle battle, int amount) => Math.Min(amount * NewMelodies,
            Math.Min(battle.DrawCount + battle.DiscardCount, Math.Max(0, battle.MaxHandSize - battle.HandCount + MelodyPlays)));
    }

    // Price the next shot and the melodies payable from this hand. A multi-tag card
    // pays once, and melodies already played this turn do not trigger Motif again.
    private FoolPowerWindow CaptureFoolPowerWindow(in Battle battle)
    {
        int played = 0;
        for (int bit = 0; bit < 3; bit++)
            if (battle.Fool.Finished[bit] > 0) played |= 1 << bit;

        Span<int> costs = stackalloc int[8];
        costs.Fill(int.MaxValue);
        costs[played] = 0;
        int melodyCards = 0;
        int bestShot = 0;
        bool canImpale = false;
        bool hasPlayableCard = false;
        bool hasArrow = Kernel.AvailableArrows(battle) > 0;
        for (int i = 0; i < battle.HandCount; i++)
        {
            ref readonly Card card = ref battle.Cards[i];
            CardStats stats = Rules.Card(card.Id, card.Upgrade);
            int melody = (int)(stats.Tags & (CardTag.Ki | CardTag.Wah | CardTag.Cho)) / (int)CardTag.Wah;
            bool shoot = hasArrow && (stats.Tags & CardTag.Shoot) != 0;
            if (!shoot && melody == 0 && played != 7) continue;
            if (!Kernel.CanPlayCard(battle, Rules, i, out int cost, out _)) continue;
            hasPlayableCard = true;
            bool costsX = (card.Flags & CardFlags.CostsX) != 0;
            int energy = costsX ? battle.Energy : Math.Max(0, cost);
            if (shoot && (!costsX || energy > 0))
            {
                int hits = Registry.Card(card.Id).NativeId switch
                {
                    "REPEAT_SHOT" => energy,
                    "RAPID_SHOT" => (int)Var(battle, card, stats, 1),
                    "MELODIC_SHOT" => (int)(Var(battle, card, stats, 1)
                        + Var(battle, card, stats, 2) * Kernel.MelodiesThisTurn(battle)),
                    _ => 1,
                };
                int damage = (int)Var(battle, card, stats, 0);
                if (Registry.Card(card.Id).NativeId == "THE_SHOT")
                    damage += (int)Var(battle, card, stats, 1) * card.Private;
                bestShot = Math.Max(bestShot, damage * hits);
                canImpale |= hits > 0;
            }
            if ((melody & ~played) == 0) continue;
            melodyCards++;
            for (int mask = 6; mask >= 0; mask--)
                if (costs[mask] <= battle.Energy - energy)
                    costs[mask | melody] = Math.Min(costs[mask | melody], costs[mask] + energy);
        }
        int newMelodies = 0;
        bool ki = false;
        for (int mask = 0; mask < costs.Length; mask++)
        {
            if (costs[mask] == int.MaxValue) continue;
            newMelodies = Math.Max(newMelodies, BitOperations.PopCount((uint)(mask & ~played)));
            ki |= (mask & ((int)CardTag.Ki / (int)CardTag.Wah)) != 0;
        }
        return new(bestShot, canImpale, newMelodies, Math.Min(melodyCards, newMelodies), costs[7] != int.MaxValue && hasPlayableCard, ki);
    }

    private StrategicEffectVector EvaluateFoolPower(in Battle battle, in Power power,
        StrategicKind kind, FoolPowerWindow window, in StrategicEffectContext context,
        long openBlockableDamage, ref int handDraw)
    {
        int amount = power.Amount;
        switch (kind)
        {
            case StrategicKind.Honing:
                return Damage((int)((long)window.ShotDamage * amount / 100), context.EnemyHp);
            case StrategicKind.ExplosiveArrows when window.CanImpale:
                return Damage(DamageAcrossTargets(battle, amount), context.EnemyHp);
            case StrategicKind.Motif:
                return CardAccess(window.Draws(battle, amount) * context.AverageCardValue);
            case StrategicKind.Lullaby when power.Aux0 == 0 && window.CanComplete:
                return new(0, (int)Math.Clamp(openBlockableDamage, 0, amount), 0, 0, 0);
            case StrategicKind.Tune when window.CanFinishWithKi:
                return Resource(TuneEnergySavings(battle, amount, ref handDraw) * Math.Max(3, context.AverageCardValue));
            default:
                return default;
        }
    }

    // Tune grants one free play per stack. Only retained cards and the visible draw
    // prefix can supply a known next-turn cost; do not infer the order of a shuffle.
    private int TuneEnergySavings(in Battle battle, int freePlays, ref int handDraw)
    {
        int draws = Math.Min(battle.DrawCount, Math.Min(battle.MaxHandSize, ModifiedHandDraw(battle, ref handDraw)));
        Span<int> costs = stackalloc int[battle.HandCount + draws];
        int retained = 0;
        int count = 0;
        for (int i = 0; i < battle.HandCount + draws; i++)
        {
            ref readonly Card card = ref battle.Cards[i];
            if (i < battle.HandCount)
            {
                if (!ShouldRetainThisTurn(card)) continue;
                retained++;
            }
            else if (i - battle.HandCount >= battle.MaxHandSize - retained) break;
            if (card.HasKeyword(Keywords.Unplayable) || (card.Flags & CardFlags.CostsX) != 0) continue;
            int cost = Kernel.EnergyCostWithLocalModifiers(battle, card);
            if (cost > 0) costs[count++] = cost;
        }
        costs[..count].Sort();
        int saved = 0;
        for (int i = 0; i < Math.Min(count, freePlays); i++)
            saved += costs[count - 1 - i];
        return saved;
    }

    // The opening projection prices only the effect gained by playing this copy.
    internal int FoolOpeningPotential(string cardId, ushort powerId, int gain, scoped in PowerEvidence.View view)
    {
        ref readonly Battle battle = ref view.Battle;
        switch (cardId)
        {
            case "MYTHIC_FORM":
                return Math.Clamp(view.RemainingTurns() - 1, 0, 2) * gain * view.EnergyUnit();
            case "HIDDEN_ROOTS":
                return view.MaxBlock() > 0 ? Math.Clamp(view.RemainingTurns() - 1, 0, 2) * gain : 0;
            case "MOTIF":
                return CaptureFoolPowerWindow(battle).Draws(battle, gain) * view.EnergyUnit();
            case "LULLABY":
                int slot = Kernel.FindPower(battle, Limits.PlayerOwner, powerId);
                if (battle.Powers[slot].Aux0 != 0 || !CaptureFoolPowerWindow(battle).CanComplete) return 0;
                break;
            case "SONG_PATH":
                if (!CanGainMelody(battle, CardTag.Cho)) return 0;
                break;
            case "VIOLENT_CHORD":
                return CanGainMelody(battle, CardTag.Wah) ? DamageAcrossTargets(battle, gain) : 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(cardId));
        }
        var threat = ProjectHpAfterThreat(battle);
        return (int)Math.Clamp(threat.BlockableDamage - threat.BlockConsumed, 0L, gain);
    }

    private bool CanGainMelody(in Battle battle, CardTag tag)
    {
        if (Kernel.PlayedMelody(battle, tag)) return false;
        for (int i = 0; i < battle.HandCount; i++)
        {
            ref readonly Card card = ref battle.Cards[i];
            if ((Rules.Card(card.Id, card.Upgrade).Tags & tag) != 0 && Kernel.CanPlayCard(battle, Rules, i))
                return true;
        }
        return false;
    }

    private static int DamageAcrossTargets(in Battle battle, int amount)
    {
        int damage = 0;
        for (int i = 0; i < battle.RosterCount; i++)
        {
            byte owner = Kernel.EnemyOwner(battle.Roster[i]);
            if (Kernel.IsHittable(battle, owner))
                damage += Math.Min(amount, battle.Creature(owner).CurrentHp);
        }
        return damage;
    }
}
