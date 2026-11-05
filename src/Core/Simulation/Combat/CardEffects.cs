namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// Shared card effect building blocks, mirroring <c>GeneralCardMirrors</c> and the common
/// compensation calls. Variable index i reads <c>Stats.Vars[i]</c> plus the card's delta slot i.
/// </summary>
public static class CardEffects
{
    /// <summary>Number of cards in every combat pile whose canonical tags include <paramref name="tag"/>.</summary>
    public static int CountCardsWithTag(in Exec x, CardTag tag)
    {
        int count = 0;
        for (int position = 0; position < x.B.CardCount; position++)
        {
            ref readonly Card card = ref x.B.Cards[position];
            if ((x.R.Card(card.Id, card.Upgrade).Tags & tag) != 0)
                count++;
        }
        return count;
    }

    /// <summary>Current value of the card's i-th declared dynamic variable.</summary>
    public static decimal Var(in Exec x, int index)
    {
        CardStats stats = x.Stats ?? throw new InvalidOperationException("当前没有正在打出的卡。");
        return Var(in x.B, in x.Card, stats, index);
    }

    /// <summary>Current value of variable <paramref name="index"/> for a card outside a play.</summary>
    public static decimal Var(in Battle battle, Rules rules, in Card card, int index)
        => Var(in battle, in card, rules.Card(card.Id, card.Upgrade), index);

    /// <summary>Current value of variable <paramref name="index"/> against an explicit rule row.</summary>
    public static decimal Var(in Battle battle, in Card card, CardStats stats, int index)
    {
        if ((uint)index >= (uint)stats.Vars.Length)
            throw new UnsupportedException(
                $"卡牌 {Registry.Card(card.Id).NativeId} 定义读取第 {index} 个动态变量，但规则表只有 {stats.Vars.Length} 个。");
        return stats.Vars[index] + DecimalVars.Delta(in battle, in card, index);
    }

    /// <summary>Mirrors <c>GeneralCardMirrors.GeneralAttackOnPlay</c> for a plain damage variable and one hit.</summary>
    public static void GeneralAttack(ref Exec x, int damageVar, int hitCount = 1,
        bool recordAttack = true, bool endAttack = true)
        => Attack(ref x, Var(x, damageVar), hitCount, recordAttack, endAttack);

    /// <summary>
    /// The attack half of the general recipe with an already-calculated damage value (BodySlam,
    /// PerfectedStrike and the other <c>CalculatedDamageVar</c> cards).
    /// </summary>
    public static void Attack(ref Exec x, decimal damage, int hitCount = 1, bool recordAttack = true,
        bool endAttack = true)
    {
        CardStats stats = x.Stats ?? throw new InvalidOperationException("当前没有正在打出的卡。");
        TargetType targetType = Registry.Card(x.Card.Id).ResolveTargetType(x.B, x.R, x.Card);
        AttackTargeting targeting = targetType switch
        {
            TargetType.AnyEnemy => AttackTargeting.Single,
            TargetType.AllEnemies => AttackTargeting.AllOpponents,
            TargetType.RandomEnemy => AttackTargeting.Random,
            _ => throw new UnsupportedException($"攻击目标类型 {targetType} 尚未实现。"),
        };
        Kernel.CardAttack(ref x, damage, hitCount, targeting, x.Target, recordAttack: recordAttack,
            endAttack: endAttack);
    }

    /// <summary>
    /// Self damage from a card that keeps the card as its damage source, matching
    /// <c>Simulator.Damage(..., playedCard, cardPlay)</c> (Offering, Bloodletting).
    /// </summary>
    public static void LoseHpFromCard(ref Exec x, decimal amount)
    {
        Span<byte> targets = stackalloc byte[1];
        targets[0] = Limits.PlayerOwner;
        Kernel.Damage(ref x, targets, amount,
            ValueProps.Unblockable | ValueProps.Unpowered | ValueProps.Move,
            Limits.PlayerOwner, hasCardSource: true, x.Card);
    }

    /// <summary>
    /// Damage from the card to its owner with the card as source, matching
    /// <c>CreatureCmd.Damage(context, card.Owner.Creature, amount, props, card, null)</c> (the
    /// turn-end-in-hand cards: <c>CardOnTurnEndInHandMirrors.DamageOwner</c>).
    /// </summary>
    public static void DamageOwnerFromCard(ref Exec x, decimal amount, ValueProps props)
    {
        Span<byte> targets = stackalloc byte[1];
        targets[0] = Limits.PlayerOwner;
        Kernel.Damage(ref x, targets, amount, props, Limits.PlayerOwner, hasCardSource: true, x.Card);
    }

    /// <summary>Mirrors <c>GeneralCardMirrors.GeneralBlockOnPlay</c>.</summary>
    public static void GeneralBlock(ref Exec x, int blockVar)
        => Kernel.GainBlock(ref x, Limits.PlayerOwner, Var(x, blockVar), ValueProps.Move, fromCard: true);

    /// <summary>
    /// The common compensation <c>combat.Apply&lt;T&gt;(target, card.DynamicVars.X.IntValue, owner)</c>
    /// guarded by <c>when target != null</c>.
    /// </summary>
    public static void ApplyToTarget(ref Exec x, ushort powerId, int amountVar)
    {
        if (x.Target == Limits.NoOwner)
            return;
        Kernel.ApplyPower(ref x, x.Target, powerId, (int)Var(x, amountVar), Limits.PlayerOwner);
    }

    /// <summary>
    /// The common compensation <c>combat.Apply&lt;T&gt;(owner, card.DynamicVars.X.IntValue, owner)</c>.
    /// </summary>
    public static void ApplyToOwner(ref Exec x, ushort powerId, int amountVar)
        => Kernel.ApplyPower(ref x, Limits.PlayerOwner, powerId, (int)Var(x, amountVar),
            Limits.PlayerOwner);

    /// <summary>
    /// The common compensation <c>combat.Apply&lt;T&gt;(enemy, ...)</c> over
    /// <c>SimulatedCombatState.HittableEnemies</c> in roster order.
    /// </summary>
    public static void ApplyToAllEnemies(ref Exec x, ushort powerId, int amountVar)
    {
        int amount = (int)Var(x, amountVar);
        // SimulatedCombatState.HittableEnemies returns a snapshot: an application that kills its target
        // (Sleight of Flesh, Doom kill) compacts the roster, and the native foreach still visits every
        // enemy that was hittable when the effect started. Iterating the live roster count would skip
        // the entries that shifted left.
        Span<byte> owners = stackalloc byte[Limits.Enemies];
        int count = x.B.RosterCount;
        for (int position = 0; position < count; position++)
            owners[position] = Kernel.EnemyOwner(x.B.Roster[position]);
        for (int position = 0; position < count; position++)
        {
            byte owner = owners[position];
            if (!Kernel.IsHittable(x.B, owner))
                continue;
            Kernel.ApplyPower(ref x, owner, powerId, amount, Limits.PlayerOwner);
            // CorePowerSupport.ApplyAll resolves after each target's application.
            Kernel.ResolvePowerAmountChanges(ref x);
            if (x.NeedsChoice)
                return;
        }
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to both the current play's card copy and the card in the play
    /// pile, mirroring a native <c>DynamicVars[...].BaseValue +=</c> on the played card (Rampage).
    /// Only the two native short-sized variable slots are writable.
    /// </summary>
    public static void AddCurrentCardVar(ref Exec x, int index, decimal delta)
    {
        if (delta == 0m)
            return;
        // The current card sits at Exec.PlayPosition, not at the top of the play pile: a nested
        // auto-play (OwnChoice discarding a Sly card, an in-effect auto-play) appends its card after
        // the outer one and leaves before the outer tail resumes.
        int position = x.PlayPosition;
        if ((uint)position >= (uint)x.B.PlayCount)
            throw new InvalidOperationException("当前没有正在打出的卡。");
        ref Card played = ref Piles.At(ref x.B, Pile.Play, position);
        if (played.Id != x.Card.Id)
            throw new InvalidOperationException("当前打出的卡与打出堆位置不一致。");
        // The live play-pile copy is the native mutable CardModel; a mid-play upgrade (Dampen's last
        // caster dying during this card's attack) changes it before the tail reads its variables.
        if (played.Upgrade != x.Card.Upgrade)
            Kernel.SyncCurrentPlayCard(ref x);
        if (index == 0 && DecimalVars.IsTracked(in played))
        {
            decimal current = DecimalVars.TryGet(in x.B, in played, out decimal existing) ? existing : 0m;
            decimal decimalValue = current + delta;
            // DynamicVar.BaseValue's setter clamps at 999,999,999m while Thrash.ExtraDamage is not
            // clamped; a clamped offset could no longer reproduce AfterDowngraded. Keep the clamp as
            // an explicit boundary instead of storing a value the native field would have truncated.
            CardStats decimalStats = x.R.Card(played.Id, played.Upgrade);
            if (decimalStats.Vars.Length > 0 && decimalStats.Vars[0] + decimalValue > Limits.ValueClamp)
                throw new UnsupportedException("Thrash 的小数伤害变量超过 DynamicVar.BaseValue 的 999999999 钳制上限。");
            DecimalVars.Assign(ref x.B, ref played, decimalValue);
            x.Card.Private = played.Private;
            return;
        }
        // Card's integer variable slots are the representation for every other card. A
        // fractional delta on a card that did not opt into the decimal table is a kernel boundary.
        if (delta != decimal.Truncate(delta))
            throw new UnsupportedException($"卡牌变量偏移 {delta} 不是整数，Sts2Solver 卡牌变量无法表示。");
        short value = checked((short)(index switch
        {
            0 => played.Var0,
            1 => played.Var1,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "只有前两个卡牌变量可写。"),
        } + (short)delta));
        if (index == 0)
        {
            played.Var0 = value;
            x.Card.Var0 = value;
        }
        else
        {
            played.Var1 = value;
            x.Card.Var1 = value;
        }
    }

    /// <summary>
    /// Upgrades the card currently being played (RazorTooth's <c>CardCmd.Upgrade(cardPlay.Card)</c>).
    /// The played card is the top of the play pile while its hooks run; only its identity is required
    /// because an earlier replay may already have upgraded the pile entry.
    /// </summary>
    public static void UpgradeCurrentCard(ref Exec x)
    {
        // CombatPredictionSimulator.Upgrade is a no-op while the combat is ending; RazorTooth uses it
        // for the played card and a card that lands the killing blow must stay unupgraded in the play
        // pile at the resulting stable boundary.
        if (Kernel.IsEnding(x.B))
            return;
        int position = x.B.PlayCount - 1;
        if (position < 0)
            throw new InvalidOperationException("当前没有正在打出的卡。");
        ref Card played = ref Piles.At(ref x.B, Pile.Play, position);
        if (played.Id != x.Card.Id)
            throw new InvalidOperationException("打出堆顶不是当前卡。");
        Kernel.UpgradeCard(x.R, ref played);
        // Native CardCmd.Upgrade mutates the CardModel that CardPlay.Card references. The next
        // replay index (Spiral) and the late AfterCardPlayed hooks read the upgraded values.
        Kernel.SyncCurrentPlayCard(ref x);
    }

    /// <summary>
    /// Damage from a relic to every hittable enemy in roster order, with no card source
    /// (<c>simulator.Damage(state.HittableEnemies, ..., ValueProp.Unpowered, relic.Owner.Creature)</c>).
    /// </summary>
    public static void DamageAllEnemies(ref Exec x, decimal amount, ValueProps props, byte dealer)
    {
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (Kernel.IsHittable(x.B, owner))
                targets[count++] = owner;
        }
        if (count == 0)
            return;
        Kernel.Damage(ref x, targets[..count], amount, props, dealer, hasCardSource: false, default);
    }

    /// <summary>
    /// Samples <paramref name="count"/> distinct cards of one canonical type from the root-frozen
    /// <see cref="CardPools.CharacterAll"/> pool, mirroring
    /// <c>CombatCardGenerationExtensions.GetDistinctUnlockedCharacterCardsForCombat</c>'s predicate
    /// path: the caller's type filter is applied to the combat/player-count-filtered candidates in
    /// pool order, then <c>TakeRandom</c> runs a full Fisher-Yates over the filtered list before
    /// taking the first cards (the distinct path of <see cref="Kernel.SampleCardPool"/>, with the
    /// filter applied first). <paramref name="type"/> <see cref="CardType.None"/> matches every
    /// type; a non-None <paramref name="rarity"/> adds the native rarity predicate. Cards are created
    /// at upgrade 0 by <see cref="Kernel.CreateCard"/>.
    /// </summary>
    public static int SampleCharacterCardsOfType(ref Battle battle, Rules rules, CardType type,
        int count, Span<Card> selected, string poolKey = CardPools.CharacterAll,
        CardRarity rarity = CardRarity.None)
    {
        CardPool pool = rules.CardPool(poolKey);
        if (pool.Cards.Length == 0)
            return 0;
        if (count > 0 && selected.Length < count)
            throw new CapacityException("GeneratedCards", selected.Length);
        Span<ushort> order = stackalloc ushort[pool.Cards.Length];
        int eligible = 0;
        for (int index = 0; index < pool.Cards.Length; index++)
        {
            ushort id = pool.Cards[index];
            CardStats stats = rules.Card(id, 0);
            if (type != CardType.None && stats.Type != type)
                continue;
            if (rarity != CardRarity.None && stats.Rarity != rarity)
                continue;
            order[eligible++] = id;
        }
        if (eligible == 0)
            return 0;
        order = order[..eligible];
        ref RngState rng = ref battle.RngStream(RngStream.CombatCardGeneration);
        for (int n = order.Length; n > 1;)
        {
            n--;
            int k = rng.NextInt(n + 1);
            (order[k], order[n]) = (order[n], order[k]);
        }
        int take = Math.Min(count, order.Length);
        for (int index = 0; index < take; index++)
            selected[index] = Kernel.CreateCard(rules, order[index]);
        return take;
    }

    /// <summary>Number of cards in every non-exhaust pile whose canonical type is Status (FlakCannon).</summary>
    public static int CountStatusCards(in Battle battle, Rules rules)
    {
        int count = 0;
        for (int position = 0; position < battle.CardCount; position++)
        {
            ref readonly Card card = ref battle.Cards[position];
            if (Kernel.ResolveCardType(battle, rules, card) == CardType.Status)
                count++;
        }
        // The exhaust pile is included in CardCount but excluded from the native AllCards filter.
        for (int position = 0; position < battle.ExhaustCount; position++)
        {
            int absolute = Piles.Start(battle, Pile.Exhaust) + position;
            if (Kernel.ResolveCardType(battle, rules, battle.Cards[absolute]) == CardType.Status)
                count--;
        }
        return count;
    }

    /// <summary>
    /// Exhausts every Status card outside the exhaust pile in <c>AllCards</c> order
    /// (Hand, Draw, Discard, Play), like FlakCannon's <c>GetStatuses</c> + <c>CardCmd.Exhaust</c>.
    /// The values are snapshotted and relocated by (value, occurrence) because exhaust hooks may move
    /// cards.
    /// </summary>
    public static void ExhaustAllStatusCards(ref Exec x)
    {
        Span<Card> cards = stackalloc Card[x.B.CardCount];
        Span<byte> sourcePile = stackalloc byte[x.B.CardCount];
        int count = 0;
        ReadOnlySpan<Pile> piles = [Pile.Hand, Pile.Draw, Pile.Discard, Pile.Play];
        for (int pileIndex = 0; pileIndex < piles.Length; pileIndex++)
        {
            Pile pile = piles[pileIndex];
            int start = Piles.Start(x.B, pile);
            int pileCount = Piles.Count(x.B, pile);
            for (int offset = 0; offset < pileCount; offset++)
            {
                Card card = x.B.Cards[start + offset];
                if (Kernel.ResolveCardType(x.B, x.R, card) != CardType.Status)
                    continue;
                cards[count] = card;
                sourcePile[count] = checked((byte)pileIndex);
                count++;
            }
        }
        for (int item = 0; item < count; item++)
        {
            // Equal status cards are interchangeable, so locating the first equal card in the recorded
            // pile preserves the value sequence even when an earlier equal card was removed first.
            bool found = false;
            Pile pile = piles[sourcePile[item]];
            if (TryExhaustFirstEqual(ref x, pile, cards[item]))
            {
                found = true;
            }
            else
            {
                foreach (Pile fallback in piles)
                {
                    if (!TryExhaustFirstEqual(ref x, fallback, cards[item]))
                        continue;
                    found = true;
                    break;
                }
            }
            if (!found)
                throw new UnsupportedException("主动消耗前一状态牌离开了战斗牌堆。");
            if (x.NeedsChoice)
                return;
        }
    }

    private static bool TryExhaustFirstEqual(ref Exec x, Pile pile, in Card card)
    {
        int start = Piles.Start(x.B, pile);
        int count = Piles.Count(x.B, pile);
        for (int offset = 0; offset < count; offset++)
        {
            if (!Equal(x.B.Cards[start + offset], card))
                continue;
            Kernel.Exhaust(ref x, pile, offset);
            return true;
        }
        return false;
    }

    /// <summary>Moves the whole hand to the bottom of the draw pile in hand order (Reboot).</summary>
    public static void MoveHandToDraw(ref Exec x)
    {
        int handCount = x.B.HandCount;
        Span<Card> hand = stackalloc Card[handCount];
        Piles.ReadCards(x.B, Pile.Hand).CopyTo(hand);
        foreach (Card card in hand)
        {
            int start = Piles.Start(x.B, Pile.Hand);
            int index = -1;
            for (int offset = 0; offset < x.B.HandCount; offset++)
            {
                if (Equal(x.B.Cards[start + offset], card))
                {
                    index = offset;
                    break;
                }
            }
            if (index < 0)
                throw new UnsupportedException("移动手牌时目标牌已不在手牌。");
            if (!Kernel.MoveCard(ref x.B, Pile.Hand, index, Pile.Draw))
                return;
        }
    }

    internal static bool Equal(in Card left, in Card right) => Fingerprint.Equal(left, right);

    /// <summary>
    /// One hit on a random hittable enemy, mirroring <c>Rng.CombatTargets.NextItem(HittableEnemies)</c>:
    /// the CombatTargets stream is consumed only when the candidate list is non-empty.
    /// </summary>
    public static void DamageRandomEnemy(ref Exec x, decimal amount, ValueProps props, byte dealer)
    {
        Span<byte> targets = stackalloc byte[Limits.Enemies];
        int count = 0;
        for (int position = 0; position < x.B.RosterCount; position++)
        {
            byte owner = Kernel.EnemyOwner(x.B.Roster[position]);
            if (Kernel.IsHittable(x.B, owner))
                targets[count++] = owner;
        }
        if (count == 0)
            return;
        Span<byte> target = stackalloc byte[1];
        target[0] = targets[x.B.RngStream(RngStream.CombatTargets).NextInt(0, count)];
        Kernel.Damage(ref x, target, amount, props, dealer, hasCardSource: false, default);
    }
}
