using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.Content.Cards.Regent;

/// <summary>
/// <c>BeatIntoShape</c>: the inferred attack, then forge
/// <c>CalculatedForge - Results.Count × CalculationExtra</c> (CardOnPlaySupport). The calculated
/// multiplier is the player's powered attack hits on the target this turn; the attack's own hits
/// cancel out, so the forge is CalculationBase + CalculationExtra × hits before this attack.
/// </summary>
[ContentId("BEAT_INTO_SHAPE")]
public sealed class BeatIntoShape : CardDef
{
    public override bool MayGenerateIdentityContent => true;

    public override IReadOnlyList<string> VarNames => ["Damage", "CalculationBase", "CalculationExtra"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [(Kernel.SovereignBladeId, 0)];

    public override void OnPlay(ref Exec x)
    {
        if (x.Target == Limits.NoOwner)
            throw new UnsupportedException("BeatIntoShape 缺少目标，无法读取按目标命中数。");
        int hits = Kernel.PoweredAttackHitsByPlayer(x.B, x.Target);
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        decimal forge = CardEffects.Var(x, 1) + CardEffects.Var(x, 2) * hits;
        if (forge < int.MinValue || forge > int.MaxValue || forge != decimal.Truncate(forge))
            throw new UnsupportedException($"BeatIntoShape 的锻造量 {forge} 无法用整数表示。");
        Kernel.Forge(ref x, (int)forge);
    }
}

/// <summary>
/// <c>MakeItSo</c>: the inferred attack; <c>AfterCardPlayedLate</c> returns this card to hand when the
/// number of finished Skill plays this turn is a multiple of the Cards variable
/// (<c>AfterCardPlayedMirrors.HandleMakeItSo</c>; vanilla records CardPlayFinished before the late
/// pass, so the current Skill is included).
/// </summary>
[ContentId("MAKE_IT_SO")]
public sealed class MakeItSo : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Cards"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void AfterCardPlayedLate(ref Exec x, Listener self, in CardPlayInfo play)
    {
        if (self.Kind != ListenerKind.Card)
            return;
        if (Kernel.ResolveCardType(x.B, x.R, play.Card) != CardType.Skill)
            return;
        int position = self.Index;
        if (position >= x.B.CardCount)
            return;
        int handEnd = x.B.HandCount;
        if (position < handEnd)
            return;
        int cards = (int)x.R.Card(x.B.Cards[position].Id, x.B.Cards[position].Upgrade).Vars[1];
        if (cards <= 0)
            throw new UnsupportedException("MakeItSo 的 Cards 变量不为正。");
        int finished = x.B.Counter(Counter.SkillCardsPlayedThisTurn) + 1;
        if (finished % cards != 0)
            return;
        Pile pile = Piles.PileOf(x.B, position, out int index);
        Kernel.MoveCard(ref x.B, pile, index, Pile.Hand);
    }
}

/// <summary>
/// <c>CrashLanding</c> (Rare, attack every enemy): CardEffectSpecRegistry then adds one Debris to the
/// hand for each empty hand slot (<c>GetMaxHandSize - Hand.Count</c>).
/// </summary>
[ContentId("CRASH_LANDING")]
public sealed class CrashLanding : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override IReadOnlyList<(string CardId, int Upgrade)> GeneratedCards => [(DebrisId, 0)];

    private const string DebrisId = "DEBRIS";

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        int count = x.B.MaxHandSize - x.B.HandCount;
        if (count <= 0)
            return;
        if (!Registry.TryCard(DebrisId, out ushort debris))
            throw new UnsupportedException("坠毁需要 DEBRIS 的 Sts2Solver 定义。");
        for (int index = 0; index < count; index++)
        {
            if (!Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(x.R, debris), Pile.Hand))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary><c>SevenStars</c> (7 star cost): every enemy takes the Repeat variable's number of hits.</summary>
[ContentId("SEVEN_STARS")]
public sealed class SevenStars : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat"];

    public override void OnPlay(ref Exec x)
        => CardEffects.GeneralAttack(ref x, 0, hitCount: (int)CardEffects.Var(x, 1));
}

/// <summary>
/// <c>HeirloomHammer</c>: the inferred attack, then choose one colorless hand card and add Repeat
/// clones of it to hand (<c>CardChoiceResolution.DuplicateSelectedCard</c>: <c>CreateClone</c> per
/// copy, bottom of the hand, fixed result kind).
/// </summary>
[ContentId("HEIRLOOM_HAMMER")]
public sealed class HeirloomHammer : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Repeat"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);

    public override void OwnChoice(ref Exec x)
    {
        Span<int> options = stackalloc int[x.B.HandCount];
        int count = 0;
        for (int index = 0; index < x.B.HandCount; index++)
        {
            ref readonly Card card = ref x.B.Cards[index];
            if (x.R.Card(card.Id, card.Upgrade).IsColorless)
                options[count++] = index;
        }
        if (count == 0)
            return;
        Span<int> selected = stackalloc int[1];
        if (!Kernel.ChooseCards(ref x, ChoiceEffect.Duplicate, Pile.Hand, options[..count], 1,
                selected, out int chosen))
        {
            return;
        }
        if (chosen == 0)
            return;
        Card original = Piles.At(ref x.B, Pile.Hand, selected[0]);
        int copies = (int)CardEffects.Var(x, 1);
        for (int index = 0; index < copies; index++)
        {
            Card clone = Kernel.CloneCard(original);
            if (!Kernel.AddGeneratedCard(ref x, clone, Pile.Hand))
                return;
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary>
/// <c>Comet</c> (5 star cost): attack, then CorePowerSupport applies Weak and Vulnerable after the
/// deferred enemy deaths.
/// </summary>
[ContentId("COMET")]
public sealed class Comet : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "VulnerablePower", "WeakPower"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        Kernel.ApplyEnemyDeathPowers(ref x);
        if (x.NeedsChoice || x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.WeakPower>(),
            (int)CardEffects.Var(x, 2), Limits.PlayerOwner);
        Kernel.ApplyPower(ref x, x.Target, Registry.PowerId<Powers.VulnerablePower>(),
            (int)CardEffects.Var(x, 1), Limits.PlayerOwner);
    }
}

/// <summary>
/// <c>DyingStar</c> (3 star cost, Ethereal): attack every enemy, then CardEffectSpecRegistry applies
/// DyingStarPower (a temporary Strength loss) to every hittable enemy in roster order.
/// </summary>
[ContentId("DYING_STAR")]
public sealed class DyingStar : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "StrengthLoss"];

    public override void OnPlay(ref Exec x)
    {
        CardEffects.GeneralAttack(ref x, 0);
        if (x.NeedsChoice)
            return;
        ushort power = Registry.PowerId<Powers.DyingStarPower>();
        int amount = (int)CardEffects.Var(x, 1);
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte enemy = Kernel.EnemyOwner(x.B.Roster[position]);
            if (!Kernel.IsHittable(x.B, enemy))
                continue;
            Kernel.ApplyTemporaryStrength(ref x, enemy, power, amount, Limits.PlayerOwner, -1);
            if (x.NeedsChoice)
                return;
        }
    }
}

/// <summary><c>Bombardment</c>: the inferred recipe is a plain single-target attack.</summary>
[ContentId("BOMBARDMENT")]
public sealed class Bombardment : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage"];

    public override void OnPlay(ref Exec x) => CardEffects.GeneralAttack(ref x, 0);
}

/// <summary>
/// <c>HeavenlyDrill</c> (X energy cost): BespokeCardMirrors resolves the energy X value and doubles it
/// when it reaches the canonical Energy variable, then attacks once per hit.
/// </summary>
[ContentId("HEAVENLY_DRILL")]
public sealed class HeavenlyDrill : CardDef
{
    public override IReadOnlyList<string> VarNames => ["Damage", "Energy"];

    public override void OnPlay(ref Exec x)
    {
        int hits = Kernel.ResolveEnergyXValue(ref x);
        if (hits >= (int)CardEffects.Var(x, 1))
            hits *= 2;
        CardEffects.Attack(ref x, CardEffects.Var(x, 0), hits);
    }
}
