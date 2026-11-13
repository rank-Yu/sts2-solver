using Sts2Solver.Content.Monsters.Act1;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver.KernelChecks;

/// <summary>
/// Card-play state contracts found by the FS8 action-by-action comparison against the game:
/// the result-pile position is passed through listeners that leave the location alone (native
/// <c>CardLocation</c> is a struct each modifier edits in place), a replay's BeforeCardPlayed sees the
/// live played card (native <c>CardPlay.Card</c> is the mutable CardModel), and a kill inside an
/// attack settles its death hooks before the rest of the card reads the board.
/// </summary>
internal static class CardPlayStateChecks
{
    private static void Assert(bool value, string message)
    {
        if (!value)
            throw new InvalidDataException("card-play-state-checks: " + message);
    }

    internal static void Check()
    {
        CheckFeralKeepsShiningStrikeOnDrawTop();
        CheckFeralReturnsZeroCostShiningStrike();
        CheckJugglingCloneCopiesDisabledGlam();
        CheckGeneratedCardsKeepAttachmentIdentity();
        CheckRightHandKeepsDeckCopyOrder();
        CheckThrashReadsStrengthReturnedByKill();
    }

    /// <summary>
    /// Shining Strike's own result location is the top of the draw pile. Feral does not apply to a
    /// 1-energy play, so the card must stay on top and Feral's counter must not move.
    /// </summary>
    private static void CheckFeralKeepsShiningStrikeOnDrawTop()
    {
        (Rules rules, Battle battle, ushort shining, ushort strike, ushort feral) = FeralBattle(shiningCost: 1);
        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);

        Assert(battle.HandCount == 0, "a 1-energy Shining Strike must leave the hand");
        Assert(battle.DrawCount == 2, "Shining Strike must go back to the draw pile");
        Assert(Piles.At(ref battle, Pile.Draw, 0).Id == shining
               && Piles.At(ref battle, Pile.Draw, 1).Id == strike,
            "Shining Strike must keep its top-of-draw position when Feral leaves the location alone");
        Assert(battle.Powers[FeralIndex(battle, feral)].Aux0 == 0,
            "Feral must not count a play it did not return");
    }

    /// <summary>A 0-energy Shining Strike is returned to the hand by Feral, which counts it.</summary>
    private static void CheckFeralReturnsZeroCostShiningStrike()
    {
        (Rules rules, Battle battle, ushort shining, _, ushort feral) = FeralBattle(shiningCost: 0);
        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);

        Assert(battle.HandCount == 1 && Piles.At(ref battle, Pile.Hand, 0).Id == shining,
            "Feral must return a 0-energy attack to the hand");
        Assert(battle.DrawCount == 1, "the returned card must not reach the draw pile");
        Assert(battle.Powers[FeralIndex(battle, feral)].Aux0 == 1, "Feral must count the returned play");
    }

    /// <summary>
    /// Juggling clones the third attack at its BeforeCardPlayed. A Glam card's replay is that third
    /// start here; Glam disabled itself in the first play's AfterCardPlayed, so the clone copies the
    /// used, disabled enchantment.
    /// </summary>
    private static void CheckJugglingCloneCopiesDisabledGlam()
    {
        Assert(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "missing Strike");
        Assert(Registry.TryPower("JUGGLING_POWER", out ushort juggling), "missing JugglingPower");
        Assert(Registry.TryEnchantment("GLAM", out ushort glam), "missing Glam");

        Rules rules = CardRules((strike, Attack(0, 6m)));
        Battle battle = PlayBattle(enemyHp: [50]);
        battle.PowerCount = 1;
        battle.Powers[0] = new Power { Id = juggling, Owner = Limits.PlayerOwner, Amount = 1, Aux0 = 1 };
        Card card = Kernel.CreateCard(rules, strike);
        card.EnchantmentId = checked((byte)(glam + 1));
        card.EnchantmentAmount = 1;
        Piles.Append(ref battle, Pile.Hand, card);

        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);

        Assert(battle.Powers[0].Aux0 == 3, "the Glam replay must be the third attack start");
        Assert(battle.HandCount == 1, "Juggling must add one clone to the hand");
        ref Card clone = ref Piles.At(ref battle, Pile.Hand, 0);
        Assert(clone.IsClone && clone.EnchantmentId == glam + 1, "the hand card must be the Glam clone");
        Assert(clone.EnchantmentPrivate == 1 && clone.EnchantmentStatus == 1,
            "the clone must copy the Glam state the first play already used and disabled");
    }

    /// <summary>Calamity inserts before the play pile; equal exhausted cards keep their own attachment.</summary>
    private static void CheckGeneratedCardsKeepAttachmentIdentity()
    {
        Assert(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "missing Strike");
        Assert(Registry.TryPower("CALAMITY_POWER", out ushort calamity), "missing Calamity");
        foreach (string attachment in new[] { "GLAM", "VIGOROUS" })
        {
            Assert(Registry.TryEnchantment(attachment, out ushort enchantment), "missing attachment");
            Rules rules = CardRules((strike, Attack(0, 6m)));
            Battle battle = PlayBattle(enemyHp: [100]);
            battle.RngStream(RngStream.CombatCardGeneration) = RngState.FromSerializable(0, 1, 2, 3, 4);
            battle.PowerCount = 1;
            battle.Powers[0] = new Power { Id = calamity, Owner = Limits.PlayerOwner, Amount = 1 };
            Card card = Kernel.CreateCard(rules, strike);
            card.EnchantmentId = checked((byte)(enchantment + 1));
            card.EnchantmentAmount = 1;
            Piles.Append(ref battle, Pile.Hand, card);
            Piles.Append(ref battle, Pile.Exhaust, card);
            Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);
            Assert(battle.HandCount > 0, "Calamity must insert before the play pile");
            Assert(Piles.At(ref battle, Pile.Discard, 0).EnchantmentStatus == 1,
                attachment + " must disable the played copy");
            Assert(Piles.At(ref battle, Pile.Exhaust, 0).EnchantmentStatus == 0,
                attachment + " must leave the exhausted copy active");
        }
    }

    private static void CheckRightHandKeepsDeckCopyOrder()
    {
        Assert(Registry.TryCard("BASH", out ushort bash), "missing Bash");
        Assert(Registry.TryCard("RIGHT_HAND_HAND", out ushort rightHand), "missing Right Hand Hand");
        Rules rules = CardRules((bash, Attack(2, 8m, 2m)), (rightHand, Attack(0, 4m, 2m)));
        Battle battle = PlayBattle(enemyHp: [100]);
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, bash));
        foreach (bool fromDeck in new[] { false, true, false })
        {
            Card card = Kernel.CreateCard(rules, rightHand);
            if (fromDeck)
                card.Flags |= CardFlags.DeckCard;
            Piles.Append(ref battle, Pile.Discard, card);
        }
        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);
        Assert(battle.HandCount == 3, "all three Right Hand Hand copies must return");
        for (int index = 0; index < 3; index++)
            Assert(((Piles.At(ref battle, Pile.Hand, index).Flags & CardFlags.DeckCard) != 0) == (index == 1),
                "original and generated copies must keep native return order");
    }

    /// <summary>
    /// Thrash's two hits kill the Possess Strength holder; its AfterDeath returns the stolen Strength
    /// inside the attack, so the exhausted Strike is read with Strength 0 (6), not -3 (3).
    /// </summary>
    private static void CheckThrashReadsStrengthReturnedByKill()
    {
        Assert(Registry.TryCard("THRASH", out ushort thrash), "missing Thrash");
        Assert(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "missing Strike");
        Assert(Registry.TryPower("POSSESS_STRENGTH_POWER", out ushort possess), "missing PossessStrengthPower");
        Assert(Registry.TryPower("STRENGTH_POWER", out ushort strength), "missing StrengthPower");

        Rules rules = CardRules((thrash, Attack(0, 4m)), (strike, Attack(1, 6m)));
        Battle battle = PlayBattle(enemyHp: [1, 50]);
        battle.PowerCount = 2;
        battle.Powers[0] = new Power { Id = strength, Owner = Limits.PlayerOwner, Amount = -3 };
        battle.Powers[1] = new Power { Id = possess, Owner = Kernel.EnemyOwner(0), Amount = 1, Aux0 = -3 };
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, thrash));
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, strike));

        Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);

        Assert(!battle.Creature(Kernel.EnemyOwner(0)).IsAlive, "the hits must kill the Possess Strength holder");
        Assert(Kernel.GetAmount(battle, Limits.PlayerOwner, strength) == 0, "the stolen Strength must return");
        Assert(battle.ExhaustCount == 1 && Piles.At(ref battle, Pile.Exhaust, 0).Id == strike,
            "Thrash must exhaust the Strike");
        Assert(battle.DiscardCount == 1, "Thrash must go to the discard pile");
        ref Card played = ref Piles.At(ref battle, Pile.Discard, 0);
        Assert(played.Id == thrash && DecimalVars.Delta(in battle, in played, 0) == 6m,
            "Thrash must add the Strike damage computed after the Strength came back");
    }

    private static (Rules Rules, Battle Battle, ushort Shining, ushort Strike, ushort Feral) FeralBattle(
        int shiningCost)
    {
        Assert(Registry.TryCard("SHINING_STRIKE", out ushort shining), "missing Shining Strike");
        Assert(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "missing Strike");
        Assert(Registry.TryPower("FERAL_POWER", out ushort feral), "missing FeralPower");
        Rules rules = CardRules((shining, Attack(shiningCost, 8m, 2m)), (strike, Attack(1, 6m)));
        Battle battle = PlayBattle(enemyHp: [50]);
        battle.PowerCount = 1;
        battle.Powers[0] = new Power { Id = feral, Owner = Limits.PlayerOwner, Amount = 1 };
        Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, shining));
        Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, strike));
        return (rules, battle, shining, strike, feral);
    }

    private static int FeralIndex(in Battle battle, ushort feral)
    {
        int index = Kernel.FindPower(battle, Limits.PlayerOwner, feral);
        Assert(index >= 0, "Feral must still be present");
        return index;
    }

    private static CardStats Attack(int cost, params decimal[] vars) => new()
    {
        Type = CardType.Attack,
        Target = TargetType.AnyEnemy,
        Rarity = CardRarity.Common,
        EnergyCost = cost,
        CostsX = false,
        StarCost = -1,
        StarCostsX = false,
        Keywords = Keywords.None,
        MaxUpgradeLevel = 0,
        Vars = vars,
    };

    private static Rules CardRules(params (ushort Id, CardStats Stats)[] cards)
    {
        RulesDraft builder = new()
        {
            RootEnemyCount = 2,
            HasEncounter = true,
            EncounterSlots = ["first", "second"],
            BaseMaxEnergy = 3,
            HasDecimalCardVars = true,
            HasTheftContent = true,
        };
        builder.SetMonster(0, NibbitRules(Registry.MonsterId<Nibbit>()));
        foreach ((ushort id, CardStats stats) in cards)
            builder.SetCard(id, 0, stats);
        builder.SetCardPool(CardPools.CharacterAttacks, cards.Select(card => card.Id).ToArray());
        return builder.Build();
    }

    private static Battle PlayBattle(int[] enemyHp)
    {
        Battle battle = default;
        battle.Round = 1;
        battle.PlayerTurn = 1;
        battle.Energy = 3;
        battle.MaxHandSize = 10;
        battle.Flags = BattleFlags.PlayerHooksActive;
        battle.Side = Side.Player;
        battle.Phase = PlayerPhase.Play;
        battle.Player = new Creature { CurrentHp = 50, MaxHp = 50, Flags = CreatureFlags.Present };
        battle.RngStream(RngStream.Niche) = RngState.FromSerializable(0, 1, 2, 3, 4);
        battle.EnemyCount = checked((byte)enemyHp.Length);
        battle.RosterCount = checked((byte)enemyHp.Length);
        for (int index = 0; index < enemyHp.Length; index++)
        {
            battle.Roster[index] = checked((byte)index);
            battle.Enemies[index] = new Enemy
            {
                Creature = new Creature
                {
                    CurrentHp = enemyHp[index],
                    MaxHp = enemyHp[index],
                    Flags = CreatureFlags.Present,
                },
                MonsterId = Registry.MonsterId<Nibbit>(),
                Rules = 0,
                Slot = Kernel.NoSlot,
                CombatId = checked((byte)(index + 1)),
                Move = 0,
                ForcedMove = byte.MaxValue,
                Flags = EnemyFlags.InCombat,
            };
        }
        return battle;
    }

    private static MonsterRules NibbitRules(ushort nibbit) => new()
    {
        MonsterId = nibbit,
        Nodes =
        [
            new MonsterNode
            {
                NativeId = "BUTT_MOVE",
                Kind = MonsterNodeKind.Move,
                FollowUp = 0,
                Attacks = [new MonsterAttack(12, 1)],
            },
            new MonsterNode
            {
                NativeId = "STUNNED",
                Kind = MonsterNodeKind.Move,
                MustPerformOnceBeforeTransitioning = true,
            },
            new MonsterNode
            {
                NativeId = "REVIVE_MOVE",
                Kind = MonsterNodeKind.Move,
                MustPerformOnceBeforeTransitioning = true,
            },
        ],
        InitialNode = 0,
        PendingInitialMoveNode = 0,
        StunnedNode = 1,
        ReviveNode = 2,
        InitialHpMin = 17,
        InitialHpMax = 21,
        StaticInts = [],
        InitialAiRegisters = [2, 0],
    };
}
