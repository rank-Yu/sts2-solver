// Ported from CombatSolver src/Flash/Bridge/FlashBoundBridge.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors). Per-content classifications are frozen in
// HealingBoundTables (the exact D1 answers); root predicates are reimplemented against the native
// models and the imported Battle.
using Sts2Solver.Simulation.Combat;
using Sts2Solver.Ai.Combat;
using Sts2Solver.Ai.Combat.Scoring;
using Sts2Solver.Ai.Combat.Policy;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Badges;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;

using Creature = MegaCrit.Sts2.Core.Entities.Creatures.Creature;
using EnchantmentInstinct = MegaCrit.Sts2.Core.Models.Enchantments.Instinct;

namespace Sts2Solver.Import;

/// <summary>
/// Compiles <see cref="BoundFeatures.Config"/> from the native root, the imported <see cref="Battle"/>
/// and the captured policy. Every field mirrors <c>FlashBoundBridge.Create</c>.
/// </summary>
internal static class BoundCompiler
{
    private static readonly Guid ComponentAuditMvid = new("8a76776c-0ce1-4d4f-90bd-8cce653dad8e");

    private static readonly bool[] UnsafeCards = HealingBoundTables.ComponentUnsafeCard();
    private static readonly bool[] UnsafePowers = HealingBoundTables.ComponentUnsafePower();
    private static readonly bool[] UnsafeRelics = HealingBoundTables.ComponentUnsafeRelic();
    private static readonly bool[] UnsafeMonsters = HealingBoundTables.ComponentUnsafeMonster();
    private static readonly bool[] UnsafePotions = HealingBoundTables.ComponentUnsafePotion();
    private static readonly bool[] RegenPowers = HealingBoundTables.RegenPower();
    private static readonly bool[] ReattachPowers = HealingBoundTables.ReattachPower();
    private static readonly bool[] SwipePowers = HealingBoundTables.SwipePower();
    private static readonly bool[] RegenPotions = HealingBoundTables.RegenPotion();
    private static readonly bool[] ReattachSafeMonsters = HealingBoundTables.ReattachSafeMonster();


    private static readonly HashSet<string> OnlyPostCombatHealingCardTypes = new(StringComparer.Ordinal)
    {
        "MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad",
        "MegaCrit.Sts2.Core.Models.Cards.StrikeNecrobinder",
        "MegaCrit.Sts2.Core.Models.Cards.StrikeSilent",
        "MegaCrit.Sts2.Core.Models.Cards.DefendIronclad",
        "MegaCrit.Sts2.Core.Models.Cards.DefendNecrobinder",
        "MegaCrit.Sts2.Core.Models.Cards.DefendSilent",
        "MegaCrit.Sts2.Core.Models.Cards.Neutralize",
        "MegaCrit.Sts2.Core.Models.Cards.Survivor",
        "MegaCrit.Sts2.Core.Models.Cards.Bash",
        "MegaCrit.Sts2.Core.Models.Cards.Bodyguard",
        "MegaCrit.Sts2.Core.Models.Cards.Unleash",
        "MegaCrit.Sts2.Core.Models.Cards.Bloodletting",
        "MegaCrit.Sts2.Core.Models.Cards.Neurosurge",
    };

    public static BoundFeatures.Config Compile(
        CombatState state,
        Player player,
        Battle root,
        SearchPolicy policy,
        EvaluationRootFacts facts)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(policy);

        PostCombatRelicHealProfile postCombat = ScoreCompiler.CompilePostCombatRelicHeal(player);
        bool component = ComponentCertificate(state, player);
        bool knownNative = KnownNativeCertificate(state, player);
        bool canCertify = component || EnvironmentCertificate(state, player);
        string? disabled = DisabledReason(policy, root, canCertify, component);
        if (disabled != null)
            return Disabled(disabled);

        if (component && HasRegenPotion(player))
            return Disabled("regen_potion_policy");
        if (HasSwipePower(state))
            return Disabled("swipe_power");

        HealingCertificate certificate = component
            ? HealingCertificate.Component
            : knownNative
                ? HealingCertificate.KnownNative
                : HealingCertificate.None;
        bool onlyPostCombat = HasOnlyPostCombatHealing(state, player);
        return new BoundFeatures.Config
        {
            Enabled = true,
            DisabledReason = "",
            FutureHealPotential = onlyPostCombat
                ? Math.Max(0, postCombat.UnconditionalHeal)
                : int.MaxValue,
            Certificate = certificate,
            PostCombatHeal = checked(postCombat.UnconditionalHeal + postCombat.WoundedHeal),
            BossHpRelief = ScoreCompiler.ResolveBossHpRelief(state, policy),
            RequireCertifiedHealingProof = component,
            PruneEqualHp = policy.RelicTargets.Count == 0,
            ForceAllPotionsDisabled = false,
            MaximumPotionUses = null,
            ComponentUnsafeCard = UnsafeCards,
            ComponentUnsafePower = UnsafePowers,
            ComponentUnsafeRelic = UnsafeRelics,
            ComponentUnsafeMonster = UnsafeMonsters,
            ComponentUnsafePotion = UnsafePotions,
            SafeEnchantment = HealingBoundTables.SafeEnchantment(),
            SafeAffliction = HealingBoundTables.SafeAffliction(),
            KnownBlockingCard = HealingBoundTables.KnownBlockingCard(),
            KnownBlockingRelic = HealingBoundTables.KnownBlockingRelic(),
            RegenPower = RegenPowers,
            ReattachPower = ReattachPowers,
            SwipePower = SwipePowers,
            RegenPotion = RegenPotions,
            DirectHealPotion = HealingBoundTables.DirectHealPotion(),
            FruitJuicePotion = HealingBoundTables.FruitJuicePotion(),
            FairyPotion = HealingBoundTables.FairyPotion(),
            NativePotion = HealingBoundTables.NativePotion(),
            RegenPotionAmount = HealingBoundTables.RegenPotionAmount(),
            DirectHealPotionPercent = HealingBoundTables.ExpandPercents(),
            ReattachSafeMonster = ReattachSafeMonsters,
            RegenPowerId = HealingBoundTables.RegenPowerId,
            ReattachPowerId = HealingBoundTables.ReattachPowerId,
            RootPotions = RootPotions(root),
            RootPotionSlotCount = root.PotionSlotCount,
        };
    }

    private static string? DisabledReason(SearchPolicy policy, Battle root, bool canCertify,
        bool component)
    {
        if (policy.EffectiveHasGrowthTargets)
            return "growth_targets";
        if (policy.RelicTargets.Count != 0)
            return "relic_targets";
        if (policy.TheftPolicy == SearchTheftPolicy.PreserveResources)
            return "theft_policy";
        if (GrowthRewardTotal(root) != 0)
            return "growth_rewards";
        if ((root.Flags & BattleFlags.HasRisk) != 0)
            return "root_risk";
        if (HasPendingReturningCards(root))
            return "pending_returning_cards";
        if (canCertify && !component)
            return "remaining_certificate";
        return null;
    }

    private static BoundFeatures.Config Disabled(string reason) => new()
    {
        Enabled = false,
        DisabledReason = reason,
        FutureHealPotential = int.MaxValue,
        Certificate = HealingCertificate.None,
        PostCombatHeal = 0,
        BossHpRelief = BossHpRelief.None,
        RequireCertifiedHealingProof = false,
        PruneEqualHp = false,
        ForceAllPotionsDisabled = false,
        MaximumPotionUses = null,
        ComponentUnsafeCard = [],
        ComponentUnsafePower = [],
        ComponentUnsafeRelic = [],
        ComponentUnsafeMonster = [],
        ComponentUnsafePotion = [],
        SafeEnchantment = [],
        SafeAffliction = [],
        KnownBlockingCard = [],
        KnownBlockingRelic = [],
        RegenPower = [],
        ReattachPower = [],
        SwipePower = [],
        RegenPotion = [],
        DirectHealPotion = [],
        FruitJuicePotion = [],
        FairyPotion = [],
        NativePotion = [],
        RegenPotionAmount = [],
        DirectHealPotionPercent = [],
        ReattachSafeMonster = [],
        RegenPowerId = -1,
        ReattachPowerId = -1,
        RootPotions = [],
        RootPotionSlotCount = 0,
    };

    private static bool HasPendingReturningCards(in Battle root)
    {
        for (int index = 0; index < root.CardCount; index++)
        {
            if (root.Cards[index].ReturnToHandNextTurn)
                return true;
        }
        return false;
    }

    private static int GrowthRewardTotal(in Battle root)
    {
        int total = 0;
        foreach (GrowthRewardSource source in Enum.GetValues<GrowthRewardSource>())
            total = checked(total + GrowthRewards.Get(root, source));
        return total;
    }

    private static ushort[] RootPotions(in Battle root)
    {
        ushort[] potions = new ushort[Limits.PotionSlots];
        for (int slot = 0; slot < Limits.PotionSlots && slot < root.PotionSlotCount; slot++)
            potions[slot] = root.Potions[slot];
        return potions;
    }

    /// <summary>
    /// <c>StrategicHpRecoveryBound.ComponentHealingRejection</c> minus the mod-subscriber facts the
    /// offline host never sets. Closed component certificate over the audited native assembly.
    /// </summary>
    private static bool ComponentCertificate(CombatState state, Player player)
    {
        if (typeof(AbstractModel).Module.ModuleVersionId != ComponentAuditMvid)
            return false;
        if (state.Players.Count != 1 || state.Modifiers.Count != 0)
            return false;
        if (player.Character is not (Ironclad or Silent or Regent or Necrobinder
            or MegaCrit.Sts2.Core.Models.Characters.Defect))
        {
            return false;
        }
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new UnsupportedImportException(ImportReason.Unrepresentable,
                "玩家没有 PlayerCombatState");
        foreach (CardModel card in playerState.AllCards)
        {
            if (!ComponentInitialCard(card))
                return false;
        }
        foreach (AbstractModel listener in RootHookListeners(state))
        {
            if (!ComponentRootSource(listener))
                return false;
        }
        return ComponentUpperBound(state, player);
    }


    /// <summary>
    /// <c>StrategicHpRecoveryBound.CanCertifyRemainingHealingEnvironment</c> general branch. The
    /// Defect/Decimillipede special branches are not ported; they return false and the D1 corpus
    /// never reaches them with a non-component certificate.
    /// </summary>
    private static bool EnvironmentCertificate(CombatState state, Player player)
    {
        if (player.Character is not (Regent or Silent))
            return false;
        if (state.Players.Count != 1 || state.Modifiers.Count != 0)
            return false;
        bool nativeLouse = player.Character is Regent && state.Enemies.Count > 0
            && state.Enemies.All(static enemy => enemy.Monster is LouseProgenitor);
        if (!nativeLouse)
        {
            foreach (Creature enemy in state.Enemies)
            {
                if (enemy.Monster is not (InfestedPrism or FuzzyWurmCrawler or SoulNexus))
                    return false;
            }
            if (state.Enemies.Count > 0 && state.Enemies.Any(static enemy =>
                    enemy.Monster is DecimillipedeSegmentFront or DecimillipedeSegmentMiddle
                        or DecimillipedeSegmentBack))
            {
                return false;
            }
        }
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new UnsupportedImportException(ImportReason.Unrepresentable,
                "玩家没有 PlayerCombatState");
        if (playerState.OrbQueue.Orbs.Count != 0)
            return false;
        bool copiesExhaustedSkills = player.Relics.Any(static relic =>
            !relic.IsMelted && relic is BurningSticks);
        foreach (CardModel card in playerState.AllCards)
        {
            if (!HasCertifiedRemainingAttachments(card))
                return false;
            bool safe = RemainingSafeCardTypes.Names.Contains(card.GetType().FullName ?? "")
                || nativeLouse && card is SeekingEdge or Glimmer or Arsenal or Comet
                    or RefineBlade or GuidingStar or SecretWeapon;
            bool extra = !copiesExhaustedSkills && card is BundleOfJoy or Entropy;
            if (!safe && !extra)
                return false;
        }
        foreach (RelicModel relic in player.Relics)
        {
            bool safe = relic is DivineRight or Girya or OldCoin or VitruvianMinion
                or RingOfTheSnake or PotionBelt or Shovel or IceCream;
            bool louseSafe = nativeLouse && relic is Cauldron or BurningSticks or Kusarigama;
            if (!safe && !louseSafe)
                return false;
        }
        return true;
    }

    private static bool ComponentInitialCard(CardModel card)
        => ComponentInitialCardTypes.Names.Contains(card.GetType().FullName ?? "")
            && HasCertifiedRemainingAttachments(card)
            && !GrowthTargets.HasGrowthTarget(card);

    private static bool HasCertifiedRemainingAttachments(CardModel card)
        => (card.Enchantment is null
                || card.Enchantment is Slither or Inky or EnchantmentInstinct)
            && (card.Affliction is null || card.Affliction is Tainted);

    private static bool ComponentRootSource(AbstractModel source)
    {
        bool ok = source switch
        {
            CardModel card => ComponentInitialCard(card),
            RelicModel relic => Registry.TryRelic(relic.Id.Entry, out ushort relicId)
                && !UnsafeRelics[relicId],
            PowerModel power => Registry.TryPower(power.Id.Entry, out ushort powerId)
                && !UnsafePowers[powerId],
            MonsterModel monster => Registry.TryMonster(monster.Id.Entry, out ushort monsterId)
                && !UnsafeMonsters[monsterId],
            PotionModel potion => Registry.TryPotion(potion.Id.Entry, out ushort potionId)
                && !UnsafePotions[potionId],
            CccComboModel or DebufferModel or MultiplayerScalingModel => true,
            _ => false,
        };
        return ok;
    }

    private static bool ComponentUpperBound(CombatState state, Player player)
    {
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new UnsupportedImportException(ImportReason.Unrepresentable,
                "玩家没有 PlayerCombatState");
        if (state.Enemies.Count == 0)
            return false;
        bool[] unsafeMonster = UnsafeMonsters;
        foreach (Creature enemy in state.Enemies)
        {
            if (enemy.Monster is not { } monster
                || !Registry.TryMonster(monster.Id.Entry, out ushort monsterId)
                || unsafeMonster[monsterId])
            {
                return false;
            }
        }
        bool[] unsafeRelic = UnsafeRelics;
        foreach (RelicModel relic in player.Relics)
        {
            if (!Registry.TryRelic(relic.Id.Entry, out ushort relicId) || unsafeRelic[relicId])
                return false;
        }
        bool[] unsafeCard = UnsafeCards;
        foreach (CardModel card in playerState.AllCards)
        {
            if (!Registry.TryCard(card.Id.Entry, out ushort cardId) || unsafeCard[cardId])
                return false;
        }
        foreach (Creature ally in state.Allies)
        {
            if (ally.Player is null && ally.Monster is not Osty)
                return false;
        }
        foreach (var orb in playerState.OrbQueue.Orbs)
        {
            if (orb is not (MegaCrit.Sts2.Core.Models.Orbs.LightningOrb
                or MegaCrit.Sts2.Core.Models.Orbs.FrostOrb
                or MegaCrit.Sts2.Core.Models.Orbs.DarkOrb
                or MegaCrit.Sts2.Core.Models.Orbs.PlasmaOrb
                or MegaCrit.Sts2.Core.Models.Orbs.GlassOrb))
            {
                return false;
            }
        }

        bool[] unsafePower = UnsafePowers;
        bool[] regenPower = RegenPowers;
        bool[] reattachPower = ReattachPowers;
        bool[] reattachSafe = ReattachSafeMonsters;
        int regen = 0;
        foreach (PowerModel power in EffectivePowers(state))
        {
            if (!Registry.TryPower(power.Id.Entry, out ushort powerId) || unsafePower[powerId])
                return false;
            if (regenPower[powerId] && ReferenceEquals(power.Owner, player.Creature))
                regen += Math.Max(0, power.Amount);
            if (reattachPower[powerId])
            {
                bool safe = power.Owner.Player is null
                    && state.Enemies.Contains(power.Owner)
                    && power.Owner.Monster is { } monster
                    && Registry.TryMonster(monster.Id.Entry, out ushort monsterId)
                    && reattachSafe[monsterId];
                if (!safe)
                    return false;
            }
        }

        bool[] unsafePotion = UnsafePotions;
        bool[] regenPotion = RegenPotions;
        int[] regenAmount = HealingBoundTables.RegenPotionAmount();
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(slot);
            if (potion is null)
                continue;
            if (!Registry.TryPotion(potion.Id.Entry, out ushort potionId) || unsafePotion[potionId])
                return false;
            if (regenPotion[potionId])
                regen += regenAmount[potionId];
        }
        return RegenerationHealingUpperBound(regen, 0) != int.MaxValue;
    }

    /// <summary><c>StrategicHpRecoveryBound.CanUseKnownNativeHealingPolicy</c> for the native-only root.</summary>
    private static bool KnownNativeCertificate(CombatState state, Player player)
    {
        if (state.Players.Count != 1 || state.Modifiers.Count != 0)
            return false;
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new UnsupportedImportException(ImportReason.Unrepresentable,
                "玩家没有 PlayerCombatState");
        if (!NativeAccess.IsOriginal(player.Character))
            return false;
        foreach (AbstractModel listener in RootHookListeners(state))
        {
            if (!NativeAccess.IsOriginal(listener))
                return false;
        }
        foreach (Creature enemy in state.Enemies)
        {
            if (enemy.Monster is not { } monster || !NativeAccess.IsOriginal(monster))
                return false;
        }
        foreach (RelicModel relic in player.Relics)
        {
            if (!NativeAccess.IsOriginal(relic))
                return false;
        }
        foreach (PowerModel power in EffectivePowers(state))
        {
            if (!NativeAccess.IsOriginal(power))
                return false;
        }
        foreach (var orb in playerState.OrbQueue.Orbs)
        {
            if (!NativeAccess.IsOriginal(orb))
                return false;
        }
        foreach (CardModel card in playerState.AllCards)
        {
            if (!NativeAccess.IsOriginal(card)
                || card.Enchantment is { } enchantment && !NativeAccess.IsOriginal(enchantment)
                || card.Affliction is { } affliction && !NativeAccess.IsOriginal(affliction))
            {
                return false;
            }
        }
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (player.GetPotionAtSlotIndex(slot) is { } potion
                && !NativeAccess.IsOriginal(potion))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary><c>StrategicHpRecoveryBound.Assess</c> over the native root.</summary>
    private static bool HasOnlyPostCombatHealing(CombatState state, Player player)
    {
        if (player.Character is not (Ironclad or Necrobinder or Silent))
            return false;
        foreach (Creature enemy in state.Enemies)
        {
            if (enemy.Monster is not { } monster || !NativeAccess.IsOriginal(monster))
                return false;
        }
        if (state.Modifiers.Count != 0)
            return false;
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (player.GetPotionAtSlotIndex(slot) is { })
                return false;
        }
        foreach (RelicModel relic in player.Relics)
        {
            if (relic.IsMelted)
                continue;
            if (relic is not (BurningBlood or BlackBlood or BoundPhylactery or RingOfTheSnake))
                return false;
        }
        foreach (PowerModel power in EffectivePowers(state))
        {
            if (ReferenceEquals(power.Owner, player.Creature)
                && power is not (NeurosurgePower or DoomPower))
            {
                return false;
            }
        }
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new UnsupportedImportException(ImportReason.Unrepresentable,
                "玩家没有 PlayerCombatState");
        foreach (CardModel card in playerState.AllCards)
        {
            if (card.Enchantment is not null || card.Affliction is not null)
                return false;
            if (!OnlyPostCombatHealingCardTypes.Contains(card.GetType().FullName ?? ""))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The engine's root hook listeners exclude cards, afflictions, enchantments and orbs; those are
    /// classified through their owning card. Keeping the same filter is required for the certificate.
    /// </summary>
    private static IEnumerable<AbstractModel> RootHookListeners(CombatState state)
    {
        foreach (AbstractModel listener in state.IterateHookListeners())
        {
            if (listener is CardModel or AfflictionModel or EnchantmentModel or OrbModel)
                continue;
            yield return listener;
        }
    }

    private static IEnumerable<PowerModel> EffectivePowers(CombatState state)
    {
        foreach (AbstractModel listener in RootHookListeners(state))
        {
            if (listener is PowerModel { Amount: not 0 } power)
                yield return power;
        }
    }

    private static bool HasRegenPotion(Player player)
    {
        bool[] regenPotion = RegenPotions;
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(slot);
            if (potion is not null
                && Registry.TryPotion(potion.Id.Entry, out ushort id)
                && regenPotion[id])
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasSwipePower(CombatState state)
    {
        bool[] swipe = SwipePowers;
        foreach (PowerModel power in EffectivePowers(state))
        {
            if (Registry.TryPower(power.Id.Entry, out ushort id) && swipe[id])
                return true;
        }
        return false;
    }

    private static int RegenerationHealingUpperBound(long regen, int postCombatHeal)
    {
        if (regen >= 65_536)
            return int.MaxValue;
        long amount = Math.Max(0, regen);
        return (int)Math.Min(int.MaxValue,
            amount * (amount + 1) / 2 + Math.Max(0, postCombatHeal));
    }
}
