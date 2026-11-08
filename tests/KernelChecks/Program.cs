using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sts2Solver.Simulation.Combat;
using Sts2Solver.KernelChecks;
using Sts2Solver.Content.Afflictions;
using Sts2Solver.Content.Cards.Status;
using Sts2Solver.Content.Enchantments;
using Sts2Solver.Content.Powers;
using Sts2Solver.Content.Relics;

// Pure Sts2Solver kernel contracts (K0): no game assembly; Sts2Solver.Core is the only production
// content assembly. Differential equivalence with the existing engine is checked separately by the
// offline harness probe.
Registry.RegisterTestAssembly(Assembly.GetExecutingAssembly());

Check(Unsafe.SizeOf<Card>() == 92, $"Card 应为 92 字节，实际 {Unsafe.SizeOf<Card>()}");
Check(Unsafe.SizeOf<Enemy>() == 64, $"Enemy 应为 64 字节，实际 {Unsafe.SizeOf<Enemy>()}");
Check(Unsafe.SizeOf<Power>() == 24, $"Power 应为 24 字节，实际 {Unsafe.SizeOf<Power>()}");
Check(Unsafe.SizeOf<Orb>() == 8, $"Orb 应为 8 字节，实际 {Unsafe.SizeOf<Orb>()}");
Check(Unsafe.SizeOf<RngState>() == 40, $"RngState 应为 40 字节，实际 {Unsafe.SizeOf<RngState>()}");
Check(Unsafe.SizeOf<Battle>() == Battle.ExpectedSize,
    $"Battle 应为 {Battle.ExpectedSize} 字节，实际 {Unsafe.SizeOf<Battle>()}");
Check(Unsafe.SizeOf<Battle>() <= 65535,
    $"Battle 大小 {Unsafe.SizeOf<Battle>()} 超过 .NET 数组元素上限 65535；"
    + "这样 Battle[]/List<Battle> 无法创建，Marshal.OffsetOf 也会失败。");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Cards))
        == Battle.HeaderSize,
    $"Cards 偏移应为 {Battle.HeaderSize}，实际 {System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Cards))}");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.HistoryCourseCard))
        + Unsafe.SizeOf<Card>() == Battle.HeaderSize,
    "HistoryCourseCard 应紧挨在 Cards 之前");
Check(Battle.HeaderSize + Limits.Cards * Unsafe.SizeOf<Card>() == 62740,
    "满牌打包前缀应为 62740 字节");
Check(Battle.ExpectedSize == 62744, $"Battle 满容量大小应为 62744，实际 {Battle.ExpectedSize}");
Check(Battle.ExpectedSize <= 65535, "Battle 结构体应不超过 .NET 数组元素上限");
// Header anchors: every field starts where the preceding explicit field size ends, so the widened
// pile counts and the alignment byte introduce no compiler-inserted padding.
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.HandCount)) == 32,
    "HandCount 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.MaxHandSize)) == 42,
    "MaxHandSize 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.DetachedCards)) == 52,
    "DetachedCards 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.DecimalVars)) == 328,
    "DecimalVars 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Roster)) == 392,
    "Roster 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Potions)) == 404,
    "Potions 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Player)) == 416,
    "Player 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Counters)) == 448,
    "Counters 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Rng)) == 640,
    "Rng 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Enemies)) == 1000,
    "Enemies 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Powers)) == 1768,
    "Powers 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Relics)) == 3304,
    "Relics 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.Orbs)) == 3688,
    "Orbs 偏移");
Check((int)System.Runtime.InteropServices.Marshal.OffsetOf<Battle>(nameof(Battle.HistoryCourseCard)) == 3768,
    "HistoryCourseCard 偏移");
string? execCoverageError = ExecCoverage.Validate();
Check(execCoverageError == null, $"Exec 字段覆盖合同失败：{execCoverageError}");

CheckRng();
CheckGrowthRewards();
Rules rules = SliceRules();
SearchChecks.Check();
IncumbentAndLoopChecks.Check(rules);
GoldHookChecks.Check();
PowerEndingChecks.Check();
CardPlayStateChecks.Check();
CheckHookDeactivation(rules);
bool lateRegistrationThrew = false;
try
{
    Registry.RegisterTestAssembly(Assembly.GetExecutingAssembly());
}
catch (InvalidOperationException)
{
    lateRegistrationThrew = true;
}
Check(lateRegistrationThrew, "Registry 构建后仍接受测试程序集登记。");
CheckIntroSort(rules);
CheckCapacity();
CheckPiles();
PackedCardsChecks.Check(rules);
Cards640Checks.Check(rules);
CheckMonsterForcedMoves(rules);
CheckKm2Kernel(rules);
CheckMonsterSpawn(SpawnRules());
CheckGenerationAndAutoPlay();
CheckKg2Hooks(rules);
CheckA3Kernel(rules);
CheckKaAttachments();
CheckP16Kernel(rules);
CheckSkKernel();
CheckScrapeDrawnIdentity();
CheckLsKernel();
CheckNbKernel();
CheckMbKernel(rules);
CheckTwKernel();
CheckDfKernel(rules);
CheckRgKernel(rules);
CheckM2cSemantics(rules);
CheckHandLimit(rules);
CheckLrKernel(rules);
CheckTsKernel(rules);
CheckIdKernel(rules);
CheckCxKernel(rules);
CheckDecimalVars(rules);
CheckDx3Kernel(rules);
CheckPrKernel();
CheckEcKernel(rules);
CheckDx5Kernel(rules);
CheckCardPoolContracts();
CheckStatusCardContracts();
TurnEndSelfDamageChecks.Check();
CheckPrerootGeneratedCardContracts();
var benchmark = Benchmark(rules);

Console.WriteLine(JsonSerializer.Serialize(new
{
    contracts = "Passed",
    battleBytes = Unsafe.SizeOf<Battle>(),
    runtime = Environment.Version.ToString(),
    benchmark,
}, new JsonSerializerOptions { WriteIndented = true }));

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

// Reference copy of native MegaRandom + Rng (decompiled sts2 0.111.0) to pin the Sts2Solver port.

static void CheckHookDeactivation(Rules rules)
{
    Check(Registry.TryPower("THE_GAMBIT_POWER", out ushort gambit), "缺少 THE_GAMBIT_POWER");
    Check(Registry.TryRelic("LAVA_LAMP", out ushort lamp), "缺少 LAVA_LAMP");
    Check(Registry.TryRelic("LIZARD_TAIL", out ushort lizard), "缺少 LIZARD_TAIL");

    static Battle Setup(ushort gambit, ushort lamp, ushort? lizard)
    {
        Battle battle = NewBattle();
        battle.Flags |= BattleFlags.PlayerHooksActive;
        battle.Player.CurrentHp = 5;
        battle.Player.MaxHp = 50;
        battle.PowerCount = 1;
        battle.Powers[0] = new Power { Id = gambit, Owner = Limits.PlayerOwner, Amount = 1 };
        battle.RelicCount = lizard == null ? (byte)1 : (byte)2;
        battle.Relics[0] = new Relic { Id = lamp };
        if (lizard is { } lizardId)
            battle.Relics[1] = new Relic { Id = lizardId };
        return battle;
    }

    // TheGambitPower kills the player inside AfterDamageReceived. Native deactivates the player's
    // hooks at that point, so a later relic listener (LavaLamp) must not run.
    Battle dead = Setup(gambit, lamp, lizard: null);
    Exec deadExec = new(ref dead, rules, default);
    AfterDamageReceivedHook deadHook = new()
    {
        Result = new DamageResult
        {
            Receiver = Limits.PlayerOwner,
            Props = ValueProps.Move,
            UnblockedDamage = 10,
        },
        Dealer = Limits.FirstEnemyOwner,
    };
    HookDispatch.Dispatch(ref deadExec, ref deadHook, gatedByEnding: false);
    Check(dead.Player.IsDead, "TheGambit 击杀后玩家应死亡");
    Check(dead.Relics[0].Counter == 0, "玩家钩子失效后不应再调用 LavaLamp");

    // Death prevention keeps the player's hooks active; the later relic listener still runs.
    Battle revived = Setup(gambit, lamp, lizard);
    Exec revivedExec = new(ref revived, rules, default);
    AfterDamageReceivedHook revivedHook = deadHook;
    HookDispatch.Dispatch(ref revivedExec, ref revivedHook, gatedByEnding: false);
    Check(!revived.Player.IsDead, "LizardTail 应阻止 TheGambit 的死亡");
    Check(revived.Relics[1].Counter == 1, "LizardTail 应记录已用");
    Check(revived.Relics[0].Counter == 1, "死亡被阻止后 LavaLamp 仍应收到后续伤害回调");
}

static void CheckCardPoolContracts()
{
    string[] known = [.. typeof(CardPools).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(static field => (string?)field.GetRawConstantValue()
            ?? throw new InvalidOperationException($"卡池常量 {field.Name} 没有值。"))
        .Order(StringComparer.Ordinal)];
    Check(known.Length == 10, $"CardPools 常量应为 10 个，实际 {known.Length}");
    HashSet<string> knownSet = [.. known];
    HashSet<string> declared = new(StringComparer.Ordinal);
    void Declare(ContentDef def)
    {
        foreach (string pool in def.GeneratedCardPools.Concat(def.SetupOnlyCardPools))
        {
            Check(knownSet.Contains(pool), $"{def.NativeId} 声明了未知卡池 {pool}");
            declared.Add(pool);
        }
    }
    foreach (ContentDef def in AllContentDefinitions())
        Declare(def);
    foreach (string pool in known)
        Check(declared.Contains(pool), $"卡池 {pool} 没有内容声明");

    void RequirePotionPool(string nativeId, string pool)
    {
        Check(Registry.TryPotion(nativeId, out ushort id), $"缺少药水 {nativeId}");
        Check(Registry.Potion(id).GeneratedCardPools.Contains(pool),
            $"{nativeId} 必须声明卡池 {pool}");
    }
    RequirePotionPool("ATTACK_POTION", CardPools.CharacterAll);
    RequirePotionPool("SKILL_POTION", CardPools.CharacterAll);
    RequirePotionPool("POWER_POTION", CardPools.CharacterAll);
    RequirePotionPool("OROBIC_ACID", CardPools.CharacterAll);
    RequirePotionPool("COLORLESS_POTION", CardPools.ColorlessAll);
    RequirePotionPool("COSMIC_CONCOCTION", CardPools.ColorlessAll);

    void RequireCardPool(string nativeId, string pool)
    {
        Check(Registry.TryCard(nativeId, out ushort id), $"缺少卡牌 {nativeId}");
        Check(Registry.Card(id).GeneratedCardPools.Contains(pool),
            $"{nativeId} 必须声明卡池 {pool}");
    }
    RequireCardPool("INFERNAL_BLADE", CardPools.CharacterAttacks);
    RequireCardPool("DISCOVERY", CardPools.CharacterAll);
    RequireCardPool("STOKE", CardPools.CharacterAll);
    Console.WriteLine($"CARD_POOL_CONTRACTS keys={known.Length} declared={declared.Count}");
}

static IEnumerable<ContentDef> AllContentDefinitions()
{
    for (int id = 0; id < Registry.CardCount; id++)
        yield return Registry.Card((ushort)id);
    for (int id = 0; id < Registry.PowerCount; id++)
        yield return Registry.Power((ushort)id);
    for (int id = 0; id < Registry.RelicCount; id++)
        yield return Registry.Relic((ushort)id);
    for (int id = 0; id < Registry.PotionCount; id++)
        yield return Registry.Potion((ushort)id);
    for (int id = 0; id < Registry.MonsterCount; id++)
        yield return Registry.Monster((ushort)id);
    for (int id = 0; id < Registry.OrbCount; id++)
        yield return Registry.Orb((ushort)id);
    for (int id = 0; id < Registry.EnchantmentCount; id++)
        yield return Registry.Enchantment((ushort)id);
    for (int id = 0; id < Registry.AfflictionCount; id++)
        yield return Registry.Affliction((ushort)id);
    for (int id = 0; id < Registry.EncounterCount; id++)
        yield return Registry.Encounter((ushort)id);
}

static void CheckStatusCardContracts()
{
    string[] statusIds =
    [
        "BECKON", "BURN", "DAZED", "DEBRIS", "FRANTIC_ESCAPE", "INFECTION",
        "WITHER", "SLIMED", "SOOT", "TOXIC", "VOID", "WOUND",
    ];
    HashSet<string> statusSet = [.. statusIds];
    foreach (string status in statusIds)
        Check(Registry.TryCard(status, out _), $"缺少状态牌定义 {status}");
    HashSet<string> generated = new(StringComparer.Ordinal);
    foreach (ContentDef def in AllContentDefinitions())
    {
        foreach ((string cardId, _) in def.GeneratedCards)
        {
            if (statusSet.Contains(cardId))
                generated.Add(cardId);
        }
    }
    foreach (string status in statusIds)
        Check(generated.Contains(status), $"状态牌 {status} 没有任何内容声明为生成卡");

    void RequireCard(string defId, string status)
    {
        Check(Registry.TryCard(defId, out ushort id), $"缺少卡牌 {defId}");
        Check(Registry.Card(id).GeneratedCards.Any(item => item.CardId == status),
            $"{defId} 必须声明生成状态牌 {status}");
    }
    void RequirePower(string defId, string status)
    {
        Check(Registry.TryPower(defId, out ushort id), $"缺少 Power {defId}");
        Check(Registry.Power(id).GeneratedCards.Any(item => item.CardId == status),
            $"{defId} 必须声明生成状态牌 {status}");
    }
    void RequireMonster(string defId, string status)
    {
        Check(Registry.TryMonster(defId, out ushort id), $"缺少怪物 {defId}");
        Check(Registry.Monster(id).GeneratedCards.Any(item => item.CardId == status),
            $"{defId} 必须声明生成状态牌 {status}");
    }
    void RequireRelic(string defId, string status)
    {
        Check(Registry.TryRelic(defId, out ushort id), $"缺少遗物 {defId}");
        Check(Registry.Relic(id).GeneratedCards.Any(item => item.CardId == status),
            $"{defId} 必须声明生成状态牌 {status}");
    }

    RequireCard("OVERCLOCK", "BURN");
    RequireCard("FIGHT_THROUGH", "WOUND");
    RequireCard("TURBO", "VOID");
    RequireCard("BOOST_AWAY", "DAZED");
    RequireCard("GUNK_UP", "SLIMED");
    RequireCard("COLLISION_COURSE", "DEBRIS");
    RequireCard("CRASH_LANDING", "DEBRIS");
    RequirePower("WITHERING_PRESENCE_POWER", "WITHER");
    RequirePower("PAINFUL_STABS_POWER", "WOUND");
    RequirePower("PERSONAL_HIVE_POWER", "DAZED");
    RequireRelic("BIIIG_HUG", "SOOT");
    RequireMonster("CHOMPER", "DAZED");
    RequireMonster("EYE_WITH_TEETH", "DAZED");
    RequireMonster("VANTOM", "WOUND");
    RequireMonster("MECHA_KNIGHT", "BURN");
    RequireMonster("TWIG_SLIME_M", "SLIMED");
    RequireMonster("LEAF_SLIME_M", "SLIMED");
    RequireMonster("LEAF_SLIME_S", "SLIMED");
    RequireMonster("SLIMED_BERSERKER", "SLIMED");
    RequireMonster("TEST_SUBJECT", "BURN");
    RequireMonster("AEONGLASS", "WITHER");
    RequireMonster("PHROG_PARASITE", "INFECTION");
    RequireMonster("WRIGGLER", "INFECTION");
    RequireMonster("THE_INSATIABLE", "FRANTIC_ESCAPE");
    RequireMonster("MYTE", "TOXIC");
    RequireMonster("HAUNTED_SHIP", "DAZED");
    RequireMonster("SOUL_FYSH", "BECKON");
    RequireMonster("NOISEBOT", "DAZED");
    Console.WriteLine($"STATUS_CARD_CONTRACTS statuses={statusIds.Length} generated={generated.Count}");
}

static void CheckPrerootGeneratedCardContracts()
{
    // The controller compiles rules in the CombatStartingEvent prefix, before Hook.BeforeCombatStart
    // and the first turn's BeforeHandDraw. Every native generation in those pre-root hooks must be
    // visible to the rule closure even though the generated card is only in the imported battle.
    void RequireCard(string relicId, string cardId, int upgrade)
    {
        Check(Registry.TryRelic(relicId, out ushort id), $"缺少遗物 {relicId}");
        Check(Registry.Relic(id).GeneratedCards.Any(item =>
                item.CardId == cardId && item.Upgrade == upgrade),
            $"{relicId} 必须在规则编译闭包里声明 {cardId}+{upgrade}");
    }
    void RequirePool(string relicId, string pool)
    {
        Check(Registry.TryRelic(relicId, out ushort id), $"缺少遗物 {relicId}");
        Check(Registry.Relic(id).GeneratedCardPools.Contains(pool),
            $"{relicId} 必须在规则编译闭包里声明卡池 {pool}");
    }

    RequireCard("BLESSED_ANTLER", "DAZED", 0);
    RequireCard("TEA_OF_DISCOURTESY", "DAZED", 0);
    RequireCard("FUNERARY_MASK", "SOUL", 0);
    RequireCard("NINJA_SCROLL", "SHIV", 0);
    RequireCard("RADIANT_PEARL", "LUMINESCE", 0);
    RequireCard("RADIANT_PEARL", "LUMINESCE", 1);
    RequirePool("ORANGE_DOUGH", CardPools.ColorlessAll);
    RequirePool("TOOLBOX", CardPools.ColorlessAll);
    RequirePool("CHOICES_PARADOX", CardPools.CharacterAll);
    RequirePool("CROSSBOW", CardPools.CharacterAttacks);
    RequirePool("BIG_HAT", CardPools.CharacterAll);
    RequirePool("VEXING_PUZZLEBOX", CardPools.CharacterAll);
    Console.WriteLine("PREROOT_GENERATED_CARD_CONTRACTS relics=11");
}

static void CheckGrowthRewards()
{
    Battle battle = default;
    foreach (GrowthRewardSource source in Enum.GetValues<GrowthRewardSource>())
        GrowthRewards.Add(ref battle, source);
    foreach (GrowthRewardSource source in Enum.GetValues<GrowthRewardSource>())
        Check(GrowthRewards.Get(battle, source) == 1, $"成长来源 {source} 应记录一次");
    Check(GrowthRewards.Get(battle, GrowthRewardSource.TheHunt) == 1, "同计数器的高 16 位不应串值");
    GrowthRewards.Set(ref battle, GrowthRewardSource.MadScience, ushort.MaxValue);
    bool overflowed = false;
    try
    {
        GrowthRewards.Add(ref battle, GrowthRewardSource.MadScience);
    }
    catch (CapacityException)
    {
        overflowed = true;
    }
    Check(overflowed, "成长计数溢出必须显式成为容量边界");
    Check((int)Counter.GrowthRewardsForbiddenGrimoireMadScience < Limits.Counters,
        "成长计数器必须位于固定计数器数组内");
}

static void CheckRng()
{
    ulong seed = 0x1234_5678_9ABC_DEF0UL;
    ulong Split(ref ulong x)
    {
        ulong z = x += 11400714819323198485UL;
        z = (z ^ (z >> 30)) * 13787848793156543929UL;
        z = (z ^ (z >> 27)) * 10723151780598845931UL;
        return z ^ (z >> 31);
    }
    ulong s0 = Split(ref seed), s1 = Split(ref seed), s2 = Split(ref seed), s3 = Split(ref seed);
    ulong Next()
    {
        ulong result = BitOperations.RotateLeft(s1 * 5, 7) * 9;
        ulong t = s1 << 17;
        s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= t; s3 = BitOperations.RotateLeft(s3, 45);
        return result;
    }
    double NextDouble() => (Next() >> 11) * 1.1102230246251565E-16;

    RngState rng = RngState.FromSerializable(0, s0, s1, s2, s3);
    for (int index = 0; index < 2000; index++)
    {
        int max = 1 + index % 37;
        int expected = (int)(NextDouble() * max);
        Check(rng.NextInt(max) == expected, $"NextInt 第 {index} 次不一致");
        float total = 0.5f + index % 7;
        float expectedFloat = (float)(NextDouble() * total);
        Check(rng.NextFloat(total) == expectedFloat, $"NextFloat 第 {index} 次不一致");
        int expectedRange = (int)(NextDouble() * 9) + 3;
        Check(rng.NextInt(3, 12) == expectedRange, $"NextInt(min,max) 第 {index} 次不一致");
    }
    Check(rng.Counter == 6000, $"计数器应为 6000，实际 {rng.Counter}");
}

// Sts2Solver's introsort must permute equal-key cards exactly like List<T>.Sort().
static void CheckIntroSort(Rules rules)
{
    Random random = new(17);
    for (int trial = 0; trial < 4000; trial++)
    {
        int length = 1 + random.Next(80);
        Card[] cards = new Card[length];
        List<Keyed> reference = new(length);
        for (int index = 0; index < length; index++)
        {
            cards[index] = new Card { Id = (ushort)random.Next(3), Upgrade = (byte)random.Next(2), Private = (short)index };
            reference.Add(new Keyed(cards[index]));
        }
        reference.Sort();
        Card[] original = (Card[])cards.Clone();
        CardSort.Sort(cards, rules);
        Card[] withOrigins = (Card[])original.Clone();
        int[] origins = new int[length];
        for (int index = 0; index < length; index++)
            origins[index] = index;
        CardSort.SortWithOrigins(withOrigins, origins, rules);
        for (int index = 0; index < length; index++)
        {
            Check(cards[index].Private == reference[index].Card.Private, $"排序第 {trial} 组第 {index} 位与 List.Sort 不一致");
            Check(withOrigins[index].Private == cards[index].Private, $"带来源排序第 {trial} 组第 {index} 位与 Sort 不一致");
            Check(original[origins[index]].Private == cards[index].Private, $"来源映射第 {trial} 组第 {index} 位错误");
        }
    }
}

static void CheckCapacity()
{
    Battle battle = default;
    Card card = new() { Id = 0 };
    for (int index = 0; index < Limits.Cards; index++)
        Piles.Append(ref battle, Pile.Draw, card);
    try
    {
        Piles.Append(ref battle, Pile.Draw, card);
        throw new InvalidOperationException("超出卡牌容量应抛出 CapacityException");
    }
    catch (CapacityException)
    {
    }
}

static void CheckPiles()
{
    Battle battle = default;
    for (short index = 0; index < 6; index++)
        Piles.Append(ref battle, index < 3 ? Pile.Draw : Pile.Discard, new Card { Private = index });
    Piles.DrawTopToHand(ref battle);
    Check(battle.HandCount == 1 && battle.DrawCount == 2 && Piles.At(ref battle, Pile.Hand, 0).Private == 0, "抽顶牌应只移动段边界");
    Card removed = Piles.RemoveAt(ref battle, Pile.Discard, 1);
    Check(removed.Private == 4 && battle.DiscardCount == 2
        && Piles.At(ref battle, Pile.Discard, 0).Private == 3
        && Piles.At(ref battle, Pile.Discard, 1).Private == 5, "移除后应前移");
    Battle copy = battle;
    Check(Fingerprint.Equal(copy, battle) && Fingerprint.Hash(copy) == Fingerprint.Hash(battle), "复制后应字节相同");
}

// P16 kernel capabilities: the return-to-hand card bit, X-cost SetToFreeThisTurn, and instanced
// power row addressing.
static void CheckP16Kernel(Rules rules)
{
    Card card = new() { Id = 0 };
    card.ReturnToHandNextTurn = true;
    card.SetTemporaryStarCost(0, clearsAtTurnEnd: true, clearsWhenPlayed: true);
    Check(card.ReturnToHandNextTurn, "临时星能费用不应清除 ReturnToHandNextTurn");
    card.EnchantmentStatus = 3;
    Check(card.ReturnToHandNextTurn && card.EnchantmentStatus == 3, "附魔状态不应清除 ReturnToHandNextTurn");
    card.ClearTemporaryStarCost();
    Check(card.ReturnToHandNextTurn, "费用清理不应清除 ReturnToHandNextTurn");
    card.ReturnToHandNextTurn = false;
    Check(!card.ReturnToHandNextTurn && card.EnchantmentStatus == 3, "清除 ReturnToHandNextTurn 不应影响附魔状态");

    // CardEnergyCost.SetThisTurnOrUntilPlayed adds the modifier for X-cost cards (canonical 0).
    Registry.TryCard("WHIRLWIND", out ushort whirlwind);
    Card xCard = Kernel.CreateCard(rules, whirlwind);
    Kernel.MakeFreeThisTurn(rules, ref xCard);
    Check(xCard.EnergyModifierCount == 1 && xCard.HasTemporaryStarCost,
        "X 费卡的 SetToFreeThisTurn 也应添加 0 费修饰与临时星能费用");

    // Instanced powers share (owner, id); occurrence addresses the exact row.
    Battle battle = default;
    battle.PowerCount = 2;
    battle.Powers[0] = new Power { Id = 1, Owner = Limits.PlayerOwner, Amount = 1 };
    battle.Powers[1] = new Power { Id = 1, Owner = Limits.PlayerOwner, Amount = 2 };
    Check(Kernel.FindPowerOccurrence(battle, Limits.PlayerOwner, 1, 0) == 0
        && Kernel.FindPowerOccurrence(battle, Limits.PlayerOwner, 1, 1) == 1
        && Kernel.FindLastPower(battle, Limits.PlayerOwner, 1) == 1,
        "实例 Power 的 occurrence 定位错误");
}

// Forced moves and the synthetic STUNNED node (SimulatedCombatState.MonsterAi.cs:58-128).
static void CheckMonsterForcedMoves(Rules rules)
{
    byte owner = Kernel.EnemyOwner(0);
    Battle battle = SliceBattle();
    PlayerAction action = default;
    Exec x = new(ref battle, rules, action);
    Kernel.ForceStunnedMove(ref x, owner, -1);
    Check(battle.Enemies[0].Move == 4 && battle.Enemies[0].ForcedMove == 1,
        "STUNNED 缺省后继应取 StateLog 最后一条");
    Check(battle.Enemies[0].LoggedMoves == 1, "强制行动不应写入行动日志");
    battle.Enemies[0].Flags |= EnemyFlags.PerformedMoveThisRound;
    Kernel.PrepareMonsterMovesForNextRound(ref x);
    Check(battle.Enemies[0].Move == 1 && battle.Enemies[0].ForcedMove == byte.MaxValue,
        "执行 STUNNED 后应推进到动态后继并清空 ForcedMove");
    Check(battle.Enemies[0].LoggedMoves == 2, "推进后的后继应写入行动日志");

    battle = SliceBattle();
    x = new Exec(ref battle, rules, action);
    battle.Enemies[0].Flags |= EnemyFlags.PerformedMoveThisRound;
    Kernel.ForceStunnedMove(ref x, owner, 2);
    Check((battle.Enemies[0].Flags & EnemyFlags.ForcedMustPerformAfterMove) != 0,
        "本轮已行动后强制 STUNNED 应记下 must-perform-once 未满足");
    Kernel.PrepareMonsterMovesForNextRound(ref x);
    Check(battle.Enemies[0].Move == 4 && battle.Enemies[0].ForcedMove == 2,
        "本轮已行动后强制 STUNNED 不应推进");
    Check((battle.Enemies[0].Flags & EnemyFlags.ForcedMustPerformAfterMove) == 0,
        "权威边界上不得残留 ForcedMustPerformAfterMove");
    battle.Enemies[0].Flags |= EnemyFlags.PerformedMoveThisRound;
    Kernel.PrepareMonsterMovesForNextRound(ref x);
    Check(battle.Enemies[0].Move == 2, "STUNNED 在下一轮执行后应推进到后继");

    battle = SliceBattle();
    x = new Exec(ref battle, rules, action);
    battle.Enemies[0].Flags |= EnemyFlags.PerformedMoveThisRound;
    Kernel.ForceMonsterMove(ref x, owner, 3);
    Kernel.PrepareMonsterMovesForNextRound(ref x);
    Check(battle.Enemies[0].Move == 1 && battle.Enemies[0].ForcedMove == byte.MaxValue,
        "普通强制行动在本轮已行动后应直接推进");

    battle = SliceBattle();
    x = new Exec(ref battle, rules, action);
    Kernel.ForceStunnedMove(ref x, owner, 2);
    Check(battle.Enemies[0].ForcedMove == 2, "显式后继应写入 ForcedMove");
    Check(battle.Enemies[0].Move == 4, "强制 STUNNED 应指向合成节点");
}

// KM2 kernel contracts: slot names outside the encounter list behave like
// SimulatedCombatState.GetSlotIndex (-1), a fully blocked Damage result exposes the flag the
// Imbalanced stun reads, and an unresolved spawning death keeps IsEnding false.
static void CheckKm2Kernel(Rules rules)
{
    Check(Kernel.SlotIndex(rules, "second") == 1, "已知槽位应返回其下标");
    Check(Kernel.SlotIndex(rules, "middle") == Kernel.NoSlot,
        "EncounterModel.Slots 之外的槽名应映射到 NoSlot");
    Check(Kernel.SlotIndex(rules, null) == Kernel.NoSlot, "null 槽名应映射到 NoSlot");

    PlayerAction action = default;
    Battle battle = SliceBattle();
    battle.Player.Block = 100;
    Exec x = new(ref battle, rules, action);
    Check(Kernel.Damage(ref x, [Limits.PlayerOwner], 10m, ValueProps.Move,
            Kernel.EnemyOwner(0), hasCardSource: false, default),
        "完全格挡的 Damage 应返回 true");
    battle = SliceBattle();
    x = new Exec(ref battle, rules, action);
    Check(!Kernel.Damage(ref x, [Limits.PlayerOwner], 10m, ValueProps.Move,
            Kernel.EnemyOwner(0), hasCardSource: false, default),
        "未完全格挡的 Damage 应返回 false");

    Check(Registry.TryPower("INFESTED_POWER", out ushort infested), "InfestedPower 应有 Sts2Solver 定义");
    battle = default;
    battle.Outcome = Outcome.None;
    battle.EnemyCount = 1;
    battle.RosterCount = 0;
    battle.Enemies[0].Creature.Flags = CreatureFlags.Present;
    battle.Enemies[0].DeathPhase = DeathPhase.None;
    battle.PowerCount = 1;
    battle.Powers[0] = new Power
    {
        Id = infested, Owner = Kernel.EnemyOwner(0), Amount = 1, Applier = Kernel.EnemyOwner(0),
    };
    Check(!Kernel.IsEnding(battle), "未结算的寄生死亡应阻止战斗结束");
    battle.PowerCount = 0;
    battle.Powers[0] = default;
    Check(Kernel.IsEnding(battle), "死亡效果结算完后应允许战斗结束");
}

// Rules for the slice content: Ironclad starter cards and one Nibbit.
// DF: orb kernel contracts. The existing engine's order and values are pinned here: full-queue
// channel evokes the leftmost orb first, Dark evokes its raw accumulation (no ModifyOrbValue), Glass
// decays its private passive by one after a positive passive, Focus modifies passive/evoke values,
// Plasma's passive fires at turn start, and GoldPlatedCables adds one passive trigger to the first orb.
const int TwStunnedNode = 2;

// TW: theft and Waterfall Giant kernel capabilities. The contracts cover the detached stolen-card
// table, the outstanding-resource ledger, escape compaction, the Steam Eruption death rewrite/forced
// phases and the explicit rejection of a Flutter stun whose last move leads to a branch node.
static void CheckTwKernel()
{
    Rules rules = TwRules();
    Check(Unsafe.SizeOf<Enemy>() == 64, "Enemy 加入蒸汽阶段后仍应为 64 字节");
    Check(Unsafe.SizeOf<Battle>() == Battle.ExpectedSize, "Battle 尺寸应与 ExpectedSize 一致");

    // Detached table: three live slots (two Nightmare instances plus one stolen card), no eviction;
    // a removed SwipePower keeps its value for the write-back, while Nightmare releases its slot.
    Battle detached = TwBattle(rules);
    Card value = new() { Id = 1, BaseCost = 1, Flags = CardFlags.RemovedFromState };
    int firstSlot = -1;
    for (int index = 0; index < Limits.DetachedCards; index++)
    {
        int slot = Kernel.AllocateDetachedCard(ref detached, value);
        if (index == 0)
            firstSlot = slot;
    }
    bool overflow = false;
    try
    {
        Kernel.AllocateDetachedCard(ref detached, value);
    }
    catch (CapacityException)
    {
        overflow = true;
    }
    Check(overflow, "DetachedCards 满后应抛 CapacityException");
    Exec detachExec = new(ref detached, rules, default);
    Kernel.ApplyPower(ref detachExec, Kernel.EnemyOwner(0), Registry.PowerId<SwipePower>(), 1, 0);
    int swipeRow = Kernel.FindPower(detached, Kernel.EnemyOwner(0), Registry.PowerId<SwipePower>());
    detached.Powers[swipeRow].Aux0 = firstSlot + 1;
    Kernel.SetPowerAmount(ref detached, Kernel.EnemyOwner(0), Registry.PowerId<SwipePower>(), 0);
    Check(detached.DetachedCards[firstSlot].Id == value.Id
        && (detached.DetachedCards[firstSlot].Flags & CardFlags.RemovedFromState) != 0,
        "未回收的 SwipePower 应保留被偷牌值并保留 RemovedFromState");

    // GremlinMerc: min(power amount, gold), the private gold ledger and the outstanding counter.
    Battle thievery = TwBattle(rules);
    thievery.Gold = 50;
    Exec thiefExec = new(ref thievery, rules, default);
    Kernel.ApplyPowerTargeted(ref thiefExec, Kernel.EnemyOwner(0), Limits.PlayerOwner,
        Registry.PowerId<ThieveryPower>(), 20, Kernel.EnemyOwner(0));
    Kernel.RecordThievery(ref thiefExec, Kernel.EnemyOwner(0));
    Check(thievery.Gold == 30, $"首次偷窃应拿走 20 金币，实际余额 {thievery.Gold}");
    Check(thievery.Counter(Counter.OutstandingStolenGold) == 20, "未追回金币应为 20");
    int thieveryLedgerRow = Kernel.FindPower(thievery, Kernel.EnemyOwner(0),
        Registry.PowerId<ThieveryPower>());
    Check(thieveryLedgerRow >= 0 && thievery.Powers[thieveryLedgerRow].Aux0 == 20,
        "ThieveryPower 的私有金币账本应为 20");
    thievery.Gold = 5;
    Kernel.RecordThievery(ref thiefExec, Kernel.EnemyOwner(0));
    Check(thievery.Gold == 0 && thievery.Counter(Counter.OutstandingStolenGold) == 25,
        "第二次偷窃应受剩余金币限制并为 5");
    int thieveryRow = Kernel.FindPower(thievery, Kernel.EnemyOwner(0), Registry.PowerId<ThieveryPower>());
    thievery.Powers[thieveryRow].Target = Limits.NoOwner;
    int before = thievery.Counter(Counter.OutstandingStolenGold);
    Kernel.RecordThievery(ref thiefExec, Kernel.EnemyOwner(0));
    Check(thievery.Counter(Counter.OutstandingStolenGold) == before, "缺少目标时不应偷窃");

    // RecoverStolenResources: killing the Swipe/Heist owner returns the card and gold; the detached
    // value is dropped with the power.
    Battle recovered = TwBattle(rules);
    Exec recoverExec = new(ref recovered, rules, default);
    int stolenSlot = Kernel.AllocateDetachedCard(ref recovered, value);
    Kernel.ApplyPowerTargeted(ref recoverExec, Kernel.EnemyOwner(0), Limits.PlayerOwner,
        Registry.PowerId<SwipePower>(), 1, Kernel.EnemyOwner(0));
    int recoverSwipe = Kernel.FindPower(recovered, Kernel.EnemyOwner(0), Registry.PowerId<SwipePower>());
    recovered.Powers[recoverSwipe].Aux0 = stolenSlot + 1;
    Kernel.ApplyPowerTargeted(ref recoverExec, Kernel.EnemyOwner(0), Limits.PlayerOwner,
        Registry.PowerId<HeistPower>(), 7, Kernel.EnemyOwner(0));
    recovered.SetCounter(Counter.OutstandingStolenCards, 1);
    recovered.SetCounter(Counter.OutstandingStolenGold, 7);
    recovered.Enemies[0].Creature.CurrentHp = 0;
    Kernel.ApplyEnemyDeathPowers(ref recoverExec);
    Check(recovered.Counter(Counter.OutstandingStolenCards) == 0
        && recovered.Counter(Counter.OutstandingStolenGold) == 0, "击杀飞贼应追回被偷卡与金币");
    Check(recovered.DetachedCards[stolenSlot].Id == value.Id
        && (recovered.DetachedCards[stolenSlot].Flags & CardFlags.RemovedFromState) == 0,
        "追回后应保留归还卡值并清除 RemovedFromState");
    Check(Kernel.FindPower(recovered, Kernel.EnemyOwner(0), Registry.PowerId<SwipePower>()) < 0,
        "死亡清理应移除 SwipePower");

    // Owner-left-roster cleanup: MinionPower (native keep-on-death) is removed once the creature
    // leaves the roster; FlutterPower keeps its observed pending row (PowerRemainsAfterOwnerLeavesRoster).
    Battle roster = TwBattle(rules);
    Exec rosterExec = new(ref roster, rules, default);
    // Apply Flutter while the enemy is still primary: MinionPower turns the only enemy secondary,
    // which would make IsEnding true and correctly stop the later Flutter application.
    Kernel.ApplyPower(ref rosterExec, Kernel.EnemyOwner(0), Registry.PowerId<FlutterPower>(), 1,
        Kernel.EnemyOwner(0));
    Kernel.ApplyPower(ref rosterExec, Kernel.EnemyOwner(0), Registry.PowerId<MinionPower>(), 1,
        Kernel.EnemyOwner(0));
    roster.Enemies[0].Creature.CurrentHp = 0;
    Kernel.RemoveFromRoster(ref roster, Kernel.EnemyOwner(0));
    Kernel.ApplyEnemyDeathPowers(ref rosterExec);
    Check(Kernel.FindPower(roster, Kernel.EnemyOwner(0), Registry.PowerId<MinionPower>()) < 0,
        "离开名单的死者应移除 MinionPower");
    Check(Kernel.FindPower(roster, Kernel.EnemyOwner(0), Registry.PowerId<FlutterPower>()) >= 0,
        "离开名单的 Hopper 应保留 FlutterPower 的既有 pending 行");

    // Escape compacts the known-enemy array and remaps later owners, but does not recover loot.
    Battle escaped = TwBattle(rules);
    escaped.EnemyCount = 2;
    escaped.RosterCount = 2;
    escaped.Roster[0] = 0;
    escaped.Roster[1] = 1;
    escaped.Enemies[1] = escaped.Enemies[0];
    escaped.Enemies[1].CombatId = 2;
    escaped.Enemies[1].Creature = new Creature { CurrentHp = 40, MaxHp = 40, Flags = CreatureFlags.Present };
    escaped.Enemies[1].Flags = EnemyFlags.InCombat;
    Exec escapeExec = new(ref escaped, rules, default);
    int escapeSlot = Kernel.AllocateDetachedCard(ref escaped, value);
    Kernel.ApplyPowerTargeted(ref escapeExec, Kernel.EnemyOwner(0), Limits.PlayerOwner,
        Registry.PowerId<SwipePower>(), 1, Kernel.EnemyOwner(0));
    int escapeSwipe = Kernel.FindPower(escaped, Kernel.EnemyOwner(0), Registry.PowerId<SwipePower>());
    escaped.Powers[escapeSwipe].Aux0 = escapeSlot + 1;
    escaped.SetCounter(Counter.OutstandingStolenCards, 1);
    Kernel.ApplyPower(ref escapeExec, Kernel.EnemyOwner(1), Registry.PowerId<StrengthPower>(), 2,
        Kernel.EnemyOwner(1));
    Kernel.EscapeEnemy(ref escapeExec, Kernel.EnemyOwner(0));
    Check(escaped.EnemyCount == 2 && escaped.RosterCount == 1 && escaped.Roster[0] == 0,
        "逃跑应压实名单并在已知敌人末尾留下逃跑墓碑");
    Check(escaped.Enemies[1].Flags == EnemyFlags.Escaped
        && escaped.Enemies[1].DeathPhase == DeathPhase.PermanentlyDead,
        "逃跑墓碑应带 Escaped 标记且为永久死亡");
    Check(escaped.DetachedCards[escapeSlot].Id == value.Id
        && (escaped.DetachedCards[escapeSlot].Flags & CardFlags.RemovedFromState) != 0,
        "逃跑移除 SwipePower 应保留丢失牌值并保留 RemovedFromState");
    Check(escaped.Counter(Counter.OutstandingStolenCards) == 1, "逃跑后未追回卡数应保持不变");
    int shifted = Kernel.FindPower(escaped, Kernel.EnemyOwner(0), Registry.PowerId<StrengthPower>());
    Check(shifted >= 0 && escaped.Powers[shifted].Owner == Kernel.EnemyOwner(0),
        "后续敌人的 Power owner 应随下标前移");

    // A surviving power that names the escaped creature as applier cannot be projected.
    Battle referenced = TwBattle(rules);
    referenced.EnemyCount = 2;
    referenced.RosterCount = 2;
    referenced.Roster[1] = 1;
    referenced.Enemies[1] = referenced.Enemies[0];
    referenced.Enemies[1].CombatId = 2;
    referenced.Enemies[1].Flags = EnemyFlags.InCombat;
    Exec referencedExec = new(ref referenced, rules, default);
    Kernel.ApplyPowerFromMonster(ref referencedExec, Limits.PlayerOwner, Registry.PowerId<WeakPower>(), 1,
        Kernel.EnemyOwner(0));
    bool rejected = false;
    try
    {
        Kernel.EscapeEnemy(ref referencedExec, Kernel.EnemyOwner(0));
    }
    catch (UnsupportedException)
    {
        rejected = true;
    }
    Check(rejected, "逃跑生物仍被 Power 引用时应显式拒绝");

    // Steam Eruption: a dead owner is rewritten to the forced about-to-blow phase, the power is kept by
    // the death cleanup, and the two forced consumptions record then deal the explosion damage.
    Battle steam = TwBattle(rules);
    Exec steamExec = new(ref steam, rules, default);
    Kernel.ApplyPower(ref steamExec, Kernel.EnemyOwner(0), Registry.PowerId<SteamEruptionPower>(), 18,
        Kernel.EnemyOwner(0));
    steam.Enemies[0].Creature.CurrentHp = 0;
    Kernel.Kill(ref steamExec, [Kernel.EnemyOwner(0)], force: false);
    Check(steam.Enemies[0].Creature.CurrentHp == Limits.ValueClamp
        && steam.Enemies[0].Creature.MaxHp == Limits.ValueClamp, "蒸汽爆发的死亡改写应把 HP/最大 HP 设为钳制值");
    Check(steam.Enemies[0].SteamPhase == SteamPhase.AboutToBlow, "死亡改写应进入 AboutToBlow 阶段");
    Check(steam.Enemies[0].Move == 0, $"死亡改写应强制 ABOUT_TO_BLOW_MOVE，实际节点 {steam.Enemies[0].Move}");
    Check(Kernel.GetAmount(steam, Kernel.EnemyOwner(0), Registry.PowerId<SteamEruptionPower>()) == 18,
        "死亡清理应保留 SteamEruptionPower");
    Check(!Kernel.IsEnding(steam), "有水火巨兽时战斗不应结束");
    Check(Kernel.TryConsumeSteamForcedMove(ref steamExec, Kernel.EnemyOwner(0), out bool explode1, out _)
        && !explode1 && steam.Enemies[0].SteamPhase == SteamPhase.Explode, "第一次强制行动应进入 Explode 阶段");
    Check(Kernel.GetAmount(steam, Kernel.EnemyOwner(0), Registry.PowerId<SteamEruptionPower>()) == 0
        && steam.Enemies[0].Ai[Kernel.SteamEruptionDamageRegister] == 18, "AboutToBlow 应记录爆发伤害并移除 Power");
    Check(Kernel.TryConsumeSteamForcedMove(ref steamExec, Kernel.EnemyOwner(0), out bool explode2, out int damage)
        && explode2 && damage == 18, "第二次强制行动应报告 18 点爆发伤害");

    // Flutter: the last stack resolves the next state of the last logged move. A direct move is
    // accepted; a branch follow-up is an explicit boundary (the assumption contract).
    Battle flutter = TwBattle(rules);
    flutter.Enemies[0].Move = 0;
    Kernel.LogMonsterMove(ref flutter.Enemies[0], 0);
    Exec flutterExec = new(ref flutter, rules, default);
    Kernel.ApplyPower(ref flutterExec, Kernel.EnemyOwner(0), Registry.PowerId<FlutterPower>(), 1,
        Kernel.EnemyOwner(0));
    Kernel.Damage(ref flutterExec, [Kernel.EnemyOwner(0)], 5m, ValueProps.Move, Limits.PlayerOwner,
        hasCardSource: false, default);
    Check(flutter.Enemies[0].Move == TwStunnedNode && flutter.Enemies[0].ForcedMove == 1,
        "振翅归零应以最近行动的下一状态进入 STUNNED");
    Battle branch = TwBattle(rules);
    branch.Enemies[0].Move = 3;
    Kernel.LogMonsterMove(ref branch.Enemies[0], 3);
    Exec branchExec = new(ref branch, rules, default);
    Kernel.ApplyPower(ref branchExec, Kernel.EnemyOwner(0), Registry.PowerId<FlutterPower>(), 1,
        Kernel.EnemyOwner(0));
    bool branchRejected = false;
    try
    {
        Kernel.Damage(ref branchExec, [Kernel.EnemyOwner(0)], 5m, ValueProps.Move, Limits.PlayerOwner,
            hasCardSource: false, default);
    }
    catch (UnsupportedException)
    {
        branchRejected = true;
    }
    Check(branchRejected, "振翅眩晕的后继是分支节点时应显式拒绝");
}

static Battle TwBattle(Rules rules)
{
    Battle battle = NewBattle();
    battle.Gold = 0;
    battle.Enemies[0].MonsterId = Registry.MonsterId<Sts2Solver.Content.Monsters.Act1.WaterfallGiant>();
    battle.Enemies[0].Rules = 0;
    battle.Enemies[0].Move = 0;
    battle.Enemies[0].ForcedMove = byte.MaxValue;
    return battle;
}

static Rules TwRules()
{
    Registry.TryMonster("WATERFALL_GIANT", out ushort waterfall);
    RulesDraft builder = new() { RootEnemyCount = 1, HasTheftContent = true };
    MonsterNode[] nodes =
    [
        new MonsterNode
        {
            NativeId = "ABOUT_TO_BLOW_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 1,
            MustPerformOnceBeforeTransitioning = true,
        },
        new MonsterNode { NativeId = "EXPLODE_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 1 },
        new MonsterNode { NativeId = "STUNNED", Kind = MonsterNodeKind.Move },
        new MonsterNode { NativeId = "FLUTTER_TEST_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 4 },
        new MonsterNode
        {
            NativeId = "TEST_BRANCH", Kind = MonsterNodeKind.ConditionalBranch, ConditionalTarget = 1,
        },
    ];
    builder.SetMonster(0, new MonsterRules
    {
        MonsterId = waterfall,
        Nodes = nodes,
        InitialNode = 0,
        PendingInitialMoveNode = 0,
        StunnedNode = TwStunnedNode,
        InitialHpMin = 100,
        InitialHpMax = 100,
        StaticInts = [],
    });
    builder.SetPowerVars(Registry.PowerId<FlutterPower>(), [50m]);
    return builder.Build();
}

static void CheckDfKernel(Rules rules)
{
    Check(Registry.TryOrb("LIGHTNING_ORB", out ushort lightning), "缺少 LIGHTNING_ORB");
    Check(Registry.TryOrb("FROST_ORB", out ushort frost), "缺少 FROST_ORB");
    Check(Registry.TryOrb("DARK_ORB", out ushort dark), "缺少 DARK_ORB");
    Check(Registry.TryOrb("PLASMA_ORB", out ushort plasma), "缺少 PLASMA_ORB");
    Check(Registry.TryOrb("GLASS_ORB", out ushort glass), "缺少 GLASS_ORB");
    Check(Registry.TryPower("FOCUS_POWER", out ushort focus), "缺少 FOCUS_POWER");
    Check(Registry.TryRelic("GOLD_PLATED_CABLES", out ushort gold), "缺少 GOLD_PLATED_CABLES");
    Check(Registry.TryRelic("CRACKED_CORE", out ushort crackedCore), "缺少 CRACKED_CORE");
    Check(Registry.TryRelic("RUNIC_CAPACITOR", out ushort runic), "缺少 RUNIC_CAPACITOR");
    Check(Registry.TryRelic("SYMBIOTIC_VIRUS", out ushort symbiotic), "缺少 SYMBIOTIC_VIRUS");
    Check(Registry.TryRelic("REPTILE_TRINKET", out ushort reptile), "缺少 REPTILE_TRINKET");
    Check(Registry.TryPower("THUNDER_POWER", out ushort thunder), "缺少 THUNDER_POWER");
    Check(Registry.TryPower("REPTILE_TRINKET_POWER", out ushort reptilePower), "缺少 REPTILE_TRINKET_POWER");
    Check(Registry.TryPower("STRENGTH_POWER", out ushort strength), "缺少 STRENGTH_POWER");
    PlayerAction action = default;

    Battle OrbBattle(int enemyHp = 1000)
    {
        Battle battle = SliceBattle();
        battle.HandCount = 0;
        battle.DrawCount = 0;
        battle.DiscardCount = 0;
        battle.ExhaustCount = 0;
        battle.PlayCount = 0;
        battle.OrbCount = 0;
        battle.OrbCapacity = 3;
        battle.PowerCount = 0;
        battle.RelicCount = 0;
        battle.Player.Block = 0;
        battle.Enemies[0].Creature.CurrentHp = enemyHp;
        battle.Enemies[0].Creature.MaxHp = enemyHp;
        return battle;
    }

    void SetFocus(ref Battle battle, int amount)
    {
        battle.PowerCount = 1;
        battle.Powers[0] = new Power { Id = focus, Owner = Limits.PlayerOwner, Amount = amount };
    }

    // Full queue channel: the leftmost (Dark) orb is evoked first. Dark's evoke uses the raw
    // accumulation, while its passive would have used ModifyOrbValue.
    Battle battle = OrbBattle();
    SetFocus(ref battle, 2);
    battle.OrbCount = 3;
    battle.Orbs[0] = new Orb { Id = checked((byte)dark), Aux = 10 };
    battle.Orbs[1] = new Orb { Id = checked((byte)frost) };
    battle.Orbs[2] = new Orb { Id = checked((byte)lightning) };
    Exec x = new(ref battle, rules, action);
    Check(Kernel.ChannelOrb(ref x, lightning), "满槽充能应成功");
    Check(battle.OrbCount == 3 && battle.Orbs[0].Id == frost && battle.Orbs[2].Id == lightning,
        "满槽充能应先激发最左侧球");
    Check(battle.Enemies[0].Creature.CurrentHp == 990, "暗球激发不应受集中修正（原始累积 10）");
    Check(battle.Counter(Counter.LightningChannels) == 1 && battle.Counter(Counter.OrbChannels) == 1,
        "充能计数错误");

    // Turn-end passives in queue order: Frost 2+Focus=4 block, then two Lightning 3+Focus=5 hits.
    Kernel.TriggerBeforeTurnEndOrbs(ref x);
    Check(battle.Player.Block == 4, $"冰球回合末格挡应为 4，实际 {battle.Player.Block}");
    Check(battle.Enemies[0].Creature.CurrentHp == 980, "闪电球回合末被动伤害错误");

    // GoldPlatedCables adds one trigger to the first orb's passive.
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = gold };
    Check(HookDispatch.ModifyOrbPassiveTriggerCounts(battle, rules, battle.Orbs[0], 0, 1) == 2,
        "金缆应让首个球的被动触发 +1");
    battle.Player.Block = 0;
    Kernel.TriggerBeforeTurnEndOrbs(ref x);
    Check(battle.Player.Block == 8, $"金缆双次冰球被动应给 8 格挡，实际 {battle.Player.Block}");
    Check(battle.Enemies[0].Creature.CurrentHp == 970, "金缆回合末的闪电被动次数错误");

    // Dark accumulation and raw evoke.
    battle = OrbBattle();
    SetFocus(ref battle, 3);
    battle.OrbCount = 1;
    battle.Orbs[0] = new Orb { Id = checked((byte)dark), Aux = 10 };
    x = new Exec(ref battle, rules, action);
    Kernel.TriggerOrbPassive(ref x, 0);
    Check(battle.Orbs[0].Aux == 19, $"暗球被动应累积 6+3，实际 {battle.Orbs[0].Aux}");
    Kernel.EvokeOrb(ref x, 0);
    Check(battle.Enemies[0].Creature.CurrentHp == 981, "暗球激发应使用原始累积 19");

    // Glass decays after a positive passive; evoke doubles the modified remaining value.
    battle = OrbBattle();
    SetFocus(ref battle, 1);
    battle.OrbCount = 1;
    battle.Orbs[0] = new Orb { Id = checked((byte)glass), Aux = 4 };
    x = new Exec(ref battle, rules, action);
    Kernel.TriggerOrbPassive(ref x, 0);
    Check(battle.Orbs[0].Aux == 3 && battle.Enemies[0].Creature.CurrentHp == 995,
        "玻璃球被动应造成 5 并递减私有值");
    Kernel.EvokeOrb(ref x, 0);
    Check(battle.Enemies[0].Creature.CurrentHp == 987, "玻璃球激发应造成 (3+1)*2 = 8");

    // Plasma passive/evoke energy.
    battle = OrbBattle();
    battle.Energy = 0;
    battle.OrbCount = 1;
    battle.Orbs[0] = new Orb { Id = checked((byte)plasma) };
    x = new Exec(ref battle, rules, action);
    Kernel.TriggerOrbPassive(ref x, 0);
    Check(battle.Energy == 1, "等离子球被动应 +1 能量");
    Kernel.EvokeOrb(ref x, 0);
    Check(battle.Energy == 3, "等离子球激发应 +2 能量");

    // Dualcast semantics: two evocations of the same orb, dequeue only on the last one.
    battle = OrbBattle();
    battle.OrbCount = 2;
    battle.Orbs[0] = new Orb { Id = checked((byte)lightning) };
    battle.Orbs[1] = new Orb { Id = checked((byte)lightning) };
    x = new Exec(ref battle, rules, action);
    Kernel.EvokeNext(ref x, repeat: 2);
    Check(battle.OrbCount == 1 && battle.Enemies[0].Creature.CurrentHp == 984,
        "双发激发应激发两次且只出队一次");

    // Capacity 0 refuses a channel for a character with base slots.
    battle = OrbBattle();
    battle.OrbCapacity = 0;
    x = new Exec(ref battle, rules, action);
    Check(!Kernel.ChannelOrb(ref x, lightning) && battle.OrbCount == 0, "零容量不应充能");

    // Slot growth/removal.
    battle = OrbBattle();
    battle.OrbCapacity = 9;
    Kernel.AddOrbSlots(ref battle, 3);
    Check(battle.OrbCapacity == 10, "球槽容量应钳制在 10");
    battle.OrbCapacity = 3;
    battle.OrbCount = 3;
    battle.Orbs[0] = new Orb { Id = checked((byte)lightning) };
    battle.Orbs[1] = new Orb { Id = checked((byte)frost) };
    battle.Orbs[2] = new Orb { Id = checked((byte)dark) };
    Kernel.RemoveOrbSlots(ref battle, 2);
    Check(battle.OrbCapacity == 1 && battle.OrbCount == 1 && battle.Orbs[0].Id == lightning,
        "移除球槽应从队尾删球");

    // CrackedCore channels one Lightning on the first player side turn.
    battle = OrbBattle();
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = crackedCore };
    x = new Exec(ref battle, rules, action);
    Registry.Relic(crackedCore).BeforeSideTurnStart(ref x,
        new Listener(ListenerKind.Relic, 0, Limits.PlayerOwner), Side.Player);
    Check(battle.OrbCount == 1 && battle.Orbs[0].Id == lightning, "破碎核心应在第一回合充能闪电");

    // RunicCapacitor and SymbioticVirus run in the AfterSideTurnStart relic batch.
    battle = OrbBattle();
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = runic };
    x = new Exec(ref battle, rules, action);
    Registry.Relic(runic).AfterSideTurnStart(ref x,
        new Listener(ListenerKind.Relic, 0, Limits.PlayerOwner), Side.Player);
    Check(battle.OrbCapacity == 6, "符文电容器应 +3 球槽");

    battle = OrbBattle();
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = symbiotic };
    x = new Exec(ref battle, rules, action);
    Registry.Relic(symbiotic).AfterSideTurnStart(ref x,
        new Listener(ListenerKind.Relic, 0, Limits.PlayerOwner), Side.Player);
    Check(battle.OrbCount == 1 && battle.Orbs[0].Id == checked((byte)dark), "共生病毒应充能暗球");

    // AfterOrbEvoked: ThunderPower adds its amount to every living evoke target of a Lightning orb.
    battle = OrbBattle();
    battle.PowerCount = 1;
    battle.Powers[0] = new Power { Id = thunder, Owner = Limits.PlayerOwner, Amount = 5 };
    battle.OrbCount = 1;
    battle.Orbs[0] = new Orb { Id = checked((byte)lightning) };
    x = new Exec(ref battle, rules, action);
    Kernel.EvokeOrb(ref x, 0);
    Check(battle.Enemies[0].Creature.CurrentHp == 987, "雷霆应在闪电球激发后追加 5 伤害");

    // AfterPotionUsed: ReptileTrinket grants temporary and permanent Strength.
    battle = OrbBattle();
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = reptile };
    x = new Exec(ref battle, rules, action);
    HookDispatch.AfterPotionUsed(ref x, 0);
    Check(Kernel.GetAmount(battle, Limits.PlayerOwner, reptilePower) == 3,
        "爬行动物饰品应赋予 3 层临时力量");
    Check(Kernel.GetAmount(battle, Limits.PlayerOwner, strength) == 3,
        "临时力量首次施加应同时获得 3 力量");
}

// MB: contracts for the fixed-option choice protocol (Knowledge Demon's curse), the instanced
// targeted SandpitPower (exact-instance decrement), and EntropicBrew's out-of-combat pool with the
// freeFromEntropic slot mask and generated-use counter.
static void CheckMbKernel(Rules battleRules)
{
    Check(Registry.TryPower("SANDPIT_POWER", out ushort sandpit), "缺少 SANDPIT_POWER");
    Battle battle = NewBattle();
    PlayerAction noChoice = default;
    Exec x = new(ref battle, battleRules, noChoice);
    Check(Kernel.ApplyPowerTargeted(ref x, Limits.FirstEnemyOwner, Limits.PlayerOwner,
        sandpit, 4, Limits.FirstEnemyOwner) == 4, "沙坑首次施加应为 4 层");
    Check(Kernel.ApplyPowerTargeted(ref x, Limits.FirstEnemyOwner, Limits.PlayerOwner,
        sandpit, 4, Limits.FirstEnemyOwner) == 4, "沙坑第二次施加应为独立实例");
    Check(Kernel.GetAmount(battle, Limits.FirstEnemyOwner, sandpit) == 4,
        "GetAmount 读取第一个沙坑实例应为 4 层");
    int first = Kernel.FindPower(battle, Limits.FirstEnemyOwner, sandpit);
    int last = Kernel.FindLastPower(battle, Limits.FirstEnemyOwner, sandpit);
    Check(first != last && battle.Powers[first].Target == Limits.PlayerOwner
        && battle.Powers[last].Target == Limits.PlayerOwner,
        "沙坑的两个实例都应记录玩家目标");
    Kernel.IncrementSandpitTargeting(ref x, Limits.PlayerOwner);
    Check(battle.Powers[first].Amount == 5 && battle.Powers[last].Amount == 4,
        "FranticEscape 应只增加第一个沙坑实例");
    Kernel.SetPowerAmountAt(ref battle, first, 1);
    Check(battle.Powers[first].Amount == 1 && battle.Powers[last].Amount == 4,
        "沙坑计数应只改访问到的实例");
    Kernel.SetPowerAmountAt(ref battle, first, 0);
    Check(battle.PowerCount == 1 && battle.Powers[0].Amount == 4,
        "沙坑归零应只移除该实例");

    // Fixed option list: exactly one bit is selected and the request carries the option values.
    Battle optionBattle = NewBattle();
    PlayerAction optionAction = PlayerAction.EndTurn().WithChoice(0b10);
    Exec optionExec = new(ref optionBattle, battleRules, optionAction);
    Span<Card> options = stackalloc Card[2];
    options[0] = Kernel.CreateCard(battleRules, Registry.CardId<Disintegration>());
    options[1] = Kernel.CreateCard(battleRules, Registry.CardId<MindRot>());
    Check(Kernel.ChooseOption(ref optionExec, ChoiceEffect.ApplyKnowledgeCurse, options, out int selected),
        "固定选项选择应被动作满足");
    Check(selected == 1 && !optionExec.NeedsChoice, "固定选项应选中第二个选项");
    Exec missing = new(ref optionBattle, battleRules, default);
    Check(!Kernel.ChooseOption(ref missing, ChoiceEffect.ApplyKnowledgeCurse, options, out _),
        "动作没有选择时固定选项应返回 NeedsChoice");
    Check(missing.NeedsChoice && missing.Request.Kind == ChoiceKind.SelectOption
        && missing.Request.OptionCount == 2 && missing.Request.MinCount == 1 && missing.Request.MaxCount == 1,
        "固定选项请求应为 2 项单选");
    Exec invalid = new(ref optionBattle, battleRules, PlayerAction.EndTurn().WithChoice(0b11));
    bool rejected = false;
    try
    {
        Kernel.ChooseOption(ref invalid, ChoiceEffect.ApplyKnowledgeCurse, options, out _);
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }
    Check(rejected, "固定选项选择两个或零个选项应被拒绝");

    // EntropicBrew: out-of-combat pool generation fills an open slot with freeFromEntropic set; using
    // that generated potion records one generated use and clears the slot's free bit.
    Rules rules = MbPotionRules();
    Check(Registry.TryPotion("ENTROPIC_BREW", out ushort brew), "缺少 ENTROPIC_BREW");
    Check(Registry.TryPotion("FIRE_POTION", out ushort fire), "缺少 FIRE_POTION");
    Battle potionBattle = NewBattle();
    potionBattle.PotionSlotCount = 1;
    potionBattle.Potions[0] = (ushort)(brew + 1);
    // The pool has a single Common potion; this recorded state's first rarity roll is 0.875.
    potionBattle.RngStream(RngStream.CombatPotionGeneration) = RngState.FromSerializable(
        0, 0x161922C645CE50E8, 0xAD760CAFA1697B60, 0x3501FF44902CA50D, 0x417CB9A826D831DF);
    Kernel.Step(ref potionBattle, PlayerAction.UsePotion(0, Limits.NoOwner), rules, out _);
    Check(potionBattle.Potions[0] == (ushort)(fire + 1) && potionBattle.Potions[0] != brew + 1,
        "EntropicBrew 应把随机战斗外药水放入空槽");
    Check((potionBattle.Counter(Counter.EntropicFreePotionMask) & 0b1) != 0,
        "EntropicBrew 生成的药水槽应标记为 free");
    int enemyHp = potionBattle.Enemies[0].Creature.CurrentHp;
    Kernel.Step(ref potionBattle, PlayerAction.UsePotion(0, Limits.FirstEnemyOwner), rules, out _);
    Check(potionBattle.Potions[0] == 0 && potionBattle.Counter(Counter.GeneratedPotionUseCount) == 1,
        "使用免费药水应记录一次生成药水使用");
    Check(potionBattle.Counter(Counter.GeneratedPotionStrategicCost) == 0,
        "免费药水不应计入生成药水战略成本");
    Check(potionBattle.Counter(Counter.EntropicFreePotionMask) == 0, "使用后应清除免费位");
    Check(potionBattle.Enemies[0].Creature.CurrentHp < enemyHp, "免费药水效果应正常结算");
}

static Rules MbPotionRules()
{
    RulesDraft builder = new() { BaseMaxEnergy = 3, HasEncounter = true, BaseOrbSlotCount = 0 };
    Check(Registry.TryPotion("ENTROPIC_BREW", out ushort brew), "缺少 ENTROPIC_BREW");
    Check(Registry.TryPotion("FIRE_POTION", out ushort fire), "缺少 FIRE_POTION");
    builder.SetPotion(brew, new PotionStats
    {
        Target = TargetType.Self, Rarity = PotionRarity.Rare, StrategicCost = 0,
        Vars = [], VarProps = [],
    });
    builder.SetPotion(fire, new PotionStats
    {
        Target = TargetType.AnyEnemy, Rarity = PotionRarity.Common, StrategicCost = 9,
        Vars = [20m], VarProps = [ValueProps.None],
    });
    builder.SetPotionPool([fire]);
    builder.SetOutOfCombatPotionPool([fire]);
    builder.SetRootPotion(0, brew);
    Rules built = builder.Build();
    return built;
}

static Rules SliceRules()
{
    RulesDraft builder = new()
    {
        BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first", "second", "third"], BaseOrbSlotCount = 3,
        // The CX contracts install PerfectFit/SlumberingEssence on hand/discard cards.
        HasShuffleOrderEnchantment = true, HasBeforeFlushEnchantment = true,
    };
    Registry.TryRelic("CRACKED_CORE", out ushort crackedCore);
    builder.SetRelicVars(crackedCore, [1m]);
    Registry.TryRelic("RUNIC_CAPACITOR", out ushort runicCapacitor);
    builder.SetRelicVars(runicCapacitor, [3m]);
    Registry.TryRelic("SYMBIOTIC_VIRUS", out ushort symbioticVirus);
    builder.SetRelicVars(symbioticVirus, [1m]);
    Registry.TryRelic("REPTILE_TRINKET", out ushort reptile);
    builder.SetRelicVars(reptile, [3m]);
    Registry.TryRelic("LIZARD_TAIL", out ushort lizardTail);
    builder.SetRelicVars(lizardTail, [50m]);
    Registry.TryCard("STRIKE_IRONCLAD", out ushort strike);
    Registry.TryCard("DEFEND_IRONCLAD", out ushort defend);
    Registry.TryCard("BASH", out ushort bash);
    Registry.TryCard("WHIRLWIND", out ushort whirlwind);
    Registry.TryCard("VOID", out ushort voidCard);
    builder.SetCard(voidCard, 0, Stats(CardType.Status, TargetType.None, 0, [1m]));
    Registry.TryCard("THRASH", out ushort thrash);
    builder.SetCard(strike, 0, Stats(CardType.Attack, TargetType.AnyEnemy, 1, [6m]));
    builder.SetCard(thrash, 0, Stats(CardType.Attack, TargetType.AnyEnemy, 1, [4m]));
    builder.SetCard(thrash, 1, Stats(CardType.Attack, TargetType.AnyEnemy, 1, [6m]));
    builder.HasDecimalCardVars = true;
    builder.SetCard(defend, 0, Stats(CardType.Skill, TargetType.Self, 1, [5m]));
    builder.SetCard(bash, 0, Stats(CardType.Attack, TargetType.AnyEnemy, 2, [8m, 2m]));
    builder.SetCard(whirlwind, 0, Stats(CardType.Attack, TargetType.AllEnemies, 0, [5m], costsX: true));
    Registry.TryCard("FETCH", out ushort fetch);
    builder.SetCard(fetch, 0, Stats(CardType.Attack, TargetType.AnyEnemy, 0, [3m, 1m]));
    // DX3: Dampen's level-0 rules and the Rampage accumulator restore.
    Registry.TryCard("RAMPAGE", out ushort rampage);
    builder.SetCard(rampage, 0, Stats(CardType.Attack, TargetType.AnyEnemy, 1, [10m, 5m]));
    builder.SetCard(rampage, 1, Stats(CardType.Attack, TargetType.AnyEnemy, 1, [10m, 8m]));
    // RG: the forge kernel creates SOVEREIGN_BLADE; the star-cost contract uses a synthetic card.
    Registry.TryCard("SOVEREIGN_BLADE", out ushort blade);
    builder.SetCard(blade, 0, Stats(CardType.Attack, TargetType.AnyEnemy, 2, [10m]));
    builder.SetCard(blade, 1, Stats(CardType.Attack, TargetType.AnyEnemy, 2, [13m]));
    Registry.TryCard("SUMMON_FORTH", out ushort summonForthCard);
    builder.SetCard(summonForthCard, 0, Stats(CardType.Skill, TargetType.Self, 1, [8m]));
    builder.SetCard(summonForthCard, 1, Stats(CardType.Skill, TargetType.Self, 1, [11m]));
    // M2c: temporary-Dexterity ordering, Omnislice's AttackContext counter and the three powers
    // whose native Target is null.
    Registry.TryCard("ANTICIPATE", out ushort anticipate);
    builder.SetCard(anticipate, 0, Stats(CardType.Skill, TargetType.Self, 0, [2m]));
    Registry.TryCard("OMNISLICE", out ushort omnislice);
    builder.SetCard(omnislice, 0, Stats(CardType.Attack, TargetType.AnyEnemy, 0, [8m]));
    Registry.TryCard("THE_BOMB", out ushort theBomb);
    builder.SetCard(theBomb, 0, Stats(CardType.Skill, TargetType.Self, 1, [3m, 40m]));
    Registry.TryCard("PANACHE", out ushort panache);
    builder.SetCard(panache, 0, Stats(CardType.Power, TargetType.Self, 1, [10m]));
    Registry.TryCard("TORIC_TOUGHNESS", out ushort toric);
    builder.SetCard(toric, 0, Stats(CardType.Skill, TargetType.Self, 1, [3m, 10m]));
    Registry.TryRelic("BELT_BUCKLE", out ushort beltBuckle);
    builder.SetRelicVars(beltBuckle, [2m]);
    Registry.TryCard("RG_TEST_STAR_CARD", out ushort starCard);
    builder.SetCard(starCard, 0, Stats(CardType.Skill, TargetType.Self, 0, [0m], starCost: 2));
    // LR: choice, transform, history-counter and VoidForm cards used by the K0 contracts.
    void LrCard(string id, CardType type, TargetType target, int cost, decimal[] vars,
        int starCost = -1)
    {
        Registry.TryCard(id, out ushort card);
        builder.SetCard(card, 0, Stats(type, target, cost, vars, starCost: starCost));
        builder.SetCard(card, 1, Stats(type, target, cost, vars, starCost: starCost));
    }
    LrCard("TYRANNY", CardType.Power, TargetType.Self, 1, []);
    LrCard("FOREGONE_CONCLUSION", CardType.Skill, TargetType.Self, 1, [2m]);
    LrCard("CHARGE", CardType.Skill, TargetType.Self, 1, [2m]);
    LrCard("GUARDS", CardType.Skill, TargetType.Self, 2, []);
    LrCard("BEGONE", CardType.Skill, TargetType.Self, 1, []);
    LrCard("HEIRLOOM_HAMMER", CardType.Attack, TargetType.AnyEnemy, 2, [20m, 1m]);
    LrCard("DECISIONS_DECISIONS", CardType.Skill, TargetType.Self, 0, [3m, 3m], starCost: 6);
    LrCard("MINION_DIVE_BOMB", CardType.Attack, TargetType.AnyEnemy, 0, [13m]);
    LrCard("MINION_SACRIFICE", CardType.Skill, TargetType.Self, 0, [7m]);
    LrCard("MINION_STRIKE", CardType.Attack, TargetType.AnyEnemy, 0, [6m, 1m]);
    LrCard("SUPERMASSIVE", CardType.Attack, TargetType.AnyEnemy, 1, [5m, 3m]);
    LrCard("BEAT_INTO_SHAPE", CardType.Attack, TargetType.AnyEnemy, 1, [5m, 5m, 5m]);
    LrCard("KINGLY_KICK", CardType.Attack, TargetType.AnyEnemy, 4, [27m]);
    LrCard("KINGLY_PUNCH", CardType.Attack, TargetType.AnyEnemy, 1, [8m, 4m]);
    LrCard("VOID_FORM", CardType.Power, TargetType.Self, 3, [2m]);
    // TS: the two turn-start choice sources used by the turn-setup fixtures. ToastyMittens exhausts
    // one hand card then gains Strength; ChoicesParadox offers a generated character card option set.
    Registry.TryRelic("TOASTY_MITTENS", out ushort toastyMittens);
    builder.SetRelicVars(toastyMittens, [2m]);
    Registry.TryRelic("CHOICES_PARADOX", out ushort choicesParadox);
    builder.SetRelicVars(choicesParadox, [3m]);
    builder.SetCardPool(CardPools.CharacterAll, [strike, defend, bash]);

    // MB: Knowledge Demon's fixed curse options only need canonical card values.
    Registry.TryCard("DISINTEGRATION", out ushort disintegration);
    builder.SetCard(disintegration, 0, Stats(CardType.Status, TargetType.Self, -1, []));
    Registry.TryCard("MIND_ROT", out ushort mindRot);
    builder.SetCard(mindRot, 0, Stats(CardType.Status, TargetType.Self, -1, []));
    // CX: a 0-cost skill whose OnPlay removes another hand card. The Imbued contract uses it to make a
    // captured card disappear before its auto-play turn, which must be an explicit boundary.
    Registry.TryCard("CX_TEST_IMBUED_REMOVER", out ushort cxRemover);
    builder.SetCard(cxRemover, 0, Stats(CardType.Skill, TargetType.Self, 0, []));
    Registry.TryPower("VULNERABLE_POWER", out ushort vulnerable);
    builder.SetPowerVars(vulnerable, [1.5m]);
    Registry.TryMonster("NIBBIT", out ushort nibbit);
    builder.RootEnemyCount = 1;
    builder.EncounterSlots = ["first", "second", "third"];
    builder.SetMonster(0, new MonsterRules
    {
        MonsterId = nibbit,
        Nodes =
        [
            new MonsterNode { NativeId = "INIT_MOVE", Kind = MonsterNodeKind.ConditionalBranch, ConditionalTarget = 1 },
            new MonsterNode { NativeId = "BUTT_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 2, Attacks = [new(12, 1)] },
            new MonsterNode { NativeId = "SLICE_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 3, Attacks = [new(6, 1)] },
            new MonsterNode { NativeId = "HISS_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 1 },
            new MonsterNode
            {
                NativeId = "STUNNED", Kind = MonsterNodeKind.Move, MustPerformOnceBeforeTransitioning = true,
            },
        ],
        InitialNode = 0,
        PendingInitialMoveNode = 1,
        StunnedNode = 4,
        InitialHpMin = 17,
        InitialHpMax = 21,
        StaticInts = [5, 2],
        InitialAiRegisters = [2, 0],
    });
    return builder.Build();

    static CardStats Stats(CardType type, TargetType target, int cost, decimal[] vars,
        bool costsX = false, int starCost = -1) => new()
    {
        Type = type, Target = target, Rarity = CardRarity.Basic, EnergyCost = cost, CostsX = costsX,
        StarCost = starCost, StarCostsX = false, Keywords = Keywords.None, MaxUpgradeLevel = 1, Vars = vars,
    };
}



// ID: card-instance ledgers and instance powers. Monologue's instance Strength, SwordSage's per-blade
// applied replay bonus, Nightmare's detached selection, Orbit's energy remainder and the
// UnsettlingLamp latch are the stable-boundary contract; Fetch's played bit and Vambrace's trigger are
// per-card/per-play values and are asserted through their kernel setters.
static void CheckIdKernel(Rules rules)
{
    Check(Registry.TryCard("SOVEREIGN_BLADE", out ushort blade), "缺少 SOVEREIGN_BLADE 定义");
    Check(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "缺少 STRIKE_IRONCLAD 定义");
    Check(Registry.TryCard("DEFEND_IRONCLAD", out ushort defend), "缺少 DEFEND_IRONCLAD 定义");
    Check(Registry.TryCard("FETCH", out ushort fetch), "缺少 FETCH 定义");
    Check(Registry.TryPower("MONOLOGUE_POWER", out ushort monologue), "缺少 MONOLOGUE_POWER");
    Check(Registry.TryPower("STRENGTH_POWER", out ushort strength), "缺少 STRENGTH_POWER");
    Check(Registry.TryPower("SWORD_SAGE_POWER", out ushort swordSage), "缺少 SWORD_SAGE_POWER");
    Check(Registry.TryPower("NIGHTMARE_POWER", out ushort nightmare), "缺少 NIGHTMARE_POWER");
    Check(Registry.TryPower("ORBIT_POWER", out ushort orbit), "缺少 ORBIT_POWER");
    Check(Registry.TryPower("DRAW_CARDS_NEXT_TURN_POWER", out ushort drawNext), "缺少 DRAW_CARDS_NEXT_TURN_POWER");
    Check(Registry.TryPower("WEAK_POWER", out ushort weak), "缺少 WEAK_POWER");
    Check(Registry.TryPower("VULNERABLE_POWER", out ushort vulnerable), "缺少 VULNERABLE_POWER");
    Check(Registry.TryRelic("UNSETTLING_LAMP", out ushort lamp), "缺少 UNSETTLING_LAMP");

    // Power application target: the native Monologue/Nightmare/Orbit bodies all use PowerCmd.Apply,
    // which does not write PowerModel.Target. The kernel row must stay NoOwner.
    Check(Registry.TryCard("MONOLOGUE", out ushort monologueCard), "缺少 MONOLOGUE 定义");
    Check(Registry.TryCard("NIGHTMARE", out ushort nightmareCard), "缺少 NIGHTMARE 定义");
    Check(Registry.TryCard("ORBIT", out ushort orbitCard), "缺少 ORBIT 定义");
    CardStats TargetStats(decimal[] vars) => new()
    {
        Type = CardType.Skill, Target = TargetType.Self, Rarity = CardRarity.Uncommon, EnergyCost = 0,
        CostsX = false, StarCost = -1, StarCostsX = false, Keywords = Keywords.None,
        MaxUpgradeLevel = 1, Vars = vars,
    };
    // Call the card definition directly: the KernelChecks rules table intentionally carries only the
    // fixture cards, not every identity card. This still exercises the exact OnPlay/OwnChoice body.
    Battle monologueCardBattle = NewBattle();
    Exec monologueCardExec = new(ref monologueCardBattle, rules, PlayerAction.EndTurn());
    monologueCardExec.Card = new Card { Id = monologueCard };
    monologueCardExec.Stats = TargetStats([1m]);
    Registry.Card(monologueCard).OnPlay(ref monologueCardExec);
    int monologueCardRow = Kernel.FindLastPower(monologueCardBattle, Limits.PlayerOwner, monologue);
    Check(monologueCardRow >= 0
          && monologueCardBattle.Powers[monologueCardRow].Target == Limits.NoOwner,
        "Monologue 的 Power 行应保持 Target=NoOwner");

    Battle nightmareCardBattle = NewBattle();
    Piles.Append(ref nightmareCardBattle, Pile.Hand, Kernel.CreateCard(rules, strike));
    Exec nightmareCardExec = new(ref nightmareCardBattle, rules, PlayerAction.EndTurn().WithChoice(1UL));
    nightmareCardExec.Card = new Card { Id = nightmareCard };
    nightmareCardExec.Stats = TargetStats([0m]);
    Registry.Card(nightmareCard).OwnChoice(ref nightmareCardExec);
    int nightmareCardRow = Kernel.FindLastPower(nightmareCardBattle, Limits.PlayerOwner, nightmare);
    Check(nightmareCardRow >= 0
          && nightmareCardBattle.Powers[nightmareCardRow].Target == Limits.NoOwner,
        "Nightmare 的 Power 行应保持 Target=NoOwner");

    Battle orbitCardBattle = NewBattle();
    Exec orbitCardExec = new(ref orbitCardBattle, rules, PlayerAction.EndTurn());
    orbitCardExec.Card = new Card { Id = orbitCard };
    orbitCardExec.Stats = TargetStats([2m]);
    Registry.Card(orbitCard).OnPlay(ref orbitCardExec);
    int orbitCardRow = Kernel.FindLastPower(orbitCardBattle, Limits.PlayerOwner, orbit);
    Check(orbitCardRow >= 0 && orbitCardBattle.Powers[orbitCardRow].Target == Limits.NoOwner,
        "Orbit 的 Power 行应保持 Target=NoOwner");

    // Monologue: two instances keep separate Strength/StrengthApplied rows; a play applies both and the
    // side turn end removes each and reverts its applied Strength.
    Battle monologueBattle = NewBattle();
    Piles.Append(ref monologueBattle, Pile.Hand, Kernel.CreateCard(rules, strike));
    Exec monologueExec = new(ref monologueBattle, rules, default);
    Kernel.ApplyPowerPreservingTarget(ref monologueExec, Limits.PlayerOwner, monologue, 1,
        Limits.PlayerOwner, Limits.PlayerOwner);
    Kernel.ApplyPowerPreservingTarget(ref monologueExec, Limits.PlayerOwner, monologue, 1,
        Limits.PlayerOwner, Limits.PlayerOwner);
    Check(monologueBattle.PowerCount == 2, "Monologue 应有两个实例行");
    monologueBattle.Powers[0].Aux0 = 3;
    monologueBattle.Powers[1].Aux0 = 5;
    Kernel.ManualPlay(ref monologueExec, 0, Kernel.EnemyOwner(0));
    Check(Kernel.GetAmount(monologueBattle, Limits.PlayerOwner, strength) == 8,
        "Monologue 应在出牌后施加两个实例的 Strength");
    Check(monologueBattle.Powers[0].Aux1 == 3 && monologueBattle.Powers[1].Aux1 == 5,
        "Monologue 的 StrengthApplied 应按实例累计");
    // EndTurnPowerSupport.Batch048 visits each instance; removing the first shifts the second to row 0.
    PowerDef monologueDef = Registry.Power(monologue);
    monologueDef.EndTurnBatch048(ref monologueExec, new Listener(ListenerKind.Power, 0,
        Limits.PlayerOwner), Side.Player, participates: true);
    monologueDef.EndTurnBatch048(ref monologueExec, new Listener(ListenerKind.Power, 0,
        Limits.PlayerOwner), Side.Player, participates: true);
    Check(Kernel.GetAmount(monologueBattle, Limits.PlayerOwner, monologue) == 0
          && Kernel.GetAmount(monologueBattle, Limits.PlayerOwner, strength) == 0,
        "Monologue 应在玩家回合末移除并归还 Strength");

    // SwordSage: the per-blade applied bonus is a card value; normalization follows the power amount.
    Battle sageBattle = NewBattle();
    Card sageBlade = Kernel.CreateCard(rules, blade);
    Piles.Append(ref sageBattle, Pile.Hand, sageBlade);
    Exec sageExec = new(ref sageBattle, rules, default);
    Kernel.ApplyPower(ref sageExec, Limits.PlayerOwner, swordSage, 2, Limits.PlayerOwner);
    Kernel.NormalizeCardAfflictions(ref sageExec);
    Check(sageBattle.Cards[0].ReplayCount == 2 && sageBattle.Cards[0].Private == 2,
        $"SwordSage 应把重放加到 2 并记录已应用值，实际 {sageBattle.Cards[0].ReplayCount}/{sageBattle.Cards[0].Private}");
    Kernel.SetPowerAmount(ref sageBattle, Limits.PlayerOwner, swordSage, 0);
    Kernel.NormalizeCardAfflictions(ref sageExec);
    Check(sageBattle.Cards[0].ReplayCount == 0 && sageBattle.Cards[0].Private == 0,
        "SwordSage 移除应回退已应用的重放");
    Card sageClone = Kernel.CloneCard(sageBattle.Cards[0]);
    Check(sageClone.Private == Limits.UninitializedLedger,
        "克隆卡应重置实例账本与 Fetch 位");

    // Nightmare: the selected card lives in DetachedCards; BeforeHandDraw clones it Amount times,
    // removes the instance and frees the slot.
    Battle nightmareBattle = NewBattle();
    Exec nightmareExec = new(ref nightmareBattle, rules, default);
    Kernel.ApplyPowerPreservingTarget(ref nightmareExec, Limits.PlayerOwner, nightmare, 3,
        Limits.PlayerOwner, Limits.PlayerOwner);
    Card selected = Kernel.CreateCard(rules, strike);
    int slot = Kernel.AllocateDetachedCard(ref nightmareBattle, selected);
    nightmareBattle.Powers[0].Aux0 = slot + 1;
    Kernel.BeforeHandDraw(ref nightmareExec);
    Check(nightmareBattle.HandCount == 3, $"Nightmare 应生成 3 张选定卡的克隆，实际手牌 {nightmareBattle.HandCount}");
    Check(Kernel.GetAmount(nightmareBattle, Limits.PlayerOwner, nightmare) == 0,
        "Nightmare 结算后应移除实例");
    Check(nightmareBattle.DetachedCards[slot].Id == 0, "Nightmare 实例移除应释放 detached 槽位");
    for (int index = 0; index < nightmareBattle.HandCount; index++)
    {
        Check(nightmareBattle.Cards[index].Id == strike && nightmareBattle.Cards[index].IsClone,
            "Nightmare 生成的卡应是选定卡的克隆");
    }

    // Orbit: spent energy accumulates in Aux0; each full 4 grants Amount energy.
    Battle orbitBattle = NewBattle();
    orbitBattle.Energy = 0;
    Exec orbitExec = new(ref orbitBattle, rules, default);
    Kernel.ApplyPowerPreservingTarget(ref orbitExec, Limits.PlayerOwner, orbit, 2,
        Limits.PlayerOwner, Limits.PlayerOwner);
    Card energyCard = Kernel.CreateCard(rules, strike);
    HookDispatch.AfterEnergySpent(ref orbitExec, energyCard, 3);
    Check(orbitBattle.Powers[0].Aux0 == 3 && orbitBattle.Energy == 0,
        $"Orbit 3 点能量应只留下余数 3，实际余数 {orbitBattle.Powers[0].Aux0} 能量 {orbitBattle.Energy}");
    HookDispatch.AfterEnergySpent(ref orbitExec, energyCard, 1);
    Check(orbitBattle.Powers[0].Aux0 == 0 && orbitBattle.Energy == 2,
        $"Orbit 满 4 点应给 2 能量，实际余数 {orbitBattle.Powers[0].Aux0} 能量 {orbitBattle.Energy}");

    // PaleBlueDot: the fifth completed play latches and grants next-turn draws once.
    Battle dotBattle = NewBattle();
    dotBattle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref dotBattle, Pile.Hand, Kernel.CreateCard(rules, strike));
    dotBattle.SetCounter(Counter.CardsPlayedThisTurn, 4);
    Exec dotExec = new(ref dotBattle, rules, default);
    Kernel.ApplyPower(ref dotExec, Limits.PlayerOwner,
        Registry.PowerId<PaleBlueDotPower>(), 2, Limits.PlayerOwner);
    Kernel.ManualPlay(ref dotExec, 0, Kernel.EnemyOwner(0));
    Check(dotBattle.Powers[0].Aux0 == 1, "PaleBlueDot 第五次出牌应锁存");
    Check(Kernel.GetAmount(dotBattle, Limits.PlayerOwner, drawNext) == 2,
        "PaleBlueDot 应给出 2 层下回合抽牌");

    // UnsettlingLamp: the first card-sourced debuff is doubled once; the internal temporary type is
    // exempt; completing the card power scope latches and clears the transient trigger.
    Battle lampBattle = NewBattle();
    lampBattle.Flags |= BattleFlags.PlayerHooksActive;
    lampBattle.RelicCount = 1;
    lampBattle.Relics[0] = new Relic { Id = lamp };
    Exec lampExec = new(ref lampBattle, rules, default);
    lampExec.CardPowerSourceActive = true;
    lampExec.CardPowerSourceSerial = ++lampExec.CardPowerSourceSerialCounter;
    int weakApplied = Kernel.ApplyPower(ref lampExec, Kernel.EnemyOwner(0), weak, 1,
        Limits.PlayerOwner);
    Check(weakApplied == 2, $"UnsettlingLamp 应把首个减益翻倍，实际 {weakApplied}");
    int vulnerableApplied = Kernel.ApplyPower(ref lampExec, Kernel.EnemyOwner(0), vulnerable, 1,
        Limits.PlayerOwner);
    Check(vulnerableApplied == 2, $"同一张牌的第二个减益也应翻倍，实际 {vulnerableApplied}");
    Kernel.CompleteCardPowerSource(ref lampExec);
    Check(lampBattle.Relics[0].Counter == 1 && lampExec.LampTriggerSerial == 0,
        "UnsettlingLamp 完成卡牌 Power 作用域后应锁存并清空触发");
    lampExec.CardPowerSourceSerial = ++lampExec.CardPowerSourceSerialCounter;
    int weakAgain = Kernel.ApplyPower(ref lampExec, Kernel.EnemyOwner(0), weak, 1,
        Limits.PlayerOwner);
    Check(weakAgain == 1, $"已锁存的 UnsettlingLamp 不应再翻倍，实际 {weakAgain}");

    // Fetch: completing a play sets the per-card bit; a clone starts clear.
    Battle fetchBattle = NewBattle();
    Piles.Append(ref fetchBattle, Pile.Hand, Kernel.CreateCard(rules, fetch));
    Exec fetchExec = new(ref fetchBattle, rules, default);
    Kernel.ManualPlay(ref fetchExec, 0, Kernel.EnemyOwner(0));
    int fetchPosition = Piles.Start(fetchBattle, Pile.Discard);
    Check(fetchBattle.DiscardCount == 1 && fetchBattle.Cards[fetchPosition].Id == fetch,
        "Fetch 应完成并进入弃牌堆");
    Check((fetchBattle.Cards[fetchPosition].Private & Kernel.FetchPlayedBit) != 0,
        "Fetch 完成一次出牌后应置本回合已打出位");
    Check((Kernel.CloneCard(fetchBattle.Cards[fetchPosition]).Private & Kernel.FetchPlayedBit) == 0,
        "Fetch 的克隆不应继承已打出位");

    // Vambrace: the first card-sourced block of the combat is doubled; the relic latches on the
    // matching AfterCardPlayed and never doubles again.
    Check(Registry.TryRelic("VAMBRACE", out ushort vambrace), "缺少 VAMBRACE");
    Battle vambraceBattle = NewBattle();
    vambraceBattle.Flags |= BattleFlags.PlayerHooksActive;
    vambraceBattle.RelicCount = 1;
    vambraceBattle.Relics[0] = new Relic { Id = vambrace };
    Exec vambraceExec = new(ref vambraceBattle, rules, default);
    vambraceExec.Card = Kernel.CreateCard(rules, defend);
    decimal doubled = Kernel.GainBlock(ref vambraceExec, Limits.PlayerOwner, 5m, ValueProps.Move,
        fromCard: true);
    Check(doubled == 10m && vambraceBattle.Player.Block == 10,
        $"Vambrace 应把首次格挡翻倍，实际 {doubled}/{vambraceBattle.Player.Block}");
    Check(vambraceExec.VambraceTriggerActive, "Vambrace 应记录触发卡");
}

// LS: contracts for the remaining Silent/Necrobinder/Defect kernel capabilities: the power
// amount-given pass, EchoingSlash attack waves, KnifeTrap's duplicate-Shiv auto-play and the
// drawn-card position assumption, Misery's temporary-power boundary, MomentumStrike's live play slot
// and HelicalDart's temporary Dexterity.
static void CheckLsKernel()
{
    Rules rules = LsRules();
    Check(Registry.TryCard("ECHOING_SLASH", out ushort echoing), "缺少 ECHOING_SLASH");
    Check(Registry.TryCard("KNIFE_TRAP", out ushort knifeTrap), "缺少 KNIFE_TRAP");
    Check(Registry.TryCard("SHIV", out ushort shiv), "缺少 SHIV");
    Check(Registry.TryCard("MOMENTUM_STRIKE", out ushort momentum), "缺少 MOMENTUM_STRIKE");
    Check(Registry.TryCard("MISERY", out ushort misery), "缺少 MISERY");
    Check(Registry.TryPower("POISON_POWER", out ushort poison), "缺少 POISON_POWER");
    Check(Registry.TryPower("PIERCING_WAIL_POWER", out ushort piercing), "缺少 PIERCING_WAIL_POWER");
    Check(Registry.TryPower("DEXTERITY_POWER", out ushort dexterity), "缺少 DEXTERITY_POWER");
    Check(Registry.TryPower("HELICAL_DART_POWER", out ushort helicalPower), "缺少 HELICAL_DART_POWER");
    Check(Registry.TryRelic("SNECKO_SKULL", out ushort skull), "缺少 SNECKO_SKULL");
    Check(Registry.TryRelic("HELICAL_DART", out ushort dart), "缺少 HELICAL_DART");

    // ModifyPowerAmountGiven: SneckoSkull adds 1 to Poison the player gives.
    Battle battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = skull };
    Exec x = new(ref battle, rules, default);
    int applied = Kernel.ApplyPower(ref x, 2, poison, 2, Limits.PlayerOwner);
    Check(applied == 3, $"SneckoSkull 应把毒 2 变成 3，实际 {applied}");
    Check(Kernel.GetAmount(battle, 2, poison) == 3, "SneckoSkull 后的毒层数应为 3");

    // EchoingSlash: the first wave kills one enemy, which adds a second wave.
    battle = NewBattle();
    battle.EnemyCount = 2;
    battle.RosterCount = 2;
    battle.Roster[1] = 1;
    battle.Enemies[0].Creature.CurrentHp = 5;
    battle.Enemies[0].Creature.MaxHp = 5;
    battle.Enemies[1].Creature.CurrentHp = 100;
    battle.Enemies[1].Creature.MaxHp = 100;
    battle.Enemies[1].Flags = EnemyFlags.InCombat;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, echoing));
    x = new(ref battle, rules, default);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Check(battle.Enemies[0].Creature.IsDead, "EchoingSlash 第一波应击杀低血量敌人");
    Check(battle.Enemies[1].Creature.CurrentHp == 80,
        $"EchoingSlash 击杀后应追加一波，实际敌人生命 {battle.Enemies[1].Creature.CurrentHp}");
    Check(battle.Counter(Counter.PlayerAttacksThisTurn) == 0,
        "EchoingSlash 使用 AttackContext，不应增加本回合攻击次数");

    // KnifeTrap: two byte-equal Shivs in the exhaust pile are both auto-played (duplicate copies
    // must not make the second search pick the first again).
    battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, knifeTrap));
    Piles.Append(ref battle, Pile.Exhaust, Kernel.CreateCard(rules, shiv));
    Piles.Append(ref battle, Pile.Exhaust, Kernel.CreateCard(rules, shiv));
    x = new(ref battle, rules, default);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, 2), rules, out _);
    Check(battle.Enemies[0].Creature.CurrentHp == 92,
        $"KnifeTrap 应自动打出两张 Shiv（12 伤害），实际生命 {battle.Enemies[0].Creature.CurrentHp}");
    Check(battle.ExhaustCount == 2, "两张 Shiv 打出后应回到消耗堆");

    // Upgraded KnifeTrap upgrades every exhausted Shiv before playing it.
    battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, knifeTrap, 1));
    Piles.Append(ref battle, Pile.Exhaust, Kernel.CreateCard(rules, shiv));
    Piles.Append(ref battle, Pile.Exhaust, Kernel.CreateCard(rules, shiv));
    x = new(ref battle, rules, default);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, 2), rules, out _);
    for (int index = 0; index < battle.ExhaustCount; index++)
    {
        Card card = battle.Cards[Piles.Start(battle, Pile.Exhaust) + index];
        Check(card.Id == shiv && card.Upgrade == 1, "升级版 KnifeTrap 应把每张 Shiv 升级");
    }

    // Burst adds one replay to the next Skill; the block card counter and block gain both run twice.
    battle = NewBattle();
    battle.Energy = 9;
    Check(Registry.TryCard("BURST", out ushort burstCard), "缺少 BURST");
    Check(Registry.TryCard("MIRAGE", out ushort mirageCard), "缺少 MIRAGE");
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, burstCard));
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, mirageCard));
    x = new(ref battle, rules, default);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Check(Kernel.GetAmount(battle, 0, Registry.PowerId<BurstPower>()) == 1,
        $"Burst 应施加 1 层 BurstPower，实际 {Kernel.GetAmount(battle, 0, Registry.PowerId<BurstPower>())}");
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Check(battle.Counter(Counter.BlockCardsPlayedThisTurn) == 2,
        $"Burst 应重放 Mirage 两次，格挡牌计数实际 {battle.Counter(Counter.BlockCardsPlayedThisTurn)}");

    battle = NewBattle();
    battle.Energy = 9;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, burstCard));
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, mirageCard));
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, 0, dexterity, 2, 0);
    Kernel.ApplyPower(ref x, 0, Registry.PowerId<ShadowmeldPower>(), 1, 0);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Check(battle.Player.Block == 16, $"Burst+Mirage(2,dex,shadowmeld) 应为 16 格挡，实际 {battle.Player.Block}");

    // Misery clones a temporary debuff as its own power row (the native ApplyClonedPower skips the
    // temporary BeforeApplied) and leaves the internally applied stat alone when it is not among the
    // cloned debuffs (PiercingWail's Strength is a buff).
    battle = NewBattle();
    battle.Energy = 9;
    battle.EnemyCount = 2;
    battle.RosterCount = 2;
    battle.Roster[1] = 1;
    battle.Enemies[1].Creature.CurrentHp = 100;
    battle.Enemies[1].Creature.MaxHp = 100;
    battle.Enemies[1].Flags = EnemyFlags.InCombat;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, misery));
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, 2, piercing, 1, Limits.PlayerOwner);
    x = new(ref battle, rules, default);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, 2), rules, out _);
    Check(Kernel.GetAmount(battle, 3, piercing) == 1, "Misery 应把临时减益克隆到其他敌人");
    Check(Kernel.GetAmount(battle, 3, dexterity) == 0, "PiercingWail 的内部力量是增益，不应被合并");

    // MomentumStrike sets its own cost to 0 this combat; PlayPosition is valid during OnPlay.
    battle = NewBattle();
    battle.Energy = 9;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, momentum));
    x = new(ref battle, rules, default);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, 2), rules, out _);
    bool found = false;
    for (int index = 0; index < battle.DiscardCount; index++)
    {
        Card card = battle.Cards[Piles.Start(battle, Pile.Discard) + index];
        if (card.Id != momentum)
            continue;
        found = true;
        Check(card.EnergyModifierCount == 1, "MomentumStrike 应留下一条费用修饰");
        Check(Kernel.EnergyCostValueWithModifiers(battle, rules, card, Pile.Discard) == 0,
            "MomentumStrike 本场费用应为 0");
    }
    Check(found, "MomentumStrike 应进入弃牌堆");

    // HelicalDart: a Shiv play applies 1 temporary Dexterity.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = dart };
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, shiv));
    x = new(ref battle, rules, default);
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, 2), rules, out _);
    Check(Kernel.GetAmount(battle, 0, dexterity) == 1, "HelicalDart 应给 1 敏捷");
    Check(Kernel.GetAmount(battle, 0, helicalPower) == 1, "HelicalDart 应施加临时敏捷层");
}

static Rules LsRules()
{
    RulesDraft builder = new() { BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"], RootEnemyCount = 0 };
    Registry.TryCard("ECHOING_SLASH", out ushort echoing);
    Registry.TryCard("KNIFE_TRAP", out ushort knifeTrap);
    Registry.TryCard("SHIV", out ushort shiv);
    Registry.TryCard("MOMENTUM_STRIKE", out ushort momentum);
    Registry.TryCard("MISERY", out ushort misery);
    Registry.TryRelic("SNECKO_SKULL", out ushort skull);
    Registry.TryRelic("HELICAL_DART", out ushort dart);
    builder.SetCard(echoing, 0, LsStats(CardType.Attack, TargetType.AllEnemies, 1, [10m]));
    builder.SetCard(knifeTrap, 0, LsStats(CardType.Skill, TargetType.AnyEnemy, 1));
    builder.SetCard(knifeTrap, 1, LsStats(CardType.Skill, TargetType.AnyEnemy, 1));
    builder.SetCard(shiv, 0, LsStats(CardType.Attack, TargetType.AnyEnemy, 0, [4m],
        Keywords.Exhaust, CardTag.Shiv));
    builder.SetCard(shiv, 1, LsStats(CardType.Attack, TargetType.AnyEnemy, 0, [6m],
        Keywords.Exhaust, CardTag.Shiv));
    builder.SetCard(momentum, 0, LsStats(CardType.Attack, TargetType.AnyEnemy, 1, [11m]));
    builder.SetCard(misery, 0, LsStats(CardType.Attack, TargetType.AnyEnemy, 0, [7m]));
    Registry.TryCard("BURST", out ushort burst);
    Registry.TryCard("MIRAGE", out ushort mirage);
    builder.SetCard(burst, 0, LsStats(CardType.Skill, TargetType.Self, 1, [1m]));
    builder.SetCard(mirage, 0, LsStats(CardType.Skill, TargetType.Self, 1, [2m, 1m]));
    builder.SetRelicVars(skull, [1m]);
    builder.SetRelicVars(dart, [1m]);
    return builder.Build();

    static CardStats LsStats(CardType type, TargetType target, int cost, decimal[]? vars = null,
        Keywords keywords = Keywords.None, CardTag tags = CardTag.None) => new()
    {
        Type = type, Target = target, Rarity = CardRarity.Basic, EnergyCost = cost, CostsX = false,
        StarCost = -1, StarCostsX = false, Keywords = keywords, Tags = tags, MaxUpgradeLevel = 1,
        Vars = vars ?? [],
    };
}

// NB: contracts for the Necrobinder kernel capabilities. Osty's summon/revive ledger, the
// DieForYou redirect and spill-over, the Osty attack dealer, Doom's side-turn-end kill, the
// RightHandHand/Flatten/Flast cost-modifier hooks, EtherealPlays and the UnceasingTop special case.
static void CheckNbKernel()
{
    Rules rules = NbRules();
    Check(Registry.TryCard("STRIKE_NECROBINDER", out ushort strike), "缺少 STRIKE_NECROBINDER");
    Check(Registry.TryCard("DEFEND_NECROBINDER", out ushort defend), "缺少 DEFEND_NECROBINDER");
    Check(Registry.TryCard("BODYGUARD", out ushort bodyguard), "缺少 BODYGUARD");
    Check(Registry.TryCard("SOUL", out ushort soul), "缺少 SOUL");
    Check(Registry.TryCard("PARSE", out ushort parse), "缺少 PARSE");
    Check(Registry.TryCard("FLATTEN", out ushort flatten), "缺少 FLATTEN");
    Check(Registry.TryCard("HIGH_FIVE", out ushort highFive), "缺少 HIGH_FIVE");
    Check(Registry.TryCard("RIGHT_HAND_HAND", out ushort rightHandHand), "缺少 RIGHT_HAND_HAND");
    Check(Registry.TryPower("DIE_FOR_YOU_POWER", out ushort dieForYou), "缺少 DIE_FOR_YOU_POWER");
    Check(Registry.TryPower("DOOM_POWER", out ushort doom), "缺少 DOOM_POWER");
    Check(Registry.TryPower("OBLIVION_POWER", out ushort oblivion), "缺少 OBLIVION_POWER");
    Check(Registry.TryRelic("UNCEASING_TOP", out ushort unceasingTop), "缺少 UNCEASING_TOP");

    // Summon: the first call creates the pet at the requested HP, applies DieForYouPower 1 and consumes
    // one CombatId; a later summon raises both HP values; a dead pet is revived with the new amount.
    Battle battle = NewBattle();
    battle.NextCombatId = 7;
    Exec x = new(ref battle, rules, default);
    Kernel.SummonOsty(ref x, 5);
    Check((battle.Flags & BattleFlags.OstyPresent) != 0, "召唤后 Osty 应存在");
    Check(battle.Osty.CurrentHp == 5 && battle.Osty.MaxHp == 5, "首次召唤 HP 应为 5/5");
    Check(battle.NextCombatId == 8, "创建 Osty 应消耗一个 CombatId");
    Check(Kernel.GetAmount(battle, Limits.OstyOwner, dieForYou) == 1, "首次召唤应获得 1 层 DieForYouPower");
    x = new(ref battle, rules, default);
    Kernel.SummonOsty(ref x, 3);
    Check(battle.Osty.CurrentHp == 8 && battle.Osty.MaxHp == 8, "存活 Osty 召唤应提高最大生命并治疗");
    battle.Osty.CurrentHp = 0;
    x = new(ref battle, rules, default);
    Kernel.SummonOsty(ref x, 4);
    Check(battle.Osty.CurrentHp == 4 && battle.Osty.MaxHp == 4, "死亡 Osty 召唤应按新值复活");
    x = new(ref battle, rules, default);
    Kernel.HealOsty(ref x, 99);
    Check(battle.Osty.CurrentHp == 4, "治疗 Osty 不得超过最大生命");

    // Redirect: powered attack damage above Osty's HP spills over to the player; unpowered damage and
    // a dead pet do not redirect.
    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.SummonOsty(ref x, 8);
    battle.Player.CurrentHp = 50;
    Span<byte> player = stackalloc byte[1];
    player[0] = Limits.PlayerOwner;
    x = new(ref battle, rules, default);
    Kernel.Damage(ref x, player, 20m, ValueProps.Move, Limits.NoOwner, false, default);
    Check(battle.Osty.CurrentHp == 0, "Osty 应吸收其全部 HP 的伤害");
    Check(battle.Player.CurrentHp == 38, "Osty 死亡后的溢出伤害应打给玩家");
    Check((battle.Player.Flags & CreatureFlags.LostHpThisTurn) != 0, "溢出伤害应标记玩家本回合失血");
    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.SummonOsty(ref x, 8);
    battle.Player.CurrentHp = 50;
    x = new(ref battle, rules, default);
    Kernel.Damage(ref x, player, 20m, ValueProps.Move | ValueProps.Unpowered, Limits.NoOwner,
        false, default);
    Check(battle.Osty.CurrentHp == 8 && battle.Player.CurrentHp == 30, "Unpowered 伤害不得重定向到 Osty");

    // Osty attack: the pet is the dealer, so its attack history is recorded and the player's is not.
    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.SummonOsty(ref x, 8);
    x.Card = Kernel.CreateCard(rules, flatten);
    byte enemy = Kernel.EnemyOwner(0);
    x = new(ref battle, rules, default);
    Kernel.CardAttack(ref x, 6m, 1, AttackTargeting.Single, enemy, ValueProps.Move,
        Limits.OstyOwner);
    Check(battle.Enemies[0].Creature.CurrentHp == 94, "Osty 攻击应伤害目标");
    Check(battle.Counter(Counter.OstyAttacksThisTurn) == 1, "Osty 攻击应计入 Osty 本回合次数");
    Check(battle.Counter(Counter.PlayerAttacksThisTurn) == 0, "Osty 攻击不得计入玩家攻击次数");

    // Assumption contract: a single-target attack without a target owner is unsupported, not an
    // index crash. K0 pins the UnsupportedException.
    battle = NewBattle();
    x = new(ref battle, rules, default);
    bool missingTargetThrew = false;
    try
    {
        Kernel.CardAttack(ref x, 6m, 1, AttackTargeting.Single, Limits.NoOwner);
    }
    catch (UnsupportedException)
    {
        missingTargetThrew = true;
    }
    Check(missingTargetThrew, "单体攻击缺少目标应抛 UnsupportedException");

    // Doom: the enemy-side turn-end hook kills every doomed participant together.
    battle = NewBattle();
    battle.Enemies[0].Creature.CurrentHp = 20;
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, enemy, doom, 25, Limits.PlayerOwner);
    int doomIndex = Kernel.FindPower(battle, enemy, doom);
    Check(doomIndex >= 0, "Doom 应已施加");
    x = new(ref battle, rules, default);
    Listener doomListener = new(ListenerKind.Power, checked((byte)doomIndex), enemy);
    Registry.Power(doom).BeforeSideTurnEnd(ref x, doomListener, Side.Enemy,
        Kernel.ParticipantMask(enemy));
    Check(battle.Enemies[0].Creature.IsDead, "Doom 达到生命值应在敌方回合末击杀");
    Check(battle.RosterCount == 0, "被 Doom 击杀的敌人应离开名单");

    // RightHandHand: any play whose energy value reaches its Energy variable returns the copy from the
    // discard pile to hand.
    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.SummonOsty(ref x, 8);
    battle.Energy = 3;
    battle.Phase = PlayerPhase.Play;
    battle.Side = Side.Player;
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, highFive));
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, defend));
    Piles.Append(ref battle, Pile.Discard, Kernel.CreateCard(rules, rightHandHand));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Check(battle.HandCount == 2, "费用 >= RightHandHand 的 Energy 的打出应把弃牌堆的副本收回手牌");

    // Flatten: after an Osty attack every Flatten copy in a combat pile gets an absolute zero
    // EndOfTurn cost modifier.
    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.SummonOsty(ref x, 8);
    battle.Energy = 3;
    battle.Phase = PlayerPhase.Play;
    battle.Side = Side.Player;
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, flatten));
    Piles.Append(ref battle, Pile.Discard, Kernel.CreateCard(rules, flatten));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Kernel.EnemyOwner(0)), rules, out _);
    Check(battle.DiscardCount == 2, "Flatten 应打出并进入弃牌堆");
    int discardStart = Piles.Start(battle, Pile.Discard);
    Check(battle.Cards[discardStart].EnergyModifierCount == 1
        && battle.Cards[discardStart + 1].EnergyModifierCount == 1,
        "Osty 攻击后每张 Flatten 应各获得一个零费修饰");

    // EtherealPlays and Oblivion: playing an Ethereal Soul counts the play and applies the Oblivion
    // amount as Doom to its owner.
    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, enemy, oblivion, 5, Limits.PlayerOwner);
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, dieForYou, 1, Limits.NoOwner);
    battle.Energy = 1;
    battle.Phase = PlayerPhase.Play;
    battle.Side = Side.Player;
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, parse));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Check(battle.Counter(Counter.EtherealPlays) == 1, "打出 Ethereal 牌应计入 EtherealPlays");
    Check(Kernel.GetAmount(battle, enemy, doom) == 5, "Oblivion 应在卡牌打出后施加等量 Doom");

    // UnceasingTop: playing the last hand card with an empty hand and a non-empty draw pile draws one.
    battle = NewBattle();
    battle.Phase = PlayerPhase.Play;
    battle.Side = Side.Player;
    battle.Energy = 1;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = unceasingTop };
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, defend));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, strike));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Check(battle.HandCount == 1, "手牌打空后 UnceasingTop 应抽一张");
    Check(battle.Cards[0].Id == strike, "UnceasingTop 应抽到抽牌堆顶");
}

static Rules NbRules()
{
    RulesDraft builder = new() { BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"], RootEnemyCount = 1 };
    Registry.TryCard("STRIKE_NECROBINDER", out ushort strike);
    Registry.TryCard("DEFEND_NECROBINDER", out ushort defend);
    Registry.TryCard("BODYGUARD", out ushort bodyguard);
    Registry.TryCard("SOUL", out ushort soul);
    Registry.TryCard("PARSE", out ushort parse);
    Registry.TryCard("FLATTEN", out ushort flatten);
    Registry.TryCard("HIGH_FIVE", out ushort highFive);
    Registry.TryCard("RIGHT_HAND_HAND", out ushort rightHandHand);
    builder.SetCard(strike, 0, NbStats(CardType.Attack, TargetType.AnyEnemy, 1, [6m]));
    builder.SetCard(strike, 1, NbStats(CardType.Attack, TargetType.AnyEnemy, 1, [9m]));
    builder.SetCard(defend, 0, NbStats(CardType.Skill, TargetType.Self, 1, [5m]));
    builder.SetCard(defend, 1, NbStats(CardType.Skill, TargetType.Self, 1, [8m]));
    builder.SetCard(bodyguard, 0, NbStats(CardType.Skill, TargetType.Self, 1, [5m]));
    builder.SetCard(soul, 0, NbStats(CardType.Skill, TargetType.Self, 0, [2m], keywords: Keywords.Exhaust));
    builder.SetCard(parse, 0, NbStats(CardType.Skill, TargetType.Self, 1, [3m], keywords: Keywords.Ethereal));
    builder.SetCard(flatten, 0, NbStats(CardType.Attack, TargetType.AnyEnemy, 2, [12m]));
    builder.SetCard(flatten, 1, NbStats(CardType.Attack, TargetType.AnyEnemy, 2, [15m]));
    builder.SetCard(highFive, 0, NbStats(CardType.Attack, TargetType.AllEnemies, 2, [11m, 2m]));
    builder.SetCard(rightHandHand, 0, NbStats(CardType.Attack, TargetType.AnyEnemy, 0, [4m, 2m]));
    return builder.Build();

    static CardStats NbStats(CardType type, TargetType target, int cost, decimal[] vars,
        Keywords keywords = Keywords.None) => new()
    {
        Type = type, Target = target, Rarity = CardRarity.Basic, EnergyCost = cost, CostsX = false,
        StarCost = -1, StarCostsX = false, Keywords = keywords, MaxUpgradeLevel = 1, Vars = vars,
    };
}

static Battle SliceBattle()
{
    Registry.TryCard("STRIKE_IRONCLAD", out ushort strike);
    Registry.TryCard("DEFEND_IRONCLAD", out ushort defend);
    Registry.TryCard("BASH", out ushort bash);
    Registry.TryMonster("NIBBIT", out ushort nibbit);
    Battle battle = new()
    {
        Round = 1, PlayerTurn = 1, Energy = 3, Side = Side.Player, Phase = PlayerPhase.Play,
        Flags = BattleFlags.PlayerHooksActive, MaxHandSize = 10, EnemyCount = 1, RosterCount = 1,
        Player = new Creature { CurrentHp = 80, MaxHp = 80, Flags = CreatureFlags.Present },
    };
    battle.Enemies[0] = new Enemy
    {
        Creature = new Creature { CurrentHp = 44, MaxHp = 44, Flags = CreatureFlags.Present },
        MonsterId = nibbit, CombatId = 1, Move = 1, ForcedMove = byte.MaxValue, Flags = EnemyFlags.InCombat,
    };
    Kernel.LogMonsterMove(ref battle.Enemies[0], 1);
    battle.RngStream(RngStream.Shuffle) = RngState.FromSerializable(0, 1, 2, 3, 4);
    battle.RngStream(RngStream.MonsterAi) = RngState.FromSerializable(0, 5, 6, 7, 8);
    ushort[] deck = [strike, defend, defend, strike, bash, strike, defend, defend, strike, strike];
    for (int index = 0; index < deck.Length; index++)
    {
        Card card = new() { Id = deck[index], BaseCost = (sbyte)(deck[index] == bash ? 2 : 1), BaseStarCost = -1, Flags = CardFlags.DeckCard };
        Piles.Append(ref battle, index < 5 ? Pile.Hand : Pile.Draw, card);
    }
    return battle;
}

// CX: the four previously missing enchantments. Imbued's turn-1 auto-play (and its explicit boundary
// when a captured card disappears), Slither's draw-time random cost, SlumberingEssence's BeforeFlush
// cost modifier and PerfectFit's ModifyShuffleOrder placement.
static void CheckCxKernel(Rules rules)
{
    Check(Registry.TryEnchantment("IMBUED", out ushort imbued), "缺少 IMBUED");
    Check(Registry.TryEnchantment("SLITHER", out ushort slither), "缺少 SLITHER");
    Check(Registry.TryEnchantment("SLUMBERING_ESSENCE", out ushort slumbering), "缺少 SLUMBERING_ESSENCE");
    Check(Registry.TryEnchantment("PERFECT_FIT", out ushort perfectFit), "缺少 PERFECT_FIT");
    Check(Registry.TryCard("DEFEND_IRONCLAD", out ushort defend), "缺少 DEFEND_IRONCLAD");
    Check(Registry.TryCard("CX_TEST_IMBUED_REMOVER", out ushort remover), "缺少 CX_TEST_IMBUED_REMOVER");

    static Card Enchanted(Rules rules, ushort card, ushort enchantment)
    {
        Card value = Kernel.CreateCard(rules, card);
        value.EnchantmentId = (byte)(enchantment + 1);
        return value;
    }

    // Imbued: every enchanted card auto-plays on player turn 1, in AllCards order, without paying energy.
    Battle imbuedBattle = NewBattle();
    imbuedBattle.PlayerTurn = 1;
    imbuedBattle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref imbuedBattle, Pile.Hand, Enchanted(rules, defend, imbued));
    Piles.Append(ref imbuedBattle, Pile.Hand, Enchanted(rules, defend, imbued));
    Exec imbuedExec = new(ref imbuedBattle, rules, default);
    Kernel.AutoPrePlay(ref imbuedExec);
    Check(imbuedBattle.HandCount == 0 && imbuedBattle.DiscardCount == 2,
        $"Imbued 应在第一回合自动打出两张牌，实际手牌 {imbuedBattle.HandCount} 弃牌 {imbuedBattle.DiscardCount}");
    Check(imbuedBattle.Energy == 3 && imbuedBattle.Counter(Counter.CardsPlayedThisTurn) == 2,
        "Imbued 自动打出不应支付能量");
    Check(imbuedBattle.Counter(Counter.EnergySpentThisTurn) == 0, "Imbued 自动打出不应记录能量消耗");

    // Turn 2 (and later) must not auto-play Imbued.
    imbuedBattle.PlayerTurn = 2;
    Piles.Append(ref imbuedBattle, Pile.Hand, Enchanted(rules, defend, imbued));
    Kernel.AutoPrePlay(ref imbuedExec);
    Check(imbuedBattle.HandCount == 1, "Imbued 只应在第一回合自动打出");

    // Boundary: the remover plays first and takes the other captured card out of combat. The card count
    // no longer matches the tracked move, so the kernel must throw UnsupportedException (not replay
    // a guessed card and not silently skip it).
    Battle boundaryBattle = NewBattle();
    boundaryBattle.PlayerTurn = 1;
    boundaryBattle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref boundaryBattle, Pile.Hand, Enchanted(rules, remover, imbued));
    Piles.Append(ref boundaryBattle, Pile.Hand, Enchanted(rules, defend, imbued));
    Exec boundaryExec = new(ref boundaryBattle, rules, default);
    bool boundaryThrown = false;
    try
    {
        Kernel.AutoPrePlay(ref boundaryExec);
    }
    catch (UnsupportedException)
    {
        boundaryThrown = true;
    }
    Check(boundaryThrown, "Imbued 捕获卡消失时应抛 UnsupportedException");

    // Slither: drawing the card into hand spends one CombatEnergyCosts draw and sets an absolute
    // this-combat cost.
    Battle slitherBattle = NewBattle();
    slitherBattle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref slitherBattle, Pile.Draw, Enchanted(rules, defend, slither));
    RngState expected = slitherBattle.RngStream(RngStream.CombatEnergyCosts);
    int counterBefore = expected.Counter;
    int expectedCost = expected.NextInt(4);
    Exec slitherExec = new(ref slitherBattle, rules, default);
    Kernel.Draw(ref slitherExec, 1, fromHandDraw: false);
    Check(slitherBattle.HandCount == 1, "Slither 测试应抽到一张手牌");
    ref Card drawn = ref slitherBattle.Cards[0];
    Check(drawn.EnchantmentId == (byte)(slither + 1), "抽到的应是 Slither 卡");
    Check(drawn.EnergyModifierCount == 1
        && drawn.EnergyModifiers[0].Type == CostType.Absolute
        && drawn.EnergyModifiers[0].Expiration == CostExpiration.EndOfCombat
        && drawn.EnergyModifiers[0].Amount == expectedCost,
        $"Slither 应写入绝对值 {expectedCost} 的本场费用修饰，实际 {drawn.EnergyModifierCount}");
    Check(slitherBattle.RngStream(RngStream.CombatEnergyCosts).Counter == counterBefore + 1,
        "Slither 应只消耗一次 CombatEnergyCosts");

    // SlumberingEssence: BeforeFlush adds a WhenPlayed -1 relative modifier before the flush.
    Battle slumberBattle = NewBattle();
    Piles.Append(ref slumberBattle, Pile.Hand, Enchanted(rules, defend, slumbering));
    Exec slumberExec = new(ref slumberBattle, rules, default);
    Kernel.BeforeFlush(ref slumberExec);
    ref Card slumberCard = ref slumberBattle.Cards[0];
    Check(slumberCard.EnergyModifierCount == 1
        && slumberCard.EnergyModifiers[0].Type == CostType.Relative
        && slumberCard.EnergyModifiers[0].Expiration == CostExpiration.WhenPlayed
        && slumberCard.EnergyModifiers[0].Amount == -1,
        "SlumberingEssence BeforeFlush 应写入 WhenPlayed -1 修饰");
    Kernel.BeforeFlush(ref slumberExec);
    Check(slumberCard.EnergyModifierCount == 2, "每次 BeforeFlush 都应追加一条 SlumberingEssence 修饰");

    // PerfectFit: after the non-initial shuffle the enchanted card is the top of the new draw pile.
    Battle shuffleBattle = NewBattle();
    shuffleBattle.Flags |= BattleFlags.PlayerHooksActive;
    Card perfect = Enchanted(rules, defend, perfectFit);
    Piles.Append(ref shuffleBattle, Pile.Discard, Enchanted(rules, defend, imbued));
    Piles.Append(ref shuffleBattle, Pile.Discard, perfect);
    Piles.Append(ref shuffleBattle, Pile.Draw, Enchanted(rules, defend, slither));
    Exec shuffleExec = new(ref shuffleBattle, rules, default);
    Kernel.Shuffle(ref shuffleExec);
    Check(shuffleBattle.DrawCount == 3 && shuffleBattle.DiscardCount == 0, "洗牌后三张牌都应进入抽牌堆");
    Check(Fingerprint.Equal(shuffleBattle.Cards[shuffleBattle.HandCount], perfect),
        "PerfectFit 应把附魔卡放在洗牌后抽牌堆顶");
}

// SK: contracts for the Silent kernel capabilities. Discard pipeline + AfterCardDiscarded + Sly
// auto-play, the poison trigger, the dynamic Shiv target, the card-play pair snapshot and the
// next-turn draw/energy terms.
static void CheckSkKernel()
{
    Rules rules = SilentRules();
    Check(Registry.TryCard("KS_DISCARD_TARGET", out ushort targetCard), "缺少 KS_DISCARD_TARGET");
    Check(Registry.TryCard("KS_SLY_CARD", out ushort slyCard), "缺少 KS_SLY_CARD");
    Check(Registry.TryCard("SHIV", out ushort shiv), "缺少 SHIV");
    Check(Registry.TryPower("POISON_POWER", out ushort poison), "缺少 POISON_POWER");
    Check(Registry.TryPower("ACCELERANT_POWER", out ushort accelerant), "缺少 ACCELERANT_POWER");
    Check(Registry.TryPower("FAN_OF_KNIVES_POWER", out ushort fan), "缺少 FAN_OF_KNIVES_POWER");
    Check(Registry.TryPower("AFTERIMAGE_POWER", out ushort afterimage), "缺少 AFTERIMAGE_POWER");
    Check(Registry.TryPower("DRAW_CARDS_NEXT_TURN_POWER", out ushort drawNext), "缺少 DRAW_CARDS_NEXT_TURN_POWER");
    Check(Registry.TryPower("ENERGY_NEXT_TURN_POWER", out ushort energyNext), "缺少 ENERGY_NEXT_TURN_POWER");
    Check(Registry.TryRelic("KS_TEST_DISCARD_RELIC", out ushort discardRelic), "缺少 KS_TEST_DISCARD_RELIC");
    Check(Registry.TryRelic("KS_TEST_SLY_RELIC", out ushort slyRelic), "缺少 KS_TEST_SLY_RELIC");

    // Discard: the card moves, the counter grows and AfterCardDiscarded reaches the relic listener.
    Battle battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 2;
    battle.Relics[0] = new Relic { Id = discardRelic };
    battle.Relics[1] = new Relic { Id = slyRelic };
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, targetCard));
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, slyCard));
    Exec x = new(ref battle, rules, default);
    KsTestLog.Clear();
    Kernel.Discard(ref x, Pile.Hand, 1);
    Check(battle.Counter(Counter.CardsDiscardedThisTurn) == 1, "弃牌计数应为 1");
    Check(KsTestLog.Lines.Contains("discarded"), "AfterCardDiscarded 未派发");
    Check(KsTestLog.Lines.Contains("sly-play"), "Sly 弃牌未自动打出");

    // Discard-and-draw with a Sly card: the draw may shuffle the Sly card out of the discard pile;
    // the auto-play locates it anywhere in the battle instead of assuming the discard pile.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 2;
    battle.Relics[0] = new Relic { Id = discardRelic };
    battle.Relics[1] = new Relic { Id = slyRelic };
    // The draw pile starts empty, so Draw shuffles the discard (containing the Sly card) into the
    // draw pile before the auto-play locates it.
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, targetCard));
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, slyCard));
    battle.RngStream(RngStream.Shuffle) = RngState.FromSerializable(0, 1, 2, 3, 4);
    x = new(ref battle, rules, default);
    KsTestLog.Clear();
    Span<int> selected = stackalloc int[2];
    selected[0] = 0;
    selected[1] = 1;
    Kernel.DiscardAndDraw(ref x, Pile.Hand, selected, 1);
    Check(battle.Counter(Counter.CardsDiscardedThisTurn) == 2, "批量弃牌计数应为 2");
    Check(KsTestLog.Lines.Count(line => line == "discarded") == 2, "批量弃牌应派发两次 AfterCardDiscarded");
    Check(KsTestLog.Lines.Contains("sly-play"), "批量弃牌后 Sly 未自动打出");

    // Two byte-equal Sly cards are not interchangeable. The discarded copy must auto-play; taking
    // the copy still in hand would consume both and leave the hand empty (DX4).
    battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, slyCard));
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, slyCard));
    x = new(ref battle, rules, default);
    KsTestLog.Clear();
    Span<int> duplicateSelection = stackalloc int[1];
    duplicateSelection[0] = 1;
    Kernel.DiscardAndDraw(ref x, Pile.Hand, duplicateSelection, 0);
    Check(battle.HandCount == 1 && Piles.At(ref battle, Pile.Hand, 0).Id == slyCard,
        "两个相同 Sly 牌时应只自动打出被弃置的那一张，手里保留另一张");
    Check(battle.DiscardCount == 1 && Piles.At(ref battle, Pile.Discard, 0).Id == slyCard,
        "自动打出结束后被弃置的 Sly 牌应在弃牌堆");
    Check(KsTestLog.Lines.Count(line => line == "sly-play") == 1, "两个相同 Sly 牌时只应自动打出一次");
    Check(x.TransientMarkBase == 0, "Sly 瞬态身份范围应在结算后释放");

    // The identity range is a capacity-bounded transient resource: exhausting it is an explicit
    // CapacityException, not a silent reuse of another batch's marks.
    battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, slyCard));
    x = new(ref battle, rules, default) { TransientMarkBase = 200 };
    bool markCapacity = false;
    try
    {
        Span<int> one = stackalloc int[1];
        one[0] = 0;
        Kernel.DiscardAndDraw(ref x, Pile.Hand, one, 0);
    }
    catch (CapacityException)
    {
        markCapacity = true;
    }
    Check(markCapacity, "Sly 瞬态身份范围耗尽时应抛 CapacityException");

    // A hook that removes the discarded card leaves no copy carrying its mark; the auto-play target
    // is unrepresentable and must fail explicitly instead of playing a byte-equal stranger.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = discardRelic };
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, slyCard));
    x = new(ref battle, rules, default);
    KsTestDiscardRelic.RemoveDiscardedCard = true;
    bool batchUnsupported = false;
    try
    {
        Span<int> one = stackalloc int[1];
        one[0] = 0;
        Kernel.DiscardAndDraw(ref x, Pile.Hand, one, 0);
    }
    catch (UnsupportedException)
    {
        batchUnsupported = true;
    }
    finally
    {
        KsTestDiscardRelic.RemoveDiscardedCard = false;
    }
    Check(batchUnsupported, "批量弃牌的 Sly 目标失去瞬态标记时应抛 UnsupportedException");

    // An AfterCardDiscarded listener that moves the card out of every pile makes the Sly auto-play
    // target unrepresentable: the kernel must surface a UnsupportedException, not guess a slot.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = discardRelic };
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, slyCard));
    x = new(ref battle, rules, default);
    KsTestLog.Clear();
    KsTestDiscardRelic.RemoveDiscardedCard = true;
    bool unsupported = false;
    try
    {
        Kernel.Discard(ref x, Pile.Hand, 0);
    }
    catch (UnsupportedException)
    {
        unsupported = true;
    }
    finally
    {
        KsTestDiscardRelic.RemoveDiscardedCard = false;
    }
    Check(unsupported, "Sly 目标离开所有牌堆时应抛 UnsupportedException");

    // Poison: min(amount, 1 + opponents' Accelerant) hits of the current amount, decrementing while
    // alive (CorePowerSupport.TriggerPoison).
    battle = NewBattle();
    x = new(ref battle, rules, default);
    byte enemy = Kernel.EnemyOwner(0);
    Kernel.ApplyPower(ref x, enemy, poison, 3, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Span<byte> enemies = stackalloc byte[1];
    enemies[0] = enemy;
    Kernel.TriggerPoison(ref x, enemies);
    // TriggerCount = min(amount, 1 + opponents' Accelerant): no Accelerant means one hit.
    Check(battle.Enemies[0].Creature.CurrentHp == 97, $"毒应造成 3 点伤害，实际血量 {battle.Enemies[0].Creature.CurrentHp}");
    Check(Kernel.GetAmount(battle, enemy, poison) == 2, "毒应递减到 2");

    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, enemy, poison, 3, Limits.PlayerOwner);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, accelerant, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Kernel.TriggerPoison(ref x, enemies);
    Check(battle.Enemies[0].Creature.CurrentHp == 95, $"加速毒应触发 2 次，实际血量 {battle.Enemies[0].Creature.CurrentHp}");
    Check(Kernel.GetAmount(battle, enemy, poison) == 1, "加速毒应剩余 1 层");

    // Shiv's dynamic target: all enemies only while the player holds FanOfKnivesPower.
    Card shivCard = Kernel.CreateCard(rules, shiv);
    Check(Registry.Card(shiv).ResolveTargetType(battle, rules, shivCard) == TargetType.AnyEnemy,
        "无飞刀扇时 Shiv 应单体");
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, fan, 1, Limits.PlayerOwner);
    Check(Registry.Card(shiv).ResolveTargetType(battle, rules, shivCard) == TargetType.AllEnemies,
        "飞刀扇在场时 Shiv 应打全体");

    // Card-play pair: BeforeCardPlayed snapshots the Afterimage amount for AfterCardPlayed.
    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, afterimage, 2, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    int afterimageIndex = Kernel.FindPower(battle, Limits.PlayerOwner, afterimage);
    Check(afterimageIndex >= 0, "Afterimage 未施加");
    ListenerDef afterimageDef = Registry.Power(afterimage);
    Listener self = new(ListenerKind.Power, (byte)afterimageIndex, Limits.PlayerOwner);
    CardPlayInfo play = new(Kernel.CreateCard(rules, targetCard), Limits.NoOwner, 0, 1, Pile.Discard);
    afterimageDef.BeforeCardPlayed(ref x, self, play);
    Check(x.TryTakeCardPlayPair(CardPlayPairKind.AfterimageBlock, Limits.PlayerOwner, afterimage,
        out int pairAmount) && pairAmount == 2, "Afterimage 快照金额错误");
    Check(!x.TryTakeCardPlayPair(CardPlayPairKind.AfterimageBlock, Limits.PlayerOwner, afterimage,
        out _), "Afterimage 快照只应消费一次");

    // Next-turn draw/energy: DrawCardsNextTurn adds only its snapshotted amount; EnergyNextTurn is
    // consumed by the turn-start reset.
    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, drawNext, 2, Limits.PlayerOwner);
    int drawIndex = Kernel.FindPower(battle, Limits.PlayerOwner, drawNext);
    battle.Powers[drawIndex].AmountOnTurnStart = 2;
    Check(Kernel.ModifiedHandDraw(ref x, 5) == 7, "下回合抽牌应加 2");
    battle.Powers[drawIndex].AmountOnTurnStart = 0;
    Check(Kernel.ModifiedHandDraw(ref x, 5) == 5, "未快照的下回合抽牌不应由监听者加");

    battle = NewBattle();
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, energyNext, 2, Limits.PlayerOwner);
    Check(Kernel.ConsumeEnergyNextTurn(ref battle) == 2, "下回合能量应为 2");
    Check(Kernel.GetAmount(battle, Limits.PlayerOwner, energyNext) == 0, "下回合能量应被消费");
}

// Scrape: the drawn card identities are stamped before AfterCardDrawn, so the discard step follows
// the same value through auto-play, a move to the draw pile or a re-discard, and the Sly auto-play
// runs only after every AfterCardDiscarded. Equal copies in hand must survive.
static void CheckScrapeDrawnIdentity()
{
    Rules rules = ScrapeRules();
    Check(Registry.TryCard("SCRAPE", out ushort scrape), "缺少 SCRAPE");
    Check(Registry.TryCard("KS_SCRAPE_DRAWN", out ushort drawnId), "缺少 KS_SCRAPE_DRAWN");
    Check(Registry.TryCard("KS_SCRAPE_SLY", out ushort slyId), "缺少 KS_SCRAPE_SLY");
    Check(Registry.TryCard("KS_SCRAPE_OTHER", out ushort otherId), "缺少 KS_SCRAPE_OTHER");
    Check(Registry.TryRelic("KS_SCRAPE_DRAW_RELIC", out ushort drawRelic), "缺少 KS_SCRAPE_DRAW_RELIC");
    Check(Registry.TryRelic("KS_SCRAPE_LOG_RELIC", out ushort logRelic), "缺少 KS_SCRAPE_LOG_RELIC");
    Check(Registry.TryRelic("KS_SCRAPE_CHOICE_RELIC", out ushort choiceRelic), "缺少 KS_SCRAPE_CHOICE_RELIC");

    // The Sly card drawn by Scrape is only auto-played after both selected cards were discarded;
    // its own draw then appends the two remaining draw-pile cards.
    Battle battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = logRelic };
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, slyId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, otherId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, otherId));
    KsScrapeLog.Clear();
    KsScrapeDrawRelic.Reset();
    Exec x = new(ref battle, rules, default)
    {
        Card = Kernel.CreateCard(rules, scrape),
        Stats = rules.Card(scrape, 0),
        Target = Kernel.EnemyOwner(0),
    };
    Registry.Card(scrape).OnPlay(ref x);
    Check(!x.NeedsChoice, "Scrape 批量弃牌不应中断");
    Check(KsScrapeLog.Lines.SequenceEqual(["discard", "discard", "sly-play"]),
        $"Scrape 应先弃完再自动打出 Sly，实际顺序 {string.Join(',', KsScrapeLog.Lines)}");
    Check(battle.HandCount == 2
        && Piles.At(ref battle, Pile.Hand, 0).Id == otherId
        && Piles.At(ref battle, Pile.Hand, 1).Id == otherId,
        $"Sly 自动打出应再抽两张，手牌实际 {battle.HandCount}");
    Check(battle.DiscardCount == 2
        && Piles.At(ref battle, Pile.Discard, 0).Id == drawnId
        && Piles.At(ref battle, Pile.Discard, 1).Id == slyId,
        "Sly 自动打出后弃牌堆应是先弃的普通牌、后落下的 Sly 牌");
    Check(x.TransientMarkBase == 0, "Scrape 的瞬态身份范围应在结算后释放");
    CheckNoDynamicMarks(in battle, "Scrape 批量弃牌后");

    // A draw hook auto-plays the drawn card. The identity mark follows it to the discard pile, while
    // the byte-equal copy already in hand must not be discarded instead.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = drawRelic };
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, drawnId));
    battle.Cards[0].TransientMark = 1;
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    KsScrapeDrawRelic.Set(KsScrapeDrawMode.AutoPlay, drawnId, 1);
    x = new(ref battle, rules, default)
    {
        Card = Kernel.CreateCard(rules, scrape),
        Stats = rules.Card(scrape, 0),
        Target = Kernel.EnemyOwner(0),
    };
    Registry.Card(scrape).OnPlay(ref x);
    Check(!x.NeedsChoice, "抽牌钩子自动打出不应中断 Scrape");
    Check(battle.HandCount == 1 && battle.Cards[0].TransientMark == 1,
        "Scrape 应弃掉被自动打出的抽牌，保留手中字节相同的旧副本");
    Check(battle.DiscardCount == 2 && battle.DrawCount == 0,
        $"自动打出的抽牌和被弃的抽牌都应在弃牌堆，实际 discard={battle.DiscardCount} draw={battle.DrawCount}");
    Check(x.TransientMarkBase == 0, "自动打出路径应释放瞬态身份范围");
    CheckNoDynamicMarks(in battle, "自动打出路径");

    // The only draw-pile card is auto-played by the draw hook, lands in the discard pile, and the
    // next draw shuffles that same card back: the existing engine returns the same CardModel twice.
    // Both occurrences must be discarded, reusing one inner identity byte.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = drawRelic };
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    battle.RngStream(RngStream.Shuffle) = RngState.FromSerializable(0, 1, 2, 3, 4);
    KsScrapeDrawRelic.Set(KsScrapeDrawMode.AutoPlay, drawnId, 2);
    x = new(ref battle, rules, default)
    {
        Card = Kernel.CreateCard(rules, scrape),
        Stats = rules.Card(scrape, 0),
        Target = Kernel.EnemyOwner(0),
    };
    Registry.Card(scrape).OnPlay(ref x);
    Check(!x.NeedsChoice, "同一张牌重复抽到不应中断 Scrape");
    Check(battle.Counter(Counter.CardsDiscardedThisTurn) == 2,
        $"同一张牌抽到两次应弃置两次，实际计数 {battle.Counter(Counter.CardsDiscardedThisTurn)}");
    Check(battle.HandCount == 0 && battle.DiscardCount == 1,
        $"同一张牌抽到两次仍只留下一个牌值，实际 hand={battle.HandCount} discard={battle.DiscardCount}");
    Check(x.TransientMarkBase == 0, "重复抽到的路径应释放瞬态身份范围");
    CheckNoDynamicMarks(in battle, "重复抽到路径");

    // A draw hook moves the drawn card to the bottom of the draw pile. The second draw takes the
    // remaining copy; both are then discarded by identity, and the older hand copy stays.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = drawRelic };
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, drawnId));
    battle.Cards[0].TransientMark = 1;
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    KsScrapeDrawRelic.Set(KsScrapeDrawMode.MoveToDrawBottom, drawnId, 1);
    x = new(ref battle, rules, default)
    {
        Card = Kernel.CreateCard(rules, scrape),
        Stats = rules.Card(scrape, 0),
        Target = Kernel.EnemyOwner(0),
    };
    Registry.Card(scrape).OnPlay(ref x);
    Check(!x.NeedsChoice, "抽牌钩子移动不应中断 Scrape");
    Check(battle.HandCount == 1 && battle.Cards[0].TransientMark == 1,
        "Scrape 应按标记弃掉移到抽牌堆的抽牌，保留旧副本");
    Check(battle.DrawCount == 0 && battle.DiscardCount == 2,
        $"移到抽牌堆的抽牌也应被弃置，实际 draw={battle.DrawCount} discard={battle.DiscardCount}");
    Check(x.TransientMarkBase == 0, "移动路径应释放瞬态身份范围");
    CheckNoDynamicMarks(in battle, "移到抽牌堆路径");

    // A draw hook puts the drawn card into the discard pile before Scrape discards it. The existing
    // engine removes and re-appends that same object, moving it to the bottom; the old discard-pile
    // sentinel must stay above it.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = drawRelic };
    Piles.Append(ref battle, Pile.Discard, Kernel.CreateCard(rules, otherId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    KsScrapeDrawRelic.Set(KsScrapeDrawMode.MoveToDiscardTop, drawnId, 1);
    x = new(ref battle, rules, default)
    {
        Card = Kernel.CreateCard(rules, scrape),
        Stats = rules.Card(scrape, 0),
        Target = Kernel.EnemyOwner(0),
    };
    Registry.Card(scrape).OnPlay(ref x);
    Check(!x.NeedsChoice, "已进入弃牌堆的抽牌不应中断 Scrape");
    Check(battle.DiscardCount == 3
        && Piles.At(ref battle, Pile.Discard, 0).Id == otherId
        && Piles.At(ref battle, Pile.Discard, 1).Id == drawnId
        && Piles.At(ref battle, Pile.Discard, 2).Id == drawnId,
        "已在弃牌堆的抽牌应重新移到弃牌堆底");
    Check(x.TransientMarkBase == 0, "弃牌堆路径应释放瞬态身份范围");
    CheckNoDynamicMarks(in battle, "已在弃牌堆路径");

    // The existing engine filters the live CardModel after the whole draw. A draw hook that makes
    // the drawn card free must exclude it from the discard even though its draw-time value had cost 1.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = drawRelic };
    Card freeScrape = Kernel.CreateCard(rules, scrape);
    Piles.Append(ref battle, Pile.Play, freeScrape);
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    KsScrapeDrawRelic.Set(KsScrapeDrawMode.MakeFree, drawnId, 1);
    x = new(ref battle, rules, default)
    {
        Card = freeScrape,
        Stats = rules.Card(scrape, 0),
        Target = Kernel.EnemyOwner(0),
    };
    Registry.Card(scrape).OnPlay(ref x);
    Check(!x.NeedsChoice, "抽牌钩子改费不应中断 Scrape");
    Check(battle.HandCount == 1 && battle.DiscardCount == 0,
        $"抽牌钩子改成 0 费后不应弃置，实际 hand={battle.HandCount} discard={battle.DiscardCount}");
    Check(x.TransientMarkBase == 0, "改费路径应释放瞬态身份范围");
    CheckNoDynamicMarks(in battle, "改费路径");

    // A listener raises a choice after the first discard. Scrape must unwind through finally,
    // restore every inner mark (including the card that triggered the choice) and its base.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = choiceRelic };
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, drawnId));
    battle.Cards[0].TransientMark = 65;
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    KsScrapeDrawRelic.Reset();
    KsScrapeChoiceRelic.RequestChoice = true;
    x = new(ref battle, rules, default)
    {
        Card = Kernel.CreateCard(rules, scrape),
        Stats = rules.Card(scrape, 0),
        Target = Kernel.EnemyOwner(0),
        TransientMarkBase = 3,
    };
    try
    {
        Registry.Card(scrape).OnPlay(ref x);
    }
    finally
    {
        KsScrapeChoiceRelic.RequestChoice = false;
    }
    Check(x.NeedsChoice, "弃牌钩子提出选择时应停在 NeedsChoice");
    Check(x.TransientMarkBase == 3, $"选择中断后应恢复外层 TransientMarkBase=3，实际 {x.TransientMarkBase}");
    Check(battle.Cards[0].TransientMark == 65, "内层批次必须保留外层 65 标记");
    CheckNoDynamicMarksInRange(in battle, 67, 2, "选择中断");
    Check(battle.DiscardCount == 1 && battle.HandCount == 2,
        "选择中断时应只完成第一张弃牌");

    // Reserving more identity bytes than the 64..255 range has is an explicit capacity error.
    battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, drawnId));
    battle.Cards[0].TransientMark = 1;
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    Piles.Append(ref battle, Pile.Draw, Kernel.CreateCard(rules, drawnId));
    KsScrapeDrawRelic.Reset();
    x = new(ref battle, rules, default)
    {
        Card = Kernel.CreateCard(rules, scrape),
        Stats = rules.Card(scrape, 0),
        Target = Kernel.EnemyOwner(0),
        TransientMarkBase = 191,
    };
    bool capacity = false;
    try
    {
        Registry.Card(scrape).OnPlay(ref x);
    }
    catch (CapacityException)
    {
        capacity = true;
    }
    Check(capacity, "Scrape 瞬态身份范围耗尽时应抛 CapacityException");
    Check(x.TransientMarkBase == 191, "容量失败后 TransientMarkBase 应保持不变");
    CheckNoDynamicMarks(in battle, "容量失败");
}

static void CheckNoDynamicMarks(in Battle battle, string context)
{
    for (int position = 0; position < battle.CardCount; position++)
    {
        Check(battle.Cards[position].TransientMark < 64,
            $"{context}: 卡牌 {position} 残留动态标记 {battle.Cards[position].TransientMark}");
    }
    for (int slot = 0; slot < Limits.DetachedCards; slot++)
    {
        Check(battle.DetachedCards[slot].TransientMark < 64,
            $"{context}: detached {slot} 残留动态标记 {battle.DetachedCards[slot].TransientMark}");
    }
}

static void CheckNoDynamicMarksInRange(in Battle battle, int markStart, int count, string context)
{
    for (int position = 0; position < battle.CardCount; position++)
    {
        int mark = battle.Cards[position].TransientMark;
        Check(mark < markStart || mark >= markStart + count,
            $"{context}: 卡牌 {position} 残留内层标记 {mark}");
    }
    for (int slot = 0; slot < Limits.DetachedCards; slot++)
    {
        int mark = battle.DetachedCards[slot].TransientMark;
        Check(mark < markStart || mark >= markStart + count,
            $"{context}: detached {slot} 残留内层标记 {mark}");
    }
}

static Rules ScrapeRules()
{
    RulesDraft builder = new() { BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"], RootEnemyCount = 0 };
    Registry.TryCard("SCRAPE", out ushort scrape);
    Registry.TryCard("KS_SCRAPE_DRAWN", out ushort drawn);
    Registry.TryCard("KS_SCRAPE_SLY", out ushort sly);
    Registry.TryCard("KS_SCRAPE_OTHER", out ushort other);
    builder.SetCard(scrape, 0, ScrapeStats(CardType.Attack, TargetType.AnyEnemy, 1, [7m, 2m]));
    builder.SetCard(drawn, 0, ScrapeStats(CardType.Skill, TargetType.Self, 1));
    builder.SetCard(sly, 0, ScrapeStats(CardType.Skill, TargetType.Self, 1, [2m], Keywords.Sly));
    builder.SetCard(other, 0, ScrapeStats(CardType.Skill, TargetType.Self, 1));
    return builder.Build();

    static CardStats ScrapeStats(CardType type, TargetType target, int cost,
        decimal[]? vars = null, Keywords keywords = Keywords.None) => new()
    {
        Type = type, Target = target, Rarity = CardRarity.Basic, EnergyCost = cost, CostsX = false,
        StarCost = -1, StarCostsX = false, Keywords = keywords, MaxUpgradeLevel = 1,
        Vars = vars ?? [],
    };
}

static Rules SilentRules()
{
    RulesDraft builder = new() { BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"], RootEnemyCount = 0 };
    Registry.TryCard("KS_DISCARD_TARGET", out ushort targetCard);
    Registry.TryCard("KS_SLY_CARD", out ushort slyCard);
    Registry.TryCard("SHIV", out ushort shiv);
    builder.SetCard(targetCard, 0, SilentStats(CardType.Skill, TargetType.Self, 0));
    builder.SetCard(slyCard, 0, SilentStats(CardType.Skill, TargetType.Self, 0, Keywords.Sly));
    builder.SetCard(shiv, 0, SilentStats(CardType.Attack, TargetType.AnyEnemy, 0));
    return builder.Build();

    static CardStats SilentStats(CardType type, TargetType target, int cost,
        Keywords keywords = Keywords.None) => new()
    {
        Type = type, Target = target, Rarity = CardRarity.Basic, EnergyCost = cost, CostsX = false,
        StarCost = -1, StarCostsX = false, Keywords = keywords, MaxUpgradeLevel = 1, Vars = [],
    };
}



// Beam-shaped workload: copy parent into a preallocated slot, step in place, hash. Reports the
// mean per transition; zero managed allocation is asserted on the measured loop.
static object Benchmark(Rules rules)
{
    const int width = 64;
    Battle root = SliceBattle();
    Battle[] parents = new Battle[width];
    Battle[] children = new Battle[width * 16];
    PlayerAction[] actions = new PlayerAction[Kernel.MaxLegalActions];
    Random random = new(3);
    for (int index = 0; index < width; index++)
    {
        Battle battle = root;
        int depth = random.Next(12);
        for (int step = 0; step < depth && Kernel.IsInProgress(battle); step++)
        {
            int count = Kernel.LegalActions(battle, rules, actions);
            Kernel.Step(ref battle, actions[random.Next(count)], rules, out _);
        }
        parents[index] = Kernel.IsInProgress(battle) ? battle : root;
    }

    long transitions = 0;
    ulong sink = 0;
    // mode 0: copy+step+hash, 1: copy only, 2: copy+hash, 3: copy+step
    void Round(int mode)
    {
        int slot = 0;
        for (int parent = 0; parent < width; parent++)
        {
            int count = Kernel.LegalActions(parents[parent], rules, actions);
            for (int action = 0; action < count && slot < children.Length; action++)
            {
                children[slot] = parents[parent];
                if (mode is 0 or 3)
                    Kernel.Step(ref children[slot], actions[action], rules, out _);
                if (mode is 0 or 2)
                    sink += Fingerprint.Hash(children[slot]);
                slot++;
                transitions++;
            }
        }
    }
    Dictionary<string, double> breakdown = [];
    long allocated = 0;
    foreach ((string name, int mode) in new[] { ("copyStepHash", 0), ("copyOnly", 1), ("copyHash", 2), ("copyStep", 3) })
    {
        for (int warm = 0; warm < 200; warm++)
            Round(mode);
        transitions = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int round = 0; round < 1000; round++)
            Round(mode);
        watch.Stop();
        if (mode == 0)
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        breakdown[name] = watch.Elapsed.TotalNanoseconds / transitions;
    }
    return new
    {
        transitionsPerMode = transitions,
        nanosecondsPerTransition = breakdown,
        allocatedBytes = allocated,
        sink,
    };
}

// Spawn contract: CombatId allocation, the native initial-HP distribution, encounter-slot roster
// insertion, per-side initial roll and the synthetic revive move. Mirrors
// SimulatedCombatState.CreatePredictedMonster/AddPredictedMonster/PreparePredictedMonster.
// KG1 contracts: generation-pool sampling order and the auto-play result-pile rules.
static void CheckGenerationAndAutoPlay()
{
    Check(Registry.TryCard("DEFEND_IRONCLAD", out ushort defend), "缺少 DEFEND_IRONCLAD 定义");
    Check(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "缺少 STRIKE_IRONCLAD 定义");
    RulesDraft builder = new()
    {
        BaseMaxEnergy = 3,
        PlayerSlotIndex = 0,
        HasEncounter = true,
        EncounterSlots = ["a"],
        RootEnemyCount = 0,
    };
    builder.SetCard(defend, 0, new CardStats
    {
        Type = CardType.Skill, Target = TargetType.Self, Rarity = CardRarity.Basic,
        EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false, Keywords = Keywords.None,
        MaxUpgradeLevel = 1, Vars = [6m],
    });
    builder.SetCard(strike, 0, new CardStats
    {
        Type = CardType.Attack, Target = TargetType.AnyEnemy, Rarity = CardRarity.Basic,
        EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false, Keywords = Keywords.None,
        MaxUpgradeLevel = 1, Vars = [6m],
    });
    builder.SetCard(strike, 1, new CardStats
    {
        Type = CardType.Attack, Target = TargetType.AnyAlly, Rarity = CardRarity.Basic,
        EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false, Keywords = Keywords.None,
        MaxUpgradeLevel = 1, Vars = [6m],
    });
    builder.SetCardPool("test.pool", [strike, defend, strike, defend]);
    Rules rules = builder.Build();

    // Distinct selection mirrors TakeRandom/UnstableShuffle: n-1 Shuffle draws over the whole list,
    // then the first count entries of the shuffled order. With-replacement selection draws each card.
    Battle battle = NewBattle();
    battle.RngStream(RngStream.Shuffle) = RngState.FromSerializable(1, 11, 22, 33, 44);
    Span<Card> selected = stackalloc Card[4];
    RngState reference = battle.RngStream(RngStream.Shuffle);
    int distinctCount = Kernel.SampleCardPool(ref battle, rules, "test.pool", 1, distinct: true,
        RngStream.Shuffle, selected);
    Check(distinctCount == 1, "distinct 取样数量应为 1");
    Span<ushort> expected = stackalloc ushort[4];
    for (int index = 0; index < 4; index++)
        expected[index] = index % 2 == 0 ? strike : defend;
    for (int n = 4; n > 1;)
    {
        n--;
        int k = reference.NextInt(n + 1);
        (expected[k], expected[n]) = (expected[n], expected[k]);
    }
    Check(selected[0].Id == expected[0], "distinct 取样顺序与 UnstableShuffle 不一致");
    Check(battle.RngStream(RngStream.Shuffle).Counter == 4, "distinct 取样应消耗 3 次 Shuffle");
    Check(battle.RngStream(RngStream.Shuffle).Counter == reference.Counter,
        "distinct 取样参考计数器不一致");

    Battle replacement = NewBattle();
    replacement.RngStream(RngStream.Shuffle) = RngState.FromSerializable(1, 11, 22, 33, 44);
    Span<Card> repeated = stackalloc Card[3];
    int repeatedCount = Kernel.SampleCardPool(ref replacement, rules, "test.pool", 3, distinct: false,
        RngStream.Shuffle, repeated);
    RngState replacementReference = RngState.FromSerializable(1, 11, 22, 33, 44);
    Check(repeatedCount == 3, "有放回取样数量应为 3");
    for (int index = 0; index < 3; index++)
    {
        // The same NextInt draw indexes the frozen pool [strike, defend, strike, defend].
        int pick = replacementReference.NextInt(0, 4);
        Check(repeated[index].Id == (pick % 2 == 0 ? strike : defend), $"有放回第 {index} 张与 NextInt 不一致");
    }
    Check(replacement.RngStream(RngStream.Shuffle).Counter == 4, "有放回取样应每张消耗一次 Shuffle");

    // Unplayable / no-target auto-play goes to the result pile without playing.
    Battle unplayable = NewBattle();
    Card blocked = Kernel.CreateCard(rules, defend);
    blocked.Keywords |= Keywords.Unplayable;
    Piles.Append(ref unplayable, Pile.Hand, blocked);
    Exec x = new(ref unplayable, rules, PlayerAction.PlayCard(0, Limits.NoOwner));
    Check(!Kernel.AutoPlay(ref x, Pile.Hand, 0), "不可打出的自动打出应返回 false");
    Check(unplayable.HandCount == 0 && unplayable.DiscardCount == 1, "不可打出的自动打出应进入弃牌堆");
    Check(!x.NeedsChoice, "不可打出的自动打出不应请求选择");

    Battle targetless = NewBattle();
    Piles.Append(ref targetless, Pile.Hand, Kernel.CreateCard(rules, strike, 1));
    Exec y = new(ref targetless, rules, PlayerAction.PlayCard(0, Limits.NoOwner));
    Check(!Kernel.AutoPlay(ref y, Pile.Hand, 0), "无目标的自动打出应返回 false");
    Check(targetless.HandCount == 0 && targetless.DiscardCount == 1, "无目标的自动打出应进入弃牌堆");

    // Exhaust-keyword and force-exhaust cards land in the exhaust pile; a played force-exhaust card
    // clears ExhaustOnNextPlay, while one moved without playing keeps it (native behavior).
    Battle exhaustKeyword = NewBattle();
    Card howl = Kernel.CreateCard(rules, defend);
    howl.Keywords |= Keywords.Unplayable | Keywords.Exhaust;
    Piles.Append(ref exhaustKeyword, Pile.Hand, howl);
    Exec z = new(ref exhaustKeyword, rules, PlayerAction.PlayCard(0, Limits.NoOwner));
    _ = Kernel.AutoPlay(ref z, Pile.Hand, 0);
    Check(exhaustKeyword.ExhaustCount == 1 && exhaustKeyword.DiscardCount == 0, "消耗关键字的自动打出应进入消耗堆");

    Battle played = NewBattle();
    Piles.Append(ref played, Pile.Draw, Kernel.CreateCard(rules, defend));
    Exec w = new(ref played, rules, PlayerAction.PlayCard(0, Limits.NoOwner));
    Kernel.AutoPlayFromDrawPile(ref w, 1, forceExhaust: true);
    Check(played.ExhaustCount == 1 && played.DiscardCount == 0, "强制消耗的自动打出应进入消耗堆");
    Check(played.Player.Block == 6, "自动打出的防御应获得格挡");
    Check((Piles.At(ref played, Pile.Exhaust, 0).Flags & CardFlags.ExhaustOnNextPlay) == 0,
        "打出的强制消耗标记应被清除");

    Battle notPlayed = NewBattle();
    Piles.Append(ref notPlayed, Pile.Draw, Kernel.CreateCard(rules, strike, 1));
    Exec v = new(ref notPlayed, rules, PlayerAction.PlayCard(0, Limits.NoOwner));
    Kernel.AutoPlayFromDrawPile(ref v, 1, forceExhaust: true);
    Check(notPlayed.ExhaustCount == 1, "无目标的强制消耗自动打出应进入消耗堆");
    Check((Piles.At(ref notPlayed, Pile.Exhaust, 0).Flags & CardFlags.ExhaustOnNextPlay) != 0,
        "未打出的强制消耗标记应保留");
}

// KB: decimal card variables (Thrash) use an exact value table keyed from Card.Private. The
// contracts pin canonical slot order, clone sharing, upgrade/downgrade retention, orphan removal,
// capacity, the fractional-delta boundary on a card that did not opt in, and the transient
// BeatDown mark's clone semantics.
static void CheckDecimalVars(Rules rules)
{
    Check(Registry.TryCard("THRASH", out ushort thrash), "缺少 THRASH 定义");
    Battle battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, thrash));
    DecimalVars.Assign(ref battle, ref battle.Cards[0], 0.75m);
    Check(battle.DecimalVarCount == 1 && battle.Cards[0].Private == 1,
        $"Thrash 的小数变量应占用第一个槽位，实际 count={battle.DecimalVarCount} private={battle.Cards[0].Private}");
    Check(DecimalVars.Delta(in battle, in battle.Cards[0], 0) == 0.75m, "小数变量应精确保存 0.75");

    // A gameplay clone inherits the value; updating one card must not change the other.
    Piles.Append(ref battle, Pile.Hand, Kernel.CloneCard(battle.Cards[0]));
    Check(DecimalVars.Delta(in battle, in battle.Cards[1], 0) == 0.75m, "克隆应继承小数变量");
    DecimalVars.Assign(ref battle, ref battle.Cards[0], 1.5m);
    Check(DecimalVars.Delta(in battle, in battle.Cards[0], 0) == 1.5m, "更新后原卡应为 1.5");
    Check(DecimalVars.Delta(in battle, in battle.Cards[1], 0) == 0.75m, "共享槽位的克隆不应被改写");
    Check(battle.DecimalVarCount == 2, "两个不同值应占两个槽位");

    // Upgrade keeps the delta; Downgrade resets Var0 and keeps the private ExtraDamage equivalent.
    Kernel.UpgradeCard(rules, ref battle.Cards[0]);
    Check(DecimalVars.Delta(in battle, in battle.Cards[0], 0) == 1.5m, "升级不应改变小数变量偏移");
    Check(battle.Cards[0].Var0 == 0, "小数变量卡不应同时写 Var0");
    Exec x = new(ref battle, rules, default);
    Kernel.DowngradeCard(ref x, 0);
    Check(DecimalVars.Delta(in battle, in battle.Cards[0], 0) == 1.5m,
        "降级后应保留 ExtraDamage 等价的小数偏移");
    Check(battle.Cards[0].Var0 == 0, "降级不应把小数偏移写入 Var0");

    // An orphaned value disappears during canonicalization.
    Piles.RemoveAt(ref battle, Pile.Hand, 1);
    DecimalVars.Canonicalize(ref battle);
    Check(battle.DecimalVarCount == 1, $"移除引用后应只留一个槽位，实际 {battle.DecimalVarCount}");

    // Fractional deltas on a card that did not opt into the table remain an explicit boundary.
    Battle strikeBattle = NewBattle();
    Registry.TryCard("STRIKE_IRONCLAD", out ushort strike);
    Piles.Append(ref strikeBattle, Pile.Play, Kernel.CreateCard(rules, strike));
    Exec strikeExec = new(ref strikeBattle, rules, default)
    {
        Card = strikeBattle.Cards[0],
        Stats = rules.Card(strike, 0),
        PlayPosition = 0,
    };
    bool fractionalRejected = false;
    try
    {
        CardEffects.AddCurrentCardVar(ref strikeExec, 0, 0.5m);
    }
    catch (UnsupportedException)
    {
        fractionalRejected = true;
    }
    Check(fractionalRejected, "未声明小数变量的卡收到小数偏移时应抛 UnsupportedException");

    // Every distinct live value fits; the next one throws CapacityException.
    Battle capacity = NewBattle();
    for (int index = 0; index < Limits.DecimalCardVars; index++)
    {
        Piles.Append(ref capacity, Pile.Hand, Kernel.CreateCard(rules, thrash));
        DecimalVars.Assign(ref capacity, ref capacity.Cards[index], 0.25m * (index + 1));
    }
    Check(capacity.DecimalVarCount == Limits.DecimalCardVars, "全部小数变量槽位应被占满");
    Piles.Append(ref capacity, Pile.Hand, Kernel.CreateCard(rules, thrash));
    bool capacityRejected = false;
    try
    {
        DecimalVars.Assign(ref capacity, ref capacity.Cards[Limits.DecimalCardVars], 99m);
    }
    catch (CapacityException)
    {
        capacityRejected = true;
    }
    Check(capacityRejected, "超出容量的小数变量应抛 CapacityException");

    Card marked = Kernel.CreateCard(rules, thrash);
    marked.TransientMark = 5;
    Check(Kernel.CloneCard(marked).TransientMark == 0, "克隆应清除 BeatDown 瞬态标记");
}

// DX3: contracts for the rules/identity fixes. The spawn contract uses the state-machine
// constructor's logged initial MoveState (InitialMoveLogged), the card contract pins the play-pile
// position of AddCurrentCardVar and Dampen's accumulator restore.
static void CheckDx3Kernel(Rules rules)
{
    Check(Registry.TryMonster("NIBBIT", out ushort nibbit), "缺少 NIBBIT");
    RulesDraft builder = new() { RootEnemyCount = 1, HasEncounter = true, EncounterSlots = ["first", "second", "third"] };
    builder.SetMonster(0, SpawnMonsterRules(nibbit));
    builder.AddSpawnableMonster(SpawnMonsterRules(nibbit, initialMoveLogged: true));
    Rules spawnRules = builder.Build();
    Battle battle = SpawnBattle();
    battle.Side = Side.Enemy;
    Exec spawnExec = new(ref battle, spawnRules, default);
    int rulesId = spawnRules.SpawnRulesId(nibbit);
    byte owner = Kernel.SpawnMonster(ref spawnExec, Limits.PlayerOwner, rulesId, 1, minion: false);
    Check((battle.Enemy(owner).Flags & EnemyFlags.NeedsInitialRoll) == 0,
        "InitialMoveLogged 的敌方召唤物应直接采用状态机构造器记录的初始行动");
    Check(battle.Enemy(owner).Move == 0 && battle.Enemy(owner).LoggedMoves == 1,
        $"InitialMoveLogged 召唤物应已记录初始行动，实际 move={battle.Enemy(owner).Move} logged={battle.Enemy(owner).LoggedMoves}");

    // AddCurrentCardVar must address Exec.PlayPosition, not the top of the play pile: a nested
    // auto-play leaves its card above the outer card until its tail moves it.
    Check(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "缺少 STRIKE_IRONCLAD");
    Battle pileBattle = NewBattle();
    Piles.Append(ref pileBattle, Pile.Play, Kernel.CreateCard(rules, strike));
    Piles.Append(ref pileBattle, Pile.Play, Kernel.CreateCard(rules, strike));
    int playStart = Piles.Start(pileBattle, Pile.Play);
    Exec pileExec = new(ref pileBattle, rules, default);
    pileExec.Card = Piles.At(ref pileBattle, Pile.Play, 0);
    pileExec.PlayPosition = 0;
    CardEffects.AddCurrentCardVar(ref pileExec, 0, 5);
    Check(Piles.At(ref pileBattle, Pile.Play, 0).Var0 == 5
        && Piles.At(ref pileBattle, Pile.Play, 1).Var0 == 0,
        "AddCurrentCardVar 应写到当前出牌位置，而不是打出堆顶");

    // Dampen downgrade: the level-0 value is read and the accumulated Rampage var is restored.
    Check(Registry.TryCard("RAMPAGE", out ushort rampage), "缺少 RAMPAGE");
    Battle rampBattle = NewBattle();
    Piles.Append(ref rampBattle, Pile.Hand, Kernel.CreateCard(rules, rampage, 1));
    int rampageIndex = Piles.Start(rampBattle, Pile.Hand);
    rampBattle.Cards[rampageIndex].Var0 = 10;
    Exec rampExec = new(ref rampBattle, rules, default);
    rampExec.Card = rampBattle.Cards[rampageIndex];
    rampExec.PlayPosition = rampageIndex;
    Kernel.DowngradeCard(ref rampExec, rampageIndex);
    Check(rampBattle.Cards[rampageIndex].Upgrade == 0
        && rampBattle.Cards[rampageIndex].DampenOriginalUpgrade == 2
        && rampBattle.Cards[rampageIndex].Var0 == 10,
        "Dampen 降级后应使用 0 级规则并恢复 Rampage 的累计 Var0");

    // The pending amount marker is transient and cleared at the stable boundary.
    Battle flagBattle = NewBattle();
    Check(Registry.TryPower("STRENGTH_POWER", out ushort strength), "缺少 STRENGTH_POWER");
    flagBattle.PowerCount = 1;
    flagBattle.Powers[0] = new Power
    {
        Id = strength, Owner = Limits.PlayerOwner, Amount = 1,
        Flags = PowerFlags.PendingAmountState,
    };
    Kernel.ClearPendingPowerAmountStates(ref flagBattle);
    Check((flagBattle.Powers[0].Flags & PowerFlags.PendingAmountState) == 0,
        "PendingAmountState 应在稳定边界清除");
}

// DX5: the Pen Nib latch names the CardPlay whose damage is doubled, the HistoryCourse cross-turn
// slot clones the previous turn's last attack, and the Battleworn Dummy's last tick is EventDefeat.
static void CheckDx5Kernel(Rules rules)
{
    // Pen Nib: a Sly card auto-played from the tenth attack's discard choice is a separate CardPlay;
    // it must not inherit the doubling (the native latch is AttackToDouble == CardSource.Original).
    Check(Registry.TryRelic("PEN_NIB", out ushort penNib), "缺少 PEN_NIB");
    Registry.TryCard("STRIKE_IRONCLAD", out ushort strike);
    Battle nibBattle = NewBattle();
    nibBattle.RelicCount = 1;
    nibBattle.Relics[0] = new Relic { Id = penNib, Counter = 9 };
    Exec nibExec = new(ref nibBattle, rules, default);
    nibExec.PlayIdentity = 7;
    Card nibCard = Kernel.CreateCard(rules, strike);
    nibExec.Card = nibCard;
    CardPlayInfo nibPlay = new(nibCard, Limits.NoOwner, 0, 1, Pile.Discard, 0);
    PenNib nibDef = (PenNib)Registry.Relic(penNib);
    Listener nibSelf = new(ListenerKind.Relic, 0, Limits.PlayerOwner);
    nibDef.BeforeCardPlayed(ref nibExec, nibSelf, nibPlay);
    Check(nibBattle.Relics[0].Counter == 0 && nibBattle.Relics[0].Aux == 7,
        "Pen Nib 第十次攻击应锁存触发它的 CardPlay 身份");
    Check(nibDef.ModifyDamageMultiplicative(ref nibExec, nibSelf,
        new DamageContext(Limits.NoOwner, Limits.PlayerOwner, ValueProps.Move,
            true, nibCard), 6m) == 2m,
        "Pen Nib 应加倍触发它的那张攻击");
    nibExec.PlayIdentity = 8;
    Card slyCard = Kernel.CreateCard(rules, strike);
    slyCard.Upgrade = 1;
    Check(nibDef.ModifyDamageMultiplicative(ref nibExec, nibSelf,
        new DamageContext(Limits.NoOwner, Limits.PlayerOwner, ValueProps.Move,
            true, slyCard), 6m) == 1m,
        "Pen Nib 的加倍不能泄漏到同一次选择里嵌套自动打出的牌");
    nibExec.LastPlayIdentity = 8;
    nibDef.AfterCardPlayed(ref nibExec, nibSelf, nibPlay);
    Check(nibBattle.Relics[0].Aux == 7, "嵌套打出的 AfterCardPlayed 不应清除外层的 Pen Nib 锁存");
    nibExec.PlayIdentity = 7;
    nibExec.LastPlayIdentity = 7;
    nibDef.AfterCardPlayed(ref nibExec, nibSelf, nibPlay);
    Check(nibBattle.Relics[0].Aux == 0, "触发卡自己的 AfterCardPlayed 应清除 Pen Nib 锁存");

    // HistoryCourse: the turn-start AutoPrePlay clones the previous turn's last attack, auto-plays it,
    // removes the dupe from combat and leaves the slot empty for the new turn.
    Check(Registry.TryRelic("HISTORY_COURSE", out ushort historyCourse), "缺少 HISTORY_COURSE");
    RulesDraft historyBuilder = new()
    {
        BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"], RootEnemyCount = 1,
        HasHistoryCourse = true,
    };
    historyBuilder.SetCard(strike, 0, new CardStats
    {
        Type = CardType.Attack, Target = TargetType.AnyEnemy, Rarity = CardRarity.Basic,
        EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false, Keywords = Keywords.None,
        MaxUpgradeLevel = 1, Vars = [6m],
    });
    Rules historyRules = historyBuilder.Build();
    Battle historyBattle = NewBattle();
    historyBattle.PlayerTurn = 2;
    historyBattle.Side = Side.Player;
    historyBattle.Phase = PlayerPhase.AutoPrePlay;
    historyBattle.Flags |= BattleFlags.PlayerHooksActive;
    historyBattle.RelicCount = 1;
    historyBattle.Relics[0] = new Relic { Id = historyCourse };
    Card previous = Kernel.CreateCard(historyRules, strike);
    historyBattle.HistoryCourseCard = previous;
    historyBattle.Flags |= BattleFlags.HistoryCourseCardPresent;
    Exec historyExec = new(ref historyBattle, historyRules, PlayerAction.EndTurn());
    Kernel.AutoPrePlay(ref historyExec);
    Check(!historyExec.NeedsChoice, "HistoryCourse 的复制品自动打出不应请求选择");
    Check(historyBattle.Counter(Counter.FinishedCardPlays) == 1,
        $"HistoryCourse 应自动打出复制品一次（实际完成 {historyBattle.Counter(Counter.FinishedCardPlays)} 次）");
    Check(historyBattle.CardCount == 0, "HistoryCourse 的复制品是 dupe，打出后应离开战斗牌堆");
    Check((historyBattle.Flags & BattleFlags.HistoryCourseCardPresent) == 0
        && Fingerprint.Equal(historyBattle.HistoryCourseCard, default),
        "HistoryCourse 的跨回合槽在 AutoPrePlay 消费后应清空");

    // Battleworn Dummy: the amount-1 branch is the event defeat; it leaves Amount at 1 (the existing
    // engine returns before any decrement/removal) and does not touch a normal combat outcome.
    Check(Registry.TryPower("BATTLEWORN_DUMMY_TIME_LIMIT_POWER", out ushort dummyLimit),
        "缺少 BATTLEWORN_DUMMY_TIME_LIMIT_POWER");
    Battle dummyBattle = NewBattle();
    dummyBattle.PlayerTurn = 1;
    dummyBattle.Side = Side.Enemy;
    dummyBattle.PowerCount = 1;
    dummyBattle.Powers[0] = new Power
    {
        Id = dummyLimit, Owner = Limits.FirstEnemyOwner, Applier = Limits.FirstEnemyOwner, Amount = 1,
    };
    Exec dummyExec = new(ref dummyBattle, rules, PlayerAction.EndTurn());
    Listener dummySelf = new(ListenerKind.Power, 0, Limits.FirstEnemyOwner);
    BattlewornDummyTimeLimitPower dummyDef = (BattlewornDummyTimeLimitPower)Registry.Power(dummyLimit);
    dummyDef.EndTurnRegular(ref dummyExec, dummySelf, Side.Enemy, participates: true);
    Check(dummyBattle.Outcome == Outcome.EventDefeat && dummyBattle.Powers[0].Amount == 1,
        "战损假人最后一层应记 EventDefeat 且保留 Amount 1（现有引擎在逃跑前返回）");
    Battle tickBattle = NewBattle();
    tickBattle.PlayerTurn = 1;
    tickBattle.Side = Side.Enemy;
    tickBattle.PowerCount = 1;
    tickBattle.Powers[0] = new Power
    {
        Id = dummyLimit, Owner = Limits.FirstEnemyOwner, Applier = Limits.FirstEnemyOwner, Amount = 2,
    };
    Exec tickExec = new(ref tickBattle, rules, PlayerAction.EndTurn());
    dummyDef.EndTurnRegular(ref tickExec, dummySelf, Side.Enemy, participates: true);
    Check(tickBattle.Outcome == Outcome.None && tickBattle.Powers[0].Amount == 1,
        "战损假人非最后一层应只递减一层");
}

// PR batch: the extra-turn pipeline (PaelsEye) must keep RoundNumber unchanged, advance the player
// turn, exhaust the triggering hand and consume only the relic's once-per-combat latch.
static void CheckPrKernel()
{
    Registry.TryRelic("PAELS_EYE", out ushort paelsEye);
    Registry.TryCard("STRIKE_IRONCLAD", out ushort strike);
    RulesDraft builder = new()
    {
        BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"], RootEnemyCount = 1,
    };
    builder.SetCard(strike, 0, new CardStats
    {
        Type = CardType.Attack, Target = TargetType.AnyEnemy, Rarity = CardRarity.Basic,
        EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false, Keywords = Keywords.None,
        MaxUpgradeLevel = 1, Vars = [6m],
    });
    Rules rules = builder.Build();
    Battle battle = NewBattle();
    battle.Round = 1;
    battle.PlayerTurn = 1;
    battle.Side = Side.Player;
    battle.Phase = PlayerPhase.Play;
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.MaxHandSize = 10;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = paelsEye, Aux = 1 };
    battle.Cards[0] = Kernel.CreateCard(rules, strike);
    battle.HandCount = 1;
    Exec x = new(ref battle, rules, PlayerAction.EndTurn());
    Kernel.AdvanceRound(ref x);
    Check(!x.NeedsChoice, "PaelsEye 的额外回合不应产生待处理选择");
    Check(battle.Round == 1, $"额外回合不应增加 RoundNumber（实际 {battle.Round}）");
    Check(battle.PlayerTurn == 2, $"额外回合应推进玩家回合数（实际 {battle.PlayerTurn}）");
    Check(battle.HandCount == 0, $"PaelsEye 应在回合末消耗整手牌（剩余 {battle.HandCount}）");
    Check(battle.Relics[0].Counter == 1, "PaelsEye 的 AfterTakingExtraTurn 应置为已用");
    Check(battle.Side == Side.Player, "额外回合应直接开始新的玩家回合");

    // PaelsLegion doubles a card's block while the cooldown is down; the root flag replaces the
    // identity-content gate that used to skip the changed-block listener pass entirely.
    Registry.TryRelic("PAELS_LEGION", out ushort paelsLegion);
    RulesDraft blockBuilder = new()
    {
        BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"], RootEnemyCount = 1,
        HasPaelsLegion = true,
    };
    blockBuilder.SetRelicVars(paelsLegion, [2m]);
    Rules blockRules = blockBuilder.Build();
    Battle blockBattle = NewBattle();
    blockBattle.Round = 1;
    blockBattle.PlayerTurn = 1;
    blockBattle.Side = Side.Player;
    blockBattle.Phase = PlayerPhase.Play;
    blockBattle.Flags |= BattleFlags.PlayerHooksActive;
    blockBattle.RelicCount = 1;
    blockBattle.Relics[0] = new Relic { Id = paelsLegion };
    Exec blockExec = new(ref blockBattle, blockRules, PlayerAction.EndTurn());
    decimal doubled = Kernel.GainBlock(ref blockExec, Limits.PlayerOwner, 5m,
        ValueProps.Move, fromCard: true);
    Check(doubled == 10m, $"PaelsLegion 冷却结束时卡牌格挡应翻倍（实际 {doubled}）");
    blockBattle.Relics[0].Counter = 2;
    decimal normal = Kernel.GainBlock(ref blockExec, Limits.PlayerOwner, 5m,
        ValueProps.Move, fromCard: true);
    Check(normal == 5m, $"PaelsLegion 冷却中不应加倍卡牌格挡（实际 {normal}）");

    // FurCoat freezes the marked-coordinate check into the rules: a spawned enemy starts at 1 HP
    // but keeps its rolled max HP (MonsterSpawnSupport.ApplyCreatureAddedRelics).
    Registry.TryMonster("NIBBIT", out ushort nibbit);
    RulesDraft coatBuilder = new()
    {
        RootEnemyCount = 1, HasEncounter = true, EncounterSlots = ["first", "second", "third"],
        FurCoatSpawnHpOne = true,
    };
    coatBuilder.SetMonster(0, SpawnMonsterRules(nibbit));
    coatBuilder.AddSpawnableMonster(SpawnMonsterRules(nibbit));
    Rules coatRules = coatBuilder.Build();
    Battle coatBattle = SpawnBattle();
    Exec coatExec = new(ref coatBattle, coatRules, default);
    byte spawnedOwner = Kernel.SpawnMonster(ref coatExec, Kernel.EnemyOwner(0), 1, slotIndex: 2, minion: false);
    Check(coatBattle.Creature(spawnedOwner).CurrentHp == 1,
        $"FurCoat 命中的生成敌人当前生命应为 1（实际 {coatBattle.Creature(spawnedOwner).CurrentHp}）");
    Check(coatBattle.Creature(spawnedOwner).MaxHp is >= 17 and <= 21,
        $"FurCoat 不应改写生成敌人的最大生命（实际 {coatBattle.Creature(spawnedOwner).MaxHp}）");

    // Bookmark: a retained, non-X card with a positive local cost gets an until-played -1 after the
    // flush (SimulatedCombatState.TriggerBookmarkAfterFlush).
    Registry.TryRelic("BOOKMARK", out ushort bookmark);
    Registry.TryCard("GOLD_AXE", out ushort goldAxe);
    RulesDraft bookmarkBuilder = new()
    {
        BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["first"], RootEnemyCount = 1,
    };
    bookmarkBuilder.SetCard(goldAxe, 0, new CardStats
    {
        Type = CardType.Attack, Target = TargetType.AnyEnemy, Rarity = CardRarity.Rare,
        EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false,
        Keywords = Keywords.Retain, MaxUpgradeLevel = 0, Vars = [],
    });
    Rules bookmarkRules = bookmarkBuilder.Build();
    Battle bookmarkBattle = NewBattle();
    bookmarkBattle.Flags |= BattleFlags.PlayerHooksActive;
    bookmarkBattle.RelicCount = 1;
    bookmarkBattle.Relics[0] = new Relic { Id = bookmark };
    bookmarkBattle.Cards[0] = Kernel.CreateCard(bookmarkRules, goldAxe);
    bookmarkBattle.HandCount = 1;
    Exec bookmarkExec = new(ref bookmarkBattle, bookmarkRules, PlayerAction.EndTurn());
    Registry.Relic(bookmark).AfterFlush(ref bookmarkExec,
        new Listener(ListenerKind.Relic, 0, Limits.PlayerOwner));
    Check(bookmarkBattle.Cards[0].EnergyModifierCount == 1,
        $"Bookmark 应给保留牌追加一条费用修饰（实际 {bookmarkBattle.Cards[0].EnergyModifierCount}）");
    Check(bookmarkBattle.Cards[0].EnergyModifiers[0].Type == CostType.Relative
        && bookmarkBattle.Cards[0].EnergyModifiers[0].Amount == -1
        && bookmarkBattle.Cards[0].EnergyModifiers[0].Expiration == CostExpiration.WhenPlayed,
        "Bookmark 的费用修饰应为直到打出的 -1");
}

// EC: contracts for the event-card kernel helpers. SetInstanceBlockValue backs ToricToughnessPower's
// block amount (an integer in the settled card block; a fractional value is an explicit boundary), and
// MakeFreeThisCombat mirrors CardModel.SetToFreeThisCombat's energy and star-cost modifiers.
static void CheckEcKernel(Rules rules)
{
    Check(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "缺少 STRIKE_IRONCLAD 定义");
    Check(Registry.TryCard("DEFEND_IRONCLAD", out ushort defend), "缺少 DEFEND_IRONCLAD 定义");
    Card free = Kernel.CreateCard(rules, defend);
    Kernel.MakeFreeThisCombat(rules, ref free);
    Check(free.EnergyModifierCount == 1, "SetToFreeThisCombat 应添加一个费用修饰");
    CostModifier modifier = free.EnergyModifiers[0];
    Check(modifier.Type == CostType.Absolute && modifier.Amount == 0
        && modifier.Expiration == CostExpiration.EndOfCombat && !modifier.IsReduceOnly,
        "SetToFreeThisCombat 应是本场战斗的绝对 0 费修饰");
    Check(free.HasTemporaryStarCost && free.TemporaryStarCost == 0
        && !free.TemporaryStarCostClearsAtTurnEnd && !free.TemporaryStarCostClearsWhenPlayed,
        "SetToFreeThisCombat 的临时星能费用不在回合末或打出时清除");

    Battle battle = NewBattle();
    battle.PowerCount = 1;
    battle.Powers[0] = new Power
    {
        Id = Registry.PowerId<ToricToughnessPower>(),
        Owner = Limits.PlayerOwner,
        Amount = 2,
    };
    Kernel.SetInstanceBlockValue(ref battle, 0, 7m);
    Check(battle.Powers[0].Aux0 == 7, "实例格挡值应写入 Aux0");
    bool rejected = false;
    try
    {
        Kernel.SetInstanceBlockValue(ref battle, 0, 7.5m);
    }
    catch (UnsupportedException)
    {
        rejected = true;
    }
    Check(rejected, "小数格挡量应抛 UnsupportedException");
}

static Battle NewBattle()
{
    Battle battle = default;
    battle.Player = new Creature
    {
        CurrentHp = 50,
        MaxHp = 50,
        Flags = CreatureFlags.Present,
    };
    battle.MaxHandSize = 10;
    battle.Energy = 3;
    battle.PotionSlotCount = 0;
    // One alive primary enemy keeps IsEnding false; targetless tests use an AnyAlly card instead.
    battle.EnemyCount = 1;
    battle.RosterCount = 1;
    battle.Roster[0] = 0;
    battle.Enemies[0].Creature = new Creature
    {
        CurrentHp = 100,
        MaxHp = 100,
        Flags = CreatureFlags.Present,
    };
    battle.Enemies[0].Flags = EnemyFlags.InCombat;
    return battle;
}

static void CheckMonsterSpawn(Rules rules)
{
    // HashSet<int> enumeration order is the ascending insertion order the native NextItem sees.
    for (int trial = 1; trial <= 40; trial++)
    {
        int minimum = 17, maximum = 21;
        HashSet<int> available = Enumerable.Range(minimum, maximum - minimum + 1).ToHashSet();
        List<int> excluded = [];
        for (int hp = minimum; hp <= maximum; hp++)
        {
            if ((hp + trial) % 4 == 0)
                excluded.Add(hp);
        }
        available.ExceptWith(excluded);
        List<int> ordered = [.. available];
        Check(ordered.SequenceEqual(ordered.OrderBy(value => value)),
            $"HashSet 枚举顺序应升序（trial={trial}）");
    }

    // Player-turn spawn: CombatId, HP draw, roster order and the immediate initial roll.
    Battle battle = SpawnBattle();
    RngState nicheBefore = battle.RngStream(RngStream.Niche);
    int expectedHp = ExpectedHp(ref nicheBefore, 17, 21, [44]);
    Exec x = new(ref battle, rules, default);
    byte spawned = Kernel.SpawnMonster(ref x, Kernel.EnemyOwner(0), 1, slotIndex: 2, minion: false);
    Check(spawned == Kernel.EnemyOwner(1), "生成应返回新 owner");
    Check(battle.EnemyCount == 2 && battle.RosterCount == 2, "生成后已知敌人数与名单数应各加一");
    Check(battle.NextCombatId == 8 && battle.Enemies[1].CombatId == 7, "CombatId 应从 NextCombatId 分配并递增");
    Check(battle.Enemies[1].Creature.CurrentHp == expectedHp && battle.Enemies[1].Creature.MaxHp == expectedHp,
        $"生成生命应为排除现有 MaxHp 后的 Niche 取值（期望 {expectedHp}，实际 {battle.Enemies[1].Creature.CurrentHp}）");
    Check(SameRng(battle.RngStream(RngStream.Niche), nicheBefore), "生成只应消耗一次 Niche 抽样");
    Check(battle.Enemies[1].Rules == 1 && battle.Enemies[1].Slot == 2, "生成敌人应记录规则编号与槽位下标");
    Check(battle.Roster[0] == 0 && battle.Roster[1] == 1, "新槽位在名单末尾时直接追加");
    Check(battle.Enemies[1].Move == 0 && battle.Enemies[1].LoggedMoves == 1
        && (battle.Enemies[1].Flags & EnemyFlags.NeedsInitialRoll) == 0,
        "玩家回合内生成应立即掷出初始行动并写日志");
    Check(battle.Enemies[1].Ai[0] == 2 && battle.Enemies[1].Ai[1] == 0, "生成应写入编译的初始 AI 寄存器");

    // Stable slot insertion: a same-slot enemy goes after the existing one, before a later slot.
    Kernel.SpawnMonster(ref x, Kernel.EnemyOwner(0), 1, slotIndex: 1, minion: false);
    Check(battle.RosterCount == 3 && battle.Roster[0] == 0 && battle.Roster[1] == 2 && battle.Roster[2] == 1,
        "同槽位生成应稳定插在原槽位敌人之后");

    // A null slot name sorts as -1: before named slots, after earlier nulls.
    Battle nullSlot = SpawnBattle();
    nullSlot.Enemies[1] = nullSlot.Enemies[0];
    nullSlot.Enemies[1].Slot = (byte)Kernel.NoSlot;
    nullSlot.Enemies[1].CombatId = 2;
    nullSlot.EnemyCount = 2;
    nullSlot.RosterCount = 2;
    nullSlot.Roster[0] = 1;
    nullSlot.Roster[1] = 0;
    x = new Exec(ref nullSlot, rules, default);
    Kernel.SpawnMonster(ref x, Kernel.EnemyOwner(0), 1, Kernel.NoSlot, minion: false);
    Check(nullSlot.RosterCount == 3 && nullSlot.Roster[0] == 1 && nullSlot.Roster[1] == 2 && nullSlot.Roster[2] == 0,
        "无槽名生成应排在有名字的槽位之前，并保持既有空槽顺序");

    // NextFreeSlot / LastFreeSlot: null without an encounter, "" when full, otherwise the first/last.
    Check(Kernel.NextFreeSlot(battle, rules) == "first" && Kernel.LastFreeSlot(battle, rules) == "first",
        "只剩 first 时两者都应返回 first");
    Check(Kernel.NextFreeSlot(battle, rules) == "first" && Kernel.LastFreeSlot(battle, rules) == "first",
        "槽位查询不应改变状态");
    Rules noEncounter = NoEncounterRules();
    Check(Kernel.NextFreeSlot(battle, noEncounter) == null && Kernel.LastFreeSlot(battle, noEncounter) == null,
        "无遭遇时槽位查询应返回 null");

    // Exhausted slots: NextFreeSlot returns "", LastFreeSlot returns null.
    Battle full = SpawnBattle();
    full.RosterCount = 3;
    full.EnemyCount = 3;
    for (int index = 0; index < 3; index++)
    {
        full.Enemies[index] = full.Enemies[0];
        full.Enemies[index].Slot = (byte)index;
        full.Roster[index] = (byte)index;
    }
    Check(Kernel.NextFreeSlot(full, rules) == "" && Kernel.LastFreeSlot(full, rules) == null,
        "无空槽时 NextFreeSlot 应为空串、LastFreeSlot 应为 null");

    // HP exclusion follows the roster's current MaxHp, including earlier spawns; an exhausted range
    // falls back to NextInt(min, max + 1).
    battle = SpawnBattle();
    battle.Enemies[0].Creature.MaxHp = 17;
    x = new Exec(ref battle, rules, default);
    nicheBefore = battle.RngStream(RngStream.Niche);
    expectedHp = ExpectedHp(ref nicheBefore, 17, 21, [17]);
    Kernel.SpawnMonster(ref x, Kernel.EnemyOwner(0), 1, slotIndex: 0, minion: false);
    Check(battle.Enemies[1].Creature.MaxHp == expectedHp, "生命排除应使用名单敌人的当前 MaxHp");
    battle.Enemies[0].Creature.MaxHp = 18;
    nicheBefore = battle.RngStream(RngStream.Niche);
    expectedHp = ExpectedHp(ref nicheBefore, 17, 21, [18, battle.Enemies[1].Creature.MaxHp]);
    Kernel.SpawnMonster(ref x, Kernel.EnemyOwner(0), 1, slotIndex: 2, minion: false);
    Check(battle.Enemies[2].Creature.MaxHp == expectedHp, "生命排除应包含同回合先前生成的敌人");

    battle = SpawnBattle();
    for (int index = 0; index < 5; index++)
    {
        battle.Enemies[index] = battle.Enemies[0];
        battle.Enemies[index].Creature.MaxHp = 17 + index;
        battle.Enemies[index].CombatId = (uint)(index + 1);
        battle.Enemies[index].Slot = (byte)(index % 3);
        battle.Roster[index] = (byte)index;
    }
    battle.EnemyCount = 5;
    battle.RosterCount = 5;
    x = new Exec(ref battle, rules, default);
    nicheBefore = battle.RngStream(RngStream.Niche);
    int expectedFallback = nicheBefore.NextInt(17, 22);
    Kernel.SpawnMonster(ref x, Kernel.EnemyOwner(0), 1, slotIndex: 0, minion: false);
    Check(battle.Enemies[5].Creature.MaxHp == expectedFallback,
        $"可取值耗尽时应使用 NextInt(min, max+1)（期望 {expectedFallback}）");

    // Enemy-turn spawn: pending initial roll until the end-of-enemy-turn pass.
    battle = SpawnBattle();
    battle.Side = Side.Enemy;
    battle.Enemies[0].Flags |= EnemyFlags.PerformedMoveThisRound;
    x = new Exec(ref battle, rules, default);
    Kernel.SpawnMonster(ref x, Kernel.EnemyOwner(0), 1, slotIndex: 2, minion: false);
    Check((battle.Enemies[1].Flags & EnemyFlags.NeedsInitialRoll) != 0, "敌人回合内生成应保留待掷标记");
    Check(battle.Enemies[1].Move == 0 && battle.Enemies[1].LoggedMoves == 0, "待掷时行动节点应为占位行动且不写日志");
    Kernel.PrepareMonsterMovesForNextRound(ref x);
    Check((battle.Enemies[1].Flags & EnemyFlags.NeedsInitialRoll) == 0
        && battle.Enemies[1].Move == 0 && battle.Enemies[1].LoggedMoves == 1,
        "敌人回合结束时应对待掷敌人掷出初始行动");

    // Illusion revive: forced synthetic node, dynamic follow-up, heal to max and advancement.
    battle = SpawnBattle();
    x = new Exec(ref battle, rules, default);
    byte rat = Kernel.EnemyOwner(0);
    Kernel.LogMonsterMove(ref battle.Enemies[0], 0);
    battle.Enemies[0].Creature.CurrentHp = 0;
    Kernel.BeginIllusionRevive(ref x, rat);
    Check(battle.Enemies[0].DeathPhase == DeathPhase.Reviving
        && battle.Enemies[0].Move == 2 && battle.Enemies[0].ForcedMove == 0,
        "幻象复活应写复活节点与最近行动后继");
    Check(Kernel.CanPerformMonsterMove(battle.Enemies[0]), "复活中的敌人应可执行行动");
    Kernel.ExecuteMonsterMove(ref x, rat);
    Check(battle.Enemies[0].Creature.CurrentHp == 44 && battle.Enemies[0].DeathPhase == DeathPhase.None,
        "REVIVE_MOVE 应回满生命并清除复活阶段");
    battle.Enemies[0].Flags |= EnemyFlags.PerformedMoveThisRound;
    Kernel.PrepareMonsterMovesForNextRound(ref x);
    Check(battle.Enemies[0].Move == 0 && battle.Enemies[0].ForcedMove == byte.MaxValue,
        "复活行动执行后应推进到动态后继并清空 ForcedMove");
}

// A rules table with a root entry and one spawnable entry, both for NIBBIT, plus the synthetic
// revive node so the revive contract can force it.
static Rules SpawnRules()
{
    Registry.TryMonster("NIBBIT", out ushort nibbit);
    RulesDraft builder = new() { RootEnemyCount = 1, HasEncounter = true, EncounterSlots = ["first", "second", "third"] };
    builder.SetMonster(0, SpawnMonsterRules(nibbit));
    builder.AddSpawnableMonster(SpawnMonsterRules(nibbit));
    return builder.Build();
}

static Rules NoEncounterRules()
{
    Registry.TryMonster("NIBBIT", out ushort nibbit);
    RulesDraft builder = new();
    builder.SetMonster(0, SpawnMonsterRules(nibbit));
    return builder.Build();
}

static MonsterRules SpawnMonsterRules(ushort nibbit, bool initialMoveLogged = false) => new()
{
    MonsterId = nibbit,
    Nodes =
    [
        new MonsterNode { NativeId = "BUTT_MOVE", Kind = MonsterNodeKind.Move, FollowUp = 0, Attacks = [new(12, 1)] },
        new MonsterNode { NativeId = "STUNNED", Kind = MonsterNodeKind.Move, MustPerformOnceBeforeTransitioning = true },
        new MonsterNode { NativeId = "REVIVE_MOVE", Kind = MonsterNodeKind.Move, MustPerformOnceBeforeTransitioning = true },
    ],
    InitialNode = 0,
    PendingInitialMoveNode = 0,
    StunnedNode = 1,
    ReviveNode = 2,
    InitialHpMin = 17,
    InitialHpMax = 21,
    StaticInts = [],
    InitialAiRegisters = [2, 0],
    InitialMoveLogged = initialMoveLogged,
};

static bool SameRng(in RngState left, in RngState right)
    => left.Counter == right.Counter && left.S0 == right.S0 && left.S1 == right.S1
        && left.S2 == right.S2 && left.S3 == right.S3;

static Battle SpawnBattle()
{
    Registry.TryMonster("NIBBIT", out ushort nibbit);
    Battle battle = new()
    {
        Round = 1, PlayerTurn = 1, Energy = 3, Side = Side.Player, Phase = PlayerPhase.Play,
        Flags = BattleFlags.PlayerHooksActive, MaxHandSize = 10,
        EnemyCount = 1, RosterCount = 1, NextCombatId = 7,
        Player = new Creature { CurrentHp = 80, MaxHp = 80, Flags = CreatureFlags.Present },
    };
    battle.Enemies[0] = new Enemy
    {
        Creature = new Creature { CurrentHp = 44, MaxHp = 44, Flags = CreatureFlags.Present },
        MonsterId = nibbit, Rules = 0, Slot = 1, CombatId = 1, Move = 0, ForcedMove = byte.MaxValue,
        Flags = EnemyFlags.InCombat,
    };
    battle.Roster[0] = 0;
    battle.RngStream(RngStream.Shuffle) = RngState.FromSerializable(0, 1, 2, 3, 4);
    battle.RngStream(RngStream.MonsterAi) = RngState.FromSerializable(0, 5, 6, 7, 8);
    battle.RngStream(RngStream.Niche) = RngState.FromSerializable(0, 9, 10, 11, 12);
    return battle;
}

// Reference CreatePredictedMonster HP draw: the eligible values are the ascending HashSet
// enumeration order; an empty set uses NextInt(min, max + 1).
static int ExpectedHp(ref RngState niche, int minimum, int maximum, IReadOnlyList<int> rosterMaxHp)
{
    HashSet<int> available = Enumerable.Range(minimum, maximum - minimum + 1).ToHashSet();
    available.ExceptWith(rosterMaxHp);
    if (available.Count == 0)
        return niche.NextInt(minimum, maximum + 1);
    int index = niche.NextInt(0, available.Count);
    return available.ElementAt(index);
}


// KG2: contracts for the new dispatch points. The helper definitions below are registered only in
// this pure-kernel executable; they log call order and prove gate, listener scope and multi-pass order.
static void CheckA3Kernel(Rules rules)
{
    Check(Registry.TryPower("A3_TEST_RAMPART_POWER", out ushort rampart), "缺少 A3 Rampart 测试 Power");
    Check(Registry.TryRelic("A3_TEST_GENERATED_RELIC", out ushort generatedRelic), "缺少 A3 生成遗物");
    Check(Registry.TryPower("A3_TEST_ENDING_POWER", out ushort ending), "缺少 A3 终局测试 Power");
    Check(Registry.TryPower("HEX_POWER", out ushort hex), "缺少 HEX_POWER");
    Check(Registry.TryAffliction("HEXED", out ushort hexed), "缺少 HEXED");
    Check(Registry.TryCard("DEFEND_IRONCLAD", out ushort defend), "缺少 DEFEND_IRONCLAD");

    // PersistentPowerSupport.TriggerRampart: the player-side turn start calls the Rampart hook.
    Battle battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Exec x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, rampart, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Kg2TestLog.Clear();
    Span<byte> player = stackalloc byte[1];
    player[0] = Limits.PlayerOwner;
    Kernel.TriggerSideTurnStart(ref x, Side.Player, player, decrementPlating: false);
    Check(Kg2TestLog.Lines.Contains("rampart"), "A3 Rampart 派发未生效");

    // AfterCardGeneratedForCombat receives the absolute card position.
    Battle generated = NewBattle();
    generated.Flags |= BattleFlags.PlayerHooksActive;
    generated.RelicCount = 1;
    generated.Relics[0] = new Relic { Id = generatedRelic };
    Exec g = new(ref generated, rules, default);
    Piles.Append(ref generated, Pile.Hand, Kernel.CreateCard(rules,
        defend));
    Kg2TestLog.Clear();
    Check(Kernel.AddGeneratedCard(ref g,
        Kernel.CreateCard(rules, defend), Pile.Discard),
        "A3 生成卡加入失败");
    Check(Kg2TestLog.Lines.Contains("generated-ok"), "A3 AfterCardGeneratedForCombat 位置错误");

    // HexPower.TryModifyKeywordsInCombat gives a Hexed card Ethereal without storing it.
    Battle keywords = NewBattle();
    Exec k = new(ref keywords, rules, default);
    Card hexedCard = Kernel.CreateCard(rules, defend);
    hexedCard.AfflictionId = (byte)(hexed + 1);
    Check(!Kernel.HasKeyword(keywords, hexedCard, Keywords.Ethereal), "A3 无 Hex 时不应有 Ethereal");
    Kernel.ApplyPower(ref k, Limits.PlayerOwner, hex, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref k);
    Check(Kernel.HasKeyword(keywords, hexedCard, Keywords.Ethereal), "A3 Hex 未授予 Ethereal");

    // ShouldStopCombatFromEnding: a positive flag keeps the ending check false.
    Battle endingBattle = NewBattle();
    endingBattle.EnemyCount = 1;
    endingBattle.RosterCount = 1;
    endingBattle.Roster[0] = 0;
    ref Enemy enemy = ref endingBattle.Enemies[0];
    enemy.Creature.Flags = CreatureFlags.Present;
    enemy.Creature.CurrentHp = 0;
    enemy.Flags = EnemyFlags.InCombat;
    endingBattle.PowerCount = 1;
    endingBattle.Powers[0] = new Power { Id = ending, Owner = Limits.FirstEnemyOwner, Amount = 1 };
    Check(!Kernel.IsEnding(endingBattle), "A3 PreventsCombatEnding 未阻止终局");
    endingBattle.Powers[0].Amount = 0;
    Check(Kernel.IsEnding(endingBattle), "A3 无阻止 Power 时终局应成立");
}

static void CheckKg2Hooks(Rules rules)
{
    Check(Registry.TryCard("DEFEND_IRONCLAD", out ushort defend), "缺少 DEFEND_IRONCLAD 定义");
    Check(Registry.TryPower("KG2_TEST_COST_POWER", out ushort costPower), "缺少 KG2 费用 Power");
    Check(Registry.TryPower("KG2_TEST_PLAY_COUNT_POWER", out ushort countPower), "缺少 KG2 出牌次数 Power");
    Check(Registry.TryPower("KG2_TEST_RESULT_LOCATION_POWER", out ushort locationPower), "缺少 KG2 去向 Power");
    Check(Registry.TryPower("KG2_TEST_POWER_CHANGE_POWER", out ushort changePower), "缺少 KG2 Power 数量 Power");
    Check(Registry.TryPower("KG2_TEST_DAMAGE_SOURCE_POWER", out ushort damagePower), "缺少 KG2 伤害来源 Power");
    Check(Registry.TryRelic("KG2_TEST_COST_RELIC", out ushort costRelic), "缺少 KG2 费用遗物");
    Check(Registry.TryRelic("KG2_TEST_X_VALUE_RELIC", out ushort xRelic), "缺少 KG2 X 值遗物");
    Check(Registry.TryRelic("KG2_TEST_RESET_RELIC", out ushort resetRelic), "缺少 KG2 能量重置遗物");
    Check(Registry.TryRelic("KG2_TEST_RECEIVED_RELIC", out ushort receivedRelic), "缺少 KG2 Power 数量接收遗物");
    Check(Registry.TryRelic("KG2_TEST_SHUFFLE_RELIC", out ushort shuffleRelic), "缺少 KG2 洗牌遗物");
    Check(Registry.TryRelic("KG2_TEST_ENTERED_RELIC", out ushort enteredRelic), "缺少 KG2 进战斗遗物");
    Check(Registry.TryRelic("KG2_TEST_POWER_CHANGE_RELIC", out ushort changeRelic), "缺少 KG2 Power 数量遗物");
    Check(Registry.TryPower("VULNERABLE_POWER", out ushort VulnerableId), "缺少 VULNERABLE_POWER");
    Check(Registry.TryPower("STRENGTH_POWER", out ushort StrengthId), "缺少 STRENGTH_POWER");

    Battle battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Card card = Kernel.CreateCard(rules, defend);
    Piles.Append(ref battle, Pile.Hand, card);
    battle.RelicCount = 6;
    battle.Relics[0] = new Relic { Id = costRelic };
    battle.Relics[1] = new Relic { Id = xRelic };
    battle.Relics[2] = new Relic { Id = resetRelic };
    battle.Relics[3] = new Relic { Id = receivedRelic };
    battle.Relics[4] = new Relic { Id = shuffleRelic };
    battle.Relics[5] = new Relic { Id = enteredRelic };
    Exec x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, costPower, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);

    // Normal pass order: power then relic; late pass: power. Multiplicative late makes the order visible.
    Kg2TestLog.Clear();
    int cost = Kernel.EnergyCostWithModifiers(battle, rules, card, Pile.Hand);
    Check(cost == 10, $"KG2 费用顺序应为 10，实际 {cost} log={string.Join(",", Kg2TestLog.Lines)}");
    Check(string.Join(",", Kg2TestLog.Lines) == "cost-normal-power,cost-normal-relic,cost-late-power",
        $"KG2 费用监听顺序错误：{string.Join(",", Kg2TestLog.Lines)}");
    battle.Outcome = Outcome.Victory;
    int endedCost = Kernel.EnergyCostWithModifiers(battle, rules, card, Pile.Hand);
    Check(endedCost == 1 && Kg2TestLog.Lines.Count == 3, "KG2 费用 Hook 应在战斗结束时跳过");
    battle.Outcome = Outcome.None;

    // Result location: the changed power is recorded and its after hook runs.
    Kg2TestLog.Clear();
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, locationPower, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Span<Listener> locationModifiers = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
    Pile location = HookDispatch.ModifyCardPlayResultLocation(ref x, card, false, 0, Pile.Discard,
        PilePosition.Bottom, locationModifiers, out int locationCount, out PilePosition locationPosition);
    Check(location == Pile.Draw && locationPosition == PilePosition.Top && locationCount == 1,
        "KG2 去向 Hook 未生效（含 Draw/Top 位置）");
    HookDispatch.AfterModifyingCardPlayResultLocation(ref x, card, location, locationModifiers[..locationCount]);
    Check(Kg2TestLog.Lines.Contains("location-after"), "KG2 去向 After Hook 未派发");

    // Play count: changed listener recorded, after only for changed listeners.
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, countPower, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Kg2TestLog.Clear();
    Span<Listener> countModifiers = stackalloc Listener[HookDispatch.MaxListenerCount(x.B)];
    int playCount = HookDispatch.ModifyCardPlayCount(ref x, card, Limits.NoOwner, 1, countModifiers,
        out int countModifierCount);
    Check(playCount == 2 && countModifierCount == 1, "KG2 出牌次数 Hook 未生效");
    HookDispatch.AfterModifyingCardPlayCount(ref x, card, countModifiers[..countModifierCount]);
    Check(Kg2TestLog.Lines.Contains("count-after"), "KG2 出牌次数 After Hook 未派发");

    // AfterPowerAmountChanged is power-only; the relic override must not be visited.
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, changePower, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    // The relic hook is registered on a relic, not on a power and must not be visited by the
    // power-only AfterPowerAmountChanged dispatch.
    battle.RelicCount++;
    battle.Relics[battle.RelicCount - 1] = new Relic { Id = changeRelic };
    Kg2TestLog.Clear();
    Kernel.ApplyPower(ref x, Limits.FirstEnemyOwner, VulnerableId, 1,
        Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Check(Kg2TestLog.Lines.Contains("power-changed"), "KG2 AfterPowerAmountChanged 未派发");
    Check(Kg2TestLog.Lines.Contains("power-changed-target"), "KG2 AfterPowerAmountChanged 未传目标");
    Check(!Kg2TestLog.Lines.Contains("power-changed-relic"), "KG2 AfterPowerAmountChanged 不应派发遗物");

    // AfterShuffle, ModifyXValue, ShouldPlayerResetEnergy, AfterCardEnteredCombat.
    Kg2TestLog.Clear();
    Piles.Append(ref battle, Pile.Discard, Kernel.CreateCard(rules, defend));
    battle.DrawCount = 0;
    Kernel.Shuffle(ref x);
    Check(Kg2TestLog.Lines.Contains("shuffle"), "KG2 AfterShuffle 未派发");
    Check(HookDispatch.ModifyXValue(battle, rules, card, 5) == 6, "KG2 ModifyXValue 未生效");
    Check(!HookDispatch.ShouldPlayerResetEnergy(battle, rules, Limits.PlayerOwner), "KG2 能量重置判定未生效");
    Kg2TestLog.Clear();
    Check(Kernel.AddGeneratedCard(ref x, Kernel.CreateCard(rules, defend), Pile.Hand),
        "KG2 生成卡加入失败");
    Check(Kg2TestLog.Lines.Contains("entered"), "KG2 遗物 AfterCardEnteredCombat 未派发");

    // Received power amount: the doubling pass runs before the Artifact check and the after records it.
    Kg2TestLog.Clear();
    int applied = Kernel.ApplyPower(ref x, Limits.PlayerOwner,
        StrengthId, 2, Limits.PlayerOwner);
    Check(applied == 4, $"KG2 接收 Power 数量应为 4，实际 {applied}");
    Check(Kg2TestLog.Lines.Contains("received-after"), "KG2 AfterModifyingPowerAmountReceived 未派发");

    // AfterDamageReceived carries the card source.
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, damagePower, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Kg2TestLog.Clear();
    Span<byte> enemy = stackalloc byte[1];
    enemy[0] = Limits.FirstEnemyOwner;
    Kernel.Damage(ref x, enemy, 1, ValueProps.Move, Limits.PlayerOwner, true, card);
    Check(Kg2TestLog.Lines.Contains("damage-card"), $"KG2 AfterDamageReceived 缺少卡牌来源 log={string.Join(",", Kg2TestLog.Lines)} outcome={battle.Outcome} flags={battle.Flags} hooks={Registry.Power(damagePower).Hooks}");
    Kg2TestLog.Clear();
    Kernel.Damage(ref x, enemy, 1, ValueProps.Move, Limits.PlayerOwner, false, default);
    Check(Kg2TestLog.Lines.Contains("damage-none"), $"KG2 AfterDamageReceived 无来源分支错误 log={string.Join(",", Kg2TestLog.Lines)}");
}

// KA: card afflictions/enchantments, their modification hooks and the enchantment relic.
static void CheckKaAttachments()
{
    Rules rules = KaRules();
    Check(Registry.TryAffliction("TAINTED", out ushort tainted), "缺少 TAINTED 定义");
    Check(Registry.TryAffliction("ENTANGLED", out ushort entangled), "缺少 ENTANGLED 定义");
    Check(Registry.TryAffliction("RINGING", out ushort ringing), "缺少 RINGING 定义");
    Check(Registry.TryAffliction("SMOG", out ushort smog), "缺少 SMOG 定义");
    Check(Registry.TryEnchantment("SHARP", out ushort sharp), "缺少 SHARP 定义");
    Check(Registry.TryEnchantment("GLAM", out _), "缺少 GLAM 定义");
    Check(Registry.TryEnchantment("SPIRAL", out _), "缺少 SPIRAL 定义");
    Check(Registry.TryPower("VITAL_SPARK_POWER", out ushort vitalSpark), "缺少 VITAL_SPARK_POWER");
    Check(Registry.TryPower("SMOGGY_POWER", out ushort smoggy), "缺少 SMOGGY_POWER");
    Check(Registry.TryPower("TANGLED_POWER", out ushort tangled), "缺少 TANGLED_POWER");
    Check(Registry.TryPower("RINGING_POWER", out ushort ringingPower), "缺少 RINGING_POWER");
    Check(Registry.TryPower("TAINTED_POWER", out ushort taintedPower), "缺少 TAINTED_POWER");
    Check(Registry.TryRelic("MYSTIC_LIGHTER", out ushort lighter), "缺少 MYSTIC_LIGHTER");

    Check(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "缺少 STRIKE_IRONCLAD");
    Check(Registry.TryCard("DEFEND_IRONCLAD", out ushort defend), "缺少 DEFEND_IRONCLAD");
    Battle battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, strike));
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, defend));
    Exec x = new(ref battle, rules, default);
    int attackPosition = 0;
    int skillPosition = 1;

    // CardCmd.Afflict: Tainted is Skill-only and stacks; Entangled is non-stackable.
    Kernel.ApplyAffliction(ref x, skillPosition, tainted, 2);
    Check(battle.Cards[skillPosition].AfflictionId == tainted + 1
        && battle.Cards[skillPosition].AfflictionAmount == 2, "Tainted 应施加到技能牌");
    Kernel.ApplyAffliction(ref x, skillPosition, tainted, 3);
    Check(battle.Cards[skillPosition].AfflictionAmount == 5, "可叠加苦难应累加层数");
    Kernel.ApplyAffliction(ref x, attackPosition, tainted, 1);
    Check(battle.Cards[attackPosition].AfflictionId == 0, "Tainted 不应施加到攻击牌");
    Kernel.ApplyAffliction(ref x, attackPosition, entangled, 1);
    Kernel.ApplyAffliction(ref x, attackPosition, entangled, 1);
    Check(battle.Cards[attackPosition].AfflictionId == entangled + 1
        && battle.Cards[attackPosition].AfflictionAmount == 1, "不可叠加苦难只应保留首次层数");
    Kernel.ClearAfflictions(ref battle, entangled);
    Check(battle.Cards[attackPosition].AfflictionId == 0, "ClearAfflictions 应清除同类苦难");

    // Vital Spark one-time entry affliction, amount-change rewrite and removal clearing.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, defend));
    x = new(ref battle, rules, default);
    byte enemyOwner = Kernel.EnemyOwner(0);
    Kernel.ApplyPower(ref x, enemyOwner, vitalSpark, 2, enemyOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Kernel.NormalizeCardAfflictions(ref x);
    Check(battle.Cards[0].AfflictionId == tainted + 1 && battle.Cards[0].AfflictionAmount == 2,
        "新进场技能牌应获得 Vital Spark 的 Tainted");
    Kernel.SetPowerAmount(ref battle, enemyOwner, vitalSpark, 3);
    Kernel.NormalizeCardAfflictions(ref x);
    Check(battle.Cards[0].AfflictionAmount == 3, "Vital Spark 数量变化应同步 Tainted 层数");
    Kernel.SetPowerAmount(ref battle, enemyOwner, vitalSpark, 0);
    Kernel.NormalizeCardAfflictions(ref x);
    Check(battle.Cards[0].AfflictionId == 0, "Life Spark 移除后应清除 Tainted");

    // SmoggyPower: a Skill played this side turn makes newly entered Skills unplayable.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, defend));
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, smoggy, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    battle.SetCounter(Counter.SkillPlayedThisSideTurn, 1);
    Kernel.NormalizeCardAfflictions(ref x);
    Check(battle.Cards[0].AfflictionId == smog + 1, "SmoggyPower 应给进场技能牌施加 Smog");
    Check(!Kernel.CanPlayCard(battle, rules, 0), "Smog 牌应不可打出");

    // TangledPower cost and RingingPower second-card restriction.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, strike));
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, tangled, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Kernel.ApplyAffliction(ref x, 0, entangled, 1);
    Check(Kernel.EnergyCostWithModifiers(battle, rules, battle.Cards[0], Pile.Hand) == 2,
        "TangledPower 应给 Entangled 牌加 1 费");
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, ringingPower, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Kernel.ClearAfflictions(ref battle, entangled);
    Kernel.ApplyAffliction(ref x, 0, ringing, 1);
    battle.SetCounter(Counter.CardPlaySeriesStartedThisTurn, 0);
    Check(Kernel.CanPlayCard(battle, rules, 0), "Ringing 牌应能作为本回合第一张打出");
    battle.SetCounter(Counter.CardPlaySeriesStartedThisTurn, 1);
    Check(!Kernel.CanPlayCard(battle, rules, 0), "Ringing 牌不应作为本回合第二张打出");

    // TaintedPower adds to the player's powered attacks.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, taintedPower, 2, Limits.NoOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    decimal taintedDamage = HookDispatch.ModifyDamage(ref x, Limits.PlayerOwner, enemyOwner, 6m,
        ValueProps.Move, hasCardSource: false, default);
    Check(taintedDamage == 8m, $"TaintedPower 应加 2 伤害，实际 {taintedDamage}");

    // Enchantment damage/block modifiers and play count.
    battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, strike));
    x = new(ref battle, rules, default);
    ref Card enchanted = ref battle.Cards[0];
    enchanted.EnchantmentId = checked((byte)(sharp + 1));
    enchanted.EnchantmentAmount = 3;
    decimal sharpDamage = HookDispatch.ModifyDamage(ref x, enemyOwner, Limits.PlayerOwner, 6m,
        ValueProps.Move, hasCardSource: true, enchanted);
    Check(sharpDamage == 9m, $"Sharp 应加 3 伤害，实际 {sharpDamage}");
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = lighter };
    decimal lighterDamage = HookDispatch.ModifyDamage(ref x, enemyOwner, Limits.PlayerOwner, 6m,
        ValueProps.Move, hasCardSource: true, enchanted);
    Check(lighterDamage == 18m, $"MysticLighter 应给附魔攻击加 9 伤害，实际 {lighterDamage}");

    Card glam = Kernel.CreateCard(rules, strike);
    glam.EnchantmentId = checked((byte)(Registry.EnchantmentId<Glam>() + 1));
    Check(Kernel.EnchantedReplayCount(glam) == 1, "Glam 首次应加 1 次重放");
    glam.EnchantmentPrivate = 1;
    Check(Kernel.EnchantedReplayCount(glam) == 0, "Glam 已用后不应再加重放");
    Card spiral = Kernel.CreateCard(rules, strike);
    spiral.EnchantmentId = checked((byte)(Registry.EnchantmentId<Spiral>() + 1));
    Check(Kernel.EnchantedReplayCount(spiral) == 1, "Spiral 应加 1 次重放");
}

static Rules KaRules()
{
    RulesDraft builder = new() { BaseMaxEnergy = 3, HasEncounter = true, EncounterSlots = ["a"], RootEnemyCount = 0 };
    Registry.TryCard("STRIKE_IRONCLAD", out ushort strike);
    Registry.TryCard("DEFEND_IRONCLAD", out ushort defend);
    Registry.TryRelic("MYSTIC_LIGHTER", out ushort lighter);
    builder.SetCard(strike, 0, new CardStats
    {
        Type = CardType.Attack, Target = TargetType.AnyEnemy, Rarity = CardRarity.Basic,
        EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false, Keywords = Keywords.None,
        MaxUpgradeLevel = 1, Vars = [6m],
    });
    builder.SetCard(defend, 0, new CardStats
    {
        Type = CardType.Skill, Target = TargetType.Self, Rarity = CardRarity.Basic,
        EnergyCost = 1, CostsX = false, StarCost = -1, StarCostsX = false, Keywords = Keywords.None,
        MaxUpgradeLevel = 1, Vars = [5m],
    });
    builder.SetRelicVars(lighter, [9m]);
    return builder.Build();
}

// RG: star and forge kernel contracts. The helper definitions below are registered only in this
// pure-kernel executable; the star relic/power log the dispatch order and the paid amount.
static void CheckRgKernel(Rules rules)
{
    Check(Registry.TryCard("SOVEREIGN_BLADE", out ushort blade), "缺少 SOVEREIGN_BLADE 定义");
    Check(Registry.TryCard("RG_TEST_STAR_CARD", out ushort starCard), "缺少 RG_TEST_STAR_CARD 定义");
    Check(Registry.TryPower("RG_TEST_STAR_POWER", out ushort starPower), "缺少 RG_TEST_STAR_POWER");
    Check(Registry.TryRelic("RG_TEST_STAR_RELIC", out ushort starRelic), "缺少 RG_TEST_STAR_RELIC");

    // Star cost: the hook only sees non-negative costs, runs in listener order and is gated by combat
    // ending; the temporary cost replaces the base; X star cost returns every current star without
    // dispatching the hook.
    Battle battle = NewBattle();
    battle.Flags |= BattleFlags.PlayerHooksActive;
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = starRelic };
    Card card = Kernel.CreateCard(rules, starCard);
    Kg2TestLog.Clear();
    Check(Kernel.StarCostWithModifiers(battle, rules, card, Pile.Hand) == 0,
        "RG 星能费用 Hook 未生效（应把 2 降到 -2 后钳制到 0）");
    Check(Kg2TestLog.Lines.Count == 1 && Kg2TestLog.Lines[0] == "star-cost:2",
        $"RG 星能费用 Hook 应只收到一次 2，实际 {string.Join(",", Kg2TestLog.Lines)}");
    card.SetTemporaryStarCost(5, clearsAtTurnEnd: true, clearsWhenPlayed: true);
    Kg2TestLog.Clear();
    Check(Kernel.StarCostWithModifiers(battle, rules, card, Pile.Hand) == 1
        && Kg2TestLog.Lines.Count == 1 && Kg2TestLog.Lines[0] == "star-cost:5",
        "RG 临时星能费用应替换基础费用");
    card.Flags |= CardFlags.StarCostsX;
    battle.Stars = 3;
    Kg2TestLog.Clear();
    Check(Kernel.StarCostWithModifiers(battle, rules, card, Pile.Hand) == 3
        && Kg2TestLog.Lines.Count == 0, "RG X 星能费用应返回当前星能且不派发 Hook");
    card.Flags &= ~CardFlags.StarCostsX;
    battle.Outcome = Outcome.Victory;
    Kg2TestLog.Clear();
    Check(Kernel.StarCostWithModifiers(battle, rules, card, Pile.Hand) == 5
        && Kg2TestLog.Lines.Count == 0, "RG 战斗结束时星能费用 Hook 应跳过");
    battle.Outcome = Outcome.None;
    card.ClearTemporaryStarCost();
    card.BaseStarCost = -1;
    Kg2TestLog.Clear();
    Check(Kernel.StarCostWithModifiers(battle, rules, card, Pile.Hand) == 0
        && Kg2TestLog.Lines.Count == 0, "RG 负星能费用不应派发 Hook");

    // AfterStarsSpent: the existing engine runs the relics before the powers, not in listener order.
    Exec x = new(ref battle, rules, default);
    Kernel.ApplyPower(ref x, Limits.PlayerOwner, starPower, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref x);
    Kg2TestLog.Clear();
    HookDispatch.AfterStarsSpent(ref x, 4);
    Check(string.Join(",", Kg2TestLog.Lines) == "stars-spent-relic:4,stars-spent-power:4",
        $"RG 星能消耗顺序错误：{string.Join(",", Kg2TestLog.Lines)}");

    // GainStars: clamp, then AfterStarsGained; skipped while the combat is ending.
    battle.Stars = Limits.ValueClamp - 2;
    Kg2TestLog.Clear();
    Check(Kernel.GainStars(ref x, 5), "RG 星能获得不应产生待处理选择");
    Check(battle.Stars == Limits.ValueClamp, "RG 星能获得应钳制到上限");
    Check(Kg2TestLog.Lines.Contains("stars-gained:5"), "RG 星能获得 Hook 未派发");
    battle.Stars = 0;
    battle.Enemies[0].Creature.CurrentHp = 0;
    Check(Kernel.IsEnding(battle), "RG 终局测试前提不成立");
    Kg2TestLog.Clear();
    Check(Kernel.GainStars(ref x, 5) && battle.Stars == 0 && Kg2TestLog.Lines.Count == 0,
        "RG 战斗结束时星能获得应跳过");
    battle.Enemies[0].Creature.CurrentHp = 100;

    // Star payment: ManualPlay deducts the star cost once and dispatches AfterStarsSpent with the paid
    // amount (K0 contract for the "the count changed by exactly the paid amount" assumption). No
    // cost-modifying relic here, so the base cost 2 is paid.
    Battle paid = NewBattle();
    paid.Flags |= BattleFlags.PlayerHooksActive;
    Piles.Append(ref paid, Pile.Hand, Kernel.CreateCard(rules, starCard));
    Exec pay = new(ref paid, rules, default);
    Kernel.ApplyPower(ref pay, Limits.PlayerOwner, starPower, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref pay);
    paid.Stars = 5;
    Kg2TestLog.Clear();
    Kernel.ManualPlay(ref pay, 0, Limits.NoOwner);
    Check(paid.Stars == 3, $"RG 出牌应恰好支付 2 星能，实际星能 {paid.Stars}");
    Check(string.Join(",", Kg2TestLog.Lines) == "stars-spent-power:2",
        $"RG 出牌应只派发一次金额 2 的星能消耗 Hook：{string.Join(",", Kg2TestLog.Lines)}");

    // Forge: grows every Sovereign Blade in every pile; creates one canonical blade only when no
    // unexhausted blade exists; the freshly created blade receives the same forge amount.
    Battle forgeBattle = NewBattle();
    Exec forge = new(ref forgeBattle, rules, default);
    Card bladeValue = Kernel.CreateCard(rules, blade);
    Piles.Append(ref forgeBattle, Pile.Hand, bladeValue);
    Piles.Append(ref forgeBattle, Pile.Discard, bladeValue);
    Check(Kernel.Forge(ref forge, 3), "RG 锻造失败");
    Check(forgeBattle.CardCount == 2, "RG 已有未消耗君王之刃时不应再生成");
    Check(forgeBattle.Cards[0].Var0 == 3 && forgeBattle.Cards[1].Var0 == 3,
        "RG 锻造应增加所有君王之刃的伤害变量");

    Battle exhaustedOnly = NewBattle();
    Exec forge2 = new(ref exhaustedOnly, rules, default);
    Piles.Append(ref exhaustedOnly, Pile.Exhaust, bladeValue);
    Check(Kernel.Forge(ref forge2, 4), "RG 消耗堆君王之刃的锻造失败");
    Check(exhaustedOnly.HandCount == 1 && exhaustedOnly.ExhaustCount == 1,
        "RG 只有消耗堆君王之刃时应生成一张新牌");
    Check(exhaustedOnly.Cards[0].Var0 == 4 && exhaustedOnly.Cards[1].Var0 == 4,
        "RG 新生成的君王之刃也应获得本次锻造量");

    // StarsGainedThisTurn: positive gains accumulate (Radiate's CalculatedHits), non-positive gains do
    // not, and the player side turn start resets it (BeginSideTurn's ResetTurnCounter).
    Battle turn = SliceBattle();
    Exec turnExec = new(ref turn, rules, default);
    turn.Stars = 10;
    Check(Kernel.GainStars(ref turnExec, 4), "RG 星能获得不应产生待处理选择");
    Check(turn.Counter(Counter.StarsGainedThisTurn) == 4,
        $"RG 本回合获得星能应为 4，实际 {turn.Counter(Counter.StarsGainedThisTurn)}");
    Check(Kernel.GainStars(ref turnExec, -2), "RG 负星能获得不应产生待处理选择");
    Check(turn.Counter(Counter.StarsGainedThisTurn) == 4,
        "RG 负星能获得不应计入本回合获得星能");
    Kernel.Step(ref turn, PlayerAction.EndTurn(), rules, out _);
    Check(turn.Counter(Counter.StarsGainedThisTurn) == 0,
        $"RG 玩家回合开始应清零本回合获得星能，实际 {turn.Counter(Counter.StarsGainedThisTurn)}");

    // The blade targets every enemy while the owner holds SeekingEdgePower.
    Check(Registry.TryPower("SEEKING_EDGE_POWER", out ushort seekingEdge), "缺少 SEEKING_EDGE_POWER");
    Battle targetBattle = NewBattle();
    Exec target = new(ref targetBattle, rules, default);
    Card bladeCard = Kernel.CreateCard(rules, blade);
    Check(Registry.Card(blade).ResolveTargetType(targetBattle, rules, bladeCard) == TargetType.AnyEnemy,
        "RG 君王之刃默认目标应为单体");
    Kernel.ApplyPower(ref target, Limits.PlayerOwner, seekingEdge, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref target);
    Check(Registry.Card(blade).ResolveTargetType(targetBattle, rules, bladeCard) == TargetType.AllEnemies,
        "RG 寻求锋芒下君王之刃目标应为全体");
}


// LR: new kernel capabilities. Turn-start choices, the fixed transform, the generated-card and
// per-target powered-hit counters, and VoidForm's free-play window / forced turn end.
static void CheckM2cSemantics(Rules rules)
{
    Check(Registry.TryCard("ANTICIPATE", out ushort anticipate), "缺少 ANTICIPATE 定义");
    Check(Registry.TryCard("OMNISLICE", out ushort omnislice), "缺少 OMNISLICE 定义");
    Check(Registry.TryCard("THE_BOMB", out ushort theBomb), "缺少 THE_BOMB 定义");
    Check(Registry.TryCard("PANACHE", out ushort panache), "缺少 PANACHE 定义");
    Check(Registry.TryCard("TORIC_TOUGHNESS", out ushort toric), "缺少 TORIC_TOUGHNESS 定义");
    Check(Registry.TryPower("DEXTERITY_POWER", out ushort dexterity), "缺少 DEXTERITY_POWER");
    Check(Registry.TryPower("ANTICIPATE_POWER", out ushort anticipatePower), "缺少 ANTICIPATE_POWER");
    Check(Registry.TryPower("THE_BOMB_POWER", out ushort theBombPower), "缺少 THE_BOMB_POWER");
    Check(Registry.TryPower("PANACHE_POWER", out ushort panachePower), "缺少 PANACHE_POWER");
    Check(Registry.TryPower("TORIC_TOUGHNESS_POWER", out ushort toricPower), "缺少 TORIC_TOUGHNESS_POWER");
    Check(Registry.TryRelic("BELT_BUCKLE", out ushort beltBuckle), "缺少 BELT_BUCKLE");

    // Anticipate: TemporaryDexterityPower.BeforeApplied applies Dexterity before the temporary row.
    Battle battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, anticipate));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    Check(battle.PowerCount == 2,
        $"Anticipate 应留下敏捷与临时敏捷两行，实际 {battle.PowerCount}");
    Check(battle.Powers[0].Id == dexterity && battle.Powers[1].Id == anticipatePower,
        "Anticipate 的 Power 行顺序应为 DEXTERITY_POWER、ANTICIPATE_POWER");
    Check(battle.Powers[0].Amount == 2 && battle.Powers[1].Amount == 2,
        "Anticipate 应先给 2 敏捷再插入 2 层临时敏捷");

    // Omnislice uses an AttackContext, so neither hit records CreatureAttacked.
    battle = NewBattle();
    battle.EnemyCount = 2;
    battle.RosterCount = 2;
    battle.Roster[0] = 0;
    battle.Roster[1] = 1;
    battle.Enemies[0].Creature.CurrentHp = 100;
    battle.Enemies[0].Creature.MaxHp = 100;
    battle.Enemies[0].Flags = EnemyFlags.InCombat;
    battle.Enemies[1].Creature.CurrentHp = 100;
    battle.Enemies[1].Creature.MaxHp = 100;
    battle.Enemies[1].Flags = EnemyFlags.InCombat;
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, omnislice));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.FirstEnemyOwner + 1), rules, out _);
    Check(battle.Counter(Counter.PlayerAttacksThisTurn) == 0,
        "Omnislice 使用 AttackContext，不应增加本回合攻击次数");
    Check(battle.Enemies[0].Creature.CurrentHp == 92 && battle.Enemies[1].Creature.CurrentHp == 92,
        "Omnislice 应命中目标并把伤害复制给另一名敌人");

    // TheBomb/Panache/ToricToughness use PowerCmd.Apply and leave Target null (= NoOwner).
    battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, theBomb));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    int bombRow = Kernel.FindLastPower(battle, Limits.PlayerOwner, theBombPower);
    Check(bombRow >= 0 && battle.Powers[bombRow].Target == Limits.NoOwner,
        "TheBomb 的 Power.Target 应为 NoOwner");
    Check(bombRow >= 0 && battle.Powers[bombRow].Applier == Limits.PlayerOwner,
        "TheBomb 的 Power.Applier 应保留玩家");

    battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, panache));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    int panacheRow = Kernel.FindLastPower(battle, Limits.PlayerOwner, panachePower);
    Check(panacheRow >= 0 && battle.Powers[panacheRow].Target == Limits.NoOwner,
        "Panache 的 Power.Target 应为 NoOwner");

    battle = NewBattle();
    Piles.Append(ref battle, Pile.Hand, Kernel.CreateCard(rules, toric));
    Kernel.Step(ref battle, PlayerAction.PlayCard(0, Limits.NoOwner), rules, out _);
    int toricRow = Kernel.FindLastPower(battle, Limits.PlayerOwner, toricPower);
    Check(toricRow >= 0 && battle.Powers[toricRow].Target == Limits.NoOwner,
        "ToricToughness 的 Power.Target 应为 NoOwner");

    // BeltBuckle applies Dexterity with a null native applier.
    battle = NewBattle();
    battle.RelicCount = 1;
    battle.Relics[0] = new Relic { Id = beltBuckle };
    battle.PotionSlotCount = 0;
    Exec belt = new(ref battle, rules, default);
    Registry.Relic(beltBuckle).AfterPotionUsed(ref belt,
        new Listener(ListenerKind.Relic, 0, Limits.PlayerOwner), 0);
    int dexRow = Kernel.FindPower(battle, Limits.PlayerOwner, dexterity);
    Check(dexRow >= 0 && battle.Powers[dexRow].Applier == Limits.NoOwner,
        "BeltBuckle 的 Dexterity Applier 应为 NoOwner");
}

static void CheckHandLimit(Rules rules)
{
    Check(Registry.TryCard("SUMMON_FORTH", out ushort summonForth), "缺少 SUMMON_FORTH 定义");
    Check(Registry.TryCard("SOVEREIGN_BLADE", out ushort blade), "缺少 SOVEREIGN_BLADE 定义");
    Check(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "缺少 STRIKE_IRONCLAD 定义");

    // Full hand: the hand stays at the cap and the overflow card goes to the discard pile, exactly
    // like CardPileCmd.Add's isFullHandAdd branch.
    Battle full = NewBattle();
    full.Flags |= BattleFlags.PlayerHooksActive;
    for (int index = 0; index < Limits.MaxHandSize - 1; index++)
        Piles.Append(ref full, Pile.Hand, Kernel.CreateCard(rules, strike));
    Piles.Append(ref full, Pile.Hand, Kernel.CreateCard(rules, summonForth));
    Piles.Append(ref full, Pile.Draw, Kernel.CreateCard(rules, blade));
    Piles.Append(ref full, Pile.Draw, Kernel.CreateCard(rules, blade));
    Exec fullExec = new(ref full, rules, default);
    int handBefore = full.HandCount;
    int summonIndex = -1;
    for (int index = 0; index < full.HandCount; index++)
    {
        if (full.Cards[index].Id == summonForth)
        {
            summonIndex = index;
            break;
        }
    }
    Check(summonIndex >= 0, "SummonForth 测试牌应在手牌中");
    Check(Kernel.Step(ref full, PlayerAction.PlayCard(summonIndex, Limits.NoOwner), rules, out _)
        == StepResult.Completed, "SummonForth 满手测试应完成");
    Check(full.HandCount == Limits.MaxHandSize,
        $"SummonForth 满手后手牌应保持 {Limits.MaxHandSize}，实际 {full.HandCount}");
    int handBlades = 0, discardBlades = 0;
    for (int index = 0; index < full.HandCount; index++)
        if (full.Cards[index].Id == blade) handBlades++;
    for (int index = 0; index < full.DiscardCount; index++)
        if (Piles.At(ref full, Pile.Discard, index).Id == blade) discardBlades++;
    Check(handBlades == 1 && discardBlades == 1,
        $"SummonForth 应把手牌上限外的一张君王之刃送入弃牌堆，实际手牌 {handBlades} 弃牌 {discardBlades}");
    Check(full.HandCount <= Limits.MaxHandSize, "SummonForth 不应突破手牌上限");

    // The fixed legal-action buffer only fits ten hand cards. Eleven must fail explicitly, not write
    // past the caller's span.
    Battle overflow = SliceBattle();
    for (int index = 0; index < 6; index++)
        Piles.Append(ref overflow, Pile.Hand, Kernel.CreateCard(rules, strike));
    Check(overflow.HandCount == Limits.MaxHandSize + 1, "越界测试前提不成立");
    Span<PlayerAction> actions = stackalloc PlayerAction[Kernel.MaxLegalActions];
    bool legalThrew = false;
    try
    {
        Kernel.LegalActions(overflow, rules, actions);
    }
    catch (InvalidOperationException)
    {
        legalThrew = true;
    }
    Check(legalThrew, "手牌超过上限时 LegalActions 必须显式失败");
    bool invariantThrew = false;
    try
    {
        Kernel.ValidateStableBoundary(in overflow);
    }
    catch (InvalidOperationException)
    {
        invariantThrew = true;
    }
    Check(invariantThrew, "手牌超过上限时稳定边界不变量必须显式失败");

    Battle atLimit = SliceBattle();
    for (int index = 0; index < 5; index++)
        Piles.Append(ref atLimit, Pile.Hand, Kernel.CreateCard(rules, strike));
    Check(atLimit.HandCount == Limits.MaxHandSize, "上限测试前提不成立");
    int legalCount = Kernel.LegalActions(atLimit, rules, actions);
    Check(legalCount <= Kernel.MaxLegalActions,
        $"手牌不超过上限时 LegalActions 不应超过 {Kernel.MaxLegalActions}，实际 {legalCount}");
    Kernel.ValidateStableBoundary(atLimit);
}

static void CheckLrKernel(Rules rules)
{
    Check(Registry.TryCard("STRIKE_IRONCLAD", out ushort strike), "缺少 STRIKE_IRONCLAD");
    Check(Registry.TryCard("DEFEND_IRONCLAD", out ushort defend), "缺少 DEFEND_IRONCLAD");
    Check(Registry.TryCard("SOVEREIGN_BLADE", out ushort blade), "缺少 SOVEREIGN_BLADE");
    Check(Registry.TryCard("RG_TEST_STAR_CARD", out ushort starCard), "缺少 RG_TEST_STAR_CARD");
    Check(Registry.TryCard("MINION_STRIKE", out ushort minionStrike), "缺少 MINION_STRIKE");
    Check(Registry.TryPower("VOID_FORM_POWER", out ushort voidForm), "缺少 VOID_FORM_POWER");
    Check(Registry.TryCard("VOID_FORM", out ushort voidFormCard), "缺少 VOID_FORM");
    Check(Registry.TryPower("TYRANNY_POWER", out ushort tyranny), "缺少 TYRANNY_POWER");
    Check(Registry.TryPower("MAYHEM_POWER", out ushort mayhem), "缺少 MAYHEM_POWER");
    Check(Registry.TryPower("VOID_FORM_POWER", out ushort voidFormPower), "缺少 VOID_FORM_POWER");
    Check(Registry.TryPower("FOREGONE_CONCLUSION_POWER", out ushort foregone), "缺少 FOREGONE_CONCLUSION_POWER");

    // Generated cards: AddGeneratedCard records History.CardGenerated before insertion, so a card
    // refused while the combat is ending still counts (HeirloomHammer clones on a killing blow).
    Battle generated = NewBattle();
    Exec generate = new(ref generated, rules, default);
    Check(Kernel.AddGeneratedCard(ref generate, Kernel.CreateCard(rules, defend), Pile.Hand),
        "LR 生成卡应加入手牌");
    Check(generated.Counter(Counter.CardsGeneratedThisCombat) == 1,
        "LR 生成卡应计数 1");
    generated.Enemies[0].Creature.CurrentHp = 0;
    Check(Kernel.IsEnding(generated), "LR 终局测试前提不成立");
    int handBefore = generated.HandCount;
    Check(Kernel.AddGeneratedCard(ref generate, Kernel.CreateCard(rules, defend), Pile.Hand),
        "LR 战斗结束时生成卡按现有引擎继续执行而不中止");
    Check(generated.HandCount == handBefore, "LR 战斗结束时生成卡不应加入牌堆");
    Check(generated.Counter(Counter.CardsGeneratedThisCombat) == 2,
        "LR 战斗结束时被拒绝的生成卡仍应计入生成数");

    // The fixed transform replaces each selected card in place and keeps equal copies distinct by
    // occurrence (CardChoiceResolution.TransformCards).
    Battle transformed = NewBattle();
    Exec transform = new(ref transformed, rules, default);
    Card defendA = Kernel.CreateCard(rules, defend);
    defendA.Flags |= CardFlags.PowerAfflictionChecked;
    Card strikeA = Kernel.CreateCard(rules, strike);
    strikeA.Flags |= CardFlags.PowerAfflictionChecked;
    Card defendB = Kernel.CreateCard(rules, defend);
    defendB.Flags |= CardFlags.PowerAfflictionChecked;
    Piles.Append(ref transformed, Pile.Hand, defendA);
    Piles.Append(ref transformed, Pile.Hand, strikeA);
    Piles.Append(ref transformed, Pile.Hand, defendB);
    Span<int> selection = stackalloc int[2];
    selection[0] = 0;
    selection[1] = 2;
    Kernel.TransformSelectedTo(ref transform, Pile.Hand, selection, minionStrike, upgrade: true);
    Check(transformed.HandCount == 3, "LR 变换不应改变手牌数");
    Check(transformed.Cards[0].Id == minionStrike && transformed.Cards[0].Upgrade == 1
        && transformed.Cards[2].Id == minionStrike && transformed.Cards[2].Upgrade == 1,
        "LR 变换应原位替换两张防御并升到 1 级");
    Check(transformed.Cards[1].Id == strike, "LR 未选中的卡不应改变");
    Check(transformed.Counter(Counter.CardsGeneratedThisCombat) == 2,
        "LR 每次固定变换应计入一次生成");

    // Powered attack hits: recorded for the player's powered attacks even when fully blocked,
    // ignored for unpowered damage, and an overflowing byte is an explicit boundary.
    Battle hits = NewBattle();
    Exec hit = new(ref hits, rules, default);
    Span<byte> target = stackalloc byte[1];
    target[0] = Kernel.EnemyOwner(0);
    Kernel.Damage(ref hit, target, 6m, ValueProps.Move, Limits.PlayerOwner, false, default);
    Check(Kernel.PoweredAttackHitsByPlayer(hits, target[0]) == 1, "LR 强化攻击命中应计数");
    hits.Enemies[0].Creature.Block = 100;
    Check(Kernel.Damage(ref hit, target, 6m, ValueProps.Move, Limits.PlayerOwner, false, default),
        "LR 被完全格挡的强化攻击应报告完全格挡");
    Check(Kernel.PoweredAttackHitsByPlayer(hits, target[0]) == 2,
        "LR 被完全格挡的强化攻击仍应计数");
    Kernel.Damage(ref hit, target, 6m,
        ValueProps.Move | ValueProps.Unpowered, Limits.PlayerOwner, false, default);
    Check(Kernel.PoweredAttackHitsByPlayer(hits, target[0]) == 2,
        "LR 非强化伤害不应计数");
    // The game counts hits in an int; 255 is not a limit (a guarded damage loop can exceed it).
    hits.Enemies[0].PoweredAttackHitsThisTurn = byte.MaxValue;
    Kernel.Damage(ref hit, target, 1m, ValueProps.Move, Limits.PlayerOwner, false, default);
    Check(Kernel.PoweredAttackHitsByPlayer(hits, target[0]) == 256,
        "LR 强化攻击命中数超过 255 应继续计数");
    hits.Enemies[0].PoweredAttackHitsThisTurn = ushort.MaxValue;
    bool overflowed = false;
    try
    {
        Kernel.Damage(ref hit, target, 1m, ValueProps.Move, Limits.PlayerOwner, false, default);
    }
    catch (UnsupportedException)
    {
        overflowed = true;
    }
    Check(overflowed, "LR 强化攻击命中数溢出应抛 UnsupportedException");
    Battle reset = SliceBattle();
    reset.Enemies[0].PoweredAttackHitsThisTurn = 3;
    Kernel.Step(ref reset, PlayerAction.EndTurn(), rules, out _);
    Check(reset.Enemies[0].PoweredAttackHitsThisTurn == 0,
        "LR 玩家回合开始应清零强化攻击命中数");

    // Action choice capacity: WithChoice refuses the 33rd selection explicitly.
    PlayerAction choices = PlayerAction.EndTurn();
    for (int index = 0; index < Limits.MaxChoices; index++)
        choices = choices.WithChoice(1UL << (index & 63));
    bool capacity = false;
    try
    {
        choices.WithChoice(1);
    }
    catch (CapacityException)
    {
        capacity = true;
    }
    Check(capacity, "LR 动作选择超出容量应抛 CapacityException");

    // VoidForm's free-play window: the late energy/star hooks zero the cost while fewer than Amount
    // non-auto last-in-series plays happened this turn; the counter resets at the side turn start.
    Battle voidBattle = SliceBattle();
    Exec voidExec = new(ref voidBattle, rules, default);
    Kernel.ApplyPower(ref voidExec, Limits.PlayerOwner, voidForm, 2, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref voidExec);
    int voidIndex = Kernel.FindPower(voidBattle, Limits.PlayerOwner, voidForm);
    Check(voidIndex >= 0, "LR VoidFormPower 未施加");
    Listener voidSelf = new(ListenerKind.Power, checked((byte)voidIndex), Limits.PlayerOwner);
    PowerDef voidDef = Registry.Power(voidForm);
    Card strikeCard = Kernel.CreateCard(rules, strike);
    Check(voidDef.TryModifyEnergyCostInCombatLate(voidBattle, rules, voidSelf, strikeCard, Pile.Hand, 3m) == 0m,
        "LR VoidForm 首次出牌的能量费用应为 0");
    Check(voidDef.TryModifyStarCost(voidBattle, rules, voidSelf,
            Kernel.CreateCard(rules, starCard), Pile.Hand, 2m) == 0m,
        "LR VoidForm 首次出牌的星能费用应为 0");
    Check(voidDef.TryModifyEnergyCostInCombatLate(voidBattle, rules, voidSelf, strikeCard, Pile.Draw, 3m) == 3m,
        "LR VoidForm 只应影响手牌与打出区");
    Exec voidPlay = new(ref voidBattle, rules, default);
    voidDef.AfterCardPlayed(ref voidPlay, voidSelf,
        new CardPlayInfo(strikeCard, Limits.NoOwner, 0, 1, Pile.Discard));
    Check(voidBattle.Powers[voidIndex].Aux0 == 1, "LR VoidForm 非自动最后一段出牌应计数");
    voidPlay.CurrentPlayIsAutoPlay = true;
    voidDef.AfterCardPlayed(ref voidPlay, voidSelf,
        new CardPlayInfo(strikeCard, Limits.NoOwner, 0, 1, Pile.Discard));
    Check(voidBattle.Powers[voidIndex].Aux0 == 1, "LR VoidForm 自动打出不应计数");
    voidPlay.CurrentPlayIsAutoPlay = false;
    voidDef.AfterCardPlayed(ref voidPlay, voidSelf,
        new CardPlayInfo(strikeCard, Limits.NoOwner, 0, 2, Pile.Discard));
    Check(voidBattle.Powers[voidIndex].Aux0 == 1, "LR VoidForm 非最后一段出牌不应计数");
    Check(voidDef.TryModifyEnergyCostInCombatLate(voidBattle, rules, voidSelf, strikeCard, Pile.Hand, 3m) == 0m,
        "LR VoidForm 第二次出牌仍在免费窗口内");
    voidPlay.CurrentPlayIsAutoPlay = false;
    voidDef.AfterCardPlayed(ref voidPlay, voidSelf,
        new CardPlayInfo(strikeCard, Limits.NoOwner, 1, 2, Pile.Discard));
    Check(voidBattle.Powers[voidIndex].Aux0 == 2, "LR VoidForm 最后一段出牌应计数");
    Check(voidDef.TryModifyEnergyCostInCombatLate(voidBattle, rules, voidSelf, strikeCard, Pile.Hand, 3m) == 3m,
        "LR VoidForm 免费窗口用尽后应恢复费用");
    voidDef.BeforeSideTurnStart(ref voidPlay, voidSelf, participates: true);
    Check(voidBattle.Powers[voidIndex].Aux0 == 0, "LR VoidForm 侧回合开始应清零出牌计数");
    Battle request = SliceBattle();
    Kernel.RequestPlayerTurnEnd(ref request);
    Check((request.Flags & BattleFlags.PlayerTurnEndRequested) != 0,
        "LR VoidForm 的结束回合请求应置位");

    // TyrannyPower's turn-start exhaust choice is a PlayerTurnStart choice on EndTurn; replaying the
    // action with the selection exhausts the chosen hand card and the power also increases the draw.
    Battle tyrannyBattle = SliceBattle();
    Exec tyrannyExec = new(ref tyrannyBattle, rules, default);
    Kernel.ApplyPower(ref tyrannyExec, Limits.PlayerOwner, tyranny, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref tyrannyExec);
    Battle tyrannyParent = tyrannyBattle;
    PlayerAction endTurn = PlayerAction.EndTurn();
    Check(Kernel.Step(ref tyrannyBattle, endTurn, rules, out ChoiceRequest tyrannyRequest)
        == StepResult.NeedsChoice, "LR 暴政应在回合开始请求选牌");
    Check(tyrannyRequest.Timing == ChoiceTiming.PlayerTurnStart
        && tyrannyRequest.Effect == ChoiceEffect.Exhaust
        && tyrannyRequest.Source == Pile.Hand
        && tyrannyRequest.MinCount == 1 && tyrannyRequest.MaxCount == 1,
        $"LR 暴政选择规格错误 timing={tyrannyRequest.Timing} effect={tyrannyRequest.Effect} "
        + $"source={tyrannyRequest.Source} count={tyrannyRequest.MinCount}..{tyrannyRequest.MaxCount}");
    Battle tyrannyReplay = tyrannyParent;
    Check(Kernel.Step(ref tyrannyReplay, endTurn.WithChoice(1UL), rules, out _)
        == StepResult.Completed, "LR 暴政选择后回合应完成");
    Check(tyrannyReplay.ExhaustCount == 1, $"LR 暴政应消耗 1 张选中的手牌，实际 {tyrannyReplay.ExhaustCount}");

    // Mayhem's AutoPrePlay can auto-play VoidForm during the next turn's setup. VoidForm's
    // PlayerCmd.EndTurn arrives while the setup is still resolving; the native turn loop finishes
    // the setup, then immediately runs turn end/enemy turn and asks the following turn's Tyranny
    // choice. Both turn-start choices belong to the same EndTurn action.
    Battle mayhemBattle = SliceBattle();
    Exec mayhemExec = new(ref mayhemBattle, rules, default);
    Kernel.ApplyPower(ref mayhemExec, Limits.PlayerOwner, tyranny, 1, Limits.PlayerOwner);
    Kernel.ApplyPower(ref mayhemExec, Limits.PlayerOwner, mayhem, 1, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref mayhemExec);
    Piles.Append(ref mayhemBattle, Pile.Draw, Kernel.CreateCard(rules, defend));
    Piles.Append(ref mayhemBattle, Pile.Draw, Kernel.CreateCard(rules, voidFormCard));
    Battle mayhemParent = mayhemBattle;
    PlayerAction mayhemEnd = PlayerAction.EndTurn();
    Check(Kernel.Step(ref mayhemBattle, mayhemEnd, rules, out ChoiceRequest mayhemFirst)
        == StepResult.NeedsChoice,
        "LR Mayhem 场景应先请求第 2 回合的暴政选择");
    Check(mayhemFirst.Timing == ChoiceTiming.PlayerTurnStart
        && mayhemFirst.Effect == ChoiceEffect.Exhaust
        && mayhemBattle.PlayerTurn == 2,
        $"LR Mayhem 首个暴政选择规格错误 turn={mayhemBattle.PlayerTurn} "
        + $"timing={mayhemFirst.Timing} effect={mayhemFirst.Effect}");
    Battle mayhemSecond = mayhemParent;
    Check(Kernel.Step(ref mayhemSecond, mayhemEnd.WithChoice(1UL), rules,
            out ChoiceRequest mayhemRequest)
        == StepResult.NeedsChoice,
        "LR Mayhem 自动打出 VoidForm 后应结束第 2 回合并请求第 3 回合的暴政选择");
    Check(mayhemSecond.PlayerTurn == 3
        && mayhemRequest.Timing == ChoiceTiming.PlayerTurnStart
        && mayhemRequest.Effect == ChoiceEffect.Exhaust
        && mayhemRequest.Source == Pile.Hand,
        $"LR VoidForm 强制结束后的暴政选择规格错误 turn={mayhemSecond.PlayerTurn} "
        + $"timing={mayhemRequest.Timing} effect={mayhemRequest.Effect} source={mayhemRequest.Source}");
    Battle mayhemDone = mayhemParent;
    Check(Kernel.Step(ref mayhemDone, mayhemEnd.WithChoice(1UL).WithChoice(1UL), rules, out _)
        == StepResult.Completed,
        "LR 两次暴政选择后 EndTurn 应完成");
    Check(mayhemDone.PlayerTurn == 3
        && (mayhemDone.Flags & BattleFlags.PlayerTurnEndRequested) == 0,
        $"LR VoidForm 强制结束应清掉请求并停在第 3 回合 Play，实际 turn={mayhemDone.PlayerTurn} "
        + $"flags={mayhemDone.Flags}");
    Check(Kernel.FindPower(mayhemDone, Limits.PlayerOwner, voidFormPower) >= 0,
        "LR Mayhem 自动打出的 VoidForm 应施加 VoidFormPower");

    // ForegoneConclusionPower: its BeforeHandDraw choice moves draw cards to hand and removes the
    // power; the empty-draw branch shuffles first and asks nothing.
    Battle foregoneBattle = SliceBattle();
    Exec foregoneExec = new(ref foregoneBattle, rules, default);
    Kernel.ApplyPower(ref foregoneExec, Limits.PlayerOwner, foregone, 2, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref foregoneExec);
    Battle foregoneParent = foregoneBattle;
    PlayerAction foregoneEnd = PlayerAction.EndTurn();
    Check(Kernel.Step(ref foregoneBattle, foregoneEnd, rules, out ChoiceRequest foregoneRequest)
        == StepResult.NeedsChoice, "LR 先见之明应在摸牌前请求选牌");
    Check(foregoneRequest.Timing == ChoiceTiming.PlayerTurnStart
        && foregoneRequest.Effect == ChoiceEffect.MoveToHand
        && foregoneRequest.Source == Pile.Draw
        && foregoneRequest.MinCount == 2 && foregoneRequest.MaxCount == 2,
        $"LR 先见之明选择规格错误 timing={foregoneRequest.Timing} effect={foregoneRequest.Effect} "
        + $"source={foregoneRequest.Source} count={foregoneRequest.MinCount}..{foregoneRequest.MaxCount}");
    Battle foregoneReplay = foregoneParent;
    Check(Kernel.Step(ref foregoneReplay, foregoneEnd.WithChoice(0b11UL), rules, out _)
        == StepResult.Completed, "LR 先见之明选择后回合应完成");
    Check(Kernel.GetAmount(foregoneReplay, Limits.PlayerOwner, foregone) == 0,
        "LR 先见之明结算后应移除 Power");
    Check(foregoneReplay.HandCount == 7,
        $"LR 先见之明应先移入手牌再抽 5 张，实际手牌 {foregoneReplay.HandCount}");

    // A DrawCardsNextTurn power applied during the current turn must survive the ForegoneConclusion
    // BeforeHandDraw choice on an ordinary EndTurn: production answers that choice through
    // CardSelectCmd.Selector, so the native SetupPlayerTurn task never pauses and AfterSideTurnStart
    // (which removes DrawCardsNextTurnPower) runs after the draw.
    Check(Registry.TryPower("DRAW_CARDS_NEXT_TURN_POWER", out ushort drawNextTurn),
        "缺少 DRAW_CARDS_NEXT_TURN_POWER");
    Battle foregoneDrawBattle = SliceBattle();
    for (int index = 0; index < 10; index++)
    {
        Card extra = new()
        {
            Id = strike, BaseCost = 1, BaseStarCost = -1, Flags = CardFlags.DeckCard,
        };
        Piles.Append(ref foregoneDrawBattle, Pile.Draw, extra);
    }
    Exec foregoneDrawExec = new(ref foregoneDrawBattle, rules, default);
    Kernel.ApplyPower(ref foregoneDrawExec, Limits.PlayerOwner, foregone, 2, Limits.PlayerOwner);
    Kernel.ApplyPower(ref foregoneDrawExec, Limits.PlayerOwner, drawNextTurn, 2, Limits.PlayerOwner);
    Kernel.ResolvePowerAmountChanges(ref foregoneDrawExec);
    Battle foregoneDrawParent = foregoneDrawBattle;
    PlayerAction foregoneDrawEnd = PlayerAction.EndTurn();
    Check(Kernel.Step(ref foregoneDrawBattle, foregoneDrawEnd, rules, out ChoiceRequest foregoneDrawRequest)
        == StepResult.NeedsChoice, "LR 先见之明+下回合抽牌应在摸牌前请求选牌");
    Check(foregoneDrawRequest.Timing == ChoiceTiming.PlayerTurnStart
        && foregoneDrawRequest.MinCount == 2 && foregoneDrawRequest.MaxCount == 2,
        "LR 先见之明+下回合抽牌的选择规格错误");
    Battle foregoneDrawReplay = foregoneDrawParent;
    Check(Kernel.Step(ref foregoneDrawReplay, foregoneDrawEnd.WithChoice(0b11UL), rules, out _)
        == StepResult.Completed, "LR 先见之明+下回合抽牌选择后回合应完成");
    Check(foregoneDrawReplay.HandCount == 9,
        $"LR 普通结束回合不应在摸牌前清掉下回合抽牌，实际手牌 {foregoneDrawReplay.HandCount}");
    Check(foregoneDrawReplay.DrawCount == 6,
        $"LR 先见之明+下回合抽牌后应剩 6 张抽牌堆，实际 {foregoneDrawReplay.DrawCount}");

    // A host that takes the next turn over on the visible setup page releases the selector: the
    // same choice pauses the native setup task, and the side start at that pause removes
    // DrawCardsNextTurnPower before the draw, as PreparePlayerPlayPhase does on a captured page.
    Battle foregonePauseReplay = foregoneDrawParent;
    foregonePauseReplay.Flags |= BattleFlags.TurnStartChoicePauses;
    Check(Kernel.Step(ref foregonePauseReplay, foregoneDrawEnd.WithChoice(0b11UL), rules, out _)
        == StepResult.Completed, "LR 准备页暂停时先见之明+下回合抽牌选择后回合应完成");
    Check(foregonePauseReplay.HandCount == 7,
        $"LR 准备页暂停时侧回合开始应在摸牌前清掉下回合抽牌，实际手牌 {foregonePauseReplay.HandCount}");
    Check(foregonePauseReplay.DrawCount == 8,
        $"LR 准备页暂停时应剩 8 张抽牌堆，实际 {foregonePauseReplay.DrawCount}");
    Check((foregonePauseReplay.Flags & BattleFlags.TurnStartChoicePauses) != 0,
        "LR 结束回合后应保留准备页暂停标志");
}

/// <summary>
/// TS: the two turn-start choice shapes the turn-setup fixtures rely on. The bridge replays the
/// setup page through the existing engine, and the kernel uses the same shapes for later turns:
/// <c>ToastyMittens</c> (exhaust one hand card, then gain Strength) and <c>ChoicesParadox</c>
/// (a generated-card fixed option set). Both choices are stamped <c>PlayerTurnStart</c>.
/// </summary>
static void CheckTsKernel(Rules rules)
{
    Check(Registry.TryRelic("TOASTY_MITTENS", out ushort toasty), "缺少 TOASTY_MITTENS");
    Check(Registry.TryRelic("CHOICES_PARADOX", out ushort paradox), "缺少 CHOICES_PARADOX");
    Check(Registry.TryPower("STRENGTH_POWER", out ushort strength), "缺少 STRENGTH_POWER");
    RelicDef toastyDef = Registry.Relic(toasty);
    RelicDef paradoxDef = Registry.Relic(paradox);
    Listener self = new(ListenerKind.Relic, 0, Limits.PlayerOwner);

    // ToastyMittens: the request is an Exhaust choice over the hand, stamped PlayerTurnStart.
    Battle toastyBattle = SliceBattle();
    toastyBattle.RelicCount = 1;
    toastyBattle.Relics[0] = new Relic { Id = toasty };
    int toastyHand = toastyBattle.HandCount;
    Exec toastyProbe = new(ref toastyBattle, rules, default);
    toastyProbe.ChoiceTiming = ChoiceTiming.PlayerTurnStart;
    toastyDef.AfterPlayerTurnStart(ref toastyProbe, self);
    Check(toastyProbe.NeedsChoice, "TS 烘焙手套应在回合开始请求选牌");
    Check(toastyProbe.Request.Kind == ChoiceKind.SelectCards
        && toastyProbe.Request.Effect == ChoiceEffect.Exhaust
        && toastyProbe.Request.Source == Pile.Hand
        && toastyProbe.Request.Timing == ChoiceTiming.PlayerTurnStart
        && toastyProbe.Request.MinCount == 1 && toastyProbe.Request.MaxCount == 1
        && toastyProbe.Request.OptionCount == toastyHand,
        $"TS 烘焙手套选择规格错误 kind={toastyProbe.Request.Kind} effect={toastyProbe.Request.Effect} "
        + $"source={toastyProbe.Request.Source} timing={toastyProbe.Request.Timing} "
        + $"count={toastyProbe.Request.MinCount}..{toastyProbe.Request.MaxCount} "
        + $"options={toastyProbe.Request.OptionCount} hand={toastyHand}");
    Battle toastyReplayBattle = toastyBattle;
    Exec toastyReplay = new(ref toastyReplayBattle, rules, PlayerAction.EndTurn().WithChoice(0b1UL));
    toastyReplay.ChoiceTiming = ChoiceTiming.PlayerTurnStart;
    toastyDef.AfterPlayerTurnStart(ref toastyReplay, self);
    Check(!toastyReplay.NeedsChoice && toastyReplayBattle.ExhaustCount == 1,
        $"TS 烘焙手套应消耗 1 张选中的手牌，实际消耗 {toastyReplayBattle.ExhaustCount}");
    Check(Kernel.GetAmount(toastyReplayBattle, Limits.PlayerOwner, strength) == 2,
        $"TS 烘焙手套应施加 2 力量，实际 {Kernel.GetAmount(toastyReplayBattle, Limits.PlayerOwner, strength)}");

    // ChoicesParadox: a generated-card option set (no pile), one selected card enters the hand and
    // carries Retain. The kernel samples CharacterAll in option order with CombatCardGeneration.
    Battle paradoxBattle = SliceBattle();
    paradoxBattle.RelicCount = 1;
    paradoxBattle.Relics[0] = new Relic { Id = paradox };
    paradoxBattle.RngStream(RngStream.CombatCardGeneration) =
        RngState.FromSerializable(0, 1, 2, 3, 4);
    int paradoxHand = paradoxBattle.HandCount;
    Exec paradoxProbe = new(ref paradoxBattle, rules, default);
    paradoxProbe.ChoiceTiming = ChoiceTiming.PlayerTurnStart;
    paradoxDef.AfterPlayerTurnStart(ref paradoxProbe, self);
    Check(paradoxProbe.NeedsChoice, "TS 抉择悖论应在回合开始请求固定选项");
    Check(paradoxProbe.Request.Kind == ChoiceKind.SelectOption
        && paradoxProbe.Request.Effect == ChoiceEffect.GenerateToHand
        && paradoxProbe.Request.OptionsAreGenerated
        && paradoxProbe.Request.SourceIsNone
        && paradoxProbe.Request.Timing == ChoiceTiming.PlayerTurnStart
        && paradoxProbe.Request.OptionCount == 3,
        $"TS 抉择悖论选择规格错误 kind={paradoxProbe.Request.Kind} effect={paradoxProbe.Request.Effect} "
        + $"generated={paradoxProbe.Request.OptionsAreGenerated} timing={paradoxProbe.Request.Timing} "
        + $"options={paradoxProbe.Request.OptionCount}");
    Battle paradoxReplayBattle = paradoxBattle;
    Exec paradoxReplay = new(ref paradoxReplayBattle, rules, PlayerAction.EndTurn().WithChoice(0b010UL));
    paradoxReplay.ChoiceTiming = ChoiceTiming.PlayerTurnStart;
    paradoxDef.AfterPlayerTurnStart(ref paradoxReplay, self);
    Check(!paradoxReplay.NeedsChoice, "TS 抉择悖论选定后不应再请求选择");
    Check(paradoxReplayBattle.HandCount == paradoxHand + 1,
        $"TS 抉择悖论应把选中的生成卡加入手牌，实际 {paradoxReplayBattle.HandCount}");
    ref Card generatedCard = ref paradoxReplayBattle.Cards[
        Piles.Start(paradoxReplayBattle, Pile.Hand) + paradoxReplayBattle.HandCount - 1];
    Check((generatedCard.Keywords & Keywords.Retain) != 0,
        "TS 抉择悖论生成的卡应带保留关键字");
}

static class Kg2TestLog
{
    public static List<string> Lines { get; } = [];

    public static void Clear() => Lines.Clear();

    public static void Add(string line) => Lines.Add(line);
}

[ContentId("KG2_TEST_COST_POWER")]
public sealed class Kg2TestCostPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal TryModifyEnergyCostInCombat(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        Kg2TestLog.Add("cost-normal-power");
        return amount + 1m;
    }

    public override decimal TryModifyEnergyCostInCombatLate(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        Kg2TestLog.Add("cost-late-power");
        return amount * 2m;
    }
}

[ContentId("KG2_TEST_COST_RELIC")]
public sealed class Kg2TestCostRelic : RelicDef
{
    public override decimal TryModifyEnergyCostInCombat(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        Kg2TestLog.Add("cost-normal-relic");
        return amount + 3m;
    }
}

[ContentId("KG2_TEST_RESULT_LOCATION_POWER")]
public sealed class Kg2TestResultLocationPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override Pile ModifyCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        bool isAutoPlay, int energyValue, Pile location, ref PilePosition position)
    {
        position = PilePosition.Top;
        return Pile.Draw;
    }

    public override void AfterModifyingCardPlayResultLocation(ref Exec x, Listener self, in Card card,
        Pile location) => Kg2TestLog.Add("location-after");
}

[ContentId("KG2_TEST_PLAY_COUNT_POWER")]
public sealed class Kg2TestPlayCountPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override int ModifyCardPlayCount(ref Exec x, Listener self, in Card card, byte target,
        int playCount) => playCount + 1;

    public override void AfterModifyingCardPlayCount(ref Exec x, Listener self, in Card card)
        => Kg2TestLog.Add("count-after");
}

[ContentId("KG2_TEST_POWER_CHANGE_POWER")]
public sealed class Kg2TestPowerChangePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterPowerAmountChanged(ref Exec x, Listener self, ushort powerId, int delta,
        byte applier, byte target)
    {
        Kg2TestLog.Add("power-changed");
        if (target == Limits.FirstEnemyOwner)
            Kg2TestLog.Add("power-changed-target");
    }
}

[ContentId("KG2_TEST_POWER_CHANGE_RELIC")]
public sealed class Kg2TestPowerChangeRelic : RelicDef
{
    public override void AfterPowerAmountChanged(ref Exec x, Listener self, ushort powerId, int delta,
        byte applier, byte target) => Kg2TestLog.Add("power-changed-relic");
}

[ContentId("A3_TEST_RAMPART_POWER")]
public sealed class A3TestRampartPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void Rampart(ref Exec x, Listener self) => Kg2TestLog.Add("rampart");
}

[ContentId("A3_TEST_GENERATED_RELIC")]
public sealed class A3TestGeneratedRelic : RelicDef
{
    public override void AfterCardGeneratedForCombat(ref Exec x, Listener self, int position,
        byte creator)
    {
        bool matches = creator == Limits.PlayerOwner
            && (uint)position < (uint)x.B.CardCount
            && Registry.TryCard("DEFEND_IRONCLAD", out ushort expected)
            && x.B.Cards[position].Id == expected;
        Kg2TestLog.Add(matches ? "generated-ok" : "generated-wrong-position");
    }
}

[ContentId("A3_TEST_ENDING_POWER")]
public sealed class A3TestEndingPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool PreventsCombatEnding => true;

    public override bool ShouldPowerBeRemovedAfterOwnerDeath => false;
}

[ContentId("KG2_TEST_X_VALUE_RELIC")]
public sealed class Kg2TestXValueRelic : RelicDef
{
    public override int ModifyXValue(in Battle battle, Rules rules, Listener self, in Card card,
        int value) => value + 1;
}

[ContentId("KG2_TEST_RESET_RELIC")]
public sealed class Kg2TestResetRelic : RelicDef
{
    public override bool ShouldPlayerResetEnergy(in Battle battle, Rules rules, Listener self,
        byte player) => false;
}

[ContentId("KG2_TEST_RECEIVED_RELIC")]
public sealed class Kg2TestReceivedRelic : RelicDef
{
    public override bool TryModifyPowerAmountReceived(in Battle battle, Rules rules, Listener self,
        ushort powerId, byte target, int amount, out int modified)
    {
        modified = amount * 2;
        return amount > 0;
    }

    public override void AfterModifyingPowerAmountReceived(ref Exec x, Listener self, ushort powerId,
        byte target) => Kg2TestLog.Add("received-after");
}

[ContentId("KG2_TEST_SHUFFLE_RELIC")]
public sealed class Kg2TestShuffleRelic : RelicDef
{
    public override void AfterShuffle(ref Exec x, Listener self, byte shuffler)
        => Kg2TestLog.Add("shuffle");
}

[ContentId("KG2_TEST_ENTERED_RELIC")]
public sealed class Kg2TestEnteredRelic : RelicDef
{
    public override void AfterCardEnteredCombat(ref Exec x, Listener self, Pile pile, int index,
        in Card card) => Kg2TestLog.Add("entered");
}

[ContentId("KG2_TEST_DAMAGE_SOURCE_POWER")]
public sealed class Kg2TestDamageSourcePower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterDamageReceived(ref Exec x, Listener self, in DamageResult result, byte dealer)
        => Kg2TestLog.Add(result.HasCardSource ? "damage-card" : "damage-none");
}

sealed class Keyed(Card card) : IComparable<Keyed>
{
    public Card Card { get; } = card;

    public int CompareTo(Keyed? other)
    {
        int id = Card.Id.CompareTo(other!.Card.Id);
        return id != 0 ? id : Card.Upgrade.CompareTo(other.Card.Upgrade);
    }
}

static class KsTestLog
{
    public static List<string> Lines { get; } = [];

    public static void Clear() => Lines.Clear();
}

[ContentId("KS_DISCARD_TARGET")]
public sealed class KsDiscardTarget : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

[ContentId("KS_SLY_CARD")]
public sealed class KsSlyCard : CardDef
{
    public override void OnPlay(ref Exec x) => KsTestLog.Lines.Add("sly-play");
}

[ContentId("KS_TEST_DISCARD_RELIC")]
public sealed class KsTestDiscardRelic : RelicDef
{
    public static bool RemoveDiscardedCard { get; set; }

    public override void AfterCardDiscarded(ref Exec x, Listener self, in Card card)
    {
        KsTestLog.Lines.Add("discarded");
        if (!RemoveDiscardedCard)
            return;
        for (int index = 0; index < x.B.DiscardCount; index++)
        {
            if (!Fingerprint.Equal(Piles.At(ref x.B, Pile.Discard, index), card))
                continue;
            // Leave every pile: removing without inserting models the unrepresentable target.
            Piles.RemoveAt(ref x.B, Pile.Discard, index);
            return;
        }
    }
}

[ContentId("KS_TEST_SLY_RELIC")]
public sealed class KsTestSlyRelic : RelicDef;

public enum KsScrapeDrawMode
{
    None,
    AutoPlay,
    MoveToDrawBottom,
    MoveToDiscardTop,
    MakeFree,
}

[ContentId("KS_SCRAPE_DRAWN")]
public sealed class KsScrapeDrawn : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

[ContentId("KS_SCRAPE_SLY")]
public sealed class KsScrapeSly : CardDef
{
    public override void OnPlay(ref Exec x)
    {
        KsScrapeLog.Lines.Add("sly-play");
        Kernel.Draw(ref x, (int)CardEffects.Var(x, 0), fromHandDraw: false);
    }
}

[ContentId("KS_SCRAPE_OTHER")]
public sealed class KsScrapeOther : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

[ContentId("KS_SCRAPE_DRAW_RELIC")]
public sealed class KsScrapeDrawRelic : RelicDef
{
    public static KsScrapeDrawMode Mode { get; set; }
    public static ushort FilterId { get; set; }
    public static int Remaining { get; set; }

    public static void Set(KsScrapeDrawMode mode, ushort filterId, int remaining)
    {
        Mode = mode;
        FilterId = filterId;
        Remaining = remaining;
    }

    public static void Reset()
    {
        Mode = KsScrapeDrawMode.None;
        FilterId = 0;
        Remaining = 0;
    }

    public override void AfterCardDrawn(ref Exec x, Listener self, int drawnPosition, bool fromHandDraw)
    {
        if (Mode == KsScrapeDrawMode.None || Remaining <= 0
            || (uint)drawnPosition >= (uint)x.B.HandCount)
            return;
        ref Card card = ref Piles.At(ref x.B, Pile.Hand, drawnPosition);
        if (FilterId != 0 && card.Id != FilterId)
            return;
        Remaining--;
        switch (Mode)
        {
            case KsScrapeDrawMode.AutoPlay:
                Kernel.AutoPlay(ref x, Pile.Hand, drawnPosition);
                break;
            case KsScrapeDrawMode.MoveToDrawBottom:
                Kernel.MoveCard(ref x.B, Pile.Hand, drawnPosition, Pile.Draw, PilePosition.Bottom);
                break;
            case KsScrapeDrawMode.MoveToDiscardTop:
                Kernel.MoveCard(ref x.B, Pile.Hand, drawnPosition, Pile.Discard, PilePosition.Top);
                break;
            case KsScrapeDrawMode.MakeFree:
                card.BaseCost = 0;
                break;
            default:
                throw new InvalidOperationException($"未知的抽牌钩子模式 {Mode}。");
        }
    }
}

[ContentId("KS_SCRAPE_LOG_RELIC")]
public sealed class KsScrapeLogRelic : RelicDef
{
    public override void AfterCardDiscarded(ref Exec x, Listener self, in Card card)
        => KsScrapeLog.Lines.Add("discard");
}

[ContentId("KS_SCRAPE_CHOICE_RELIC")]
public sealed class KsScrapeChoiceRelic : RelicDef
{
    public static bool RequestChoice { get; set; }

    public override void AfterCardDiscarded(ref Exec x, Listener self, in Card card)
    {
        if (!RequestChoice)
            return;
        int handCount = x.B.HandCount;
        Span<int> options = stackalloc int[handCount];
        for (int index = 0; index < handCount; index++)
            options[index] = index;
        Span<int> selected = stackalloc int[1];
        Kernel.ChooseCardsInRange(ref x, ChoiceEffect.DiscardAndDraw, Pile.Hand, options, 1, 1,
            selected, out _);
    }
}

static class KsScrapeLog
{
    public static List<string> Lines { get; } = [];

    public static void Clear() => Lines.Clear();
}

[ContentId("RG_TEST_STAR_CARD")]
public sealed class RgTestStarCard : CardDef
{
    public override void OnPlay(ref Exec x)
    {
    }
}

[ContentId("RG_TEST_STAR_POWER")]
public sealed class RgTestStarPower : PowerDef
{
    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override void AfterStarsSpent(ref Exec x, Listener self, int amount)
        => Kg2TestLog.Add($"stars-spent-power:{amount}");

    public override void AfterStarsGained(ref Exec x, Listener self, int amount)
        => Kg2TestLog.Add($"stars-gained:{amount}");
}

[ContentId("RG_TEST_STAR_RELIC")]
public sealed class RgTestStarRelic : RelicDef
{
    public override decimal TryModifyStarCost(in Battle battle, Rules rules, Listener self,
        in Card card, Pile pile, decimal amount)
    {
        Kg2TestLog.Add($"star-cost:{(int)amount}");
        return amount - 4m;
    }

    public override void AfterStarsSpent(ref Exec x, Listener self, int amount)
        => Kg2TestLog.Add($"stars-spent-relic:{amount}");
}

[ContentId("CX_TEST_IMBUED_REMOVER")]
public sealed class CxTestImbuedRemover : CardDef
{
    /// <summary>Removes the first other Imbued card from the hand, making its pending auto-play unlocatable.</summary>
    public override void OnPlay(ref Exec x)
    {
        Registry.TryEnchantment(Kernel.ImbuedEnchantmentId, out ushort imbued);
        for (int index = 0; index < x.B.HandCount; index++)
        {
            if (x.B.Cards[index].EnchantmentId != (byte)(imbued + 1))
                continue;
            Piles.RemoveAt(ref x.B, Pile.Hand, index);
            return;
        }
    }
}
