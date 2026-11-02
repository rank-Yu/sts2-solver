// Adapted from CombatSolver/src/Testing/Host/UnattendedTestProtocol.cs at commit a62f1850
// (MIT, Copyright (c) 2026 Torch1230 and contributors).
// Only the fields required to build the native root of the 410 frozen inputs are carried over.
using System.Text.Json;
using Sts2Solver.Import;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;

namespace Sts2Solver.TestHost;

internal static class OfflineJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OfflineRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string ScenarioId { get; init; } = "SMOKE-001";
    public string CharacterId { get; init; } = "IRONCLAD";
    public string EncounterId { get; init; } = "FUZZY_WURM_CRAWLER_WEAK";
    public string Seed { get; init; } = "COMBATSOLVER";
    public string? GeneratedScenarioPath { get; init; }
    public string? EvidenceDirectory { get; init; }
    public bool PreserveNativeCombatStateForTest { get; init; }
    /// <summary>Restore an already owned deck and relics before the native opening, without acquisition upgrades.</summary>
    public OfflineOwnedLoadout? OwnedLoadoutForTest { get; init; }
    public int Ascension { get; init; }
    public int ActIndexForTest { get; init; }
    public RoomType TargetRoomType { get; init; } = RoomType.Monster;
    public MapPointType TargetMapPointType { get; init; } = MapPointType.Unassigned;
    public int EnemyCurrentHp { get; init; } = 1;
    public int[] InitialEnemyMaxHps { get; init; } = [];
    public int[] InitialEnemyCurrentHps { get; init; } = [];
    public int[] InitialEnemyBlocks { get; init; } = [];
    public int? InitialPlayerHp { get; init; }
    public int? InitialPlayerMaxHp { get; init; }
    public int? InitialPlayerBlock { get; init; }
    public int? InitialPlayerEnergy { get; init; }
    public int? InitialPlayerStars { get; init; }
    public int? InitialRoundNumber { get; init; }
    public int? InitialPlayerTurnNumber { get; init; }
    public string[][] InitialEnemyStateLogs { get; init; } = [];
    public Dictionary<string, int>[] InitialEnemyIntegerMembers { get; init; } = [];
    public string[] InitialEnemyMoveIds { get; init; } = [];
    public UnattendedCardInjection[] Cards { get; init; } =
    [
        new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand", Count = 1 },
    ];
    public UnattendedCardInjection[] RunCards { get; init; } = [];
    public UnattendedPowerInjection[] Powers { get; init; } = [];
    public UnattendedOrbInjection[] Orbs { get; init; } = [];
    public UnattendedPotionInjection[] Potions { get; init; } = [];
    public UnattendedRelicInjection[] Relics { get; init; } = [];
    public UnattendedRelicInjection[] CombatRelics { get; init; } = [];
    public bool ClearPlayerHand { get; init; }
    public bool ClearPlayerPiles { get; init; }
    public bool ClearRunDeck { get; init; }
    public bool ClearAllPowers { get; init; }
    public double TimeoutSeconds { get; init; } = 120;

    // Fixed prefix and turn-setup fixtures; parsed by PrefixPlan/TurnSetupRootCapture.
    public JsonElement[] FlashPrefixActions { get; init; } = [];
    public JsonElement[] FlashPrefixPlan { get; init; } = [];
    public bool FlashTurnSetupRootForTest { get; init; }

    // M2b-1 search test protocol. The tier defaults to Medium; deterministic removes only the soft
    // time budget so a game result can be replayed byte for byte offline.
    public string SearchTier { get; init; } = "Medium";
    public bool DeterministicSearchForTest { get; init; } = true;
    public int? SearchCancelAfterMillisecondsForTest { get; init; }

    // Host-only thread count for one single search. The production request never carries it, and
    // omitted or 1 keeps the deterministic single-search path unchanged. Omitted when unset so a
    // default request stays readable by a Mod build that predates the field.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SearchThreadsForTest { get; init; }

    // Whole-combat acceptance. The run starts full auto immediately after the root is captured and
    // uses normal-tier searches with the profile's soft time; HeadlessFastModeForTest may override
    // the combat animation speed (Instant by default for this mode).
    public bool FullCombatForTest { get; init; }

    // UI infrastructure scenarios. These only change the test entry point, never the production
    // request or search semantics.
    public bool UiSnapshotForTest { get; init; }
    public bool AutoStartFullCombatForTest { get; init; }
    public bool EnglishForTest { get; init; }
    public string? ControllerScenarioForTest { get; init; }
    public bool SearchTierFromSettingsForTest { get; init; }
    public string? SettingsTierForTest { get; init; }
    public bool? SettingsAutoEnableFullAutoForTest { get; init; }

    // Search/deployment fields present in frozen fixture JSON. They do not affect root construction;
    // ValidateSupported rejects the values this batch does not understand.
    // Policy fields the evaluation-config compiler reads. The frozen corpus uses the fixed M0
    // defaults and root-derived values; these are the request overrides the harness protocol
    // carried so a fixture can reproduce a different player setting.
    public OfflineGrowthBudgets? GrowthBudgets { get; init; }
    public OfflineRelicCounterRule[] RelicCounterRules { get; init; } = [];
    public bool? IgnoreLongTermRewards { get; init; }
    public string? ActTransitionBossHpStrategy { get; init; }
    public string? FinalBossHpStrategy { get; init; }
    public string? TheftPolicyForTest { get; init; }
    public string? PotionDefaultModeForTest { get; init; }
    public OfflinePotionDirective[] PotionDirectivesForTest { get; init; } = [];
    public bool? Act3BossStrategyForTest { get; init; }
    public bool? BaseScoreOnlyForTest { get; init; }
    public bool? ContinuousThreatRankingForTest { get; init; }
    public string? BeamWeightTermForTest { get; init; }
    public double? BeamWeightScaleForTest { get; init; }
    public string? HeadlessFastModeForTest { get; init; }
    public bool ExportBugReportAfterCombat { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UnattendedPowerInjection
{
    public string PowerId { get; init; } = string.Empty;
    public string Target { get; init; } = "Enemy";
    public int TargetIndex { get; init; }
    public string? PowerTarget { get; init; }
    public int PowerTargetIndex { get; init; }
    public string? Applier { get; init; }
    public int ApplierIndex { get; init; }
    public int Amount { get; init; } = 1;
    public Dictionary<string, int> DynamicVars { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> InternalIntegerMembers { get; init; } = new(StringComparer.Ordinal);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UnattendedCardInjection
{
    public string CardId { get; init; } = string.Empty;
    public string Pile { get; init; } = "Hand";
    public int Count { get; init; } = 1;
    public int UpgradeLevels { get; init; }
    // Legacy single-card field present in one frozen fixture. The reference harness ignores it.
    public int UpgradeLevel { get; init; }
    public string? EnchantmentId { get; init; }
    public int EnchantmentAmount { get; init; } = 1;
    public string? AfflictionId { get; init; }
    public int AfflictionAmount { get; init; } = 1;
    public bool TreatAsDeckCard { get; init; }
    public Dictionary<string, int> DynamicVars { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> EnumMembers { get; init; } = new(StringComparer.Ordinal);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UnattendedPotionInjection
{
    public string PotionId { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UnattendedRelicInjection
{
    public string RelicId { get; init; } = string.Empty;
    public bool AddWithoutObtainedEffects { get; init; }
    public Dictionary<string, int> IntegerMembers { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> BooleanMembers { get; init; } = new(StringComparer.Ordinal);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UnattendedOrbInjection
{
    public string OrbId { get; init; } = string.Empty;
    public int Count { get; init; } = 1;
    public Dictionary<string, decimal> DecimalMembers { get; init; } = new(StringComparer.Ordinal);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class OfflinePotionDirective
{
    public int Slot { get; init; }
    public string PotionId { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class OfflineGrowthBudgets
{
    public int HandOfGreed { get; init; }
    public int TheHunt { get; init; }
    public int Feed { get; init; }
    public int Royalties { get; init; }
    public int Alchemize { get; init; }
    public int GeneticAlgorithm { get; init; }
    public int TheScythe { get; init; }
    public int Goopy { get; init; }
    public int ForbiddenGrimoire { get; init; }
    public int MadScience { get; init; }

    public SearchGrowthBudget ToPolicy()
    {
        foreach (int value in new[]
                 {
                     HandOfGreed, TheHunt, Feed, Royalties, Alchemize, GeneticAlgorithm,
                     TheScythe, Goopy, ForbiddenGrimoire, MadScience,
                 })
        {
            if (value is < 0 or > 1000)
                throw new UnsupportedImportException(ImportReason.UnsupportedPolicy,
                    $"成长预算 {value} 必须在 0..1000");
        }
        return new(HandOfGreed, TheHunt, Feed, Royalties, Alchemize, GeneticAlgorithm,
            TheScythe, Goopy, ForbiddenGrimoire, MadScience);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class OfflineRelicCounterRule
{
    public string Id { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public int Minimum { get; init; }
    public int Maximum { get; init; }
    public int HpAllowance { get; init; }
    public int Priority { get; init; } = 1;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OfflineOwnedLoadout
{
    public int Hp { get; init; }
    public int MaxHp { get; init; }
    public int Gold { get; init; }
}
