namespace Sts2Solver.Simulation.Combat;

/// <summary>
/// K0 contract for <see cref="RulesCodec"/>. It builds a synthetic rule table whose every
/// constructor input has a distinctive value, then mutates one field at a time and requires the
/// encoded bytes to change. A field added to <see cref="Rules"/> without a codec entry, or a
/// codec that silently drops a field, therefore fails the contract instead of producing a lossy
/// golden file.
/// </summary>
public static class RulesCodecContract
{
    public static void Run()
    {
        if (Rules.SchemaFieldCount != 32 || RulesCodec.EncodedFieldCount != 32)
            throw new InvalidOperationException(
                $"Rules K0 字段数不一致：owner={Rules.SchemaFieldCount} codec={RulesCodec.EncodedFieldCount}。");
        Spec spec = Spec.Create();
        Rules baseline = spec.Build();
        byte[] baselineBytes = RulesCodec.Encode(baseline);
        if (!baselineBytes.AsSpan().SequenceEqual(RulesCodec.Encode(spec.Build())))
            throw new InvalidOperationException("同一份 Rules 两次编码字节不同。");
        Rules decoded = RulesCodec.Decode(baselineBytes);
        byte[] roundTrip = RulesCodec.Encode(decoded);
        if (!baselineBytes.AsSpan().SequenceEqual(roundTrip))
            throw new InvalidOperationException("Rules decode 后再 encode 字节不同。");
        CheckDecoded(decoded);

        void Field(string name, Action<Spec> mutate)
        {
            Spec variant = spec.Clone();
            mutate(variant);
            byte[] bytes = RulesCodec.Encode(variant.Build());
            if (baselineBytes.AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException($"Rules K0：字段 {name} 没有进入编码字节。");
        }

        Field("cards", value => value.CardType = CardType.Power);
        Field("powerVars", value => value.PowerVar = 9.5m);
        Field("potions", value => value.PotionCost = 42);
        Field("relicVars", value => value.RelicVar = -3.25m);
        Field("monsters", value => value.MonsterNodeId = "CHANGED");
        Field("spawnRules", value => value.SpawnValue = 1);
        Field("cardPools", value => value.PoolKey = "POOL-B");
        Field("potionPool", value => value.PotionPool = [11, 12]);
        Field("outOfCombatPotionPool", value => value.OutOfCombatPotionPool = [13]);
        Field("rootPotions", value => value.RootPotions = [1, 0, 0, 0, 0]);
        Field("powerDeathHistoryRisk", value => value.PowerRisk = false);
        Field("hasEncounter", value => value.HasEncounter = false);
        Field("encounterSlots", value => value.EncounterSlots = ["slot-x"]);
        Field("rootEnemyCount", value => value.RootEnemyCount = 3);
        Field("baseMaxEnergy", value => value.BaseMaxEnergy = 4);
        Field("playerSlotIndex", value => value.PlayerSlotIndex = 1);
        Field("baseOrbSlotCount", value => value.BaseOrbSlotCount = 2);
        Field("rootSwordSageAmount", value => value.RootSwordSageAmount = 8);
        Field("unsettlingLampRelicId", value => value.UnsettlingLampRelicId = 12);
        Field("hasIdentityContent", value => value.HasIdentityContent = false);
        Field("hasShuffleOrderEnchantment", value => value.HasShuffleOrderEnchantment = true);
        Field("hasBeforeFlushEnchantment", value => value.HasBeforeFlushEnchantment = true);
        Field("hasTheftContent", value => value.HasTheftContent = true);
        Field("hasDecimalCardVars", value => value.HasDecimalCardVars = true);
        Field("hasInfiniteHpDisplayMonster", value => value.HasInfiniteHpDisplayMonster = true);
        Field("hasPaelsLegion", value => value.HasPaelsLegion = true);
        Field("furCoatSpawnHpOne", value => value.FurCoatSpawnHpOne = true);
        Field("rootLostHpMask", value => value.RootLostHpMask = 0x1234);
        Field("hasHistoryCourse", value => value.HasHistoryCourse = true);
        Field("totalFloor", value => value.TotalFloor = 23);
        Field("dominion.kind", value => value.Dominion = value.Dominion with { Kind = DominionKind.WaterfallGiant });
        Field("dominion.power", value => value.Dominion = value.Dominion with { SpawnPower = 8 });
        Field("dominion.amount", value => value.Dominion = value.Dominion with { SpawnAmount = 9 });
        Field("mode", value => value.Mode = CombatMode.Blitz);

        // Bound the size of the contract artifact: every table is a handful of entries.
        if (baselineBytes.Length > 1 << 20)
            throw new InvalidOperationException($"Rules K0 样例异常大：{baselineBytes.Length} 字节。");
    }

    private static void CheckDecoded(Rules rules)
    {
        if (rules.BaseMaxEnergy != 3 || rules.PlayerSlotIndex != 2 || rules.BaseOrbSlotCount != 1
            || rules.RootEnemyCount != 1 || rules.EncounterSlots.Count != 2
            || rules.PotionPool.Count != 2 || rules.OutOfCombatPotionPool.Count != 1
            || rules.CardPool("POOL-A").Cards.Length != 2 || rules.MonsterRule(0).Nodes.Length != 1
            || rules.MonsterRule(0).Nodes[0].NativeId != "MOVE")
            throw new InvalidOperationException("Rules K0 解码后的标量或表内容不符。");
        if (rules.TotalFloor != 17 || rules.Mode != CombatMode.Dread
            || rules.Dominion != new DominionRules(DominionKind.TestSubject, 4, 3))
            throw new InvalidOperationException("Rules K0 解码后的模式、领域或楼层不符。");
        CardStats card = rules.Card(0, 0);
        if (card.Type != CardType.Attack || card.EnergyCost != 1 || card.Vars[0] != 6.5m)
            throw new InvalidOperationException("Rules K0 解码后的卡牌数值不符。");
        PotionStats potion = rules.Potion(1);
        if (potion.StrategicCost != 1 || potion.Vars[0] != 2.5m || potion.VarProps[0] != ValueProps.Unblockable)
            throw new InvalidOperationException("Rules K0 解码后的药水数值不符。");
        if (!rules.PowerRecordsDeathHistoryRisk(1) || rules.PowerVar(0, 0) != 1.25m
            || rules.RelicVar(1, 0) != 4.75m)
            throw new InvalidOperationException("Rules K0 解码后的 Power/遗物数值不符。");
    }

    private sealed class Spec
    {
        public CardType CardType = CardType.Attack;
        public decimal PowerVar = 1.25m;
        public int PotionCost = 1;
        public decimal RelicVar = 4.75m;
        public string MonsterNodeId = "MOVE";
        public int SpawnValue;
        public string PoolKey = "POOL-A";
        public ushort[] PotionPool = [1, 2];
        public ushort[] OutOfCombatPotionPool = [3];
        public ushort[] RootPotions = [0, 2, 0, 0, 3];
        public bool PowerRisk = true;
        public bool HasEncounter = true;
        public string[] EncounterSlots = ["slot-a", "slot-b"];
        public int RootEnemyCount = 1;
        public int BaseMaxEnergy = 3;
        public int PlayerSlotIndex = 2;
        public int BaseOrbSlotCount = 1;
        public int RootSwordSageAmount = 2;
        public ushort UnsettlingLampRelicId = 5;
        public bool HasIdentityContent = true;
        public bool HasShuffleOrderEnchantment;
        public bool HasBeforeFlushEnchantment;
        public bool HasTheftContent;
        public bool HasDecimalCardVars;
        public bool HasInfiniteHpDisplayMonster;
        public bool HasPaelsLegion;
        public bool FurCoatSpawnHpOne;
        public ushort RootLostHpMask = 0x0102;
        public bool HasHistoryCourse;
        public int TotalFloor = 17;
        public DominionRules Dominion = new(DominionKind.TestSubject, 4, 3);
        public CombatMode Mode = CombatMode.Dread;

        public static Spec Create() => new();

        public Spec Clone() => (Spec)MemberwiseClone();

        public Rules Build()
        {
            CardStats stats = new()
            {
                Type = CardType,
                Target = TargetType.AnyEnemy,
                Rarity = CardRarity.Common,
                Pool = TransformPool.Character,
                EnergyCost = 1,
                CostsX = false,
                StarCost = 0,
                StarCostsX = false,
                Keywords = Keywords.Exhaust,
                HasDynamicType = false,
                MaxUpgradeLevel = 2,
                Tags = CardTag.Strike,
                IsSoul = false,
                IsColorless = false,
                PlaysRecordRisk = false,
                Vars = [6.5m, 3m],
            };
            CardStats?[][] cards =
            [
                [stats, null, null],
                [null, null, stats],
            ];
            decimal[][] powerVars = [[PowerVar], [2m]];
            PotionStats potion = new()
            {
                Target = TargetType.Self,
                Rarity = PotionRarity.Uncommon,
                StrategicCost = PotionCost,
                Vars = [2.5m],
                VarProps = [ValueProps.Unblockable],
            };
            decimal[][] relicVars = [[1m], [RelicVar]];
            MonsterNode node = new()
            {
                NativeId = MonsterNodeId,
                Kind = MonsterNodeKind.Move,
                Attacks = [new MonsterAttack(5, 1)],
            };
            MonsterRules monster = new()
            {
                MonsterId = 7,
                Nodes = [node],
                InitialNode = 0,
                StunnedNode = 1,
                InitialHpMin = 10,
                InitialHpMax = 20,
                StaticInts = [1, 2],
                RootPowerAmounts = [3],
                InitialAiRegisters = [4],
            };
            return new Rules(
                cards, powerVars, [null, potion], relicVars, [monster, null],
                new Dictionary<ushort, int> { [7] = SpawnValue },
                new Dictionary<string, CardPool>(StringComparer.Ordinal)
                {
                    [PoolKey] = new CardPool { Cards = [1, 2] },
                },
                PotionPool, OutOfCombatPotionPool, RootPotions, [false, PowerRisk],
                HasEncounter, EncounterSlots, RootEnemyCount, BaseMaxEnergy, PlayerSlotIndex,
                BaseOrbSlotCount, RootSwordSageAmount, UnsettlingLampRelicId, HasIdentityContent,
                HasShuffleOrderEnchantment, HasBeforeFlushEnchantment, HasTheftContent,
                HasDecimalCardVars, HasInfiniteHpDisplayMonster, HasPaelsLegion, FurCoatSpawnHpOne,
                RootLostHpMask, HasHistoryCourse, TotalFloor, Dominion, Mode);
        }
    }
}
