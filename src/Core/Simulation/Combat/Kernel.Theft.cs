namespace Sts2Solver.Simulation.Combat;

public static partial class Kernel
{
    /// <summary>AI register of Waterfall Giant's <c>_pressureGunBonus</c> (definition declaration order).</summary>
    public const int PressureGunBonusRegister = 0;

    /// <summary>AI register of Waterfall Giant's <c>_steamEruptionDamage</c>.</summary>
    public const int SteamEruptionDamageRegister = 1;

    /// <summary>Mirrors <c>SimulatedCombatState.OutstandingStolenResource</c>.</summary>
    public static int OutstandingStolenResource(in Battle battle)
        => checked(battle.Counter(Counter.OutstandingStolenGold)
            + battle.Counter(Counter.OutstandingStolenCards));

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.MinimumOutstandingStolenResource</c>: every resource held by a
    /// present living enemy is still recoverable by killing it; only irrecoverable loot remains.
    /// </summary>
    public static int MinimumOutstandingStolenResource(in Battle battle)
    {
        int outstanding = OutstandingStolenResource(battle);
        ref Battle mutable = ref System.Runtime.CompilerServices.Unsafe.AsRef(in battle);
        long recoverable = 0;
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Amount <= 0 || !IsEnemyOwner(power.Owner))
                continue;
            if (!InRoster(battle, power.Owner) || !mutable.Creature(power.Owner).IsAlive)
                continue;
            string nativeId = Registry.Power(power.Id).NativeId;
            if (nativeId == "SWIPE_POWER")
            {
                if (power.Aux0 != 0)
                    recoverable++;
            }
            else if (nativeId == "HEIST_POWER")
            {
                recoverable += Math.Max(0, power.Amount);
            }
            else if (nativeId == "THIEVERY_POWER")
            {
                recoverable += Math.Max(0, power.Aux0);
            }
        }
        return (int)Math.Max(0, outstanding - recoverable);
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.RecordThievery</c> (GremlinMerc's GIMME/DOUBLE_SMASH/HEHE):
    /// the first <c>ThieveryPower</c> instance of <paramref name="owner"/> steals
    /// <c>min(power.Amount, player gold)</c>, adds it to its private gold ledger
    /// (<see cref="Power.Aux0"/>) and to the outstanding resource counter, and removes it from
    /// the player's gold. A missing target, a dead player or zero gold is a no-op, like the existing
    /// engine.
    /// </summary>
    public static void RecordThievery(ref Exec x, byte owner)
    {
        if (!Registry.TryPower("THIEVERY_POWER", out ushort id))
            throw new UnsupportedException("偷窃需要 THIEVERY_POWER 的 Sts2Solver 定义。");
        int index = FindPower(x.B, owner, id);
        if (index < 0)
            return;
        ref Power power = ref x.B.Powers[index];
        if (power.Target != Limits.PlayerOwner || x.B.Player.IsDead)
            return;
        int stolen = Math.Min(power.Amount, x.B.Gold);
        if (stolen <= 0)
            return;
        x.B.AddCounter(Counter.OutstandingStolenGold, stolen);
        power.Aux0 = Clamp((long)power.Aux0 + stolen);
        x.B.Gold = Math.Max(0, x.B.Gold - stolen);
    }

    /// <summary>
    /// The THIEVERY move's card theft (<c>MonsterMoveEffects.ApplyBeforeAttack</c>): draws one card
    /// from the draw pile then discard pile, preferring the native rarity order, removes it from
    /// combat and stores its complete value in <see cref="Battle.DetachedCards"/> behind a new
    /// <c>SwipePower</c> row (<see cref="Power.Aux0"/> = slot + 1). The value stays in the detached
    /// table when the power is removed: death clears <see cref="CardFlags.RemovedFromState"/> and
    /// the write-back appends the card to the deck end; escape keeps the marker and the card is lost.
    /// </summary>
    public static void StealCardForThievery(ref Exec x, byte owner)
    {
        ushort imbued = Registry.TryEnchantment("IMBUED", out ushort imbuedId)
            ? (ushort)(imbuedId + 1)
            : (ushort)0;
        int count = ThieveryCandidates(x.B, x.R, -1, imbued);
        if (count == 0)
            return;
        int selectedPriority = -1;
        for (int priority = 0; priority < 4; priority++)
        {
            int preferred = ThieveryCandidates(x.B, x.R, priority, imbued);
            if (preferred == 0)
                continue;
            count = preferred;
            selectedPriority = priority;
            break;
        }
        int chosen = x.B.RngStream(RngStream.CombatCardGeneration).NextInt(0, count);
        Pile pile = Pile.Draw;
        int position = -1;
        for (int absolute = x.B.HandCount; absolute < x.B.HandCount + x.B.DrawCount + x.B.DiscardCount; absolute++)
        {
            ref readonly Card card = ref x.B.Cards[absolute];
            if ((card.Flags & CardFlags.DeckCard) == 0)
                continue;
            if (!MatchesThieveryPriority(card, x.R, selectedPriority, imbued))
                continue;
            if (chosen-- != 0)
                continue;
            pile = absolute < x.B.HandCount + x.B.DrawCount ? Pile.Draw : Pile.Discard;
            position = absolute - Piles.Start(x.B, pile);
            break;
        }
        if (position < 0)
            throw new InvalidOperationException("偷牌候选非空但没有定位到牌。");
        Card stolen = Piles.At(ref x.B, pile, position);
        // CardPileCmd.RemoveFromCombat marks the card's preview HasBeenRemovedFromState; the detached
        // value keeps that flag because the existing importer projects it from the stored preview.
        stolen.Flags |= CardFlags.RemovedFromState;
        int slot = AllocateDetachedCard(ref x.B, stolen);
        Piles.RemoveAt(ref x.B, pile, position);
        ushort swipe = Registry.TryPower("SWIPE_POWER", out ushort swipeId)
            ? swipeId
            : throw new UnsupportedException("偷牌需要 SWIPE_POWER 的 Sts2Solver 定义。");
        int applied = ApplyPowerTargeted(ref x, owner, Limits.PlayerOwner, swipe, 1, owner);
        if (applied == 0)
            throw new InvalidOperationException("飞贼的 SwipePower 未能施加。");
        int row = FindLastPower(x.B, owner, swipe);
        if (row < 0)
            throw new InvalidOperationException("飞贼的 SwipePower 行缺失。");
        x.B.Powers[row].Aux0 = slot + 1;
        x.B.AddCounter(Counter.OutstandingStolenCards, 1);
    }

    /// <summary>
    /// Number of theft candidates in the draw/discard piles; <paramref name="priority"/> -1 counts
    /// every deck card, otherwise the native <c>ThievingHopper._stealPriorities</c> predicate.
    /// </summary>
    private static int ThieveryCandidates(in Battle battle, Rules rules, int priority, ushort imbued)
    {
        int count = 0;
        int end = battle.HandCount + battle.DrawCount + battle.DiscardCount;
        for (int index = battle.HandCount; index < end; index++)
        {
            ref readonly Card card = ref battle.Cards[index];
            if ((card.Flags & CardFlags.DeckCard) == 0)
                continue;
            if (priority < 0 || MatchesThieveryPriority(card, rules, priority, imbued))
                count++;
        }
        return count;
    }

    private static bool MatchesThieveryPriority(in Card card, Rules rules, int priority, ushort imbued)
    {
        // ThievingHopper._stealPriorities: Uncommon; Common/Rare/Event; Basic/Quest; Ancient or Imbued.
        // The first three exclude Imbued cards; the last selects them.
        bool isImbued = imbued != 0 && card.EnchantmentId == imbued;
        CardRarity rarity = rules.Card(card.Id, card.Upgrade).Rarity;
        return priority switch
        {
            0 => !isImbued && rarity == CardRarity.Uncommon,
            1 => !isImbued && rarity is CardRarity.Common or CardRarity.Rare or CardRarity.Event,
            2 => !isImbued && rarity is CardRarity.Basic or CardRarity.Quest,
            3 => rarity == CardRarity.Ancient || isImbued,
            _ => true,
        };
    }

    /// <summary>
    /// Mirrors <c>SimulatedCombatState.CreatureEscaped</c>: drop every power of the escaped creature,
    /// remove it from the roster, and compact the known-enemy array so later rows and every owner
    /// reference shift down by one. The escaped monster is then appended at the end as an
    /// <see cref="EnemyFlags.Escaped"/> permanent tombstone; the native run history already added its
    /// monster id, and the write-back needs the row to append that id exactly once. The tombstone is
    /// not in the roster. The escaped list itself is not part of the prediction state (only the
    /// encounter's post-combat gold proportion reads it).
    /// </summary>
    public static void EscapeEnemy(ref Exec x, byte owner)
    {
        int enemyIndex = owner - Limits.FirstEnemyOwner;
        if ((uint)enemyIndex >= (uint)x.B.EnemyCount || !InRoster(x.B, owner))
            throw new InvalidOperationException("逃跑的生物不在当前模拟战斗中。");
        // Mark before dropping powers: RemovePowerAt distinguishes a death return (clear the
        // detached card's RemovedFromState marker) from an escape loss (keep it).
        x.B.Enemies[enemyIndex].Flags |= EnemyFlags.Escaped;
        RemovePowersOf(ref x.B, owner);
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref readonly Power power = ref x.B.Powers[index];
            if (power.Applier == owner || power.Target == owner)
                throw new UnsupportedException("逃跑生物仍被其他 Power 的施法者/目标引用，Sts2Solver 无法表示。");
        }
        int roster = 0;
        for (int index = 0; index < x.B.RosterCount; index++)
        {
            int current = x.B.Roster[index];
            if (current == enemyIndex)
                continue;
            x.B.Roster[roster++] = checked((byte)(current > enemyIndex ? current - 1 : current));
        }
        for (int index = roster; index < x.B.RosterCount; index++)
            x.B.Roster[index] = 0;
        x.B.RosterCount = checked((byte)roster);
        Enemy escaped = x.B.Enemies[enemyIndex];
        for (int index = enemyIndex; index + 1 < x.B.EnemyCount; index++)
            x.B.Enemies[index] = x.B.Enemies[index + 1];
        x.B.Enemies[x.B.EnemyCount - 1] = default;
        x.B.EnemyCount--;
        for (int index = 0; index < x.B.PowerCount; index++)
        {
            ref Power power = ref x.B.Powers[index];
            if (power.Owner > owner)
                power.Owner--;
            if (power.Applier > owner && power.Applier != Limits.NoOwner)
                power.Applier--;
            if (power.Target > owner && power.Target != Limits.NoOwner)
                power.Target--;
        }
        // Preserve the escaped monster as a tombstone at the end of the known table. Native removes
        // it from CombatState.Enemies, but the run history keeps the monster id added at spawn time;
        // the write-back needs that row to append it exactly once. The tombstone is not in the
        // roster and no power references it.
        if (x.B.EnemyCount >= Limits.Enemies)
            throw new CapacityException("Enemies", Limits.Enemies);
        escaped.Flags = (escaped.Flags & ~(EnemyFlags.InCombat | EnemyFlags.NeedsInitialRoll
            | EnemyFlags.SkipNextMove | EnemyFlags.PerformedMoveThisRound
            | EnemyFlags.ForcedMustPerformAfterMove | EnemyFlags.ActingThisRound))
            | EnemyFlags.Escaped;
        escaped.DeathPhase = DeathPhase.PermanentlyDead;
        escaped.Move = 0;
        escaped.ForcedMove = byte.MaxValue;
        escaped.LoggedMoves = 0;
        escaped.EverUsedMoves = 0;
        escaped.PoweredAttackHitsThisTurn = 0;
        escaped.SteamPhase = SteamPhase.None;
        escaped.RecentMoves = default;
        escaped.Ai = default;
        x.B.Enemies[x.B.EnemyCount++] = escaped;
    }

    /// <summary>
    /// <c>SimulatedCombatState.PrepareSteamEruption</c>: record the current Steam Eruption amount as
    /// the explosion damage and remove the power. The damage lives in the AI register projected from
    /// <c>_steamEruptionDamage</c>.
    /// </summary>
    public static void PrepareSteamEruption(ref Exec x, byte owner)
    {
        if (!Registry.TryPower("STEAM_ERUPTION_POWER", out ushort steam))
            throw new UnsupportedException("蒸汽爆发需要 STEAM_ERUPTION_POWER 的 Sts2Solver 定义。");
        int amount = Math.Max(0, GetAmount(x.B, owner, steam));
        x.B.Enemy(owner).Ai[SteamEruptionDamageRegister] = amount;
        SetPowerAmount(ref x.B, owner, steam, 0);
    }

    /// <summary>
    /// <c>SteamEruptionPower.AfterDeath</c> plus <c>WaterfallGiant.TriggerAboutToBlowState</c>: a
    /// Waterfall Giant that dies while holding Steam Eruption is rewritten to its forced about-to-blow
    /// phase instead of dying. HP and max HP become the value clamp, the current move is forced to
    /// <c>ABOUT_TO_BLOW_MOVE</c>, the phase is armed, and the death power cleanup runs (Steam Eruption
    /// itself is kept by <c>ShouldPowerBeRemovedAfterOwnerDeath</c>). The native AfterDeath hook runs
    /// immediately inside the kill, so later damage in the same action hits the rewritten creature.
    /// </summary>
    public static void TriggerSteamEruptionDeath(ref Exec x, byte owner)
    {
        if (!IsEnemyOwner(owner))
            throw new UnsupportedException("SteamEruptionPower 只能作用于敌方生物。");
        ref Enemy enemy = ref x.B.Enemy(owner);
        if (Registry.Monster(enemy.MonsterId).NativeId != "WATERFALL_GIANT")
            throw new UnsupportedException("SteamEruptionPower 只支持 Waterfall Giant。");
        ref Creature creature = ref x.B.Creature(owner);
        creature.MaxHp = Limits.ValueClamp;
        creature.CurrentHp = Limits.ValueClamp;
        MonsterRules rules = MonsterRules(ref x, owner);
        ForceMonsterMove(ref x, owner, rules.NodeIndex("ABOUT_TO_BLOW_MOVE"));
        enemy.SteamPhase = SteamPhase.AboutToBlow;
        RemovePowersAfterDeath(ref x, owner);
    }

    /// <summary>
    /// <c>SimulatedCombatState.TryConsumeForcedMonsterMove</c>: the forced phase replaces the normal
    /// action. About-to-blow records the explosion damage and advances to the explode phase without
    /// executing the move; explode reports the damage for the caller to apply before force-killing the
    /// owner.
    /// </summary>
    public static bool TryConsumeSteamForcedMove(ref Exec x, byte owner, out bool explode, out int damage)
    {
        explode = false;
        damage = 0;
        ref Enemy enemy = ref x.B.Enemy(owner);
        switch (enemy.SteamPhase)
        {
            case SteamPhase.None:
                return false;
            case SteamPhase.AboutToBlow:
                PrepareSteamEruption(ref x, owner);
                x.B.Enemy(owner).SteamPhase = SteamPhase.Explode;
                return true;
            case SteamPhase.Explode:
                damage = x.B.Enemy(owner).Ai[SteamEruptionDamageRegister];
                x.B.Enemy(owner).SteamPhase = SteamPhase.None;
                explode = true;
                return true;
            default:
                throw new UnsupportedException($"未知的蒸汽爆发阶段 {(byte)enemy.SteamPhase}。");
        }
    }

    /// <summary>
    /// <c>SimulatedCombatState.RecoverStolenResources</c>: a dead thief returns the cards behind its
    /// SwipePower rows and the gold of its HeistPower rows. ThieveryPower gold is not returned here
    /// (Surprise transfers it to a Heist carrier first).
    /// </summary>
    private static void RecoverStolenResources(ref Battle battle, byte dead)
    {
        for (int index = 0; index < battle.PowerCount; index++)
        {
            ref readonly Power power = ref battle.Powers[index];
            if (power.Owner != dead || power.Amount <= 0)
                continue;
            string nativeId = Registry.Power(power.Id).NativeId;
            if (nativeId == "SWIPE_POWER" && power.Aux0 != 0)
            {
                battle.SetCounter(Counter.OutstandingStolenCards,
                    Math.Max(0, battle.Counter(Counter.OutstandingStolenCards) - 1));
            }
            else if (nativeId == "HEIST_POWER")
            {
                battle.SetCounter(Counter.OutstandingStolenGold,
                    Math.Max(0, battle.Counter(Counter.OutstandingStolenGold) - Math.Max(0, power.Amount)));
            }
        }
    }
}
